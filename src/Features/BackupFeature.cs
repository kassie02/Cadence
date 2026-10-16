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
using LSPD_First_Response.Mod.Callouts;
using System.Runtime.CompilerServices;
using Cadence.Core;
using Cadence.Common;
using Cadence.Services;
using Cadence.Data;
using System.Threading;

namespace Cadence.Features
{
    /// <summary>
    /// Ambient Backup & Response Systems for Cadence.
    /// Provides 1:1 Ultimate Backup compatibility (Panic Button, Group Backup, Lean Backup Menu)
    /// backed by an Ambient-First Commandeering Architecture and full delete-friendly Ponytail decoupling.
    /// </summary>
    public sealed class BackupFeature : IFeature
    {
        public static BackupFeature Instance { get; } = new BackupFeature();
        public string Name => "Backup";
        public bool IsRunning => _isRunning;

        void IFeature.Start() => Start();
        void IFeature.Stop() => Stop();
        void IFeature.PurgeAll(bool immediateDelete) => PurgeAll(immediateDelete);
        int IFeature.GetActiveCount() => GetActiveCount();

        private const uint WeaponCombatPistol = 0x5EF9FEC4;
        private const uint WeaponUnarmed = 0xA2719263; // Verified JOAAT for WEAPON_UNARMED

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
        private static readonly List<GameFiber> _trackedFibers = new List<GameFiber>();

        private static GameFiber _backupFiber;
        private static bool _isRunning;
        private static int _callsignCounter = 50;
        private static uint _dismissKeyDownTime;
        private static uint _lastPanicTriggerTime;
        private static uint _lastGroupTriggerTime;

        private static GameFiber StartTrackedFiber(Action action, string name)
        {
            GameFiber fiber = null;
            fiber = GameFiber.StartNew(() =>
            {
                try
                {
                    action();
                }
                finally
                {
                    lock (_syncLock)
                    {
                        _trackedFibers.Remove(fiber);
                    }
                }
            }, name);

            lock (_syncLock)
            {
                _trackedFibers.RemoveAll(f => f == null || !f.IsAlive);
                if (fiber != null) _trackedFibers.Add(fiber);
            }
            return fiber;
        }

        // RAGENativeUI Lean Backup Menu (UB Class74 Architecture)
        private static MenuPool _menuPool;
        private static UIMenu _backupMenu;
        private static readonly object[] NormalModes = new object[] { "Code 3 (Emergency)", "Code 2 (Silent)" };
        private static readonly object[] NormalModesWithCallout = new object[] { "Callout Response", "Code 3 (Emergency)", "Code 2 (Silent)" };
        private static readonly object[] PursuitModes = new object[] { "Pursuit Backup", "Code 3 (Emergency)", "Code 2 (Silent)" };
        private static readonly object[] PursuitModesWithCallout = new object[] { "Callout Response", "Pursuit Backup", "Code 3 (Emergency)", "Code 2 (Silent)" };
        private static UIMenuListItem _responseModeNormal;
        private static UIMenuListItem _responseModeNormalWithCallout;
        private static UIMenuListItem _responseModePursuit;
        private static UIMenuListItem _responseModePursuitWithCallout;

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

        // Callout Response Items
        private static UIMenuItem _itemCalloutLocalCode3;
        private static UIMenuItem _itemCalloutLocalCode2;
        private static UIMenuItem _itemCalloutStateCode3;
        private static UIMenuItem _itemCalloutStateCode2;
        private static UIMenuItem _itemCalloutGroupCode3;
        private static UIMenuItem _itemCalloutAmbulanceCode3;
        private static UIMenuItem _itemCalloutAmbulanceCode2;
        private static UIMenuItem _itemCalloutFireCode3;
        private static UIMenuItem _itemCalloutFireCode2;

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

                try
                {
                    Events.OnCalloutFinished += OnLspdfrCalloutFinished;
                    Events.OnPursuitEnded += OnLspdfrPursuitEnded;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[BackupFeature] Could not hook LSPDFR events: {ex.Message}");
                }

                _backupFiber = GameFiber.StartNew(BackupProcessLoop, "Cadence - Backup Feature Fiber");
            }
        }

        public static void Stop()
        {
            List<GameFiber> fibersToWait;
            lock (_syncLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                Logger.Info("[BackupFeature] Stopping Backup feature and dismissing active units.");

                try
                {
                    Events.OnCalloutFinished -= OnLspdfrCalloutFinished;
                    Events.OnPursuitEnded -= OnLspdfrPursuitEnded;
                }
                catch { }

                CloseMenu();

                PurgeAllInternal(immediateDelete: false);

                fibersToWait = new List<GameFiber>(_trackedFibers);
                if (_backupFiber != null) fibersToWait.Add(_backupFiber);
            }

            // Bounded cooperative wait outside _syncLock
            uint waitStart = Game.GameTime;
            while (fibersToWait.Any(f => f != null && f.IsAlive) && Game.GameTime - waitStart < 500)
            {
                GameFiber.Sleep(25);
            }

            foreach (var f in fibersToWait)
            {
                try
                {
                    if (f != null && f.IsAlive) f.Abort();
                }
                catch { }
            }

            lock (_syncLock)
            {
                _trackedFibers.Clear();
                _backupFiber = null;
            }
        }

        public static void PurgeAll(bool immediateDelete = true)
        {
            lock (_syncLock)
            {
                Logger.Info($"[BackupFeature] Purging all active backup units (immediateDelete={immediateDelete}).");
                PurgeAllInternal(immediateDelete);
            }
        }

        public static int GetActiveCount()
        {
            lock (_syncLock)
            {
                var toClean = _activeUnits.Where(u => u == null || !u.IsValid() || u.State == BackupUnitSession.UnitState.Completed).ToList();
                foreach (var u in toClean)
                {
                    if (u != null) u.Dispose(immediateDelete: false);
                    _activeUnits.Remove(u);
                }
                return _activeUnits.Count;
            }
        }

        public static int GetActivePoliceResponderCount()
        {
            lock (_syncLock)
            {
                var toClean = _activeUnits.Where(u => u == null || !u.IsValid() || u.State == BackupUnitSession.UnitState.Completed).ToList();
                foreach (var u in toClean)
                {
                    if (u != null) u.Dispose(immediateDelete: false);
                    _activeUnits.Remove(u);
                }
                return _activeUnits.Count(u => u != null && u.IsValid() &&
                    u.State != BackupUnitSession.UnitState.Completed &&
                    u.State != BackupUnitSession.UnitState.Dismissing &&
                    u.ServiceType != BackupType.AmbulanceCode2 &&
                    u.ServiceType != BackupType.AmbulanceCode3 &&
                    u.ServiceType != BackupType.FireDeptCode2 &&
                    u.ServiceType != BackupType.FireDeptCode3);
            }
        }

        public static bool IsBackupOfficer(Ped ped)
        {
            if (ped == null || !ped.Exists()) return false;
            lock (_syncLock)
            {
                return _activeUnits.Any(u => u != null && (u.Driver == ped || u.Passenger == ped || (u.Officers != null && u.Officers.Contains(ped))));
            }
        }

        public static bool IsBackupCruiser(Vehicle cruiser)
        {
            if (cruiser == null || !cruiser.Exists()) return false;
            lock (_syncLock)
            {
                return _activeUnits.Any(u => u != null && u.Cruiser == cruiser);
            }
        }

        public static void RelinquishCruiser(Vehicle cruiser)
        {
            if (cruiser == null) return;
            BackupUnitSession sessionToDispose = null;
            lock (_syncLock)
            {
                _reservedCruisers.Remove(cruiser);
                sessionToDispose = _activeUnits.FirstOrDefault(u => u != null && u.Cruiser == cruiser);
                if (sessionToDispose != null)
                {
                    _activeUnits.Remove(sessionToDispose);
                }
            }
            if (sessionToDispose != null)
            {
                sessionToDispose.Dispose(immediateDelete: false);
            }
            UnitRegistry.Release(cruiser);
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

        public static bool TriggerCalloutResponse()
        {
            if (!TryGetCalloutOrWaypointPosition(out _, out _))
            {
                return false;
            }
            RequestCalloutBackup(BackupType.LocalPatrolCode3, isCode3: true);
            return true;
        }

        private static void OnLspdfrCalloutFinished(LHandle handle)
        {
            try
            {
                BackupUnitSession[] unitsSnapshot;
                lock (_syncLock)
                {
                    unitsSnapshot = _activeUnits.ToArray();
                }

                foreach (var unit in unitsSnapshot)
                {
                    if (unit != null && unit.IsCalloutResponse && unit.State != BackupUnitSession.UnitState.Dismissing && unit.State != BackupUnitSession.UnitState.Completed)
                    {
                        Logger.Info($"[BackupFeature] LSPDFR Callout finished. Holding callout responder {unit.Callsign} on scene standby until dismissed by player.");
                        unit.HoldOnSceneAfterCallout();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error holding callout units upon callout finish", ex);
            }
        }

        private static void OnLspdfrPursuitEnded(LHandle pursuit)
        {
            try
            {
                Logger.Info("[BackupFeature] LSPDFR OnPursuitEnded event intercepted. Transitioning units to scene standby.");
                BackupUnitSession[] unitsSnapshot;
                lock (_syncLock)
                {
                    unitsSnapshot = _activeUnits.ToArray();
                }

                foreach (var unit in unitsSnapshot)
                {
                    if (unit != null && unit.IsValid())
                    {
                        unit.HoldOnSceneAfterPursuit();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error holding backup units upon pursuit finish", ex);
            }
        }

        public static void DismissAll()
        {
            BackupUnitSession[] unitsSnapshot;
            lock (_syncLock)
            {
                unitsSnapshot = _activeUnits.ToArray();
            }

            if (unitsSnapshot.Length > 0)
            {
                Logger.Info($"[BackupFeature] Dismissing all active backup units ({unitsSnapshot.Length}).");

                foreach (var unit in unitsSnapshot)
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

        // ====================================================================
        // Background Process Fiber
        // ====================================================================

        private static void BackupProcessLoop()
        {
            while (_isRunning)
            {
                try
                {
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
                        InputHelper.SuppressMenuNavigationControls();
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
                }
                finally
                {
                    GameFiber.Sleep((_backupMenu != null && _backupMenu.Visible) ? 0 : 25);
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
            InputHelper.SuppressVehicleDuckIfXHeld(Settings.BackupMenuKey, Settings.BackupMenuModifierKey);

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

            BackupUnitSession[] unitsSnapshot;
            lock (_syncLock)
            {
                unitsSnapshot = _activeUnits.ToArray();
            }

            var toRemove = new List<BackupUnitSession>();

            foreach (var unit in unitsSnapshot)
            {
                try
                {
                    if (unit == null || !unit.IsValid())
                    {
                        if (unit != null) unit.Dispose(immediateDelete: false);
                        toRemove.Add(unit);
                        continue;
                    }

                    unit.Update(player, playerPos);

                    if (unit.State == BackupUnitSession.UnitState.Completed)
                    {
                        unit.Dispose(immediateDelete: false);
                        toRemove.Add(unit);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[BackupFeature] Error updating backup unit {unit?.Callsign}: {ex.Message}", ex);
                    if (unit != null)
                    {
                        try { unit.Dispose(immediateDelete: false); } catch { }
                        toRemove.Add(unit);
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

        private static void PurgeAllInternal(bool immediateDelete)
        {
            BackupUnitSession[] unitsSnapshot;
            lock (_syncLock)
            {
                unitsSnapshot = _activeUnits.ToArray();
                _activeUnits.Clear();
            }

            foreach (var unit in unitsSnapshot)
            {
                if (unit != null)
                {
                    unit.Dispose(immediateDelete);
                }
            }
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
                _backupMenu.ControlDisablingEnabled = true;
                _menuPool.ControlDisablingEnabled = true;
                _menuPool.Add(_backupMenu);

                _responseModeNormal = new UIMenuListItem("Response Mode", "Select backup authorization level.", NormalModes);
                _responseModeNormalWithCallout = new UIMenuListItem("Response Mode", "Select backup authorization level.", NormalModesWithCallout);
                _responseModePursuit = new UIMenuListItem("Response Mode", "Select backup authorization level.", PursuitModes);
                _responseModePursuitWithCallout = new UIMenuListItem("Response Mode", "Select backup authorization level.", PursuitModesWithCallout);

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

                // Callout Actions
                _itemCalloutLocalCode3 = new UIMenuItem("Local Patrol (Code 3)", "Dispatches ambient local police cruiser to the active waypoint/callout with sirens and lights.");
                _itemCalloutLocalCode2 = new UIMenuItem("Local Patrol (Code 2)", "Dispatches ambient local police cruiser to the active waypoint/callout with silent lightbars.");
                _itemCalloutStateCode3 = new UIMenuItem("State Patrol (Code 3)", "Dispatches ambient highway/state patrol cruiser to the active waypoint/callout with sirens and lights.");
                _itemCalloutStateCode2 = new UIMenuItem("State Patrol (Code 2)", "Dispatches ambient highway/state patrol cruiser to the active waypoint/callout with silent lightbars.");
                _itemCalloutGroupCode3 = new UIMenuItem("Group Callout (Code 3)", "Requests multi-unit tactical ambient reinforcement to the active waypoint/callout.");
                _itemCalloutAmbulanceCode3 = new UIMenuItem("Ambulance (Code 3)", "Dispatches paramedic ambulance to the active waypoint/callout with sirens and lightbars.");
                _itemCalloutAmbulanceCode2 = new UIMenuItem("Ambulance (Code 2)", "Dispatches paramedic ambulance to the active waypoint/callout with silent lightbars.");
                _itemCalloutFireCode3 = new UIMenuItem("Fire Dept (Code 3)", "Dispatches fire department engine to the active waypoint/callout with sirens and lightbars.");
                _itemCalloutFireCode2 = new UIMenuItem("Fire Dept (Code 2)", "Dispatches fire department engine to the active waypoint/callout with silent lightbars.");

                // Common
                _itemDismissAll = new UIMenuItem("Dismiss All", "Releases all active backup units and returns them to routine patrol.");

                _backupMenu.OnListChange += (sender, listItem, newIndex) =>
                {
                    if (listItem == _responseModeNormal || listItem == _responseModeNormalWithCallout ||
                        listItem == _responseModePursuit || listItem == _responseModePursuitWithCallout)
                    {
                        string selectedMode = listItem.Collection[newIndex].ToString();
                        RebuildActionsForMode(listItem, selectedMode);
                    }
                };

                _backupMenu.OnItemSelect += (sender, item, index) =>
                {
                    if (item is UIMenuListItem)
                    {
                        return;
                    }

                    // Only close on Dismiss All; allow calling multiple backup units without auto-closing menu
                    if (item == _itemDismissAll)
                    {
                        _backupMenu.Visible = false;
                        DismissAll();
                        return;
                    }

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
                    else if (item == _itemCalloutLocalCode3) RequestCalloutBackup(BackupType.LocalPatrolCode3, isCode3: true);
                    else if (item == _itemCalloutLocalCode2) RequestCalloutBackup(BackupType.LocalPatrolCode2, isCode3: false);
                    else if (item == _itemCalloutStateCode3) RequestCalloutBackup(BackupType.StatePatrolCode3, isCode3: true);
                    else if (item == _itemCalloutStateCode2) RequestCalloutBackup(BackupType.StatePatrolCode2, isCode3: false);
                    else if (item == _itemCalloutGroupCode3) RequestCalloutGroupBackup();
                    else if (item == _itemCalloutAmbulanceCode3) RequestCalloutBackup(BackupType.AmbulanceCode3, isCode3: true);
                    else if (item == _itemCalloutAmbulanceCode2) RequestCalloutBackup(BackupType.AmbulanceCode2, isCode3: false);
                    else if (item == _itemCalloutFireCode3) RequestCalloutBackup(BackupType.FireDeptCode3, isCode3: true);
                    else if (item == _itemCalloutFireCode2) RequestCalloutBackup(BackupType.FireDeptCode2, isCode3: false);
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
                else if (selectedMode == "Callout Response")
                {
                    _backupMenu.AddItem(_itemCalloutLocalCode3);
                    _backupMenu.AddItem(_itemCalloutLocalCode2);
                    _backupMenu.AddItem(_itemCalloutStateCode3);
                    _backupMenu.AddItem(_itemCalloutStateCode2);
                    _backupMenu.AddItem(_itemCalloutGroupCode3);
                    _backupMenu.AddItem(_itemCalloutAmbulanceCode3);
                    _backupMenu.AddItem(_itemCalloutAmbulanceCode2);
                    _backupMenu.AddItem(_itemCalloutFireCode3);
                    _backupMenu.AddItem(_itemCalloutFireCode2);
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

            bool hasCalloutOrWaypoint = TryGetCalloutOrWaypointPosition(out _, out string locName);

            UIMenuListItem modeItem;
            if (inPursuit)
            {
                modeItem = hasCalloutOrWaypoint ? _responseModePursuitWithCallout : _responseModePursuit;
            }
            else
            {
                modeItem = hasCalloutOrWaypoint ? _responseModeNormalWithCallout : _responseModeNormal;
            }

            // Always select first mode by default:
            // When callout or waypoint is active, index 0 is "Callout Response".
            // When not active, index 0 is "Code 3 (Emergency)" or "Pursuit Backup".
            modeItem.Index = 0;
            string selectedMode = modeItem.Collection[modeItem.Index].ToString();

            // Dynamic contextual subtitle feedback
            if (hasCalloutOrWaypoint)
            {
                _backupMenu.SubtitleText = $"~b~Responding: ~y~{locName}~s~";
            }
            else
            {
                _backupMenu.SubtitleText = inPursuit ? "~o~Pursuit Tactical Reinforcements~s~" : "~b~Ambient Backup Menu~s~";
            }

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
            StartTrackedFiber(() =>
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
                            LHandle activePursuit = null;
                            try { activePursuit = Functions.GetActivePursuit(); } catch { }
                            bool inPursuit = activePursuit != null && Functions.IsPursuitStillRunning(activePursuit);

                            int activePolice = GetActivePoliceResponderCount();
                            int pursuitCops = inPursuit ? PursuitFeature.GetActivePursuitUnitCount() : 0;

                            int configuredPanic = Settings.PanicButtonUnits;
                            int maxAllowedPanic = Math.Max(1, Settings.MaxPursuitUnits - activePolice);
                            int panicCount = inPursuit
                                ? Math.Max(1, Math.Min(configuredPanic, Math.Max(Settings.PanicButtonUnitsMin, Settings.MaxPursuitUnits - pursuitCops)))
                                : Math.Max(1, Math.Min(configuredPanic, maxAllowedPanic));

                            PlayPanicButtonSound();
                            AudioHelper.SafePlayPoliceAssistance(playerPos);

                            // Priority 0: Re-engage existing units already on scene or dismissing nearby (< 120m)
                            List<BackupUnitSession> nearbyPanicCandidates;
                            lock (_syncLock)
                            {
                                nearbyPanicCandidates = _activeUnits.Where(u => u != null && u.IsValid() &&
                                    u.ServiceType != BackupType.AmbulanceCode2 && u.ServiceType != BackupType.AmbulanceCode3 &&
                                    u.ServiceType != BackupType.FireDeptCode2 && u.ServiceType != BackupType.FireDeptCode3 &&
                                    u.Cruiser != null && u.Cruiser.Exists() && u.Cruiser.Position.DistanceTo(playerPos) <= 120.0f &&
                                    (u.State == BackupUnitSession.UnitState.Dismissing || u.State == BackupUnitSession.UnitState.ArrivedOnScene)).ToList();
                            }

                            int reengagedPanicCount = 0;
                            foreach (var u in nearbyPanicCandidates)
                            {
                                if (reengagedPanicCount >= panicCount) break;
                                u.Reengage(type, playerPos, isCode3: true);
                                reengagedPanicCount++;
                            }

                            int remainingPanic = panicCount - reengagedPanicCount;

                            if (reengagedPanicCount > 0 && remainingPanic == 0)
                            {
                                Game.DisplayNotification($"~r~Panic Backup:~s~ Re-engaged ~g~{reengagedPanicCount}~s~ on-scene unit(s)!");
                                Game.DisplayHelp($"~r~Panic Backup Units ({reengagedPanicCount})~s~ on scene are responding to your emergency!", 4000);
                            }
                            else if (reengagedPanicCount > 0)
                            {
                                Game.DisplayNotification($"~r~Panic Backup:~s~ Re-engaged ~g~{reengagedPanicCount}~s~ on scene, ~y~{remainingPanic}~s~ en route.");
                                Game.DisplayHelp($"~r~Panic Backup Units ({panicCount})~s~: {reengagedPanicCount} on scene, {remainingPanic} en route.", 4000);
                                DispatchUnitsSequence(type, remainingPanic, isCode3: true);
                            }
                            else
                            {
                                Game.DisplayNotification("Requesting ~r~Panic Backup~s~ to ~g~Dispatch~s~");
                                Game.DisplayHelp($"~r~Panic Backup Units ({panicCount})~s~ are on the way. ~g~Please wait~s~", 4000);
                                DispatchUnitsSequence(type, panicCount, isCode3: true);
                            }
                            break;

                        case BackupType.GroupBackup:
                            LHandle grpPursuit = null;
                            try { grpPursuit = Functions.GetActivePursuit(); } catch { }
                            bool grpInPursuit = grpPursuit != null && Functions.IsPursuitStillRunning(grpPursuit);

                            int grpActivePolice = GetActivePoliceResponderCount();
                            int grpPursuitCops = grpInPursuit ? PursuitFeature.GetActivePursuitUnitCount() : 0;

                            int configuredGroup = Settings.GroupBackupUnits;
                            int maxAllowedGroup = Math.Max(1, Settings.MaxPursuitUnits - grpActivePolice);
                            int groupCount = grpInPursuit
                                ? Math.Max(1, Math.Min(configuredGroup, Settings.MaxPursuitUnits - grpPursuitCops))
                                : Math.Max(1, Math.Min(configuredGroup, maxAllowedGroup));

                            AudioHelper.SafePlayPoliceAssistance(playerPos);

                            // Priority 0: Re-engage existing units already on scene or dismissing nearby (< 120m)
                            List<BackupUnitSession> nearbyGroupCandidates;
                            lock (_syncLock)
                            {
                                nearbyGroupCandidates = _activeUnits.Where(u => u != null && u.IsValid() &&
                                    u.ServiceType != BackupType.AmbulanceCode2 && u.ServiceType != BackupType.AmbulanceCode3 &&
                                    u.ServiceType != BackupType.FireDeptCode2 && u.ServiceType != BackupType.FireDeptCode3 &&
                                    u.Cruiser != null && u.Cruiser.Exists() && u.Cruiser.Position.DistanceTo(playerPos) <= 120.0f &&
                                    (u.State == BackupUnitSession.UnitState.Dismissing || u.State == BackupUnitSession.UnitState.ArrivedOnScene)).ToList();
                            }

                            int reengagedGroupCount = 0;
                            foreach (var u in nearbyGroupCandidates)
                            {
                                if (reengagedGroupCount >= groupCount) break;
                                u.Reengage(type, playerPos, Settings.GroupBackupCode3);
                                reengagedGroupCount++;
                            }

                            int remainingGroup = groupCount - reengagedGroupCount;

                            if (reengagedGroupCount > 0 && remainingGroup == 0)
                            {
                                Game.DisplayNotification($"~y~Group Backup:~s~ Re-engaged ~g~{reengagedGroupCount}~s~ on-scene unit(s)!");
                                Game.DisplayHelp($"~y~Group Backup Units ({reengagedGroupCount})~s~ on scene are responding to your assistance request!", 4000);
                            }
                            else if (reengagedGroupCount > 0)
                            {
                                Game.DisplayNotification($"~y~Group Backup:~s~ Re-engaged ~g~{reengagedGroupCount}~s~ on scene, ~y~{remainingGroup}~s~ en route.");
                                Game.DisplayHelp($"~y~Group Backup Units ({groupCount})~s~: {reengagedGroupCount} on scene, {remainingGroup} en route.", 4000);
                                DispatchUnitsSequence(type, remainingGroup, Settings.GroupBackupCode3);
                            }
                            else
                            {
                                Game.DisplayNotification("Requesting ~y~Group Backup~s~ to ~g~Dispatch~s~");
                                Game.DisplayHelp($"~y~Group Backup Units ({groupCount})~s~ are on the way. ~g~Please wait~s~", 4000);
                                DispatchUnitsSequence(type, groupCount, Settings.GroupBackupCode3);
                            }
                            break;

                        case BackupType.LocalPatrolCode2:
                            Game.DisplayNotification("Requesting ~y~Code 2~s~ Backup to ~g~Dispatch~s~");
                            Game.DisplayHelp("~y~Code 2 Backup Unit~s~ is on the way. ~g~Please wait~s~", 4000);
                            AudioHelper.SafePlayPoliceAssistance(playerPos);
                            DispatchSingleUnit(type, isCode3: false);
                            break;

                        case BackupType.LocalPatrolCode3:
                            Game.DisplayNotification("Requesting ~r~Code 3~s~ Backup to ~g~Dispatch~s~");
                            AudioHelper.SafePlayPoliceAssistance(playerPos);
                            DispatchSingleUnit(type, isCode3: true);
                            break;

                        case BackupType.StatePatrolCode2:
                            Game.DisplayNotification("Requesting ~y~State Patrol (Code 2)~s~ Backup to ~g~Dispatch~s~");
                            Game.DisplayHelp("~y~State Patrol Unit~s~ is on the way. ~g~Please wait~s~", 4000);
                            AudioHelper.SafePlayPoliceAssistance(playerPos);
                            DispatchSingleUnit(type, isCode3: false);
                            break;

                        case BackupType.StatePatrolCode3:
                            Game.DisplayNotification("Requesting ~r~State Patrol (Code 3)~s~ Backup to ~g~Dispatch~s~");
                            AudioHelper.SafePlayPoliceAssistance(playerPos);
                            DispatchSingleUnit(type, isCode3: true);
                            break;

                        case BackupType.AmbulanceCode2:
                            Game.DisplayNotification("Requesting ~g~Ambulance (EMS)~s~ Backup to ~g~Dispatch~s~");
                            AudioHelper.SafePlayAmbulanceAssistance(playerPos);
                            DispatchSingleUnit(type, isCode3: false);
                            break;

                        case BackupType.AmbulanceCode3:
                            Game.DisplayNotification("Requesting ~g~Ambulance (EMS)~s~ Backup to ~g~Dispatch~s~");
                            AudioHelper.SafePlayAmbulanceAssistance(playerPos);
                            DispatchSingleUnit(type, isCode3: true);
                            break;

                        case BackupType.FireDeptCode2:
                            Game.DisplayNotification("Requesting ~r~Fire Department~s~ Backup to ~g~Dispatch~s~");
                            AudioHelper.SafePlayFiretruckAssistance(playerPos);
                            DispatchSingleUnit(type, isCode3: false);
                            break;

                        case BackupType.FireDeptCode3:
                            Game.DisplayNotification("Requesting ~r~Fire Department~s~ Backup to ~g~Dispatch~s~");
                            AudioHelper.SafePlayFiretruckAssistance(playerPos);
                            DispatchSingleUnit(type, isCode3: true);
                            break;

                        case BackupType.LocalPursuit:
                            Game.DisplayNotification("Requesting ~r~Local Pursuit Interceptor~s~ to ~g~Dispatch~s~");
                            AudioHelper.SafePlayPoliceAssistance(playerPos);
                            PursuitFeature.RequestPursuitInterceptor(isHighway: false);
                            break;

                        case BackupType.StatePursuit:
                            Game.DisplayNotification("Requesting ~r~State Pursuit Interceptor~s~ to ~g~Dispatch~s~");
                            AudioHelper.SafePlayPoliceAssistance(playerPos);
                            PursuitFeature.RequestPursuitInterceptor(isHighway: true);
                            break;

                        case BackupType.GroupPursuit:
                            int groupPursuitCount = Settings.GroupBackupUnits;
                            Game.DisplayNotification("Requesting ~r~Group Pursuit Backup~s~ to ~g~Dispatch~s~");
                            AudioHelper.SafePlayPoliceAssistance(playerPos);
                            PursuitFeature.RequestGroupPursuit(groupPursuitCount);
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
                    if (!_activeUnits.Contains(unit))
                    {
                        _activeUnits.Add(unit);
                    }
                    if (unit.Cruiser != null)
                    {
                        _reservedCruisers.Remove(unit.Cruiser);
                    }
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
                        if (!_activeUnits.Contains(unit))
                        {
                            _activeUnits.Add(unit);
                        }
                        if (unit.Cruiser != null)
                        {
                            _reservedCruisers.Remove(unit.Cruiser);
                        }
                    }
                    dispatched++;
                    Logger.Info($"[BackupFeature] Dispatched unit {dispatched}/{count} ({unit.Callsign}) for {type}.");
                }

                // Pacing delay between multiple unit dispatches to prevent vehicle stacking (rapid for panic, staged for group)
                if (i < count - 1)
                {
                    GameFiber.Sleep(type == BackupType.PanicButton ? 650 : 1200);
                }
            }
        }

        // ====================================================================
        // Callout Response Orchestrator & Waypoint Dispatchers
        // ====================================================================

        public static void RequestCalloutBackup(BackupType type, bool isCode3)
        {
            StartTrackedFiber(() =>
            {
                try
                {
                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists()) return;

                    if (!TryGetCalloutOrWaypointPosition(out Vector3 targetPos, out string locName))
                    {
                        Game.DisplayNotification("~y~[Cadence]~s~ No active waypoint or callout location.");
                        Game.DisplayHelp("Place a ~y~waypoint~s~ on your radar or accept an LSPDFR callout to dispatch units.", 4000);
                        return;
                    }

                    PlayPlayerRadioAnimation();

                    string typeName = GetBackupTypeDisplayTitle(type);
                    string codeColor = isCode3 ? "~r~" : "~y~";
                    string codeName = isCode3 ? "Code 3" : "Code 2";

                    Game.DisplayNotification($"Requesting {codeColor}{typeName} ({codeName})~s~ to ~g~{locName}~s~");
                    Game.DisplayHelp($"{codeColor}{typeName}~s~ is en route to ~g~{locName}~s~. ~g~Please wait~s~", 4000);

                    if (type == BackupType.AmbulanceCode2 || type == BackupType.AmbulanceCode3)
                    {
                        AudioHelper.SafePlayAmbulanceAssistance(targetPos);
                    }
                    else if (type == BackupType.FireDeptCode2 || type == BackupType.FireDeptCode3)
                    {
                        AudioHelper.SafePlayFiretruckAssistance(targetPos);
                    }
                    else
                    {
                        AudioHelper.SafePlayPoliceAssistance(targetPos);
                    }

                    DispatchCalloutUnit(type, targetPos, isCode3);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[BackupFeature] Error processing RequestCalloutBackup({type})", ex);
                }
            }, "Cadence - Callout Dispatch Fiber");
        }

        public static void RequestCalloutGroupBackup()
        {
            StartTrackedFiber(() =>
            {
                try
                {
                    Ped player = Game.LocalPlayer?.Character;
                    if (player == null || !player.Exists()) return;

                    if (!TryGetCalloutOrWaypointPosition(out Vector3 targetPos, out string locName))
                    {
                        Game.DisplayNotification("~y~[Cadence]~s~ No active waypoint or callout location.");
                        Game.DisplayHelp("Place a ~y~waypoint~s~ on your radar or accept an LSPDFR callout to dispatch units.", 4000);
                        return;
                    }

                    PlayPlayerRadioAnimation();

                    int activePolice = GetActivePoliceResponderCount();
                    int maxAllowedGroup = Math.Max(1, Settings.MaxPursuitUnits - activePolice);
                    int groupCount = Math.Min(Settings.GroupBackupUnits, maxAllowedGroup);
                    bool isCode3 = Settings.GroupBackupCode3;
                    string codeColor = isCode3 ? "~r~" : "~y~";

                    Game.DisplayNotification($"Requesting {codeColor}Group Callout Backup ({groupCount} Units)~s~ to ~g~{locName}~s~");
                    Game.DisplayHelp($"{codeColor}Group Callout Units ({groupCount})~s~ are on the way to ~g~{locName}~s~.", 4000);
                    AudioHelper.SafePlayPoliceAssistance(targetPos);

                    DispatchCalloutUnitsSequence(BackupType.GroupBackup, targetPos, groupCount, isCode3);
                }
                catch (Exception ex)
                {
                    Logger.Error("[BackupFeature] Error processing RequestCalloutGroupBackup", ex);
                }
            }, "Cadence - Callout Group Dispatch Fiber");
        }

        private static void DispatchCalloutUnit(BackupType type, Vector3 destination, bool isCode3)
        {
            Ped player = Game.LocalPlayer?.Character;
            if (player == null || !player.Exists()) return;
            Vector3 playerPos = player.Position;

            BackupUnitSession unit = ResolveOrSpawnCalloutUnit(type, playerPos, destination, isCode3);
            if (unit != null && unit.IsValid())
            {
                lock (_syncLock)
                {
                    if (!_activeUnits.Contains(unit))
                    {
                        _activeUnits.Add(unit);
                    }
                    if (unit.Cruiser != null)
                    {
                        _reservedCruisers.Remove(unit.Cruiser);
                    }
                }
                Logger.Info($"[BackupFeature] Dispatched callout {type} unit {unit.Callsign} (Code 3: {isCode3}) to destination {destination}.");
            }
        }

        private static void DispatchCalloutUnitsSequence(BackupType type, Vector3 destination, int count, bool isCode3)
        {
            Ped player = Game.LocalPlayer?.Character;
            if (player == null || !player.Exists()) return;
            Vector3 playerPos = player.Position;

            for (int i = 0; i < count; i++)
            {
                BackupUnitSession unit = ResolveOrSpawnCalloutUnit(type, playerPos, destination, isCode3, unitIndex: i);
                if (unit != null && unit.IsValid())
                {
                    lock (_syncLock)
                    {
                        if (!_activeUnits.Contains(unit))
                        {
                            _activeUnits.Add(unit);
                        }
                        if (unit.Cruiser != null)
                        {
                            _reservedCruisers.Remove(unit.Cruiser);
                        }
                    }
                    Logger.Info($"[BackupFeature] Dispatched callout sequence unit #{i + 1} {unit.Callsign} (Code 3: {isCode3}) to destination {destination}.");
                }
                GameFiber.Sleep(600);
            }
        }

        private static BackupUnitSession ResolveOrSpawnCalloutUnit(BackupType type, Vector3 playerPos, Vector3 destination, bool isCode3, int unitIndex = 0)
        {
            bool isAmbulance = type == BackupType.AmbulanceCode2 || type == BackupType.AmbulanceCode3;
            bool isFire = type == BackupType.FireDeptCode2 || type == BackupType.FireDeptCode3;

            // Priority 0: Check for existing dismissing units nearby that can be re-engaged!
            BackupUnitSession reengaged = TryReengageDismissingUnit(playerPos, type, isCode3, destinationOverride: destination, isCallout: true);
            if (reengaged != null)
            {
                return reengaged;
            }

            // Step 1: Ambient-First Commandeering Query within 350m (near player or near callout destination)
            if (Settings.AllowAmbientCommandeer)
            {
                Vehicle candidate = FindCandidateAmbientVehicle(playerPos, type, isAmbulance, isFire);
                if (candidate == null && destination != Vector3.Zero && destination.DistanceTo(playerPos) > 150.0f)
                {
                    candidate = FindCandidateAmbientVehicle(destination, type, isAmbulance, isFire);
                }

                if (candidate != null && candidate.Exists())
                {
                    lock (_syncLock)
                    {
                        _reservedCruisers.Add(candidate);
                    }

                    BackupUnitSession commandeered = null;
                    try
                    {
                        commandeered = CommandeerAmbientVehicle(candidate, playerPos, type, isCode3, destinationOverride: destination, isCallout: true);
                        if (commandeered != null && commandeered.IsValid())
                        {
                            Logger.Info($"[BackupFeature] Commandeered ambient vehicle {candidate.Model.Name} (0x{candidate.Handle:X}) for callout {type} to {destination}.");
                            return commandeered;
                        }
                    }
                    finally
                    {
                        if (commandeered == null || !commandeered.IsValid())
                        {
                            lock (_syncLock)
                            {
                                _reservedCruisers.Remove(candidate);
                            }
                            UnitRegistry.Release(candidate, UnitRegistry.OwnerBackup);
                        }
                    }
                }
            }

            // Step 2: Fallback Spawner if ambient candidate not found or not permitted
            if (Settings.AllowFallbackSpawning)
            {
                return SpawnFallbackCalloutUnit(playerPos, destination, type, isCode3, unitIndex);
            }

            Logger.Warn($"[BackupFeature] No ambient unit found and FallbackSpawning is disabled for callout {type}.");
            return null;
        }

        private static BackupUnitSession SpawnFallbackCalloutUnit(Vector3 playerPos, Vector3 destination, BackupType type, bool isCode3, int unitIndex = 0)
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
                    Logger.Warn("[BackupFeature] Could not resolve valid road node for fallback callout spawner.");
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
                        try
                        {
                            var ubResult = UltimateBackupService.SpawnAmbulanceUnit(spawnPos, 2);
                            if (ubResult != null)
                            {
                                if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                                {
                                    amb = ubResult.Item1;
                                    medic = ubResult.Item2[0];
                                    if (ubResult.Item2.Count > 1) passMedic = ubResult.Item2[1];
                                }
                                else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                                {
                                    TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                                }
                            }
                        }
                        catch (Exception ubEx)
                        {
                            Logger.Warn($"[BackupFeature] Ultimate Backup Ambulance dispatch error: {ubEx.Message}");
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
                        if (nativeResult != null)
                        {
                            if (nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                            {
                                amb = nativeResult.Item1;
                                medic = nativeResult.Item2[0];
                            }
                            else if (nativeResult.Item1 != null && nativeResult.Item1.Exists())
                            {
                                TeardownHelper.SafeDismissVehicle(nativeResult.Item1, delete: true);
                            }
                        }
                    }

                    if (amb != null && amb.Exists() && medic != null && medic.Exists())
                    {
                        amb.Heading = spawnHeading;
                        WorldUtils.SecureEntityGround(amb);
                        return ConfigureBackupSession(amb, medic, passMedic, destination, type, isCode3, callsign, isCommandeer: false, isCallout: true);
                    }

                    if (amb != null && amb.Exists())
                    {
                        TeardownHelper.SafeDismissVehicle(amb, delete: true);
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
                        try
                        {
                            var ubResult = UltimateBackupService.SpawnFireTruckUnit(spawnPos, 2);
                            if (ubResult != null)
                            {
                                if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                                {
                                    fire = ubResult.Item1;
                                    fireman = ubResult.Item2[0];
                                    if (ubResult.Item2.Count > 1) passFireman = ubResult.Item2[1];
                                }
                                else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                                {
                                    TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                                }
                            }
                        }
                        catch (Exception ubEx)
                        {
                            Logger.Warn($"[BackupFeature] Ultimate Backup Fire dispatch error: {ubEx.Message}");
                        }
                    }

                    if (fire == null || !fire.Exists() || fireman == null || !fireman.Exists())
                    {
                        if (Settings.UseUltimateBackup && !Settings.AllowFallback)
                        {
                            Logger.Debug("[BackupFeature] Ultimate Backup did not return firetruck and AllowFallback is false. Aborting spawn.");
                            return null;
                        }

                        var nativeResult = UltimateBackupService.SpawnNativeFireUnit(spawnPos, spawnHeading);
                        if (nativeResult != null)
                        {
                            if (nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                            {
                                fire = nativeResult.Item1;
                                fireman = nativeResult.Item2[0];
                            }
                            else if (nativeResult.Item1 != null && nativeResult.Item1.Exists())
                            {
                                TeardownHelper.SafeDismissVehicle(nativeResult.Item1, delete: true);
                            }
                        }
                    }

                    if (fire != null && fire.Exists() && fireman != null && fireman.Exists())
                    {
                        fire.Heading = spawnHeading;
                        WorldUtils.SecureEntityGround(fire);
                        return ConfigureBackupSession(fire, fireman, passFireman, destination, type, isCode3, callsign, isCommandeer: false, isCallout: true);
                    }

                    if (fire != null && fire.Exists())
                    {
                        TeardownHelper.SafeDismissVehicle(fire, delete: true);
                    }
                    return null;
                }

                // Police Backup Spawner (Local vs State)
                bool isState = type == BackupType.StatePatrolCode2 || type == BackupType.StatePatrolCode3;
                bool isPanicOrGroup = type == BackupType.PanicButton || type == BackupType.GroupBackup;

                Vehicle cruiser = null;
                Ped driver = null;
                Ped passenger = null;
                List<Ped> extraOfficers = null;

                if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable)
                {
                    try
                    {
                        int reqOfficers = isPanicOrGroup ? 2 : 1;
                        var ubResult = isState
                            ? UltimateBackupService.SpawnStatePatrol(spawnPos, reqOfficers)
                            : UltimateBackupService.SpawnLocalPatrol(spawnPos, reqOfficers);

                        if (ubResult != null)
                        {
                            if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                            {
                                cruiser = ubResult.Item1;
                                driver = ubResult.Item2[0];
                                if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                                if (ubResult.Item2.Count > 2) extraOfficers = ubResult.Item2.Skip(2).ToList();
                            }
                            else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                            {
                                TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                            }
                        }
                    }
                    catch (Exception ubEx)
                    {
                        Logger.Warn($"[BackupFeature] Ultimate Backup dispatch error: {ubEx.Message}");
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

                    if (nativeResult != null)
                    {
                        if (nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                        {
                            cruiser = nativeResult.Item1;
                            driver = nativeResult.Item2[0];
                            if (nativeResult.Item2.Count > 1) passenger = nativeResult.Item2[1];
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

                if (cruiser != null && cruiser.Exists() && driver != null && driver.Exists())
                {
                    cruiser.Heading = spawnHeading;
                    WorldUtils.SecureEntityGround(cruiser);
                    return ConfigureBackupSession(cruiser, driver, passenger, destination, type, isCode3, callsign, isCommandeer: false, extraOfficers, isCallout: true);
                }

                if (cruiser != null && cruiser.Exists())
                {
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error spawning fallback callout unit", ex);
            }

            return null;
        }

        public static bool TryGetCalloutOrWaypointPosition(out Vector3 targetPos, out string locationName)
        {
            targetPos = Vector3.Zero;
            locationName = "Waypoint";

            try
            {
                // 1. High-priority: Player Map Waypoint
                if (NativeFunction.Natives.IS_WAYPOINT_ACTIVE<bool>())
                {
                    int blip = NativeFunction.Natives.GET_FIRST_BLIP_INFO_ID<int>(8);
                    if (blip != 0 && NativeFunction.Natives.DOES_BLIP_EXIST<bool>(blip))
                    {
                        Vector3 rawCoords = NativeFunction.Natives.GET_BLIP_INFO_ID_COORD<Vector3>(blip);
                        if (rawCoords != Vector3.Zero && (Math.Abs(rawCoords.X) > 0.01f || Math.Abs(rawCoords.Y) > 0.01f))
                        {
                            targetPos = GetRoadsideCurbPosition(rawCoords);
                            locationName = "Map Waypoint";
                            return true;
                        }
                    }
                }

                // 2. Fallback: Active LSPDFR Callout Scene
                if (Functions.IsCalloutRunning())
                {
                    LHandle currentCallout = Functions.GetCurrentCallout();
                    if (currentCallout != null)
                    {
                        CalloutAcceptanceState state = Functions.GetCalloutAcceptanceState(currentCallout);
                        if (state == CalloutAcceptanceState.Running)
                        {
                            Callout callout = GetCalloutFromHandle(currentCallout);
                            if (callout != null && callout.CalloutPosition != Vector3.Zero)
                            {
                                targetPos = GetRoadsideCurbPosition(callout.CalloutPosition);
                                string friendly = "";
                                try { friendly = Functions.GetCalloutFriendlyName(currentCallout); } catch { }
                                locationName = !string.IsNullOrEmpty(friendly) ? friendly : (!string.IsNullOrEmpty(callout.FriendlyName) ? callout.FriendlyName : "Callout Scene");
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error resolving callout or waypoint position", ex);
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Callout GetCalloutFromHandle(LHandle handle)
        {
            if (handle != null)
            {
                try
                {
                    var prop = handle.GetType().GetProperty("Object", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null && prop.GetIndexParameters().Length == 0)
                    {
                        try
                        {
                            object obj = prop.GetValue(handle);
                            if (obj is Callout callout) return callout;
                        }
                        catch { }
                    }

                    foreach (PropertyInfo runtimeProperty in handle.GetType().GetRuntimeProperties())
                    {
                        if (runtimeProperty != null && runtimeProperty.GetIndexParameters().Length == 0 && runtimeProperty.CanRead)
                        {
                            try
                            {
                                if (runtimeProperty.GetValue(handle) is Callout callout)
                                {
                                    return callout;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static string GetBackupTypeDisplayTitle(BackupType type)
        {
            switch (type)
            {
                case BackupType.LocalPatrolCode2:
                case BackupType.LocalPatrolCode3:
                    return "Local Patrol";
                case BackupType.StatePatrolCode2:
                case BackupType.StatePatrolCode3:
                    return "State Patrol";
                case BackupType.AmbulanceCode2:
                case BackupType.AmbulanceCode3:
                    return "Ambulance (EMS)";
                case BackupType.FireDeptCode2:
                case BackupType.FireDeptCode3:
                    return "Fire Department";
                case BackupType.GroupBackup:
                    return "Group Backup";
                case BackupType.PanicButton:
                    return "Panic Button";
                case BackupType.LocalPursuit:
                    return "Local Pursuit Interceptor";
                case BackupType.StatePursuit:
                    return "State Pursuit Interceptor";
                case BackupType.GroupPursuit:
                    return "Group Pursuit Backup";
                default:
                    return "Backup Unit";
            }
        }

        // ====================================================================
        // Ambient-First Commandeering Architecture with Fallback Spawner
        // ====================================================================

        private static BackupUnitSession TryReengageDismissingUnit(Vector3 targetPos, BackupType type, bool isCode3, Vector3? destinationOverride = null, bool isCallout = false)
        {
            try
            {
                bool isAmbulance = type == BackupType.AmbulanceCode2 || type == BackupType.AmbulanceCode3;
                bool isFire = type == BackupType.FireDeptCode2 || type == BackupType.FireDeptCode3;
                bool isStateRequested = type == BackupType.StatePatrolCode2 || type == BackupType.StatePatrolCode3 || type == BackupType.StatePursuit;
                bool isLocalRequested = type == BackupType.LocalPatrolCode2 || type == BackupType.LocalPatrolCode3 || type == BackupType.LocalPursuit;

                Vector3 targetLocation = destinationOverride.HasValue ? destinationOverride.Value : targetPos;

                BackupUnitSession bestCandidate = null;
                lock (_syncLock)
                {
                    float bestDist = float.MaxValue;

                    foreach (var unit in _activeUnits)
                    {
                        if (unit == null || !unit.CanReengage() || unit.Cruiser == null || !unit.Cruiser.Exists()) continue;

                        // Agency / service type matching
                        bool unitIsAmbulance = unit.ServiceType == BackupType.AmbulanceCode2 || unit.ServiceType == BackupType.AmbulanceCode3;
                        bool unitIsFire = unit.ServiceType == BackupType.FireDeptCode2 || unit.ServiceType == BackupType.FireDeptCode3;

                        if (isAmbulance)
                        {
                            if (!unitIsAmbulance) continue;
                        }
                        else if (isFire)
                        {
                            if (!unitIsFire) continue;
                        }
                        else
                        {
                            // Police call: must NOT be ambulance or fire
                            if (unitIsAmbulance || unitIsFire) continue;

                            string vName = unit.Cruiser.Model.Name.ToLowerInvariant();
                            bool isHighwayModel = vName.Contains("chp") || vName.Contains("hway") ||
                                                  vName.Contains("highway") || vName.Contains("sahp") ||
                                                  vName.Contains("state");

                            if (isLocalRequested && isHighwayModel) continue;
                            if (isStateRequested && !isHighwayModel && !WorldUtils.IsHighway(unit.Cruiser.Position)) continue;
                        }

                        float dist = isCallout
                            ? Math.Min(unit.Cruiser.Position.DistanceTo(targetLocation), unit.Cruiser.Position.DistanceTo(targetPos))
                            : unit.Cruiser.Position.DistanceTo(targetLocation);
                        // Re-engage units within reasonable proximity (up to 300m)
                        if (dist > 300.0f) continue;

                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestCandidate = unit;
                        }
                    }
                }

                if (bestCandidate != null)
                {
                    bestCandidate.Reengage(type, targetLocation, isCode3, isCallout);
                    return bestCandidate;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error in TryReengageDismissingUnit", ex);
            }

            return null;
        }

        private static BackupUnitSession ResolveOrSpawnUnit(BackupType type, Vector3 playerPos, bool isCode3, int unitIndex = 0)
        {
            bool isAmbulance = type == BackupType.AmbulanceCode2 || type == BackupType.AmbulanceCode3;
            bool isFire = type == BackupType.FireDeptCode2 || type == BackupType.FireDeptCode3;

            // Priority 0: Check for existing dismissing units on scene / nearby that can be re-engaged!
            BackupUnitSession reengaged = TryReengageDismissingUnit(playerPos, type, isCode3, destinationOverride: null, isCallout: false);
            if (reengaged != null)
            {
                return reengaged;
            }

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

                    BackupUnitSession commandeered = null;
                    try
                    {
                        commandeered = CommandeerAmbientVehicle(candidate, playerPos, type, isCode3);
                        if (commandeered != null && commandeered.IsValid())
                        {
                            Logger.Info($"[BackupFeature] Commandeered ambient vehicle {candidate.Model.Name} (0x{candidate.Handle:X}) for {type}.");
                            return commandeered;
                        }
                    }
                    finally
                    {
                        if (commandeered == null || !commandeered.IsValid())
                        {
                            lock (_syncLock)
                            {
                                _reservedCruisers.Remove(candidate);
                            }
                            UnitRegistry.Release(candidate, UnitRegistry.OwnerBackup);
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

                    // Screen out vehicles already in pursuit or claimed by other owners
                    if (PursuitFeature.IsPursuitCruiser(veh)) continue;
                    if (UnitRegistry.IsClaimedBy(veh, UnitRegistry.OwnerBackup) || UnitRegistry.IsClaimedBy(veh, UnitRegistry.OwnerPursuit)) continue;

                    // Ensure not already tracked or reserved in active backup units
                    lock (_syncLock)
                    {
                        if (_activeUnits.Any(u => u.Cruiser == veh && u.State != BackupUnitSession.UnitState.Dismissing && u.State != BackupUnitSession.UnitState.Completed) || _reservedCruisers.Contains(veh)) continue;
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

                    // Guard against hijacking prisoner transport units (police only)
                    if (!isAmbulance && !isFire)
                    {
                        bool hasPrisoner = false;
                        try
                        {
                            Ped[] occupants = veh.Occupants;
                            if (occupants != null)
                            {
                                foreach (Ped occ in occupants)
                                {
                                    if (occ == null || !occ.Exists()) continue;
                                    if (Functions.IsPedArrested(occ) || occ.IsCuffed)
                                    {
                                        hasPrisoner = true;
                                        break;
                                    }
                                    if (occ != veh.Driver && !Functions.IsPedACop(occ) && occ.RelationshipGroup != "COP")
                                    {
                                        hasPrisoner = true;
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                        if (hasPrisoner) continue;

                        try
                        {
                            Entity[] nearbyPeds = World.GetEntities(veh.Position, 15.0f, GetEntitiesFlags.ConsiderHumanPeds);
                            if (nearbyPeds != null && nearbyPeds.OfType<Ped>().Any(p => p != null && p.Exists() && (Functions.IsPedArrested(p) || p.IsCuffed)))
                            {
                                continue;
                            }
                        }
                        catch { }
                    }

                    // Agency matching: prevent highway units responding to local police calls and vice-versa
                    bool isStateRequested = type == BackupType.StatePatrolCode2 || type == BackupType.StatePatrolCode3;
                    bool isLocalRequested = type == BackupType.LocalPatrolCode2 || type == BackupType.LocalPatrolCode3;
                    bool isHighwayModel = vName.Contains("chp") || vName.Contains("hway") || 
                                          vName.Contains("highway") || vName.Contains("sahp") || 
                                          vName.Contains("state");

                    if (isLocalRequested && isHighwayModel)
                    {
                        continue;
                    }

                    if (isStateRequested && !isHighwayModel && !WorldUtils.IsHighway(veh.Position))
                    {
                        continue;
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
                        if (inPursuit || driver.IsInCombat) continue;

                        bool isBusy = false;
                        try { isBusy = Functions.IsCopBusy(driver, false); } catch { }
                        if (isBusy) continue;

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

                            bool isOfficerBusy = false;
                            try { isOfficerBusy = Functions.IsCopBusy(designatedOfficer, false); } catch { }
                            if (isOfficerBusy) continue;

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

                if (bestCandidate != null)
                {
                    ReleaseFromOtherFeatures(bestCandidate);
                    if (!UnitRegistry.TryClaim(bestCandidate, UnitRegistry.OwnerBackup))
                    {
                        return null;
                    }
                    return bestCandidate;
                }

                return null;
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
            try { PursuitFeature.RelinquishCruiser(cruiser); } catch { }
            try { AssistFeature.RelinquishCruiser(cruiser); } catch { }
            try { UnitRegistry.Release(cruiser); } catch { }

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
                if (Functions.IsPedInPursuit(officer))
                {
                    Functions.RemovePedFromPursuit(officer);
                }
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

        private static BackupUnitSession CommandeerAmbientVehicle(Vehicle cruiser, Vector3 playerPos, BackupType type, bool isCode3, Vector3? destinationOverride = null, bool isCallout = false)
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

                Vector3 targetLocation = destinationOverride.HasValue ? destinationOverride.Value : playerPos;
                bool isAlreadyOnScene = cruiser.Position.DistanceTo(targetLocation) <= 35.0f;

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
                Vector3 destination = destinationOverride.HasValue
                    ? destinationOverride.Value
                    : (isAlreadyOnScene ? cruiser.Position : GetRoadsideCurbPosition(playerPos));

                return ConfigureBackupSession(cruiser, driver, passenger, destination, type, isCode3, callsign, isCommandeer: true, additionalOfficers: null, isAlreadyOnScene: isAlreadyOnScene, isCallout: isCallout);
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

            // High-speed pursuit dynamic stagger:
            // When player is travelling fast (> 10 m/s / ~22 mph), spawn tighter (50m - 90m) so units can actually catch up
            float baseDist = (player.Speed > 10.0f) ? 50.0f : 80.0f;
            float staggerPerUnit = (player.Speed > 10.0f) ? 15.0f : 25.0f;
            float behindDist = baseDist + (unitIndex * staggerPerUnit);

            // Lateral offset: alternate left and right by 20m
            float sideOffset = (unitIndex == 0) ? 0f : (unitIndex % 2 == 1 ? 20.0f * ((unitIndex + 1) / 2) : -20.0f * ((unitIndex + 1) / 2));

            Vector3 probeCenter = player.GetOffsetPosition(new Vector3(sideOffset, -behindDist, 0f));
            float minSafeDist = (player.Speed > 10.0f) ? 40.0f : 60.0f;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                Vector3 testPos = World.GetNextPositionOnStreet(probeCenter);
                if (testPos != Vector3.Zero && !WorldUtils.IsSubterraneanOrVoid(testPos))
                {
                    float dist = testPos.DistanceTo(playerPos);
                    // Ensure the spawn point is at safe distance from the player
                    if (dist >= minSafeDist && WorldUtils.IsAreaClear(testPos, 8.0f))
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
                probeCenter = player.GetOffsetPosition(new Vector3(sideOffset + (attempt * 12.0f), -(behindDist + (attempt * 10.0f)), 0f));
            }

            Vector3 fallbackProbe = player.GetOffsetPosition(new Vector3(0f, -baseDist, 0f));
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
                        try
                        {
                            var ubResult = UltimateBackupService.SpawnAmbulanceUnit(spawnPos, 2);
                            if (ubResult != null)
                            {
                                if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                                {
                                    amb = ubResult.Item1;
                                    medic = ubResult.Item2[0];
                                    if (ubResult.Item2.Count > 1) passMedic = ubResult.Item2[1];
                                }
                                else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                                {
                                    TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                                }
                            }
                        }
                        catch (Exception ubEx)
                        {
                            Logger.Warn($"[BackupFeature] Ultimate Backup Ambulance dispatch error: {ubEx.Message}");
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
                        if (nativeResult != null)
                        {
                            if (nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                            {
                                amb = nativeResult.Item1;
                                medic = nativeResult.Item2[0];
                            }
                            else if (nativeResult.Item1 != null && nativeResult.Item1.Exists())
                            {
                                TeardownHelper.SafeDismissVehicle(nativeResult.Item1, delete: true);
                            }
                        }
                    }

                    if (amb != null && amb.Exists() && medic != null && medic.Exists())
                    {
                        amb.Heading = spawnHeading;
                        WorldUtils.SecureEntityGround(amb);
                        Vector3 destination = GetRoadsideCurbPosition(playerPos);
                        return ConfigureBackupSession(amb, medic, passMedic, destination, type, isCode3, callsign, isCommandeer: false);
                    }

                    if (amb != null && amb.Exists())
                    {
                        TeardownHelper.SafeDismissVehicle(amb, delete: true);
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
                        try
                        {
                            var ubResult = UltimateBackupService.SpawnFireTruckUnit(spawnPos, 2);
                            if (ubResult != null)
                            {
                                if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                                {
                                    fire = ubResult.Item1;
                                    fireman = ubResult.Item2[0];
                                    if (ubResult.Item2.Count > 1) passFireman = ubResult.Item2[1];
                                }
                                else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                                {
                                    TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                                }
                            }
                        }
                        catch (Exception ubEx)
                        {
                            Logger.Warn($"[BackupFeature] Ultimate Backup FireTruck dispatch error: {ubEx.Message}");
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
                        if (nativeResult != null)
                        {
                            if (nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                            {
                                fire = nativeResult.Item1;
                                fireman = nativeResult.Item2[0];
                            }
                            else if (nativeResult.Item1 != null && nativeResult.Item1.Exists())
                            {
                                TeardownHelper.SafeDismissVehicle(nativeResult.Item1, delete: true);
                            }
                        }
                    }

                    if (fire != null && fire.Exists() && fireman != null && fireman.Exists())
                    {
                        fire.Heading = spawnHeading;
                        WorldUtils.SecureEntityGround(fire);
                        Vector3 destination = GetRoadsideCurbPosition(playerPos);
                        return ConfigureBackupSession(fire, fireman, passFireman, destination, type, isCode3, callsign, isCommandeer: false);
                    }

                    if (fire != null && fire.Exists())
                    {
                        TeardownHelper.SafeDismissVehicle(fire, delete: true);
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

                    if (ubResult != null)
                    {
                        if (ubResult.Item1 != null && ubResult.Item1.Exists() && ubResult.Item2 != null && ubResult.Item2.Count > 0)
                        {
                            cruiser = ubResult.Item1;
                            driver = ubResult.Item2[0];
                            if (ubResult.Item2.Count > 1) passenger = ubResult.Item2[1];
                            if (ubResult.Item2.Count > 2) extraOfficers = ubResult.Item2.Skip(2).ToList();
                        }
                        else if (ubResult.Item1 != null && ubResult.Item1.Exists())
                        {
                            TeardownHelper.SafeDismissVehicle(ubResult.Item1, delete: true);
                        }
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
                    if (nativeResult != null)
                    {
                        if (nativeResult.Item1 != null && nativeResult.Item1.Exists() && nativeResult.Item2 != null && nativeResult.Item2.Count > 0)
                        {
                            cruiser = nativeResult.Item1;
                            driver = nativeResult.Item2[0];
                            if (nativeResult.Item2.Count > 1) passenger = nativeResult.Item2[1];
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

                if (cruiser != null && cruiser.Exists() && driver != null && driver.Exists())
                {
                    cruiser.Heading = spawnHeading;
                    WorldUtils.SecureEntityGround(cruiser);
                    Vector3 destination = GetRoadsideCurbPosition(playerPos);
                    return ConfigureBackupSession(cruiser, driver, passenger, destination, type, isCode3, callsign, isCommandeer: false, extraOfficers);
                }

                if (cruiser != null && cruiser.Exists())
                {
                    TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[BackupFeature] Error spawning fallback backup unit", ex);
            }

            return null;
        }

        private static BackupUnitSession ConfigureBackupSession(Vehicle cruiser, Ped driver, Ped passenger, Vector3 destination, BackupType type, bool isCode3, string callsign, bool isCommandeer, IEnumerable<Ped> additionalOfficers = null, bool isAlreadyOnScene = false, bool isCallout = false)
        {
            Blip blip = null;
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

                UnitRegistry.TryClaim(cruiser, UnitRegistry.OwnerBackup);
                UnitRegistry.TryClaim(driver, UnitRegistry.OwnerBackup);
                if (passenger != null && passenger.Exists()) UnitRegistry.TryClaim(passenger, UnitRegistry.OwnerBackup);
                if (additionalOfficers != null)
                {
                    foreach (var ofc in additionalOfficers)
                    {
                        if (ofc != null && ofc.Exists()) UnitRegistry.TryClaim(ofc, UnitRegistry.OwnerBackup);
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

                // Purge any preexisting blips from cruiser and officers before attaching new emergency blip
                BlipService.SafeDeleteEntityBlip(cruiser);
                BlipService.SafeDeleteEntityBlip(driver);
                if (passenger != null) BlipService.SafeDeleteEntityBlip(passenger);

                // Attach flashing emergency blip or purple callout response blip
                string labelSuffix = isCode3 ? "CODE 3" : "CODE 2";
                blip = isCallout
                    ? BlipService.AttachCalloutResponseBlip(cruiser, callsign)
                    : BlipService.AttachEmergencyBlip(cruiser, $"{callsign} [{labelSuffix}]");

                var session = new BackupUnitSession(cruiser, driver, passenger, destination, blip, callsign, type, isCode3, isCommandeer, additionalOfficers, isAlreadyOnScene, isCallout: isCallout);

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
                if (blip != null && blip.Exists())
                {
                    BlipService.SafeDeleteBlip(ref blip);
                }
                if (!isCommandeer)
                {
                    if (cruiser != null && cruiser.Exists()) TeardownHelper.SafeDismissVehicle(cruiser, delete: true);
                    if (driver != null && driver.Exists()) TeardownHelper.SafeDismissPed(driver, delete: true);
                    if (passenger != null && passenger.Exists()) TeardownHelper.SafeDismissPed(passenger, delete: true);
                    if (additionalOfficers != null)
                    {
                        foreach (var ofc in additionalOfficers)
                        {
                            if (ofc != null && ofc.Exists()) TeardownHelper.SafeDismissPed(ofc, delete: true);
                        }
                    }
                }
                return null;
            }
        }

        // ====================================================================
        // Audio & Radio Animation Utilities
        // ====================================================================

        private static void PlayPanicButtonSound()
        {
            StartTrackedFiber(() =>
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
            private readonly Dictionary<PoolHandle, Blip> _officerFootBlips = new Dictionary<PoolHandle, Blip>();
            public IEnumerable<Blip> FootBlips => _officerFootBlips.Values;
            public string Callsign { get; }
            public BackupType ServiceType { get; private set; }
            public bool IsCode3 { get; private set; }
            public bool IsCommandeer { get; }
            public bool IsCalloutResponse { get; set; }

            private readonly object _stateLock = new object();
            private UnitState _state;
            public UnitState State
            {
                get { lock (_stateLock) return _state; }
                private set { lock (_stateLock) _state = value; }
            }

            public uint StartTime { get; }
            public uint StateStartTime { get; private set; }
            public Ped ThreatPed { get; private set; }

            private uint _lastNavigationRefresh;
            private uint _lastThreatScan;
            private uint _lastCoverCheck;
            private uint _lastSlowdownCheck;
            private uint _lastStuckCheckTime;
            private uint _stuckStartTime;
            private uint _lastBuddyFollowUpdate;
            private uint _lastPerimeterUpdate;
            private bool _isArrivedSirenMuted;
            private bool _departureInitiated;
            private bool _isPursuitOrangeBlip;
            private bool _isParkedBlip;
            private readonly HashSet<Ped> _enrolledPursuitOfficers = new HashSet<Ped>();
            private LHandle _enrolledPursuitHandle;

            public BackupUnitSession(Vehicle cruiser, Ped driver, Ped passenger, Vector3 destination, Blip blip, string callsign, BackupType serviceType, bool isCode3, bool isCommandeer, IEnumerable<Ped> additionalOfficers = null, bool isAlreadyOnScene = false, bool isCallout = false)
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
                IsCalloutResponse = isCallout;

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

            public bool CanReengage()
            {
                return State == UnitState.Dismissing && IsValid();
            }

            public void Reengage(BackupType newType, Vector3 newTarget, bool isCode3, bool isCallout = false)
            {
                Logger.Info($"[BackupFeature] Re-engaging dismissing unit {Callsign} for {newType} (Code 3: {isCode3}, Callout: {isCallout}).");

                ServiceType = newType;
                TargetPosition = newTarget;
                IsCode3 = isCode3;
                IsCalloutResponse = isCallout;
                StateStartTime = Game.GameTime;
                _departureInitiated = false;
                _isArrivedSirenMuted = false;

                // Clear any previous dismissal / boarding tasks on officers so they don't fight new orders
                if (Officers != null)
                {
                    foreach (var ofc in Officers)
                    {
                        if (ofc != null && ofc.Exists() && ofc.IsAlive)
                        {
                            try
                            {
                                if (Functions.IsPedInPursuit(ofc))
                                {
                                    Functions.RemovePedFromPursuit(ofc);
                                }
                                ofc.Tasks.Clear();
                            }
                            catch { }
                        }
                    }
                }

                if (Cruiser != null && Cruiser.Exists())
                {
                    try
                    {
                        if (isCode3)
                        {
                            Cruiser.IsSirenOn = true;
                            Cruiser.IsSirenSilent = false;
                        }
                        else
                        {
                            Cruiser.IsSirenOn = false;
                            Cruiser.IsSirenSilent = false;
                            NativeTasks.SetSilentEmergencyLights(Cruiser, false);
                        }
                    }
                    catch { }
                }

                Ped player = Game.LocalPlayer?.Character;
                Vector3 playerPos = (player != null && player.Exists()) ? player.Position : TargetPosition;
                float distToTarget = (Cruiser != null && Cruiser.Exists()) ? Cruiser.Position.DistanceTo(TargetPosition) : float.MaxValue;
                float distToPlayer = (Cruiser != null && Cruiser.Exists()) ? Cruiser.Position.DistanceTo(playerPos) : float.MaxValue;

                // Check if unit is already on scene
                bool alreadyOnScene = false;
                if (isCallout)
                {
                    alreadyOnScene = distToTarget <= 25.0f;
                }
                else
                {
                    alreadyOnScene = distToPlayer <= 35.0f || (Officers != null && Officers.Any(o => o != null && o.Exists() && o.IsAlive && o.DistanceTo(playerPos) <= 25.0f));
                }

                if (alreadyOnScene)
                {
                    State = UnitState.ArrivedOnScene;

                    // If emergency, mute audible siren but keep lightbars
                    if (isCode3 && Cruiser != null && Cruiser.Exists())
                    {
                        try
                        {
                            Cruiser.IsSirenOn = true;
                            Cruiser.IsSirenSilent = true;
                            NativeTasks.SetSilentEmergencyLights(Cruiser, true);
                            _isArrivedSirenMuted = true;
                        }
                        catch { }
                    }

                    // Arm officers if Panic Button
                    if (newType == BackupType.PanicButton && Officers != null)
                    {
                        foreach (var ofc in Officers)
                        {
                            if (ofc != null && ofc.Exists() && ofc.IsAlive)
                            {
                                try
                                {
                                    NativeFunction.Natives.GIVE_WEAPON_TO_PED(ofc, WeaponCombatPistol, 120, false, true);
                                    NativeFunction.Natives.SET_CURRENT_PED_WEAPON(ofc, WeaponCombatPistol, true);
                                }
                                catch { }
                            }
                        }
                    }

                    // If officers are mounted, staggered egress (ONLY if NO active vehicular pursuit is running!)
                    LHandle checkPursuit = null;
                    try { checkPursuit = Functions.GetActivePursuit(); } catch { }
                    bool isPursuitActive = checkPursuit != null && Functions.IsPursuitStillRunning(checkPursuit);

                    if (!isPursuitActive)
                    {
                        bool anyMounted = Officers != null && Cruiser != null && Cruiser.Exists() && Officers.Any(o => o != null && o.Exists() && o.IsAlive && o.IsInVehicle(Cruiser, false));
                        if (anyMounted)
                        {
                            StartTrackedFiber(() =>
                            {
                                try
                                {
                                    for (int i = 0; i < Officers.Count; i++)
                                    {
                                        Ped ofc = Officers[i];
                                        if (ofc != null && ofc.Exists() && ofc.IsAlive && Cruiser != null && Cruiser.Exists() && ofc.IsInVehicle(Cruiser, false))
                                        {
                                            ofc.Tasks.LeaveVehicle(LeaveVehicleFlags.None);
                                        }
                                        GameFiber.Sleep(300);
                                    }
                                }
                                catch { }
                            }, "Cadence - Reengage Egress Fiber");
                        }
                    }
                }
                else
                {
                    State = UnitState.EnRoute;

                    bool allMounted = Officers != null && Cruiser != null && Cruiser.Exists() && Officers.Where(o => o != null && o.Exists() && o.IsAlive).All(o => o.IsInVehicle(Cruiser, false));
                    if (!allMounted)
                    {
                        OrderOfficersEnterVehicle();
                    }

                    if (Driver != null && Driver.Exists() && Driver.IsAlive && Cruiser != null && Cruiser.Exists())
                    {
                        float speed = isCode3 ? 35.0f : 22.0f;
                        NativeTasks.TaskLongRangeEmergencyDrive(Driver, Cruiser, TargetPosition, speed, 12.0f);
                    }
                }

                UpdateBlipStates();
            }

            public void HoldOnSceneAfterCallout()
            {
                if (State == UnitState.Dismissing || State == UnitState.Completed) return;

                State = UnitState.ArrivedOnScene;
                StateStartTime = Game.GameTime;

                // Shut off siren and lights completely upon callout completion
                if (Cruiser != null && Cruiser.Exists())
                {
                    NativeTasks.ShutOffSirenAndLights(Cruiser);
                }

                if (Officers != null)
                {
                    foreach (var ofc in Officers)
                    {
                        if (ofc != null && ofc.Exists() && ofc.IsAlive)
                        {
                            ofc.BlockPermanentEvents = true; // Hold until player explicitly dismisses!
                            if (!ofc.IsInCombat && ofc.Speed < 0.1f)
                            {
                                try
                                {
                                    if (!NativeFunction.Natives.IS_PED_USING_ANY_SCENARIO<bool>(ofc))
                                    {
                                        NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(ofc, "WORLD_HUMAN_COP_IDLES", 0, true);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }

                UpdateBlipStates();
            }

            public void HoldOnSceneAfterPursuit()
            {
                if (State == UnitState.Dismissing || State == UnitState.Completed) return;

                State = UnitState.ArrivedOnScene;
                StateStartTime = Game.GameTime;
                _enrolledPursuitOfficers.Clear();
                _enrolledPursuitHandle = null;

                try
                {
                    // 1. Silent emergency lights
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        Cruiser.IsSirenOn = true;
                        Cruiser.IsSirenSilent = true;
                        NativeTasks.SetSilentEmergencyLights(Cruiser, true);
                        NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(Cruiser, true);
                    }

                    // 2. Staggered dismount and defensive scene guard
                    StartTrackedFiber(() =>
                    {
                        try
                        {
                            if (Officers == null) return;
                            for (int i = 0; i < Officers.Count; i++)
                            {
                                Ped ofc = Officers[i];
                                if (ofc != null && ofc.Exists() && ofc.IsAlive)
                                {
                                    ofc.BlockPermanentEvents = true; // Hold until player explicitly dismisses!
                                    if (Cruiser != null && Cruiser.Exists() && ofc.IsInVehicle(Cruiser, false))
                                    {
                                        ofc.Tasks.LeaveVehicle(LeaveVehicleFlags.None);
                                    }
                                    else if (!ofc.IsInCombat && !ofc.IsCuffed)
                                    {
                                        try
                                        {
                                            if (!NativeFunction.Natives.IS_PED_USING_ANY_SCENARIO<bool>(ofc))
                                            {
                                                NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(ofc, "WORLD_HUMAN_COP_IDLES", 0, true);
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                GameFiber.Sleep(300);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"[BackupFeature] HoldOnSceneAfterPursuit egress error: {ex.Message}");
                        }
                    }, "Cadence - Backup Hold Scene Pursuit Fiber");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[BackupFeature] Error in HoldOnSceneAfterPursuit for {Callsign}: {ex.Message}");
                }

                UpdateBlipStates();
            }

            public void Dismiss()
            {
                if (State == UnitState.Dismissing || State == UnitState.Completed) return;

                State = UnitState.Dismissing;
                StateStartTime = Game.GameTime;
                _departureInitiated = false;

                // 1. Disenroll officers from any active LSPDFR pursuit before clearing tasks to prevent LSPDFR NullReferenceExceptions
                LHandle activePursuit = null;
                try { activePursuit = Functions.GetActivePursuit(); } catch { }
                if (activePursuit != null && Officers != null)
                {
                    foreach (var ofc in Officers)
                    {
                        if (ofc != null && ofc.Exists())
                        {
                            try
                            {
                                if (Functions.IsPedInPursuit(ofc))
                                {
                                    Functions.RemovePedFromPursuit(ofc);
                                }
                            }
                            catch { }
                        }
                    }
                }
                _enrolledPursuitOfficers.Clear();

                // 2. Cleanly recreate radar blip to eradicate any sticky native flash timers or intervals
                bool allLivingOfficersOutside = Officers != null && Cruiser != null && Cruiser.Exists() &&
                                                Officers.Where(o => o != null && o.Exists() && o.IsAlive).All(o => !o.IsInVehicle(Cruiser, false));
                BlipService.SafeDeleteBlip(ref RadarBlip);
                if (Cruiser != null && Cruiser.Exists())
                {
                    if (allLivingOfficersOutside)
                    {
                        _isParkedBlip = true;
                        RadarBlip = BlipService.AttachParkedCruiserBlip(Cruiser, Callsign);
                    }
                    else
                    {
                        _isParkedBlip = false;
                        RadarBlip = BlipService.AttachPatrolBlip(Cruiser, Callsign);
                    }
                }

                // Mute audible sirens, but keep emergency lights active while officers remount vehicle
                if (Cruiser != null && Cruiser.Exists())
                {
                    try
                    {
                        Cruiser.IsSirenOn = true;
                        Cruiser.IsSirenSilent = true;
                        NativeTasks.SetSilentEmergencyLights(Cruiser, true);
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
                    StartTrackedFiber(() =>
                    {
                        try
                        {
                            if (Functions.IsPedInPursuit(ofc))
                            {
                                Functions.RemovePedFromPursuit(ofc);
                            }
                            ofc.Tasks.Clear();
                            NativeFunction.Natives.SET_CURRENT_PED_WEAPON(ofc, WeaponUnarmed, true);

                            float enterSpeed = (Cruiser != null && Cruiser.Exists() && ofc.DistanceTo(Cruiser) > 20.0f) ? 2.0f : 1.0f;
                            for (int attempt = 0; attempt < 5; attempt++)
                            {
                                if (State != UnitState.Dismissing && State != UnitState.EnRoute) break;
                                if (!ofc.Exists() || !ofc.IsAlive || (Cruiser != null && ofc.IsInVehicle(Cruiser, false))) break;
                                if (Cruiser != null && Cruiser.Exists())
                                {
                                    ofc.Tasks.EnterVehicle(Cruiser, 30000, seatIdx, enterSpeed, EnterVehicleFlags.None).WaitForCompletion();
                                }
                                GameFiber.Sleep(500);
                            }

                            // Warp fallback after 5 failed attempts
                            if ((State == UnitState.Dismissing || State == UnitState.EnRoute) && ofc.Exists() && ofc.IsAlive && Cruiser != null && Cruiser.Exists() && !ofc.IsInVehicle(Cruiser, false))
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

                // Enforce global siren policy (sirens require active emergency lights)
                NativeTasks.EnforceSirenLightbarPolicy(Cruiser);

                // Dynamically synchronize radar blips (empty white car vs badge foot blips)
                UpdateBlipStates();

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

                // Check active pursuit status
                LHandle activePursuit = null;
                try { activePursuit = Functions.GetActivePursuit(); } catch { }
                bool isPursuitRunning = activePursuit != null && Functions.IsPursuitStillRunning(activePursuit);

                // Dynamic pursuit blip: transition cleanly to native pursuit blip once during pursuit
                if (isPursuitRunning)
                {
                    if (!_isPursuitOrangeBlip)
                    {
                        _isPursuitOrangeBlip = true;
                        BlipService.TransitionToPursuit(RadarBlip, Callsign);
                    }
                }
                else if (_isPursuitOrangeBlip)
                {
                    _isPursuitOrangeBlip = false;
                    BlipService.TransitionToEmergency(RadarBlip, Callsign);
                }

                // Periodic course recalculation:
                // If in pursuit, target the front of the pursuit / fleeing suspect; otherwise roadside curb
                uint refreshInterval = isPursuitRunning ? 6000u : 3000u;
                if (now - _lastNavigationRefresh > refreshInterval)
                {
                    _lastNavigationRefresh = now;
                    Vector3 desiredTarget;
                    if (isPursuitRunning)
                    {
                        desiredTarget = PursuitFeature.GetPursuitTargetPosition(activePursuit, player);
                    }
                    else if (IsCalloutResponse)
                    {
                        if (TryGetCalloutOrWaypointPosition(out Vector3 dynamicWaypoint, out _))
                        {
                            desiredTarget = dynamicWaypoint;
                        }
                        else
                        {
                            desiredTarget = TargetPosition;
                        }
                    }
                    else
                    {
                        desiredTarget = GetRoadsideCurbPosition(playerPos);
                    }

                    float reissuanceThreshold = isPursuitRunning ? 50.0f : 30.0f;
                    if (TargetPosition.DistanceTo(desiredTarget) > reissuanceThreshold)
                    {
                        TargetPosition = desiredTarget;
                        float speed = IsCode3 ? 35.0f : 22.0f;
                        NativeTasks.TaskLongRangeEmergencyDrive(Driver, Cruiser, TargetPosition, speed, 12.0f);
                    }
                }

                // Approach slowdown: within 55m and travelling fast (> 14 m/s), decelerate smoothly to ~11 m/s (non-pursuit only)
                float approachDist = IsCalloutResponse ? distToTarget : distToPlayer;
                if (!isPursuitRunning && approachDist <= 55.0f && Cruiser.Speed > 14.0f && (now - _lastSlowdownCheck > 2000))
                {
                    _lastSlowdownCheck = now;
                    NativeTasks.TaskLongRangeEmergencyDrive(Driver, Cruiser, TargetPosition, 11.0f, 12.0f);
                }

                // Anti-stuck watchdog: if moving very slowly (< 0.8 m/s) while far from target (> 25m)
                if (Cruiser.Speed < 0.8f && approachDist > 25.0f && Driver != null && Driver.Exists() && Driver.IsInVehicle(Cruiser, false))
                {
                    if (_stuckStartTime == 0)
                    {
                        _stuckStartTime = now;
                    }
                    else if (now - _stuckStartTime > 3500 && (now - _lastStuckCheckTime > 4000))
                    {
                        _lastStuckCheckTime = now;
                        _stuckStartTime = now;
                        float speed = IsCode3 ? 32.0f : 20.0f;
                        NativeTasks.TaskLongRangeEmergencyDrive(Driver, Cruiser, TargetPosition, speed, 12.0f);
                        Logger.Debug($"[BackupFeature] Unit {Callsign} re-asserted emergency driving to avoid obstruction (speed={Cruiser.Speed:F1}).");
                    }
                }
                else
                {
                    _stuckStartTime = 0;
                }

                uint elapsedEnRoute = now - StateStartTime;

                // Pursuit catch-up assist: if pursuit is running fast and responder is far behind (> 280m) after 35s
                if (isPursuitRunning && elapsedEnRoute > 35000 && distToPlayer > 280.0f)
                {
                    Vector3 catchUpProbe = player.GetOffsetPosition(new Vector3(0f, -90.0f, 0f));
                    if (WorldUtils.GetSafeStreetPosition(catchUpProbe, 1, out Vector3 warpPos, out float warpH))
                    {
                        if (warpPos != Vector3.Zero && !WorldUtils.IsSubterraneanOrVoid(warpPos) && warpPos.DistanceTo(playerPos) >= 60.0f)
                        {
                            Cruiser.Position = warpPos;
                            Cruiser.Heading = warpH;
                            WorldUtils.SecureEntityGround(Cruiser);
                            NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(Cruiser, Math.Max(player.Speed, 20.0f));
                            NativeTasks.TaskLongRangeEmergencyDrive(Driver, Cruiser, playerPos, 35.0f, 12.0f);
                            Logger.Info($"[BackupFeature] Catch-up repositioned unit {Callsign} into active pursuit range ({Cruiser.Position.DistanceTo(playerPos):F1}m).");
                        }
                    }
                }

                // Despawn timeout for hopelessly lost/stuck units
                if (IsCalloutResponse)
                {
                    if (elapsedEnRoute > 180000 && distToTarget > 600.0f)
                    {
                        Logger.Info($"[BackupFeature] Callout unit {Callsign} timed out en route ({distToTarget:F1}m away after {elapsedEnRoute / 1000}s). Dismissing.");
                        Dismiss();
                        State = UnitState.Completed;
                        return;
                    }
                }
                else
                {
                    if (!isPursuitRunning && elapsedEnRoute > 75000 && distToPlayer > 480.0f)
                    {
                        Logger.Info($"[BackupFeature] Unit {Callsign} timed out en route ({distToPlayer:F1}m away after {elapsedEnRoute / 1000}s). Dismissing.");
                        Dismiss();
                        State = UnitState.Completed;
                        return;
                    }
                }

                // Scene arrival check:
                bool isArrived = false;

                if (isPursuitRunning)
                {
                    // In a pursuit, catching within 100m of the player or within 110m of the target suspect is arrival
                    if (distToPlayer <= 100.0f || distToTarget <= 110.0f)
                    {
                        isArrived = true;
                    }
                }
                else if (IsCalloutResponse)
                {
                    // Callout arrival: within 22m of target curb position or stopped near target after grace period
                    if (distToTarget <= 22.0f)
                    {
                        isArrived = true;
                    }
                    else if (distToTarget <= 35.0f && Cruiser.Speed < 2.0f && elapsedEnRoute > 4000)
                    {
                        isArrived = true;
                    }
                }
                else
                {
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

                // Check if an active pursuit is running
                LHandle activePursuit = null;
                try { activePursuit = Functions.GetActivePursuit(); } catch { }
                bool isPursuitRunning = activePursuit != null && Functions.IsPursuitStillRunning(activePursuit);

                if (isPursuitRunning)
                {
                    // Active pursuit is in progress! Officers MUST remain in their vehicle and engage in pursuit.
                    // Keep sirens and emergency lightbars fully active.
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        Cruiser.IsSirenOn = true;
                        Cruiser.IsSirenSilent = false;
                    }

                    // Add officers to pursuit up to MaxPursuitUnits cap (stable pursuit handle tracking)
                    if (_enrolledPursuitHandle == null || !Functions.IsPursuitStillRunning(_enrolledPursuitHandle))
                    {
                        _enrolledPursuitOfficers.Clear();
                        _enrolledPursuitHandle = activePursuit;
                    }

                    int currentPursuitCops = 0;
                    try { currentPursuitCops = Functions.GetPursuitCops(activePursuit)?.Count(p => p != null && p.Exists() && p.IsAlive) ?? 0; } catch { }

                    // Only add officers if suspect is actively fleeing (not surrendering / being arrested)
                    if (!PursuitFeature.IsAnySuspectSurrenderingOrArrested(activePursuit))
                    {
                        foreach (var ofc in Officers)
                        {
                            if (ofc != null && ofc.Exists() && ofc.IsAlive && !Functions.IsPedInPursuit(ofc))
                            {
                                if (_enrolledPursuitOfficers.Contains(ofc))
                                {
                                    continue;
                                }

                                if (currentPursuitCops < Settings.MaxPursuitUnits)
                                {
                                    // DO NOT call ofc.Tasks.Clear()! It destroys TaskCopChasePedAdv and causes in/out car thrashing.
                                    ofc.BlockPermanentEvents = false;
                                    _enrolledPursuitOfficers.Add(ofc);
                                    Functions.SetPedAsCop(ofc);
                                    Functions.AddCopToPursuit(activePursuit, ofc);
                                    currentPursuitCops++;
                                    Logger.Info($"[BackupFeature] Unit {Callsign} officer added to active pursuit on arrival ({currentPursuitCops}/{Settings.MaxPursuitUnits}).");
                                }
                                else
                                {
                                    Ped suspect = PursuitFeature.GetValidPursuitSuspects(activePursuit)?.FirstOrDefault(p => p != null && p.Exists() && p.IsAlive);
                                    if (suspect != null && ofc == Driver && Cruiser != null && Cruiser.Exists())
                                    {
                                        NativeTasks.TaskVehicleChase(ofc, suspect);
                                    }
                                }
                            }
                        }
                    }

                    // Do NOT halt the cruiser, order dismount, or create foot blips during an active vehicle pursuit!
                    return;
                }

                // Standard non-pursuit scene arrival:
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
                StartTrackedFiber(() =>
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

                // Step 4: Synchronize blips (officers getting out receive badge blip, car turns white when empty)
                UpdateBlipStates();
            }

            private void UpdateOnScene(Ped player, Vector3 playerPos, uint now)
            {
                // Scan for active threats or pursuits every 1000ms
                if (now - _lastThreatScan >= 1000)
                {
                    _lastThreatScan = now;

                    // 1. Check active LSPDFR pursuit
                    LHandle activePursuit = null;
                    try { activePursuit = Functions.GetActivePursuit(); } catch { }
                    bool isPursuitRunning = activePursuit != null && Functions.IsPursuitStillRunning(activePursuit);

                    if (isPursuitRunning)
                    {
                        // Stable pursuit tracking: do NOT clear on new LHandle instance if pursuit is still running!
                        if (_enrolledPursuitHandle == null || !Functions.IsPursuitStillRunning(_enrolledPursuitHandle))
                        {
                            _enrolledPursuitOfficers.Clear();
                            _enrolledPursuitHandle = activePursuit;
                        }

                        int currentPursuitCops = 0;
                        try { currentPursuitCops = Functions.GetPursuitCops(activePursuit)?.Count(p => p != null && p.Exists() && p.IsAlive) ?? 0; } catch { }

                        bool isSurrenderingOrArrested = PursuitFeature.IsAnySuspectSurrenderingOrArrested(activePursuit);

                        // Only add officers if suspect is actively fleeing (not surrendering / being arrested)
                        if (!isSurrenderingOrArrested)
                        {
                            foreach (var ofc in Officers)
                            {
                                if (ofc != null && ofc.Exists() && ofc.IsAlive && !Functions.IsPedInPursuit(ofc))
                                {
                                    if (_enrolledPursuitOfficers.Contains(ofc))
                                    {
                                        continue;
                                    }

                                    if (currentPursuitCops < Settings.MaxPursuitUnits)
                                    {
                                        // DO NOT call ofc.Tasks.Clear()! It destroys TaskCopChasePedAdv and causes in/out car thrashing.
                                        ofc.BlockPermanentEvents = false;
                                        _enrolledPursuitOfficers.Add(ofc);
                                        Functions.SetPedAsCop(ofc);
                                        Functions.AddCopToPursuit(activePursuit, ofc);
                                        currentPursuitCops++;
                                        Logger.Info($"[BackupFeature] Unit {Callsign} officer added to active pursuit ({currentPursuitCops}/{Settings.MaxPursuitUnits}).");
                                    }
                                    else
                                    {
                                        Ped suspect = PursuitFeature.GetValidPursuitSuspects(activePursuit)?.FirstOrDefault(p => p != null && p.Exists() && p.IsAlive);
                                        if (suspect != null && ofc == Driver && Cruiser != null && Cruiser.Exists())
                                        {
                                            NativeTasks.TaskVehicleChase(ofc, suspect);
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Suspect is surrendering or being arrested:
                            // Mute audible sirens immediately!
                            if (Cruiser != null && Cruiser.Exists())
                            {
                                Cruiser.IsSirenOn = true;
                                Cruiser.IsSirenSilent = true;
                                NativeTasks.SetSilentEmergencyLights(Cruiser, true);
                            }

                            // Command all officers seated in the cruiser to dismount and take defensive positions!
                            foreach (var ofc in Officers)
                            {
                                if (ofc != null && ofc.Exists() && ofc.IsAlive && !Functions.IsPedInPursuit(ofc))
                                {
                                    if (ofc.IsInVehicle(Cruiser, false))
                                    {
                                        ofc.Tasks.LeaveVehicle(LeaveVehicleFlags.None);
                                    }
                                    else if (!ofc.IsInCombat)
                                    {
                                        ofc.BlockPermanentEvents = true;
                                    }

                                    try
                                    {
                                        NativeFunction.Natives.GIVE_WEAPON_TO_PED(ofc, WeaponCombatPistol, 120, false, true);
                                        NativeFunction.Natives.SET_CURRENT_PED_WEAPON(ofc, WeaponCombatPistol, true);
                                    }
                                    catch { }
                                }
                            }

                            UpdateBlipStates();
                        }

                        // Maintain native pursuit blip during pursuit
                        if (!_isPursuitOrangeBlip)
                        {
                            _isPursuitOrangeBlip = true;
                            BlipService.TransitionToPursuit(RadarBlip, Callsign);
                        }

                        // CRITICAL: Do NOT execute UpdateBuddyFollowing during an active pursuit!
                        // LSPDFR controls pursuit pathing.
                        return;
                    }

                    // If blip was configured for pursuit that just ended, restore to Code 3 / routine
                    if (_isPursuitOrangeBlip)
                    {
                        _isPursuitOrangeBlip = false;
                        BlipService.TransitionToEmergency(RadarBlip, Callsign);
                    }

                    // 2. Scan for hostile peds targeting player or officers within 40m
                    Vector3 threatScanCenter = (IsCalloutResponse && Cruiser != null && Cruiser.Exists()) ? Cruiser.Position : playerPos;
                    Ped threat = FindHostileThreatNear(threatScanCenter, 40.0f);
                    if (threat != null && threat.Exists() && threat.IsAlive)
                    {
                        EngageThreat(threat);
                        return;
                    }
                }

                // If any officer is currently in pursuit, DO NOT buddy-follow!
                bool anyInPursuit = false;
                try
                {
                    LHandle activePursuit = Functions.GetActivePursuit();
                    anyInPursuit = (activePursuit != null && Functions.IsPursuitStillRunning(activePursuit)) ||
                                   (Officers != null && Officers.Any(o => o != null && o.Exists() && Functions.IsPedInPursuit(o)));
                }
                catch { }

                if (anyInPursuit)
                {
                    return;
                }

                // If this is a callout response unit and the player hasn't arrived at the scene yet (>35m from cruiser),
                // maintain a defensive tactical perimeter around the cruiser rather than running across the map toward player.
                if (IsCalloutResponse && Cruiser != null && Cruiser.Exists() && Cruiser.Position.DistanceTo(playerPos) > 35.0f)
                {
                    UpdateCalloutPerimeter(now);
                    return;
                }

                // If no threat present and not in pursuit: Follow / Buddy Behavior
                if (ServiceType != BackupType.AmbulanceCode2 && ServiceType != BackupType.AmbulanceCode3 &&
                    ServiceType != BackupType.FireDeptCode2 && ServiceType != BackupType.FireDeptCode3)
                {
                    UpdateBuddyFollowing(player, playerPos);
                }
            }

            private void UpdateCalloutPerimeter(uint now)
            {
                if (now - _lastPerimeterUpdate < 2500)
                {
                    return;
                }
                _lastPerimeterUpdate = now;

                if (Cruiser == null || !Cruiser.Exists() || Officers == null)
                {
                    return;
                }

                for (int i = 0; i < Officers.Count; i++)
                {
                    Ped ofc = Officers[i];
                    if (ofc == null || !ofc.Exists() || !ofc.IsAlive || ofc.IsInVehicle(Cruiser, false) || ofc.IsInCombat)
                    {
                        continue;
                    }

                    float distToCruiser = ofc.DistanceTo(Cruiser.Position);

                    // If officer has drifted too far (>12m) from cruiser, walk back to defensive perimeter
                    if (distToCruiser > 12.0f)
                    {
                        Vector3 offset = (i % 2 == 0) ? new Vector3(-2.5f, -1.0f, 0f) : new Vector3(2.5f, -1.0f, 0f);
                        Vector3 targetPoint = Cruiser.GetOffsetPosition(offset);
                        try
                        {
                            NativeFunction.Natives.TASK_FOLLOW_NAV_MESH_TO_COORD(ofc, targetPoint.X, targetPoint.Y, targetPoint.Z, 1.2f, -1, 0.5f, 0, 0f);
                        }
                        catch
                        {
                            ofc.Tasks.FollowNavigationMeshToPosition(targetPoint, Cruiser.Heading, 1.2f);
                        }
                    }
                    else if (ofc.Speed < 0.1f)
                    {
                        // Officer is near cruiser and standing idle: hold position / cop idle scenario facing outward
                        try
                        {
                            if (!NativeFunction.Natives.IS_PED_USING_ANY_SCENARIO<bool>(ofc))
                            {
                                NativeFunction.Natives.TASK_START_SCENARIO_IN_PLACE(ofc, "WORLD_HUMAN_COP_IDLES", 0, true);
                            }
                        }
                        catch { }
                    }
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
                        try
                        {
                            isCuffed = p.IsCuffed ||
                                       NativeFunction.Natives.IS_PED_CUFFED<bool>(p) ||
                                       Functions.IsPedArrested(p) ||
                                       Functions.IsPedGettingArrested(p) ||
                                       Functions.IsPedBeingCuffed(p) ||
                                       Functions.IsPedKneelingTaskActive(p) ||
                                       Functions.IsPedCuffedTaskActive(p) ||
                                       (Functions.GetPedArrestingOfficer(p) != null && Functions.GetPedArrestingOfficer(p).Exists());
                        }
                        catch { }
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
                            if (Functions.IsPedInPursuit(ofc))
                            {
                                Functions.RemovePedFromPursuit(ofc);
                            }
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
                        threatNeutralized = ThreatPed.IsCuffed ||
                                            NativeFunction.Natives.IS_PED_CUFFED<bool>(ThreatPed) ||
                                            Functions.IsPedArrested(ThreatPed) ||
                                            Functions.IsPedGettingArrested(ThreatPed) ||
                                            Functions.IsPedBeingCuffed(ThreatPed) ||
                                            Functions.IsPedKneelingTaskActive(ThreatPed) ||
                                            Functions.IsPedCuffedTaskActive(ThreatPed) ||
                                            (Functions.GetPedArrestingOfficer(ThreatPed) != null && Functions.GetPedArrestingOfficer(ThreatPed).Exists());
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
                                if (Functions.IsPedInPursuit(ofc))
                                {
                                    Functions.RemovePedFromPursuit(ofc);
                                }
                                ofc.Tasks.Clear();
                                NativeFunction.Natives.SET_CURRENT_PED_WEAPON(ofc, WeaponUnarmed, true);
                            }
                            catch { }
                        }
                    }

                    BlipService.SafeDeleteBlip(ref RadarBlip);
                    if (Cruiser != null && Cruiser.Exists())
                    {
                        RadarBlip = BlipService.AttachPatrolBlip(Cruiser, Callsign);
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

                    StartTrackedFiber(() =>
                    {
                        try
                        {
                            if (State != UnitState.Dismissing) return;

                            Ped drv = Cruiser.Driver;
                            if (drv == null || !drv.Exists() || !drv.IsAlive) return;

                            NativeTasks.ShutOffSirenAndLights(Cruiser);

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
                                if (State != UnitState.Dismissing) break;
                                if (!Cruiser.Exists() || !drv.Exists() || !drv.IsAlive) break;
                                if (Cruiser.DistanceTo(startPos) >= 25.0f) break;
                                GameFiber.Sleep(500);
                            }

                            if (State != UnitState.Dismissing) return;

                            // Step 2b: Hand off to ambient traffic cruise / wander ONCE
                            if (drv.Exists() && drv.IsAlive && Cruiser.Exists())
                            {
                                NativeFunction.Natives.TASK_VEHICLE_DRIVE_WANDER(drv, Cruiser, 10.0f, (int)NativeTasks.NormalDrivingFlag);
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
                UpdateBlipStates();
            }

            private void UpdateBlipStates()
            {
                if (!Settings.ShowRadarBlips)
                {
                    if (RadarBlip != null && RadarBlip.Exists())
                    {
                        Blip temp = RadarBlip;
                        BlipService.SafeDeleteBlip(ref temp);
                        RadarBlip = null;
                    }
                    ClearFootBlips();
                    return;
                }

                if (Cruiser == null || !Cruiser.Exists() || Officers == null)
                {
                    return;
                }

                var livingOfficers = Officers.Where(o => o != null && o.Exists() && o.IsAlive).ToList();
                if (livingOfficers.Count == 0)
                {
                    return;
                }

                bool allLivingOfficersOutside = livingOfficers.All(o => !o.IsInVehicle(Cruiser, false));

                // 1. Foot blip management for each officer:
                // When an officer is inside the vehicle or en route, delete their foot badge blip immediately.
                // When an officer is on foot outside the vehicle, ensure they have exactly ONE badge blip.
                lock (_officerFootBlips)
                {
                    foreach (var ofc in livingOfficers)
                    {
                        bool insideCruiser = ofc.IsInVehicle(Cruiser, false);
                        if (insideCruiser || State == UnitState.EnRoute)
                        {
                            // Officer inside cruiser or unit still en route: strip foot blip immediately
                            if (_officerFootBlips.TryGetValue(ofc.Handle, out Blip fb))
                            {
                                BlipService.SafeDeleteBlip(ref fb);
                                _officerFootBlips.Remove(ofc.Handle);
                            }
                            BlipService.SafeDeleteEntityBlip(ofc);
                        }
                        else
                        {
                            // Officer is outside cruiser on foot during on-scene, engaging threat, or dismissing:
                            // Ensure single Badge blip (Sprite 58, Color 38) is tracked in _officerFootBlips
                            if (!_officerFootBlips.TryGetValue(ofc.Handle, out Blip fb) || fb == null || !fb.Exists())
                            {
                                fb = BlipService.AttachOfficerFootBlip(ofc, Callsign);
                                if (fb != null && fb.Exists())
                                {
                                    _officerFootBlips[ofc.Handle] = fb;
                                }
                            }
                        }
                    }

                    // Clean up dead officer blips
                    foreach (var ofc in Officers)
                    {
                        if (ofc != null && ofc.Exists() && !ofc.IsAlive)
                        {
                            if (_officerFootBlips.TryGetValue(ofc.Handle, out Blip fb))
                            {
                                BlipService.SafeDeleteBlip(ref fb);
                                _officerFootBlips.Remove(ofc.Handle);
                            }
                            BlipService.SafeDeleteEntityBlip(ofc);
                        }
                    }

                    // Clean up any stale/invalid blips from dictionary
                    var staleHandles = _officerFootBlips.Where(kvp => kvp.Value == null || !kvp.Value.Exists()).Select(kvp => kvp.Key).ToList();
                    foreach (var h in staleHandles)
                    {
                        _officerFootBlips.Remove(h);
                    }
                }

                // 2. Cruiser Radar Blip state synchronization:
                // When empty / all living officers are outside, cruiser blip turns White (Color 0, [PARKED]).
                // When any officer mounts, cruiser blip restores to its operational state (Patrol, Emergency, or Pursuit).
                // If the blip was dropped or invalidated by external engine operations, restore it proactively.
                if (allLivingOfficersOutside && State != UnitState.EnRoute)
                {
                    if (!_isParkedBlip || RadarBlip == null || !RadarBlip.Exists())
                    {
                        _isParkedBlip = true;
                        BlipService.SafeDeleteBlip(ref RadarBlip);
                        RadarBlip = BlipService.AttachParkedCruiserBlip(Cruiser, Callsign);
                    }
                }
                else
                {
                    if (_isParkedBlip || RadarBlip == null || !RadarBlip.Exists())
                    {
                        _isParkedBlip = false;
                        BlipService.SafeDeleteBlip(ref RadarBlip);
                        if (State == UnitState.Dismissing)
                        {
                            RadarBlip = BlipService.AttachPatrolBlip(Cruiser, Callsign);
                        }
                        else if (State == UnitState.EngagingThreat)
                        {
                            RadarBlip = BlipService.AttachCombatBlip(Cruiser, Callsign);
                        }
                        else if (_isPursuitOrangeBlip)
                        {
                            RadarBlip = BlipService.AttachPursuitBlip(Cruiser, Callsign);
                        }
                        else if (IsCalloutResponse && State == UnitState.EnRoute)
                        {
                            RadarBlip = BlipService.AttachCalloutResponseBlip(Cruiser, Callsign);
                        }
                        else if (IsCode3 && State == UnitState.EnRoute)
                        {
                            RadarBlip = BlipService.AttachEmergencyBlip(Cruiser, Callsign);
                        }
                        else
                        {
                            RadarBlip = BlipService.AttachPatrolBlip(Cruiser, Callsign);
                        }
                    }
                }
            }

            private void ClearFootBlips()
            {
                foreach (var kvp in _officerFootBlips)
                {
                    Blip temp = kvp.Value;
                    BlipService.SafeDeleteBlip(ref temp);
                }
                _officerFootBlips.Clear();

                if (Officers != null)
                {
                    foreach (var ofc in Officers)
                    {
                        if (ofc != null && ofc.Exists())
                        {
                            BlipService.SafeDeleteEntityBlip(ofc);
                        }
                    }
                }
            }

            public void Dispose(bool immediateDelete)
            {
                BlipService.SafeDeleteBlip(ref RadarBlip);
                ClearFootBlips();

                if (Cruiser != null && Cruiser.Exists())
                {
                    TeardownHelper.SafeDismissVehicle(Cruiser, immediateDelete);
                }
                if (Officers != null)
                {
                    foreach (var ofc in Officers)
                    {
                        if (ofc != null && ofc.Exists())
                        {
                            TeardownHelper.SafeDismissPed(ofc, immediateDelete);
                        }
                    }
                }
            }
        }
    }
}
