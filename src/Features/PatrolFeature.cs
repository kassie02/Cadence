using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Rage;
using Rage.Native;
using Cadence.Core;
using Cadence.Common;
using Cadence.Services;
using Cadence.Data;
using LSPD_First_Response.Mod.API;

namespace Cadence.Features
{
    /// <summary>
    /// Feature 1: Ambient Driving Patrol
    /// Spawns and manages autonomous police cruisers patrolling the roads of San Andreas.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public sealed class PatrolFeature : IFeature
    {
        public static PatrolFeature Instance { get; } = new PatrolFeature();
        public string Name => "Patrol";
        public bool IsRunning => _isRunning;

        void IFeature.Start() => Start();
        void IFeature.Stop() => Stop();
        void IFeature.PurgeAll(bool immediateDelete) => PurgeAll(immediateDelete);
        int IFeature.GetActiveCount() => GetActiveCount();

        private static readonly object _syncLock = new object();
        private static readonly List<PatrolUnitInstance> _activeUnits = new List<PatrolUnitInstance>();
        private static readonly Random _rand = new Random();

        private static GameFiber _patrolFiber;
        private static bool _isRunning;
        private static uint _lastSpawnTime;
        private static int _callsignCounter = 10;

        private static readonly string[] Divisions = new[]
        {
            "1-ADAM", "2-LINCOLN", "3-MARY", "4-GEORGE", "5-EDWARD", "6-FRANK", "7-DAVID", "8-OCEAN"
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
                _lastSpawnTime = Game.GameTime + 15000;

                Logger.Info("[PatrolFeature] Starting ambient patrol background fiber (initial spawn staggered at T+15s).");
                _patrolFiber = GameFiber.StartNew(PatrolProcessLoop, "Cadence - Patrol Feature Fiber");
            }
        }

        public static void Stop()
        {
            GameFiber fiberToJoin = null;
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[PatrolFeature] Stopping ambient patrol feature and purging units.");
                PurgeAllInternal(immediateDelete: false);

                fiberToJoin = _patrolFiber;
                _patrolFiber = null;
            }

            if (fiberToJoin != null && fiberToJoin.IsAlive)
            {
                uint waitStart = Game.GameTime;
                while (fiberToJoin.IsAlive && (Game.GameTime - waitStart < 500))
                {
                    GameFiber.Sleep(25);
                }
                try
                {
                    if (fiberToJoin.IsAlive)
                    {
                        fiberToJoin.Abort();
                    }
                }
                catch { }
            }
        }

        public static bool SpawnTestUnit()
        {
            try
            {
                Ped player = Game.LocalPlayer?.Character;
                if (player == null || !player.Exists()) return false;

                Logger.Info("[PatrolFeature] Spawning manual test patrol unit near player.");

                Vector3 playerPos = player.Position;
                float forwardHeading = player.Heading;

                List<Vector3> occupiedSpots = new List<Vector3>();
                lock (_syncLock)
                {
                    occupiedSpots.AddRange(_activeUnits.Where(u => u != null && u.Cruiser != null && u.Cruiser.Exists()).Select(u => u.Cruiser.Position));
                }

                Vector3 chosenStreetPos = Vector3.Zero;
                float chosenHeading = forwardHeading;

                // Test multi-angle offsets around player (ahead, 35 deg left/right, 65 deg left/right, behind)
                float[] angleOffsets = new float[] { 0f, 35f, -35f, 65f, -65f, 90f, -90f, 180f };
                foreach (float angle in angleOffsets)
                {
                    float rad = (player.Heading + angle) * (float)(Math.PI / 180.0);
                    Vector3 dir = new Vector3(-(float)Math.Sin(rad), (float)Math.Cos(rad), 0f);
                    float testDist;
                    lock (_rand)
                    {
                        testDist = 55.0f + (float)(_rand.NextDouble() * 30.0);
                    }
                    Vector3 probe = playerPos + dir * testDist;

                    if (WorldUtils.GetSafeStreetPosition(probe, 1, out Vector3 stPos, out float stH))
                    {
                        if (stPos != Vector3.Zero && !WorldUtils.IsSubterraneanOrVoid(stPos))
                        {
                            if (!occupiedSpots.Any(occ => occ.DistanceTo(stPos) < 25.0f) && WorldUtils.IsAreaClear(stPos, 9.0f))
                            {
                                chosenStreetPos = stPos;
                                chosenHeading = stH;
                                break;
                            }
                        }
                    }
                }

                if (chosenStreetPos == Vector3.Zero)
                {
                    chosenStreetPos = World.GetNextPositionOnStreet(playerPos + player.ForwardVector * 65.0f);
                    chosenHeading = forwardHeading;
                }

                if (chosenStreetPos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(chosenStreetPos))
                {
                    Logger.Warn("[PatrolFeature] Could not find valid street node for test patrol unit.");
                    return false;
                }

                PatrolUnitInstance unit = CreatePatrolUnitAt(chosenStreetPos, chosenHeading);
                if (unit != null && unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                    }
                    Logger.Info($"[PatrolFeature] Successfully spawned test unit {unit.Callsign} at {chosenStreetPos}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[PatrolFeature] SpawnTestUnit encountered an error", ex);
            }

            return false;
        }

        public static void PurgeAll(bool immediateDelete = true)
        {
            lock (_syncLock)
            {
                Logger.Info($"[PatrolFeature] Purging all active patrol units (immediateDelete={immediateDelete}).");
                PurgeAllInternal(immediateDelete: immediateDelete);
            }
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
        /// Relinquishes control of a patrol cruiser to an external system (e.g. BackupFeature).
        /// Safely deletes radar blip, releases tasks, releases claims, and drops from internal tracking.
        /// </summary>
        public static bool RelinquishCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;

            PatrolUnitInstance unitToRelinquish = null;
            lock (_syncLock)
            {
                unitToRelinquish = _activeUnits.FirstOrDefault(u => u != null && u.Cruiser == cruiser);
                if (unitToRelinquish != null)
                {
                    _activeUnits.Remove(unitToRelinquish);
                }
            }

            if (unitToRelinquish != null)
            {
                Logger.Info($"[PatrolFeature] Relinquishing cruiser 0x{cruiser.Handle:X} ({unitToRelinquish.Callsign}) to external feature.");
                unitToRelinquish.Relinquish();
                if (unitToRelinquish.Driver != null) UnitRegistry.Release(unitToRelinquish.Driver);
                if (unitToRelinquish.Passenger != null) UnitRegistry.Release(unitToRelinquish.Passenger);
                UnitRegistry.Release(cruiser);
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
        // Background Process Fiber
        // ====================================================================

        private static void PatrolProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    GameFiber.Sleep(1500);

                    if (!_isRunning || !Settings.EnablePlugin || !Settings.EnablePatrol)
                    {
                        continue;
                    }

                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists())
                    {
                        continue;
                    }

                    // 1. Despawn & Health Watchdog
                    RunWatchdog(player.Position);

                    // 2. Ambient Spawning Cycle
                    RunSpawningCycle(player.Position);
                }
                catch (System.Threading.ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[PatrolFeature] Unexpected exception in patrol fiber", ex);
                    GameFiber.Sleep(3000);
                }
            }
        }

        private static void RunWatchdog(Vector3 playerPos)
        {
            PatrolUnitInstance[] snapshot;
            lock (_syncLock)
            {
                snapshot = _activeUnits.ToArray();
            }

            var toRemove = new List<PatrolUnitInstance>();

            foreach (var unit in snapshot)
            {
                if (unit == null)
                {
                    continue;
                }

                if (!unit.IsValid())
                {
                    Logger.Debug("PatrolFeature", $"Removing invalid/dead unit {unit.Callsign}.");
                    unit.Dispose(immediateDelete: true);
                    toRemove.Add(unit);
                    continue;
                }

                // Subterranean watchdog
                if (WorldUtils.IsSubterraneanOrVoid(unit.Cruiser.Position))
                {
                    Logger.Warn("PatrolFeature", $"Unit {unit.Callsign} fell into subterranean void (Z={unit.Cruiser.Position.Z:F1}). Removing.");
                    unit.Dispose(immediateDelete: true);
                    toRemove.Add(unit);
                    continue;
                }

                // Distance watchdog: clean removal if beyond despawn threshold (> 450m)
                float dist = unit.Cruiser.Position.DistanceTo(playerPos);
                if (dist > Settings.DespawnDistance)
                {
                    bool isBusy = false;
                    bool inPursuit = false;
                    try
                    {
                        if (unit.Driver != null && unit.Driver.Exists())
                        {
                            isBusy = Functions.IsCopBusy(unit.Driver, false);
                            inPursuit = Functions.IsPedInPursuit(unit.Driver);
                        }
                    }
                    catch { }

                    if (isBusy || inPursuit)
                    {
                        // Officer drafted by LSPDFR: preserve unit, do not despawn
                        continue;
                    }

                    Logger.Debug("PatrolFeature", $"Unit {unit.Callsign} exceeded despawn distance ({dist:F0}m > {Settings.DespawnDistance:F0}m). Cleaning up.");
                    unit.Dispose(immediateDelete: false);
                    toRemove.Add(unit);
                    continue;
                }

                if (UnitRegistry.IsClaimedByOthers(unit.Cruiser, UnitRegistry.OwnerPatrol))
                {
                    toRemove.Add(unit);
                    continue;
                }

                bool isBusyOrPursuit = false;
                try
                {
                    if (unit.Driver != null && unit.Driver.Exists())
                    {
                        isBusyOrPursuit = Functions.IsCopBusy(unit.Driver, false) || Functions.IsPedInPursuit(unit.Driver);
                    }
                }
                catch { }

                if (isBusyOrPursuit)
                {
                    // Unit was commandeered by an LSPDFR callout or pursuit; do NOT stomp tasks, blips, or sirens!
                    continue;
                }

                // Reassert cruising task if driver lost objectives
                unit.EnsurePatrolDriving();

                // Synchronize radar blips (empty white car vs badge foot blips)
                unit.UpdateBlipStates();

                // Enforce siren and lightbar policy to guarantee zero audible siren leakage
                if (unit.Cruiser != null && unit.Cruiser.Exists())
                {
                    bool inPursuit = false;
                    try
                    {
                        if (unit.Driver != null && unit.Driver.Exists())
                        {
                            inPursuit = Functions.IsPedInPursuit(unit.Driver);
                        }
                    }
                    catch { }

                    if (!inPursuit && !PursuitFeature.IsPursuitCruiser(unit.Cruiser))
                    {
                        NativeTasks.EnforceSteadyCruiserSirenPolicy(unit.Cruiser);
                    }
                    else
                    {
                        NativeTasks.EnforceSirenLightbarPolicy(unit.Cruiser);
                    }
                }
            }

            if (toRemove.Count > 0)
            {
                lock (_syncLock)
                {
                    foreach (var unit in toRemove)
                    {
                        _activeUnits.Remove(unit);
                    }
                }
            }
        }

        private static void RunSpawningCycle(Vector3 playerPos)
        {
            int currentCount;
            lock (_syncLock)
            {
                currentCount = _activeUnits.Count;
            }

            if (currentCount >= Settings.MaxPatrolUnits)
            {
                return;
            }

            // Spawn pacing cooldown: 25-35 seconds between ambient spawns (12s in Chaos mode)
            uint now = Game.GameTime;
            uint cooldown;
            lock (_rand)
            {
                cooldown = Settings.ChaosMode ? 12000u : (uint)_rand.Next(25000, 35001);
            }
            if (now < _lastSpawnTime || now - _lastSpawnTime < cooldown)
            {
                return;
            }
            _lastSpawnTime = now;

            // Search for safe road node 180m–350m from player
            if (TryFindPatrolSpawnCoordinate(playerPos, out Vector3 spawnPos, out float spawnHeading))
            {
                PatrolUnitInstance unit = CreatePatrolUnitAt(spawnPos, spawnHeading);
                if (unit != null && unit.IsValid())
                {
                    int unitCount;
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                        _lastSpawnTime = Game.GameTime;
                        unitCount = _activeUnits.Count;
                    }
                    SessionMetrics.IncrementPatrol();
                    Logger.Info($"[PatrolFeature] Ambient patrol unit {unit.Callsign} spawned successfully at {spawnPos} (Total: {unitCount}/{Settings.MaxPatrolUnits}).");
                }
            }
        }

        private static bool TryFindPatrolSpawnCoordinate(Vector3 playerPos, out Vector3 spawnPos, out float spawnHeading)
        {
            spawnPos = Vector3.Zero;
            spawnHeading = 0f;

            Ped player = Game.LocalPlayer?.Character;
            float playerHeading = player != null && player.Exists() ? player.Heading : 0f;

            float minDist = Settings.SpawnDistanceMin;
            float maxDist = Settings.SpawnDistanceMax;

            for (int attempt = 0; attempt < 25; attempt++)
            {
                // Bias spawning behind and flanking the player to avoid direct line-of-sight pop-in
                float angleOffset;
                float distance;
                lock (_rand)
                {
                    angleOffset = (float)((_rand.NextDouble() * 220.0) - 110.0); // Flanking / rear arc
                    distance = minDist + (float)(_rand.NextDouble() * (maxDist - minDist));
                }
                float targetAngle = (playerHeading + 180f + angleOffset) * (float)(Math.PI / 180.0);

                Vector3 probeCenter = new Vector3(
                    playerPos.X - (float)Math.Sin(targetAngle) * distance,
                    playerPos.Y + (float)Math.Cos(targetAngle) * distance,
                    playerPos.Z
                );

                if (WorldUtils.GetSafeStreetPosition(probeCenter, 1, out Vector3 candidatePos, out float candidateHeading))
                {
                    if (WorldUtils.IsSubterraneanOrVoid(candidatePos)) continue;
                    if (WorldUtils.IsPositionInWater(candidatePos)) continue;

                    float distToPlayer = candidatePos.DistanceTo(playerPos);
                    if (distToPlayer < minDist || distToPlayer > (maxDist + 60f)) continue;

                    if (!WorldUtils.IsAreaClear(candidatePos, 7.5f)) continue;

                    if (WorldUtils.GetAccurateGroundZ(candidatePos, out float safeGroundZ))
                    {
                        candidatePos.Z = safeGroundZ + 0.35f;
                    }

                    spawnPos = candidatePos;
                    spawnHeading = candidateHeading;
                    return true;
                }
            }

            return false;
        }

        private static PatrolUnitInstance CreatePatrolUnitAt(Vector3 position, float heading)
        {
            string callsign = GenerateCallsign();
            Vehicle cruiser = null;
            Ped driver = null;
            Ped passenger = null;
            Blip blip = null;

            try
            {
                // Tier 1: Ultimate Backup Integration
                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    bool isHighway = WorldUtils.ShouldSpawnHighwayUnit(position);
                    var ubResult = isHighway
                        ? UltimateBackupService.SpawnStatePatrol(position, 1)
                        : UltimateBackupService.SpawnLocalPatrol(position, 1);

                    if (ubResult != null)
                    {
                        if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                        {
                            cruiser = ubResult.Item1;
                            NativeTasks.ShutOffSirenAndLights(cruiser);
                            driver = ubResult.Item2.FirstOrDefault(p => p != null && p.Exists());
                            if (ubResult.Item2.Count > 1)
                            {
                                passenger = ubResult.Item2[1];
                            }
                        }
                        else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                        {
                            TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                        }
                    }
                }

                // Tier 2: Native Fallback Spawner (Strictly blocked if Settings.AllowFallback is false)
                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("PatrolFeature", $"Ultimate Backup did not return a unit at {position}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                        if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                        return null;
                    }

                    string vehicleModel = ZoneSpawnData.GetDefaultPoliceVehicleForPosition(position);
                    string copModel = ZoneSpawnData.GetDefaultCopPedModelForPosition(position);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(position, heading, vehicleModel, copModel);

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

                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    Logger.Warn($"[PatrolFeature] Failed to create patrol unit entities at {position}.");
                    if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    return null;
                }

                // Register claim in UnitRegistry
                if (!UnitRegistry.TryClaim(cruiser, UnitRegistry.OwnerPatrol))
                {
                    Logger.Debug($"[PatrolFeature] Cruiser 0x{cruiser.Handle:X} could not be claimed in UnitRegistry. Aborting spawn.");
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    if (driver != null) TeardownHelper.SafeDismissPed(driver, delete: true);
                    if (passenger != null) TeardownHelper.SafeDismissPed(passenger, delete: true);
                    return null;
                }
                UnitRegistry.TryClaim(driver, UnitRegistry.OwnerPatrol);
                if (passenger != null && passenger.Exists())
                {
                    UnitRegistry.TryClaim(passenger, UnitRegistry.OwnerPatrol);
                }

                // Ground stabilization & configuration
                cruiser.Heading = heading;
                WorldUtils.SecureEntityGround(cruiser);
                cruiser.MakePersistent();
                driver.MakePersistent();
                if (passenger != null && passenger.Exists())
                {
                    passenger.MakePersistent();
                    passenger.BlockPermanentEvents = true;
                    try { NativeFunction.Natives.TASK_PAUSE(passenger, -1); } catch { }
                }
                cruiser.IsEngineOn = true;
                NativeTasks.ConfigurePatrolDriver(driver, cruiser);

                // Initial motion & authentic patrol cruising AI
                NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(cruiser, 7.0f);
                bool isHwy = WorldUtils.IsHighway(cruiser.Position);
                float cruisingSpeed;
                lock (_rand)
                {
                    cruisingSpeed = isHwy
                        ? (18.0f + (float)(_rand.NextDouble() * 3.0))   // Highway: ~40 - 47 mph
                        : (12.0f + (float)(_rand.NextDouble() * 3.0));  // City: ~27 - 33 mph
                }
                driver.Tasks.CruiseWithVehicle(cruiser, cruisingSpeed, NativeTasks.CruisingFlags);

                // Attach Navy Blue radar blip
                blip = BlipService.SetUnitState(cruiser, EntityBlipState.Patrol, callsign);

                Logger.Debug("PatrolFeature", $"Configured patrol unit {callsign}: Cruiser={cruiser.Model.Name} (0x{cruiser.Handle:X}), Driver={driver.Model.Name} (0x{driver.Handle:X}), CruisingSpeed={cruisingSpeed:F1}m/s");

                return new PatrolUnitInstance(cruiser, driver, passenger, blip, callsign);
            }
            catch (Exception ex)
            {
                Logger.Error($"[PatrolFeature] Exception creating patrol unit at {position}", ex);

                if (blip != null) BlipService.SafeDeleteBlip(ref blip);
                if (driver != null && driver.Exists()) TeardownHelper.SafeDismissPed(driver, delete: true);
                if (passenger != null && passenger.Exists()) TeardownHelper.SafeDismissPed(passenger, delete: true);
                if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);

                return null;
            }
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
                Interlocked.Exchange(ref _callsignCounter, 10);
                num = 10;
            }
            return $"{div}-{num:D2}";
        }

        private static void PurgeAllInternal(bool immediateDelete = true)
        {
            List<PatrolUnitInstance> unitsToDispose;
            lock (_syncLock)
            {
                unitsToDispose = new List<PatrolUnitInstance>(_activeUnits);
                _activeUnits.Clear();
                _lastSpawnTime = 0;
            }

            foreach (var unit in unitsToDispose)
            {
                if (unit != null)
                {
                    unit.Dispose(immediateDelete: immediateDelete);
                }
            }
        }

        // ====================================================================
        // Managed Unit Representation
        // ====================================================================

        private class PatrolUnitInstance
        {
            public Vehicle Cruiser { get; }
            public Ped Driver { get; }
            public Ped Passenger { get; }
            public Blip RadarBlip;
            public Blip DriverBadgeBlip;
            public Blip PassengerBadgeBlip;
            public string Callsign { get; }
            public uint SpawnTime { get; }

            private uint _lastTaskVerifyTime;
            private bool _isParkedBlip;

            public PatrolUnitInstance(Vehicle cruiser, Ped driver, Ped passenger, Blip blip, string callsign)
            {
                Cruiser = cruiser;
                Driver = driver;
                Passenger = passenger;
                RadarBlip = blip;
                Callsign = callsign;
                SpawnTime = Game.GameTime;
                _lastTaskVerifyTime = Game.GameTime;
            }

            public bool IsValid()
            {
                return Cruiser != null && Cruiser.Exists() && Cruiser.IsDriveable &&
                       Driver != null && Driver.Exists() && Driver.IsAlive;
            }

            public void UpdateBlipStates()
            {
                if (!Settings.ShowRadarBlips || Cruiser == null || !Cruiser.Exists()) return;

                try
                {
                    if (UnitRegistry.IsClaimedByOthers(Cruiser, UnitRegistry.OwnerPatrol)) return;
                    if (Driver != null && Driver.Exists() && (Functions.IsCopBusy(Driver, false) || Functions.IsPedInPursuit(Driver))) return;
                    if (Passenger != null && Passenger.Exists() && Functions.IsPedInPursuit(Passenger)) return;
                }
                catch { }

                // Never stomp on active pursuits or LSPDFR pursuits
                if (PursuitFeature.IsPursuitCruiser(Cruiser)) return;

                bool driverOutside = Driver != null && Driver.Exists() && Driver.IsAlive && !Driver.IsInVehicle(Cruiser, false);
                bool passengerOutside = Passenger != null && Passenger.Exists() && Passenger.IsAlive && !Passenger.IsInVehicle(Cruiser, false);
                bool anyOutside = driverOutside || passengerOutside;

                bool allLivingOutside = true;
                if (Driver != null && Driver.Exists() && Driver.IsAlive && Driver.IsInVehicle(Cruiser, false)) allLivingOutside = false;
                if (Passenger != null && Passenger.Exists() && Passenger.IsAlive && Passenger.IsInVehicle(Cruiser, false)) allLivingOutside = false;

                // 1. Driver foot blip
                if (driverOutside)
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
                if (passengerOutside)
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

                // 3. Cruiser blip: turns White [PARKED] when empty / all living officers are outside
                if (allLivingOutside && anyOutside)
                {
                    if (!_isParkedBlip)
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
                        RadarBlip = BlipService.SetUnitState(Cruiser, EntityBlipState.Patrol, Callsign);
                    }
                    // Steady blue cruiser: enforce zero siren and light leakage
                    NativeTasks.EnforceSteadyCruiserSirenPolicy(Cruiser);
                }
            }

            public void EnsurePatrolDriving()
            {
                if (!IsValid()) return;

                bool isBusy = false;
                bool inPursuit = false;
                try
                {
                    isBusy = Functions.IsCopBusy(Driver, false);
                    inPursuit = Functions.IsPedInPursuit(Driver);
                }
                catch { }

                if (isBusy || inPursuit)
                {
                    // Officer drafted by LSPDFR into backup or pursuit: do not override tasks
                    return;
                }

                uint now = Game.GameTime;
                if (now - _lastTaskVerifyTime > 15000)
                {
                    _lastTaskVerifyTime = now;
                    if (Driver.IsInVehicle(Cruiser, false))
                    {
                        var status = Driver.Tasks.CurrentTaskStatus;
                        if (status == TaskStatus.None || status == TaskStatus.Interrupted)
                        {
                            bool isHwy = WorldUtils.IsHighway(Cruiser.Position);
                            float speed = isHwy ? 19.0f : 13.0f;
                            Driver.Tasks.CruiseWithVehicle(Cruiser, speed, NativeTasks.CruisingFlags);
                        }
                    }
                }
            }

            public void Relinquish()
            {
                BlipService.SafeDeleteBlip(ref RadarBlip);
                BlipService.SafeDeleteBlip(ref DriverBadgeBlip);
                BlipService.SafeDeleteBlip(ref PassengerBadgeBlip);
                if (Cruiser != null && Cruiser.Exists())
                {
                    BlipService.SafeDeleteEntityBlip(Cruiser);
                }
                if (Driver != null && Driver.Exists())
                {
                    BlipService.SafeDeleteEntityBlip(Driver);
                }
                if (Passenger != null && Passenger.Exists())
                {
                    BlipService.SafeDeleteEntityBlip(Passenger);
                }
                try
                {
                    if (Driver != null && Driver.Exists())
                    {
                        try { if (Functions.IsPedInPursuit(Driver)) Functions.RemovePedFromPursuit(Driver); } catch { }
                        Driver.Tasks.Clear();
                        Driver.BlockPermanentEvents = false;
                    }
                }
                catch { }

                try
                {
                    if (Passenger != null && Passenger.Exists())
                    {
                        try { if (Functions.IsPedInPursuit(Passenger)) Functions.RemovePedFromPursuit(Passenger); } catch { }
                        Passenger.Tasks.Clear();
                        Passenger.BlockPermanentEvents = false;
                    }
                }
                catch { }

                if (Driver != null) UnitRegistry.Release(Driver);
                if (Passenger != null) UnitRegistry.Release(Passenger);
                if (Cruiser != null) UnitRegistry.Release(Cruiser);
            }

            public void Dispose(bool immediateDelete)
            {
                // 1. Zero-leak 4-step destructor protocol for blips
                BlipService.SafeDeleteBlip(ref RadarBlip);
                BlipService.SafeDeleteBlip(ref DriverBadgeBlip);
                BlipService.SafeDeleteBlip(ref PassengerBadgeBlip);
                BlipService.SafeDeleteEntityBlip(Cruiser);
                BlipService.SafeDeleteEntityBlip(Driver);
                BlipService.SafeDeleteEntityBlip(Passenger);

                // 2. Safe entity dismissals via TeardownHelper
                try
                {
                    if (Passenger != null)
                    {
                        TeardownHelper.SafeDismissPed(Passenger, delete: immediateDelete);
                    }

                    if (Driver != null)
                    {
                        TeardownHelper.SafeDismissPed(Driver, delete: immediateDelete);
                    }

                    if (Cruiser != null)
                    {
                        bool hasLivingDriver = (Driver != null && Driver.Exists() && Driver.IsAlive) ||
                                               (Passenger != null && Passenger.Exists() && Passenger.IsAlive);
                        bool shouldDeleteCruiser = immediateDelete || !hasLivingDriver;
                        NativeTasks.ShutOffSirenAndLights(Cruiser);
                        TeardownHelper.SafeDismissVehicle(Cruiser, delete: shouldDeleteCruiser);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[PatrolFeature] Error disposing unit {Callsign}: {ex.Message}");
                }
            }
        }
    }
}
