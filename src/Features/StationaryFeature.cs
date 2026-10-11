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
                _lastSpawnAttemptTime = Game.GameTime;

                Logger.Info("[StationaryFeature] Starting stationary unit background fiber.");
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

                // Attempt to find curated turnout spot within 400m
                if (!ZoneSpawnData.FindBestPulloverSpot(playerPos, 40.0f, 400.0f, out spotPos, out spotHeading))
                {
                    // Fallback to roadside turnout/median ahead of player
                    Vector3 aheadPos = playerPos + player.ForwardVector * 50.0f;
                    if (WorldUtils.GetSafeStreetPosition(aheadPos, 1, out Vector3 stPos, out float stH))
                    {
                        spotPos = stPos;
                        spotHeading = stH;
                    }
                    else
                    {
                        spotPos = World.GetNextPositionOnStreet(aheadPos);
                        spotHeading = player.Heading;
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

            // Spawn pacing: check every 25 seconds
            uint now = Game.GameTime;
            if (now - _lastSpawnAttemptTime < 25000 && currentCount > 0)
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
                        Logger.Info($"[StationaryFeature] Ambient stationary unit {unit.Callsign} (Scenario {unit.Scenario}) spawned at {spawnPos} (Total: {_activeUnits.Count}/{Settings.MaxStationaryUnits}).");
                    }
                }
            }
        }

        private static bool TryFindStationaryCoordinate(Vector3 playerPos, out Vector3 spawnPos, out float spawnHeading)
        {
            spawnPos = Vector3.Zero;
            spawnHeading = 0f;

            // Priority 1: Check curated turnouts from ZoneSpawnData
            if (ZoneSpawnData.FindBestPulloverSpot(playerPos, 120.0f, 300.0f, out Vector3 curatedPos, out float curatedHeading))
            {
                if (!WorldUtils.IsSubterraneanOrVoid(curatedPos) && WorldUtils.IsAreaClear(curatedPos, 8.0f))
                {
                    spawnPos = curatedPos;
                    spawnHeading = curatedHeading;
                    return true;
                }
            }

            // Priority 2: Probe safe roadside node
            for (int attempt = 0; attempt < 15; attempt++)
            {
                float dist = (float)_rand.Next(130, 290);
                double angle = _rand.NextDouble() * Math.PI * 2.0;

                Vector3 probeCenter = new Vector3(
                    playerPos.X + (float)Math.Cos(angle) * dist,
                    playerPos.Y + (float)Math.Sin(angle) * dist,
                    playerPos.Z
                );

                if (WorldUtils.GetSafeStreetPosition(probeCenter, 1, out Vector3 nodePos, out float nodeHeading))
                {
                    if (WorldUtils.IsSubterraneanOrVoid(nodePos)) continue;

                    float d = nodePos.DistanceTo(playerPos);
                    if (d < 110f || d > 320f) continue;

                    if (!WorldUtils.IsAreaClear(nodePos, 8.0f)) continue;

                    if (WorldUtils.GetAccurateGroundZ(nodePos, out float safeZ))
                    {
                        nodePos.Z = safeZ + 0.35f;
                    }

                    spawnPos = nodePos;
                    spawnHeading = nodeHeading;
                    return true;
                }
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
                    bool isHighway = spawnPos.Y > 1000f;
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
                    string policeModelName = ZoneSpawnData.GetDefaultPoliceVehicleForPosition(spawnPos);
                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(spawnPos, heading, policeModelName, "s_m_y_cop_01");
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
                cruiser.IsEngineOn = true;
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
                // Determine whether unit is directly in active road lanes; if so, keep officer seated inside!
                bool isOnActiveLane = false;
                if (WorldUtils.GetSafeStreetPosition(spawnPos, 1, out Vector3 nearNode, out _))
                {
                    if (spawnPos.DistanceTo(nearNode) < 4.0f)
                    {
                        isOnActiveLane = true;
                    }
                }

                int scenario = 0;
                if (isOnActiveLane)
                {
                    scenario = 0; // Seated radar watch
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

                        NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(Cruiser, 5.0f);
                        Officer.Tasks.CruiseWithVehicle(Cruiser, 18.0f, NativeTasks.CruisingFlags);

                        // Transition blip to normal patrol radar blip
                        BlipService.TransitionToPatrol(RadarBlip, Callsign);

                        _state = StationaryState.CruisingAway;
                        _stateStartTime = now;
                        break;

                    case StationaryState.CruisingAway:
                        // After 8 seconds of cruising away, dismiss persistence and complete
                        if (elapsed >= 8000)
                        {
                            IsCompleted = true;
                            _state = StationaryState.Completed;
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
