using System;
using System.Collections.Generic;
using System.Linq;
using Rage;
using Rage.Native;
using Cadence.Core;
using Cadence.Common;
using Cadence.Services;
using Cadence.Data;

namespace Cadence.Features
{
    /// <summary>
    /// Feature 1: Ambient Driving Patrol
    /// Spawns and manages autonomous police cruisers patrolling the roads of San Andreas.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public static class PatrolFeature
    {
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
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[PatrolFeature] Stopping ambient patrol feature and purging units.");
                PurgeAllInternal();

                try
                {
                    if (_patrolFiber != null && _patrolFiber.IsAlive)
                    {
                        _patrolFiber.Abort();
                    }
                }
                catch { }
                finally
                {
                    _patrolFiber = null;
                }
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
                Vector3 searchTarget = playerPos + player.ForwardVector * 65.0f;

                if (!WorldUtils.GetSafeStreetPosition(searchTarget, 1, out Vector3 streetPos, out float streetHeading))
                {
                    streetPos = World.GetNextPositionOnStreet(searchTarget);
                    streetHeading = forwardHeading;
                }

                if (streetPos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(streetPos))
                {
                    Logger.Warn("[PatrolFeature] Could not find valid street node for test patrol unit.");
                    return false;
                }

                PatrolUnitInstance unit = CreatePatrolUnitAt(streetPos, streetHeading);
                if (unit != null && unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                    }
                    Logger.Info($"[PatrolFeature] Successfully spawned test unit {unit.Callsign} at {streetPos}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[PatrolFeature] SpawnTestUnit encountered an error", ex);
            }

            return false;
        }

        public static void PurgeAll()
        {
            lock (_syncLock)
            {
                Logger.Info("[PatrolFeature] Purging all active patrol units.");
                PurgeAllInternal();
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
        /// Safely deletes radar blip, releases tasks, and drops from internal tracking.
        /// </summary>
        public static bool RelinquishCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;

            lock (_syncLock)
            {
                for (int i = _activeUnits.Count - 1; i >= 0; i--)
                {
                    PatrolUnitInstance unit = _activeUnits[i];
                    if (unit != null && unit.Cruiser == cruiser)
                    {
                        Logger.Info($"[PatrolFeature] Relinquishing cruiser 0x{cruiser.Handle:X} ({unit.Callsign}) to external feature.");
                        unit.Relinquish();
                        _activeUnits.RemoveAt(i);
                        return true;
                    }
                }
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
            lock (_syncLock)
            {
                for (int i = _activeUnits.Count - 1; i >= 0; i--)
                {
                    PatrolUnitInstance unit = _activeUnits[i];
                    if (unit == null)
                    {
                        _activeUnits.RemoveAt(i);
                        continue;
                    }

                    if (!unit.IsValid())
                    {
                        Logger.Debug("PatrolFeature", $"Removing invalid/dead unit {unit.Callsign}. Active remaining: {_activeUnits.Count - 1}/{Settings.MaxPatrolUnits}");
                        unit.Dispose(immediateDelete: false);
                        _activeUnits.RemoveAt(i);
                        continue;
                    }

                    // Subterranean watchdog
                    if (WorldUtils.IsSubterraneanOrVoid(unit.Cruiser.Position))
                    {
                        Logger.Warn("PatrolFeature", $"Unit {unit.Callsign} fell into subterranean void (Z={unit.Cruiser.Position.Z:F1}). Removing. Active remaining: {_activeUnits.Count - 1}");
                        unit.Dispose(immediateDelete: true);
                        _activeUnits.RemoveAt(i);
                        continue;
                    }

                    // Distance watchdog: clean removal if beyond despawn threshold (> 450m)
                    float dist = unit.Cruiser.Position.DistanceTo(playerPos);
                    if (dist > Settings.DespawnDistance)
                    {
                        Logger.Debug("PatrolFeature", $"Unit {unit.Callsign} exceeded despawn distance ({dist:F0}m > {Settings.DespawnDistance:F0}m). Cleaning up. Active remaining: {_activeUnits.Count - 1}");
                        unit.Dispose(immediateDelete: false);
                        _activeUnits.RemoveAt(i);
                        continue;
                    }

                    // Reassert cruising task if driver lost objectives
                    unit.EnsurePatrolDriving();
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
            uint cooldown = Settings.ChaosMode ? 12000u : (uint)_rand.Next(25000, 35001);
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
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                        _lastSpawnTime = Game.GameTime;
                    }
                    SessionMetrics.IncrementPatrol();
                    Logger.Info($"[PatrolFeature] Ambient patrol unit {unit.Callsign} spawned successfully at {spawnPos} (Total: {_activeUnits.Count}/{Settings.MaxPatrolUnits}).");
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
                float angleOffset = (float)((_rand.NextDouble() * 220.0) - 110.0); // Flanking / rear arc
                float targetAngle = (playerHeading + 180f + angleOffset) * (float)(Math.PI / 180.0);
                float distance = minDist + (float)(_rand.NextDouble() * (maxDist - minDist));

                Vector3 probeCenter = new Vector3(
                    playerPos.X - (float)Math.Sin(targetAngle) * distance,
                    playerPos.Y + (float)Math.Cos(targetAngle) * distance,
                    playerPos.Z
                );

                if (WorldUtils.GetSafeStreetPosition(probeCenter, 1, out Vector3 candidatePos, out float candidateHeading))
                {
                    if (WorldUtils.IsSubterraneanOrVoid(candidatePos)) continue;

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

                    if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                    {
                        cruiser = ubResult.Item1;
                        driver = ubResult.Item2.FirstOrDefault(p => p != null && p.Exists());
                        if (ubResult.Item2.Count > 1)
                        {
                            passenger = ubResult.Item2[1];
                        }
                    }
                }

                // Tier 2: Native Fallback Spawner (Strictly blocked if Settings.AllowFallback is false)
                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("PatrolFeature", $"Ultimate Backup did not return a unit at {position}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                        return null;
                    }

                    string vehicleModel = ZoneSpawnData.GetDefaultPoliceVehicleForPosition(position);
                    string copModel = ZoneSpawnData.GetDefaultCopPedModelForPosition(position);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(position, heading, vehicleModel, copModel);

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

                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    Logger.Warn($"[PatrolFeature] Failed to create patrol unit entities at {position}.");
                    return null;
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
                float cruisingSpeed = isHwy
                    ? (18.0f + (float)(_rand.NextDouble() * 3.0))   // Highway: ~40 - 47 mph
                    : (12.0f + (float)(_rand.NextDouble() * 3.0));  // City: ~27 - 33 mph
                driver.Tasks.CruiseWithVehicle(cruiser, cruisingSpeed, NativeTasks.CruisingFlags);

                // Attach Navy Blue radar blip
                blip = BlipService.AttachPatrolBlip(cruiser, callsign);

                Logger.Debug("PatrolFeature", $"Configured patrol unit {callsign}: Cruiser={cruiser.Model.Name} (0x{cruiser.Handle:X}), Driver={driver.Model.Name} (0x{driver.Handle:X}), CruisingSpeed={cruisingSpeed:F1}m/s");

                return new PatrolUnitInstance(cruiser, driver, passenger, blip, callsign);
            }
            catch (Exception ex)
            {
                Logger.Error($"[PatrolFeature] Exception creating patrol unit at {position}", ex);

                if (blip != null) BlipService.SafeDeleteBlip(ref blip);
                if (driver != null && driver.Exists()) driver.Delete();
                if (passenger != null && passenger.Exists()) passenger.Delete();
                if (cruiser != null && cruiser.Exists()) cruiser.Delete();

                return null;
            }
        }

        private static string GenerateCallsign()
        {
            string div = Divisions[_rand.Next(Divisions.Length)];
            int num = _callsignCounter++;
            if (_callsignCounter > 99) _callsignCounter = 10;
            return $"{div}-{num:D2}";
        }

        private static void PurgeAllInternal()
        {
            foreach (var unit in _activeUnits)
            {
                if (unit != null)
                {
                    unit.Dispose(immediateDelete: true);
                }
            }
            _activeUnits.Clear();
            _lastSpawnTime = Game.GameTime + 35000;
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
            public string Callsign { get; }
            public uint SpawnTime { get; }

            private uint _lastTaskVerifyTime;

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

            public void EnsurePatrolDriving()
            {
                if (!IsValid()) return;

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
                try
                {
                    if (Driver != null && Driver.Exists())
                    {
                        Driver.Tasks.Clear();
                        Driver.BlockPermanentEvents = false;
                    }
                    if (Passenger != null && Passenger.Exists())
                    {
                        Passenger.Tasks.Clear();
                        Passenger.BlockPermanentEvents = false;
                    }
                }
                catch { }
            }

            public void Dispose(bool immediateDelete)
            {
                // 1. Zero-leak 4-step destructor protocol for blip
                BlipService.SafeDeleteBlip(ref RadarBlip);

                // 2. Entities clean up
                try
                {
                    if (Passenger != null && Passenger.Exists())
                    {
                        Passenger.BlockPermanentEvents = false;
                        if (immediateDelete) Passenger.Delete();
                        else Passenger.Dismiss();
                    }

                    if (Driver != null && Driver.Exists())
                    {
                        Driver.BlockPermanentEvents = false;
                        if (immediateDelete) Driver.Delete();
                        else Driver.Dismiss();
                    }

                    if (Cruiser != null && Cruiser.Exists())
                    {
                        try
                        {
                            Cruiser.IsSirenOn = false;
                            Cruiser.IsSirenSilent = true;
                            NativeFunction.Natives.SET_VEHICLE_SIREN(Cruiser, false);
                        }
                        catch { }

                        if (immediateDelete) Cruiser.Delete();
                        else Cruiser.Dismiss();
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
