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
    /// Feature 2: 1803 Roadside Traffic Stops
    /// Native RPH port of the 1803 roadside pullover system.
    /// Manages authentic highway and shoulder traffic stops across 3 scenarios:
    /// Routine Citation, Driver Pat-Down, and K9 Vehicle Sniff.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public static class TrafficStopFeature
    {
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
                _lastSpawnAttemptTime = Game.GameTime;

                Logger.Info("[TrafficStopFeature] Starting roadside traffic stop background fiber.");
                _trafficStopFiber = GameFiber.StartNew(TrafficStopProcessLoop, "Cadence - Traffic Stop Feature Fiber");
            }
        }

        public static void Stop()
        {
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[TrafficStopFeature] Stopping roadside traffic stop feature and purging stops.");
                PurgeAllInternal();

                try
                {
                    if (_trafficStopFiber != null && _trafficStopFiber.IsAlive)
                    {
                        _trafficStopFiber.Abort();
                    }
                }
                catch { }
                finally
                {
                    _trafficStopFiber = null;
                }
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

                // Attempt to find closest curated shoulder spot within 500m
                if (!ZoneSpawnData.FindBestPulloverSpot(playerPos, 50.0f, 500.0f, out spotPos, out spotHeading))
                {
                    // Fallback to closest street shoulder forward of player
                    Vector3 searchAhead = playerPos + player.ForwardVector * 45.0f;
                    if (WorldUtils.GetSafeStreetPosition(searchAhead, 1, out Vector3 stPos, out float stH))
                    {
                        spotPos = stPos;
                        spotHeading = stH;
                    }
                    else
                    {
                        spotPos = World.GetNextPositionOnStreet(searchAhead);
                        spotHeading = player.Heading;
                    }
                }

                if (spotPos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(spotPos))
                {
                    Logger.Warn("[TrafficStopFeature] Could not find valid coordinates for test traffic stop.");
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

        public static void PurgeAll()
        {
            lock (_syncLock)
            {
                Logger.Info("[TrafficStopFeature] Purging all active roadside traffic stops.");
                PurgeAllInternal();
            }
        }

        public static int GetActiveCount()
        {
            lock (_syncLock)
            {
                _activeStops.RemoveAll(s => s == null || !s.IsValid());
                return _activeStops.Count;
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
            lock (_syncLock)
            {
                for (int i = _activeStops.Count - 1; i >= 0; i--)
                {
                    TrafficStopInstance stop = _activeStops[i];
                    if (stop == null)
                    {
                        _activeStops.RemoveAt(i);
                        continue;
                    }

                    if (!stop.IsValid())
                    {
                        Logger.Debug($"[TrafficStopFeature] Removing completed/invalid stop {stop.Callsign}.");
                        stop.Dispose(immediateDelete: false);
                        _activeStops.RemoveAt(i);
                        continue;
                    }

                    // Despawn distance watchdog (> 450m)
                    float dist = stop.GetDistanceTo(playerPos);
                    if (dist > Settings.DespawnDistance)
                    {
                        Logger.Debug($"[TrafficStopFeature] Stop {stop.Callsign} out of range ({dist:F0}m > {Settings.DespawnDistance:F0}m). Cleaning up.");
                        stop.Dispose(immediateDelete: false);
                        _activeStops.RemoveAt(i);
                        continue;
                    }

                    // Advance scenario state machine
                    stop.Update();

                    if (stop.IsCompleted)
                    {
                        stop.Dispose(immediateDelete: false);
                        _activeStops.RemoveAt(i);
                    }
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

            // Spawn attempt pacing: check every 20 seconds
            uint now = Game.GameTime;
            if (now - _lastSpawnAttemptTime < 20000 && currentCount > 0)
            {
                return;
            }
            _lastSpawnAttemptTime = now;

            // Search for curated shoulder spot 150m–400m from player
            if (ZoneSpawnData.FindBestPulloverSpot(playerPos, 150.0f, 400.0f, out Vector3 candidatePos, out float candidateHeading))
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
                        lock (_syncLock)
                        {
                            _activeStops.Add(stop);
                        }
                        Logger.Info($"[TrafficStopFeature] Ambient traffic stop {stop.Callsign} (Scenario {stop.Scenario}) spawned at {candidatePos} (Total: {_activeStops.Count}/{Settings.MaxTrafficStops}).");
                    }
                }
            }
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

            int scenario = _rand.Next(1, 4); // 1 = Citation, 2 = Pat-Down, 3 = K9 Search

            try
            {
                // Validate ground elevation
                Vector3 cruiserPos = shoulderPos;
                if (WorldUtils.GetAccurateGroundZ(cruiserPos, out float groundZ))
                {
                    cruiserPos.Z = groundZ + 0.35f;
                }

                // 1. Spawn Police Cruiser & Officer
                string policeModelName = ZoneSpawnData.GetDefaultPoliceVehicleForPosition(cruiserPos);
                Model policeVehModel = new Model(policeModelName);
                Model copPedModel = new Model("s_m_y_cop_01");

                policeVehModel.LoadAndWait();
                copPedModel.LoadAndWait();

                cruiser = new Vehicle(policeVehModel, cruiserPos, heading);
                policeVehModel.Dismiss();

                if (cruiser == null || !cruiser.Exists())
                {
                    copPedModel.Dismiss();
                    return null;
                }

                cruiser.IsPersistent = false;
                cruiser.IsEngineOn = true;
                WorldUtils.SecureEntityGround(cruiser);

                officer = new Ped(copPedModel, cruiserPos, heading);
                copPedModel.Dismiss();

                if (officer == null || !officer.Exists())
                {
                    cruiser.Delete();
                    return null;
                }

                officer.WarpIntoVehicle(cruiser, -1);
                officer.BlockPermanentEvents = true;

                if (Settings.EnableEupOutfits)
                {
                    EupService.ApplyTrafficVest(officer);
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
                Vector3 civPos = cruiserPos + cruiser.ForwardVector * 8.0f;
                if (WorldUtils.GetAccurateGroundZ(civPos, out float civGroundZ))
                {
                    civPos.Z = civGroundZ + 0.35f;
                }

                string civVehName = ZoneSpawnData.GetRandomCivilianVehicle();
                Model civVehModel = new Model(civVehName);
                Model civPedModel = new Model("a_m_y_business_01");

                civVehModel.LoadAndWait();
                civPedModel.LoadAndWait();

                civilianVehicle = new Vehicle(civVehModel, civPos, heading);
                civVehModel.Dismiss();

                if (civilianVehicle == null || !civilianVehicle.Exists())
                {
                    civPedModel.Dismiss();
                    officer.Delete();
                    cruiser.Delete();
                    return null;
                }

                civilianVehicle.IsPersistent = false;
                civilianVehicle.IsEngineOn = true;
                civilianVehicle.IndicatorLightsStatus = VehicleIndicatorLightsStatus.Both; // Hazard lights
                WorldUtils.SecureEntityGround(civilianVehicle);

                civilianDriver = new Ped(civPedModel, civPos, heading);
                civPedModel.Dismiss();

                if (civilianDriver == null || !civilianDriver.Exists())
                {
                    civilianVehicle.Delete();
                    officer.Delete();
                    cruiser.Delete();
                    return null;
                }

                civilianDriver.WarpIntoVehicle(civilianVehicle, -1);
                civilianDriver.BlockPermanentEvents = true;

                // 3. Scenario 3 K9 Setup: German Shepherd beside cruiser
                if (scenario == 3)
                {
                    try
                    {
                        Model k9Model = new Model("a_c_shepherd");
                        k9Model.LoadAndWait();

                        Vector3 k9SpawnPos = cruiserPos + cruiser.RightVector * 1.5f - cruiser.ForwardVector * 0.8f;
                        k9Dog = new Ped(k9Model, k9SpawnPos, heading);
                        k9Model.Dismiss();

                        if (k9Dog != null && k9Dog.Exists())
                        {
                            k9Dog.BlockPermanentEvents = true;
                            WorldUtils.SecureEntityGround(k9Dog);
                            k9Dog.Tasks.StandStill(10000);
                        }
                        else
                        {
                            scenario = 1; // Fallback to routine citation if K9 model fails
                        }
                    }
                    catch
                    {
                        scenario = 1;
                    }
                }

                // 4. Attach Yellow Traffic Stop Radar Blip
                blip = BlipService.AttachTrafficStopBlip(cruiser, callsign);

                return new TrafficStopInstance(cruiser, officer, civilianVehicle, civilianDriver, k9Dog, blip, callsign, scenario, cruiserPos);
            }
            catch (Exception ex)
            {
                Logger.Error($"[TrafficStopFeature] Exception creating traffic stop at {shoulderPos}", ex);

                if (blip != null) BlipService.SafeDeleteBlip(ref blip);
                if (k9Dog != null && k9Dog.Exists()) k9Dog.Delete();
                if (civilianDriver != null && civilianDriver.Exists()) civilianDriver.Delete();
                if (civilianVehicle != null && civilianVehicle.Exists()) civilianVehicle.Delete();
                if (officer != null && officer.Exists()) officer.Delete();
                if (cruiser != null && cruiser.Exists()) cruiser.Delete();

                return null;
            }
        }

        private static string GenerateCallsign()
        {
            string div = Divisions[_rand.Next(Divisions.Length)];
            int num = _callsignCounter++;
            if (_callsignCounter > 99) _callsignCounter = 30;
            return $"{div}-{num:D2}";
        }

        private static void PurgeAllInternal()
        {
            foreach (var stop in _activeStops)
            {
                if (stop != null)
                {
                    stop.Dispose(immediateDelete: true);
                }
            }
            _activeStops.Clear();
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
            public string Callsign { get; }
            public int Scenario { get; }
            public Vector3 OriginPosition { get; }
            public bool IsCompleted { get; private set; }

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

                // Reinforce silent lightbars to prevent audio siren chirps
                uint now = Game.GameTime;
                if (now - _lastSirenReinforceTime >= 500 && _state != StopState.ReleasingVehicles)
                {
                    _lastSirenReinforceTime = now;
                    NativeTasks.SetSilentSiren(Cruiser, true);
                }

                uint elapsed = now - _stateStartTime;

                switch (_state)
                {
                    case StopState.InitialExiting:
                        Officer.Tasks.LeaveVehicle(Cruiser, LeaveVehicleFlags.None);
                        _state = StopState.ApproachPassengerWindow;
                        _stateStartTime = now;
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
                        if (elapsed > 1500)
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
                        // Civilian merges into traffic first
                        if (!_civDeparted && elapsed >= 1500)
                        {
                            _civDeparted = true;
                            CivDriver.BlockPermanentEvents = false;
                            NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(CivVehicle, 5.0f);
                            CivDriver.Tasks.CruiseWithVehicle(CivVehicle, 18.0f, NativeTasks.CruisingFlags);
                        }

                        // Officer cruiser merges into traffic 3 seconds later
                        if (!_copDeparted && elapsed >= 4500)
                        {
                            _copDeparted = true;
                            Officer.BlockPermanentEvents = false;
                            NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(Cruiser, 6.0f);
                            Officer.Tasks.CruiseWithVehicle(Cruiser, 18.0f, NativeTasks.CruisingFlags);
                        }

                        // Complete stop after both depart cleanly
                        if (_civDeparted && _copDeparted && elapsed >= 10000)
                        {
                            IsCompleted = true;
                            _state = StopState.Completed;
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
                    propModel.LoadAndWait();

                    _activeProp = new Rage.Object(propModel, Officer.Position);
                    propModel.Dismiss();

                    if (_activeProp != null && _activeProp.Exists())
                    {
                        // Attach to left hand
                        int handBone = NativeFunction.Natives.GET_PED_BONE_INDEX<int>(Officer, 60309); // SKEL_L_Hand
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

                // 2. Clean up props
                CleanupProp();

                // 3. Dismiss or delete entities
                try
                {
                    if (K9Dog != null && K9Dog.Exists())
                    {
                        K9Dog.BlockPermanentEvents = false;
                        if (immediateDelete) K9Dog.Delete();
                        else K9Dog.Dismiss();
                    }

                    if (CivDriver != null && CivDriver.Exists())
                    {
                        CivDriver.BlockPermanentEvents = false;
                        if (immediateDelete) CivDriver.Delete();
                        else CivDriver.Dismiss();
                    }

                    if (CivVehicle != null && CivVehicle.Exists())
                    {
                        CivVehicle.IndicatorLightsStatus = VehicleIndicatorLightsStatus.Off;
                        if (immediateDelete) CivVehicle.Delete();
                        else CivVehicle.Dismiss();
                    }

                    if (Officer != null && Officer.Exists())
                    {
                        Officer.BlockPermanentEvents = false;
                        if (immediateDelete) Officer.Delete();
                        else Officer.Dismiss();
                    }

                    if (Cruiser != null && Cruiser.Exists())
                    {
                        NativeTasks.SetSilentSiren(Cruiser, false);
                        if (immediateDelete) Cruiser.Delete();
                        else Cruiser.Dismiss();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[TrafficStopFeature] Error disposing stop {Callsign}: {ex.Message}");
                }

                IsCompleted = true;
            }
        }
    }
}
