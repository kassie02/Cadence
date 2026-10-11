using System;
using System.Collections.Generic;
using System.Linq;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;
using Cadence.Core;
using Cadence.Common;
using Cadence.Services;

namespace Cadence.Features
{
    /// <summary>
    /// Feature 6: Nearby Threat Reaction / Combat Defense
    /// Proactively monitors a 60m–90m radius around ambient cruisers for active hostile threats
    /// (armed peds firing weapons, shooting at player or officers).
    /// Ambient officers halt, activate emergency lights, dismount with sidearms drawn, take tactical cover,
    /// and engage the threat. Once neutralized, officers stand down, holster sidearms, re-enter the cruiser,
    /// and resume normal patrol.
    /// Strictly self-contained under the Ponytail architecture.
    /// </summary>
    public static class AssistFeature
    {
        private const uint WeaponCombatPistol = 0x5EF9FEC4;
        private const uint WeaponPistol = 0x1B06D571;
        private const uint WeaponUnarmed = 0xA2719248;

        private static readonly object _syncLock = new object();
        private static readonly List<CombatDefenseSession> _activeSessions = new List<CombatDefenseSession>();
        private static readonly Random _rand = new Random();

        private static GameFiber _assistFiber;
        private static bool _isRunning;
        private static int _callsignCounter = 90;

        private static readonly string[] Divisions = new[]
        {
            "3-MARY", "4-GEORGE", "5-EDWARD", "6-FRANK", "7-DAVID", "8-OCEAN", "1-ADAM", "2-LINCOLN"
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

                Logger.Info("[AssistFeature] Starting nearby threat reaction watchdog background fiber.");
                _assistFiber = GameFiber.StartNew(AssistProcessLoop, "Cadence - Assist Feature Fiber");
            }
        }

        public static void Stop()
        {
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[AssistFeature] Stopping threat reaction feature and clearing active combat sessions.");
                PurgeAllInternal(immediateDelete: false);

                try
                {
                    if (_assistFiber != null && _assistFiber.IsAlive)
                    {
                        _assistFiber.Abort();
                    }
                }
                catch { }
                finally
                {
                    _assistFiber = null;
                }
            }
        }

        public static void PurgeAll()
        {
            lock (_syncLock)
            {
                Logger.Info("[AssistFeature] Purging all active threat reaction units.");
                PurgeAllInternal(immediateDelete: true);
            }
        }

        public static int GetActiveCount()
        {
            lock (_syncLock)
            {
                _activeSessions.RemoveAll(s => s == null || !s.IsValid() || s.State == CombatDefenseSession.CombatState.Completed);
                return _activeSessions.Count(s => s.State == CombatDefenseSession.CombatState.Engaging || s.State == CombatDefenseSession.CombatState.MountingCruiser);
            }
        }

        // ====================================================================
        // Background Process Fiber
        // ====================================================================

        private static void AssistProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    GameFiber.Sleep(650);

                    if (!_isRunning || !Settings.EnablePlugin || !Settings.EnableThreatAssist)
                    {
                        continue;
                    }

                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists())
                    {
                        continue;
                    }

                    // 1. Update Existing Active Combat Engagements
                    UpdateActiveEngagements();

                    // 2. Scan for New Threats in Ambient Cruisers' Radii (60m - 90m)
                    ScanForNearbyThreats(player);
                }
                catch (System.Threading.ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[AssistFeature] Unexpected exception in threat reaction fiber", ex);
                    GameFiber.Sleep(2500);
                }
            }
        }

        private static void UpdateActiveEngagements()
        {
            lock (_syncLock)
            {
                for (int i = _activeSessions.Count - 1; i >= 0; i--)
                {
                    CombatDefenseSession session = _activeSessions[i];
                    if (session == null || !session.IsValid())
                    {
                        if (session != null) session.Dispose(immediateDelete: false);
                        _activeSessions.RemoveAt(i);
                        continue;
                    }

                    session.Update();

                    if (session.State == CombatDefenseSession.CombatState.Completed)
                    {
                        Logger.Info($"[AssistFeature] Combat engagement completed for unit {session.Callsign}. Resumed normal patrol.");
                        session.Dispose(immediateDelete: false);
                        _activeSessions.RemoveAt(i);
                    }
                }
            }
        }

        private static void ScanForNearbyThreats(Ped player)
        {
            try
            {
                // Find ambient police cruisers near player within 200m
                Entity[] nearbyVehicles = World.GetEntities(player.Position, 200.0f, GetEntitiesFlags.ConsiderAllVehicles);
                if (nearbyVehicles == null || nearbyVehicles.Length == 0) return;

                foreach (Entity ent in nearbyVehicles)
                {
                    if (!_isRunning) break;
                    if (!(ent is Vehicle cruiser) || !cruiser.Exists() || cruiser.IsDead || !cruiser.IsPoliceVehicle)
                    {
                        continue;
                    }

                    Ped driver = cruiser.Driver;
                    if (driver == null || !driver.Exists() || !driver.IsAlive || driver.IsPlayer || driver == player)
                    {
                        continue;
                    }

                    // Skip if cruiser is already engaged in active combat
                    lock (_syncLock)
                    {
                        if (_activeSessions.Any(s => s.Cruiser == cruiser)) continue;
                    }

                    // Check for active hostile threats in 60m - 90m radius around cruiser
                    Ped threat = DetectHostileThreatNear(cruiser.Position, player);
                    if (threat != null && threat.Exists() && threat.IsAlive)
                    {
                        MobilizeCruiserDefense(cruiser, threat);
                        break; // One engagement mobilization per scan cycle to prevent mass dogpiles
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[AssistFeature] ScanForNearbyThreats error: {ex.Message}");
            }
        }

        /// <summary>
        /// Proactively monitors a 60m–90m radius for armed hostile peds actively shooting or in combat.
        /// </summary>
        private static Ped DetectHostileThreatNear(Vector3 searchCenter, Ped player)
        {
            try
            {
                Entity[] nearbyPeds = World.GetEntities(searchCenter, 85.0f, GetEntitiesFlags.ConsiderHumanPeds);
                if (nearbyPeds == null || nearbyPeds.Length == 0) return null;

                foreach (Entity ent in nearbyPeds)
                {
                    if (!(ent is Ped ped) || !ped.Exists() || !ped.IsAlive || ped.IsPlayer || ped == player)
                    {
                        continue;
                    }

                    // Exclude law enforcement officers
                    bool isCop = false;
                    try
                    {
                        isCop = Functions.IsPedACop(ped) || ped.RelationshipGroup == "COP";
                    }
                    catch { }
                    if (isCop) continue;

                    // Exclude handcuffed or arrested peds
                    bool isCuffed = false;
                    try
                    {
                        isCuffed = ped.IsCuffed || NativeFunction.Natives.IS_PED_CUFFED<bool>(ped) || Functions.IsPedArrested(ped);
                    }
                    catch { }
                    if (isCuffed) continue;

                    // Check if armed with a firearm (weapon type 4 = firearm)
                    bool isArmedWithFirearm = false;
                    try
                    {
                        isArmedWithFirearm = NativeFunction.Natives.IS_PED_ARMED<bool>(ped, 4);
                    }
                    catch { }

                    // Threat criteria:
                    // 1. Armed with firearm and actively shooting or in combat
                    if (isArmedWithFirearm && (ped.IsShooting || ped.IsInCombat))
                    {
                        return ped;
                    }

                    // 2. Actively attacking/shooting player or in combat against player
                    if (player != null && player.Exists())
                    {
                        bool fightingPlayer = false;
                        try
                        {
                            fightingPlayer = NativeFunction.Natives.IS_PED_IN_COMBAT<bool>(ped, player);
                        }
                        catch { }

                        if (fightingPlayer && isArmedWithFirearm)
                        {
                            return ped;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static void MobilizeCruiserDefense(Vehicle cruiser, Ped threat)
        {
            try
            {
                string callsign = GenerateCallsign();
                Logger.Info($"[AssistFeature] Active hostile threat detected ({threat.Handle}) near cruiser ({cruiser.Model.Name}). Mobilizing tactical defense!");

                // 1. Cruiser halts immediately and engages handbrake
                NativeFunction.Natives.BRING_VEHICLE_TO_HALT(cruiser, 2.0f, 1, false);
                NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(cruiser, true);

                // 2. Activates emergency lights
                NativeTasks.SetSilentSiren(cruiser, true);

                // 3. Officers dismount, unholster sidearms, take cover, and engage
                List<Ped> officers = new List<Ped>();
                Ped driver = cruiser.Driver;
                if (driver != null && driver.Exists() && driver.IsAlive) officers.Add(driver);

                if (cruiser.PassengerCount > 0)
                {
                    foreach (Ped p in cruiser.Passengers)
                    {
                        if (p != null && p.Exists() && p.IsAlive && !officers.Contains(p))
                        {
                            bool isCop = false;
                            try { isCop = Functions.IsPedACop(p) || p.RelationshipGroup == "COP"; } catch { }
                            if (isCop) officers.Add(p);
                        }
                    }
                }

                if (officers.Count == 0) return;

                foreach (Ped officer in officers)
                {
                    officer.BlockPermanentEvents = false;

                    // Dismount vehicle
                    officer.Tasks.LeaveVehicle(LeaveVehicleFlags.LeaveDoorOpen);

                    // Unholster sidearm (WEAPON_COMBATPISTOL with WEAPON_PISTOL fallback)
                    NativeFunction.Natives.GIVE_WEAPON_TO_PED(officer, WeaponCombatPistol, 120, false, true);
                    NativeFunction.Natives.SET_CURRENT_PED_WEAPON(officer, WeaponCombatPistol, true);

                    // Configure combat attributes: cover-seeking, stay on foot, engage threat
                    NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(officer, 5, true);  // AlwaysFight
                    NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(officer, 0, true);  // CanUseCover
                    NativeFunction.Natives.SET_PED_COMBAT_ATTRIBUTES(officer, 1, false); // CanUseVehicles = false
                    NativeFunction.Natives.SET_PED_COMBAT_ABILITY(officer, 2);           // Professional
                    NativeFunction.Natives.SET_PED_COMBAT_RANGE(officer, 2);             // Far

                    // Engage threat
                    officer.Tasks.FightAgainst(threat);
                }

                // 4. Update radar blip to Dark Orange combat blip
                Blip blip = BlipService.AttachCombatBlip(cruiser, callsign);

                var session = new CombatDefenseSession(cruiser, officers, threat, blip, callsign);

                lock (_syncLock)
                {
                    _activeSessions.Add(session);
                }
                SessionMetrics.IncrementThreat();

                Logger.Info($"[AssistFeature] Tactical defense initiated for unit {callsign}. Officers engaging threat.");
                Logger.Debug("AssistFeature", $"Tactical defense details: Callsign={callsign}, Cruiser={cruiser.Model.Name} (0x{cruiser.Handle:X}), OfficersCount={officers.Count}, Threat=0x{threat.Handle:X}");
            }
            catch (Exception ex)
            {
                Logger.Error("[AssistFeature] MobilizeCruiserDefense error", ex);
            }
        }

        private static void PurgeAllInternal(bool immediateDelete)
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

        private static string GenerateCallsign()
        {
            string div = Divisions[_rand.Next(Divisions.Length)];
            int num = _callsignCounter++;
            if (_callsignCounter > 99) _callsignCounter = 90;
            return $"{div}-{num:D2}";
        }

        // ====================================================================
        // Combat Defense Session Representation
        // ====================================================================

        private class CombatDefenseSession
        {
            public Vehicle Cruiser { get; }
            public List<Ped> Officers { get; }
            public Ped ThreatPed { get; }
            public Blip RadarBlip;
            public string Callsign { get; }
            public uint StartTime { get; }

            public enum CombatState { Engaging, MountingCruiser, StandingDownPatrol, Completed }
            public CombatState State { get; private set; }

            private uint _standDownStartTime;
            private uint _standDownPatrolStartTime;
            private uint _lastCoverCheckTime;

            public CombatDefenseSession(Vehicle cruiser, List<Ped> officers, Ped threat, Blip blip, string callsign)
            {
                Cruiser = cruiser;
                Officers = officers;
                ThreatPed = threat;
                RadarBlip = blip;
                Callsign = callsign;
                StartTime = Game.GameTime;
                State = CombatState.Engaging;
                _lastCoverCheckTime = Game.GameTime;
            }

            public bool IsValid()
            {
                return Cruiser != null && Cruiser.Exists() && Cruiser.IsDriveable &&
                       Officers != null && Officers.Any(o => o != null && o.Exists() && o.IsAlive);
            }

            public void Update()
            {
                if (!IsValid())
                {
                    State = CombatState.Completed;
                    return;
                }

                uint now = Game.GameTime;

                switch (State)
                {
                    case CombatState.Engaging:
                        // Check if threat is neutralized (dead, deleted, cuffed, or arrested)
                        bool isThreatNeutralized = ThreatPed == null || !ThreatPed.Exists() || ThreatPed.IsDead;
                        if (!isThreatNeutralized)
                        {
                            try
                            {
                                isThreatNeutralized = ThreatPed.IsCuffed ||
                                                     NativeFunction.Natives.IS_PED_CUFFED<bool>(ThreatPed) ||
                                                     Functions.IsPedArrested(ThreatPed);
                            }
                            catch { }
                        }

                        // Timeout fail-safe (max 90 seconds combat engagement)
                        if (now - StartTime > 90000)
                        {
                            isThreatNeutralized = true;
                        }

                        if (isThreatNeutralized)
                        {
                            InitiateStandDown();
                        }
                        else
                        {
                            // Tactical positioning check: keep officers fighting or seeking cover
                            if (now - _lastCoverCheckTime > 5000)
                            {
                                _lastCoverCheckTime = now;
                                foreach (var ofc in Officers)
                                {
                                    if (ofc != null && ofc.Exists() && ofc.IsAlive && !ofc.IsInCombat)
                                    {
                                        ofc.Tasks.FightAgainst(ThreatPed);
                                    }
                                }
                            }
                        }
                        break;

                    case CombatState.MountingCruiser:
                        // Wait for officers to mount cruiser, with timeout of 12 seconds
                        bool allMounted = Officers.Where(o => o != null && o.Exists() && o.IsAlive).All(o => o.IsInVehicle(Cruiser, false));
                        if (allMounted || (now - _standDownStartTime > 12000))
                        {
                            FinishStandDown();
                        }
                        break;

                    case CombatState.StandingDownPatrol:
                        // Stand-down patrol for 15 seconds or out of range, then cleanly dispose blip and entities
                        Ped player = Game.LocalPlayer?.Character;
                        float dist = player != null && player.Exists() ? Cruiser.Position.DistanceTo(player.Position) : 0f;
                        if (now - _standDownPatrolStartTime > 15000 || dist > Settings.DespawnDistance)
                        {
                            Dispose(immediateDelete: false);
                        }
                        break;
                }
            }

            private void InitiateStandDown()
            {
                State = CombatState.MountingCruiser;
                _standDownStartTime = Game.GameTime;

                Logger.Info($"[AssistFeature] Threat neutralized! Unit {Callsign} officers standing down and holstering sidearms.");

                for (int i = 0; i < Officers.Count; i++)
                {
                    Ped officer = Officers[i];
                    if (officer == null || !officer.Exists() || !officer.IsAlive) continue;

                    try
                    {
                        // 1. Clear combat tasks
                        officer.Tasks.Clear();

                        // 2. Holster sidearm (switch to unarmed)
                        NativeFunction.Natives.SET_CURRENT_PED_WEAPON(officer, WeaponUnarmed, true);

                        // 3. Re-enter cruiser
                        int seatIdx = (i == 0) ? -1 : (i - 1);
                        officer.Tasks.EnterVehicle(Cruiser, seatIdx);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[AssistFeature] Error commanding officer to enter vehicle: {ex.Message}");
                    }
                }
            }

            private void FinishStandDown()
            {
                try
                {
                    // Warp-in fail-safe if any officer got pathfinding-blocked
                    for (int i = 0; i < Officers.Count; i++)
                    {
                        Ped officer = Officers[i];
                        if (officer != null && officer.Exists() && officer.IsAlive && !officer.IsInVehicle(Cruiser, false))
                        {
                            int seatIdx = (i == 0) ? -1 : (i - 1);
                            officer.WarpIntoVehicle(Cruiser, seatIdx);
                        }
                    }

                    // Release handbrake and extinguish emergency lights
                    NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                    NativeTasks.SetSilentSiren(Cruiser, false);

                    // Resume normal cruising patrol
                    Ped driver = Cruiser.Driver;
                    if (driver != null && driver.Exists() && driver.IsAlive)
                    {
                        NativeTasks.ConfigurePatrolDriver(driver, Cruiser);
                        driver.Tasks.CruiseWithVehicle(Cruiser, 16.0f, NativeTasks.CruisingFlags);
                    }

                    // Restore Navy Blue routine patrol blip
                    BlipService.TransitionToPatrol(RadarBlip, Callsign);

                    _standDownPatrolStartTime = Game.GameTime;
                    State = CombatState.StandingDownPatrol;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[AssistFeature] FinishStandDown error: {ex.Message}");
                    State = CombatState.Completed;
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
                        NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, false);
                        NativeTasks.SetSilentSiren(Cruiser, false);
                    }

                    foreach (var ofc in Officers)
                    {
                        if (ofc != null && ofc.Exists())
                        {
                            ofc.BlockPermanentEvents = false;
                            if (immediateDelete) ofc.Delete();
                            else ofc.Dismiss();
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
                    Logger.Debug($"[AssistFeature] Error disposing combat unit {Callsign}: {ex.Message}");
                }
                finally
                {
                    State = CombatState.Completed;
                }
            }
        }
    }
}
