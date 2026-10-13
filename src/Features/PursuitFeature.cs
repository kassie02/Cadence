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
                        // Pursuit has terminated or Code 4 was called: stand down active units to patrol
                        InitiateCode4StandDown();
                    }

                    // Watchdog: prune invalid sessions and process stand-down expiration
                    PruneAndWatchSessions(player.Position);

                    if (!isPursuitRunning)
                    {
                        continue;
                    }

                    // Evaluate cruiser involvement count vs MaxPursuitUnits
                    int draftedCount;
                    lock (_syncLock)
                    {
                        draftedCount = _activeSessions.Count(s => s != null && s.State == PursuitUnitSession.SessionState.InPursuit);
                    }

                    int distinctCruiserCount = GetActivePursuitCruiserCount(activePursuit);
                    int totalInvolved = Math.Max(distinctCruiserCount, draftedCount);

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

                    // Resolve target pursuit/suspect location
                    Vector3 targetPos = GetPursuitTargetPosition(activePursuit, player);
                    if (targetPos == Vector3.Zero)
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

                return pursuitCops
                    .Where(p => p != null && p.Exists() && p.CurrentVehicle != null && p.CurrentVehicle.Exists())
                    .Select(p => p.CurrentVehicle.Handle)
                    .Distinct()
                    .Count();
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
                Ped[] suspects = Functions.GetPursuitPeds(activePursuit);
                Ped primarySuspect = suspects?.FirstOrDefault(p => p != null && p.Exists() && p.IsAlive);

                if (primarySuspect != null && primarySuspect.Exists())
                {
                    return primarySuspect.Position;
                }
            }
            catch { }

            return player != null && player.Exists() ? player.Position : Vector3.Zero;
        }

        private static void InitiateCode4StandDown()
        {
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
                        s.BeginStandDown();
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

                    if (s.State == PursuitUnitSession.SessionState.StandingDown)
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

                // Add driver and passenger to pursuit AI
                driver.BlockPermanentEvents = false;
                Functions.AddCopToPursuit(activePursuit, driver);

                if (passenger != null && passenger.Exists() && passenger.IsAlive)
                {
                    passenger.BlockPermanentEvents = false;
                    Functions.AddCopToPursuit(activePursuit, passenger);
                }

                // Switch radar blip to Orange Flashing pursuit blip
                Blip blip = BlipService.AttachPursuitBlip(cruiser, callsign);

                var session = new PursuitUnitSession(cruiser, driver, passenger, blip, callsign, wasSpawned);

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
            public enum SessionState { InPursuit, StandingDown, Completed }

            public Vehicle Cruiser { get; }
            public Ped Driver { get; }
            public Ped Passenger { get; }
            public Blip RadarBlip;
            public string Callsign { get; }
            public uint JoinTime { get; }
            public bool WasAmbientSpawned { get; }
            public SessionState State { get; private set; }
            public uint StandDownStartTime { get; private set; }

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

            public bool IsValid()
            {
                return Cruiser != null && Cruiser.Exists() && Cruiser.IsDriveable &&
                       Driver != null && Driver.Exists() && Driver.IsAlive;
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
