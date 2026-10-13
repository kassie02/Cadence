using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Windows.Forms;
using Rage;
using Rage.Native;
using RAGENativeUI;
using RAGENativeUI.Elements;
using LSPD_First_Response.Mod.API;
using Cadence.Core;
using Cadence.Common;
using Cadence.Services;
using Cadence.Data;

namespace Cadence.Features
{
    /// <summary>
    /// Ambient Backup & Response Systems for Cadence.
    /// Provides 1:1 Ultimate Backup compatibility (Panic Button, Group Backup, Lean Backup Menu)
    /// backed by an Ambient-First Commandeering Architecture and full delete-friendly Ponytail decoupling.
    /// </summary>
    public static class BackupFeature
    {
        private const uint WeaponCombatPistol = 0x5EF9FEC4;
        private const uint WeaponCarbineRifle = 0x83BF0278;
        private const uint WeaponUnarmed = 0xA2719248;

        public enum BackupType
        {
            LocalPatrolCode2,
            LocalPatrolCode3,
            StatePatrolCode2,
            StatePatrolCode3,
            AmbulanceCode2,
            AmbulanceCode3,
            FireDeptCode2,
            FireDeptCode3,
            GroupBackup,
            PanicButton,
            LocalPursuit,
            StatePursuit,
            GroupPursuit
        }

        private static readonly object _syncLock = new object();
        private static readonly List<BackupUnitSession> _activeUnits = new List<BackupUnitSession>();
        private static readonly HashSet<Vehicle> _reservedCruisers = new HashSet<Vehicle>();
        private static readonly Random _rand = new Random();

        private static GameFiber _backupFiber;
        private static bool _isRunning;
        private static int _callsignCounter = 50;
        private static uint _dismissKeyDownTime;
        private static uint _lastPanicTriggerTime;
        private static uint _lastGroupTriggerTime;

        // RAGENativeUI Lean Backup Menu (UB Class74 Architecture)
        private static MenuPool _menuPool;
        private static UIMenu _backupMenu;
        private static readonly object[] NormalModes = new object[] { "Code 3 (Emergency)", "Code 2 (Silent)" };
        private static readonly object[] PursuitModes = new object[] { "Pursuit Backup", "Code 3 (Emergency)", "Code 2 (Silent)" };
        private static UIMenuListItem _responseModeNormal;
        private static UIMenuListItem _responseModePursuit;

        // Code 3 & Code 2 Items
        private static UIMenuItem _itemLocalCode2;
        private static UIMenuItem _itemLocalCode3;
        private static UIMenuItem _itemStateCode2;
        private static UIMenuItem _itemStateCode3;
        private static UIMenuItem _itemAmbulanceCode2;
        private static UIMenuItem _itemAmbulanceCode3;
        private static UIMenuItem _itemFireCode2;
        private static UIMenuItem _itemFireCode3;
        private static UIMenuItem _itemGroupBackup;
        private static UIMenuItem _itemPanicButton;

        // Pursuit Backup Items
        private static UIMenuItem _itemLocalPursuit;
        private static UIMenuItem _itemStatePursuit;
        private static UIMenuItem _itemGroupPursuit;

        private static UIMenuItem _itemDismissAll;

        private static readonly string[] Divisions = new[]
        {
            "LINCOLN", "ADAM", "MARY", "GEORGE", "EDWARD", "FRANK", "DAVID", "OCEAN"
        };

        // ====================================================================
        // Public API Contract (Ponytail Strand Interface)
        // ====================================================================

        public static void Start()
        {
            lock (_syncLock)
            {
                if (_isRunning) return;
                _isRunning = true;

                Logger.Info("[BackupFeature] Initializing Ambient Backup & Response Systems.");

                BuildBackupMenu();

                _backupFiber = GameFiber.StartNew(BackupProcessLoop, "Cadence - Backup Feature Fiber");
            }
        }

        public static void Stop()
        {
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[BackupFeature] Stopping Backup feature and dismissing active units.");

                CloseMenu();

                PurgeAllInternal(immediateDelete: false);

                try
                {
                    if (_backupFiber != null && _backupFiber.IsAlive)
                    {
                        _backupFiber.Abort();
                    }
                }
                catch { }
                finally
                {
                    _backupFiber = null;
                }
            }
        }

        public static void PurgeAll()
        {
            lock (_syncLock)
            {
                Logger.Info("[BackupFeature] Purging all active backup units.");
                PurgeAllInternal(immediateDelete: true);
            }
        }

        public static int GetActiveCount()
        {
            lock (_syncLock)
            {
                _activeUnits.RemoveAll(u => u == null || !u.IsValid() || u.State == BackupUnitSession.UnitState.Completed);
                return _activeUnits.Count;
            }
        }

        public static bool TriggerPanicButton()
        {
            uint now = Game.GameTime;
            if (now - _lastPanicTriggerTime < 2500)
            {
                Logger.Debug("[BackupFeature] Panic button debounced (already triggered within 2.5s).");
                return false;
            }
            _lastPanicTriggerTime = now;

            RequestBackup(BackupType.PanicButton);
            return true;
        }

        public static bool TriggerGroupBackup()
        {
            uint now = Game.GameTime;
            if (now - _lastGroupTriggerTime < 2500)
            {
                Logger.Debug("[BackupFeature] Group backup debounced (already triggered within 2.5s).");
                return false;
            }
            _lastGroupTriggerTime = now;

            RequestBackup(BackupType.GroupBackup);
            return true;
        }

        public static void DismissAll()
        {
            lock (_syncLock)
            {
                if (_activeUnits.Count > 0)
                {
                    Logger.Info($"[BackupFeature] Dismissing all active backup units ({_activeUnits.Count}).");

                    foreach (var unit in _activeUnits)
                    {
                        if (unit != null && unit.IsValid())
                        {
                            unit.Dismiss();
                        }
                    }

                    Game.DisplayNotification("~b~Backup units~s~ dismissed. Returning to routine patrol.");
                }

                PursuitFeature.DismissStandbyUnits();
            }
        }

        // ====================================================================
        // Background Process Fiber
        // ====================================================================

        private static void BackupProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
                    GameFiber.Sleep(15);

                    if (!_isRunning || !Settings.EnablePlugin || !Settings.EnableBackup)
                    {
                        continue;
                    }

                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists())
                    {
                        continue;
                    }

                    // 1. Process Menu Rendering & UI Interaction
                    if (_menuPool != null && _backupMenu != null && _backupMenu.Visible)
                    {
                        _menuPool.ProcessMenus();
                    }

                    // 2. Process Keybinds (Panic, Group, Menu, Dismiss)
                    ProcessKeybinds(player);

                    // 3. Update Active Backup Unit State Machines
                    UpdateActiveUnits(player);
                }
                catch (System.Threading.ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[BackupFeature] Unexpected exception in backup fiber", ex);
                    GameFiber.Sleep(1000);
                }
            }
        }

        private static void ProcessKeybinds(Ped player)
        {
            bool inputBlocked = Game.Console.IsOpen || Game.IsPaused;
            if (inputBlocked) return;

            // 1. Panic Button Keybind (Default: LControlKey + Z)
            if (Settings.PanicButtonKey != Keys.None && InputHelper.IsKeyComboPressed(Settings.PanicButtonKey, Settings.PanicButtonModifierKey))
            {
                TriggerPanicButton();
                return;
            }

            // 2. Group Backup Keybind (Default: LControlKey + C)
            if (Settings.GroupBackupKey != Keys.None && InputHelper.IsKeyComboPressed(Settings.GroupBackupKey, Settings.GroupBackupModifierKey))
            {
                TriggerGroupBackup();
                return;
            }

            // 3. Lean Backup Menu Keybind (Default: X + PageDown)
            if (Settings.BackupMenuKey != Keys.None && InputHelper.IsKeyComboPressed(Settings.BackupMenuKey, Settings.BackupMenuModifierKey))
            {
                ToggleMenu();
                return;
            }

            // Suppress INPUT_COVER if any keybind uses Q as a modifier or primary key
            InputHelper.SuppressCoverIfQHeld(Settings.PanicButtonKey, Settings.PanicButtonModifierKey);
            InputHelper.SuppressCoverIfQHeld(Settings.GroupBackupKey, Settings.GroupBackupModifierKey);
            InputHelper.SuppressCoverIfQHeld(Settings.BackupMenuKey, Settings.BackupMenuModifierKey);

            // 4. Dismiss Backup Keybind (Default: Enter held for ~1.2s or pressed when not typing)
            if (Settings.DismissBackupKey != Keys.None)
            {
                int activeCount;
                lock (_syncLock) { activeCount = _activeUnits.Count; }

                if (activeCount > 0 && (_backupMenu == null || !_backupMenu.Visible))
                {
                    if (Game.IsKeyDownRightNow(Settings.DismissBackupKey))
                    {
                        if (_dismissKeyDownTime == 0)
                        {
                            _dismissKeyDownTime = Game.GameTime;
                        }
                        else if (Game.GameTime - _dismissKeyDownTime >= 1200)
                        {
                            _dismissKeyDownTime = 0;
                            DismissAll();
                        }
                    }
                    else
                    {
                        _dismissKeyDownTime = 0;
                    }
                }
                else
                {
                    _dismissKeyDownTime = 0;
                }
            }
        }



        private static void UpdateActiveUnits(Ped player)
        {
            Vector3 playerPos = player.Position;

            lock (_syncLock)
            {
                for (int i = _activeUnits.Count - 1; i >= 0; i--)
                {
                    BackupUnitSession unit = _activeUnits[i];
                    if (unit == null || !unit.IsValid())
                    {
                        if (unit != null) unit.Dispose(immediateDelete: false);
                        _activeUnits.RemoveAt(i);
                        continue;
                    }

                    unit.Update(player, playerPos);

                    if (unit.State == BackupUnitSession.UnitState.Completed)
                    {
                        unit.Dispose(immediateDelete: false);
                        _activeUnits.RemoveAt(i);
                    }
                }
            }
        }

        private static void PurgeAllInternal(bool immediateDelete)
        {
            foreach (var unit in _activeUnits)
            {
                if (unit != null)
                {
                    unit.Dispose(immediateDelete);
                }
            }
            _activeUnits.Clear();
        }

        // ====================================================================
        // Lean Backup Menu Builder & Dispatchers
        // ====================================================================

        private static void BuildBackupMenu()
        {
            try
            {
                _menuPool = new MenuPool();
                _backupMenu = new UIMenu("Cadence", "~b~Ambient Backup Menu~s~");
                _backupMenu.MouseControlsEnabled = false;
                _menuPool.Add(_backupMenu);

                _responseModeNormal = new UIMenuListItem("Response Mode", "Select backup authorization level.", NormalModes);
                _responseModePursuit = new UIMenuListItem("Response Mode", "Select backup authorization level.", PursuitModes);

                // Code 3 Actions
                _itemLocalCode3 = new UIMenuItem("Local Patrol (Code 3)", "Dispatches local police cruiser with sirens and flashing lights.");
                _itemStateCode3 = new UIMenuItem("State Patrol (Code 3)", "Dispatches highway/state patrol cruiser with full emergency sirens.");
                _itemGroupBackup = new UIMenuItem("Group Backup (Code 3)", "Requests multi-unit tactical reinforcement to your location.");
                _itemPanicButton = new UIMenuItem("Panic Button (Code Red)", "High-priority distress signal with maximum agency response.");
                _itemAmbulanceCode3 = new UIMenuItem("Ambulance (Code 3)", "Dispatches paramedic ambulance with sirens and lightbars.");
                _itemFireCode3 = new UIMenuItem("Fire Dept (Code 3)", "Dispatches fire department engine with sirens and lightbars.");

                // Code 2 Actions
                _itemLocalCode2 = new UIMenuItem("Local Patrol (Code 2)", "Dispatches local police cruiser with silent lightbars.");
                _itemStateCode2 = new UIMenuItem("State Patrol (Code 2)", "Dispatches highway/state patrol cruiser with silent lightbars.");
                _itemAmbulanceCode2 = new UIMenuItem("Ambulance (Code 2)", "Dispatches paramedic ambulance with silent emergency lightbars.");
                _itemFireCode2 = new UIMenuItem("Fire Dept (Code 2)", "Dispatches fire department engine with silent lightbars.");

                // Pursuit Actions
                _itemLocalPursuit = new UIMenuItem("Local Pursuit Interceptor", "Dispatches local police cruiser directly into the active pursuit.");
                _itemStatePursuit = new UIMenuItem("State Pursuit Interceptor", "Dispatches high-speed state interceptor into the active chase.");
                _itemGroupPursuit = new UIMenuItem("Group Pursuit Backup", "Requests multiple tactical cruisers to box in and join the pursuit.");

                // Common
                _itemDismissAll = new UIMenuItem("Dismiss All", "Releases all active backup units and returns them to routine patrol.");

                _backupMenu.OnListChange += (sender, listItem, newIndex) =>
                {
                    if (listItem == _responseModeNormal || listItem == _responseModePursuit)
                    {
                        string selectedMode = listItem.Collection[newIndex].ToString();
                        RebuildActionsForMode(listItem, selectedMode);
                    }
                };

                _backupMenu.OnItemSelect += (sender, item, index) =>
                {
                    if (item == _responseModeNormal || item == _responseModePursuit)
                    {
                        return;
                    }

                    _backupMenu.Visible = false;

                    if (item == _itemLocalCode3) RequestBackup(BackupType.LocalPatrolCode3);
                    else if (item == _itemStateCode3) RequestBackup(BackupType.StatePatrolCode3);
                    else if (item == _itemGroupBackup) RequestBackup(BackupType.GroupBackup);
                    else if (item == _itemPanicButton) RequestBackup(BackupType.PanicButton);
                    else if (item == _itemAmbulanceCode3) RequestBackup(BackupType.AmbulanceCode3);
                    else if (item == _itemFireCode3) RequestBackup(BackupType.FireDeptCode3);
                    else if (item == _itemLocalCode2) RequestBackup(BackupType.LocalPatrolCode2);
                    else if (item == _itemStateCode2) RequestBackup(BackupType.StatePatrolCode2);
                    else if (item == _itemAmbulanceCode2) RequestBackup(BackupType.AmbulanceCode2);
                    else if (item == _itemFireCode2) RequestBackup(BackupType.FireDeptCode2);
                    else if (item == _itemLocalPursuit) RequestBackup(BackupType.LocalPursuit);
                    else if (item == _itemStatePursuit) RequestBackup(BackupType.StatePursuit);
                    else if (item == _itemGroupPursuit) RequestBackup(BackupType.GroupPursuit);
                    else if (item == _itemDismissAll) DismissAll();
                };

                Logger.Info("[BackupFeature] Lean Backup Menu created successfully (UB Class74 parity).");
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error building Lean Backup Menu", ex);
            }
        }

        private static void RebuildActionsForMode(UIMenuListItem modeItem, string selectedMode)
        {
            try
            {
                _backupMenu.Clear();
                _backupMenu.AddItem(modeItem);

                if (selectedMode == "Pursuit Backup")
                {
                    _backupMenu.AddItem(_itemLocalPursuit);
                    _backupMenu.AddItem(_itemStatePursuit);
                    _backupMenu.AddItem(_itemGroupPursuit);
                    _backupMenu.AddItem(_itemPanicButton);
                    _backupMenu.AddItem(_itemDismissAll);
                }
                else if (selectedMode == "Code 2 (Silent)")
                {
                    _backupMenu.AddItem(_itemLocalCode2);
                    _backupMenu.AddItem(_itemStateCode2);
                    _backupMenu.AddItem(_itemAmbulanceCode2);
                    _backupMenu.AddItem(_itemFireCode2);
                    _backupMenu.AddItem(_itemDismissAll);
                }
                else // "Code 3 (Emergency)"
                {
                    _backupMenu.AddItem(_itemLocalCode3);
                    _backupMenu.AddItem(_itemStateCode3);
                    _backupMenu.AddItem(_itemGroupBackup);
                    _backupMenu.AddItem(_itemPanicButton);
                    _backupMenu.AddItem(_itemAmbulanceCode3);
                    _backupMenu.AddItem(_itemFireCode3);
                    _backupMenu.AddItem(_itemDismissAll);
                }

                _backupMenu.RefreshIndex();
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] RebuildActionsForMode error", ex);
            }
        }

        public static void ToggleMenu()
        {
            if (_backupMenu == null) return;

            if (_backupMenu.Visible)
            {
                _backupMenu.Visible = false;
                return;
            }

            if (Game.IsPaused) return;

            LHandle activePursuit = null;
            try { activePursuit = Functions.GetActivePursuit(); } catch { }
            bool inPursuit = activePursuit != null && Functions.IsPursuitStillRunning(activePursuit);

            UIMenuListItem modeItem = inPursuit ? _responseModePursuit : _responseModeNormal;
            if (modeItem.Index >= modeItem.Collection.Count)
            {
                modeItem.Index = 0;
            }
            string selectedMode = modeItem.Collection[modeItem.Index].ToString();
            RebuildActionsForMode(modeItem, selectedMode);

            _backupMenu.Visible = true;
        }

        public static void CloseMenu()
        {
            if (_backupMenu != null)
            {
                _backupMenu.Visible = false;
            }
        }

        // ====================================================================
        // Backup Dispatch Orchestrator & Ultimate Backup Parity
        // ====================================================================

        public static void RequestBackup(BackupType type)
        {
            GameFiber.StartNew(() =>
            {
                try
                {
                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists()) return;
                    Vector3 playerPos = player.Position;

                    PlayPlayerRadioAnimation();

                    switch (type)
                    {
                        case BackupType.PanicButton:
                            Game.DisplayNotification("Requesting ~r~Panic Backup~s~ to ~g~Dispatch~s~");
                            Game.DisplayHelp("~r~Panic Backup Units~s~ are on the way. ~g~Please wait~s~", 4000);
                            PlayPanicButtonSound();
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);

                            DispatchUnitsSequence(type, Settings.PanicButtonUnits, isCode3: true);
                            break;

                        case BackupType.GroupBackup:
                            Game.DisplayNotification("Requesting ~y~Group Backup~s~ to ~g~Dispatch~s~");
                            Game.DisplayHelp("~y~Group Backup Units~s~ are on the way. ~g~Please wait~s~", 4000);
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);
                            DispatchUnitsSequence(type, Settings.GroupBackupUnits, Settings.GroupBackupCode3);
                            break;

                        case BackupType.LocalPatrolCode2:
                            Game.DisplayNotification("Requesting ~y~Code 2~s~ Backup to ~g~Dispatch~s~");
                            Game.DisplayHelp("~y~Code 2 Backup Unit~s~ is on the way. ~g~Please wait~s~", 4000);
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);
                            DispatchSingleUnit(type, isCode3: false);
                            break;

                        case BackupType.LocalPatrolCode3:
                            Game.DisplayNotification("Requesting ~r~Code 3~s~ Backup to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);
                            DispatchSingleUnit(type, isCode3: true);
                            break;

                        case BackupType.StatePatrolCode2:
                            Game.DisplayNotification("Requesting ~y~State Patrol (Code 2)~s~ Backup to ~g~Dispatch~s~");
                            Game.DisplayHelp("~y~State Patrol Unit~s~ is on the way. ~g~Please wait~s~", 4000);
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);
                            DispatchSingleUnit(type, isCode3: false);
                            break;

                        case BackupType.StatePatrolCode3:
                            Game.DisplayNotification("Requesting ~r~State Patrol (Code 3)~s~ Backup to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);
                            DispatchSingleUnit(type, isCode3: true);
                            break;

                        case BackupType.AmbulanceCode2:
                            Game.DisplayNotification("Requesting ~g~Ambulance (EMS)~s~ Backup to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("AMBULANCE ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION", playerPos);
                            DispatchSingleUnit(type, isCode3: false);
                            break;

                        case BackupType.AmbulanceCode3:
                            Game.DisplayNotification("Requesting ~g~Ambulance (EMS)~s~ Backup to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("AMBULANCE ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION", playerPos);
                            DispatchSingleUnit(type, isCode3: true);
                            break;

                        case BackupType.FireDeptCode2:
                            Game.DisplayNotification("Requesting ~r~Fire Department~s~ Backup to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("FIRETRUCK ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION", playerPos);
                            DispatchSingleUnit(type, isCode3: false);
                            break;

                        case BackupType.FireDeptCode3:
                            Game.DisplayNotification("Requesting ~r~Fire Department~s~ Backup to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("FIRETRUCK ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION", playerPos);
                            DispatchSingleUnit(type, isCode3: true);
                            break;

                        case BackupType.LocalPursuit:
                            Game.DisplayNotification("Requesting ~r~Local Pursuit Interceptor~s~ to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);
                            PursuitFeature.RequestPursuitInterceptor(isHighway: false);
                            break;

                        case BackupType.StatePursuit:
                            Game.DisplayNotification("Requesting ~r~State Pursuit Interceptor~s~ to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);
                            PursuitFeature.RequestPursuitInterceptor(isHighway: true);
                            break;

                        case BackupType.GroupPursuit:
                            Game.DisplayNotification("Requesting ~r~Group Pursuit Backup~s~ to ~g~Dispatch~s~");
                            Functions.PlayScannerAudioUsingPosition("ASSISTANCE_REQUIRED IN_OR_ON_POSITION", playerPos);
                            PursuitFeature.RequestGroupPursuit(Settings.GroupBackupUnits);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[BackupFeature] Error processing RequestBackup({type})", ex);
                }
            }, "Cadence - Backup Request Dispatch Fiber");
        }

        private static void DispatchSingleUnit(BackupType type, bool isCode3)
        {
            Ped player = Game.LocalPlayer?.Character;
            if (player == null || !player.Exists()) return;
            Vector3 playerPos = player.Position;

            BackupUnitSession unit = ResolveOrSpawnUnit(type, playerPos, isCode3);
            if (unit != null && unit.IsValid())
            {
                lock (_syncLock)
                {
                    _activeUnits.Add(unit);
                }
                Logger.Info($"[BackupFeature] Dispatched single {type} unit {unit.Callsign} (Code 3: {isCode3}).");
            }
        }

        private static void DispatchUnitsSequence(BackupType type, int count, bool isCode3)
        {
            Ped player = Game.LocalPlayer?.Character;
            if (player == null || !player.Exists()) return;
            Vector3 playerPos = player.Position;

            int dispatched = 0;
            for (int i = 0; i < count; i++)
            {
                BackupUnitSession unit = ResolveOrSpawnUnit(type, playerPos, isCode3, unitIndex: i);
                if (unit != null && unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        _activeUnits.Add(unit);
                    }
                    dispatched++;
                    Logger.Info($"[BackupFeature] Dispatched unit {dispatched}/{count} ({unit.Callsign}) for {type}.");
                }

                // 1500ms pacing delay between multiple unit dispatches to prevent vehicle stacking
                if (i < count - 1)
                {
                    GameFiber.Sleep(1500);
                }
            }
        }

        // ====================================================================
        // Ambient-First Commandeering Architecture with Fallback Spawner
        // ====================================================================

        private static BackupUnitSession ResolveOrSpawnUnit(BackupType type, Vector3 playerPos, bool isCode3, int unitIndex = 0)
        {
            bool isAmbulance = type == BackupType.AmbulanceCode2 || type == BackupType.AmbulanceCode3;
            bool isFire = type == BackupType.FireDeptCode2 || type == BackupType.FireDeptCode3;

            // Step 1: Ambient-First Commandeering Query within 350m (0m minimum for Panic/Group)
            if (Settings.AllowAmbientCommandeer)
            {
                Vehicle candidate = FindCandidateAmbientVehicle(playerPos, type, isAmbulance, isFire);
                if (candidate != null && candidate.Exists())
                {
                    lock (_syncLock)
                    {
                        _reservedCruisers.Add(candidate);
                    }

                    try
                    {
                        BackupUnitSession commandeered = CommandeerAmbientVehicle(candidate, playerPos, type, isCode3);
                        if (commandeered != null && commandeered.IsValid())
                        {
                            Logger.Info($"[BackupFeature] Commandeered ambient vehicle {candidate.Model.Name} (0x{candidate.Handle:X}) for {type}.");
                            return commandeered;
                        }
                    }
                    finally
                    {
                        lock (_syncLock)
                        {
                            _reservedCruisers.Remove(candidate);
                        }
                    }
                }
            }

            // Step 2: Fallback Spawner if ambient candidate not found or not permitted
            if (Settings.AllowFallbackSpawning)
            {
                return SpawnFallbackBackupUnit(playerPos, type, isCode3, unitIndex);
            }

            Logger.Warn($"[BackupFeature] No ambient unit found and FallbackSpawning is disabled for {type}.");
            return null;
        }

        private static Vehicle FindCandidateAmbientVehicle(Vector3 playerPos, BackupType type, bool isAmbulance, bool isFire)
        {
            try
            {
                Ped player = Game.LocalPlayer?.Character;
                float maxSearchRadius = 350.0f;

                // Collect candidate cruisers from both Cadence active features and the surrounding GTA world
                var candidatePool = new HashSet<Vehicle>();

                // 1. Cadence Feature Cruisers (ambient patrol, stationary, traffic stops, emergency runs)
                if (!isAmbulance && !isFire)
                {
                    foreach (var v in PatrolFeature.GetActiveCruisers())
                    {
                        if (v != null && v.Exists() && !v.IsDead && v.IsDriveable) candidatePool.Add(v);
                    }
                    foreach (var v in StationaryFeature.GetActiveCruisers())
                    {
                        if (v != null && v.Exists() && !v.IsDead && v.IsDriveable) candidatePool.Add(v);
                    }
                    foreach (var v in TrafficStopFeature.GetActiveCruisers())
                    {
                        if (v != null && v.Exists() && !v.IsDead && v.IsDriveable) candidatePool.Add(v);
                    }
                    foreach (var v in ResponseFeature.GetActiveCruisers())
                    {
                        if (v != null && v.Exists() && !v.IsDead && v.IsDriveable) candidatePool.Add(v);
                    }
                }

                // 2. World Vehicles Scan
                Entity[] nearby = World.GetEntities(playerPos, maxSearchRadius, GetEntitiesFlags.ConsiderAllVehicles);
                if (nearby != null)
                {
                    foreach (Entity ent in nearby)
                    {
                        if (ent is Vehicle v && v.Exists() && !v.IsDead && v.IsDriveable)
                        {
                            candidatePool.Add(v);
                        }
                    }
                }

                if (candidatePool.Count == 0) return null;

                Vehicle bestCandidate = null;
                float bestDist = float.MaxValue;

                foreach (Vehicle veh in candidatePool)
                {
                    if (veh == null || !veh.Exists() || veh.IsDead || !veh.IsDriveable) continue;

                    float dist = veh.Position.DistanceTo(playerPos);
                    if (dist > maxSearchRadius) continue;

                    // Do not commandeer player's current or last vehicle
                    if (player != null && (player.CurrentVehicle == veh || player.LastVehicle == veh)) continue;

                    // Ensure not already tracked or reserved in active backup units
                    lock (_syncLock)
                    {
                        if (_activeUnits.Any(u => u.Cruiser == veh) || _reservedCruisers.Contains(veh)) continue;
                    }

                    string vName = veh.Model.Name.ToLowerInvariant();
                    if (isAmbulance)
                    {
                        if (!veh.Model.IsEmergencyVehicle && !vName.Contains("ambu") && !vName.Contains("medic") && !vName.Contains("ems")) continue;
                    }
                    else if (isFire)
                    {
                        if (!veh.Model.IsEmergencyVehicle && !vName.Contains("fire") && !vName.Contains("engine") && !vName.Contains("truk") && !vName.Contains("truck")) continue;
                    }
                    else
                    {
                        // Strict Law Enforcement Vehicle Verification
                        // Vehicle MUST be an authentic police vehicle - under NO circumstances allow civilian vehicles!
                        bool isPolice = veh.IsPoliceVehicle || veh.Model.IsLawEnforcementVehicle;
                        if (!isPolice) continue;
                    }

                    // Check driver availability & designated officer verification
                    Ped targetDriver = null;
                    Ped driver = veh.Driver;

                    if (driver != null && driver.Exists() && driver.IsAlive && !driver.IsPlayer && driver != player)
                    {
                        if (!isAmbulance && !isFire)
                        {
                            bool isCop = Functions.IsPedACop(driver) || driver.RelationshipGroup == "COP";
                            if (!isCop) continue; // Not an officer (e.g. civilian or criminal in cruiser)
                        }

                        bool inPursuit = false;
                        try { inPursuit = Functions.IsPedInPursuit(driver); } catch { }
                        if (inPursuit) continue;

                        if (UltimateBackupService.IsUltimateBackupCop(driver)) continue;

                        targetDriver = driver;
                    }
                    else if (veh.Driver == null && !isAmbulance && !isFire)
                    {
                        // Driver is outside (e.g. stationary / radar / traffic stop).
                        // Strictly verify this officer is explicitly the designated officer for this cruiser!
                        Ped designatedOfficer = StationaryFeature.GetAssignedOfficer(veh) ?? TrafficStopFeature.GetAssignedOfficer(veh);

                        // If not from active Cadence features, check if an ambient cop nearby has this cruiser as their LastVehicle
                        if (designatedOfficer == null)
                        {
                            Entity[] nearbyEntities = World.GetEntities(veh.Position, 20.0f, GetEntitiesFlags.ConsiderHumanPeds);
                            if (nearbyEntities != null && nearbyEntities.Length > 0)
                            {
                                designatedOfficer = nearbyEntities.OfType<Ped>().FirstOrDefault(p =>
                                    p != null && p.Exists() && p.IsAlive && !p.IsPlayer && p != player &&
                                    (Functions.IsPedACop(p) || p.RelationshipGroup == "COP") &&
                                    p.LastVehicle == veh);
                            }
                        }

                        if (designatedOfficer != null && designatedOfficer.Exists() && designatedOfficer.IsAlive && !designatedOfficer.IsPlayer)
                        {
                            bool inPursuit = false;
                            try { inPursuit = Functions.IsPedInPursuit(designatedOfficer); } catch { }
                            if (inPursuit) continue;

                            if (UltimateBackupService.IsUltimateBackupCop(designatedOfficer)) continue;

                            targetDriver = designatedOfficer;
                        }
                    }

                    if (targetDriver == null) continue;

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCandidate = veh;
                    }
                }

                return bestCandidate;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[BackupFeature] FindCandidateAmbientVehicle: {ex.Message}");
            }

            return null;
        }

        private static void ReleaseFromOtherFeatures(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return;

            // Direct release handshakes across Cadence features (no fragile reflection)
            try { StationaryFeature.RelinquishCruiser(cruiser); } catch { }
            try { PatrolFeature.RelinquishCruiser(cruiser); } catch { }
            try { ResponseFeature.RelinquishCruiser(cruiser); } catch { }
            try { TrafficStopFeature.RelinquishCruiser(cruiser); } catch { }

            try
            {
                NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(cruiser, false);
            }
            catch { }
        }

        private static void MobilizeStandingOfficer(Ped officer, Vehicle cruiser)
        {
            if (officer == null || !officer.Exists()) return;

            try
            {
                StationaryFeature.MobilizeCruiser(cruiser);
            }
            catch { }

            try
            {
                officer.Tasks.Clear();
                if (cruiser != null && cruiser.Exists() && !officer.IsInVehicle(cruiser, false))
                {
                    officer.Tasks.EnterVehicle(cruiser, -1);
                }
            }
            catch { }
        }

        public static Vector3 GetRoadsideCurbPosition(Vector3 playerPos)
        {
            try
            {
                if (WorldUtils.GetSafeStreetPosition(playerPos, 1, out Vector3 streetPos, out float streetHeading))
                {
                    if (streetPos != Vector3.Zero && !WorldUtils.IsSubterraneanOrVoid(streetPos))
                    {
                        return streetPos;
                    }
                }

                Vector3 rphStreet = World.GetNextPositionOnStreet(playerPos);
                if (rphStreet != Vector3.Zero && !WorldUtils.IsSubterraneanOrVoid(rphStreet))
                {
                    return rphStreet;
                }
            }
            catch { }

            return playerPos;
        }

        private static BackupUnitSession CommandeerAmbientVehicle(Vehicle cruiser, Vector3 playerPos, BackupType type, bool isCode3)
        {
            try
            {
                if (cruiser == null || !cruiser.Exists()) return null;

                bool isAmbulance = type == BackupType.AmbulanceCode2 || type == BackupType.AmbulanceCode3;
                bool isFire = type == BackupType.FireDeptCode2 || type == BackupType.FireDeptCode3;

                // Strict sanity check: cruiser MUST be an authentic police or emergency vehicle
                if (!isAmbulance && !isFire && !cruiser.IsPoliceVehicle && !cruiser.Model.IsLawEnforcementVehicle)
                {
                    Logger.Warn($"[BackupFeature] Commandeer rejected on non-police vehicle {cruiser.Model.Name}.");
                    return null;
                }

                ReleaseFromOtherFeatures(cruiser);

                cruiser.MakePersistent();
                cruiser.IsEngineOn = true;
                NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(cruiser, false);

                Ped driver = cruiser.Driver;
                Ped passenger = null;

                bool isAlreadyOnScene = cruiser.Position.DistanceTo(playerPos) <= 35.0f;

                if (driver == null || !driver.Exists())
                {
                    // If driver is outside (e.g. stationary / radar / traffic stop), resolve designated officer for this cruiser
                    driver = StationaryFeature.GetAssignedOfficer(cruiser) ?? TrafficStopFeature.GetAssignedOfficer(cruiser);
                    if (driver == null)
                    {
                        Entity[] nearbyEnts = World.GetEntities(cruiser.Position, 20.0f, GetEntitiesFlags.ConsiderHumanPeds);
                        driver = nearbyEnts?.OfType<Ped>().FirstOrDefault(p =>
                            p != null && p.Exists() && p.IsAlive && !p.IsPlayer &&
                            (Functions.IsPedACop(p) || p.RelationshipGroup == "COP") &&
                            p.LastVehicle == cruiser);
                    }

                    if (driver != null && driver.Exists() && driver.IsAlive)
                    {
                        driver.MakePersistent();
                        if (!isAlreadyOnScene)
                        {
                            driver.WarpIntoVehicle(cruiser, -1);
                        }
                        else
                        {
                            driver.Tasks.Clear();
                        }
                    }
                }

                if (driver == null || !driver.Exists() || !driver.IsAlive) return null;

                driver.MakePersistent();
                driver.BlockPermanentEvents = true;
                driver.Tasks.Clear();

                // Check for passenger
                try
                {
                    if (!cruiser.IsSeatFree(0))
                    {
                        Ped pass = cruiser.GetPedOnSeat(0);
                        if (pass != null && pass.Exists() && pass.IsAlive && !pass.IsPlayer)
                        {
                            passenger = pass;
                            passenger.MakePersistent();
                            passenger.BlockPermanentEvents = true;
                        }
                    }
                }
                catch { }

                string callsign = GenerateCallsign();
                Vector3 destination = isAlreadyOnScene ? cruiser.Position : GetRoadsideCurbPosition(playerPos);
                return ConfigureBackupSession(cruiser, driver, passenger, destination, type, isCode3, callsign, isCommandeer: true, additionalOfficers: null, isAlreadyOnScene: isAlreadyOnScene);
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error commandeering ambient vehicle", ex);
                return null;
            }
        }

        private static bool TryFindStaggeredBackupSpawnPoint(Vector3 playerPos, float playerHeading, int unitIndex, out Vector3 spawnPos, out float spawnHeading)
        {
            spawnPos = Vector3.Zero;
            spawnHeading = 0f;

            Ped player = Game.LocalPlayer?.Character;
            if (player == null || !player.Exists()) return false;

            // Stagger behind distance: 120m to 200m behind player (out of direct line of sight)
            float behindDist = 120.0f + (unitIndex * 35.0f);
            // Lateral offset: alternate left and right by 25m
            float sideOffset = (unitIndex == 0) ? 0f : (unitIndex % 2 == 1 ? 25.0f * ((unitIndex + 1) / 2) : -25.0f * ((unitIndex + 1) / 2));

            Vector3 probeCenter = player.GetOffsetPosition(new Vector3(sideOffset, -behindDist, 0f));

            for (int attempt = 0; attempt < 8; attempt++)
            {
                Vector3 testPos = World.GetNextPositionOnStreet(probeCenter);
                if (testPos != Vector3.Zero && !WorldUtils.IsSubterraneanOrVoid(testPos))
                {
                    float dist = testPos.DistanceTo(playerPos);
                    // Ensure the spawn point is at least 80m away from the player
                    if (dist >= 80.0f && WorldUtils.IsAreaClear(testPos, 9.0f))
                    {
                        spawnPos = testPos;
                        // Orient cruiser heading directly towards the player's position
                        float dx = playerPos.X - spawnPos.X;
                        float dy = playerPos.Y - spawnPos.Y;
                        spawnHeading = (float)(Math.Atan2(dy, dx) * (180.0 / Math.PI)) - 90f;
                        if (spawnHeading < 0) spawnHeading += 360f;
                        return true;
                    }
                }
                probeCenter = player.GetOffsetPosition(new Vector3(sideOffset + (attempt * 15.0f), -(behindDist + (attempt * 10.0f)), 0f));
            }

            Vector3 fallbackProbe = player.GetOffsetPosition(new Vector3(0f, -130.0f, 0f));
            return WorldUtils.GetSafeStreetPosition(fallbackProbe, unitIndex + 1, out spawnPos, out spawnHeading);
        }

        private static BackupUnitSession SpawnFallbackBackupUnit(Vector3 playerPos, BackupType type, bool isCode3, int unitIndex = 0)
        {
            try
            {
                float playerH = Game.LocalPlayer?.Character?.Heading ?? 0f;
                Vector3 spawnPos = Vector3.Zero;
                float spawnHeading = playerH;
                if (!TryFindStaggeredBackupSpawnPoint(playerPos, playerH, unitIndex, out spawnPos, out spawnHeading))
                {
                    Ped player = Game.LocalPlayer?.Character;
                    Vector3 rearPos = (player != null && player.Exists()) ? player.GetOffsetPosition(new Vector3(0f, -120.0f, 0f)) : playerPos;
                    if (!WorldUtils.GetSafeStreetPosition(rearPos, unitIndex + 1, out spawnPos, out spawnHeading))
                    {
                        spawnPos = World.GetNextPositionOnStreet(rearPos);
                        spawnHeading = playerH;
                    }
                }

                if (spawnPos == Vector3.Zero || WorldUtils.IsSubterraneanOrVoid(spawnPos))
                {
                    Logger.Warn("[BackupFeature] Could not resolve valid road node for fallback backup spawner.");
                    return null;
                }

                string callsign = GenerateCallsign();

                // Ambulance / EMS Spawner
                if (type == BackupType.AmbulanceCode2 || type == BackupType.AmbulanceCode3)
                {
                    Vehicle amb = null;
                    Ped medic = null;
                    Ped passMedic = null;

                    if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                    {
                        var ubResult = UltimateBackupService.SpawnAmbulanceUnit(spawnPos, 2);
                        if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                        {
                            amb = ubResult.Item1;
                            medic = ubResult.Item2[0];
                            if (ubResult.Item2.Count > 1) passMedic = ubResult.Item2[1];
                        }
                    }

                    if (amb == null || !amb.Exists() || medic == null || !medic.Exists())
                    {
                        if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                        {
                            Logger.Debug("[BackupFeature] Ultimate Backup did not return ambulance and AllowFallback is false. Aborting spawn.");
                            return null;
                        }

                        var nativeResult = UltimateBackupService.SpawnNativeAmbulanceUnit(spawnPos, spawnHeading);
                        if (nativeResult != null && nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                        {
                            amb = nativeResult.Item1;
                            medic = nativeResult.Item2[0];
                        }
                    }

                    if (amb != null && amb.Exists() && medic != null && medic.Exists())
                    {
                        amb.Heading = spawnHeading;
                        WorldUtils.SecureEntityGround(amb);
                        Vector3 destination = GetRoadsideCurbPosition(playerPos);
                        return ConfigureBackupSession(amb, medic, passMedic, destination, type, isCode3, callsign, isCommandeer: false);
                    }

                    return null;
                }

                // Fire Department Spawner
                if (type == BackupType.FireDeptCode2 || type == BackupType.FireDeptCode3)
                {
                    Vehicle fire = null;
                    Ped fireman = null;
                    Ped passFireman = null;

                    if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                    {
                        var ubResult = UltimateBackupService.SpawnFireTruckUnit(spawnPos, 2);
                        if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                        {
                            fire = ubResult.Item1;
                            fireman = ubResult.Item2[0];
                            if (ubResult.Item2.Count > 1) passFireman = ubResult.Item2[1];
                        }
                    }

                    if (fire == null || !fire.Exists() || fireman == null || !fireman.Exists())
                    {
                        if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                        {
                            Logger.Debug("[BackupFeature] Ultimate Backup did not return fire truck and AllowFallback is false. Aborting spawn.");
                            return null;
                        }

                        var nativeResult = UltimateBackupService.SpawnNativeFireUnit(spawnPos, spawnHeading);
                        if (nativeResult != null && nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                        {
                            fire = nativeResult.Item1;
                            fireman = nativeResult.Item2[0];
                        }
                    }

                    if (fire != null && fire.Exists() && fireman != null && fireman.Exists())
                    {
                        fire.Heading = spawnHeading;
                        WorldUtils.SecureEntityGround(fire);
                        Vector3 destination = GetRoadsideCurbPosition(playerPos);
                        return ConfigureBackupSession(fire, fireman, passFireman, destination, type, isCode3, callsign, isCommandeer: false);
                    }

                    return null;
                }

                // Police Backup Spawner (Local vs State)
                bool isState = type == BackupType.StatePatrolCode2 || type == BackupType.StatePatrolCode3;
                if (!isState && (type == BackupType.PanicButton || type == BackupType.GroupBackup))
                {
                    isState = WorldUtils.ShouldSpawnHighwayUnit(spawnPos);
                }

                Vehicle cruiser = null;
                Ped driver = null;
                Ped passenger = null;
                List<Ped> extraOfficers = null;

                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    int pedsToSpawn = (type == BackupType.PanicButton || type == BackupType.GroupBackup) ? 2 : 1;
                    var ubResult = isState
                        ? UltimateBackupService.SpawnStatePatrol(spawnPos, pedsToSpawn)
                        : UltimateBackupService.SpawnLocalPatrol(spawnPos, pedsToSpawn);

                    if (ubResult != null && ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                    {
                        cruiser = ubResult.Item1;
                        driver = ubResult.Item2[0];
                        if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                        if (ubResult.Item2.Count > 2) extraOfficers = ubResult.Item2.Skip(2).ToList();
                    }
                }

                if (cruiser == null || !cruiser.Exists() || driver == null || !driver.Exists())
                {
                    if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                    {
                        Logger.Debug("[BackupFeature] Ultimate Backup did not return unit and AllowFallback is false. Aborting spawn.");
                        return null;
                    }

                    string vModelName = isState ? ZoneSpawnData.GetDefaultStatePatrolVehicle() : ZoneSpawnData.GetDefaultPoliceVehicleForPosition(spawnPos);
                    string pModelName = isState ? ZoneSpawnData.GetDefaultStatePatrolPed() : ZoneSpawnData.GetDefaultCopPedModelForPosition(spawnPos);

                    var nativeResult = UltimateBackupService.SpawnNativePatrolUnit(spawnPos, spawnHeading, vModelName, pModelName);
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
                    Vector3 destination = GetRoadsideCurbPosition(playerPos);
                    return ConfigureBackupSession(cruiser, driver, passenger, destination, type, isCode3, callsign, isCommandeer: false, extraOfficers);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error spawning fallback backup unit", ex);
            }

            return null;
        }

        private static BackupUnitSession ConfigureBackupSession(Vehicle cruiser, Ped driver, Ped passenger, Vector3 destination, BackupType type, bool isCode3, string callsign, bool isCommandeer, IEnumerable<Ped> additionalOfficers = null, bool isAlreadyOnScene = false)
        {
            try
            {
                cruiser.MakePersistent();
                driver.MakePersistent();
                if (passenger != null && passenger.Exists()) passenger.MakePersistent();
                if (additionalOfficers != null)
                {
                    foreach (var ofc in additionalOfficers)
                    {
                        if (ofc != null && ofc.Exists()) ofc.MakePersistent();
                    }
                }

                NativeTasks.ConfigureEmergencyDriver(driver, cruiser);

                // Siren Protocol: Code 3 audible, Code 2 silent lightbars
                if (isCode3)
                {
                    cruiser.IsSirenOn = true;
                    cruiser.IsSirenSilent = false;
                }
                else
                {
                    cruiser.IsSirenOn = true;
                    cruiser.IsSirenSilent = true;
                    NativeTasks.SetSilentEmergencyLights(cruiser, true);
                }

                if (isAlreadyOnScene)
                {
                    // If already on scene (within 35m), do not accelerate forward
                    try
                    {
                        cruiser.IsSirenOn = true;
                        cruiser.IsSirenSilent = true;
                        NativeTasks.SetSilentEmergencyLights(cruiser, true);
                        if (driver != null && driver.Exists() && driver.IsAlive && driver.IsInVehicle(cruiser, false))
                        {
                            driver.Tasks.PerformDrivingManeuver(VehicleManeuver.Wait);
                        }
                    }
                    catch { }
                }
                else
                {
                    // High-speed emergency driving to destination using long-range task
                    float speed = isCode3 ? 32.0f : 20.0f; // ~71 mph vs ~45 mph
                    NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(cruiser, 10.0f);
                    NativeTasks.TaskLongRangeEmergencyDrive(driver, cruiser, destination, speed, 12.0f);
                }

                // Attach flashing emergency blip
                string labelSuffix = isCode3 ? "CODE 3" : "CODE 2";
                Blip blip = BlipService.AttachEmergencyBlip(cruiser, $"{callsign} [{labelSuffix}]");

                var session = new BackupUnitSession(cruiser, driver, passenger, destination, blip, callsign, type, isCode3, isCommandeer, additionalOfficers, isAlreadyOnScene);

                if (isAlreadyOnScene)
                {
                    Ped player = Game.LocalPlayer?.Character;
                    if (player != null && player.Exists())
                    {
                        session.TransitionToSceneArrival(player);
                    }
                }

                return session;
            }
            catch (Exception ex)
            {
                Logger.Error($"[BackupFeature] ConfigureBackupSession error for {callsign}", ex);
                return null;
            }
        }

        // ====================================================================
        // Audio & Radio Animation Utilities
        // ====================================================================

        private static void PlayPanicButtonSound()
        {
            GameFiber.StartNew(() =>
            {
                try
                {
                    string[] paths = new[]
                    {
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "LSPDFR", "Cadence", "Audio", "PANIC_BUTTON.wav"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "LSPDFR", "UltimateBackup", "Audio", "PANIC_BUTTON.wav"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", "Audio", "PANIC_BUTTON.wav"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Audio", "PANIC_BUTTON.wav")
                    };

                    string wavPath = paths.FirstOrDefault(File.Exists);
                    if (!string.IsNullOrEmpty(wavPath))
                    {
                        SoundPlayer sp = new SoundPlayer(wavPath);
                        sp.Play();
                    }
                    else
                    {
                        Logger.Warn("[BackupFeature] PANIC_BUTTON.wav audio file not found on disk.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("[BackupFeature] PlayPanicButtonSound error", ex);
                }
            }, "Cadence - Panic Sound Player Fiber");
        }

        private static void PlayPlayerRadioAnimation()
        {
            try
            {
                Ped player = Game.LocalPlayer?.Character;
                if (player == null || !player.Exists()) return;

                bool played = false;
                try
                {
                    Functions.PlayPlayerRadioAction(Functions.GetPlayerRadioAction(), 4000);
                    played = true;
                }
                catch { }

                if (!played)
                {
                    if (NativeTasks.RequestAnimDict("random@arrests"))
                    {
                        player.Tasks.PlayAnimation("random@arrests", "generic_radio_enter", 8.0f, AnimationFlags.UpperBodyOnly | AnimationFlags.SecondaryTask);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[BackupFeature] PlayPlayerRadioAnimation error: {ex.Message}");
            }
        }

        private static string GenerateCallsign()
        {
            string div = Divisions[_rand.Next(Divisions.Length)];
            int num = _callsignCounter++;
            if (_callsignCounter > 99) _callsignCounter = 50;
            return $"{div}-{num:D2}";
        }

        // ====================================================================
        // Responding Backup Unit Session State Machine
        // ====================================================================

        public class BackupUnitSession
        {
            public enum UnitState { EnRoute, ArrivedOnScene, EngagingThreat, Dismissing, Completed }

            public Vehicle Cruiser { get; }
            public Ped Driver { get; }
            public Ped Passenger { get; }
            public List<Ped> Officers { get; }
            public Vector3 TargetPosition { get; set; }
            public Blip RadarBlip;
            public List<Blip> FootBlips { get; } = new List<Blip>();
            public string Callsign { get; }
            public BackupType ServiceType { get; }
            public bool IsCode3 { get; }
            public bool IsCommandeer { get; }

            public UnitState State { get; private set; }
            public uint StartTime { get; }
            public uint StateStartTime { get; private set; }
            public Ped ThreatPed { get; private set; }

            private uint _lastNavigationRefresh;
            private uint _lastThreatScan;
            private uint _lastCoverCheck;
            private uint _lastSlowdownCheck;
            private uint _lastBuddyFollowUpdate;
            private bool _isArrivedSirenMuted;
            private bool _departureInitiated;

            public BackupUnitSession(Vehicle cruiser, Ped driver, Ped passenger, Vector3 destination, Blip blip, string callsign, BackupType serviceType, bool isCode3, bool isCommandeer, IEnumerable<Ped> additionalOfficers = null, bool isAlreadyOnScene = false)
            {
                Cruiser = cruiser;
                Driver = driver;
                Passenger = passenger;
                Officers = new List<Ped>();
                if (driver != null && driver.Exists()) Officers.Add(driver);
                if (passenger != null && passenger.Exists() && !Officers.Contains(passenger)) Officers.Add(passenger);
                if (additionalOfficers != null)
                {
                    foreach (var ofc in additionalOfficers)
                    {
                        if (ofc != null && ofc.Exists() && !Officers.Contains(ofc))
                        {
                            Officers.Add(ofc);
                        }
                    }
                }

                TargetPosition = destination;
                RadarBlip = blip;
                Callsign = callsign;
                ServiceType = serviceType;
                IsCode3 = isCode3;
                IsCommandeer = isCommandeer;

                StartTime = Game.GameTime;
                StateStartTime = Game.GameTime;
                State = isAlreadyOnScene ? UnitState.ArrivedOnScene : UnitState.EnRoute;
            }

            private static int GetSeatIndexForPed(int pedIndex)
            {
                return (pedIndex == 0) ? -1 : (pedIndex - 1);
            }

            public bool IsValid()
            {
                return Cruiser != null && Cruiser.Exists() && Cruiser.IsDriveable &&
                       Officers != null && Officers.Any(o => o != null && o.Exists() && o.IsAlive);
            }

            public void Dismiss()
            {
                if (State == UnitState.Dismissing || State == UnitState.Completed) return;

                State = UnitState.Dismissing;
                StateStartTime = Game.GameTime;
                _departureInitiated = false;

                // Keep vehicle radar blip active during departure so player sees unit leaving
                if (RadarBlip != null && RadarBlip.Exists())
                {
                    BlipService.TransitionToPatrol(RadarBlip, Callsign);
                }
                ClearFootBlips();

                // Extinguish emergency lightbars and sirens
                if (Cruiser != null && Cruiser.Exists())
                {
                    try
                    {
                        Cruiser.IsSirenOn = false;
                        Cruiser.IsSirenSilent = false;
                        NativeTasks.SetSilentEmergencyLights(Cruiser, false);
                    }
                    catch { }
                }

                // Command officers to remount cruiser with collision-free seat indexing
                OrderOfficersEnterVehicle();
            }

            private void OrderOfficersEnterVehicle()
            {
                for (int i = 0; i < Officers.Count; i++)
                {
                    Ped ofc = Officers[i];
                    if (ofc == null || !ofc.Exists() || !ofc.IsAlive) continue;

                    int seatIdx = GetSeatIndexForPed(i);
                    GameFiber.StartNew(() =>
                    {
                        try
                        {
                            ofc.Tasks.Clear();
                            NativeFunction.Natives.SET_CURRENT_PED_WEAPON(ofc, WeaponUnarmed, true);

                            float enterSpeed = (Cruiser != null && Cruiser.Exists() && ofc.DistanceTo(Cruiser) > 20.0f) ? 2.0f : 1.0f;
                            for (int attempt = 0; attempt < 5; attempt++)
                            {
                                if (!ofc.Exists() || !ofc.IsAlive || (Cruiser != null && ofc.IsInVehicle(Cruiser, false))) break;
                                if (Cruiser != null && Cruiser.Exists())
                                {
                                    ofc.Tasks.EnterVehicle(Cruiser, 30000, seatIdx, enterSpeed, EnterVehicleFlags.None).WaitForCompletion();
                                }
                                GameFiber.Sleep(500);
                            }

                            // Warp fallback after 5 failed attempts
                            if (ofc.Exists() && ofc.IsAlive && Cruiser != null && Cruiser.Exists() && !ofc.IsInVehicle(Cruiser, false))
                            {
                                ofc.WarpIntoVehicle(Cruiser, seatIdx);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"[BackupFeature] Boarding fiber error for officer {ofc.Handle}: {ex.Message}");
                        }
                    }, $"Cadence - Boarding Fiber {ofc.Handle}");
                }
            }

            public void Update(Ped player, Vector3 playerPos)
            {
                if (!IsValid())
                {
                    State = UnitState.Completed;
                    return;
                }

                uint now = Game.GameTime;

                switch (State)
                {
                    case UnitState.EnRoute:
                        UpdateEnRoute(player, playerPos, now);
                        break;

                    case UnitState.ArrivedOnScene:
                        UpdateOnScene(player, playerPos, now);
                        break;

                    case UnitState.EngagingThreat:
                        UpdateThreatEngagement(player, playerPos, now);
                        break;

                    case UnitState.Dismissing:
                        UpdateDismissing(player, playerPos, now);
                        break;
                }
            }

            private void UpdateEnRoute(Ped player, Vector3 playerPos, uint now)
            {
                float distToPlayer = Cruiser.Position.DistanceTo(playerPos);
                float distToTarget = Cruiser.Position.DistanceTo(TargetPosition);

                // Ensure lights & sirens remain active during approach
                if (IsCode3)
                {
                    if (!Cruiser.IsSirenOn) Cruiser.IsSirenOn = true;
                    if (Cruiser.IsSirenSilent) Cruiser.IsSirenSilent = false;
                }

                // Periodic course recalculation if player moved > 30m
                if (now - _lastNavigationRefresh > 3000)
                {
                    _lastNavigationRefresh = now;
                    if (TargetPosition.DistanceTo(playerPos) > 30.0f)
                    {
                        TargetPosition = GetRoadsideCurbPosition(playerPos);
                        float speed = IsCode3 ? 32.0f : 20.0f;
                        NativeTasks.TaskLongRangeEmergencyDrive(Driver, Cruiser, TargetPosition, speed, 12.0f);
                    }
                }

                // Approach slowdown: within 55m and travelling fast (> 14 m/s), decelerate smoothly to ~11 m/s
                if (distToPlayer <= 55.0f && Cruiser.Speed > 14.0f && (now - _lastSlowdownCheck > 2000))
                {
                    _lastSlowdownCheck = now;
                    NativeTasks.TaskLongRangeEmergencyDrive(Driver, Cruiser, TargetPosition, 11.0f, 12.0f);
                }

                // Scene arrival check with harmonized stopping radius (12.0m) and arrival distance
                uint elapsedEnRoute = now - StateStartTime;
                bool isArrived = false;

                // 1. Definite close arrival: within 14.0m of target curb position or within 16.0m of player
                if (distToTarget <= 14.0f || distToPlayer <= 16.0f)
                {
                    isArrived = true;
                }
                // 2. Slowed to approach halt near target or player, but ONLY after pacing grace period (5s)
                else if ((distToTarget <= 25.0f || distToPlayer <= 25.0f) && Cruiser.Speed < 1.8f && elapsedEnRoute > 5000)
                {
                    isArrived = true;
                }
                // 3. Fallback: full stop within 35m after at least 8 seconds of active travel
                else if ((distToTarget <= 35.0f || distToPlayer <= 35.0f) && Cruiser.Speed < 0.5f && elapsedEnRoute > 8000)
                {
                    isArrived = true;
                }

                if (isArrived)
                {
                    TransitionToSceneArrival(player);
                }
            }

            public void TransitionToSceneArrival(Ped player)
            {
                State = UnitState.ArrivedOnScene;
                StateStartTime = Game.GameTime;

                Logger.Info($"[BackupFeature] Unit {Callsign} arrived on scene ({Cruiser.Position.DistanceTo(player.Position):F1}m from player).");

                // Step 1: Mute audible siren immediately, keep flashing lightbars active
                if (!_isArrivedSirenMuted)
                {
                    _isArrivedSirenMuted = true;
                    try
                    {
                        Cruiser.IsSirenOn = true;
                        Cruiser.IsSirenSilent = true;
                        NativeTasks.SetSilentEmergencyLights(Cruiser, true);
                    }
                    catch { }
                }

                // Step 2: Halt cruiser
                try
                {
                    if (Driver != null && Driver.Exists() && Driver.IsAlive && Cruiser != null && Cruiser.Exists() && Driver.IsInVehicle(Cruiser, false))
                    {
                        Driver.Tasks.PerformDrivingManeuver(VehicleManeuver.Wait);
                    }
                }
                catch { }

                // Step 3: Staggered vehicle egress for all responding officers
                GameFiber.StartNew(() =>
                {
                    try
                    {
                        for (int i = 0; i < Officers.Count; i++)
                        {
                            Ped ofc = Officers[i];
                            if (ofc != null && ofc.Exists() && ofc.IsAlive)
                            {
                                if (Cruiser != null && Cruiser.Exists() && ofc.IsInVehicle(Cruiser, false))
                                {
                                    ofc.Tasks.LeaveVehicle(LeaveVehicleFlags.None);
                                }

                                if (ServiceType == BackupType.PanicButton)
                                {
                                    try
                                    {
                                        NativeFunction.Natives.GIVE_WEAPON_TO_PED(ofc, WeaponCombatPistol, 120, false, true);
                                        NativeFunction.Natives.SET_CURRENT_PED_WEAPON(ofc, WeaponCombatPistol, true);
                                    }
                                    catch { }
                                }
                            }
                            GameFiber.Sleep(300);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[BackupFeature] Egress fiber error: {ex.Message}");
                    }
                }, "Cadence - Backup Egress Fiber");

                // Step 4: Attach foot blips for dismounted officers
                CreateFootBlips();
            }

            private void UpdateOnScene(Ped player, Vector3 playerPos, uint now)
            {
                // Scan for active threats or pursuits every 1000ms
                if (now - _lastThreatScan >= 1000)
                {
                    _lastThreatScan = now;

                    // 1. Check active LSPDFR pursuit
                    try
                    {
                        LHandle activePursuit = Functions.GetActivePursuit();
                        if (activePursuit != null)
                        {
                            foreach (var ofc in Officers)
                            {
                                if (ofc != null && ofc.Exists() && ofc.IsAlive && !Functions.IsPedInPursuit(ofc))
                                {
                                    Functions.AddCopToPursuit(activePursuit, ofc);
                                }
                            }
                        }
                    }
                    catch { }

                    // 2. Scan for hostile peds targeting player or officers within 40m
                    Ped threat = FindHostileThreatNear(playerPos, 40.0f);
                    if (threat != null && threat.Exists() && threat.IsAlive)
                    {
                        EngageThreat(threat);
                        return;
                    }
                }

                // If no threat present: Follow / Buddy Behavior
                if (ServiceType != BackupType.AmbulanceCode2 && ServiceType != BackupType.AmbulanceCode3 &&
                    ServiceType != BackupType.FireDeptCode2 && ServiceType != BackupType.FireDeptCode3)
                {
                    UpdateBuddyFollowing(player, playerPos);
                }
            }

            private void UpdateBuddyFollowing(Ped player, Vector3 playerPos)
            {
                if (Driver == null || !Driver.Exists() || !Driver.IsAlive || Driver.IsInVehicle(Cruiser, false))
                {
                    return;
                }

                uint now = Game.GameTime;
                if (now - _lastBuddyFollowUpdate < 1300)
                {
                    return;
                }
                _lastBuddyFollowUpdate = now;

                float distToPlayer = Driver.DistanceTo(playerPos);

                // Interior Watchdog: if distance > 25m into interior or building, teleport buddy to prevent lock-out
                bool isPlayerInInterior = false;
                try
                {
                    isPlayerInInterior = NativeFunction.Natives.GET_INTERIOR_FROM_ENTITY<int>(player) != 0;
                }
                catch { }

                if (distToPlayer > 25.0f && (isPlayerInInterior || Math.Abs(Driver.Position.Z - playerPos.Z) > 4.0f))
                {
                    Vector3 tpPos = player.GetOffsetPosition(new Vector3(-1.0f, -1.2f, 0f));
                    Driver.Position = tpPos;
                    Driver.Tasks.Clear();
                    Logger.Debug("[BackupFeature] Interior watchdog teleported buddy to player interior offset.");
                    return;
                }

                // Tactical following distances with throttle protection against 60Hz navmesh task spam
                if (distToPlayer > 12.0f)
                {
                    // Sprint to catch up
                    TaskFollowPlayer(Driver, player, new Vector3(-1.2f, -1.5f, 0f), 2.5f);
                }
                else if (distToPlayer > 3.5f)
                {
                    // Walk close support
                    TaskFollowPlayer(Driver, player, new Vector3(-1.2f, -1.5f, 0f), 1.2f);
                }
                else
                {
                    // Within 3.5m of player: stand naturally instead of vibrating/freezing
                    if (Driver.Speed > 0.1f)
                    {
                        Driver.Tasks.Clear();
                    }
                }

                // Passenger secondary position
                if (Passenger != null && Passenger.Exists() && Passenger.IsAlive && !Passenger.IsInVehicle(Cruiser, false))
                {
                    float passDist = Passenger.DistanceTo(playerPos);
                    if (passDist > 25.0f && isPlayerInInterior)
                    {
                        Passenger.Position = player.GetOffsetPosition(new Vector3(1.0f, -1.2f, 0f));
                        Passenger.Tasks.Clear();
                    }
                    else if (passDist > 5.0f)
                    {
                        TaskFollowPlayer(Passenger, player, new Vector3(1.2f, -1.8f, 0f), 1.2f);
                    }
                    else
                    {
                        if (Passenger.Speed > 0.1f)
                        {
                            Passenger.Tasks.Clear();
                        }
                    }
                }
            }

            private static void TaskFollowPlayer(Ped officer, Ped player, Vector3 offset, float speed)
            {
                if (officer == null || !officer.Exists() || player == null || !player.Exists()) return;
                try
                {
                    Vector3 targetPoint = player.GetOffsetPosition(offset);
                    officer.Tasks.FollowNavigationMeshToPosition(targetPoint, player.Heading, speed, 0.5f);
                }
                catch
                {
                    try
                    {
                        Vector3 targetPoint = player.GetOffsetPosition(offset);
                        NativeFunction.Natives.TASK_FOLLOW_NAV_MESH_TO_COORD(officer, targetPoint.X, targetPoint.Y, targetPoint.Z, speed, -1, 0.5f, 0, 0f);
                    }
                    catch { }
                }
            }

            private Ped FindHostileThreatNear(Vector3 center, float radius)
            {
                try
                {
                    Ped player = Game.LocalPlayer?.Character;
                    Ped[] nearby = World.GetEntities(center, radius, GetEntitiesFlags.ConsiderHumanPeds)
                                        .OfType<Ped>()
                                        .ToArray();

                    foreach (Ped p in nearby)
                    {
                        if (p == null || !p.Exists() || p.IsDead || p.IsPlayer || p == player) continue;
                        if (Functions.IsPedACop(p) || p.RelationshipGroup == "COP") continue;

                        bool isArmed = false;
                        try
                        {
                            isArmed = NativeFunction.Natives.IS_PED_ARMED<bool>(p, 7);
                        }
                        catch { }

                        bool isShooting = p.IsShooting;
                        bool inCombat = p.IsInCombat;
                        bool isCuffed = false;
                        try { isCuffed = p.IsCuffed || Functions.IsPedArrested(p); } catch { }
                        if (isCuffed) continue;

                        if (isShooting || (inCombat && isArmed))
                        {
                            return p;
                        }
                    }
                }
                catch { }

                return null;
            }

            private void EngageThreat(Ped threat)
            {
                ThreatPed = threat;
                State = UnitState.EngagingThreat;
                StateStartTime = Game.GameTime;
                _lastCoverCheck = Game.GameTime;

                Logger.Info($"[BackupFeature] Unit {Callsign} engaging hostile threat 0x{threat.Handle:X}.");

                if (RadarBlip != null && RadarBlip.Exists())
                {
                    BlipService.TransitionToCombat(RadarBlip, Callsign);
                }

                foreach (var ofc in Officers)
                {
                    if (ofc != null && ofc.Exists() && ofc.IsAlive)
                    {
                        try
                        {
                            ofc.Tasks.Clear();
                            NativeFunction.Natives.GIVE_WEAPON_TO_PED(ofc, WeaponCombatPistol, 120, false, true);
                            NativeFunction.Natives.SET_CURRENT_PED_WEAPON(ofc, WeaponCombatPistol, true);
                            NativeFunction.Natives.TASK_COMBAT_PED(ofc, threat, 0, 16);
                        }
                        catch { }
                    }
                }
            }

            private void UpdateThreatEngagement(Ped player, Vector3 playerPos, uint now)
            {
                bool threatNeutralized = ThreatPed == null || !ThreatPed.Exists() || ThreatPed.IsDead;
                if (!threatNeutralized)
                {
                    try
                    {
                        threatNeutralized = ThreatPed.IsCuffed || Functions.IsPedArrested(ThreatPed);
                    }
                    catch { }
                }

                // 90s engagement timeout fail-safe
                if (now - StateStartTime > 90000)
                {
                    threatNeutralized = true;
                }

                if (threatNeutralized)
                {
                    Logger.Info($"[BackupFeature] Threat neutralized. Unit {Callsign} standing down and holstering sidearms.");

                    ThreatPed = null;
                    State = UnitState.ArrivedOnScene;
                    StateStartTime = now;

                    foreach (var ofc in Officers)
                    {
                        if (ofc != null && ofc.Exists() && ofc.IsAlive)
                        {
                            try
                            {
                                ofc.Tasks.Clear();
                                NativeFunction.Natives.SET_CURRENT_PED_WEAPON(ofc, WeaponUnarmed, true);
                            }
                            catch { }
                        }
                    }

                    if (RadarBlip != null && RadarBlip.Exists())
                    {
                        BlipService.TransitionToPatrol(RadarBlip, Callsign);
                    }
                }
                else
                {
                    // Tactical repositioning check every 5s
                    if (now - _lastCoverCheck > 5000)
                    {
                        _lastCoverCheck = now;
                        foreach (var ofc in Officers)
                        {
                            if (ofc != null && ofc.Exists() && ofc.IsAlive && !ofc.IsInCombat)
                            {
                                ofc.Tasks.FightAgainst(ThreatPed);
                            }
                        }
                    }
                }
            }

            private void UpdateDismissing(Ped player, Vector3 playerPos, uint now)
            {
                if (Cruiser == null || !Cruiser.Exists())
                {
                    Dispose(immediateDelete: false);
                    State = UnitState.Completed;
                    return;
                }

                // Step 1: Check if all living officers are mounted
                bool allMounted = Officers.Where(o => o != null && o.Exists() && o.IsAlive).All(o => o.IsInVehicle(Cruiser, false));

                // 15s timeout fail-safe: warp in any stuck officer to their designated seat
                if (!allMounted && (now - StateStartTime > 15000))
                {
                    for (int i = 0; i < Officers.Count; i++)
                    {
                        Ped ofc = Officers[i];
                        if (ofc != null && ofc.Exists() && ofc.IsAlive && !ofc.IsInVehicle(Cruiser, false))
                        {
                            int seatIdx = GetSeatIndexForPed(i);
                            try { ofc.WarpIntoVehicle(Cruiser, seatIdx); } catch { }
                        }
                    }
                    allMounted = true;
                }

                if (!allMounted) return;

                // Step 2: One-shot departure sequence trigger (PREVENTS 20 HZ TASK SPAM FREEZING)
                if (!_departureInitiated)
                {
                    _departureInitiated = true;

                    GameFiber.StartNew(() =>
                    {
                        try
                        {
                            Ped drv = Cruiser.Driver;
                            if (drv == null || !drv.Exists() || !drv.IsAlive) return;

                            Cruiser.IsSirenOn = false;
                            Cruiser.IsSirenSilent = false;
                            NativeTasks.SetSilentEmergencyLights(Cruiser, false);

                            NativeTasks.ConfigurePatrolDriver(drv, Cruiser);

                            // Step 2a: Smooth initial departure drive away from the scene
                            Vector3 departurePoint = World.GetNextPositionOnStreet(Cruiser.GetOffsetPositionFront(80.0f));
                            if (departurePoint != Vector3.Zero && !WorldUtils.IsSubterraneanOrVoid(departurePoint))
                            {
                                NativeTasks.DriveToPositionLongRange(drv, Cruiser, departurePoint, 10.0f, NativeTasks.NormalDrivingFlag, 5.0f);
                            }
                            else
                            {
                                drv.Tasks.CruiseWithVehicle(Cruiser, 12.0f, NativeTasks.CruisingFlags);
                            }

                            Vector3 startPos = Cruiser.Position;
                            // Wait until cruiser has moved at least 25m away from start position
                            for (int i = 0; i < 40; i++)
                            {
                                if (!Cruiser.Exists() || !drv.Exists() || !drv.IsAlive) break;
                                if (Cruiser.DistanceTo(startPos) >= 25.0f) break;
                                GameFiber.Sleep(500);
                            }

                            // Step 2b: Hand off to ambient traffic cruise / wander ONCE
                            if (drv.Exists() && drv.IsAlive && Cruiser.Exists())
                            {
                                NativeFunction.Natives.TASK_VEHICLE_DRIVE_WANDER(drv, Cruiser, 10.0f, NativeTasks.NormalDrivingFlag);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("[BackupFeature] Departure fiber exception", ex);
                        }
                    }, $"Cadence - Departure Fiber {Callsign}");
                }

                // Step 3: Monitor distance for safe dismissal / ambient handover
                float dist = Cruiser.DistanceTo(playerPos);
                // When beyond 85m after minimum 15s departure drive, or when past 180m, or 50s timeout
                if ((_departureInitiated && (now - StateStartTime > 15000) && dist >= 85.0f) || dist >= 180.0f || (now - StateStartTime > 50000))
                {
                    Logger.Info($"[BackupFeature] Unit {Callsign} released back to ambient GTA traffic ({dist:F0}m away).");
                    BlipService.SafeDeleteBlip(ref RadarBlip);
                    Dispose(immediateDelete: false);
                    State = UnitState.Completed;
                }
            }

            private void CreateFootBlips()
            {
                if (!Settings.ShowRadarBlips) return;

                ClearFootBlips();
                foreach (var ofc in Officers)
                {
                    if (ofc != null && ofc.Exists() && ofc.IsAlive)
                    {
                        Blip fb = BlipService.AttachOfficerFootBlip(ofc, Callsign);
                        if (fb != null && fb.Exists())
                        {
                            FootBlips.Add(fb);
                        }
                    }
                }
            }

            private void ClearFootBlips()
            {
                foreach (var fb in FootBlips)
                {
                    Blip temp = fb;
                    BlipService.SafeDeleteBlip(ref temp);
                }
                FootBlips.Clear();
            }

            public void Dispose(bool immediateDelete)
            {
                BlipService.SafeDeleteBlip(ref RadarBlip);
                ClearFootBlips();

                if (immediateDelete)
                {
                    try
                    {
                        if (Cruiser != null && Cruiser.Exists()) Cruiser.Delete();
                        foreach (var ofc in Officers)
                        {
                            if (ofc != null && ofc.Exists()) ofc.Delete();
                        }
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        if (Cruiser != null && Cruiser.Exists()) Cruiser.Dismiss();
                        foreach (var ofc in Officers)
                        {
                            if (ofc != null && ofc.Exists()) ofc.Dismiss();
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
