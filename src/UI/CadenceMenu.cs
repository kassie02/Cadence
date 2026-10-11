using System;
using System.Reflection;
using System.Windows.Forms;
using Rage;
using RAGENativeUI;
using RAGENativeUI.Elements;
using Cadence.Core;

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
        private static UIMenu _testingMenu;

        // Submenu 1: Telemetry Items
        private static UIMenuItem _patrolCountItem;
        private static UIMenuItem _trafficStopCountItem;
        private static UIMenuItem _stationaryCountItem;
        private static UIMenuItem _emergencyRunCountItem;
        private static UIMenuItem _pursuitCountItem;
        private static UIMenuItem _combatCountItem;
        private static UIMenuItem _totalCountItem;

        // Submenu 2: Feature Toggles
        private static UIMenuCheckboxItem _patrolToggle;
        private static UIMenuCheckboxItem _trafficStopsToggle;
        private static UIMenuCheckboxItem _stationaryToggle;
        private static UIMenuCheckboxItem _emergencyRunsToggle;
        private static UIMenuCheckboxItem _pursuitToggle;
        private static UIMenuCheckboxItem _threatToggle;

        // Submenu 3: Testing & Spawns
        private static UIMenuItem _spawnPatrolItem;
        private static UIMenuItem _spawnPulloverItem;
        private static UIMenuItem _spawnStationaryItem;
        private static UIMenuItem _triggerEmergencyRunItem;

        // Top-Level Actions
        private static UIMenuItem _purgeAllItem;
        private static UIMenuItem _reloadConfigItem;

        // Watchdog & State
        private static GameFiber _watchdogFiber;
        private static bool _isInitialized;
        private static bool _isRunning;
        private static uint _lastTelemetryUpdate;

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
                    bool modPressed = Settings.MenuModifierKey == Keys.None || Game.IsKeyDownRightNow(Settings.MenuModifierKey);
                    if (modPressed && Game.IsKeyDown(Settings.MenuKey))
                    {
                        ToggleVisibility();
                    }
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
            if (IsVisible)
            {
                Close();
            }
            else if (!Game.IsPaused)
            {
                SyncTogglesFromSettings();
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

        public static void PurgeAllFeatures()
        {
            Logger.Info("[CadenceMenu] Purging all active Cadence features and units.");
            string[] featureNames = new[]
            {
                "PatrolFeature",
                "TrafficStopFeature",
                "StationaryFeature",
                "ResponseFeature",
                "PursuitFeature",
                "AssistFeature"
            };

            foreach (string feat in featureNames)
            {
                try
                {
                    Type t = Type.GetType($"Cadence.Features.{feat}");
                    if (t != null)
                    {
                        MethodInfo method = t.GetMethod("PurgeAll", BindingFlags.Public | BindingFlags.Static);
                        method?.Invoke(null, null);
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
                "AssistFeature"
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
            _totalCountItem = new UIMenuItem("Total Active Cadence Entities", "Cumulative count of all living Cadence officers and cruisers.");

            _telemetryMenu.AddItem(_patrolCountItem);
            _telemetryMenu.AddItem(_trafficStopCountItem);
            _telemetryMenu.AddItem(_stationaryCountItem);
            _telemetryMenu.AddItem(_emergencyRunCountItem);
            _telemetryMenu.AddItem(_pursuitCountItem);
            _telemetryMenu.AddItem(_combatCountItem);
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

            _togglesMenu.AddItem(_patrolToggle);
            _togglesMenu.AddItem(_trafficStopsToggle);
            _togglesMenu.AddItem(_stationaryToggle);
            _togglesMenu.AddItem(_emergencyRunsToggle);
            _togglesMenu.AddItem(_pursuitToggle);
            _togglesMenu.AddItem(_threatToggle);
            _togglesMenu.RefreshIndex();

            // Submenu 3: Testing & Spawns
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

            _testingMenu.AddItem(_spawnPatrolItem);
            _testingMenu.AddItem(_spawnPulloverItem);
            _testingMenu.AddItem(_spawnStationaryItem);
            _testingMenu.AddItem(_triggerEmergencyRunItem);
            _testingMenu.RefreshIndex();

            // Top-Level Actions on Main Menu
            _purgeAllItem = new UIMenuItem("~r~[Purge All Cadence Units]~s~", "Instantly clears all active Cadence entities, props, and blips.");
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
                UpdateTelemetry();
            };

            _testingMenu.OnItemSelect += (sender, item, index) =>
            {
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
                UpdateTelemetry();
            };

            _mainMenu.OnItemSelect += (sender, item, index) =>
            {
                if (item == _purgeAllItem)
                {
                    PurgeAllFeatures();
                    UpdateTelemetry();
                    Game.DisplayNotification("~b~Cadence~s~: All Cadence units, blips, and props ~r~purged~s~.");
                }
                else if (item == _reloadConfigItem)
                {
                    Settings.Load();
                    SyncTogglesFromSettings();
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
            int total = patrol + stops + stat + resp + pursuit + combat;

            if (_patrolCountItem != null) _patrolCountItem.RightLabel = $"~b~{patrol}~s~";
            if (_trafficStopCountItem != null) _trafficStopCountItem.RightLabel = $"~y~{stops}~s~";
            if (_stationaryCountItem != null) _stationaryCountItem.RightLabel = $"~g~{stat}~s~";
            if (_emergencyRunCountItem != null) _emergencyRunCountItem.RightLabel = $"~r~{resp}~s~";
            if (_pursuitCountItem != null) _pursuitCountItem.RightLabel = $"~o~{pursuit}~s~";
            if (_combatCountItem != null) _combatCountItem.RightLabel = $"~r~{combat}~s~";
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
                    GameFiber.Yield();
                }
            }
        }
    }
}
