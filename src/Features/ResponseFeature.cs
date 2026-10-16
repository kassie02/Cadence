using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;
using LSPD_First_Response.Mod.Callouts;
using Cadence.Core;
using Cadence.Common;
using Cadence.Services;
using Cadence.Data;

namespace Cadence.Features
{
    /// <summary>
    /// Feature 4: Responding Units & Rejected Callout Takeover
    /// Dispatches ambient cruisers on autonomous Code 2/3 emergency runs, and hooks LSPDFR
    /// to silently take over rejected/declined player callouts with the nearest available cruiser.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public sealed class ResponseFeature : IFeature
    {
        public static ResponseFeature Instance { get; } = new ResponseFeature();
        public string Name => "Response";
        public bool IsRunning => _isRunning;

        void IFeature.Start() => Start();
        void IFeature.Stop() => Stop();
        void IFeature.PurgeAll(bool immediateDelete) => PurgeAll(immediateDelete);
        int IFeature.GetActiveCount() => GetActiveCount();

        private static readonly object _syncLock = new object();
        private static readonly List<ResponseUnitInstance> _activeUnits = new List<ResponseUnitInstance>();
        private static readonly Random _rand = new Random();

        private static GameFiber _responseFiber;
        private static bool _isRunning;
        private static uint _lastAutonomousRunTime;
        private static int _callsignCounter = 70;

        private static readonly string[] Divisions = new[]
        {
            "1-DAVID", "2-OCEAN", "3-FRANK", "4-ADAM", "5-LINCOLN", "6-MARY", "7-GEORGE", "8-EDWARD"
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
                _lastAutonomousRunTime = Game.GameTime + 105000;

                // Hook LSPDFR rejected callout event
                try
                {
                    Events.OnCalloutNotAccepted += OnLspdfrCalloutNotAccepted;
                    Logger.Info("[ResponseFeature] Hooked LSPDFR Events.OnCalloutNotAccepted.");
                }
                catch (Exception ex)
                {
                    Logger.Error("[ResponseFeature] Failed to hook OnCalloutNotAccepted", ex);
                }

                Logger.Info("[ResponseFeature] Starting response background fiber (initial run staggered at T+105s).");
                _responseFiber = GameFiber.StartNew(ResponseProcessLoop, "Cadence - Response Feature Fiber");
            }
        }

        public static void Stop()
        {
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;
            }

            // Unhook LSPDFR rejected callout event
            try
            {
                Events.OnCalloutNotAccepted -= OnLspdfrCalloutNotAccepted;
                Logger.Info("[ResponseFeature] Unhooked LSPDFR Events.OnCalloutNotAccepted.");
            }
            catch { }

            Logger.Info("[ResponseFeature] Stopping response feature and purging units.");
            PurgeAllInternal(immediateDelete: false);

            try
            {
                if (_responseFiber != null && _responseFiber.IsAlive)
                {
                    uint startWait = Game.GameTime;
                    while (_responseFiber.IsAlive && (Game.GameTime - startWait < 1500))
                    {
                        GameFiber.Sleep(50);
                    }
                    if (_responseFiber.IsAlive)
                    {
                        _responseFiber.Abort();
                    }
                }
            }
            catch { }
            finally
            {
                _responseFiber = null;
            }
        }

        public static bool TriggerTestRun()
        {
            try
            {
                Ped player = Game.LocalPlayer?.Character;
                if (player == null || !player.Exists()) return false;

                Logger.Info("[ResponseFeature] Triggering manual test emergency run.");

                Vector3 playerPos = player.Position;
                // Find road node 450m - 650m away from player
                if (!TryFindRoadDestination(playerPos, 450f, 650f, out Vector3 targetPos))
                {
                    Logger.Warn("[ResponseFeature] Failed to find valid destination road node for test emergency run.");
                    return false;
                }

                // Dispatch nearest available cruiser or spawn a dedicated response unit
                ResponseUnitInstance unit = DispatchOrSpawnCruiser(targetPos, isCode3: true, isCalloutTakeover: false);
                if (unit != null && unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                    }
                    Logger.Info($"[ResponseFeature] Successfully dispatched test response unit {unit.Callsign} to {targetPos}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[ResponseFeature] TriggerTestRun encountered an error", ex);
            }

            return false;
        }

        public static void PurgeAll(bool immediateDelete = true)
        {
            Logger.Info("[ResponseFeature] Purging all active response units.");
            PurgeAllInternal(immediateDelete);
        }

        public static int GetActiveCount()
        {
            lock (_syncLock)
            {
                _activeUnits.RemoveAll(u => u == null || !u.IsValid());
                return _activeUnits.Count;
            }
        }

        /// <summary>
        /// Relinquishes control of a response cruiser to an external system (e.g. BackupFeature).
        /// Safely deletes radar blip, releases handbrake, clears tasks, and drops from internal tracking.
        /// </summary>
        public static bool RelinquishCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;

            ResponseUnitInstance unitToRelinquish = null;
            lock (_syncLock)
            {
                for (int i = _activeUnits.Count - 1; i >= 0; i--)
                {
                    ResponseUnitInstance unit = _activeUnits[i];
                    if (unit != null && unit.Cruiser == cruiser)
                    {
                        unitToRelinquish = unit;
                        _activeUnits.RemoveAt(i);
                        break;
                    }
                }
            }

            if (unitToRelinquish != null)
            {
                Logger.Info($"[ResponseFeature] Relinquishing cruiser 0x{cruiser.Handle:X} ({unitToRelinquish.Callsign}) to external feature.");
                unitToRelinquish.Relinquish();
                return true;
            }

            return false;
        }

        public static bool IsActiveCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;
            lock (_syncLock)
            {
                return _activeUnits.Any(u => u != null && u.Cruiser == cruiser);
            }
        }

        public static List<Vehicle> GetActiveCruisers()
        {
            lock (_syncLock)
            {
                return _activeUnits
                    .Where(u => u != null && u.IsValid() && u.Cruiser != null && u.Cruiser.Exists())
                    .Select(u => u.Cruiser)
                    .ToList();
            }
        }

        // ====================================================================
        // LSPDFR Event Hook (Part B: Rejected Callout Takeover)
        // ====================================================================

        private static void OnLspdfrCalloutNotAccepted(LHandle lhandle)
        {
            if (!_isRunning || !Settings.EnablePlugin || !Settings.EnableRejectedCallouts)
            {
                return;
            }

            GameFiber.StartNew(() =>
            {
                try
                {
                    if (lhandle == null) return;

                    Vector3 calloutPos = ExtractCalloutPosition(lhandle);
                    if (calloutPos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(calloutPos))
                    {
                        Logger.Debug("[ResponseFeature] Callout rejected, but could not resolve a valid scene position.");
                        return;
                    }

                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists()) return;

                    float distToPlayer = calloutPos.DistanceTo(player.Position);
                    // Immersion filter: If callout was right on top of player (< 60m), skip takeover to avoid unnatural pop-in
                    if (distToPlayer < 60.0f)
                    {
                        Logger.Debug($"[ResponseFeature] Rejected callout is too close ({distToPlayer:F0}m). Skipping takeover.");
                        return;
                    }

                    Logger.Info($"[ResponseFeature] Rejected LSPDFR callout detected at {calloutPos} ({distToPlayer:F0}m from player). Silently dispatching nearest Cadence unit.");

                    ResponseUnitInstance unit = DispatchOrSpawnCruiser(calloutPos, isCode3: true, isCalloutTakeover: true);
                    if (unit != null && unit.IsValid())
                    {
                        lock (_syncLock)
                        {
                            _activeUnits.Add(unit);
                        }
                        SessionMetrics.IncrementEmergencyRun();
                        Logger.Info($"[ResponseFeature] Unit {unit.Callsign} successfully dispatched Code 3 to rejected callout scene.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("[ResponseFeature] Error handling OnCalloutNotAccepted", ex);
                }
            }, "Cadence - Callout Takeover Fiber");
        }

        /// <summary>
        /// Uses reflection on LHandle to safely extract Callout.CalloutPosition without external dependencies.
        /// </summary>
        private static Vector3 ExtractCalloutPosition(LHandle lhandle)
        {
            if (lhandle == null) return Vector3.Zero;

            try
            {
                PropertyInfo prop = null;
                try
                {
                    prop = typeof(LHandle).GetProperty("Object", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                catch { }

                object calloutObj = null;
                try
                {
                    calloutObj = prop?.GetValue(lhandle);
                }
                catch { }

                if (calloutObj is Callout callout)
                {
                    try
                    {
                        return callout.CalloutPosition;
                    }
                    catch { }
                }

                if (calloutObj != null)
                {
                    PropertyInfo posProp = null;
                    try
                    {
                        posProp = calloutObj.GetType().GetProperty("CalloutPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                               ?? calloutObj.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    catch { }

                    if (posProp != null)
                    {
                        try
                        {
                            if (posProp.GetValue(calloutObj) is Vector3 v3)
                            {
                                return v3;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[ResponseFeature] ExtractCalloutPosition reflection failed: {ex.Message}");
            }

            return Vector3.Zero;
        }

        // ====================================================================
        // Background Process Fiber (Part A: Autonomous Emergency Runs)
        // ====================================================================

        private static void ResponseProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    GameFiber.Sleep(1200);

                    if (!_isRunning || !Settings.EnablePlugin || (!Settings.EnableEmergencyRuns && !Settings.EnableRejectedCallouts))
                    {
                        continue;
                    }

                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists())
                    {
                        continue;
                    }

                    // 1. Manage Active Responding Units & Watchdog
                    RunResponseWatchdog(player.Position);

                    // 2. Autonomous Emergency Run Generation
                    RunAutonomousSpawningCycle(player.Position);
                }
                catch (System.Threading.ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[ResponseFeature] Unexpected exception in response fiber", ex);
                    GameFiber.Sleep(3000);
                }
            }
        }

        private static void RunResponseWatchdog(Vector3 playerPos)
        {
            ResponseUnitInstance[] snapshot;
            lock (_syncLock)
            {
                snapshot = _activeUnits.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                ResponseUnitInstance unit = snapshot[i];
                if (unit == null) continue;

                if (!unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        if (!_activeUnits.Contains(unit)) continue;
                        _activeUnits.Remove(unit);
                        Logger.Debug($"[ResponseFeature] Removing invalid/dead response unit {unit.Callsign}. Active remaining: {_activeUnits.Count}/{Settings.MaxEmergencyRuns}");
                    }
                    unit.Dispose(immediateDelete: false);
                    continue;
                }

                // Subterranean watchdog
                if (WorldUtils.IsSubterraneanOrVoid(unit.Cruiser.Position))
                {
                    lock (_syncLock)
                    {
                        if (!_activeUnits.Contains(unit)) continue;
                        _activeUnits.Remove(unit);
                        Logger.Warn($"[ResponseFeature] Unit {unit.Callsign} fell into subterranean void. Removing. Active remaining: {_activeUnits.Count}/{Settings.MaxEmergencyRuns}");
                    }
                    unit.Dispose(immediateDelete: true);
                    continue;
                }

                // Despawn distance watchdog: clean removal if far from player (> 550m)
                float distToPlayer = unit.Cruiser.Position.DistanceTo(playerPos);
                if (distToPlayer > (Settings.DespawnDistance + 100.0f))
                {
                    lock (_syncLock)
                    {
                        if (!_activeUnits.Contains(unit)) continue;
                        _activeUnits.Remove(unit);
                        Logger.Debug($"[ResponseFeature] Unit {unit.Callsign} out of range ({distToPlayer:F0}m). Cleaning up. Active remaining: {_activeUnits.Count}/{Settings.MaxEmergencyRuns}");
                    }
                    unit.Dispose(immediateDelete: false);
                    continue;
                }

                // Manage State Transitions (EnRoute -> Arrived -> Finished)
                unit.UpdateState(playerPos);

                if (unit.IsFinished)
                {
                    lock (_syncLock)
                    {
                        if (!_activeUnits.Contains(unit)) continue;
                        _activeUnits.Remove(unit);
                        Logger.Info($"[ResponseFeature] Unit {unit.Callsign} finished response lifecycle. Cleaning up. Active remaining: {_activeUnits.Count}/{Settings.MaxEmergencyRuns}");
                    }
                    unit.Dispose(immediateDelete: false);
                }
            }
        }

        public enum EmergencyServiceType
        {
            Police,
            Fire,
            Ambulance
        }

        private static void RunAutonomousSpawningCycle(Vector3 playerPos)
        {
            if (!Settings.EnableEmergencyRuns)
            {
                return;
            }

            // Suppress autonomous emergency runs while the player is handling an active LSPDFR callout
            try
            {
                if (Functions.IsCalloutRunning())
                {
                    LHandle currentCallout = Functions.GetCurrentCallout();
                    if (currentCallout != null && Functions.GetCalloutAcceptanceState(currentCallout) == CalloutAcceptanceState.Running)
                    {
                        return;
                    }
                }
            }
            catch { }

            int currentCount;
            lock (_syncLock)
            {
                currentCount = _activeUnits.Count;
            }

            // Cap simultaneous response runs based on settings
            if (currentCount >= Settings.MaxEmergencyRuns)
            {
                return;
            }

            // Pacing: 60 - 95 seconds between autonomous runs (30 - 50s in Chaos mode)
            uint now = Game.GameTime;
            uint cooldown;
            lock (_rand)
            {
                cooldown = Settings.ChaosMode ? (uint)_rand.Next(30000, 50000) : (uint)_rand.Next(60000, 95000);
            }
            if (now < _lastAutonomousRunTime || now - _lastAutonomousRunTime < cooldown)
            {
                return;
            }
            _lastAutonomousRunTime = now; // [D8] Immediately advance pacing timer

            // Road node 400m - 800m away from player
            if (TryFindRoadDestination(playerPos, 400.0f, 800.0f, out Vector3 targetPos))
            {
                bool isCode3;
                lock (_rand)
                {
                    isCode3 = _rand.NextDouble() >= 0.30; // 70% Code 3, 30% Code 2
                }
                ResponseUnitInstance unit = DispatchOrSpawnCruiser(targetPos, isCode3, isCalloutTakeover: false);
                if (unit != null && unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                    }
                    SessionMetrics.IncrementEmergencyRun();
                    Logger.Info($"[ResponseFeature] Autonomous {(isCode3 ? "Code 3" : "Code 2")} {unit.ServiceType} response run dispatched for unit {unit.Callsign} to {targetPos}.");
                }
            }
        }

        // ====================================================================
        // Unit Dispatch & Entity Spawner
        // ====================================================================

        private static ResponseUnitInstance DispatchOrSpawnCruiser(Vector3 targetPos, bool isCode3, bool isCalloutTakeover)
        {
            Ped player = Game.LocalPlayer?.Character;
            Vector3 playerPos = player != null && player.Exists() ? player.Position : Vector3.Zero;

            // Determine service type (Police, Fire, Ambulance)
            EmergencyServiceType serviceType = EmergencyServiceType.Police;
            if (!isCalloutTakeover)
            {
                var candidateServices = new List<EmergencyServiceType>();
                if (Settings.AllowPoliceRuns)
                {
                    candidateServices.Add(EmergencyServiceType.Police);
                    candidateServices.Add(EmergencyServiceType.Police); // 50% base weight
                }
                if (Settings.EnableEmergencyVariety)
                {
                    if (Settings.AllowFireRuns)
                    {
                        candidateServices.Add(EmergencyServiceType.Fire); // 25% base weight
                    }
                    if (Settings.AllowAmbulanceRuns)
                    {
                        candidateServices.Add(EmergencyServiceType.Ambulance); // 25% base weight
                    }
                }

                if (candidateServices.Count == 0)
                {
                    Logger.Debug("ResponseFeature", "No emergency service types allowed by settings. Aborting spawn.");
                    return null;
                }

                lock (_rand)
                {
                    serviceType = candidateServices[_rand.Next(candidateServices.Count)];
                }
            }

            // 1. Try to find an available idle ambient police cruiser within 300m (Police runs only)
            if (serviceType == EmergencyServiceType.Police)
            {
                Vehicle candidateVeh = FindIdleAmbientPoliceVehicle(playerPos, targetPos);
                if (candidateVeh != null && candidateVeh.Exists())
                {
                    Ped driver = candidateVeh.Driver;
                    if (driver != null && driver.Exists() && driver.IsAlive)
                    {
                        PatrolFeature.RelinquishCruiser(candidateVeh);
                        StationaryFeature.RelinquishCruiser(candidateVeh);
                        TrafficStopFeature.RelinquishCruiser(candidateVeh);

                        if (UnitRegistry.TryClaim(candidateVeh, UnitRegistry.OwnerResponse))
                        {
                            if (UnitRegistry.TryClaim(driver, UnitRegistry.OwnerResponse))
                            {
                                string existingCallsign = GenerateCallsign();
                                ResponseUnitInstance unit = SetupResponseUnit(candidateVeh, driver, null, targetPos, isCode3, existingCallsign, EmergencyServiceType.Police, isCalloutTakeover);
                                if (unit != null)
                                {
                                    return unit;
                                }
                                UnitRegistry.Release(driver, UnitRegistry.OwnerResponse);
                            }
                            UnitRegistry.Release(candidateVeh, UnitRegistry.OwnerResponse);
                        }
                    }
                }
            }

            // 2. If no existing cruiser is available, spawn a fast-response unit on a road node
            if (TryFindRoadSpawnPosition(playerPos, targetPos, out Vector3 spawnPos, out float spawnHeading))
            {
                if (serviceType == EmergencyServiceType.Ambulance)
                {
                    return SpawnAmbulanceUnit(spawnPos, spawnHeading, targetPos);
                }
                else if (serviceType == EmergencyServiceType.Fire)
                {
                    return SpawnFireUnit(spawnPos, spawnHeading, targetPos);
                }
                else
                {
                    // Police Emergency Run
                    string callsign = GenerateCallsign();
                    Vehicle cruiser = null;
                    Ped driver = null;
                    Ped passenger = null;

                    // Ultimate Backup or Native Fallback
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
                            Logger.Debug("ResponseFeature", $"Ultimate Backup did not return a response cruiser at {spawnPos}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                            return null;
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

                        if (UnitRegistry.TryClaim(cruiser, UnitRegistry.OwnerResponse))
                        {
                            if (UnitRegistry.TryClaim(driver, UnitRegistry.OwnerResponse))
                            {
                                if (passenger != null && passenger.Exists())
                                {
                                    UnitRegistry.TryClaim(passenger, UnitRegistry.OwnerResponse);
                                }

                                ResponseUnitInstance unit = SetupResponseUnit(cruiser, driver, passenger, targetPos, isCode3, callsign, EmergencyServiceType.Police, isCalloutTakeover);
                                if (unit != null)
                                {
                                    return unit;
                                }

                                if (passenger != null && passenger.Exists())
                                {
                                    UnitRegistry.Release(passenger, UnitRegistry.OwnerResponse);
                                }
                                UnitRegistry.Release(driver, UnitRegistry.OwnerResponse);
                            }
                            UnitRegistry.Release(cruiser, UnitRegistry.OwnerResponse);
                        }

                        TeardownHelper.SafeDismissPed(driver, delete: true);
                        if (passenger != null && passenger.Exists())
                        {
                            TeardownHelper.SafeDismissPed(passenger, delete: true);
                        }
                        TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    }
                }
            }

            return null;
        }

        private static ResponseUnitInstance SpawnAmbulanceUnit(Vector3 spawnPos, float spawnHeading, Vector3 destination)
        {
            try
            {
                string callsign;
                lock (_rand)
                {
                    callsign = $"MEDIC-{_rand.Next(1, 100):D2}";
                }
                Vehicle amb = null;
                Ped driver = null;
                Ped passenger = null;

                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    try
                    {
                        var ubResult = UltimateBackupService.SpawnAmbulanceUnit(spawnPos, 2);
                        if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                        {
                            amb = ubResult.Item1;
                            driver = ubResult.Item2[0];
                            if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                        }
                    }
                    catch (Exception ubEx)
                    {
                        Logger.Warn($"[ResponseFeature] Ultimate Backup Ambulance dispatch error: {ubEx.Message}");
                    }
                }

                if (amb == null || !amb.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("ResponseFeature", $"Ultimate Backup did not return ambulance and Fallback is disabled (Fallback=false). Aborting ambulance spawn.");
                        return null;
                    }

                    var nativeResult = UltimateBackupService.SpawnNativeAmbulanceUnit(spawnPos, spawnHeading);
                    if (nativeResult != null && nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                    {
                        amb = nativeResult.Item1;
                        driver = nativeResult.Item2[0];
                    }
                }

                if (amb == null || !amb.Exists() || driver == null || !driver.Exists()) return null;

                amb.Heading = spawnHeading;
                WorldUtils.SecureEntityGround(amb);

                if (UnitRegistry.TryClaim(amb, UnitRegistry.OwnerResponse))
                {
                    if (UnitRegistry.TryClaim(driver, UnitRegistry.OwnerResponse))
                    {
                        if (passenger != null && passenger.Exists())
                        {
                            UnitRegistry.TryClaim(passenger, UnitRegistry.OwnerResponse);
                        }

                        ResponseUnitInstance unit = SetupResponseUnit(amb, driver, passenger, destination, true, callsign, EmergencyServiceType.Ambulance);
                        if (unit != null)
                        {
                            return unit;
                        }

                        if (passenger != null && passenger.Exists())
                        {
                            UnitRegistry.Release(passenger, UnitRegistry.OwnerResponse);
                        }
                        UnitRegistry.Release(driver, UnitRegistry.OwnerResponse);
                    }
                    UnitRegistry.Release(amb, UnitRegistry.OwnerResponse);
                }

                TeardownHelper.SafeDismissPed(driver, delete: true);
                if (passenger != null && passenger.Exists())
                {
                    TeardownHelper.SafeDismissPed(passenger, delete: true);
                }
                TeardownHelper.SafeDismissVehicle(amb, delete: true);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("[ResponseFeature] Failed to spawn ambulance unit", ex);
                return null;
            }
        }

        private static ResponseUnitInstance SpawnFireUnit(Vector3 spawnPos, float spawnHeading, Vector3 destination)
        {
            try
            {
                string callsign;
                lock (_rand)
                {
                    callsign = $"ENGINE-{_rand.Next(1, 100):D2}";
                }
                Vehicle fire = null;
                Ped driver = null;
                Ped passenger = null;

                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    try
                    {
                        var ubResult = UltimateBackupService.SpawnFireTruckUnit(spawnPos, 2);
                        if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                        {
                            fire = ubResult.Item1;
                            driver = ubResult.Item2[0];
                            if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                        }
                    }
                    catch (Exception ubEx)
                    {
                        Logger.Warn($"[ResponseFeature] Ultimate Backup FireTruck dispatch error: {ubEx.Message}");
                    }
                }

                if (fire == null || !fire.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("ResponseFeature", $"Ultimate Backup did not return fire truck and Fallback is disabled (Fallback=false). Aborting fire truck spawn.");
                        return null;
                    }

                    var nativeResult = UltimateBackupService.SpawnNativeFireUnit(spawnPos, spawnHeading);
                    if (nativeResult != null && nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                    {
                        fire = nativeResult.Item1;
                        driver = nativeResult.Item2[0];
                    }
                }

                if (fire == null || !fire.Exists() || driver == null || !driver.Exists()) return null;

                fire.Heading = spawnHeading;
                WorldUtils.SecureEntityGround(fire);

                if (UnitRegistry.TryClaim(fire, UnitRegistry.OwnerResponse))
                {
                    if (UnitRegistry.TryClaim(driver, UnitRegistry.OwnerResponse))
                    {
                        if (passenger != null && passenger.Exists())
                        {
                            UnitRegistry.TryClaim(passenger, UnitRegistry.OwnerResponse);
                        }

                        ResponseUnitInstance unit = SetupResponseUnit(fire, driver, passenger, destination, true, callsign, EmergencyServiceType.Fire);
                        if (unit != null)
                        {
                            return unit;
                        }

                        if (passenger != null && passenger.Exists())
                        {
                            UnitRegistry.Release(passenger, UnitRegistry.OwnerResponse);
                        }
                        UnitRegistry.Release(driver, UnitRegistry.OwnerResponse);
                    }
                    UnitRegistry.Release(fire, UnitRegistry.OwnerResponse);
                }

                TeardownHelper.SafeDismissPed(driver, delete: true);
                if (passenger != null && passenger.Exists())
                {
                    TeardownHelper.SafeDismissPed(passenger, delete: true);
                }
                TeardownHelper.SafeDismissVehicle(fire, delete: true);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("[ResponseFeature] Failed to spawn fire engine unit", ex);
                return null;
            }
        }

        private static ResponseUnitInstance SetupResponseUnit(Vehicle cruiser, Ped driver, Ped passenger, Vector3 destination, bool isCode3, string callsign, EmergencyServiceType serviceType = EmergencyServiceType.Police, bool isCalloutTakeover = false)
        {
            try
            {
                cruiser.MakePersistent();
                driver.MakePersistent();
                if (passenger != null && passenger.Exists()) passenger.MakePersistent();
                cruiser.IsEngineOn = true;
                NativeTasks.ConfigureEmergencyDriver(driver, cruiser);

                // Siren configuration: Code 3 audible, Code 2 muted siren
                cruiser.IsSirenOn = true;
                cruiser.IsSirenSilent = !isCode3;

                // High speed emergency driving using long-range task
                float targetSpeed = isCode3 ? 32.0f : 24.0f; // ~71 mph vs ~53 mph
                NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(cruiser, 12.0f);
                NativeTasks.TaskLongRangeEmergencyDrive(driver, cruiser, destination, targetSpeed, 15.0f);

                // Attach blip: Callout units get Purple Flashing (Color 27), Ambulance gets EMS, Fire gets Fire, generic police gets emergency blip
                Blip blip;
                if (serviceType == EmergencyServiceType.Police && isCalloutTakeover)
                {
                    blip = BlipService.AttachCalloutResponseBlip(cruiser, callsign);
                }
                else
                {
                    string blipLabel = serviceType == EmergencyServiceType.Ambulance ? $"{callsign} [EMS]" :
                                       serviceType == EmergencyServiceType.Fire ? $"{callsign} [FIRE]" :
                                       callsign;
                    blip = BlipService.AttachEmergencyBlip(cruiser, blipLabel);
                }

                Logger.Debug("ResponseFeature", $"Configured {serviceType} response unit {callsign} [{(isCode3 ? "Code 3" : "Code 2")}] (CalloutTakeover={isCalloutTakeover}): Cruiser={cruiser.Model.Name} (0x{cruiser.Handle:X}), Driver={driver.Model.Name} (0x{driver.Handle:X}), Target={destination}");

                return new ResponseUnitInstance(cruiser, driver, passenger, destination, blip, callsign, isCode3, serviceType);
            }
            catch (Exception ex)
            {
                Logger.Error($"[ResponseFeature] Error setting up response unit {callsign}", ex);
                TeardownHelper.SafeDismissPed(driver, delete: true);
                if (passenger != null && passenger.Exists())
                {
                    TeardownHelper.SafeDismissPed(passenger, delete: true);
                }
                TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                return null;
            }
        }

        private static Vehicle FindIdleAmbientPoliceVehicle(Vector3 playerPos, Vector3 targetPos)
        {
            try
            {
                Entity[] nearbyVehicles = World.GetEntities(playerPos, 320.0f, GetEntitiesFlags.ConsiderAllVehicles);
                if (nearbyVehicles == null || nearbyVehicles.Length == 0) return null;

                foreach (Entity ent in nearbyVehicles)
                {
                    if (ent is Vehicle veh && veh.Exists() && !veh.IsDead && veh.IsDriveable)
                    {
                        Ped drv = veh.Driver;
                        if (drv == null || !drv.Exists() || !drv.IsAlive || drv.IsPlayer || drv == Game.LocalPlayer?.Character)
                        {
                            continue;
                        }

                        bool isPolice = veh.IsPoliceVehicle || veh.Model.IsLawEnforcementVehicle ||
                                        PatrolFeature.IsActiveCruiser(veh) || StationaryFeature.IsActiveCruiser(veh) ||
                                        Functions.IsPedACop(drv) || drv.RelationshipGroup == "COP";
                        if (!isPolice) continue;

                        // Never commandeer cruisers managed by other Cadence features
                        if (BackupFeature.IsBackupCruiser(veh) || PursuitFeature.IsPursuitCruiser(veh) || TrafficStopFeature.IsActiveCruiser(veh) || IsActiveCruiser(veh)) continue;

                        if (UnitRegistry.IsClaimed(veh))
                        {
                            if (!UnitRegistry.IsClaimedBy(veh, UnitRegistry.OwnerPatrol) && !UnitRegistry.IsClaimedBy(veh, UnitRegistry.OwnerStationary))
                            {
                                continue;
                            }
                        }

                        if (UnitRegistry.IsClaimed(drv))
                        {
                            if (!UnitRegistry.IsClaimedBy(drv, UnitRegistry.OwnerPatrol) && !UnitRegistry.IsClaimedBy(drv, UnitRegistry.OwnerStationary))
                            {
                                continue;
                            }
                        }

                        // Ensure not already tracked in active response units
                        lock (_syncLock)
                        {
                            if (_activeUnits.Any(u => u.Cruiser == veh)) continue;
                        }

                        // Ensure not currently in active pursuit or busy with LSPDFR task
                        bool inPursuit = false;
                        try { inPursuit = Functions.IsPedInPursuit(drv); } catch { }
                        if (inPursuit) continue;

                        bool isBusy = false;
                        try { isBusy = Functions.IsCopBusy(drv, false); } catch { }
                        if (isBusy) continue;

                        return veh;
                    }
                }
            }
            catch { }

            return null;
        }

        private static bool TryFindRoadDestination(Vector3 center, float minDistance, float maxDistance, out Vector3 targetPos)
        {
            targetPos = Vector3.Zero;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                float distance;
                double angle;
                lock (_rand)
                {
                    distance = (float)_rand.Next((int)minDistance, (int)maxDistance);
                    angle = _rand.NextDouble() * Math.PI * 2.0;
                }

                Vector3 probe = new Vector3(
                    center.X + (float)Math.Cos(angle) * distance,
                    center.Y + (float)Math.Sin(angle) * distance,
                    center.Z
                );

                if (WorldUtils.GetSafeStreetPosition(probe, 1, out Vector3 streetPos, out float _))
                {
                    if (WorldUtils.IsSubterraneanOrVoid(streetPos)) continue;
                    if (streetPos.DistanceTo(center) < (minDistance - 30f)) continue;

                    targetPos = streetPos;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindRoadSpawnPosition(Vector3 playerPos, Vector3 targetPos, out Vector3 spawnPos, out float spawnHeading)
        {
            spawnPos = Vector3.Zero;
            spawnHeading = 0f;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                float distance;
                double angle;
                lock (_rand)
                {
                    distance = (float)_rand.Next(160, 320);
                    angle = _rand.NextDouble() * Math.PI * 2.0;
                }

                Vector3 probe = new Vector3(
                    playerPos.X + (float)Math.Cos(angle) * distance,
                    playerPos.Y + (float)Math.Sin(angle) * distance,
                    playerPos.Z
                );

                if (WorldUtils.GetSafeStreetPosition(probe, 1, out Vector3 candidatePos, out float candidateHeading))
                {
                    if (WorldUtils.IsSubterraneanOrVoid(candidatePos)) continue;
                    if (candidatePos.DistanceTo(playerPos) < 140f) continue;
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
                Interlocked.Exchange(ref _callsignCounter, 70);
                num = 70;
            }
            return $"{div}-{num:D2}";
        }

        private static void PurgeAllInternal(bool immediateDelete = true)
        {
            List<ResponseUnitInstance> snapshot;
            lock (_syncLock)
            {
                snapshot = new List<ResponseUnitInstance>(_activeUnits);
                _activeUnits.Clear();
            }

            foreach (var unit in snapshot)
            {
                if (unit != null)
                {
                    unit.Dispose(immediateDelete);
                }
            }
            _lastAutonomousRunTime = Game.GameTime + 45000;
        }

        // ====================================================================
        // Response Unit Lifecycle Representation
        // ====================================================================

        private class ResponseUnitInstance
        {
            public Vehicle Cruiser { get; }
            public Ped Driver { get; }
            public Ped Passenger { get; }
            public Vector3 Destination { get; }
            public Blip RadarBlip;
            public string Callsign { get; }
            public bool IsCode3 { get; }
            public EmergencyServiceType ServiceType { get; }
            public uint SpawnTime { get; }

            public enum ResponseState { EnRoute, Arrived, Finished }
            public ResponseState State { get; private set; }

            private uint _arrivedTime;
            private uint _lastNavRefreshTime;

            public bool IsFinished => State == ResponseState.Finished;

            public ResponseUnitInstance(Vehicle cruiser, Ped driver, Ped passenger, Vector3 destination, Blip blip, string callsign, bool isCode3, EmergencyServiceType serviceType = EmergencyServiceType.Police)
            {
                Cruiser = cruiser;
                Driver = driver;
                Passenger = passenger;
                Destination = destination;
                RadarBlip = blip;
                Callsign = callsign;
                IsCode3 = isCode3;
                ServiceType = serviceType;
                SpawnTime = Game.GameTime;
                _lastNavRefreshTime = Game.GameTime;
                State = ResponseState.EnRoute;
            }

            public bool IsValid()
            {
                return Cruiser != null && Cruiser.Exists() && Cruiser.IsDriveable &&
                       Driver != null && Driver.Exists() && Driver.IsAlive;
            }

            public void UpdateState(Vector3 playerPos)
            {
                if (!IsValid())
                {
                    State = ResponseState.Finished;
                    return;
                }

                uint now = Game.GameTime;

                switch (State)
                {
                    case ResponseState.EnRoute:
                        float distToDest = Cruiser.Position.DistanceTo(Destination);
                        bool isAtDestination = distToDest <= 18.0f || (distToDest <= 35.0f && Cruiser.Speed < 1.0f);

                        // Timeout fail-safe (max 100 seconds en-route travel)
                        if (now - SpawnTime > 100000)
                        {
                            isAtDestination = true;
                        }

                        if (isAtDestination)
                        {
                            State = ResponseState.Arrived;
                            _arrivedTime = now;
                            OnArrival();
                        }
                        else
                        {
                            // Navigation refreshing if stuck
                            if (now - _lastNavRefreshTime > 12000)
                            {
                                _lastNavRefreshTime = now;
                                if (Cruiser.Speed < 1.0f && Driver.IsInVehicle(Cruiser, false))
                                {
                                    float speed = IsCode3 ? 30.0f : 22.0f;
                                    NativeTasks.TaskLongRangeEmergencyDrive(Driver, Cruiser, Destination, speed, 15.0f);
                                }
                            }
                        }
                        break;

                    case ResponseState.Arrived:
                        // Remain on scene with muted emergency lights for 18 seconds, then finish
                        if (now - _arrivedTime > 18000)
                        {
                            State = ResponseState.Finished;
                        }
                        break;
                }
            }

            private void OnArrival()
            {
                try
                {
                    // Halt vehicle safely and engage handbrake
                    NativeFunction.Natives.BRING_VEHICLE_TO_HALT(Cruiser, 2.0f, 0, false);
                    NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, true);

                    // Mute siren on arrival, keep flashing lights active on scene
                    try
                    {
                        Cruiser.IsSirenOn = false;
                        Cruiser.IsSirenSilent = true;
                        NativeFunction.Natives.SET_VEHICLE_SIREN(Cruiser, false);
                    }
                    catch { }

                    NativeTasks.SetSilentEmergencyLights(Cruiser, true);

                    // Switch radar blip to standby on scene
                    BlipService.TransitionToStationary(RadarBlip, Callsign);
                }
                catch { }
            }

            public void Relinquish()
            {
                BlipService.SafeDeleteBlip(ref RadarBlip);
                try
                {
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        NativeFunction.Natives.STOP_BRINGING_VEHICLE_TO_HALT(Cruiser);
                        NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                        UnitRegistry.Release(Cruiser, UnitRegistry.OwnerResponse);
                    }
                }
                catch { }

                try
                {
                    if (Driver != null && Driver.Exists())
                    {
                        try
                        {
                            if (Functions.IsPedInPursuit(Driver))
                            {
                                Functions.RemovePedFromPursuit(Driver);
                            }
                        }
                        catch { }
                        Driver.Tasks.Clear();
                        Driver.BlockPermanentEvents = false;
                        UnitRegistry.Release(Driver, UnitRegistry.OwnerResponse);
                    }
                    if (Passenger != null && Passenger.Exists())
                    {
                        try
                        {
                            if (Functions.IsPedInPursuit(Passenger))
                            {
                                Functions.RemovePedFromPursuit(Passenger);
                            }
                        }
                        catch { }
                        Passenger.Tasks.Clear();
                        Passenger.BlockPermanentEvents = false;
                        UnitRegistry.Release(Passenger, UnitRegistry.OwnerResponse);
                    }
                }
                catch { }

                State = ResponseState.Finished;
            }

            public void Dispose(bool immediateDelete)
            {
                // 1. Zero-leak blip destructor
                BlipService.SafeDeleteBlip(ref RadarBlip);

                // 2. Entities clean up
                try
                {
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        try
                        {
                            NativeFunction.Natives.STOP_BRINGING_VEHICLE_TO_HALT(Cruiser);
                            NativeTasks.SetSilentEmergencyLights(Cruiser, false);
                            NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                        }
                        catch { }
                    }

                    if (Passenger != null && Passenger.Exists())
                    {
                        TeardownHelper.SafeDismissPed(Passenger, immediateDelete);
                    }

                    if (Driver != null && Driver.Exists())
                    {
                        if (!immediateDelete && Cruiser != null && Cruiser.Exists())
                        {
                            NativeTasks.ConfigurePatrolDriver(Driver, Cruiser);
                            NativeTasks.ShutOffSirenAndLights(Cruiser);
                            Driver.Tasks.CruiseWithVehicle(Cruiser, 16.0f, NativeTasks.CruisingFlags);
                        }
                        TeardownHelper.SafeDismissPed(Driver, immediateDelete);
                    }

                    if (Cruiser != null && Cruiser.Exists())
                    {
                        TeardownHelper.SafeDismissVehicle(Cruiser, immediateDelete);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[ResponseFeature] Error disposing unit {Callsign}: {ex.Message}");
                }
                finally
                {
                    State = ResponseState.Finished;
                }
            }
        }
    }
}
