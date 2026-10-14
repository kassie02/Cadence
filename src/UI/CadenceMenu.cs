using System;
using System.Reflection;
using System.Windows.Forms;
using Rage;
using Rage.Native;
using RAGENativeUI;
using RAGENativeUI.Elements;
using Cadence.Core;
using Cadence.Common;

namespace Cadence.UI
{
    /// <summary>
    /// Premium In-Game Menu & Telemetry Dashboard for Cadence.
    /// Built with RAGENativeUI.
    /// Isolated from direct feature dependencies to honor the Ponytail architecture.
    /// </summary>
    public static class CadenceMenu
    {
        private static readonly object _syncLock = new object();

        // RAGENativeUI Core
        private static MenuPool _menuPool;
        private static UIMenu _mainMenu;
        private static UIMenu _telemetryMenu;
        private static UIMenu _togglesMenu;
        private static UIMenu _settingsMenu;
        private static UIMenu _testingMenu;

        // Submenu 1: Telemetry Items
        private static UIMenuItem _patrolCountItem;
        private static UIMenuItem _trafficStopCountItem;
        private static UIMenuItem _stationaryCountItem;
        private static UIMenuItem _emergencyRunCountItem;
        private static UIMenuItem _pursuitCountItem;
        private static UIMenuItem _combatCountItem;
        private static UIMenuItem _backupCountItem;
        private static UIMenuItem _totalCountItem;

        // Submenu 2: Feature Toggles
        private static UIMenuCheckboxItem _patrolToggle;
        private static UIMenuCheckboxItem _trafficStopsToggle;
        private static UIMenuCheckboxItem _stationaryToggle;
        private static UIMenuCheckboxItem _emergencyRunsToggle;
        private static UIMenuCheckboxItem _pursuitToggle;
        private static UIMenuCheckboxItem _threatToggle;
        private static UIMenuCheckboxItem _backupToggle;

        // Submenu 3: Settings & Customization
        private static UIMenuListItem _presetItem;
        private static UIMenuListItem _maxPatrolItem;
        private static UIMenuListItem _maxStopsItem;
        private static UIMenuListItem _maxStationaryItem;
        private static UIMenuListItem _maxEmergencyItem;
        private static UIMenuListItem _emergencyVarietyItem;
        private static UIMenuListItem _jurisdictionBalanceItem;
        private static UIMenuCheckboxItem _fallbackToggle;

        // Submenu 4: Testing & Spawns
        private static UIMenuItem _spawnPatrolItem;
        private static UIMenuItem _spawnPulloverItem;
        private static UIMenuItem _spawnStationaryItem;
        private static UIMenuItem _triggerEmergencyRunItem;
        private static UIMenuItem _triggerPanicItem;
        private static UIMenuItem _triggerGroupBackupItem;

        // Top-Level Actions
        private static UIMenuItem _purgeAllItem;
        private static UIMenuItem _reloadConfigItem;

        // Option Value Mappings
        private static readonly object[] PresetLabels = new object[] { "Realistic", "Chaos" };

        private static readonly int[] PatrolLimitValues = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16 };
        private static readonly object[] PatrolLimitLabels = new object[] { "1", "2", "3", "4 (Def)", "5", "6", "7", "8", "10", "12", "14", "16" };

        private static readonly int[] StopLimitValues = new int[] { 0, 1, 2, 3, 4, 5, 6, 8 };
        private static readonly object[] StopLimitLabels = new object[] { "Off", "1", "2 (Def)", "3", "4", "5", "6", "8" };

        private static readonly int[] StationaryLimitValues = new int[] { 0, 1, 2, 3, 4, 5, 6, 8 };
        private static readonly object[] StationaryLimitLabels = new object[] { "Off", "1", "2 (Def)", "3", "4", "5", "6", "8" };

        private static readonly int[] EmergencyLimitValues = new int[] { 0, 1, 2, 3, 4, 5, 6, 8 };
        private static readonly object[] EmergencyLimitLabels = new object[] { "Off", "1", "2 (Def)", "3", "4", "5", "6", "8" };

        private static readonly object[] EmergencyVarietyLabels = new object[] { "All (Def)", "Police/EMS", "Police/Fire", "Police Only" };

        private static readonly int[] HighwayRatioValues = new int[] { 50, 75, 25, 0, 100 };
        private static readonly object[] HighwayRatioLabels = new object[] { "Balanced (50/50)", "Highway (75/25)", "Local (25/75)", "Local (0% Fwy)", "Highway (100%)" };

        // Watchdog & State
        private static GameFiber _watchdogFiber;
        private static bool _isInitialized;
        private static bool _isRunning;
        private static uint _lastTelemetryUpdate;
        private static uint _lastMenuToggleTime;

        // ====================================================================
        // Public API Contract
        // ====================================================================

        public static bool IsVisible
        {
            get
            {
                if (_menuPool == null) return false;
                foreach (UIMenu menu in _menuPool)
                {
                    if (menu != null && menu.Visible) return true;
                }
                return false;
            }
        }

        public static void Initialize()
        {
            lock (_syncLock)
            {
                if (_isInitialized)
                {
                    SyncTogglesFromSettings();
                    SyncSettingsMenuValues();
                    UpdateTelemetry();
                    return;
                }

                Logger.Info("[CadenceMenu] Initializing Cadence in-game menu system.");
                BuildMenus();

                _isRunning = true;
                _isInitialized = true;

                if (_watchdogFiber == null || !_watchdogFiber.IsAlive)
                {
                    _watchdogFiber = GameFiber.StartNew(MenuFiberLoop, "Cadence - Menu Watchdog Fiber");
                }
            }
        }

        public static void Process()
        {
            if (!_isInitialized) return;

            try
            {
                bool inputBlocked = Game.Console.IsOpen || Game.IsPaused;
                if (inputBlocked)
                {
                    if (IsVisible)
                    {
                        Close();
                    }
                    return;
                }

                // Keybind Watchdog: Strictly disabled if Settings.MenuKey == Keys.None (0 / None / Disabled)
                if (Settings.MenuKey != Keys.None)
                {
                    if (InputHelper.IsKeyComboPressed(Settings.MenuKey, Settings.MenuModifierKey))
                    {
                        ToggleVisibility();
                    }

                    // Suppress GTA V native cover attempt while holding Q as menu modifier or key
                    InputHelper.SuppressCoverIfQHeld(Settings.MenuKey, Settings.MenuModifierKey);
                }

                if (IsVisible)
                {
                    _menuPool?.ProcessMenus();

                    // Real-time telemetry updates while the telemetry submenu is actively viewed
                    if (_telemetryMenu != null && _telemetryMenu.Visible)
                    {
                        uint now = Game.GameTime;
                        if (now - _lastTelemetryUpdate >= 1000)
                        {
                            _lastTelemetryUpdate = now;
                            UpdateTelemetry();
                        }
                    }
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Fiber terminating
            }
            catch (Exception ex)
            {
                Logger.Error("[CadenceMenu] Exception in CadenceMenu.Process()", ex);
            }
        }

        public static void ToggleVisibility()
        {
            uint now = Game.GameTime;
            if (now - _lastMenuToggleTime < 300) return;
            _lastMenuToggleTime = now;

            if (IsVisible)
            {
                Close();
            }
            else if (!Game.IsPaused)
            {
                SyncTogglesFromSettings();
                SyncSettingsMenuValues();
                UpdateTelemetry();
                if (_mainMenu != null)
                {
                    _mainMenu.Visible = true;
                }
            }
        }

        public static void Close()
        {
            if (_menuPool != null)
            {
                foreach (UIMenu menu in _menuPool)
                {
                    if (menu != null)
                    {
                        menu.Visible = false;
                    }
                }
            }
        }

        public static void Stop()
        {
            lock (_syncLock)
            {
                _isRunning = false;
                Close();

                try
                {
                    if (_watchdogFiber != null && _watchdogFiber.IsAlive)
                    {
                        _watchdogFiber.Abort();
                    }
                }
                catch { }
                finally
                {
                    _watchdogFiber = null;
                }

                _isInitialized = false;
                Logger.Info("[CadenceMenu] Menu system stopped.");
            }
        }

        // ====================================================================
        // Feature Lifecycle & Ponytail Invocation Dispatchers
        // ====================================================================

        public static void PurgeAllFeatures(bool immediateDelete = false)
        {
            Logger.Info($"[CadenceMenu] Purging all active Cadence features, vehicles, sirens, and blips (immediateDelete={immediateDelete}).");
            string[] featureNames = new[]
            {
                "PatrolFeature",
                "TrafficStopFeature",
                "StationaryFeature",
                "ResponseFeature",
                "PursuitFeature",
                "AssistFeature",
                "BackupFeature"
            };

            foreach (string feat in featureNames)
            {
                try
                {
                    Type t = Type.GetType($"Cadence.Features.{feat}");
                    if (t != null)
                    {
                        MethodInfo method = t.GetMethod("PurgeAll", BindingFlags.Public | BindingFlags.Static);
                        if (method != null)
                        {
                            var parameters = method.GetParameters();
                            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
                            {
                                method.Invoke(null, new object[] { immediateDelete });
                            }
                            else
                            {
                                method.Invoke(null, null);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[CadenceMenu] Failed to purge {feat}", ex);
                }
            }
        }

        public static void StopAllFeatures()
        {
            Logger.Info("[CadenceMenu] Stopping all Cadence features.");
            string[] featureNames = new[]
            {
                "PatrolFeature",
                "TrafficStopFeature",
                "StationaryFeature",
                "ResponseFeature",
                "PursuitFeature",
                "AssistFeature",
                "BackupFeature"
            };

            foreach (string feat in featureNames)
            {
                try
                {
                    Type t = Type.GetType($"Cadence.Features.{feat}");
                    if (t != null)
                    {
                        MethodInfo method = t.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static);
                        method?.Invoke(null, null);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[CadenceMenu] Failed to stop {feat}", ex);
                }
            }
        }

        private static int GetFeatureCount(string featureName)
        {
            try
            {
                Type t = Type.GetType($"Cadence.Features.{featureName}");
                if (t != null)
                {
                    MethodInfo method = t.GetMethod("GetActiveCount", BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        return (int)method.Invoke(null, null);
                    }
                }
            }
            catch { }
            return 0;
        }

        private static bool InvokeFeatureSpawn(string featureName, string methodName)
        {
            try
            {
                Type t = Type.GetType($"Cadence.Features.{featureName}");
                if (t != null)
                {
                    MethodInfo method = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        return (bool)method.Invoke(null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[CadenceMenu] Failed to invoke {featureName}.{methodName}", ex);
            }
            return false;
        }

        private static void ToggleFeature(string featureName, bool enable)
        {
            try
            {
                Type t = Type.GetType($"Cadence.Features.{featureName}");
                if (t != null)
                {
                    string methodName = enable ? "Start" : "Stop";
                    MethodInfo method = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                    method?.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[CadenceMenu] Failed to toggle {featureName} ({enable})", ex);
            }
        }

        // ====================================================================
        // Menu Builder & Event Wiring
        // ====================================================================

        private static void BuildMenus()
        {
            _menuPool = new MenuPool();

            // Root Menu
            _mainMenu = new UIMenu("Cadence", "~b~Ambient Policing System~s~");
            _mainMenu.MouseControlsEnabled = false;
            _menuPool.Add(_mainMenu);

            // Submenu 1: Live Telemetry & Dashboard
            _telemetryMenu = new UIMenu("Cadence", "~b~Live Telemetry & Dashboard~s~");
            _telemetryMenu.MouseControlsEnabled = false;
            _menuPool.Add(_telemetryMenu);

            UIMenuItem telemetrySubItem = new UIMenuItem("Live Telemetry & Dashboard", "Real-time active entity metrics and deployment counters.");
            _mainMenu.AddItem(telemetrySubItem);
            _mainMenu.BindMenuToItem(_telemetryMenu, telemetrySubItem);

            _patrolCountItem = new UIMenuItem("Active Patrol Cruisers", "Autonomous police cruisers actively patrolling roadways.");
            _trafficStopCountItem = new UIMenuItem("Traffic Stops", "Officers currently conducting roadside pullovers and citations.");
            _stationaryCountItem = new UIMenuItem("Speed Traps / Stationary", "Parked standby units observing traffic and running radar.");
            _emergencyRunCountItem = new UIMenuItem("Emergency Runs", "Units responding Code 2 or Code 3 to background incidents.");
            _pursuitCountItem = new UIMenuItem("Pursuit Units", "Ambient cruisers drafted to assist in active police chases.");
            _combatCountItem = new UIMenuItem("Combat Units", "Officers engaging hostile threats or active gunfire.");
            _backupCountItem = new UIMenuItem("Active Backup Units", "Responding or on-scene backup units assisting the player.");
            _totalCountItem = new UIMenuItem("Total Active Cadence Entities", "Cumulative count of all living Cadence officers and cruisers.");

            _telemetryMenu.AddItem(_patrolCountItem);
            _telemetryMenu.AddItem(_trafficStopCountItem);
            _telemetryMenu.AddItem(_stationaryCountItem);
            _telemetryMenu.AddItem(_emergencyRunCountItem);
            _telemetryMenu.AddItem(_pursuitCountItem);
            _telemetryMenu.AddItem(_combatCountItem);
            _telemetryMenu.AddItem(_backupCountItem);
            _telemetryMenu.AddItem(_totalCountItem);
            _telemetryMenu.RefreshIndex();

            // Submenu 2: Real-Time Feature Toggles
            _togglesMenu = new UIMenu("Cadence", "~b~Real-Time Feature Toggles~s~");
            _togglesMenu.MouseControlsEnabled = false;
            _menuPool.Add(_togglesMenu);

            UIMenuItem togglesSubItem = new UIMenuItem("Real-Time Feature Toggles", "Pause or resume individual ambient policing features on the fly.");
            _mainMenu.AddItem(togglesSubItem);
            _mainMenu.BindMenuToItem(_togglesMenu, togglesSubItem);

            _patrolToggle = new UIMenuCheckboxItem("Patrol Cruisers", Settings.EnablePatrol, "Autonomous road cruising patrol cruisers.");
            _trafficStopsToggle = new UIMenuCheckboxItem("Traffic Stops", Settings.EnableTrafficStops, "1803 roadside traffic stops and citations.");
            _stationaryToggle = new UIMenuCheckboxItem("Stationary & Speed Traps", Settings.EnableStationary, "Parked speed traps and stationary watch units.");
            _emergencyRunsToggle = new UIMenuCheckboxItem("Emergency Runs", Settings.EnableEmergencyRuns, "Autonomous Code 2/3 background emergency runs.");
            _pursuitToggle = new UIMenuCheckboxItem("Pursuit Drafting", Settings.EnablePursuitDrafting, "Drafting nearby ambient cruisers into active pursuits.");
            _threatToggle = new UIMenuCheckboxItem("Threat Defense", Settings.EnableThreatDefense, "Tactical officer defense when gunfire or threats erupt.");
            _backupToggle = new UIMenuCheckboxItem("Ambient Backup System", Settings.EnableBackup, "Panic button, group backup, and lean backup menu responses.");

            _togglesMenu.AddItem(_patrolToggle);
            _togglesMenu.AddItem(_trafficStopsToggle);
            _togglesMenu.AddItem(_stationaryToggle);
            _togglesMenu.AddItem(_emergencyRunsToggle);
            _togglesMenu.AddItem(_pursuitToggle);
            _togglesMenu.AddItem(_threatToggle);
            _togglesMenu.AddItem(_backupToggle);
            _togglesMenu.RefreshIndex();

            // Submenu 3: Settings & Customization
            _settingsMenu = new UIMenu("Cadence", "~b~Settings & Customization~s~");
            _settingsMenu.MouseControlsEnabled = false;
            _menuPool.Add(_settingsMenu);

            UIMenuItem settingsSubItem = new UIMenuItem("Settings & Customization", "Adjust ambient unit limits, density presets, and emergency variety.");
            _mainMenu.AddItem(settingsSubItem);
            _mainMenu.BindMenuToItem(_settingsMenu, settingsSubItem);

            _presetItem = new UIMenuListItem("Preset Profile", "Toggle density profile: Realistic (default low-profile) vs Chaos (Living City density).", PresetLabels);
            _maxPatrolItem = new UIMenuListItem("Max Patrol Cruisers", "Maximum ambient cruisers patrolling roadways simultaneously.", PatrolLimitLabels);
            _maxStopsItem = new UIMenuListItem("Max Traffic Stops", "Maximum roadside traffic stops and citations active at one time.", StopLimitLabels);
            _maxStationaryItem = new UIMenuListItem("Max Speed Traps", "Maximum parked speed traps and stationary watch units.", StationaryLimitLabels);
            _maxEmergencyItem = new UIMenuListItem("Max Emergency Runs", "Maximum autonomous background emergency runs responding concurrently.", EmergencyLimitLabels);
            _emergencyVarietyItem = new UIMenuListItem("Emergency Variety", "Permitted emergency agency types for autonomous background runs.", EmergencyVarietyLabels);
            _jurisdictionBalanceItem = new UIMenuListItem("Highway Ratio", "Ratio between State / Highway Patrol and Local Police near freeways.", HighwayRatioLabels);
            _fallbackToggle = new UIMenuCheckboxItem("Allow Native Fallback", Settings.AllowFallback, "Permit vanilla GTA vehicle spawns if Ultimate Backup is unavailable or returns null.");

            _settingsMenu.AddItem(_presetItem);
            _settingsMenu.AddItem(_maxPatrolItem);
            _settingsMenu.AddItem(_maxStopsItem);
            _settingsMenu.AddItem(_maxStationaryItem);
            _settingsMenu.AddItem(_maxEmergencyItem);
            _settingsMenu.AddItem(_emergencyVarietyItem);
            _settingsMenu.AddItem(_jurisdictionBalanceItem);
            _settingsMenu.AddItem(_fallbackToggle);
            _settingsMenu.RefreshIndex();

            // Submenu 4: Testing & Spawns
            _testingMenu = new UIMenu("Cadence", "~b~Testing & Spawns~s~");
            _testingMenu.MouseControlsEnabled = false;
            _menuPool.Add(_testingMenu);

            UIMenuItem testingSubItem = new UIMenuItem("Testing & Spawns", "Manually trigger test spawns and simulated emergency dispatches.");
            _mainMenu.AddItem(testingSubItem);
            _mainMenu.BindMenuToItem(_testingMenu, testingSubItem);

            _spawnPatrolItem = new UIMenuItem("Spawn Patrol Cruiser", "Instantly spawns an ambient patrol cruiser on the road ahead.");
            _spawnPulloverItem = new UIMenuItem("Spawn Roadside Pullover", "Spawns a roadside pullover traffic stop at a safe shoulder spot.");
            _spawnStationaryItem = new UIMenuItem("Spawn Radar Speed Trap", "Spawns a parked speed trap / stationary watch unit nearby.");
            _triggerEmergencyRunItem = new UIMenuItem("Trigger Code 3 Emergency Run", "Dispatches an ambient cruiser Code 3 to an incident node.");
            _triggerPanicItem = new UIMenuItem("Trigger Panic Backup", "Immediately triggers high-priority emergency panic backup.");
            _triggerGroupBackupItem = new UIMenuItem("Trigger Group Backup", "Immediately dispatches multi-unit tactical group reinforcement.");

            _testingMenu.AddItem(_spawnPatrolItem);
            _testingMenu.AddItem(_spawnPulloverItem);
            _testingMenu.AddItem(_spawnStationaryItem);
            _testingMenu.AddItem(_triggerEmergencyRunItem);
            _testingMenu.AddItem(_triggerPanicItem);
            _testingMenu.AddItem(_triggerGroupBackupItem);
            _testingMenu.RefreshIndex();

            // Top-Level Actions on Main Menu
            _purgeAllItem = new UIMenuItem("~r~[Purge All Cadence Units]~s~", "Instantly clears all active Cadence entities, props, blips, and silences sirens.");
            _reloadConfigItem = new UIMenuItem("[Reload Cadence.ini]", "Reloads all settings from the configuration file on the fly.");

            _mainMenu.AddItem(_purgeAllItem);
            _mainMenu.AddItem(_reloadConfigItem);
            _mainMenu.RefreshIndex();

            // Hook Events
            HookEvents();
        }

        private static void HookEvents()
        {
            _telemetryMenu.OnMenuOpen += (sender) => UpdateTelemetry();
            _settingsMenu.OnMenuOpen += (sender) => SyncSettingsMenuValues();

            _settingsMenu.OnListChange += (sender, listItem, newIndex) =>
            {
                if (listItem == _presetItem)
                {
                    bool isChaos = (newIndex == 1);
                    Settings.ApplyChaosPreset(isChaos);
                    SyncSettingsMenuValues();
                    Game.DisplayNotification($"~b~Cadence~s~: Applied {(isChaos ? "~r~Chaos Preset~s~ (Living City Density)" : "~g~Realistic Preset~s~ (Default Density)")}.");
                }
                else if (listItem == _maxPatrolItem)
                {
                    Settings.MaxPatrolUnits = PatrolLimitValues[newIndex];
                    Settings.Preset = "Custom";
                    _presetItem.Index = 0;
                    Game.DisplayNotification($"~b~Cadence~s~: Max Patrol Cruisers set to ~b~{Settings.MaxPatrolUnits}~s~.");
                }
                else if (listItem == _maxStopsItem)
                {
                    Settings.MaxTrafficStops = StopLimitValues[newIndex];
                    Settings.Preset = "Custom";
                    _presetItem.Index = 0;
                    Game.DisplayNotification($"~b~Cadence~s~: Max Traffic Stops set to ~y~{Settings.MaxTrafficStops}~s~.");
                }
                else if (listItem == _maxStationaryItem)
                {
                    Settings.MaxStationaryUnits = StationaryLimitValues[newIndex];
                    Settings.Preset = "Custom";
                    _presetItem.Index = 0;
                    Game.DisplayNotification($"~b~Cadence~s~: Max Speed Traps set to ~g~{Settings.MaxStationaryUnits}~s~.");
                }
                else if (listItem == _maxEmergencyItem)
                {
                    Settings.MaxEmergencyRuns = EmergencyLimitValues[newIndex];
                    Settings.Preset = "Custom";
                    _presetItem.Index = 0;
                    Game.DisplayNotification($"~b~Cadence~s~: Max Emergency Runs set to ~r~{Settings.MaxEmergencyRuns}~s~.");
                }
                else if (listItem == _emergencyVarietyItem)
                {
                    switch (newIndex)
                    {
                        case 0:
                            Settings.AllowPoliceRuns = true;
                            Settings.AllowFireRuns = true;
                            Settings.AllowAmbulanceRuns = true;
                            Game.DisplayNotification("~b~Cadence~s~: Emergency Variety: ~g~Police, Fire & EMS~s~.");
                            break;
                        case 1:
                            Settings.AllowPoliceRuns = true;
                            Settings.AllowFireRuns = false;
                            Settings.AllowAmbulanceRuns = true;
                            Game.DisplayNotification("~b~Cadence~s~: Emergency Variety: ~g~Police & EMS~s~.");
                            break;
                        case 2:
                            Settings.AllowPoliceRuns = true;
                            Settings.AllowFireRuns = true;
                            Settings.AllowAmbulanceRuns = false;
                            Game.DisplayNotification("~b~Cadence~s~: Emergency Variety: ~g~Police & Fire~s~.");
                            break;
                        case 3:
                            Settings.AllowPoliceRuns = true;
                            Settings.AllowFireRuns = false;
                            Settings.AllowAmbulanceRuns = false;
                            Game.DisplayNotification("~b~Cadence~s~: Emergency Variety: ~g~Police Only~s~.");
                            break;
                    }
                }
                else if (listItem == _jurisdictionBalanceItem)
                {
                    Settings.HighwayPatrolRatio = HighwayRatioValues[newIndex];
                    Game.DisplayNotification($"~b~Cadence~s~: Highway Patrol Ratio set to ~b~{Settings.HighwayPatrolRatio}%~s~.");
                }
            };

            _settingsMenu.OnCheckboxChange += (sender, item, isChecked) =>
            {
                if (item == _fallbackToggle)
                {
                    Settings.AllowFallback = isChecked;
                    Game.DisplayNotification($"~b~Cadence~s~: Allow Native Fallback: {(isChecked ? "~r~Enabled (Vanilla Models Allowed)~s~" : "~g~Disabled (Strict UB Only)~s~")}.");
                }
            };

            _togglesMenu.OnCheckboxChange += (sender, item, isChecked) =>
            {
                if (item == _patrolToggle)
                {
                    Settings.EnablePatrol = isChecked;
                    ToggleFeature("PatrolFeature", isChecked);
                    Game.DisplayNotification($"~b~Cadence~s~: Patrol Cruisers {(isChecked ? "~g~Resumed~s~" : "~o~Paused~s~")}.");
                }
                else if (item == _trafficStopsToggle)
                {
                    Settings.EnableTrafficStops = isChecked;
                    ToggleFeature("TrafficStopFeature", isChecked);
                    Game.DisplayNotification($"~b~Cadence~s~: Traffic Stops {(isChecked ? "~g~Resumed~s~" : "~o~Paused~s~")}.");
                }
                else if (item == _stationaryToggle)
                {
                    Settings.EnableStationary = isChecked;
                    ToggleFeature("StationaryFeature", isChecked);
                    Game.DisplayNotification($"~b~Cadence~s~: Stationary Units {(isChecked ? "~g~Resumed~s~" : "~o~Paused~s~")}.");
                }
                else if (item == _emergencyRunsToggle)
                {
                    Settings.EnableEmergencyRuns = isChecked;
                    bool anyResponse = Settings.EnableEmergencyRuns || Settings.EnableRejectedCallouts;
                    ToggleFeature("ResponseFeature", anyResponse);
                    Game.DisplayNotification($"~b~Cadence~s~: Emergency Runs {(isChecked ? "~g~Resumed~s~" : "~o~Paused~s~")}.");
                }
                else if (item == _pursuitToggle)
                {
                    Settings.EnablePursuitDrafting = isChecked;
                    ToggleFeature("PursuitFeature", isChecked);
                    Game.DisplayNotification($"~b~Cadence~s~: Pursuit Drafting {(isChecked ? "~g~Resumed~s~" : "~o~Paused~s~")}.");
                }
                else if (item == _threatToggle)
                {
                    Settings.EnableThreatDefense = isChecked;
                    ToggleFeature("AssistFeature", isChecked);
                    Game.DisplayNotification($"~b~Cadence~s~: Threat Defense {(isChecked ? "~g~Resumed~s~" : "~o~Paused~s~")}.");
                }
                else if (item == _backupToggle)
                {
                    Settings.EnableBackup = isChecked;
                    ToggleFeature("BackupFeature", isChecked);
                    Game.DisplayNotification($"~b~Cadence~s~: Ambient Backup System {(isChecked ? "~g~Resumed~s~" : "~o~Paused~s~")}.");
                }
                UpdateTelemetry();
            };

            _testingMenu.OnItemSelect += (sender, item, index) =>
            {
                Logger.Info("Menu", $"User selected test spawn: {item.Text}");

                if (item == _spawnPatrolItem)
                {
                    bool ok = InvokeFeatureSpawn("PatrolFeature", "SpawnTestUnit");
                    if (ok) Game.DisplayNotification("~b~Cadence~s~: Patrol cruiser ~g~spawned~s~.");
                    else Game.DisplayNotification("~b~Cadence~s~: ~r~Failed~s~ to spawn patrol cruiser (check safe street position).");
                }
                else if (item == _spawnPulloverItem)
                {
                    bool ok = InvokeFeatureSpawn("TrafficStopFeature", "SpawnTestStop");
                    if (ok) Game.DisplayNotification("~b~Cadence~s~: Roadside pullover ~g~spawned~s~.");
                    else Game.DisplayNotification("~b~Cadence~s~: ~r~Failed~s~ to spawn roadside pullover (check shoulder spot).");
                }
                else if (item == _spawnStationaryItem)
                {
                    bool ok = InvokeFeatureSpawn("StationaryFeature", "SpawnTestStationary");
                    if (ok) Game.DisplayNotification("~b~Cadence~s~: Radar speed trap ~g~spawned~s~.");
                    else Game.DisplayNotification("~b~Cadence~s~: ~r~Failed~s~ to spawn speed trap (check turnout spot).");
                }
                else if (item == _triggerEmergencyRunItem)
                {
                    bool ok = InvokeFeatureSpawn("ResponseFeature", "TriggerTestRun");
                    if (ok) Game.DisplayNotification("~b~Cadence~s~: Code 3 emergency run ~g~triggered~s~.");
                    else Game.DisplayNotification("~b~Cadence~s~: ~r~Failed~s~ to trigger emergency run.");
                }
                else if (item == _triggerPanicItem)
                {
                    bool ok = InvokeFeatureSpawn("BackupFeature", "TriggerPanicButton");
                    if (ok) Game.DisplayNotification("~b~Cadence~s~: Panic backup ~r~triggered~s~.");
                    else Game.DisplayNotification("~b~Cadence~s~: ~r~Failed~s~ to trigger panic backup.");
                }
                else if (item == _triggerGroupBackupItem)
                {
                    bool ok = InvokeFeatureSpawn("BackupFeature", "TriggerGroupBackup");
                    if (ok) Game.DisplayNotification("~b~Cadence~s~: Group backup ~y~triggered~s~.");
                    else Game.DisplayNotification("~b~Cadence~s~: ~r~Failed~s~ to trigger group backup.");
                }
                UpdateTelemetry();
            };

            _mainMenu.OnItemSelect += (sender, item, index) =>
            {
                Logger.Info("Menu", $"User selected main menu action: {item.Text}");

                if (item == _purgeAllItem)
                {
                    PurgeAllFeatures();
                    UpdateTelemetry();
                    Game.DisplayNotification("~b~Cadence~s~: All Cadence units, blips, and runs ~r~purged~s~ (spawning cooldown active).");
                }
                else if (item == _reloadConfigItem)
                {
                    Settings.Load();
                    SyncTogglesFromSettings();
                    SyncSettingsMenuValues();
                    Game.DisplayNotification("~b~Cadence~s~: Configuration reloaded from ~y~Cadence.ini~s~.");
                }
            };
        }

        private static void UpdateTelemetry()
        {
            int patrol = GetFeatureCount("PatrolFeature");
            int stops = GetFeatureCount("TrafficStopFeature");
            int stat = GetFeatureCount("StationaryFeature");
            int resp = GetFeatureCount("ResponseFeature");
            int pursuit = GetFeatureCount("PursuitFeature");
            int combat = GetFeatureCount("AssistFeature");
            int backup = GetFeatureCount("BackupFeature");
            int total = patrol + stops + stat + resp + pursuit + combat + backup;

            if (_patrolCountItem != null) _patrolCountItem.RightLabel = $"~b~{patrol}~s~";
            if (_trafficStopCountItem != null) _trafficStopCountItem.RightLabel = $"~y~{stops}~s~";
            if (_stationaryCountItem != null) _stationaryCountItem.RightLabel = $"~g~{stat}~s~";
            if (_emergencyRunCountItem != null) _emergencyRunCountItem.RightLabel = $"~r~{resp}~s~";
            if (_pursuitCountItem != null) _pursuitCountItem.RightLabel = $"~o~{pursuit}~s~";
            if (_combatCountItem != null) _combatCountItem.RightLabel = $"~r~{combat}~s~";
            if (_backupCountItem != null) _backupCountItem.RightLabel = $"~g~{backup}~s~";
            if (_totalCountItem != null) _totalCountItem.RightLabel = $"~w~{total}~s~";
        }

        private static void SyncTogglesFromSettings()
        {
            if (_patrolToggle != null) _patrolToggle.Checked = Settings.EnablePatrol;
            if (_trafficStopsToggle != null) _trafficStopsToggle.Checked = Settings.EnableTrafficStops;
            if (_stationaryToggle != null) _stationaryToggle.Checked = Settings.EnableStationary;
            if (_emergencyRunsToggle != null) _emergencyRunsToggle.Checked = Settings.EnableEmergencyRuns;
            if (_pursuitToggle != null) _pursuitToggle.Checked = Settings.EnablePursuitDrafting;
            if (_threatToggle != null) _threatToggle.Checked = Settings.EnableThreatDefense;
            if (_backupToggle != null) _backupToggle.Checked = Settings.EnableBackup;
        }

        private static int GetClosestIndex(int[] array, int target)
        {
            int bestIndex = 0;
            int bestDiff = Math.Abs(array[0] - target);
            for (int i = 1; i < array.Length; i++)
            {
                int diff = Math.Abs(array[i] - target);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        private static int GetEmergencyVarietyIndex()
        {
            if (Settings.AllowPoliceRuns && Settings.AllowFireRuns && Settings.AllowAmbulanceRuns) return 0;
            if (Settings.AllowPoliceRuns && !Settings.AllowFireRuns && Settings.AllowAmbulanceRuns) return 1;
            if (Settings.AllowPoliceRuns && Settings.AllowFireRuns && !Settings.AllowAmbulanceRuns) return 2;
            return 3;
        }

        private static void SyncSettingsMenuValues()
        {
            if (_presetItem != null) _presetItem.Index = Settings.ChaosMode ? 1 : 0;
            if (_maxPatrolItem != null) _maxPatrolItem.Index = GetClosestIndex(PatrolLimitValues, Settings.MaxPatrolUnits);
            if (_maxStopsItem != null) _maxStopsItem.Index = GetClosestIndex(StopLimitValues, Settings.MaxTrafficStops);
            if (_maxStationaryItem != null) _maxStationaryItem.Index = GetClosestIndex(StationaryLimitValues, Settings.MaxStationaryUnits);
            if (_maxEmergencyItem != null) _maxEmergencyItem.Index = GetClosestIndex(EmergencyLimitValues, Settings.MaxEmergencyRuns);
            if (_emergencyVarietyItem != null) _emergencyVarietyItem.Index = GetEmergencyVarietyIndex();
            if (_jurisdictionBalanceItem != null) _jurisdictionBalanceItem.Index = GetClosestIndex(HighwayRatioValues, Settings.HighwayPatrolRatio);
            if (_fallbackToggle != null) _fallbackToggle.Checked = Settings.AllowFallback;
        }

        private static void MenuFiberLoop()
        {
            while (_isRunning)
            {
                try
                {
                    Process();
                }
                catch (System.Threading.ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("[CadenceMenu] Error in menu watchdog fiber loop", ex);
                }
                finally
                {
                    GameFiber.Sleep(IsVisible ? 0 : 25);
                }
            }
        }
    }
}
