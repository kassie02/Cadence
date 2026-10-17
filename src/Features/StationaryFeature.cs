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
    /// Feature 3: Stationary Units & Speed Traps
    /// Spawns parked cruisers in turnouts, medians, and lots across 5 idle scenarios:
    /// In-Car Radar Watch, Officer Leaning, Coffee Break, Phone Call, and Lidar Observation.
    /// Supports tactical remount to smoothly transition from stationary standby into traffic.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public sealed class StationaryFeature : IFeature
    {
        public static StationaryFeature Instance { get; } = new StationaryFeature();
        public string Name => "Stationary";
        public bool IsRunning => _isRunning;

        void IFeature.Start() => Start();
        void IFeature.Stop() => Stop();
        void IFeature.PurgeAll(bool immediateDelete) => PurgeAll(immediateDelete);
        int IFeature.GetActiveCount() => GetActiveCount();

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
            }

            Logger.Info("[StationaryFeature] Stopping stationary unit feature and purging units.");
            PurgeAllInternal(immediateDelete: false);

            try
            {
                if (_stationaryFiber != null && _stationaryFiber.IsAlive)
                {
                    uint startWait = Game.GameTime;
                    while (_stationaryFiber.IsAlive && (Game.GameTime - startWait < 1500))
                    {
                        GameFiber.Sleep(50);
                    }
                    if (_stationaryFiber.IsAlive)
                    {
                        _stationaryFiber.Abort();
                    }
                }
            }
            catch { }
            finally
            {
                _stationaryFiber = null;
            }
        }

        public static bool SpawnTestRadarUnit() => SpawnTestStationaryInternal(forcedRadar: true);
        public static bool SpawnTestChillingUnit() => SpawnTestStationaryInternal(forcedChilling: true);
        public static bool SpawnTestStationary() => SpawnTestStationaryInternal();

        private static bool SpawnTestStationaryInternal(bool forcedRadar = false, bool forcedChilling = false)
        {
            try
            {
                Ped player = Game.LocalPlayer?.Character;
                if (player == null || !player.Exists()) return false;

                string unitTypeDesc = forcedRadar ? "radar speed trap" : (forcedChilling ? "chilling patrol" : "stationary");
                Logger.Info($"[StationaryFeature] Spawning manual test {unitTypeDesc} unit near player.");

                Vector3 playerPos = player.Position;
                Vector3 spotPos = Vector3.Zero;
                float spotHeading = 0f;

                List<Vector3> occupiedSpots = new List<Vector3>();
                lock (_syncLock)
                {
                    occupiedSpots.AddRange(_activeUnits.Where(u => u != null && u.Cruiser != null && u.Cruiser.Exists()).Select(u => u.Cruiser.Position));
                }

                float maxSearch = Math.Min(250.0f, Math.Max(60.0f, Settings.DespawnDistance - 150.0f));

                // Find candidate spots that are NOT within 35m of an occupied spot
                var candidateSpots = new List<(Vector3 pos, float heading)>();

                // Pass 1: Strict range (30m to maxSearch) across Standby, Commercial, and Roadside Pullover zones
                CollectStationaryTestCandidates(playerPos, 30.0f, maxSearch, occupiedSpots, candidateSpots);

                // Pass 2: Progressive expansion if player is in open/rural area (up to 450m)
                if (candidateSpots.Count == 0)
                {
                    CollectStationaryTestCandidates(playerPos, maxSearch, 450.0f, occupiedSpots, candidateSpots);
                }

                if (candidateSpots.Count > 0)
                {
                    // Pick a random available candidate to avoid always picking the exact same spot
                    (Vector3 pos, float heading) chosen;
                    lock (_rand)
                    {
                        chosen = candidateSpots[_rand.Next(candidateSpots.Count)];
                    }
                    spotPos = chosen.pos;
                    spotHeading = chosen.heading;
                }
                else
                {
                    // Fallback to safe off-road / turnout detection (nodeType = 3 or road boundary)
                    // NEVER spawn directly on road asphalt or centerline
                    float[] testAngles = new float[] { 0f, 45f, -45f, 90f, -90f, 180f };
                    foreach (float angle in testAngles)
                    {
                        float rad = (player.Heading + angle) * (float)(Math.PI / 180.0);
                        Vector3 dir = new Vector3(-(float)Math.Sin(rad), (float)Math.Cos(rad), 0f);
                        double extraDist;
                        lock (_rand)
                        {
                            extraDist = _rand.NextDouble() * 35.0;
                        }
                        Vector3 probe = playerPos + dir * (50.0f + (float)extraDist);

                        if (WorldUtils.GetSafeOffroadPosition(probe, 25.0f, 120.0f, out Vector3 offPos, out float offHeading))
                        {
                            if (!WorldUtils.IsHighway(offPos) && !WorldUtils.IsSubterraneanOrVoid(offPos) && !WorldUtils.IsRooftopOrElevatedStructure(offPos) && !WorldUtils.IsOnActiveRoadOrLane(offPos))
                            {
                                if (!occupiedSpots.Any(occ => occ.DistanceTo(offPos) < 35.0f) && WorldUtils.IsAreaClear(offPos, 8.0f))
                                {
                                    spotPos = offPos;
                                    spotHeading = offHeading;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (spotPos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(spotPos) || WorldUtils.IsRooftopOrElevatedStructure(spotPos))
                {
                    Logger.Warn("[StationaryFeature] Could not find unoccupied valid coordinates for test stationary unit.");
                    return false;
                }

                int forcedScen = -1;
                if (forcedRadar) forcedScen = 0;
                else if (forcedChilling)
                {
                    lock (_rand)
                    {
                        forcedScen = _rand.Next(1, 4); // 1 = Leaning, 2 = Coffee, 3 = Phone
                    }
                }

                StationaryUnitInstance unit = CreateStationaryUnitAt(spotPos, spotHeading, forcedScen);
                if (unit != null && unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                    }
                    Logger.Info($"[StationaryFeature] Spawned test {unitTypeDesc} unit {unit.Callsign} (Scenario {unit.Scenario}) at {spotPos}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[StationaryFeature] SpawnTestStationary encountered an error", ex);
            }

            return false;
        }

        private static void CollectStationaryTestCandidates(Vector3 playerPos, float minDistance, float maxDistance, List<Vector3> occupiedSpots, List<(Vector3 pos, float heading)> candidates)
        {
            foreach (var spot in ZoneSpawnData.CuratedStandbyLocations)
            {
                float dist = spot.pos.DistanceTo(playerPos);
                if (dist >= minDistance && dist <= maxDistance && !WorldUtils.IsSubterraneanOrVoid(spot.pos) && !WorldUtils.IsRooftopOrElevatedStructure(spot.pos) && WorldUtils.IsAreaClear(spot.pos, 9.0f))
                {
                    if (!occupiedSpots.Any(occ => occ.DistanceTo(spot.pos) < 35.0f))
                    {
                        candidates.Add(spot);
                    }
                }
            }

            foreach (var zone in ZoneSpawnData.GetStationaryStandbyZones())
            {
                foreach (var spot in zone)
                {
                    float dist = spot.pos.DistanceTo(playerPos);
                    if (dist >= minDistance && dist <= maxDistance && !WorldUtils.IsSubterraneanOrVoid(spot.pos) && !WorldUtils.IsRooftopOrElevatedStructure(spot.pos) && WorldUtils.IsAreaClear(spot.pos, 9.0f))
                    {
                        if (!occupiedSpots.Any(occ => occ.DistanceTo(spot.pos) < 35.0f))
                        {
                            candidates.Add(spot);
                        }
                    }
                }
            }

            foreach (var zone in ZoneSpawnData.GetRoadsidePulloverZones())
            {
                foreach (var spot in zone)
                {
                    float dist = spot.pos.DistanceTo(playerPos);
                    if (dist >= minDistance && dist <= maxDistance && !WorldUtils.IsSubterraneanOrVoid(spot.pos) && !WorldUtils.IsRooftopOrElevatedStructure(spot.pos) && WorldUtils.IsAreaClear(spot.pos, 9.0f))
                    {
                        if (!occupiedSpots.Any(occ => occ.DistanceTo(spot.pos) < 35.0f))
                        {
                            candidates.Add(spot);
                        }
                    }
                }
            }
        }

        public static void PurgeAll(bool immediateDelete = true)
        {
            Logger.Info("[StationaryFeature] Purging all active stationary units.");
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
        /// Relinquishes control of a stationary cruiser to an external system (e.g. BackupFeature).
        /// Safely detaches props, deletes radar blip, releases handbrake, and drops from internal tracking.
        /// </summary>
        public static bool RelinquishCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;

            StationaryUnitInstance unitToRelinquish = null;
            lock (_syncLock)
            {
                for (int i = _activeUnits.Count - 1; i >= 0; i--)
                {
                    StationaryUnitInstance unit = _activeUnits[i];
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
                Logger.Info($"[StationaryFeature] Relinquishing cruiser 0x{cruiser.Handle:X} ({unitToRelinquish.Callsign}) to external feature.");
                unitToRelinquish.Relinquish();
                return true;
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
            StationaryUnitInstance[] snapshot;
            lock (_syncLock)
            {
                snapshot = _activeUnits.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                StationaryUnitInstance unit = snapshot[i];
                if (unit == null) continue;

                if (!unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        if (!_activeUnits.Contains(unit)) continue;
                        _activeUnits.Remove(unit);
                        Logger.Debug("StationaryFeature", $"Removing invalid/dead unit {unit.Callsign}. Active remaining: {_activeUnits.Count}/{Settings.MaxStationaryUnits}");
                    }
                    unit.Dispose(immediateDelete: false);
                    continue;
                }

                // Despawn distance watchdog (> 450m) with 45s grace period for newly created units
                float dist = unit.GetDistanceTo(playerPos);
                uint age = Game.GameTime - unit.CreatedTime;
                if (dist > Settings.DespawnDistance && age > 45000)
                {
                    bool isBusy = false;
                    bool inPursuit = false;
                    try
                    {
                        if (unit.Officer != null && unit.Officer.Exists())
                        {
                            isBusy = Functions.IsCopBusy(unit.Officer, false);
                            inPursuit = Functions.IsPedInPursuit(unit.Officer);
                        }
                    }
                    catch { }

                    if (isBusy || inPursuit)
                    {
                        // Officer drafted by LSPDFR: preserve unit, do not despawn
                        continue;
                    }

                    lock (_syncLock)
                    {
                        if (!_activeUnits.Contains(unit)) continue;
                        _activeUnits.Remove(unit);
                        Logger.Debug("StationaryFeature", $"Unit {unit.Callsign} out of range ({dist:F0}m > {Settings.DespawnDistance:F0}m, age: {age / 1000}s). Cleaning up. Active remaining: {_activeUnits.Count}/{Settings.MaxStationaryUnits}");
                    }
                    unit.Dispose(immediateDelete: false);
                    continue;
                }

                // Advance scenario behavior or handle remount
                unit.Update();

                if (unit.IsCompleted)
                {
                    lock (_syncLock)
                    {
                        if (!_activeUnits.Contains(unit)) continue;
                        _activeUnits.Remove(unit);
                    }
                    unit.Dispose(immediateDelete: false);
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
            uint cooldown;
            lock (_rand)
            {
                cooldown = Settings.ChaosMode ? 15000u : (uint)_rand.Next(30000, 45001);
            }
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
                        int unitCount;
                        lock (_syncLock)
                        {
                            _activeUnits.Add(unit);
                            unitCount = _activeUnits.Count;
                        }
                        SessionMetrics.IncrementStationary();
                        Logger.Info($"[StationaryFeature] Ambient stationary unit {unit.Callsign} (Scenario {unit.Scenario}) spawned at {spawnPos} (Total: {unitCount}/{Settings.MaxStationaryUnits}).");
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
                if (!WorldUtils.IsSubterraneanOrVoid(standbyPos) && !WorldUtils.IsRooftopOrElevatedStructure(standbyPos) && !WorldUtils.IsPositionInWater(standbyPos) && WorldUtils.IsAreaClear(standbyPos, 8.0f))
                {
                    spawnPos = standbyPos;
                    spawnHeading = standbyHeading;
                    return true;
                }
            }

            // Priority 2: Check curated turnouts from ZoneSpawnData
            if (ZoneSpawnData.FindBestPulloverSpot(playerPos, 120.0f, 300.0f, out Vector3 curatedPos, out float curatedHeading))
            {
                if (!WorldUtils.IsSubterraneanOrVoid(curatedPos) && !WorldUtils.IsRooftopOrElevatedStructure(curatedPos) && !WorldUtils.IsPositionInWater(curatedPos) && WorldUtils.IsAreaClear(curatedPos, 8.0f))
                {
                    spawnPos = curatedPos;
                    spawnHeading = curatedHeading;
                    return true;
                }
            }

            // Priority 3: Dynamic safe offroad / shoulder generation (strictly prohibited on freeways/highways)
            for (int attempt = 0; attempt < 20; attempt++)
            {
                float dist;
                double angle;
                lock (_rand)
                {
                    dist = (float)_rand.Next(75, 250);
                    angle = _rand.NextDouble() * Math.PI * 2.0;
                }

                Vector3 probeCenter = new Vector3(
                    playerPos.X + (float)Math.Cos(angle) * dist,
                    playerPos.Y + (float)Math.Sin(angle) * dist,
                    playerPos.Z
                );

                if (WorldUtils.GetSafeOffroadPosition(probeCenter, 30.0f, 150.0f, out Vector3 offroadPos, out float offroadH))
                {
                    if (!WorldUtils.IsHighway(offroadPos) && !WorldUtils.IsSubterraneanOrVoid(offroadPos) && !WorldUtils.IsRooftopOrElevatedStructure(offroadPos) && !WorldUtils.IsOnActiveRoadOrLane(offroadPos) && !WorldUtils.IsPositionInWater(offroadPos) && WorldUtils.IsAreaClear(offroadPos, 7.5f))
                    {
                        spawnPos = offroadPos;
                        spawnHeading = offroadH;
                        return true;
                    }
                }
            }

            return false;
        }

        private static StationaryUnitInstance CreateStationaryUnitAt(Vector3 position, float heading, int forcedScenario = -1)
        {
            string callsign = GenerateCallsign();
            Vehicle cruiser = null;
            Ped officer = null;
            Blip blip = null;

            try
            {
                if (WorldUtils.IsRooftopOrElevatedStructure(position))
                {
                    Logger.Debug("StationaryFeature", $"Aborting stationary unit at {position}: candidate position is on rooftop/elevated structure.");
                    return null;
                }

                Vector3 spawnPos = position;
                if (WorldUtils.GetAccurateGroundZ(spawnPos, out float groundZ))
                {
                    if (Math.Abs(spawnPos.Z - groundZ) <= 3.0f)
                    {
                        spawnPos.Z = groundZ + 0.35f;
                    }
                }

                if (WorldUtils.IsRooftopOrElevatedStructure(spawnPos))
                {
                    Logger.Debug("StationaryFeature", $"Aborting stationary unit at {spawnPos}: adjusted position is on rooftop/elevated structure.");
                    return null;
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
                        if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                        if (officer != null && officer.Exists()) TeardownHelper.SafeDismissPed(officer, delete: true);
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
                    if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    if (officer != null && officer.Exists()) TeardownHelper.SafeDismissPed(officer, delete: true);
                    Logger.Warn($"[StationaryFeature] Failed to spawn stationary unit at {spawnPos}.");
                    return null;
                }

                if (!UnitRegistry.TryClaim(cruiser, UnitRegistry.OwnerStationary))
                {
                    TeardownHelper.SafeDismissPed(officer, delete: true);
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    return null;
                }

                if (!UnitRegistry.TryClaim(officer, UnitRegistry.OwnerStationary))
                {
                    UnitRegistry.Release(cruiser);
                    TeardownHelper.SafeDismissPed(officer, delete: true);
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
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
                bool isOnActiveLane = WorldUtils.IsOnActiveRoadOrLane(spawnPos, 5.5f);
                if (!isOnActiveLane)
                {
                    if (WorldUtils.GetSafeStreetPosition(spawnPos, 1, out Vector3 nearNode, out _))
                    {
                        if (spawnPos.DistanceTo(nearNode) < 5.5f)
                        {
                            isOnActiveLane = true;
                        }
                    }
                }

                int scenario = 0;
                if (isOnActiveLane)
                {
                    // Officer must never dismount into active traffic lanes
                    scenario = 0; // Seated radar watch (InVehicleObservation)
                }
                else if (forcedScenario >= 0)
                {
                    scenario = forcedScenario;
                }
                else
                {
                    // 0 = In-car radar watch (35%)
                    // 1 = Standing leaning against cruiser (20%)
                    // 2 = Coffee break (15%)
                    // 3 = Phone call (15%)
                    // 4 = Radar gun / lidar (15%)
                    int roll;
                    lock (_rand)
                    {
                        roll = _rand.Next(100);
                    }
                    if (roll < 35) scenario = 0;
                    else if (roll < 55) scenario = 1;
                    else if (roll < 70) scenario = 2;
                    else if (roll < 85) scenario = 3;
                    else scenario = 4;
                }

                // 4. Attach Mint Green Radar or Chilling Patrol Blip
                if (scenario == 0 || scenario == 4)
                {
                    blip = BlipService.AttachRadarWatchBlip(cruiser, callsign);
                }
                else
                {
                    blip = BlipService.AttachChillingBlip(cruiser, callsign);
                }

                string scName = scenario == 0 ? "RadarWatch" : scenario == 1 ? "Leaning" : scenario == 2 ? "Coffee" : scenario == 3 ? "Phone" : "SpeedGunLidar";
                Logger.Debug("StationaryFeature", $"Configured stationary unit {callsign} [{scName}]: Cruiser={cruiser.Model.Name} (0x{cruiser.Handle:X}), Officer={officer.Model.Name} (0x{officer.Handle:X}), Pos={spawnPos}");

                return new StationaryUnitInstance(cruiser, officer, blip, callsign, scenario, spawnPos);
            }
            catch (Exception ex)
            {
                Logger.Error($"[StationaryFeature] Exception creating stationary unit at {position}", ex);

                if (blip != null) BlipService.SafeDeleteBlip(ref blip);
                if (officer != null && officer.Exists()) TeardownHelper.SafeDismissPed(officer, delete: true);
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
                Interlocked.Exchange(ref _callsignCounter, 50);
                num = 50;
            }
            return $"{div}-{num:D2}";
        }

        private static void PurgeAllInternal(bool immediateDelete = true)
        {
            StationaryUnitInstance[] snapshot;
            lock (_syncLock)
            {
                snapshot = _activeUnits.ToArray();
                _activeUnits.Clear();
                _lastSpawnAttemptTime = 0;
            }

            foreach (var unit in snapshot)
            {
                if (unit != null)
                {
                    unit.Dispose(immediateDelete);
                }
            }
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
            public Blip OfficerFootBlip;
            public string Callsign { get; }
            public int Scenario { get; }
            public Vector3 OriginPosition { get; }
            public bool IsCompleted { get; private set; }
            public uint CreatedTime { get; } = Game.GameTime;

            private StationaryState _state;
            private uint _stateStartTime;
            private uint _scenarioDurationMs;
            private Rage.Object _activeProp;
            private GameFiber _scenarioFiber;
            private bool _isDisposed;

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
                lock (_rand)
                {
                    _scenarioDurationMs = (uint)_rand.Next(60000, 120000);
                }
            }

            public bool IsValid()
            {
                if (IsCompleted || _isDisposed) return false;
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

                // Enforce steady cruiser siren policy so standby units never have sirens sounding
                if (Cruiser != null && Cruiser.Exists())
                {
                    NativeTasks.EnforceSteadyCruiserSirenPolicy(Cruiser);
                }

                uint now = Game.GameTime;

                // Absolute station-keeping timeout watchdog: force mobilize after 5m, complete after 6m
                if (now - CreatedTime > 300000 && _state == StationaryState.IdleScenario)
                {
                    Mobilize();
                }
                if (now - CreatedTime > 360000)
                {
                    IsCompleted = true;
                    _state = StationaryState.Completed;
                    return;
                }

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

                        // Transition blip to normal patrol radar blip and clean up officer foot blip
                        BlipService.HandleOfficerRemount(Cruiser, Officer, ref RadarBlip, ref OfficerFootBlip, Callsign, "PATROL", Settings.PatrolBlipColor);
                        UnitRegistry.Release(Cruiser);
                        UnitRegistry.Release(Officer);

                        _state = StationaryState.CruisingAway;
                        _stateStartTime = now;
                        break;

                    case StationaryState.CruisingAway:
                        // Cap max duration in CruisingAway to 35s
                        if (elapsed >= 35000)
                        {
                            IsCompleted = true;
                            _state = StationaryState.Completed;
                            break;
                        }

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

            private void AbortScenarioFiber()
            {
                try
                {
                    if (_scenarioFiber != null && _scenarioFiber.IsAlive)
                    {
                        _scenarioFiber.Abort();
                    }
                }
                catch { }
                finally
                {
                    _scenarioFiber = null;
                }
            }

            private void InitializeScenario()
            {
                AbortScenarioFiber();

                switch (Scenario)
                {
                    case 0:
                        // Scenario 0: Seated inside cruiser on radar watch
                        // Officer remains in driver seat; keeps hand on wheel / scanning
                        break;

                    case 1:
                        // Scenario 1: Leaning against cruiser passenger fender (off-road / curb side)
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        BlipService.HandleOfficerDismount(Cruiser, Officer, ref RadarBlip, ref OfficerFootBlip, Callsign);
                        _scenarioFiber = GameFiber.StartNew(() =>
                        {
                            GameFiber.Sleep(2000);
                            if (!_isDisposed && Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                            {
                                Vector3 leanPoint = Cruiser.GetOffsetPosition(new Vector3(1.15f, -0.2f, 0f));
                                TaskGoTo(Officer, leanPoint);
                                GameFiber.Sleep(2500);
                                if (!_isDisposed && Officer != null && Officer.Exists() && _state == StationaryState.IdleScenario)
                                {
                                    NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(Officer, "WORLD_HUMAN_COP_IDLES", 0, true);
                                }
                            }
                        }, "Cadence - Stationary Scenario Fiber");
                        break;

                    case 2:
                        // Scenario 2: Coffee break on curb / passenger side
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        BlipService.HandleOfficerDismount(Cruiser, Officer, ref RadarBlip, ref OfficerFootBlip, Callsign);
                        _scenarioFiber = GameFiber.StartNew(() =>
                        {
                            GameFiber.Sleep(2000);
                            if (!_isDisposed && Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                            {
                                Vector3 coffeePoint = Cruiser.GetOffsetPosition(new Vector3(1.25f, 0.5f, 0f));
                                TaskGoTo(Officer, coffeePoint);
                                GameFiber.Sleep(2500);

                                if (!_isDisposed && Officer != null && Officer.Exists() && _state == StationaryState.IdleScenario)
                                {
                                    EquipCoffeeCupProp();
                                    if (NativeTasks.RequestAnimDict("amb@world_human_drinking@coffee@male@base"))
                                    {
                                        Officer.Tasks.PlayAnimation("amb@world_human_drinking@coffee@male@base", "base", 8.0f, AnimationFlags.Loop);
                                    }
                                }
                            }
                        }, "Cadence - Stationary Scenario Fiber");
                        break;

                    case 3:
                        // Scenario 3: Phone call on passenger side / curb
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        BlipService.HandleOfficerDismount(Cruiser, Officer, ref RadarBlip, ref OfficerFootBlip, Callsign);
                        _scenarioFiber = GameFiber.StartNew(() =>
                        {
                            GameFiber.Sleep(2000);
                            if (!_isDisposed && Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                            {
                                Vector3 phonePoint = Cruiser.GetOffsetPosition(new Vector3(1.25f, 0.8f, 0f));
                                TaskGoTo(Officer, phonePoint);
                                GameFiber.Sleep(2500);

                                if (!_isDisposed && Officer != null && Officer.Exists() && _state == StationaryState.IdleScenario)
                                {
                                    NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(Officer, "WORLD_HUMAN_STAND_MOBILE", 0, true);
                                }
                            }
                        }, "Cadence - Stationary Scenario Fiber");
                        break;

                    case 4:
                        // Scenario 4: Radar gun / lidar observation at passenger front bumper corner
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        BlipService.HandleOfficerDismount(Cruiser, Officer, ref RadarBlip, ref OfficerFootBlip, Callsign);
                        _scenarioFiber = GameFiber.StartNew(() =>
                        {
                            GameFiber.Sleep(2000);
                            if (!_isDisposed && Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                            {
                                Vector3 bumperPoint = Cruiser.GetOffsetPosition(new Vector3(1.0f, 2.2f, 0f));
                                TaskGoTo(Officer, bumperPoint);
                                GameFiber.Sleep(2500);

                                if (!_isDisposed && Officer != null && Officer.Exists() && Cruiser != null && Cruiser.Exists() && _state == StationaryState.IdleScenario)
                                {
                                    Vector3 aimTarget = bumperPoint + Cruiser.ForwardVector * 30.0f;
                                    NativeFunction.Natives.TASK_TURN_PED_TO_FACE_COORD(Officer, aimTarget.X, aimTarget.Y, aimTarget.Z, 1500);
                                    GameFiber.Sleep(1200);

                                    if (!_isDisposed && Officer != null && Officer.Exists() && _state == StationaryState.IdleScenario)
                                    {
                                        NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(Officer, "WORLD_HUMAN_BINOCULARS", 0, true);
                                    }
                                }
                            }
                        }, "Cadence - Stationary Scenario Fiber");
                        break;
                }
            }

            public void Mobilize()
            {
                if (_state == StationaryState.RemountingOfficer || _state == StationaryState.EnteringVehicle || _state == StationaryState.CruisingAway || _state == StationaryState.Completed)
                {
                    return;
                }

                // 1. Abort scenario fiber & drop props
                AbortScenarioFiber();
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
                    if (!cupModel.SafeLoad(2000)) return;

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
                AbortScenarioFiber();

                // 1. Delete blips
                BlipService.SafeDeleteBlip(ref RadarBlip);
                BlipService.SafeDeleteBlip(ref OfficerFootBlip);
                if (Cruiser != null && Cruiser.Exists())
                {
                    BlipService.SafeDeleteEntityBlip(Cruiser);
                }
                if (Officer != null && Officer.Exists())
                {
                    BlipService.SafeDeleteEntityBlip(Officer);
                }

                // 2. Detach and delete props
                CleanupProp();

                // 3. Release claims in UnitRegistry so recipient feature can claim them
                if (Cruiser != null && Cruiser.Exists())
                {
                    UnitRegistry.Release(Cruiser);
                }
                if (Officer != null && Officer.Exists())
                {
                    UnitRegistry.Release(Officer);
                }

                // 4. Release vehicle handbrake so physics doesn't lock up
                try
                {
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                    }
                }
                catch { }

                // 5. Safe pursuit disenrollment before clearing tasks
                try
                {
                    if (Officer != null && Officer.Exists())
                    {
                        try
                        {
                            if (Functions.IsPedInPursuit(Officer))
                            {
                                Functions.RemovePedFromPursuit(Officer);
                            }
                        }
                        catch { }
                        Officer.Tasks.Clear();
                        Officer.BlockPermanentEvents = false;
                    }
                }
                catch { }

                IsCompleted = true;
            }

            public void Dispose(bool immediateDelete)
            {
                _isDisposed = true;
                _state = StationaryState.Completed;
                IsCompleted = true;

                AbortScenarioFiber();

                // 1. 4-step destructor protocol for blips
                BlipService.SafeDeleteBlip(ref RadarBlip);
                BlipService.SafeDeleteBlip(ref OfficerFootBlip);
                if (Cruiser != null && Cruiser.Exists())
                {
                    BlipService.SafeDeleteEntityBlip(Cruiser);
                }
                if (Officer != null && Officer.Exists())
                {
                    BlipService.SafeDeleteEntityBlip(Officer);
                }

                // 2. Clean up props
                CleanupProp();

                // 3. Dismiss or delete entities via TeardownHelper (neutralizes handbrake/sirens, disenrolls pursuits, releases UnitRegistry)
                try
                {
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        try
                        {
                            NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                        }
                        catch { }
                    }

                    if (Officer != null && Officer.Exists())
                    {
                        TeardownHelper.SafeDismissPed(Officer, delete: immediateDelete);
                    }

                    if (Cruiser != null && Cruiser.Exists())
                    {
                        bool hasLivingOfficer = Officer != null && Officer.Exists() && Officer.IsAlive;
                        bool shouldDeleteCruiser = immediateDelete || !hasLivingOfficer;
                        NativeTasks.ShutOffSirenAndLights(Cruiser);
                        TeardownHelper.SafeDismissVehicle(Cruiser, delete: shouldDeleteCruiser);
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
