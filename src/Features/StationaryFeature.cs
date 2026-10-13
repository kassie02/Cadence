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
    /// Feature 3: Stationary Units & Speed Traps
    /// Spawns parked cruisers in turnouts, medians, and lots across 5 idle scenarios:
    /// In-Car Radar Watch, Officer Leaning, Coffee Break, Phone Call, and Lidar Observation.
    /// Supports tactical remount to smoothly transition from stationary standby into traffic.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public static class StationaryFeature
    {
        private static readonly object _syncLock = new object();
        private static readonly List<StationaryUnitInstance> _activeUnits = new List<StationaryUnitInstance>();
        private static readonly Random _rand = new Random();

        private static GameFiber _stationaryFiber;
        private static bool _isRunning;
        private static uint _lastSpawnAttemptTime;
        private static int _callsignCounter = 50;

        private static readonly string[] Divisions = new[]
        {
            "1-MARY", "2-GEORGE", "3-EDWARD", "4-FRANK", "5-DAVID", "6-OCEAN", "7-LINCOLN", "8-ADAM"
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
                _lastSpawnAttemptTime = Game.GameTime + 45000;

                Logger.Info("[StationaryFeature] Starting stationary unit background fiber (initial spawn staggered at T+45s).");
                _stationaryFiber = GameFiber.StartNew(StationaryProcessLoop, "Cadence - Stationary Feature Fiber");
            }
        }

        public static void Stop()
        {
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[StationaryFeature] Stopping stationary unit feature and purging units.");
                PurgeAllInternal();

                try
                {
                    if (_stationaryFiber != null && _stationaryFiber.IsAlive)
                    {
                        _stationaryFiber.Abort();
                    }
                }
                catch { }
                finally
                {
                    _stationaryFiber = null;
                }
            }
        }

        public static bool SpawnTestStationary()
        {
            try
            {
                Ped player = Game.LocalPlayer?.Character;
                if (player == null || !player.Exists()) return false;

                Logger.Info("[StationaryFeature] Spawning manual test stationary unit near player.");

                Vector3 playerPos = player.Position;
                Vector3 spotPos = Vector3.Zero;
                float spotHeading = 0f;

                // Attempt to find curated standby location or turnout spot within 400m
                if (!ZoneSpawnData.FindBestStandbyLocation(playerPos, 40.0f, 400.0f, out spotPos, out spotHeading) &&
                    !ZoneSpawnData.FindBestPulloverSpot(playerPos, 40.0f, 400.0f, out spotPos, out spotHeading))
                {
                    // Fallback to roadside turnout/median ahead of player with 3.6m shoulder offset on local streets (never freeways)
                    Vector3 aheadPos = playerPos + player.ForwardVector * 50.0f;
                    if (!WorldUtils.IsHighway(aheadPos) && WorldUtils.GetSafeStreetPosition(aheadPos, 1, out Vector3 stPos, out float stH))
                    {
                        float rad = stH * (float)(Math.PI / 180.0);
                        Vector3 rightDir = new Vector3((float)Math.Cos(rad), (float)Math.Sin(rad), 0f);
                        spotPos = stPos + rightDir * 3.6f;
                        spotHeading = stH;
                    }
                }

                if (spotPos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(spotPos))
                {
                    Logger.Warn("[StationaryFeature] Could not find valid coordinates for test stationary unit.");
                    return false;
                }

                StationaryUnitInstance unit = CreateStationaryUnitAt(spotPos, spotHeading);
                if (unit != null && unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                    }
                    Logger.Info($"[StationaryFeature] Spawned test stationary unit {unit.Callsign} (Scenario {unit.Scenario}) at {spotPos}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[StationaryFeature] SpawnTestStationary encountered an error", ex);
            }

            return false;
        }

        public static void PurgeAll()
        {
            lock (_syncLock)
            {
                Logger.Info("[StationaryFeature] Purging all active stationary units.");
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
        /// Relinquishes control of a stationary cruiser to an external system (e.g. BackupFeature).
        /// Safely detaches props, deletes radar blip, releases handbrake, and drops from internal tracking.
        /// </summary>
        public static bool RelinquishCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;

            lock (_syncLock)
            {
                for (int i = _activeUnits.Count - 1; i >= 0; i--)
                {
                    StationaryUnitInstance unit = _activeUnits[i];
                    if (unit != null && unit.Cruiser == cruiser)
                    {
                        Logger.Info($"[StationaryFeature] Relinquishing cruiser 0x{cruiser.Handle:X} ({unit.Callsign}) to external feature.");
                        unit.Relinquish();
                        _activeUnits.RemoveAt(i);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Mobilizes a standing officer and cruiser for external features.
        /// </summary>
        public static bool MobilizeCruiser(Vehicle cruiser)
        {
            return RelinquishCruiser(cruiser);
        }

        /// <summary>
        /// Retrieves the designated officer assigned to the specified stationary cruiser, if active.
        /// </summary>
        public static Ped GetAssignedOfficer(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return null;
            lock (_syncLock)
            {
                var unit = _activeUnits.FirstOrDefault(u => u != null && u.Cruiser == cruiser);
                if (unit != null && unit.Officer != null && unit.Officer.Exists() && unit.Officer.IsAlive)
                {
                    return unit.Officer;
                }
            }
            return null;
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

        private static void StationaryProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    GameFiber.Sleep(1200);

                    if (!_isRunning || !Settings.EnablePlugin || !Settings.EnableStationary)
                    {
                        continue;
                    }

                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists())
                    {
                        continue;
                    }

                    // 1. Watchdog & State Update
                    UpdateActiveUnits(player.Position);

                    // 2. Ambient Spawning Cycle
                    RunSpawningCycle(player.Position);
                }
                catch (System.Threading.ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[StationaryFeature] Unexpected exception in stationary fiber", ex);
                    GameFiber.Sleep(3000);
                }
            }
        }

        private static void UpdateActiveUnits(Vector3 playerPos)
        {
            lock (_syncLock)
            {
                for (int i = _activeUnits.Count - 1; i >= 0; i--)
                {
                    StationaryUnitInstance unit = _activeUnits[i];
                    if (unit == null)
                    {
                        _activeUnits.RemoveAt(i);
                        continue;
                    }

                    if (!unit.IsValid())
                    {
                        Logger.Debug("StationaryFeature", $"Removing invalid/dead unit {unit.Callsign}. Active remaining: {_activeUnits.Count - 1}/{Settings.MaxStationaryUnits}");
                        unit.Dispose(immediateDelete: false);
                        _activeUnits.RemoveAt(i);
                        continue;
                    }

                    // Despawn distance watchdog (> 450m)
                    float dist = unit.GetDistanceTo(playerPos);
                    if (dist > Settings.DespawnDistance)
                    {
                        Logger.Debug("StationaryFeature", $"Unit {unit.Callsign} out of range ({dist:F0}m > {Settings.DespawnDistance:F0}m). Cleaning up. Active remaining: {_activeUnits.Count - 1}/{Settings.MaxStationaryUnits}");
                        unit.Dispose(immediateDelete: false);
                        _activeUnits.RemoveAt(i);
                        continue;
                    }

                    // Advance scenario behavior or handle remount
                    unit.Update();

                    if (unit.IsCompleted)
                    {
                        unit.Dispose(immediateDelete: false);
                        _activeUnits.RemoveAt(i);
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

            if (currentCount >= Settings.MaxStationaryUnits)
            {
                return;
            }

            // Spawn pacing: check every 30-45 seconds (15s in Chaos mode)
            uint now = Game.GameTime;
            uint cooldown = Settings.ChaosMode ? 15000u : (uint)_rand.Next(30000, 45001);
            if (now < _lastSpawnAttemptTime || now - _lastSpawnAttemptTime < cooldown)
            {
                return;
            }
            _lastSpawnAttemptTime = now;

            // Search for turnout / shoulder / median spot 120m–300m away
            if (TryFindStationaryCoordinate(playerPos, out Vector3 spawnPos, out float spawnHeading))
            {
                bool spotOccupied = false;
                lock (_syncLock)
                {
                    spotOccupied = _activeUnits.Any(u => u != null && u.GetDistanceTo(spawnPos) < 70.0f);
                }

                if (!spotOccupied && WorldUtils.IsAreaClear(spawnPos, 8.0f))
                {
                    StationaryUnitInstance unit = CreateStationaryUnitAt(spawnPos, spawnHeading);
                    if (unit != null && unit.IsValid())
                    {
                        lock (_syncLock)
                        {
                            _activeUnits.Add(unit);
                        }
                        SessionMetrics.IncrementStationary();
                        Logger.Info($"[StationaryFeature] Ambient stationary unit {unit.Callsign} (Scenario {unit.Scenario}) spawned at {spawnPos} (Total: {_activeUnits.Count}/{Settings.MaxStationaryUnits}).");
                    }
                }
            }
        }

        private static bool TryFindStationaryCoordinate(Vector3 playerPos, out Vector3 spawnPos, out float spawnHeading)
        {
            spawnPos = Vector3.Zero;
            spawnHeading = 0f;

            // Priority 1: Check curated urban / station / highway standby locations (LivingCity parity)
            if (ZoneSpawnData.FindBestStandbyLocation(playerPos, 65.0f, 300.0f, out Vector3 standbyPos, out float standbyHeading))
            {
                if (!WorldUtils.IsSubterraneanOrVoid(standbyPos) && WorldUtils.IsAreaClear(standbyPos, 8.0f))
                {
                    spawnPos = standbyPos;
                    spawnHeading = standbyHeading;
                    return true;
                }
            }

            // Priority 2: Check curated turnouts from ZoneSpawnData
            if (ZoneSpawnData.FindBestPulloverSpot(playerPos, 120.0f, 300.0f, out Vector3 curatedPos, out float curatedHeading))
            {
                if (!WorldUtils.IsSubterraneanOrVoid(curatedPos) && WorldUtils.IsAreaClear(curatedPos, 8.0f))
                {
                    spawnPos = curatedPos;
                    spawnHeading = curatedHeading;
                    return true;
                }
            }

            // Priority 3: Dynamic roadside shoulder generation (LivingCity parity - 3.6m curb offset)
            for (int attempt = 0; attempt < 20; attempt++)
            {
                float dist = (float)_rand.Next(75, 250);
                double angle = _rand.NextDouble() * Math.PI * 2.0;

                Vector3 probeCenter = new Vector3(
                    playerPos.X + (float)Math.Cos(angle) * dist,
                    playerPos.Y + (float)Math.Sin(angle) * dist,
                    playerPos.Z
                );

                Vector3 streetPos = World.GetNextPositionOnStreet(probeCenter);
                if (streetPos == Vector3.Zero || streetPos.DistanceTo(playerPos) < 55.0f) continue;
                if (WorldUtils.IsSubterraneanOrVoid(streetPos)) continue;

                // Prohibit dynamic roadside spawns on high-speed freeways / highways!
                // Units on freeways must strictly only use curated off-road turnouts/rest stops.
                if (WorldUtils.IsHighway(streetPos)) continue;

                Vector3 nodePos = Vector3.Zero;
                float heading = 0f;
                bool foundNode = false;
                try
                {
                    foundNode = NativeFunction.Natives.GetClosestVehicleNodeWithHeading<bool>(streetPos.X, streetPos.Y, streetPos.Z, out nodePos, out heading, 1, 3.0f, 0);
                }
                catch
                {
                    foundNode = false;
                }

                if (!foundNode || nodePos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(nodePos)) continue;
                if (WorldUtils.IsHighway(nodePos)) continue;

                // Offset 3.6m to the right shoulder/curb relative to road heading so cruisers park roadside instead of blocking traffic lanes
                float rad = heading * (float)(Math.PI / 180.0);
                Vector3 rightDir = new Vector3((float)Math.Cos(rad), (float)Math.Sin(rad), 0f);
                Vector3 curbPos = nodePos + rightDir * 3.6f;
                Vector3 candidatePos = (curbPos != Vector3.Zero) ? curbPos : nodePos;

                try
                {
                    NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(candidatePos.X, candidatePos.Y, candidatePos.Z);
                    bool hasGround = NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(candidatePos.X, candidatePos.Y, candidatePos.Z + 2.0f, out float gZ, false);
                    if (hasGround && gZ > -50f)
                    {
                        candidatePos = new Vector3(candidatePos.X, candidatePos.Y, gZ + 0.35f);
                    }
                }
                catch { }

                if (WorldUtils.IsSubterraneanOrVoid(candidatePos)) continue;
                if (!WorldUtils.IsAreaClear(candidatePos, 7.0f)) continue;

                spawnPos = candidatePos;
                spawnHeading = heading;
                return true;
            }

            return false;
        }

        private static StationaryUnitInstance CreateStationaryUnitAt(Vector3 position, float heading)
        {
            string callsign = GenerateCallsign();
            Vehicle cruiser = null;
            Ped officer = null;
            Blip blip = null;

            try
            {
                Vector3 spawnPos = position;
                if (WorldUtils.GetAccurateGroundZ(spawnPos, out float groundZ))
                {
                    spawnPos.Z = groundZ + 0.35f;
                }

                // 1. Create Police Cruiser & Officer (UB Tier 1 or Native Fallback Tier 2)
                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    bool isHighway = WorldUtils.ShouldSpawnHighwayUnit(spawnPos);
                    var ubResult = isHighway
                        ? UltimateBackupService.SpawnStatePatrol(spawnPos, 1)
                        : UltimateBackupService.SpawnLocalPatrol(spawnPos, 1);

                    if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                    {
                        cruiser = ubResult.Item1;
                        officer = ubResult.Item2.FirstOrDefault(p => p != null && p.Exists());
                    }
                }

                if (cruiser == null || !cruiser.Exists() || officer == null || !officer.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("StationaryFeature", $"Ultimate Backup did not return a stationary unit at {spawnPos}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                        return null;
                    }

                    string policeModelName = ZoneSpawnData.GetDefaultPoliceVehicleForPosition(spawnPos);
                    string copModelName = ZoneSpawnData.GetDefaultCopPedModelForPosition(spawnPos);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(spawnPos, heading, policeModelName, copModelName);
                    if (nativeResult != null && nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                    {
                        cruiser = nativeResult.Item1;
                        officer = nativeResult.Item2[0];

                        if (Settings.EnableEupOutfits)
                        {
                            EupService.ApplyBodyArmor(officer);
                        }
                    }
                }

                if (cruiser == null || !cruiser.Exists() || officer == null || !officer.Exists())
                {
                    Logger.Warn($"[StationaryFeature] Failed to spawn stationary unit at {spawnPos}.");
                    return null;
                }

                cruiser.Heading = heading;
                cruiser.MakePersistent();
                cruiser.IsEngineOn = false;
                NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(cruiser, true);
                try { cruiser.PlaceOnGroundProperly(); } catch { }
                WorldUtils.SecureEntityGround(cruiser);

                officer.MakePersistent();
                officer.BlockPermanentEvents = true;
                if (officer.CurrentVehicle != cruiser)
                {
                    officer.WarpIntoVehicle(cruiser, -1);
                }

                // 3. Scenario Selection
                // Determine whether unit is directly in or adjacent to active road lanes; if so, keep officer seated inside!
                // LivingCity parity: 6.5m threshold forces InVehicleObservation so officers never dismount into active traffic.
                bool isOnActiveLane = false;
                if (WorldUtils.GetSafeStreetPosition(spawnPos, 1, out Vector3 nearNode, out _))
                {
                    if (spawnPos.DistanceTo(nearNode) < 6.5f)
                    {
                        isOnActiveLane = true;
                    }
                }
                else if (NativeFunction.Natives.GetClosestVehicleNodeWithHeading<bool>(spawnPos.X, spawnPos.Y, spawnPos.Z, out Vector3 nodeCoord, out float nodeHeading, 1, 3.0f, 0))
                {
                    if (spawnPos.DistanceTo(nodeCoord) < 6.5f)
                    {
                        isOnActiveLane = true;
                    }
                }

                int scenario = 0;
                if (isOnActiveLane)
                {
                    scenario = 0; // Seated radar watch (InVehicleObservation)
                }
                else
                {
                    // 0 = In-car radar watch (35%)
                    // 1 = Standing leaning against cruiser (20%)
                    // 2 = Coffee break (15%)
                    // 3 = Phone call (15%)
                    // 4 = Radar gun / lidar (15%)
                    int roll = _rand.Next(100);
                    if (roll < 35) scenario = 0;
                    else if (roll < 55) scenario = 1;
                    else if (roll < 70) scenario = 2;
                    else if (roll < 85) scenario = 3;
                    else scenario = 4;
                }

                // 4. Attach Mint Green Standby Radar Blip
                blip = BlipService.AttachStandbyBlip(cruiser, callsign);

                string scName = scenario == 0 ? "RadarWatch" : scenario == 1 ? "Leaning" : scenario == 2 ? "Coffee" : scenario == 3 ? "Phone" : "SpeedGunLidar";
                Logger.Debug("StationaryFeature", $"Configured stationary unit {callsign} [{scName}]: Cruiser={cruiser.Model.Name} (0x{cruiser.Handle:X}), Officer={officer.Model.Name} (0x{officer.Handle:X}), Pos={spawnPos}");

                return new StationaryUnitInstance(cruiser, officer, blip, callsign, scenario, spawnPos);
            }
            catch (Exception ex)
            {
                Logger.Error($"[StationaryFeature] Exception creating stationary unit at {position}", ex);

                if (blip != null) BlipService.SafeDeleteBlip(ref blip);
                if (officer != null && officer.Exists()) officer.Delete();
                if (cruiser != null && cruiser.Exists()) cruiser.Delete();

                return null;
            }
        }

        private static string GenerateCallsign()
        {
            string div = Divisions[_rand.Next(Divisions.Length)];
            int num = _callsignCounter++;
            if (_callsignCounter > 99) _callsignCounter = 50;
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
            _lastSpawnAttemptTime = Game.GameTime + 35000;
        }

        // ====================================================================
        // Managed Stationary Unit Representation
        // ====================================================================

        private enum StationaryState
        {
            Setup,
            IdleScenario,
            RemountingOfficer,
            EnteringVehicle,
            CruisingAway,
            Completed
        }

        private class StationaryUnitInstance
        {
            public Vehicle Cruiser { get; }
            public Ped Officer { get; }
            public Blip RadarBlip;
            public string Callsign { get; }
            public int Scenario { get; }
            public Vector3 OriginPosition { get; }
            public bool IsCompleted { get; private set; }

            private StationaryState _state;
            private uint _stateStartTime;
            private uint _scenarioDurationMs;
            private Rage.Object _activeProp;

            public StationaryUnitInstance(Vehicle cruiser, Ped officer, Blip blip, string callsign, int scenario, Vector3 origin)
            {
                Cruiser = cruiser;
                Officer = officer;
                RadarBlip = blip;
                Callsign = callsign;
                Scenario = scenario;
                OriginPosition = origin;

                _state = StationaryState.Setup;
                _stateStartTime = Game.GameTime;

                // 60 to 120 seconds duration before organic remount and departure
                _scenarioDurationMs = (uint)_rand.Next(60000, 120000);
            }

            public bool IsValid()
            {
                if (IsCompleted) return false;
                return Cruiser != null && Cruiser.Exists() && Cruiser.IsDriveable &&
                       Officer != null && Officer.Exists() && Officer.IsAlive;
            }

            public float GetDistanceTo(Vector3 pos)
            {
                if (Cruiser != null && Cruiser.Exists())
                {
                    return Cruiser.Position.DistanceTo(pos);
                }
                return OriginPosition.DistanceTo(pos);
            }

            public void Update()
            {
                if (!IsValid()) return;

                uint now = Game.GameTime;
                uint elapsed = now - _stateStartTime;

                switch (_state)
                {
                    case StationaryState.Setup:
                        InitializeScenario();
                        _state = StationaryState.IdleScenario;
                        _stateStartTime = now;
                        break;

                    case StationaryState.IdleScenario:
                        // Check if idle scenario completed naturally
                        if (elapsed >= _scenarioDurationMs)
                        {
                            Mobilize();
                        }
                        break;

                    case StationaryState.RemountingOfficer:
                        if (Officer.IsInVehicle(Cruiser, false) || elapsed >= 8000)
                        {
                            _state = StationaryState.EnteringVehicle;
                            _stateStartTime = now;
                        }
                        else if (elapsed >= 1000 && !Officer.IsInVehicle(Cruiser, false))
                        {
                            Officer.Tasks.EnterVehicle(Cruiser, -1);
                        }
                        break;

                    case StationaryState.EnteringVehicle:
                        // Release handbrake, restore engine, start cruising
                        NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                        Cruiser.IsEngineOn = true;
                        Officer.BlockPermanentEvents = false;

                        if (!Officer.IsInVehicle(Cruiser, false))
                        {
                            Officer.WarpIntoVehicle(Cruiser, -1);
                        }

                        NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(Cruiser, 5.0f);
                        Officer.Tasks.CruiseWithVehicle(Cruiser, 18.0f, NativeTasks.CruisingFlags);

                        // Transition blip to normal patrol radar blip
                        BlipService.TransitionToPatrol(RadarBlip, Callsign);

                        _state = StationaryState.CruisingAway;
                        _stateStartTime = now;
                        break;

                    case StationaryState.CruisingAway:
                        // Dismiss persistence and complete only after unit has cleanly driven away down the street
                        {
                            Ped playerPed = Game.LocalPlayer?.Character;
                            float distToPlayer = (Cruiser != null && Cruiser.Exists() && playerPed != null && playerPed.Exists())
                                ? Cruiser.Position.DistanceTo(playerPed.Position) : 250.0f;

                            if ((elapsed >= 25000 && distToPlayer >= 120.0f) || distToPlayer >= 180.0f)
                            {
                                IsCompleted = true;
                                _state = StationaryState.Completed;
                            }
                        }
                        break;
                }
            }

            private void InitializeScenario()
            {
                switch (Scenario)
                {
                    case 0:
                        // Scenario 0: Seated inside cruiser on radar watch
                        // Officer remains in driver seat; keeps hand on wheel / scanning
                        break;

                    case 1:
                        // Scenario 1: Leaning against cruiser fender
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        GameFiber.StartNew(() =>
                        {
                            GameFiber.Sleep(2000);
                            if (Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                            {
                                Vector3 leanPoint = Cruiser.GetOffsetPosition(new Vector3(-1.1f, -0.2f, 0f));
                                TaskGoTo(Officer, leanPoint);
                                GameFiber.Sleep(2500);
                                if (Officer != null && Officer.Exists() && _state == StationaryState.IdleScenario)
                                {
                                    NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(Officer, "WORLD_HUMAN_COP_IDLES", 0, true);
                                }
                            }
                        });
                        break;

                    case 2:
                        // Scenario 2: Coffee break
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        GameFiber.StartNew(() =>
                        {
                            GameFiber.Sleep(2000);
                            if (Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                            {
                                Vector3 coffeePoint = Cruiser.GetOffsetPosition(new Vector3(-1.3f, 0.5f, 0f));
                                TaskGoTo(Officer, coffeePoint);
                                GameFiber.Sleep(2500);

                                if (Officer != null && Officer.Exists() && _state == StationaryState.IdleScenario)
                                {
                                    EquipCoffeeCupProp();
                                    if (NativeTasks.RequestAnimDict("amb@world_human_drinking@coffee@male@base"))
                                    {
                                        Officer.Tasks.PlayAnimation("amb@world_human_drinking@coffee@male@base", "base", 8.0f, AnimationFlags.Loop);
                                    }
                                }
                            }
                        });
                        break;

                    case 3:
                        // Scenario 3: Phone call
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        GameFiber.StartNew(() =>
                        {
                            GameFiber.Sleep(2000);
                            if (Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                            {
                                Vector3 phonePoint = Cruiser.GetOffsetPosition(new Vector3(1.3f, 0.8f, 0f));
                                TaskGoTo(Officer, phonePoint);
                                GameFiber.Sleep(2500);

                                if (Officer != null && Officer.Exists() && _state == StationaryState.IdleScenario)
                                {
                                    NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(Officer, "WORLD_HUMAN_STAND_MOBILE", 0, true);
                                }
                            }
                        });
                        break;

                    case 4:
                        // Scenario 4: Radar gun / lidar observation at front bumper
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        GameFiber.StartNew(() =>
                        {
                            GameFiber.Sleep(2000);
                            if (Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                            {
                                Vector3 bumperPoint = Cruiser.GetOffsetPosition(new Vector3(-0.9f, 2.5f, 0f));
                                TaskGoTo(Officer, bumperPoint);
                                GameFiber.Sleep(2500);

                                if (Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                                {
                                    Vector3 aimTarget = bumperPoint + Cruiser.ForwardVector * 30.0f;
                                    NativeFunction.Natives.TASK_TURN_PED_TO_FACE_COORD(Officer, aimTarget.X, aimTarget.Y, aimTarget.Z, 1500);
                                    GameFiber.Sleep(1200);

                                    if (Officer != null && Officer.Exists() && _state == StationaryState.IdleScenario)
                                    {
                                        NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(Officer, "WORLD_HUMAN_BINOCULARS", 0, true);
                                    }
                                }
                            }
                        });
                        break;
                }
            }

            public void Mobilize()
            {
                if (_state == StationaryState.RemountingOfficer || _state == StationaryState.EnteringVehicle || _state == StationaryState.CruisingAway || _state == StationaryState.Completed)
                {
                    return;
                }

                // 1. Tactical prop drop & clean holster
                CleanupProp();

                // 2. Clear animations/scenarios
                if (Officer != null && Officer.Exists())
                {
                    Officer.Tasks.Clear();

                    if (!Officer.IsInVehicle(Cruiser, false))
                    {
                        Officer.Tasks.EnterVehicle(Cruiser, -1);
                        _state = StationaryState.RemountingOfficer;
                    }
                    else
                    {
                        _state = StationaryState.EnteringVehicle;
                    }
                }
                else
                {
                    _state = StationaryState.EnteringVehicle;
                }

                _stateStartTime = Game.GameTime;
            }

            private void TaskGoTo(Ped ped, Vector3 destination, float speed = 1.0f)
            {
                if (ped == null || !ped.Exists()) return;
                try
                {
                    ped.Tasks.FollowNavigationMeshToPosition(destination, ped.Heading, speed, 0.5f);
                }
                catch
                {
                    NativeFunction.Natives.TASK_FOLLOW_NAV_MESH_TO_COORD(ped, destination.X, destination.Y, destination.Z, speed, -1, 0.5f, 0, 0f);
                }
            }

            private void EquipCoffeeCupProp()
            {
                CleanupProp();
                try
                {
                    Model cupModel = new Model("ng_proc_coffee_01a");
                    cupModel.LoadAndWait();

                    _activeProp = new Rage.Object(cupModel, Officer.Position);
                    cupModel.Dismiss();

                    if (_activeProp != null && _activeProp.Exists())
                    {
                        // Right hand bone (57005)
                        int handBone = NativeFunction.Natives.GET_PED_BONE_INDEX<int>(Officer, 57005);
                        _activeProp.AttachTo(Officer, handBone, new Vector3(0.125f, -0.06f, -0.063f), new Rotator(-75.02f, 0f, -9f));
                    }
                }
                catch { }
            }

            private void CleanupProp()
            {
                try
                {
                    if (_activeProp != null && _activeProp.Exists())
                    {
                        _activeProp.Detach();
                        _activeProp.Delete();
                    }
                }
                catch { }
                finally
                {
                    _activeProp = null;
                }
            }

            public void Relinquish()
            {
                // 1. Delete blip
                BlipService.SafeDeleteBlip(ref RadarBlip);

                // 2. Detach and delete props
                CleanupProp();

                // 3. Release vehicle handbrake so physics doesn't lock up
                try
                {
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                    }
                }
                catch { }

                // 4. Release officer tasks
                try
                {
                    if (Officer != null && Officer.Exists())
                    {
                        Officer.Tasks.Clear();
                        Officer.BlockPermanentEvents = false;
                    }
                }
                catch { }

                IsCompleted = true;
            }

            public void Dispose(bool immediateDelete)
            {
                // 1. 4-step destructor protocol for blip
                BlipService.SafeDeleteBlip(ref RadarBlip);

                // 2. Clean up props
                CleanupProp();

                // 3. Dismiss or delete entities
                try
                {
                    if (Officer != null && Officer.Exists())
                    {
                        Officer.Tasks.Clear();
                        Officer.BlockPermanentEvents = false;
                        if (immediateDelete) Officer.Delete();
                        else Officer.Dismiss();
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

                        NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                        if (immediateDelete) Cruiser.Delete();
                        else Cruiser.Dismiss();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[StationaryFeature] Error disposing stationary unit {Callsign}: {ex.Message}");
                }

                IsCompleted = true;
            }
        }
    }
}
