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
    /// Feature 2: 1803 Roadside Traffic Stops
    /// Native RPH port of the 1803 roadside pullover system.
    /// Manages authentic highway and shoulder traffic stops across 4 scenarios:
    /// In-Vehicle Observation, Routine Citation, Driver Pat-Down, and K9 Vehicle Sniff.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public sealed class TrafficStopFeature : IFeature
    {
        public static TrafficStopFeature Instance { get; } = new TrafficStopFeature();
        public string Name => "TrafficStop";
        public bool IsRunning => _isRunning;

        void IFeature.Start() => Start();
        void IFeature.Stop() => Stop();
        void IFeature.PurgeAll(bool immediateDelete) => PurgeAll(immediateDelete);
        int IFeature.GetActiveCount() => GetActiveCount();

        private static readonly object _syncLock = new object();
        private static readonly List<TrafficStopInstance> _activeStops = new List<TrafficStopInstance>();
        private static readonly Random _rand = new Random();

        private static GameFiber _trafficStopFiber;
        private static bool _isRunning;
        private static uint _lastSpawnAttemptTime;
        private static int _callsignCounter = 30;

        private static readonly string[] Divisions = new[]
        {
            "1-LINCOLN", "2-ADAM", "3-MARY", "4-GEORGE", "5-EDWARD", "6-FRANK", "7-DAVID", "8-OCEAN"
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
                _lastSpawnAttemptTime = Game.GameTime + 75000;

                Logger.Info("[TrafficStopFeature] Starting roadside traffic stop background fiber (initial spawn staggered at T+75s).");
                _trafficStopFiber = GameFiber.StartNew(TrafficStopProcessLoop, "Cadence - Traffic Stop Feature Fiber");
            }
        }

        public static void Stop()
        {
            GameFiber fiberToJoin = null;
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[TrafficStopFeature] Stopping roadside traffic stop feature and purging stops.");
                PurgeAllInternal(immediateDelete: false);

                fiberToJoin = _trafficStopFiber;
                _trafficStopFiber = null;
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

        public static bool SpawnTestStop()
        {
            try
            {
                Ped player = Game.LocalPlayer?.Character;
                if (player == null || !player.Exists()) return false;

                Logger.Info("[TrafficStopFeature] Spawning manual test roadside traffic stop.");

                Vector3 playerPos = player.Position;
                Vector3 spotPos = Vector3.Zero;
                float spotHeading = 0f;

                List<Vector3> occupiedSpots = new List<Vector3>();
                lock (_syncLock)
                {
                    occupiedSpots.AddRange(_activeStops.Where(s => s != null && s.Cruiser != null && s.Cruiser.Exists()).Select(s => s.Cruiser.Position));
                }

                float maxSearch = Math.Min(250.0f, Math.Max(60.0f, Settings.DespawnDistance - 150.0f));

                // Find candidate pullover spots that are NOT within 35m of an occupied spot
                var candidateSpots = new List<(Vector3 pos, float heading)>();

                // Pass 1: Strict range (35m to maxSearch) across curated roadside pullover zones
                CollectPulloverTestCandidates(playerPos, 35.0f, maxSearch, occupiedSpots, candidateSpots);

                // Pass 2: Progressive expansion if player is in open/rural area (up to 450m)
                if (candidateSpots.Count == 0)
                {
                    CollectPulloverTestCandidates(playerPos, maxSearch, 450.0f, occupiedSpots, candidateSpots);
                }

                if (candidateSpots.Count > 0)
                {
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
                    // NEVER spawn directly on road asphalt or active travel lanes
                    float[] testAngles = new float[] { 0f, 35f, -35f, 65f, -65f, 90f };
                    foreach (float angle in testAngles)
                    {
                        float rad = (player.Heading + angle) * (float)(Math.PI / 180.0);
                        Vector3 dir = new Vector3(-(float)Math.Sin(rad), (float)Math.Cos(rad), 0f);
                        float testDist;
                        lock (_rand)
                        {
                            testDist = 50.0f + (float)(_rand.NextDouble() * 35.0);
                        }
                        Vector3 probe = playerPos + dir * testDist;

                        if (WorldUtils.GetSafeOffroadPosition(probe, 25.0f, 120.0f, out Vector3 offPos, out float offHeading))
                        {
                            if (!WorldUtils.IsRooftopOrElevatedStructure(offPos) && !occupiedSpots.Any(occ => occ.DistanceTo(offPos) < 35.0f) && WorldUtils.IsAreaClear(offPos, 9.0f) && !WorldUtils.IsHighway(offPos))
                            {
                                spotPos = offPos;
                                spotHeading = offHeading;
                                break;
                            }
                        }
                    }
                }

                if (spotPos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(spotPos))
                {
                    Logger.Warn("[TrafficStopFeature] Could not find safe roadside coordinates for test traffic stop.");
                    return false;
                }

                TrafficStopInstance stop = CreateTrafficStopAt(spotPos, spotHeading);
                if (stop != null && stop.IsValid())
                {
                    lock (_syncLock)
                    {
                        _activeStops.Add(stop);
                    }
                    Logger.Info($"[TrafficStopFeature] Spawned test traffic stop {stop.Callsign} (Scenario {stop.Scenario}) at {spotPos}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[TrafficStopFeature] SpawnTestStop encountered an error", ex);
            }

            return false;
        }

        private static void CollectPulloverTestCandidates(Vector3 playerPos, float minDistance, float maxDistance, List<Vector3> occupiedSpots, List<(Vector3 pos, float heading)> candidates)
        {
            foreach (var zone in ZoneSpawnData.GetRoadsidePulloverZones())
            {
                foreach (var spot in zone)
                {
                    float dist = spot.pos.DistanceTo(playerPos);
                    if (dist >= minDistance && dist <= maxDistance && !WorldUtils.IsRooftopOrElevatedStructure(spot.pos) && WorldUtils.IsAreaClear(spot.pos, 9.0f))
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
            lock (_syncLock)
            {
                Logger.Info($"[TrafficStopFeature] Purging all active roadside traffic stops (immediateDelete={immediateDelete}).");
                PurgeAllInternal(immediateDelete: immediateDelete);
            }
        }

        /// <summary>
        /// Relinquishes control of a traffic stop cruiser to an external system (e.g. BackupFeature).
        /// </summary>
        public static bool RelinquishCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;

            TrafficStopInstance stopToRelinquish = null;
            lock (_syncLock)
            {
                stopToRelinquish = _activeStops.FirstOrDefault(s => s != null && (s.Cruiser == cruiser || (s.Officer != null && s.Officer.Exists() && s.Officer.CurrentVehicle == cruiser)));
                if (stopToRelinquish != null)
                {
                    _activeStops.Remove(stopToRelinquish);
                }
            }

            if (stopToRelinquish != null)
            {
                Logger.Info($"[TrafficStopFeature] Relinquishing cruiser 0x{cruiser.Handle:X} ({stopToRelinquish.Callsign}) to external feature.");
                stopToRelinquish.Relinquish();
                UnitRegistry.Release(cruiser);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Retrieves the designated officer assigned to the specified traffic stop cruiser, if active.
        /// </summary>
        public static Ped GetAssignedOfficer(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return null;
            lock (_syncLock)
            {
                var stop = _activeStops.FirstOrDefault(s => s != null && s.Cruiser == cruiser);
                if (stop != null && stop.Officer != null && stop.Officer.Exists() && stop.Officer.IsAlive)
                {
                    return stop.Officer;
                }
            }
            return null;
        }

        public static int GetActiveCount()
        {
            lock (_syncLock)
            {
                _activeStops.RemoveAll(s => s == null || !s.IsValid());
                return _activeStops.Count;
            }
        }

        public static bool IsActiveCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;
            lock (_syncLock)
            {
                return _activeStops.Any(s => s != null && (s.Cruiser == cruiser || (s.Officer != null && s.Officer.Exists() && s.Officer.CurrentVehicle == cruiser)));
            }
        }

        public static List<Vehicle> GetActiveCruisers()
        {
            lock (_syncLock)
            {
                return _activeStops
                    .Where(s => s != null && s.IsValid() && s.Cruiser != null && s.Cruiser.Exists())
                    .Select(s => s.Cruiser)
                    .ToList();
            }
        }

        // ====================================================================
        // Background Process Fiber
        // ====================================================================

        private static void TrafficStopProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    GameFiber.Sleep(1000);

                    if (!_isRunning || !Settings.EnablePlugin || !Settings.EnableTrafficStops)
                    {
                        continue;
                    }

                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists())
                    {
                        continue;
                    }

                    // 1. Update and advance all active traffic stops
                    UpdateActiveStops(player.Position);

                    // 2. Ambient Spawning Cycle
                    RunSpawningCycle(player.Position);
                }
                catch (System.Threading.ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[TrafficStopFeature] Unexpected exception in traffic stop fiber", ex);
                    GameFiber.Sleep(3000);
                }
            }
        }

        private static void UpdateActiveStops(Vector3 playerPos)
        {
            TrafficStopInstance[] snapshot;
            lock (_syncLock)
            {
                snapshot = _activeStops.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                TrafficStopInstance stop = snapshot[i];
                if (stop == null) continue;

                if (!stop.IsValid())
                {
                    lock (_syncLock)
                    {
                        if (!_activeStops.Contains(stop)) continue;
                        _activeStops.Remove(stop);
                        Logger.Debug("TrafficStopFeature", $"Removing completed/invalid stop {stop.Callsign}. Active remaining: {_activeStops.Count}/{Settings.MaxTrafficStops}");
                    }
                    stop.Dispose(immediateDelete: false);
                    continue;
                }

                // Despawn distance watchdog (> 450m) with 45s grace period for newly created stops
                float dist = stop.GetDistanceTo(playerPos);
                uint age = Game.GameTime - stop.CreatedTime;
                if (dist > Settings.DespawnDistance && age > 45000)
                {
                    bool isBusy = false;
                    bool inPursuit = false;
                    try
                    {
                        if (stop.Officer != null && stop.Officer.Exists())
                        {
                            isBusy = Functions.IsCopBusy(stop.Officer, false);
                            inPursuit = Functions.IsPedInPursuit(stop.Officer);
                        }
                    }
                    catch { }

                    if (isBusy || inPursuit)
                    {
                        // Officer drafted by LSPDFR into backup or pursuit: preserve unit, do not despawn
                        continue;
                    }

                    lock (_syncLock)
                    {
                        if (!_activeStops.Contains(stop)) continue;
                        _activeStops.Remove(stop);
                        Logger.Debug("TrafficStopFeature", $"Stop {stop.Callsign} out of range ({dist:F0}m > {Settings.DespawnDistance:F0}m, age: {age / 1000}s). Cleaning up. Active remaining: {_activeStops.Count}/{Settings.MaxTrafficStops}");
                    }
                    stop.Dispose(immediateDelete: false);
                    continue;
                }

                // Advance scenario state machine
                stop.Update();

                if (stop.IsCompleted)
                {
                    lock (_syncLock)
                    {
                        if (!_activeStops.Contains(stop)) continue;
                        _activeStops.Remove(stop);
                    }
                    stop.Dispose(immediateDelete: false);
                }
            }
        }

        private static void RunSpawningCycle(Vector3 playerPos)
        {
            int currentCount;
            lock (_syncLock)
            {
                currentCount = _activeStops.Count;
            }

            if (currentCount >= Settings.MaxTrafficStops)
            {
                return;
            }

            // Spawn attempt pacing: check every 30-45 seconds (15s in Chaos mode)
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

            // Search for safe roadside coordinate (curated pullover spot or dynamic shoulder)
            if (TryFindTrafficStopCoordinate(playerPos, out Vector3 candidatePos, out float candidateHeading))
            {
                // Ensure spot is not too close to another active traffic stop (> 80m)
                bool spotOccupied = false;
                lock (_syncLock)
                {
                    spotOccupied = _activeStops.Any(s => s != null && s.GetDistanceTo(candidatePos) < 80.0f);
                }

                if (!spotOccupied && WorldUtils.IsAreaClear(candidatePos, 10.0f))
                {
                    TrafficStopInstance stop = CreateTrafficStopAt(candidatePos, candidateHeading);
                    if (stop != null && stop.IsValid())
                    {
                        int stopCount;
                        lock (_syncLock)
                        {
                            _activeStops.Add(stop);
                            stopCount = _activeStops.Count;
                        }
                        SessionMetrics.IncrementTrafficStop();
                        Logger.Info($"[TrafficStopFeature] Ambient traffic stop {stop.Callsign} (Scenario {stop.Scenario}) spawned at {candidatePos} (Total: {stopCount}/{Settings.MaxTrafficStops}).");
                    }
                }
            }
        }

        private static bool TryFindTrafficStopCoordinate(Vector3 playerPos, out Vector3 spotPos, out float spotHeading)
        {
            spotPos = Vector3.Zero;
            spotHeading = 0f;

            // Priority 1: Check curated pullover spots from ZoneSpawnData (including urban/suburban lots)
            if (ZoneSpawnData.FindBestPulloverSpot(playerPos, 120.0f, 380.0f, out Vector3 curatedPos, out float curatedHeading))
            {
                if (!WorldUtils.IsSubterraneanOrVoid(curatedPos) && !WorldUtils.IsRooftopOrElevatedStructure(curatedPos) && WorldUtils.IsAreaClear(curatedPos, 10.0f))
                {
                    spotPos = curatedPos;
                    spotHeading = curatedHeading;
                    return true;
                }
            }

            // Priority 2: Safe dynamic off-road shoulder generation (strictly prohibited on freeways/highways)
            for (int attempt = 0; attempt < 20; attempt++)
            {
                float dist;
                double angle;
                lock (_rand)
                {
                    dist = (float)_rand.Next(100, 260);
                    angle = _rand.NextDouble() * Math.PI * 2.0;
                }

                Vector3 probeCenter = new Vector3(
                    playerPos.X + (float)Math.Cos(angle) * dist,
                    playerPos.Y + (float)Math.Sin(angle) * dist,
                    playerPos.Z
                );

                if (WorldUtils.GetSafeOffroadPosition(probeCenter, 30.0f, 150.0f, out Vector3 offroadPos, out float offroadH))
                {
                    if (!WorldUtils.IsHighway(offroadPos) && !WorldUtils.IsSubterraneanOrVoid(offroadPos) && !WorldUtils.IsRooftopOrElevatedStructure(offroadPos) && !WorldUtils.IsOnActiveRoadOrLane(offroadPos) && WorldUtils.IsAreaClear(offroadPos, 9.0f))
                    {
                        spotPos = offroadPos;
                        spotHeading = offroadH;
                        return true;
                    }
                }
            }

            return false;
        }

        private static TrafficStopInstance CreateTrafficStopAt(Vector3 shoulderPos, float heading)
        {
            string callsign = GenerateCallsign();
            Vehicle cruiser = null;
            Ped officer = null;
            Vehicle civilianVehicle = null;
            Ped civilianDriver = null;
            Ped k9Dog = null;
            Blip blip = null;

            int scenario;
            if (WorldUtils.IsOnActiveRoadOrLane(shoulderPos, 6.5f))
            {
                // Scenario 0: In-Vehicle Observation (shoulder is within 6.5m of hazardous active traffic lane)
                scenario = 0;
            }
            else
            {
                lock (_rand)
                {
                    scenario = _rand.Next(1, 4); // 1 = Citation, 2 = Pat-Down, 3 = K9 Search
                }
            }

            try
            {
                if (WorldUtils.IsRooftopOrElevatedStructure(shoulderPos))
                {
                    Logger.Debug("TrafficStopFeature", $"Aborting traffic stop at {shoulderPos}: candidate position is on rooftop/elevated structure.");
                    return null;
                }

                // Validate ground elevation
                Vector3 cruiserPos = shoulderPos;
                if (WorldUtils.GetAccurateGroundZ(cruiserPos, out float groundZ))
                {
                    if (Math.Abs(cruiserPos.Z - groundZ) <= 3.0f)
                    {
                        cruiserPos.Z = groundZ + 0.35f;
                    }
                }

                if (WorldUtils.IsRooftopOrElevatedStructure(cruiserPos))
                {
                    Logger.Debug("TrafficStopFeature", $"Aborting traffic stop at {cruiserPos}: adjusted position is on rooftop/elevated structure.");
                    return null;
                }

                // 1. Spawn Police Cruiser & Officer (UB Tier 1 or Native Fallback Tier 2)
                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    bool isHighway = WorldUtils.ShouldSpawnHighwayUnit(cruiserPos);
                    var ubResult = isHighway
                        ? UltimateBackupService.SpawnStatePatrol(cruiserPos, 1)
                        : UltimateBackupService.SpawnLocalPatrol(cruiserPos, 1);

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
                        Logger.Debug("TrafficStopFeature", $"Ultimate Backup did not return a complete unit for traffic stop at {cruiserPos}. Fallback is strictly disabled (Fallback=false). Aborting spawn.");
                        return null;
                    }

                    string policeModelName = ZoneSpawnData.GetDefaultPoliceVehicleForPosition(cruiserPos);
                    string copModelName = ZoneSpawnData.GetDefaultCopPedModelForPosition(cruiserPos);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(cruiserPos, heading, policeModelName, copModelName);
                    if (nativeResult != null && nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                    {
                        cruiser = nativeResult.Item1;
                        officer = nativeResult.Item2[0];

                        if (Settings.EnableEupOutfits)
                        {
                            EupService.ApplyTrafficVest(officer);
                        }
                    }
                }

                if (cruiser == null || !cruiser.Exists() || officer == null || !officer.Exists())
                {
                    if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    if (officer != null && officer.Exists()) TeardownHelper.SafeDismissPed(officer, delete: true);
                    Logger.Warn($"[TrafficStopFeature] Failed to spawn police cruiser or officer at {cruiserPos}.");
                    return null;
                }

                if (!UnitRegistry.TryClaim(cruiser, UnitRegistry.OwnerTrafficStop))
                {
                    TeardownHelper.SafeDismissPed(officer, delete: true);
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    return null;
                }

                if (!UnitRegistry.TryClaim(officer, UnitRegistry.OwnerTrafficStop))
                {
                    TeardownHelper.SafeDismissPed(officer, delete: true);
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    return null;
                }

                cruiser.Heading = heading;
                cruiser.MakePersistent();
                cruiser.IsEngineOn = true;
                try { cruiser.PlaceOnGroundProperly(); } catch { }
                WorldUtils.SecureEntityGround(cruiser);

                officer.MakePersistent();
                officer.BlockPermanentEvents = true;
                if (officer.CurrentVehicle != cruiser)
                {
                    officer.WarpIntoVehicle(cruiser, -1);
                }

                // Activate silent lightbars immediately
                if (Settings.MuteTrafficStopSiren)
                {
                    NativeTasks.SetSilentSiren(cruiser, true);
                }
                else
                {
                    cruiser.IsSirenOn = true;
                }

                // 2. Spawn Civilian Vehicle & Driver parked 8.0m directly ahead
                Vector3 civPos = cruiser.GetOffsetPosition(new Vector3(0f, 8.0f, 0f));
                if (WorldUtils.GetAccurateGroundZ(civPos, out float civGroundZ))
                {
                    if (Math.Abs(civPos.Z - civGroundZ) <= 3.0f)
                    {
                        civPos.Z = civGroundZ + 0.35f;
                    }
                }

                if (WorldUtils.IsRooftopOrElevatedStructure(civPos))
                {
                    TeardownHelper.SafeDismissPed(officer, delete: true);
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    return null;
                }

                string civVehName = ZoneSpawnData.GetRandomCivilianVehicle();
                Model civVehModel = new Model(civVehName);
                if (!civVehModel.SafeLoad(4000))
                {
                    TeardownHelper.SafeDismissPed(officer, delete: true);
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    return null;
                }

                civilianVehicle = new Vehicle(civVehModel, civPos, heading);
                civVehModel.Dismiss();

                if (civilianVehicle == null || !civilianVehicle.Exists())
                {
                    TeardownHelper.SafeDismissPed(officer, delete: true);
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    return null;
                }

                civilianVehicle.MakePersistent();
                civilianVehicle.Heading = heading;
                civilianVehicle.IsEngineOn = true;
                civilianVehicle.IndicatorLightsStatus = VehicleIndicatorLightsStatus.Both; // Hazard lights
                try { civilianVehicle.PlaceOnGroundProperly(); } catch { }
                WorldUtils.SecureEntityGround(civilianVehicle);

                civilianDriver = civilianVehicle.CreateRandomDriver();
                if (civilianDriver == null || !civilianDriver.Exists())
                {
                    Model civPedModel = new Model("a_m_y_business_01");
                    if (civPedModel.SafeLoad(4000))
                    {
                        civilianDriver = new Ped(civPedModel, civPos, heading);
                        civPedModel.Dismiss();

                        if (civilianDriver != null && civilianDriver.Exists())
                        {
                            civilianDriver.WarpIntoVehicle(civilianVehicle, -1);
                        }
                    }
                }

                if (civilianDriver == null || !civilianDriver.Exists())
                {
                    TeardownHelper.SafeDismissVehicle(civilianVehicle, delete: true);
                    TeardownHelper.SafeDismissPed(officer, delete: true);
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    return null;
                }

                civilianDriver.MakePersistent();
                civilianDriver.BlockPermanentEvents = true;

                // 3. Scenario 3 K9 Setup: German Shepherd beside cruiser
                if (scenario == 3)
                {
                    try
                    {
                        Model k9Model = new Model("a_c_shepherd");
                        if (k9Model.SafeLoad(4000))
                        {
                            Vector3 k9SpawnPos = cruiser.GetOffsetPosition(new Vector3(1.5f, -0.8f, 0f));
                            k9Dog = new Ped(k9Model, k9SpawnPos, heading);
                            k9Model.Dismiss();

                            if (k9Dog != null && k9Dog.Exists())
                            {
                                k9Dog.MakePersistent();
                                k9Dog.BlockPermanentEvents = true;
                                WorldUtils.SecureEntityGround(k9Dog);
                                k9Dog.Tasks.StandStill(10000);
                            }
                            else
                            {
                                scenario = 1; // Fallback to routine citation if K9 model fails
                            }
                        }
                        else
                        {
                            scenario = 1;
                        }
                    }
                    catch
                    {
                        scenario = 1;
                    }
                }

                // 4. Attach Yellow Traffic Stop Radar Blip
                blip = BlipService.AttachTrafficStopBlip(cruiser, callsign);

                string scName = scenario == 0 ? "Observation" : scenario == 1 ? "Citation" : scenario == 2 ? "PatDown" : "K9Sniff";
                Logger.Debug("TrafficStopFeature", $"Configured traffic stop {callsign} [{scName}]: Cruiser={cruiser.Model.Name} (0x{cruiser.Handle:X}), Officer={officer.Model.Name} (0x{officer.Handle:X}), CivVeh={civilianVehicle.Model.Name} (0x{civilianVehicle.Handle:X}), CivDriver={civilianDriver.Model.Name} (0x{civilianDriver.Handle:X}), K9={(k9Dog != null ? "Active" : "None")}");

                return new TrafficStopInstance(cruiser, officer, civilianVehicle, civilianDriver, k9Dog, blip, callsign, scenario, cruiserPos);
            }
            catch (Exception ex)
            {
                Logger.Error($"[TrafficStopFeature] Exception creating traffic stop at {shoulderPos}", ex);

                if (blip != null) BlipService.SafeDeleteBlip(ref blip);
                if (k9Dog != null && k9Dog.Exists()) TeardownHelper.SafeDismissPed(k9Dog, delete: true);
                if (civilianDriver != null && civilianDriver.Exists()) TeardownHelper.SafeDismissPed(civilianDriver, delete: true);
                if (civilianVehicle != null && civilianVehicle.Exists()) TeardownHelper.SafeDismissVehicle(civilianVehicle, delete: true);
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
                Interlocked.Exchange(ref _callsignCounter, 30);
                num = 30;
            }
            return $"{div}-{num:D2}";
        }

        private static void PurgeAllInternal(bool immediateDelete = true)
        {
            TrafficStopInstance[] snapshot;
            lock (_syncLock)
            {
                snapshot = _activeStops.ToArray();
                _activeStops.Clear();
                _lastSpawnAttemptTime = Game.GameTime + 35000;
            }

            foreach (var stop in snapshot)
            {
                if (stop != null)
                {
                    stop.Dispose(immediateDelete);
                }
            }
        }

        // ====================================================================
        // Managed Traffic Stop Instance & State Machine
        // ====================================================================

        private enum StopState
        {
            InitialExiting,
            ApproachPassengerWindow,
            WindowContact,
            CitationWriting,
            PatDownDriverExiting,
            PatDownFrisking,
            PatDownDriverReturning,
            K9DriverExiting,
            K9SearchingChassis,
            K9ReturningToCruiser,
            OfficerReturningToCruiser,
            OfficerEnteringCruiser,
            ReleasingVehicles,
            Completed
        }

        private class TrafficStopInstance
        {
            public Vehicle Cruiser { get; }
            public Ped Officer { get; }
            public Vehicle CivVehicle { get; }
            public Ped CivDriver { get; }
            public Ped K9Dog { get; }
            public Blip RadarBlip;
            public Blip OfficerFootBlip;
            public string Callsign { get; }
            public int Scenario { get; }
            public Vector3 OriginPosition { get; }
            public bool IsCompleted { get; private set; }
            public uint CreatedTime { get; } = Game.GameTime;

            private StopState _state;
            private uint _stateStartTime;
            private uint _lastSirenReinforceTime;
            private int _k9SearchStage;
            private int _patDownAnimStage;
            private uint _patDownStageStartTime;

            private Rage.Object _activeProp;
            private bool _civDeparted;
            private bool _copDeparted;

            public TrafficStopInstance(Vehicle cruiser, Ped officer, Vehicle civVehicle, Ped civDriver, Ped k9Dog, Blip blip, string callsign, int scenario, Vector3 origin)
            {
                Cruiser = cruiser;
                Officer = officer;
                CivVehicle = civVehicle;
                CivDriver = civDriver;
                K9Dog = k9Dog;
                RadarBlip = blip;
                Callsign = callsign;
                Scenario = scenario;
                OriginPosition = origin;

                _state = StopState.InitialExiting;
                _stateStartTime = Game.GameTime;
                _lastSirenReinforceTime = Game.GameTime;
            }

            public bool IsValid()
            {
                if (IsCompleted) return false;
                return Cruiser != null && Cruiser.Exists() && Cruiser.IsDriveable &&
                       Officer != null && Officer.Exists() && Officer.IsAlive &&
                       CivVehicle != null && CivVehicle.Exists() &&
                       CivDriver != null && CivDriver.Exists() && CivDriver.IsAlive;
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
                if (now - CreatedTime > 300000)
                {
                    IsCompleted = true;
                    _state = StopState.Completed;
                    return;
                }

                // Reinforce silent lightbars to prevent audio siren chirps
                if (now - _lastSirenReinforceTime >= 500 && _state != StopState.ReleasingVehicles)
                {
                    _lastSirenReinforceTime = now;
                    NativeTasks.SetSilentSiren(Cruiser, true);
                }

                uint elapsed = now - _stateStartTime;

                switch (_state)
                {
                    case StopState.InitialExiting:
                        if (Scenario == 0)
                        {
                            // In-vehicle observation scenario: officer remains seated in cruiser for safety
                            _state = StopState.WindowContact;
                            _stateStartTime = now;
                        }
                        else
                        {
                            Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                            OfficerFootBlip = BlipService.AttachOfficerFootBlip(Officer, Callsign);
                            _state = StopState.ApproachPassengerWindow;
                            _stateStartTime = now;
                        }
                        break;

                    case StopState.ApproachPassengerWindow:
                        if (Officer.IsOnFoot || elapsed > 3500)
                        {
                            // Passenger-side approach (highway safety standard)
                            Vector3 windowPoint = CivVehicle.Position + CivVehicle.RightVector * 1.4f + CivVehicle.ForwardVector * 0.8f;
                            TaskGoTo(Officer, windowPoint);

                            if (Officer.Position.DistanceTo(windowPoint) <= 1.5f || elapsed > 9000)
                            {
                                NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(Officer, CivVehicle, 1500);
                                _stateStartTime = now;

                                if (Scenario == 1)
                                {
                                    _state = StopState.WindowContact;
                                }
                                else if (Scenario == 2)
                                {
                                    _state = StopState.PatDownDriverExiting;
                                }
                                else
                                {
                                    _state = StopState.K9DriverExiting;
                                }
                            }
                        }
                        break;

                    // ========================================================
                    // Scenario 1: Routine Citation
                    // ========================================================
                    case StopState.WindowContact:
                        if (Scenario == 0)
                        {
                            // Officer runs MDT check for 20s while seated inside cruiser
                            if (elapsed > 20000)
                            {
                                NativeTasks.SetSilentSiren(Cruiser, false);
                                CivVehicle.IndicatorLightsStatus = VehicleIndicatorLightsStatus.Off;
                                _state = StopState.ReleasingVehicles;
                                _stateStartTime = now;
                            }
                        }
                        else if (elapsed > 1500)
                        {
                            EquipNotepadProp();
                            if (NativeTasks.RequestAnimDict("amb@world_human_clipboard@male@base"))
                            {
                                Officer.Tasks.PlayAnimation("amb@world_human_clipboard@male@base", "base", 8.0f, AnimationFlags.Loop);
                            }
                            _state = StopState.CitationWriting;
                            _stateStartTime = now;
                        }
                        break;

                    case StopState.CitationWriting:
                        if (elapsed > 18000) // 18 seconds of citation writing
                        {
                            CleanupProp();
                            Officer.Tasks.Clear();
                            TaskGoTo(Officer, Cruiser.Position);
                            _state = StopState.OfficerReturningToCruiser;
                            _stateStartTime = now;
                        }
                        break;

                    // ========================================================
                    // Scenario 2: Driver Pat-Down
                    // ========================================================
                    case StopState.PatDownDriverExiting:
                        CivDriver.Tasks.LeaveVehicle(CivVehicle, LeaveVehicleFlags.None);
                        Vector3 friskPoint = Cruiser.Position + Cruiser.ForwardVector * 2.2f + Cruiser.RightVector * 0.8f;
                        TaskGoTo(CivDriver, friskPoint);
                        TaskGoTo(Officer, friskPoint + Officer.ForwardVector * 0.8f);

                        if ((CivDriver.Position.DistanceTo(friskPoint) <= 1.5f && Officer.Position.DistanceTo(friskPoint) <= 2.0f) || elapsed > 10000)
                        {
                            CivDriver.Tasks.StandStill(20000);
                            NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(Officer, CivDriver, 1000);
                            NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(CivDriver, Cruiser, 1000);

                            _patDownAnimStage = 1;
                            _patDownStageStartTime = now;
                            _state = StopState.PatDownFrisking;
                            _stateStartTime = now;
                        }
                        break;

                    case StopState.PatDownFrisking:
                        UpdatePatDownSequence(now);
                        if (_patDownAnimStage >= 5 && (now - _patDownStageStartTime > 2500))
                        {
                            Officer.Tasks.Clear();
                            CivDriver.Tasks.Clear();
                            CivDriver.Tasks.EnterVehicle(CivVehicle, -1);
                            _state = StopState.PatDownDriverReturning;
                            _stateStartTime = now;
                        }
                        break;

                    case StopState.PatDownDriverReturning:
                        if (CivDriver.IsInVehicle(CivVehicle, false) || elapsed > 8000)
                        {
                            // Officer completes brief notes at driver window
                            Vector3 windowPoint = CivVehicle.Position + CivVehicle.RightVector * 1.4f + CivVehicle.ForwardVector * 0.8f;
                            TaskGoTo(Officer, windowPoint);

                            if (Officer.Position.DistanceTo(windowPoint) <= 1.5f || elapsed > 12000)
                            {
                                EquipNotepadProp();
                                NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(Officer, "WORLD_HUMAN_CLIPBOARD", 0, true);
                                _state = StopState.CitationWriting;
                                _stateStartTime = now;
                            }
                        }
                        break;

                    // ========================================================
                    // Scenario 3: K9 Vehicle Sniff
                    // ========================================================
                    case StopState.K9DriverExiting:
                        CivDriver.Tasks.LeaveVehicle(CivVehicle, LeaveVehicleFlags.None);
                        Vector3 shoulderWaitPoint = CivVehicle.Position + CivVehicle.RightVector * 2.5f - CivVehicle.ForwardVector * 0.8f;
                        TaskGoTo(CivDriver, shoulderWaitPoint);

                        if (CivDriver.Position.DistanceTo(shoulderWaitPoint) <= 1.5f || elapsed > 8000)
                        {
                            CivDriver.Tasks.StandStill(25000);
                            _k9SearchStage = 0;
                            if (K9Dog != null && K9Dog.Exists())
                            {
                                K9Dog.Tasks.Clear();
                                TaskGoTo(K9Dog, GetK9SearchPoint(0));
                            }
                            _state = StopState.K9SearchingChassis;
                            _stateStartTime = now;
                        }
                        break;

                    case StopState.K9SearchingChassis:
                        if (K9Dog == null || !K9Dog.Exists())
                        {
                            _state = StopState.K9ReturningToCruiser;
                            _stateStartTime = now;
                            break;
                        }

                        int currentStage = Math.Min(3, (int)(elapsed / 4500));
                        if (currentStage != _k9SearchStage)
                        {
                            _k9SearchStage = currentStage;
                            TaskGoTo(K9Dog, GetK9SearchPoint(_k9SearchStage));
                        }

                        if (elapsed > 18000) // Completed all 4 search points
                        {
                            TaskGoTo(K9Dog, Cruiser.Position + Cruiser.RightVector * 1.2f);
                            _state = StopState.K9ReturningToCruiser;
                            _stateStartTime = now;
                        }
                        break;

                    case StopState.K9ReturningToCruiser:
                        if (elapsed > 4000)
                        {
                            if (K9Dog != null && K9Dog.Exists())
                            {
                                K9Dog.Tasks.StandStill(15000);
                            }

                            CivDriver.Tasks.Clear();
                            CivDriver.Tasks.EnterVehicle(CivVehicle, -1);
                            TaskGoTo(Officer, Cruiser.Position);
                            _state = StopState.OfficerReturningToCruiser;
                            _stateStartTime = now;
                        }
                        break;

                    // ========================================================
                    // Common Wrap-Up & Departure
                    // ========================================================
                    case StopState.OfficerReturningToCruiser:
                        if (Officer.Position.DistanceTo(Cruiser.Position) <= 2.5f || elapsed > 9000)
                        {
                            Officer.Tasks.EnterVehicle(Cruiser, -1);
                            _state = StopState.OfficerEnteringCruiser;
                            _stateStartTime = now;
                        }
                        break;

                    case StopState.OfficerEnteringCruiser:
                        if (Officer.IsInVehicle(Cruiser, false) || elapsed > 8000)
                        {
                            BlipService.SafeDeleteBlip(ref OfficerFootBlip);

                            // Pack K9 into cruiser if present
                            if (K9Dog != null && K9Dog.Exists())
                            {
                                int freeSeat = Cruiser.IsSeatFree(1) ? 1 : (Cruiser.IsSeatFree(2) ? 2 : 0);
                                K9Dog.WarpIntoVehicle(Cruiser, freeSeat);
                            }

                            // Turn off cruiser lightbars & civilian hazards
                            NativeTasks.SetSilentSiren(Cruiser, false);
                            CivVehicle.IndicatorLightsStatus = VehicleIndicatorLightsStatus.Off;

                            _state = StopState.ReleasingVehicles;
                            _stateStartTime = now;
                        }
                        break;

                    case StopState.ReleasingVehicles:
                        // Cap max duration in ReleasingVehicles to 35s
                        if (elapsed >= 35000)
                        {
                            IsCompleted = true;
                            _state = StopState.Completed;
                            break;
                        }

                        // Civilian merges into traffic first
                        if (!_civDeparted && elapsed >= 1500)
                        {
                            _civDeparted = true;
                            CivDriver.BlockPermanentEvents = false;
                            if (!CivDriver.IsInVehicle(CivVehicle, false))
                            {
                                CivDriver.WarpIntoVehicle(CivVehicle, -1);
                            }
                            NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(CivVehicle, 5.0f);
                            CivDriver.Tasks.CruiseWithVehicle(CivVehicle, 18.0f, NativeTasks.CruisingFlags);
                        }

                        // Officer cruiser merges into traffic 3 seconds later
                        if (!_copDeparted && elapsed >= 4500)
                        {
                            _copDeparted = true;
                            Officer.BlockPermanentEvents = false;
                            if (!Officer.IsInVehicle(Cruiser, false))
                            {
                                Officer.WarpIntoVehicle(Cruiser, -1);
                            }
                            NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(Cruiser, 6.0f);
                            Officer.Tasks.CruiseWithVehicle(Cruiser, 18.0f, NativeTasks.CruisingFlags);
                            BlipService.TransitionToPatrol(RadarBlip, Callsign);
                        }

                        // Complete stop only after units have cleanly driven away down the street
                        if (_civDeparted && _copDeparted)
                        {
                            Ped playerPed = Game.LocalPlayer?.Character;
                            float distToPlayer = (Cruiser != null && Cruiser.Exists() && playerPed != null && playerPed.Exists())
                                ? Cruiser.Position.DistanceTo(playerPed.Position) : 250.0f;

                            if ((elapsed >= 25000 && distToPlayer >= 120.0f) || distToPlayer >= 180.0f)
                            {
                                IsCompleted = true;
                                _state = StopState.Completed;
                            }
                        }
                        break;
                }
            }

            private void UpdatePatDownSequence(uint now)
            {
                uint stageElapsed = now - _patDownStageStartTime;

                switch (_patDownAnimStage)
                {
                    case 1:
                        if (stageElapsed >= 2000)
                        {
                            if (NativeTasks.RequestAnimDict("anim@heists@box_carry@"))
                            {
                                Officer.Tasks.PlayAnimation("anim@heists@box_carry@", "idle", 8.0f, AnimationFlags.Loop);
                            }
                            _patDownAnimStage = 2;
                            _patDownStageStartTime = now;
                        }
                        break;

                    case 2:
                        if (stageElapsed >= 1500)
                        {
                            if (NativeTasks.RequestAnimDict("missbigscore2aig_7@driver"))
                            {
                                Officer.Tasks.PlayAnimation("missbigscore2aig_7@driver", "boot_r_loop", 8.0f, AnimationFlags.Loop);
                            }
                            _patDownAnimStage = 3;
                            _patDownStageStartTime = now;
                        }
                        break;

                    case 3:
                        if (stageElapsed >= 1200)
                        {
                            if (NativeTasks.RequestAnimDict("missbigscore2aig_7@driver"))
                            {
                                Officer.Tasks.PlayAnimation("missbigscore2aig_7@driver", "boot_l_loop", 8.0f, AnimationFlags.Loop);
                            }
                            _patDownAnimStage = 4;
                            _patDownStageStartTime = now;
                        }
                        break;

                    case 4:
                        if (stageElapsed >= 1200)
                        {
                            if (NativeTasks.RequestAnimDict("amb@world_human_stand_guard@male@base"))
                            {
                                Officer.Tasks.PlayAnimation("amb@world_human_stand_guard@male@base", "base", 8.0f, AnimationFlags.Loop);
                            }
                            _patDownAnimStage = 5;
                            _patDownStageStartTime = now;
                        }
                        break;
                }
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

            private Vector3 GetK9SearchPoint(int stage)
            {
                if (CivVehicle == null || !CivVehicle.Exists()) return Cruiser.Position;

                return stage switch
                {
                    0 => CivVehicle.Position + CivVehicle.ForwardVector * 2.8f,             // Front bumper
                    1 => CivVehicle.Position - CivVehicle.RightVector * 1.8f,               // Driver side
                    2 => CivVehicle.Position - CivVehicle.ForwardVector * 2.8f,             // Rear bumper
                    _ => CivVehicle.Position + CivVehicle.RightVector * 1.8f                // Passenger side
                };
            }

            private void EquipNotepadProp()
            {
                CleanupProp();
                try
                {
                    Model propModel = new Model("prop_notepad_01");
                    if (!propModel.SafeLoad(2000)) return;

                    _activeProp = new Rage.Object(propModel, Officer.Position);
                    propModel.Dismiss();

                    if (_activeProp != null && _activeProp.Exists())
                    {
                        // Attach to left skeletal hand (SKEL_L_Hand = 18905)
                        int handBone = NativeFunction.Natives.GET_PED_BONE_INDEX<int>(Officer, 18905);
                        _activeProp.AttachTo(Officer, handBone, new Vector3(0.15f, 0.03f, 0.1f), new Rotator(-80f, 0f, 10f));
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

            public void Dispose(bool immediateDelete)
            {
                // 1. Zero-leak 4-step destructor protocol for blip
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

                // 3. Dismiss or delete entities via TeardownHelper (neutralizes sirens, disenrolls pursuits, releases UnitRegistry)
                try
                {
                    if (K9Dog != null && K9Dog.Exists())
                    {
                        TeardownHelper.SafeDismissPed(K9Dog, delete: immediateDelete);
                    }

                    if (CivDriver != null && CivDriver.Exists())
                    {
                        TeardownHelper.SafeDismissPed(CivDriver, delete: immediateDelete);
                    }

                    if (CivVehicle != null && CivVehicle.Exists())
                    {
                        try { CivVehicle.IndicatorLightsStatus = VehicleIndicatorLightsStatus.Off; } catch { }
                        TeardownHelper.SafeDismissVehicle(CivVehicle, delete: immediateDelete);
                    }

                    if (Officer != null && Officer.Exists())
                    {
                        TeardownHelper.SafeDismissPed(Officer, delete: immediateDelete);
                    }

                    if (Cruiser != null && Cruiser.Exists())
                    {
                        try { NativeTasks.SetSilentSiren(Cruiser, false); } catch { }
                        TeardownHelper.SafeDismissVehicle(Cruiser, delete: immediateDelete);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[TrafficStopFeature] Error disposing stop {Callsign}: {ex.Message}");
                }

                IsCompleted = true;
            }

            /// <summary>
            /// Relinquishes the cruiser and officer from the traffic stop to an external emergency feature (BackupFeature).
            /// </summary>
            public void Relinquish()
            {
                CleanupProp();
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

                // Release ownership claims in UnitRegistry so caller feature can claim them
                if (Cruiser != null && Cruiser.Exists())
                {
                    UnitRegistry.Release(Cruiser);
                }
                if (Officer != null && Officer.Exists())
                {
                    UnitRegistry.Release(Officer);
                }

                try
                {
                    if (K9Dog != null && K9Dog.Exists())
                    {
                        TeardownHelper.SafeDismissPed(K9Dog, delete: false);
                    }

                    if (CivDriver != null && CivDriver.Exists())
                    {
                        CivDriver.BlockPermanentEvents = false;
                        if (CivVehicle != null && CivVehicle.Exists())
                        {
                            CivVehicle.IndicatorLightsStatus = VehicleIndicatorLightsStatus.Off;
                            if (!CivDriver.IsInVehicle(CivVehicle, false))
                            {
                                CivDriver.WarpIntoVehicle(CivVehicle, -1);
                            }
                            CivDriver.Tasks.CruiseWithVehicle(CivVehicle, 15.0f, NativeTasks.CruisingFlags);
                            TeardownHelper.SafeDismissVehicle(CivVehicle, delete: false);
                        }
                        TeardownHelper.SafeDismissPed(CivDriver, delete: false);
                    }

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

                    if (Cruiser != null && Cruiser.Exists())
                    {
                        NativeTasks.SetSilentSiren(Cruiser, false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[TrafficStopFeature] Error relinquishing stop {Callsign}: {ex.Message}");
                }

                IsCompleted = true;
            }
        }
    }
}
