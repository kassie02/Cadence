using System;
using System.Collections.Generic;
using System.Linq;
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
    public static class PursuitFeature
    {
        private static readonly object _syncLock = new object();
        private static readonly List<PursuitUnitSession> _activeSessions = new List<PursuitUnitSession>();
        private static readonly Dictionary<Ped, Blip> _trackedSuspectBlips = new Dictionary<Ped, Blip>();
        private static readonly Random _rand = new Random();

        private static GameFiber _pursuitFiber;
        private static bool _isRunning;
        private static uint _lastJoinTime;
        private static int _callsignCounter = 80;

        private static readonly string[] Divisions = new[]
        {
            "2-LINCOLN", "3-MARY", "4-GEORGE", "5-EDWARD", "6-FRANK", "7-DAVID", "8-OCEAN", "1-ADAM"
        };

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
                _pursuitFiber = GameFiber.StartNew(PursuitProcessLoop, "Cadence - Pursuit Feature Fiber");
            }
        }

        public static void Stop()
        {
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[PursuitFeature] Stopping pursuit feature and releasing drafted units.");
                ReleaseAllPursuitUnits(immediateDelete: false);

                try
                {
                    if (_pursuitFiber != null && _pursuitFiber.IsAlive)
                    {
                        _pursuitFiber.Abort();
                    }
                }
                catch { }
                finally
                {
                    _pursuitFiber = null;
                }
            }
        }

        public static void PurgeAll()
        {
            lock (_syncLock)
            {
                Logger.Info("[PursuitFeature] Purging all active pursuit units.");
                ReleaseAllPursuitUnits(immediateDelete: true);
            }
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
                    else
                    {
                        // Watchdog suspect blips: ensures all fleeing suspects are marked on the minimap
                        UpdateSuspectRadarMarkers(activePursuit);
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

                    // Update dynamic distance-aware blips for all active pursuit sessions:
                    // Flashing Orange when close (<= 100m from suspect), Flashing Red when far (> 140m)
                    lock (_syncLock)
                    {
                        foreach (var s in _activeSessions)
                        {
                            if (s != null && s.State == PursuitUnitSession.SessionState.InPursuit)
                            {
                                s.UpdatePursuitBlip(targetPos);
                            }
                        }
                    }

                    // Evaluate cruiser involvement count vs MaxPursuitUnits cap
                    int draftedCount;
                    lock (_syncLock)
                    {
                        draftedCount = _activeSessions.Count(s => s != null && s.State == PursuitUnitSession.SessionState.InPursuit);
                    }

                    Ped[] pursuitCops = null;
                    try { pursuitCops = Functions.GetPursuitCops(activePursuit); } catch { }
                    int totalCopsInPursuit = pursuitCops?.Count(p => p != null && p.Exists() && p.IsAlive) ?? 0;
                    int distinctCruisers = GetActivePursuitCruiserCount(activePursuit);
                    int totalInvolved = Math.Max(totalCopsInPursuit, Math.Max(distinctCruisers, draftedCount));

                    if (totalInvolved >= Settings.MaxPursuitUnits || draftedCount >= Settings.MaxPursuitUnits)
                    {
                        continue;
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
                    if (IsAnySuspectSurrenderingOrArrested(activePursuit))
                    {
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

        private static Vector3 GetPursuitTargetPosition(LHandle activePursuit, Ped player)
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
                var toRemove = new List<Ped>();
                foreach (var kvp in _trackedSuspectBlips)
                {
                    Ped p = kvp.Key;
                    Blip b = kvp.Value;
                    bool shouldRemove = false;
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
                                Functions.IsPedCuffedTaskActive(p) || !validSuspects.Contains(p))
                            {
                                shouldRemove = true;
                            }
                        }
                        catch
                        {
                            shouldRemove = true;
                        }
                    }

                    if (shouldRemove)
                    {
                        BlipService.SafeDeleteBlip(ref b);
                        toRemove.Add(p);
                    }
                }
                foreach (var p in toRemove)
                {
                    _trackedSuspectBlips.Remove(p);
                }

                // Check active fleeing suspects: if they have no attached blip, attach one!
                foreach (Ped suspect in validSuspects)
                {
                    if (suspect == null || !suspect.Exists() || suspect.IsDead || suspect.IsCuffed)
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

                    // Check if suspect already has ANY active radar marker (on ped or on vehicle)
                    if (BlipService.HasAnyActiveRadarMarker(suspect))
                    {
                        continue;
                    }

                    // Suspect does not have any blip! Attach high-visibility red blip
                    Blip newBlip;
                    if (suspect.IsInAnyVehicle(false) && suspect.CurrentVehicle != null && suspect.CurrentVehicle.Exists())
                    {
                        newBlip = BlipService.AttachSuspectBlip(suspect.CurrentVehicle, "SUSPECT");
                    }
                    else
                    {
                        newBlip = BlipService.AttachSuspectBlip(suspect, "SUSPECT");
                    }

                    if (newBlip != null && newBlip.Exists())
                    {
                        _trackedSuspectBlips[suspect] = newBlip;
                        Logger.Debug($"[PursuitFeature] Attached watchdog suspect blip to {suspect.Model.Name} (0x{suspect.Handle:X}).");
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
                foreach (var kvp in _trackedSuspectBlips)
                {
                    Blip b = kvp.Value;
                    BlipService.SafeDeleteBlip(ref b);
                }
                _trackedSuspectBlips.Clear();
            }
            catch { }
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
                            s.Dispose(immediateDelete: false);
                        }
                        _activeSessions.RemoveAt(i);
                        continue;
                    }

                    if (s.State == PursuitUnitSession.SessionState.OnSceneStandby)
                    {
                        float dist = s.Cruiser.Position.DistanceTo(playerPos);
                        // Safety timeout & distance leak protection: auto-dismiss if player leaves (> 250m) or after 5 minutes (300,000ms)
                        if (dist > 250.0f || (now - s.StandbyStartTime > 300000))
                        {
                            s.DismissFromScene();
                        }
                    }
                    else if (s.State == PursuitUnitSession.SessionState.StandingDown)
                    {
                        float dist = s.Cruiser.Position.DistanceTo(playerPos);
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

        private static uint _dismissKeyDownTime;

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
                return;
            }

            Vector3 playerPos = player.Position;
            bool playerNearScene = standbyUnits.Any(s => s.Cruiser != null && s.Cruiser.Exists() && s.Cruiser.Position.DistanceTo(playerPos) <= 45.0f);

            if (playerNearScene)
            {
                string keyName = Settings.DismissBackupKey == System.Windows.Forms.Keys.Enter ? "ENTER" : Settings.DismissBackupKey.ToString().ToUpperInvariant();
                Game.DisplayHelp($"Hold ~b~[{keyName}]~s~ to dismiss pursuit scene officers", 1000);

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
                foreach (var s in _activeSessions)
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

                    if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                    {
                        cruiser = ubResult.Item1;
                        driver = ubResult.Item2[0];
                        if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                    }
                }

                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("PursuitFeature", $"Ultimate Backup did not return a cruiser for pursuit at {spawnPos}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                        return false;
                    }

                    string vehicleModel = isHighway ? ZoneSpawnData.GetDefaultPoliceVehicleForPosition(spawnPos) : ZoneSpawnData.PoliceVehiclesCity[0];
                    string copModel = ZoneSpawnData.GetDefaultCopPedModelForPosition(spawnPos);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(spawnPos, spawnHeading, vehicleModel, copModel);
                    if (nativeResult != null && nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                    {
                        cruiser = nativeResult.Item1;
                        driver = nativeResult.Item2[0];
                        if (Settings.EnableEupOutfits)
                        {
                            EupService.ApplyBodyArmor(driver);
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
            }

            return false;
        }

        public static void RequestGroupPursuit(int count)
        {
            GameFiber.StartNew(() =>
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

                    if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                    {
                        cruiser = ubResult.Item1;
                        driver = ubResult.Item2[0];
                        if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                    }
                }

                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("PursuitFeature", $"Ultimate Backup did not return a cruiser for pursuit at {spawnPos}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                        return;
                    }

                    string vehicleModel = ZoneSpawnData.GetDefaultPoliceVehicleForPosition(spawnPos);
                    string copModel = ZoneSpawnData.GetDefaultCopPedModelForPosition(spawnPos);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(spawnPos, spawnHeading, vehicleModel, copModel);
                    if (nativeResult != null && nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                    {
                        cruiser = nativeResult.Item1;
                        driver = nativeResult.Item2[0];
                        if (Settings.EnableEupOutfits)
                        {
                            EupService.ApplyBodyArmor(driver);
                        }
                    }
                }

                if (cruiser != null && cruiser.Exists() && driver != null && driver.Exists())
                {
                    cruiser.Heading = spawnHeading;
                    WorldUtils.SecureEntityGround(cruiser);
                    DraftCruiserIntoPursuit(activePursuit, cruiser, driver, passenger, wasSpawned: true);
                }
            }
        }

        private static Vehicle FindCandidateAmbientCruiser(Vector3 targetPos)
        {
            try
            {
                Entity[] nearbyVehicles = World.GetEntities(targetPos, 380.0f, GetEntitiesFlags.ConsiderAllVehicles);
                if (nearbyVehicles == null || nearbyVehicles.Length == 0) return null;

                return nearbyVehicles
                    .OfType<Vehicle>()
                    .Where(v => v != null && v.Exists() && !v.IsDead && v.IsDriveable)
                    .Where(v => v.Driver != null && v.Driver.Exists() && v.Driver.IsAlive && !v.Driver.IsPlayer && v.Driver != Game.LocalPlayer?.Character)
                    .Where(v =>
                    {
                        bool isPolice = v.IsPoliceVehicle || v.Model.IsLawEnforcementVehicle ||
                                        PatrolFeature.IsActiveCruiser(v) || StationaryFeature.IsActiveCruiser(v) ||
                                        TrafficStopFeature.IsActiveCruiser(v) || ResponseFeature.IsActiveCruiser(v) ||
                                        Functions.IsPedACop(v.Driver) || v.Driver.RelationshipGroup == "COP";
                        if (!isPolice) return false;

                        lock (_syncLock)
                        {
                            if (_activeSessions.Any(s => s.Cruiser == v)) return false;
                        }
                        bool inPursuit = false;
                        try { inPursuit = Functions.IsPedInPursuit(v.Driver); } catch { }
                        return !inPursuit;
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
                }

                string callsign = GenerateCallsign();

                cruiser.MakePersistent();
                driver.MakePersistent();
                if (passenger != null && passenger.Exists()) passenger.MakePersistent();
                cruiser.IsEngineOn = true;
                cruiser.IsSirenOn = true;
                cruiser.IsSirenSilent = false;
                NativeTasks.ConfigureEmergencyDriver(driver, cruiser);

                // Add driver and passenger to pursuit AI (purging old ambient cruising tasks first)
                driver.Tasks.Clear();
                driver.BlockPermanentEvents = false;
                Functions.AddCopToPursuit(activePursuit, driver);

                if (passenger != null && passenger.Exists() && passenger.IsAlive)
                {
                    passenger.Tasks.Clear();
                    passenger.BlockPermanentEvents = false;
                    Functions.AddCopToPursuit(activePursuit, passenger);
                }

                // Distance-aware blip: Orange [PURSUIT] when within 100m of chase target, Red [CODE 3] when responding from far
                Ped player = Game.LocalPlayer?.Character;
                Vector3 targetPos = GetPursuitTargetPosition(activePursuit, player);
                float distToTarget = (targetPos != Vector3.Zero) ? cruiser.Position.DistanceTo(targetPos) : 200.0f;

                Blip blip;
                bool isOrange;
                if (distToTarget <= 100.0f)
                {
                    blip = BlipService.AttachPursuitBlip(cruiser, callsign);
                    isOrange = true;
                }
                else
                {
                    blip = BlipService.AttachEmergencyBlip(cruiser, $"{callsign} [CODE 3]");
                    isOrange = false;
                }

                var session = new PursuitUnitSession(cruiser, driver, passenger, blip, callsign, wasSpawned, isOrange);

                lock (_syncLock)
                {
                    _activeSessions.Add(session);
                    _lastJoinTime = Game.GameTime;
                }
                SessionMetrics.IncrementPursuit();
                Logger.Info($"[PursuitFeature] Ambient cruiser {callsign} successfully drafted into pursuit (Total active: {_activeSessions.Count}/{Settings.MaxPursuitUnits}).");
            }
            catch (Exception ex)
            {
                Logger.Error("[PursuitFeature] Error drafting cruiser into pursuit", ex);
            }
        }

        private static void ReleaseAllPursuitUnits(bool immediateDelete)
        {
            ClearAllSuspectBlips();
            lock (_syncLock)
            {
                foreach (var session in _activeSessions)
                {
                    if (session != null)
                    {
                        session.Dispose(immediateDelete);
                    }
                }
                _activeSessions.Clear();
            }
        }

        private static bool TryFindRoadSpawnPosition(Vector3 targetPos, out Vector3 spawnPos, out float spawnHeading)
        {
            spawnPos = Vector3.Zero;
            spawnHeading = 0f;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                float distance = (float)_rand.Next(180, 280);
                double angle = _rand.NextDouble() * Math.PI * 2.0;

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
            string div = Divisions[_rand.Next(Divisions.Length)];
            int num = _callsignCounter++;
            if (_callsignCounter > 99) _callsignCounter = 80;
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
            public string Callsign { get; }
            public uint JoinTime { get; }
            public bool WasAmbientSpawned { get; }
            public bool IsOrangePursuitBlip { get; set; }
            public SessionState State { get; private set; }
            public uint StandbyStartTime { get; private set; }
            public uint StandDownStartTime { get; private set; }

            public PursuitUnitSession(Vehicle cruiser, Ped driver, Ped passenger, Blip blip, string callsign, bool wasSpawned, bool isOrange = true)
            {
                Cruiser = cruiser;
                Driver = driver;
                Passenger = passenger;
                RadarBlip = blip;
                Callsign = callsign;
                JoinTime = Game.GameTime;
                WasAmbientSpawned = wasSpawned;
                State = SessionState.InPursuit;
                IsOrangePursuitBlip = isOrange;
            }

            public void UpdatePursuitBlip(Vector3 targetPos)
            {
                if (RadarBlip == null || !RadarBlip.Exists() || Cruiser == null || !Cruiser.Exists() || State != SessionState.InPursuit) return;

                float dist = Cruiser.Position.DistanceTo(targetPos);
                if (dist <= 100.0f && !IsOrangePursuitBlip)
                {
                    IsOrangePursuitBlip = true;
                    BlipService.TransitionToPursuit(RadarBlip, Callsign);
                }
                else if (dist > 140.0f && IsOrangePursuitBlip)
                {
                    IsOrangePursuitBlip = false;
                    BlipService.TransitionToEmergency(RadarBlip, Callsign);
                }
            }

            public bool IsValid()
            {
                return Cruiser != null && Cruiser.Exists() && Cruiser.IsDriveable &&
                       Driver != null && Driver.Exists() && Driver.IsAlive;
            }

            public void BeginSceneStandby()
            {
                State = SessionState.OnSceneStandby;
                StandbyStartTime = Game.GameTime;

                try
                {
                    // Siren sound OFF, silent emergency lightbar stays active
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        Cruiser.IsSirenOn = true;
                        Cruiser.IsSirenSilent = true;
                        NativeTasks.SetSilentEmergencyLights(Cruiser, true);
                    }

                    // Seated driver stays parked
                    if (Driver != null && Driver.Exists() && Driver.IsAlive)
                    {
                        if (Driver.IsInVehicle(Cruiser, false))
                        {
                            Driver.Tasks.PerformDrivingManeuver(VehicleManeuver.Wait);
                        }
                        else
                        {
                            // Keep BlockPermanentEvents true so cops do not wander off or spasm before player dismisses them
                            Driver.BlockPermanentEvents = true;
                            if (!Driver.IsInCombat && !Driver.IsCuffed)
                            {
                                NativeFunction.Natives.TASK_STAND_STILL(Driver, -1);
                            }
                        }
                    }

                    if (Passenger != null && Passenger.Exists() && Passenger.IsAlive)
                    {
                        if (!Passenger.IsInVehicle(Cruiser, false))
                        {
                            Passenger.BlockPermanentEvents = true;
                            if (!Passenger.IsInCombat && !Passenger.IsCuffed)
                            {
                                NativeFunction.Natives.TASK_STAND_STILL(Passenger, -1);
                            }
                        }
                    }
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

                GameFiber.StartNew(() =>
                {
                    try
                    {
                        // Officers outside cruiser enter their vehicles
                        if (Driver != null && Driver.Exists() && Driver.IsAlive && !Driver.IsInVehicle(Cruiser, false))
                        {
                            Driver.Tasks.EnterVehicle(Cruiser, -1);
                        }

                        if (Passenger != null && Passenger.Exists() && Passenger.IsAlive && !Passenger.IsInVehicle(Cruiser, false))
                        {
                            Passenger.Tasks.EnterVehicle(Cruiser, 0);
                        }

                        // Wait up to 6 seconds for driver to enter cruiser
                        uint enterStart = Game.GameTime;
                        while (Driver != null && Driver.Exists() && Driver.IsAlive && !Driver.IsInVehicle(Cruiser, false) && (Game.GameTime - enterStart < 6000))
                        {
                            GameFiber.Sleep(250);
                        }

                        // Turn off emergency lightbars
                        if (Cruiser != null && Cruiser.Exists())
                        {
                            Cruiser.IsSirenOn = false;
                            Cruiser.IsSirenSilent = false;
                            NativeTasks.SetSilentEmergencyLights(Cruiser, false);
                        }

                        // Cruise away smoothly
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

                        // Restore Navy Blue routine patrol blip
                        BlipService.TransitionToPatrol(RadarBlip, Callsign);
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

                try
                {
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        Cruiser.IsSirenOn = false;
                        Cruiser.IsSirenSilent = false;
                        NativeTasks.SetSilentEmergencyLights(Cruiser, false);
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

                    // Restore Navy Blue routine patrol blip
                    BlipService.TransitionToPatrol(RadarBlip, Callsign);
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
                BlipService.SafeDeleteEntityBlip(Cruiser);
                BlipService.SafeDeleteEntityBlip(Driver);
                BlipService.SafeDeleteEntityBlip(Passenger);

                // 2. Entities clean up
                try
                {
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        Cruiser.IsSirenOn = false;
                        Cruiser.IsSirenSilent = false;
                    }

                    if (Passenger != null && Passenger.Exists())
                    {
                        Passenger.BlockPermanentEvents = false;
                        if (immediateDelete) Passenger.Delete();
                        else Passenger.Dismiss();
                    }

                    if (Driver != null && Driver.Exists())
                    {
                        Driver.BlockPermanentEvents = false;
                        if (immediateDelete)
                        {
                            Driver.Delete();
                        }
                        else
                        {
                            Driver.Dismiss();
                        }
                    }

                    if (Cruiser != null && Cruiser.Exists())
                    {
                        if (immediateDelete) Cruiser.Delete();
                        else Cruiser.Dismiss();
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
