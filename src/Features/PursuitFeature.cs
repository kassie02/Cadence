using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;
using Cadence.Core;
using Cadence.Common;
using Cadence.Services;
using Cadence.Data;

namespace Cadence.Features
{
    /// <summary>
    /// Feature 5: Ambient Pursuit Integration
    /// Proactively monitors active LSPDFR pursuits, intelligently drafts nearby ambient cruisers
    /// into the chase up to configured caps, updates radar blips to pursuit mode, and smoothly
    /// transitions drafted units back to routine patrol upon Code 4.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public sealed class PursuitFeature : IFeature
    {
        public static PursuitFeature Instance { get; } = new PursuitFeature();
        public string Name => "Pursuit";
        public bool IsRunning => _isRunning;

        void IFeature.Start() => Start();
        void IFeature.Stop() => Stop();
        void IFeature.PurgeAll(bool immediateDelete) => PurgeAll(immediateDelete);
        int IFeature.GetActiveCount() => GetActiveCount();

        private static readonly object _syncLock = new object();
        private static readonly List<PursuitUnitSession> _activeSessions = new List<PursuitUnitSession>();
        private static readonly Dictionary<PoolHandle, Blip> _trackedSuspectBlips = new Dictionary<PoolHandle, Blip>();
        private static readonly Dictionary<PoolHandle, PoolHandle> _trackedSuspectAttachedEntities = new Dictionary<PoolHandle, PoolHandle>();
        private static readonly List<GameFiber> _trackedFibers = new List<GameFiber>();
        private static readonly Random _rand = new Random();
        private const uint WeaponCombatPistol = 0x5EF9FEC4;

        private static GameFiber _pursuitFiber;
        private static bool _isRunning;
        private static uint _lastJoinTime;
        private static int _callsignCounter = 80;

        private static readonly string[] Divisions = new[]
        {
            "2-LINCOLN", "3-MARY", "4-GEORGE", "5-EDWARD", "6-FRANK", "7-DAVID", "8-OCEAN", "1-ADAM"
        };

        private static GameFiber StartTrackedFiber(Action action, string name)
        {
            GameFiber fiber = null;
            fiber = GameFiber.StartNew(() =>
            {
                try
                {
                    action();
                }
                finally
                {
                    lock (_syncLock)
                    {
                        _trackedFibers.Remove(fiber);
                    }
                }
            }, name);

            lock (_syncLock)
            {
                _trackedFibers.RemoveAll(f => f == null || !f.IsAlive);
                if (fiber != null) _trackedFibers.Add(fiber);
            }
            return fiber;
        }

        // ====================================================================
        // Public API Contract
        // ====================================================================

        public static void Start()
        {
            lock (_syncLock)
            {
                if (_isRunning) return;
                _isRunning = true;
                _lastJoinTime = 0;

                Logger.Info("[PursuitFeature] Starting ambient pursuit watchdog background fiber.");

                try
                {
                    Events.OnPursuitEnded += OnLspdfrPursuitEnded;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[PursuitFeature] Could not hook Events.OnPursuitEnded: {ex.Message}");
                }

                _pursuitFiber = GameFiber.StartNew(PursuitProcessLoop, "Cadence - Pursuit Feature Fiber");
            }
        }

        public static void Stop()
        {
            List<GameFiber> fibersToWait;
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[PursuitFeature] Stopping pursuit feature and releasing drafted units.");

                try
                {
                    Events.OnPursuitEnded -= OnLspdfrPursuitEnded;
                }
                catch { }

                ReleaseAllPursuitUnits(immediateDelete: false);

                fibersToWait = new List<GameFiber>(_trackedFibers);
                if (_pursuitFiber != null) fibersToWait.Add(_pursuitFiber);
            }

            uint waitStart = Game.GameTime;
            while (fibersToWait.Any(f => f != null && f.IsAlive) && Game.GameTime - waitStart < 500)
            {
                GameFiber.Sleep(25);
            }

            foreach (var f in fibersToWait)
            {
                try
                {
                    if (f != null && f.IsAlive) f.Abort();
                }
                catch { }
            }

            lock (_syncLock)
            {
                _trackedFibers.Clear();
                _pursuitFiber = null;
            }
        }

        public static void PurgeAll(bool immediateDelete = true)
        {
            lock (_syncLock)
            {
                Logger.Info($"[PursuitFeature] Purging all active pursuit units (immediateDelete={immediateDelete}).");
                ReleaseAllPursuitUnits(immediateDelete: immediateDelete);
            }
        }

        public static void RelinquishCruiser(Vehicle cruiser)
        {
            if (cruiser == null) return;
            PursuitUnitSession sessionToDispose = null;
            lock (_syncLock)
            {
                sessionToDispose = _activeSessions.FirstOrDefault(s => s != null && s.Cruiser == cruiser);
                if (sessionToDispose != null)
                {
                    _activeSessions.Remove(sessionToDispose);
                }
            }
            if (sessionToDispose != null)
            {
                sessionToDispose.Dispose(immediateDelete: false);
            }
            UnitRegistry.Release(cruiser);
        }

        public static int GetActiveCount()
        {
            lock (_syncLock)
            {
                _activeSessions.RemoveAll(s => s == null || !s.IsValid() || s.State == PursuitUnitSession.SessionState.Completed);
                return _activeSessions.Count(s => s.State == PursuitUnitSession.SessionState.InPursuit);
            }
        }

        public static bool IsDraftedOfficer(Ped ped)
        {
            if (ped == null || !ped.Exists()) return false;
            lock (_syncLock)
            {
                return _activeSessions.Any(s => s != null && (s.Driver == ped || s.Passenger == ped));
            }
        }

        public static bool IsPursuitCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;
            lock (_syncLock)
            {
                return _activeSessions.Any(s => s != null && s.Cruiser == cruiser);
            }
        }

        public static bool IsActiveCruiser(Vehicle cruiser) => IsPursuitCruiser(cruiser);

        /// <summary>
        /// Retrieves genuine fleeing suspects enrolled in the active pursuit, filtering out officers,
        /// players, emergency vehicles, and compliant/neutralized subjects.
        /// </summary>
        public static List<Ped> GetValidPursuitSuspects(LHandle activePursuit)
        {
            var list = new List<Ped>();
            if (activePursuit == null) return list;

            try
            {
                Ped[] peds = Functions.GetPursuitPeds(activePursuit);
                if (peds == null || peds.Length == 0) return list;

                Ped[] cops = null;
                try { cops = Functions.GetPursuitCops(activePursuit); } catch { }

                Ped player = Game.LocalPlayer?.Character;

                foreach (var p in peds)
                {
                    if (p == null || !p.Exists() || p.IsDead) continue;
                    if (p.IsPlayer || (player != null && p == player)) continue;

                    // Exclude any law enforcement ped or emergency relationship group
                    try
                    {
                        if (Functions.IsPedACop(p)) continue;
                    }
                    catch { }

                    try
                    {
                        if (p.RelationshipGroup == "COP" || p.RelationshipGroup == "POLICE") continue;
                    }
                    catch { }

                    // Exclude any ped inside a police or emergency service vehicle
                    try
                    {
                        if (p.CurrentVehicle != null && p.CurrentVehicle.Exists())
                        {
                            if (p.CurrentVehicle.IsPoliceVehicle || p.CurrentVehicle.Model.IsLawEnforcementVehicle ||
                                IsPursuitCruiser(p.CurrentVehicle) || BackupFeature.IsBackupCruiser(p.CurrentVehicle))
                                continue;
                        }
                    }
                    catch { }

                    // Exclude peds enrolled as cops in this pursuit
                    if (cops != null && cops.Contains(p)) continue;

                    // Exclude Cadence registered backup or drafted officers
                    if (BackupFeature.IsBackupOfficer(p) || IsDraftedOfficer(p)) continue;

                    list.Add(p);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[PursuitFeature] GetValidPursuitSuspects error: {ex.Message}");
            }

            return list;
        }

        /// <summary>
        /// Evaluates whether any pursuit suspect is currently surrendering, kneeling, getting arrested,
        /// being handcuffed, or already cuffed/arrested. While true, no new units may be drafted or added
        /// to the pursuit to ensure LSPDFR's designated primary arresting officer can complete the arrest
        /// uninterrupted without CopBustPed task aborts.
        /// </summary>
        public static bool IsAnySuspectSurrenderingOrArrested(LHandle activePursuit)
        {
            if (activePursuit == null) return false;
            try
            {
                if (!Functions.IsPursuitStillRunning(activePursuit)) return false;

                var suspects = GetValidPursuitSuspects(activePursuit);
                if (suspects == null || suspects.Count == 0) return false;

                bool isAnyArresting = false;
                bool areAllNeutralized = true;

                foreach (var s in suspects)
                {
                    if (s == null || !s.Exists()) continue;

                    bool gettingArrested = false;
                    try
                    {
                        gettingArrested = Functions.IsPedGettingArrested(s) ||
                                          Functions.IsPedBeingCuffed(s) ||
                                          Functions.IsPedKneelingTaskActive(s) ||
                                          Functions.IsPedCuffedTaskActive(s) ||
                                          (Functions.GetPedArrestingOfficer(s) != null && Functions.GetPedArrestingOfficer(s).Exists());
                    }
                    catch { }

                    bool arrested = false;
                    try { arrested = Functions.IsPedArrested(s); } catch { }

                    bool cuffed = false;
                    try { cuffed = s.IsCuffed; } catch { }

                    if (gettingArrested)
                    {
                        isAnyArresting = true;
                    }

                    if (!gettingArrested && !arrested && !cuffed && s.IsAlive)
                    {
                        areAllNeutralized = false;
                    }
                }

                return isAnyArresting || areAllNeutralized;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[PursuitFeature] IsAnySuspectSurrenderingOrArrested error: {ex.Message}");
                return false;
            }
        }

        // ====================================================================
        // Background Process Fiber
        // ====================================================================

        private static void PursuitProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    GameFiber.Sleep(750);

                    if (!_isRunning || !Settings.EnablePlugin || !Settings.EnablePursuitAssist)
                    {
                        continue;
                    }

                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists())
                    {
                        continue;
                    }

                    LHandle activePursuit = null;
                    try
                    {
                        activePursuit = Functions.GetActivePursuit();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[PursuitFeature] GetActivePursuit check error: {ex.Message}");
                    }

                    bool isPursuitRunning = activePursuit != null && Functions.IsPursuitStillRunning(activePursuit);

                    if (!isPursuitRunning)
                    {
                        // Pursuit has terminated or Code 4 was called: clean suspect markers and transition active units to OnSceneStandby
                        ClearAllSuspectBlips();
                        TransitionToSceneStandby();
                    }

                    // Watchdog: prune invalid sessions and process standby/stand-down expiration
                    PruneAndWatchSessions(player.Position);

                    // Display dismissal prompt and process key-holding if player is near standby scene
                    ProcessStandbyDismissal(player);

                    if (!isPursuitRunning)
                    {
                        continue;
                    }

                    // Resolve target pursuit/suspect location
                    Vector3 targetPos = GetPursuitTargetPosition(activePursuit, player);
                    if (targetPos == Vector3.Zero)
                    {
                        continue;
                    }

                    // Update pursuit blips and dismount states for all active pursuit sessions
                    lock (_syncLock)
                    {
                        foreach (var s in _activeSessions.ToList())
                        {
                            if (s != null && s.IsValid())
                            {
                                if (s.State == PursuitUnitSession.SessionState.InPursuit)
                                {
                                    s.UpdatePursuitBlip(targetPos);
                                }
                                s.UpdateBlipStates();
                            }
                        }
                    }

                    // Evaluate cruiser involvement count vs MaxPursuitUnits cap (strictly synchronized across Pursuit and Backup features)
                    if (IsPursuitAtMaxCapacity(activePursuit))
                    {
                        continue;
                    }

                    int draftedCount;
                    lock (_syncLock)
                    {
                        draftedCount = _activeSessions.Count(s => s != null && s.State == PursuitUnitSession.SessionState.InPursuit);
                    }

                    // Join interval pacing (minimum 6 seconds between drafting each unit)
                    uint now = Game.GameTime;
                    if (now - _lastJoinTime < 6000 && draftedCount > 0)
                    {
                        continue;
                    }

                    // Check if any suspect is currently surrendering, kneeling, or being arrested:
                    // If so, do not draft new pursuit cruisers into the chase to avoid exceeding
                    // per-suspect pursuit slots and aborting LSPDFR's CopBustPed state machine.
                    // Furthermore, immediately mute audible sirens and order pursuit officers to dismount.
                    if (IsAnySuspectSurrenderingOrArrested(activePursuit))
                    {
                        MuteNearbyPoliceSirens(targetPos, 60.0f);
                        lock (_syncLock)
                        {
                            foreach (var s in _activeSessions.ToList())
                            {
                                if (s != null && s.IsValid() && s.Cruiser != null && s.Cruiser.Exists() && s.Cruiser.Position.DistanceTo(targetPos) <= 75.0f)
                                {
                                    if (s.State == PursuitUnitSession.SessionState.InPursuit)
                                    {
                                        s.BeginSceneStandby();
                                    }
                                }
                            }
                        }
                        continue;
                    }

                    // Draft nearest available ambient cruiser or spawn interceptor
                    TryDraftNearbyCruiser(activePursuit, targetPos);
                }
                catch (System.Threading.ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[PursuitFeature] Unexpected exception in pursuit loop", ex);
                    GameFiber.Sleep(2500);
                }
            }
        }

        private static int GetActivePursuitCruiserCount(LHandle activePursuit)
        {
            try
            {
                Ped[] pursuitCops = Functions.GetPursuitCops(activePursuit);
                if (pursuitCops == null || pursuitCops.Length == 0) return 0;

                int vehicleCruisers = pursuitCops
                    .Where(p => p != null && p.Exists() && p.CurrentVehicle != null && p.CurrentVehicle.Exists())
                    .Select(p => p.CurrentVehicle.Handle)
                    .Distinct()
                    .Count();

                int footCops = pursuitCops
                    .Count(p => p != null && p.Exists() && (p.CurrentVehicle == null || !p.CurrentVehicle.Exists()));

                return vehicleCruisers + (footCops > 0 ? (footCops + 1) / 2 : 0);
            }
            catch
            {
                return 0;
            }
        }

        public static int GetActivePursuitUnitCount()
        {
            Ped[] pursuitCops = null;
            try
            {
                LHandle activePursuit = Functions.GetActivePursuit();
                if (activePursuit != null && Functions.IsPursuitStillRunning(activePursuit))
                {
                    pursuitCops = Functions.GetPursuitCops(activePursuit);
                }
            }
            catch { }

            int totalCopsInPursuit = pursuitCops?.Count(p => p != null && p.Exists() && p.IsAlive) ?? 0;

            int draftedCount;
            lock (_syncLock)
            {
                draftedCount = _activeSessions.Count(s => s != null && s.State == PursuitUnitSession.SessionState.InPursuit);
            }

            return Math.Max(totalCopsInPursuit, draftedCount);
        }

        public static int GetActiveInvolvedCount() => GetActivePursuitUnitCount();

        public static Vector3 GetPursuitTargetPosition(LHandle activePursuit, Ped player)
        {
            try
            {
                var suspects = GetValidPursuitSuspects(activePursuit);
                Ped primarySuspect = suspects?.FirstOrDefault(p => p != null && p.Exists() && p.IsAlive);

                if (primarySuspect != null && primarySuspect.Exists())
                {
                    return primarySuspect.Position;
                }
            }
            catch { }

            return player != null && player.Exists() ? player.Position : Vector3.Zero;
        }

        private static void UpdateSuspectRadarMarkers(LHandle activePursuit)
        {
            try
            {
                var validSuspects = GetValidPursuitSuspects(activePursuit);

                // Clean up blips for suspects no longer alive, cuffed, arrested, getting arrested, or no longer in pursuit
                var toRemove = new List<PoolHandle>();
                foreach (var kvp in _trackedSuspectBlips.ToList())
                {
                    PoolHandle handle = kvp.Key;
                    Blip b = kvp.Value;
                    bool shouldRemove = false;

                    Ped p = validSuspects.FirstOrDefault(s => s != null && s.Exists() && s.Handle == handle);
                    if (p == null || !p.Exists() || p.IsDead || p.IsCuffed)
                    {
                        shouldRemove = true;
                    }
                    else
                    {
                        try
                        {
                            if (Functions.IsPedArrested(p) || Functions.IsPedGettingArrested(p) ||
                                Functions.IsPedBeingCuffed(p) || Functions.IsPedKneelingTaskActive(p) ||
                                Functions.IsPedCuffedTaskActive(p))
                            {
                                shouldRemove = true;
                            }
                        }
                        catch
                        {
                            shouldRemove = true;
                        }
                    }

                    // Check if suspect bailed out on foot or carjacked/switched vehicles
                    if (!shouldRemove && p != null && p.Exists())
                    {
                        Entity targetEntity = (p.IsInAnyVehicle(false) && p.CurrentVehicle != null && p.CurrentVehicle.Exists())
                            ? (Entity)p.CurrentVehicle
                            : (Entity)p;

                        if (_trackedSuspectAttachedEntities.TryGetValue(handle, out PoolHandle attachedHandle))
                        {
                            if (attachedHandle != targetEntity.Handle)
                            {
                                // Suspect bailed out or switched vehicles: cleanly delete previous blip and re-attach to new entity
                                BlipService.SafeDeleteBlip(ref b);
                                if (!BlipService.HasAnyActiveRadarMarker(targetEntity))
                                {
                                    Blip freshBlip = BlipService.AttachSuspectBlip(targetEntity, "SUSPECT");
                                    if (freshBlip != null && freshBlip.Exists())
                                    {
                                        _trackedSuspectBlips[handle] = freshBlip;
                                        _trackedSuspectAttachedEntities[handle] = targetEntity.Handle;
                                        Logger.Debug($"[PursuitFeature] Transitioned suspect blip for {p.Model.Name} (0x{handle:X}) to entity 0x{targetEntity.Handle:X}.");
                                    }
                                    else
                                    {
                                        shouldRemove = true;
                                    }
                                }
                                else
                                {
                                    shouldRemove = true;
                                }
                            }
                        }
                    }

                    if (shouldRemove)
                    {
                        BlipService.SafeDeleteBlip(ref b);
                        toRemove.Add(handle);
                    }
                }
                foreach (var handle in toRemove)
                {
                    _trackedSuspectBlips.Remove(handle);
                    _trackedSuspectAttachedEntities.Remove(handle);
                }

                // Check active fleeing suspects: if they have no attached blip, attach one!
                foreach (Ped suspect in validSuspects)
                {
                    if (suspect == null || !suspect.Exists() || suspect.IsDead || suspect.IsCuffed)
                    {
                        continue;
                    }

                    // Already tracked in watchdog dictionary - keep locked and DO NOT touch!
                    if (_trackedSuspectBlips.ContainsKey(suspect.Handle))
                    {
                        continue;
                    }

                    try
                    {
                        if (Functions.IsPedArrested(suspect) || Functions.IsPedGettingArrested(suspect) ||
                            Functions.IsPedBeingCuffed(suspect) || Functions.IsPedKneelingTaskActive(suspect) ||
                            Functions.IsPedCuffedTaskActive(suspect) ||
                            (Functions.GetPedArrestingOfficer(suspect) != null && Functions.GetPedArrestingOfficer(suspect).Exists()))
                        {
                            continue;
                        }
                    }
                    catch { }

                    Entity targetEntity = (suspect.IsInAnyVehicle(false) && suspect.CurrentVehicle != null && suspect.CurrentVehicle.Exists())
                        ? (Entity)suspect.CurrentVehicle
                        : (Entity)suspect;

                    // Check if target entity already has ANY active radar marker (from LSPDFR or vanilla GTA V)
                    if (BlipService.HasAnyActiveRadarMarker(targetEntity))
                    {
                        continue;
                    }

                    // Suspect does not have any blip! Attach fallback watchdog suspect blip
                    Blip newBlip = BlipService.AttachSuspectBlip(targetEntity, "SUSPECT");
                    if (newBlip != null && newBlip.Exists())
                    {
                        _trackedSuspectBlips[suspect.Handle] = newBlip;
                        _trackedSuspectAttachedEntities[suspect.Handle] = targetEntity.Handle;
                        Logger.Debug($"[PursuitFeature] Attached watchdog suspect blip to {suspect.Model.Name} (0x{suspect.Handle:X}) on entity 0x{targetEntity.Handle:X}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[PursuitFeature] UpdateSuspectRadarMarkers error: {ex.Message}");
            }
        }

        private static void ClearAllSuspectBlips()
        {
            try
            {
                foreach (var kvp in _trackedSuspectBlips.ToList())
                {
                    Blip b = kvp.Value;
                    BlipService.SafeDeleteBlip(ref b);
                }
                _trackedSuspectBlips.Clear();
                _trackedSuspectAttachedEntities.Clear();
            }
            catch { }
        }

        private static void OnLspdfrPursuitEnded(LHandle pursuit)
        {
            try
            {
                Logger.Info("[PursuitFeature] LSPDFR OnPursuitEnded event intercepted. Immediate transition to scene standby.");
                ClearAllSuspectBlips();
                TransitionToSceneStandby();
            }
            catch (Exception ex)
            {
                Logger.Debug($"[PursuitFeature] Error in OnLspdfrPursuitEnded: {ex.Message}");
            }
        }

        private static void TransitionToSceneStandby()
        {
            ClearAllSuspectBlips();
            lock (_syncLock)
            {
                for (int i = _activeSessions.Count - 1; i >= 0; i--)
                {
                    var s = _activeSessions[i];
                    if (s == null || !s.IsValid())
                    {
                        if (s != null) s.Dispose(immediateDelete: false);
                        _activeSessions.RemoveAt(i);
                        continue;
                    }

                    if (s.State == PursuitUnitSession.SessionState.InPursuit)
                    {
                        s.BeginSceneStandby();
                    }
                }
            }
        }

        private static void PruneAndWatchSessions(Vector3 playerPos)
        {
            uint now = Game.GameTime;
            lock (_syncLock)
            {
                for (int i = _activeSessions.Count - 1; i >= 0; i--)
                {
                    var s = _activeSessions[i];
                    if (s == null || !s.IsValid())
                    {
                        if (s != null)
                        {
                            s.Dispose(immediateDelete: true);
                        }
                        _activeSessions.RemoveAt(i);
                        continue;
                    }

                    if (s.State == PursuitUnitSession.SessionState.OnSceneStandby)
                    {
                        s.UpdateBlipStates();
                        if (s.Cruiser == null || !s.Cruiser.Exists())
                        {
                            s.Dispose(immediateDelete: true);
                            _activeSessions.RemoveAt(i);
                            continue;
                        }
                        float dist = s.Cruiser.Position.DistanceTo(playerPos);
                        // Hold officers on scene standby until player explicitly dismisses them (Enter/menu), or player leaves the area (> DespawnDistance)
                        if (dist > Settings.DespawnDistance)
                        {
                            s.DismissFromScene();
                        }
                    }
                    else if (s.State == PursuitUnitSession.SessionState.StandingDown)
                    {
                        float dist = (s.Cruiser != null && s.Cruiser.Exists()) ? s.Cruiser.Position.DistanceTo(playerPos) : float.MaxValue;
                        if (now - s.StandDownStartTime > 15000 || dist > Settings.DespawnDistance)
                        {
                            s.Dispose(immediateDelete: false);
                            _activeSessions.RemoveAt(i);
                        }
                    }
                    else if (s.State == PursuitUnitSession.SessionState.Completed)
                    {
                        s.Dispose(immediateDelete: false);
                        _activeSessions.RemoveAt(i);
                    }
                }
            }
        }

        public static void MuteNearbyPoliceSirens(Vector3 center, float radius = 60.0f)
        {
            try
            {
                if (center == Vector3.Zero) return;
                Entity[] nearbyVehs = World.GetEntities(center, radius, GetEntitiesFlags.ConsiderAllVehicles);
                if (nearbyVehs == null || nearbyVehs.Length == 0) return;

                foreach (var ent in nearbyVehs)
                {
                    if (ent is Vehicle v && v.Exists() && !v.IsDead && (v.IsPoliceVehicle || v.Model.IsLawEnforcementVehicle))
                    {
                        try
                        {
                            if (v.IsSirenOn)
                            {
                                NativeTasks.SetSilentEmergencyLights(v, true);
                            }
                            else
                            {
                                NativeTasks.ShutOffSirenAndLights(v);
                            }
                            NativeTasks.EnforceSirenLightbarPolicy(v);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static uint _dismissKeyDownTime;
        private static bool _hasShownDismissHelp;

        private static void ProcessStandbyDismissal(Ped player)
        {
            if (player == null || !player.Exists()) return;
            if (Game.Console.IsOpen || Game.IsPaused) return;

            List<PursuitUnitSession> standbyUnits;
            lock (_syncLock)
            {
                standbyUnits = _activeSessions.Where(s => s != null && s.IsValid() && s.State == PursuitUnitSession.SessionState.OnSceneStandby).ToList();
            }

            if (standbyUnits.Count == 0)
            {
                _dismissKeyDownTime = 0;
                _hasShownDismissHelp = false;
                return;
            }

            Vector3 playerPos = player.Position;
            bool playerNearScene = standbyUnits.Any(s => s.Cruiser != null && s.Cruiser.Exists() && s.Cruiser.Position.DistanceTo(playerPos) <= 45.0f);

            if (playerNearScene)
            {
                if (!_hasShownDismissHelp)
                {
                    _hasShownDismissHelp = true;
                    string keyName = Settings.DismissBackupKey == System.Windows.Forms.Keys.Enter ? "ENTER" : Settings.DismissBackupKey.ToString().ToUpperInvariant();
                    Game.DisplayHelp($"Hold ~b~[{keyName}]~s~ to dismiss pursuit scene officers", 4000);
                }

                if (Settings.DismissBackupKey != System.Windows.Forms.Keys.None && Game.IsKeyDownRightNow(Settings.DismissBackupKey))
                {
                    if (_dismissKeyDownTime == 0)
                    {
                        _dismissKeyDownTime = Game.GameTime;
                    }
                    else if (Game.GameTime - _dismissKeyDownTime >= 1100)
                    {
                        _dismissKeyDownTime = 0;
                        DismissStandbyUnits();
                    }
                }
                else
                {
                    _dismissKeyDownTime = 0;
                }
            }
            else
            {
                _dismissKeyDownTime = 0;
            }
        }

        public static void DismissStandbyUnits()
        {
            lock (_syncLock)
            {
                bool anyDismissed = false;
                foreach (var s in _activeSessions.ToList())
                {
                    if (s != null && s.IsValid() && s.State == PursuitUnitSession.SessionState.OnSceneStandby)
                    {
                        s.DismissFromScene();
                        anyDismissed = true;
                    }
                }

                if (anyDismissed)
                {
                    Game.DisplayNotification("~b~Pursuit scene officers~s~ dismissed. Returning to routine patrol.");
                }
            }
        }

        public static bool IsPursuitAtMaxCapacity(LHandle activePursuit)
        {
            if (activePursuit == null) return false;
            int draftedCount;
            lock (_syncLock)
            {
                draftedCount = _activeSessions.Count(s => s != null && s.State == PursuitUnitSession.SessionState.InPursuit);
            }

            Ped[] pursuitCops = null;
            try { pursuitCops = Functions.GetPursuitCops(activePursuit); } catch { }
            int totalCopsInPursuit = pursuitCops?.Count(p => p != null && p.Exists() && p.IsAlive) ?? 0;
            int distinctCruisers = GetActivePursuitCruiserCount(activePursuit);
            int backupResponders = BackupFeature.GetActivePoliceResponderCount();
            int totalInvolved = Math.Max(totalCopsInPursuit, Math.Max(distinctCruisers, draftedCount)) + backupResponders;

            return totalInvolved >= Settings.MaxPursuitUnits || (draftedCount + backupResponders) >= Settings.MaxPursuitUnits;
        }

        public static bool RequestPursuitInterceptor(bool isHighway = false)
        {
            LHandle activePursuit = null;
            try { activePursuit = Functions.GetActivePursuit(); } catch { }
            if (activePursuit == null || !Functions.IsPursuitStillRunning(activePursuit))
            {
                return false;
            }

            if (IsAnySuspectSurrenderingOrArrested(activePursuit))
            {
                return false;
            }

            if (IsPursuitAtMaxCapacity(activePursuit))
            {
                Logger.Debug($"[PursuitFeature] RequestPursuitInterceptor denied: pursuit is at capacity ({Settings.MaxPursuitUnits} units).");
                return false;
            }

            Ped player = Game.LocalPlayer?.Character;
            if (player == null || !player.Exists()) return false;

            Vector3 targetPos = GetPursuitTargetPosition(activePursuit, player);
            if (targetPos == Vector3.Zero) targetPos = player.Position;

            // 1. Try drafting closest ambient cruiser first
            Vehicle candidateVeh = FindCandidateAmbientCruiser(targetPos);
            if (candidateVeh != null && candidateVeh.Exists())
            {
                Ped driver = candidateVeh.Driver;
                if (driver != null && driver.Exists() && driver.IsAlive)
                {
                    Ped passenger = candidateVeh.PassengerCount > 0 ? candidateVeh.Passengers.FirstOrDefault(p => p != null && p.Exists() && p.IsAlive) : null;
                    DraftCruiserIntoPursuit(activePursuit, candidateVeh, driver, passenger, wasSpawned: false);
                    return true;
                }
            }

            // 2. Spawn interceptor
            if (TryFindRoadSpawnPosition(targetPos, out Vector3 spawnPos, out float spawnHeading))
            {
                Vehicle cruiser = null;
                Ped driver = null;
                Ped passenger = null;

                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    var ubResult = isHighway
                        ? UltimateBackupService.SpawnStatePatrol(spawnPos, 1)
                        : UltimateBackupService.SpawnLocalPatrol(spawnPos, 1);

                    if (ubResult != null)
                    {
                        if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                        {
                            cruiser = ubResult.Item1;
                            driver = ubResult.Item2[0];
                            if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                        }
                        else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                        {
                            TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                        }
                    }
                }

                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("PursuitFeature", $"Ultimate Backup did not return a cruiser for pursuit at {spawnPos}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                        if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                        return false;
                    }

                    string vehicleModel = isHighway ? ZoneSpawnData.GetDefaultPoliceVehicleForPosition(spawnPos) : ZoneSpawnData.PoliceVehiclesCity[0];
                    string copModel = ZoneSpawnData.GetDefaultCopPedModelForPosition(spawnPos);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(spawnPos, spawnHeading, vehicleModel, copModel);
                    if (nativeResult != null)
                    {
                        if (nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                        {
                            cruiser = nativeResult.Item1;
                            driver = nativeResult.Item2[0];
                            if (Settings.EnableEupOutfits)
                            {
                                EupService.ApplyBodyArmor(driver);
                            }
                        }
                        else if (nativeResult.Item1 != null && nativeResult.Item1.Exists())
                        {
                            TeardownHelper.SafeDismissVehicle(nativeResult.Item1, delete: true);
                        }
                    }
                }

                if (cruiser != null && cruiser.Exists() && driver != null && driver.Exists())
                {
                    cruiser.Heading = spawnHeading;
                    WorldUtils.SecureEntityGround(cruiser);
                    DraftCruiserIntoPursuit(activePursuit, cruiser, driver, passenger, wasSpawned: true);
                    return true;
                }

                if (cruiser != null && cruiser.Exists())
                {
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                }
            }

            return false;
        }

        public static void RequestGroupPursuit(int count)
        {
            StartTrackedFiber(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    RequestPursuitInterceptor(isHighway: i % 2 != 0);
                    GameFiber.Sleep(1500);
                }
            }, "Cadence - Group Pursuit Dispatch Fiber");
        }

        private static void TryDraftNearbyCruiser(LHandle activePursuit, Vector3 targetPos)
        {
            // 1. Search for closest ambient police vehicle within 380m
            Vehicle candidateVeh = FindCandidateAmbientCruiser(targetPos);
            if (candidateVeh != null && candidateVeh.Exists())
            {
                Ped driver = candidateVeh.Driver;
                if (driver != null && driver.Exists() && driver.IsAlive)
                {
                    Ped passenger = candidateVeh.PassengerCount > 0 ? candidateVeh.Passengers.FirstOrDefault(p => p != null && p.Exists() && p.IsAlive) : null;
                    DraftCruiserIntoPursuit(activePursuit, candidateVeh, driver, passenger, wasSpawned: false);
                    return;
                }
            }

            // 2. If no ambient cruiser found within range, spawn an ambient interceptor 180m-260m away
            if (TryFindRoadSpawnPosition(targetPos, out Vector3 spawnPos, out float spawnHeading))
            {
                Vehicle cruiser = null;
                Ped driver = null;
                Ped passenger = null;

                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    bool isHighway = WorldUtils.ShouldSpawnHighwayUnit(spawnPos);
                    var ubResult = isHighway
                        ? UltimateBackupService.SpawnStatePatrol(spawnPos, 1)
                        : UltimateBackupService.SpawnLocalPatrol(spawnPos, 1);

                    if (ubResult != null)
                    {
                        if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                        {
                            cruiser = ubResult.Item1;
                            driver = ubResult.Item2[0];
                            if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                        }
                        else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                        {
                            TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                        }
                    }
                }

                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("PursuitFeature", $"Ultimate Backup did not return a cruiser for pursuit at {spawnPos}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                        if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                        return;
                    }

                    string vehicleModel = ZoneSpawnData.GetDefaultPoliceVehicleForPosition(spawnPos);
                    string copModel = ZoneSpawnData.GetDefaultCopPedModelForPosition(spawnPos);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(spawnPos, spawnHeading, vehicleModel, copModel);
                    if (nativeResult != null)
                    {
                        if (nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                        {
                            cruiser = nativeResult.Item1;
                            driver = nativeResult.Item2[0];
                            if (Settings.EnableEupOutfits)
                            {
                                EupService.ApplyBodyArmor(driver);
                            }
                        }
                        else if (nativeResult.Item1 != null && nativeResult.Item1.Exists())
                        {
                            TeardownHelper.SafeDismissVehicle(nativeResult.Item1, delete: true);
                        }
                    }
                }

                if (cruiser != null && cruiser.Exists() && driver != null && driver.Exists())
                {
                    cruiser.Heading = spawnHeading;
                    WorldUtils.SecureEntityGround(cruiser);
                    DraftCruiserIntoPursuit(activePursuit, cruiser, driver, passenger, wasSpawned: true);
                }
                else if (cruiser != null && cruiser.Exists())
                {
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                }
            }
        }

        private static Vehicle FindCandidateAmbientCruiser(Vector3 targetPos)
        {
            try
            {
                Entity[] nearbyVehicles = World.GetEntities(targetPos, 220.0f, GetEntitiesFlags.ConsiderAllVehicles);
                if (nearbyVehicles == null || nearbyVehicles.Length == 0) return null;

                return nearbyVehicles
                    .OfType<Vehicle>()
                    .Where(v => v != null && v.Exists() && !v.IsDead && v.IsDriveable)
                    .Where(v => v.Driver != null && v.Driver.Exists() && v.Driver.IsAlive && !v.Driver.IsPlayer && v.Driver != Game.LocalPlayer?.Character)
                    .Where(v =>
                    {
                        // Under no circumstances draft cruisers actively conducting roadside traffic stops or emergency response runs
                        if (TrafficStopFeature.IsActiveCruiser(v)) return false;
                        if (ResponseFeature.IsActiveCruiser(v)) return false;
                        if (AssistFeature.IsActiveCruiser(v)) return false;

                        // Never draft cruisers managed by BackupFeature (responding, on scene, or dismissing)
                        if (BackupFeature.IsBackupCruiser(v)) return false;

                        // Central UnitRegistry claim screening: allow drafting ambient patrol units, protect all others
                        if (UnitRegistry.IsClaimedByOthers(v, UnitRegistry.OwnerPatrol)) return false;
                        if (v.Driver != null && UnitRegistry.IsClaimedByOthers(v.Driver, UnitRegistry.OwnerPatrol)) return false;

                        bool isPolice = v.IsPoliceVehicle || v.Model.IsLawEnforcementVehicle ||
                                        PatrolFeature.IsActiveCruiser(v) || StationaryFeature.IsActiveCruiser(v) ||
                                        Functions.IsPedACop(v.Driver) || v.Driver.RelationshipGroup == "COP";
                        if (!isPolice) return false;

                        lock (_syncLock)
                        {
                            if (_activeSessions.Any(s => s.Cruiser == v)) return false;
                        }
                        bool inPursuit = false;
                        try { inPursuit = Functions.IsPedInPursuit(v.Driver); } catch { }
                        if (inPursuit) return false;

                        bool isBusy = false;
                        try { isBusy = Functions.IsCopBusy(v.Driver, false); } catch { }
                        if (isBusy) return false;

                        return true;
                    })
                    .OrderBy(v => v.Position.DistanceTo(targetPos))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static void DraftCruiserIntoPursuit(LHandle activePursuit, Vehicle cruiser, Ped driver, Ped passenger, bool wasSpawned)
        {
            try
            {
                if (!wasSpawned)
                {
                    PatrolFeature.RelinquishCruiser(cruiser);
                    StationaryFeature.RelinquishCruiser(cruiser);
                    TrafficStopFeature.RelinquishCruiser(cruiser);
                    ResponseFeature.RelinquishCruiser(cruiser);
                    AssistFeature.RelinquishCruiser(cruiser);
                    BackupFeature.RelinquishCruiser(cruiser);
                }

                if (!UnitRegistry.TryClaim(cruiser, UnitRegistry.OwnerPursuit))
                {
                    Logger.Debug($"[PursuitFeature] Cruiser 0x{cruiser.Handle:X} could not be claimed in UnitRegistry; skipping draft.");
                    if (wasSpawned)
                    {
                        TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                        if (driver != null) TeardownHelper.SafeDismissPed(driver, delete: true);
                        if (passenger != null) TeardownHelper.SafeDismissPed(passenger, delete: true);
                    }
                    return;
                }
                if (driver != null && driver.Exists())
                {
                    UnitRegistry.TryClaim(driver, UnitRegistry.OwnerPursuit);
                }
                if (passenger != null && passenger.Exists())
                {
                    UnitRegistry.TryClaim(passenger, UnitRegistry.OwnerPursuit);
                }

                string callsign = GenerateCallsign();

                cruiser.MakePersistent();
                driver.MakePersistent();
                if (passenger != null && passenger.Exists()) passenger.MakePersistent();
                cruiser.IsEngineOn = true;
                cruiser.IsSirenOn = true;
                cruiser.IsSirenSilent = false;
                NativeTasks.ConfigureEmergencyDriver(driver, cruiser);

                // Add driver and passenger to pursuit AI
                driver.BlockPermanentEvents = false;
                Functions.SetPedAsCop(driver);
                Functions.AddCopToPursuit(activePursuit, driver);

                if (passenger != null && passenger.Exists() && passenger.IsAlive)
                {
                    passenger.BlockPermanentEvents = false;
                    Functions.SetPedAsCop(passenger);
                    Functions.AddCopToPursuit(activePursuit, passenger);
                }

                Blip blip = null; // Yield to LSPDFR's native Sprite 3 (radar_police_ped)

                var session = new PursuitUnitSession(cruiser, driver, passenger, blip, callsign, wasSpawned);

                int activeCount;
                lock (_syncLock)
                {
                    _activeSessions.Add(session);
                    _lastJoinTime = Game.GameTime;
                    activeCount = _activeSessions.Count;
                }
                SessionMetrics.IncrementPursuit();
                Logger.Info($"[PursuitFeature] Ambient cruiser {callsign} successfully drafted into pursuit (Total active: {activeCount}/{Settings.MaxPursuitUnits}).");
            }
            catch (Exception ex)
            {
                Logger.Error("[PursuitFeature] Error drafting cruiser into pursuit", ex);
                if (wasSpawned)
                {
                    if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    if (driver != null && driver.Exists()) TeardownHelper.SafeDismissPed(driver, delete: true);
                    if (passenger != null && passenger.Exists()) TeardownHelper.SafeDismissPed(passenger, delete: true);
                }
            }
        }

        private static void ReleaseAllPursuitUnits(bool immediateDelete)
        {
            ClearAllSuspectBlips();
            List<PursuitUnitSession> sessionsToDispose;
            lock (_syncLock)
            {
                sessionsToDispose = new List<PursuitUnitSession>(_activeSessions);
                _activeSessions.Clear();
            }

            foreach (var session in sessionsToDispose)
            {
                if (session != null)
                {
                    session.Dispose(immediateDelete);
                }
            }
        }

        private static bool TryFindRoadSpawnPosition(Vector3 targetPos, out Vector3 spawnPos, out float spawnHeading)
        {
            spawnPos = Vector3.Zero;
            spawnHeading = 0f;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                float distance;
                double angle;
                lock (_rand)
                {
                    distance = (float)_rand.Next(180, 280);
                    angle = _rand.NextDouble() * Math.PI * 2.0;
                }

                Vector3 probe = new Vector3(
                    targetPos.X + (float)Math.Cos(angle) * distance,
                    targetPos.Y + (float)Math.Sin(angle) * distance,
                    targetPos.Z
                );

                if (WorldUtils.GetSafeStreetPosition(probe, 1, out Vector3 candidatePos, out float candidateHeading))
                {
                    if (WorldUtils.IsSubterraneanOrVoid(candidatePos)) continue;
                    if (candidatePos.DistanceTo(targetPos) < 150f) continue;
                    if (!WorldUtils.IsAreaClear(candidatePos, 7.5f)) continue;

                    spawnPos = candidatePos;
                    spawnHeading = candidateHeading;
                    return true;
                }
            }

            return false;
        }

        private static string GenerateCallsign()
        {
            string div;
            lock (_rand)
            {
                div = Divisions[_rand.Next(Divisions.Length)];
            }
            int num = Interlocked.Increment(ref _callsignCounter);
            if (num > 99)
            {
                Interlocked.Exchange(ref _callsignCounter, 80);
                num = 80;
            }
            return $"{div}-{num:D2}";
        }

        // ====================================================================
        // Pursuit Session Representation
        // ====================================================================

        private class PursuitUnitSession
        {
            public enum SessionState { InPursuit, OnSceneStandby, StandingDown, Completed }

            public Vehicle Cruiser { get; }
            public Ped Driver { get; }
            public Ped Passenger { get; }
            public Blip RadarBlip;
            public Blip DriverBadgeBlip;
            public Blip PassengerBadgeBlip;
            public string Callsign { get; }
            public uint JoinTime { get; }
            public bool WasAmbientSpawned { get; }
            public SessionState State { get; private set; }
            public uint StandbyStartTime { get; private set; }
            public uint StandDownStartTime { get; private set; }

            private bool _isParkedBlip;

            public PursuitUnitSession(Vehicle cruiser, Ped driver, Ped passenger, Blip blip, string callsign, bool wasSpawned)
            {
                Cruiser = cruiser;
                Driver = driver;
                Passenger = passenger;
                RadarBlip = blip;
                Callsign = callsign;
                JoinTime = Game.GameTime;
                WasAmbientSpawned = wasSpawned;
                State = SessionState.InPursuit;
            }

            private uint _lastSirenCycleTime;

            public void UpdatePursuitBlip(Vector3 targetPos)
            {
                if (Cruiser == null || !Cruiser.Exists() || State != SessionState.InPursuit) return;

                // Dynamic siren tone variation
                if (Settings.EnableSirenToneVariation && Cruiser.IsSirenOn)
                {
                    uint now = Game.GameTime;
                    if (now - _lastSirenCycleTime >= 8500)
                    {
                        _lastSirenCycleTime = now;
                        NativeTasks.CycleSirenTone(Cruiser);
                    }
                }
            }

            public void UpdateBlipStates()
            {
                if (Cruiser != null && Cruiser.Exists())
                {
                    NativeTasks.EnforceSirenLightbarPolicy(Cruiser);
                }

                if (!Settings.ShowRadarBlips || Cruiser == null || !Cruiser.Exists()) return;

                bool driverOutside = Driver != null && Driver.Exists() && Driver.IsAlive && !Driver.IsInVehicle(Cruiser, false);
                bool passengerOutside = Passenger != null && Passenger.Exists() && Passenger.IsAlive && !Passenger.IsInVehicle(Cruiser, false);
                bool anyOutside = driverOutside || passengerOutside;

                bool allLivingOutside = true;
                if (Driver != null && Driver.Exists() && Driver.IsAlive && Driver.IsInVehicle(Cruiser, false)) allLivingOutside = false;
                if (Passenger != null && Passenger.Exists() && Passenger.IsAlive && Passenger.IsInVehicle(Cruiser, false)) allLivingOutside = false;

                // 1. Driver foot blip
                if (driverOutside && State != SessionState.StandingDown && State != SessionState.Completed)
                {
                    if (DriverBadgeBlip == null || !DriverBadgeBlip.Exists())
                    {
                        DriverBadgeBlip = BlipService.AttachOfficerFootBlip(Driver, Callsign);
                    }
                }
                else
                {
                    BlipService.SafeDeleteBlip(ref DriverBadgeBlip);
                }

                // 2. Passenger foot blip
                if (passengerOutside && State != SessionState.StandingDown && State != SessionState.Completed)
                {
                    if (PassengerBadgeBlip == null || !PassengerBadgeBlip.Exists())
                    {
                        PassengerBadgeBlip = BlipService.AttachOfficerFootBlip(Passenger, Callsign);
                    }
                }
                else
                {
                    BlipService.SafeDeleteBlip(ref PassengerBadgeBlip);
                }

                // 3. Cruiser radar blip: turns White [PARKED] when empty / all living officers are outside
                if (allLivingOutside && anyOutside && State != SessionState.StandingDown && State != SessionState.Completed)
                {
                    if (!_isParkedBlip || RadarBlip == null || !RadarBlip.Exists())
                    {
                        _isParkedBlip = true;
                        BlipService.SafeDeleteBlip(ref RadarBlip);
                        RadarBlip = BlipService.SetUnitState(Cruiser, EntityBlipState.Parked, Callsign);
                    }
                }
                else
                {
                    if (_isParkedBlip)
                    {
                        _isParkedBlip = false;
                        BlipService.SafeDeleteBlip(ref RadarBlip);
                        if (State == SessionState.InPursuit)
                        {
                            // Suppress cruiser blip when officers remount inside pursuit
                            BlipService.SafeDeleteBlip(ref RadarBlip);
                        }
                        else
                        {
                            RadarBlip = BlipService.SetUnitState(Cruiser, EntityBlipState.Patrol, Callsign);
                        }
                    }
                    else if (State == SessionState.InPursuit)
                    {
                        // Maintain cruiser blip suppression as long as officers are inside the vehicle
                        if (RadarBlip != null)
                        {
                            BlipService.SafeDeleteBlip(ref RadarBlip);
                        }
                    }
                }
            }

            public bool IsValid()
            {
                if (Cruiser == null || !Cruiser.Exists() || !Cruiser.IsDriveable) return false;
                if (Driver == null || !Driver.Exists() || !Driver.IsAlive || Driver.IsDead) return false;

                try
                {
                    if (Driver.IsCuffed) return false;
                    if (Functions.IsPedArrested(Driver) || Functions.IsPedGettingArrested(Driver) ||
                        Functions.IsPedBeingCuffed(Driver) || Functions.IsPedKneelingTaskActive(Driver) ||
                        Functions.IsPedCuffedTaskActive(Driver))
                    {
                        return false;
                    }
                }
                catch { }

                return true;
            }

            public void BeginSceneStandby()
            {
                State = SessionState.OnSceneStandby;
                StandbyStartTime = Game.GameTime;

                try
                {
                    // Siren sound completely OFF, silent emergency lightbars stay active
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        NativeTasks.EnforceSteadyCruiserSirenPolicy(Cruiser);
                        NativeTasks.SetSilentEmergencyLights(Cruiser, true);
                    }

                    // Sweep nearby police vehicles within 60m of this cruiser to mute audible sirens
                    MuteNearbyPoliceSirens(Cruiser.Position, 60.0f);

                    // Order all seated officers to exit vehicle immediately and take calm, stationary perimeter guard
                    if (Driver != null && Driver.Exists() && Driver.IsAlive)
                    {
                        Driver.BlockPermanentEvents = true; // Hold until player explicitly dismisses!
                        if (Driver.IsInVehicle(Cruiser, false))
                        {
                            Driver.Tasks.LeaveVehicle(LeaveVehicleFlags.None);
                        }
                        else if (!Driver.IsInCombat && !Driver.IsCuffed)
                        {
                            NativeTasks.TaskCalmSceneGuard(Driver, Cruiser, Cruiser.Position);
                        }

                        try
                        {
                            NativeFunction.Natives.GIVE_WEAPON_TO_PED(Driver, WeaponCombatPistol, 120, false, true);
                            NativeFunction.Natives.SET_CURRENT_PED_WEAPON(Driver, WeaponCombatPistol, true);
                        }
                        catch { }
                    }

                    if (Passenger != null && Passenger.Exists() && Passenger.IsAlive)
                    {
                        Passenger.BlockPermanentEvents = true; // Hold until player explicitly dismisses!
                        if (Passenger.IsInVehicle(Cruiser, false))
                        {
                            Passenger.Tasks.LeaveVehicle(LeaveVehicleFlags.None);
                        }
                        else if (!Passenger.IsInCombat && !Passenger.IsCuffed)
                        {
                            NativeTasks.TaskCalmSceneGuard(Passenger, Cruiser, Cruiser.Position);
                        }

                        try
                        {
                            NativeFunction.Natives.GIVE_WEAPON_TO_PED(Passenger, WeaponCombatPistol, 120, false, true);
                            NativeFunction.Natives.SET_CURRENT_PED_WEAPON(Passenger, WeaponCombatPistol, true);
                        }
                        catch { }
                    }

                    // Synchronize radar blips immediately
                    UpdateBlipStates();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[PursuitFeature] Error in BeginSceneStandby for {Callsign}: {ex.Message}");
                }
            }

            public void DismissFromScene()
            {
                if (State != SessionState.OnSceneStandby) return;

                State = SessionState.StandingDown;
                StandDownStartTime = Game.GameTime;

                // Clean up officer foot blips upon dismissal
                BlipService.SafeDeleteBlip(ref DriverBadgeBlip);
                BlipService.SafeDeleteBlip(ref PassengerBadgeBlip);

                StartTrackedFiber(() =>
                {
                    try
                    {
                        // Remove from active pursuit AI if still registered before clearing tasks
                        LHandle activePursuit = null;
                        try { activePursuit = Functions.GetActivePursuit(); } catch { }
                        if (activePursuit != null)
                        {
                            try
                            {
                                if (Driver != null && Driver.Exists() && Functions.IsPedInPursuit(Driver))
                                {
                                    Functions.RemovePedFromPursuit(Driver);
                                }
                                if (Passenger != null && Passenger.Exists() && Functions.IsPedInPursuit(Passenger))
                                {
                                    Functions.RemovePedFromPursuit(Passenger);
                                }
                            }
                            catch { }
                        }

                        if (Driver != null && Driver.Exists()) Driver.BlockPermanentEvents = false;
                        if (Passenger != null && Passenger.Exists()) Passenger.BlockPermanentEvents = false;

                        // Officers outside cruiser enter their vehicles
                        if (Driver != null && Driver.Exists() && Driver.IsAlive && !Driver.IsInVehicle(Cruiser, false))
                        {
                            Driver.Tasks.EnterVehicle(Cruiser, -1);
                        }

                        if (Passenger != null && Passenger.Exists() && Passenger.IsAlive && !Passenger.IsInVehicle(Cruiser, false))
                        {
                            Passenger.Tasks.EnterVehicle(Cruiser, 0);
                        }

                        // Wait up to 6 seconds for driver to enter cruiser (with warp fallback)
                        uint enterStart = Game.GameTime;
                        while (Driver != null && Driver.Exists() && Driver.IsAlive && !Driver.IsInVehicle(Cruiser, false) && (Game.GameTime - enterStart < 6000))
                        {
                            GameFiber.Sleep(250);
                        }

                        if (Driver != null && Driver.Exists() && Driver.IsAlive && Cruiser != null && Cruiser.Exists() && !Driver.IsInVehicle(Cruiser, false))
                        {
                            Driver.WarpIntoVehicle(Cruiser, -1);
                        }

                        if (Passenger != null && Passenger.Exists() && Passenger.IsAlive && Cruiser != null && Cruiser.Exists() && !Passenger.IsInVehicle(Cruiser, false))
                        {
                            Passenger.WarpIntoVehicle(Cruiser, 0);
                        }

                        // Strictly enforce silent cruising siren policy so sirens don't turn on upon entering!
                        if (Cruiser != null && Cruiser.Exists())
                        {
                            NativeTasks.EnforceSteadyCruiserSirenPolicy(Cruiser);
                            NativeTasks.ShutOffSirenAndLights(Cruiser);
                        }

                        // Cruise away smoothly to safe street node (off train tracks / curbs)
                        if (Driver != null && Driver.Exists() && Driver.IsAlive && Cruiser != null && Cruiser.Exists())
                        {
                            Driver.BlockPermanentEvents = false;
                            NativeTasks.ConfigurePatrolDriver(Driver, Cruiser);

                            if (WorldUtils.GetSafeStreetPosition(Cruiser.Position, 0, out Vector3 roadPos, out _))
                            {
                                NativeTasks.DriveToPositionLongRange(Driver, Cruiser, roadPos, 12.0f, NativeTasks.NormalDrivingFlag, 5.0f);
                            }
                            else
                            {
                                Driver.Tasks.CruiseWithVehicle(Cruiser, 16.0f, NativeTasks.CruisingFlags);
                            }
                        }

                        if (Passenger != null && Passenger.Exists() && Passenger.IsAlive)
                        {
                            Passenger.BlockPermanentEvents = false;
                        }

                        // Recreate radar blip cleanly as a steady patrol blip to eradicate any sticky native flash timers or intervals
                        BlipService.SafeDeleteBlip(ref RadarBlip);
                        if (Cruiser != null && Cruiser.Exists())
                        {
                            RadarBlip = BlipService.SetUnitState(Cruiser, EntityBlipState.Patrol, Callsign);
                            UnitRegistry.Release(Cruiser, UnitRegistry.OwnerPursuit);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[PursuitFeature] Error in DismissFromScene for {Callsign}: {ex.Message}");
                    }
                }, $"Cadence - Dismiss Pursuit Unit {Callsign}");
            }

            public void BeginStandDown()
            {
                State = SessionState.StandingDown;
                StandDownStartTime = Game.GameTime;

                // Clean up officer foot blips upon stand down
                BlipService.SafeDeleteBlip(ref DriverBadgeBlip);
                BlipService.SafeDeleteBlip(ref PassengerBadgeBlip);

                try
                {
                    // Completely shut off sirens and lightbars
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        NativeTasks.ShutOffSirenAndLights(Cruiser);
                    }

                    // Remove from active pursuit AI if still registered
                    LHandle activePursuit = null;
                    try { activePursuit = Functions.GetActivePursuit(); } catch { }
                    if (activePursuit != null)
                    {
                        try
                        {
                            if (Driver != null && Driver.Exists() && Functions.IsPedInPursuit(Driver))
                            {
                                Functions.RemovePedFromPursuit(Driver);
                            }
                            if (Passenger != null && Passenger.Exists() && Functions.IsPedInPursuit(Passenger))
                            {
                                Functions.RemovePedFromPursuit(Passenger);
                            }
                        }
                        catch { }
                    }

                    if (Driver != null && Driver.Exists() && Driver.IsAlive)
                    {
                        Driver.BlockPermanentEvents = false;
                        if (Cruiser != null && Cruiser.Exists())
                        {
                            NativeTasks.ConfigurePatrolDriver(Driver, Cruiser);
                            Driver.Tasks.CruiseWithVehicle(Cruiser, 16.0f, NativeTasks.CruisingFlags);
                        }
                    }

                    if (Passenger != null && Passenger.Exists() && Passenger.IsAlive)
                    {
                        Passenger.BlockPermanentEvents = false;
                    }

                    // Recreate radar blip cleanly as a steady patrol blip to eradicate any sticky native flash timers or intervals
                    if (RadarBlip != null && RadarBlip.Exists())
                    {
                        BlipService.SafeDeleteBlip(ref RadarBlip);
                        if (Cruiser != null && Cruiser.Exists())
                        {
                            RadarBlip = BlipService.AttachPatrolBlip(Cruiser, Callsign);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[PursuitFeature] Error in BeginStandDown for {Callsign}: {ex.Message}");
                }
            }

            public void Dispose(bool immediateDelete)
            {
                // 1. Zero-leak blip destructor (4-step protocol)
                BlipService.SafeDeleteBlip(ref RadarBlip);
                BlipService.SafeDeleteBlip(ref DriverBadgeBlip);
                BlipService.SafeDeleteBlip(ref PassengerBadgeBlip);
                BlipService.SafeDeleteEntityBlip(Cruiser);
                BlipService.SafeDeleteEntityBlip(Driver);
                BlipService.SafeDeleteEntityBlip(Passenger);

                if (Cruiser != null)
                {
                    UnitRegistry.Release(Cruiser, UnitRegistry.OwnerPursuit);
                }
                if (Driver != null)
                {
                    UnitRegistry.Release(Driver, UnitRegistry.OwnerPursuit);
                }
                if (Passenger != null)
                {
                    UnitRegistry.Release(Passenger, UnitRegistry.OwnerPursuit);
                }

                bool hasLivingDriver = Driver != null && Driver.Exists() && Driver.IsAlive;
                bool shouldDeleteCruiser = immediateDelete || !hasLivingDriver;

                // 2. Safe entity dismissals via TeardownHelper
                try
                {
                    if (Passenger != null)
                    {
                        TeardownHelper.SafeDismissPed(Passenger, delete: immediateDelete || !Passenger.IsAlive);
                    }

                    if (Driver != null)
                    {
                        TeardownHelper.SafeDismissPed(Driver, delete: immediateDelete || !Driver.IsAlive);
                    }

                    if (Cruiser != null)
                    {
                        NativeTasks.EnforceSteadyCruiserSirenPolicy(Cruiser);
                        TeardownHelper.SafeDismissVehicle(Cruiser, delete: shouldDeleteCruiser);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[PursuitFeature] Error disposing pursuit unit {Callsign}: {ex.Message}");
                }
                finally
                {
                    State = SessionState.Completed;
                }
            }
        }
    }
}
