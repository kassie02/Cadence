using System;
using System.Windows.Forms;
using Rage;
using LSPD_First_Response.Mod.API;
using Cadence.UI;
using Cadence.Features;
using Cadence.Services;
using Cadence.Common;

namespace Cadence.Core
{
    /// <summary>
    /// Cadence - Ambient Policing System for LSPDFR.
    /// Thin "scrunchie" plugin entry point connecting isolated feature strands.
    /// Strictly complies with the Ponytail delete-friendly architecture.
    /// </summary>
    public class Main : Plugin
    {
        private static bool _isOnDuty;
        private static bool _tornDown;
        private static readonly object _teardownLock = new object();

        public override void Initialize()
        {
            try
            {
                Logger.Initialize();

                Logger.Info("============================================================");
                Logger.Info("  Cadence - Ambient Policing System Initializing...");
                Logger.Info("============================================================");

                // Subscribe to LSPDFR OnDuty state transitions
                Functions.OnOnDutyStateChanged += OnDutyStateChanged;

                if (IsPlayerOnDuty())
                {
                    Logger.Info("[Main] Player is already On Duty. Initializing Cadence immediately.");
                    OnDutyStateChanged(true);
                }

                Logger.Info("[Main] Cadence hook registered successfully.");
            }
            catch (Exception ex)
            {
                Logger.Error("[Main] Fatal exception during Main.Initialize()", ex);
            }
        }

        private static bool IsPlayerOnDuty()
        {
            try
            {
                if (Functions.IsPlayerAvailableForCalls() || Functions.IsCalloutRunning())
                {
                    return true;
                }

                Ped playerPed = Game.LocalPlayer?.Character;
                if (playerPed != null && playerPed.Exists() && Functions.IsPedACop(playerPed))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Main] IsPlayerOnDuty state probe encountered exception: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Multi-step isolated teardown helper with per-step exception isolation [A1].
        /// Guarantees that a failure in one step never aborts remaining teardown steps.
        /// </summary>
        /// <param name="immediateDelete">If true, deletes entities immediately; otherwise performs clean dismissal.</param>
        private static void Teardown(bool immediateDelete)
        {
            lock (_teardownLock)
            {
                if (_tornDown) return;
                _tornDown = true;

                Logger.Info($"[Main] Initiating Cadence isolated teardown (immediateDelete={immediateDelete})...");

                // Step 1: Close in-game dashboard menu
                try
                {
                    CadenceMenu.Close();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Main] Teardown Step 1 (CadenceMenu.Close) encountered exception: {ex.Message}");
                }

                // Step 2: Stop player vehicle monitoring and clean up blip
                try
                {
                    BlipService.StopPlayerVehicleMonitoring();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Main] Teardown Step 2 (BlipService.StopPlayerVehicleMonitoring) encountered exception: {ex.Message}");
                }

                // Step 3: Purge all active entities, blips, and sessions across all features (Inverted Teardown Rule)
                try
                {
                    FeatureRegistry.PurgeAll(immediateDelete);
                }
                catch (Exception ex)
                {
                    Logger.Error("[Main] Teardown Step 3 (FeatureRegistry.PurgeAll) encountered exception", ex);
                }

                // Step 4: Stop all background feature processing fibers
                try
                {
                    FeatureRegistry.StopAll();
                }
                catch (Exception ex)
                {
                    Logger.Error("[Main] Teardown Step 4 (FeatureRegistry.StopAll) encountered exception", ex);
                }

                // Step 5: Stop and tear down menu system
                try
                {
                    CadenceMenu.Stop();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Main] Teardown Step 5 (CadenceMenu.Stop) encountered exception: {ex.Message}");
                }

                Logger.Info("[Main] Isolated teardown sequence completed.");
            }
        }

        public override void Finally()
        {
            try
            {
                Logger.Info("[Main] Plugin Finally() triggered. Unloading Cadence...");

                try
                {
                    Functions.OnOnDutyStateChanged -= OnDutyStateChanged;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Main] Unsubscribing OnOnDutyStateChanged encountered exception: {ex.Message}");
                }

                _isOnDuty = false;
                Teardown(immediateDelete: true);

                // Step 6: Shutdown Ultimate Backup Integration singleton caches [A12]
                try
                {
                    UltimateBackupService.Shutdown();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Main] UltimateBackupService.Shutdown encountered exception: {ex.Message}");
                }

                Logger.Info("[Main] Cadence successfully unloaded.");

                // Step 7: Logger shutdown (logs session summary once here [A13])
                try
                {
                    Logger.Shutdown();
                }
                catch (Exception ex)
                {
                    try
                    {
                        Game.LogTrivial($"[Cadence] [ERROR] Logger.Shutdown exception: {ex.Message}");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[Main] Exception during Main.Finally() cleanup", ex);
            }
        }

        private static void OnDutyStateChanged(bool onDuty)
        {
            try
            {
                if (onDuty)
                {
                    if (_isOnDuty) return;
                    _isOnDuty = true;
                    _tornDown = false; // Reset teardown guard for the new on-duty session

                    // 1. Load User Configuration
                    Settings.Load();

                    if (!Settings.EnablePlugin)
                    {
                        Logger.Info("[Main] Cadence is DEACTIVATED in Cadence.ini (EnablePlugin=false). Skipping feature bootstrap.");
                        Game.DisplayNotification("~y~[Cadence] Plugin is deactivated in Cadence.ini (EnablePlugin = false).~s~");
                        return;
                    }

                    // 2. Initialize Ultimate Backup Integration
                    UltimateBackupService.Initialize();

                    if (Settings.UseUltimateBackup && !Settings.AllowFallback && !UltimateBackupService.IsAvailable)
                    {
                        Logger.Warn("[Main] Ultimate Backup is required (Fallback=false) but was NOT detected! Ambient police spawning is disabled until UB loads.");
                        Game.DisplayNotification("~r~[Cadence] Ultimate Backup not detected! Spawning paused (Fallback=false in settings).~s~");
                    }

                    // 3. Initialize In-Game Menu
                    CadenceMenu.Initialize();

                    // 4. Bootstrap Enabled Features (Staggered Ambient Pacing)
                    Logger.Info("[Main] Bootstrapping features with staggered spawn pacing: Patrol (T+15s), Stationary (T+45s), Traffic Stops (T+75s), Response (T+105s).");
                    if (Settings.EnablePatrol) PatrolFeature.Start();
                    if (Settings.EnableStationary) StationaryFeature.Start();
                    if (Settings.EnableTrafficStops) TrafficStopFeature.Start();
                    if (Settings.EnableEmergencyRuns || Settings.EnableRejectedCallouts) ResponseFeature.Start();
                    if (Settings.EnablePursuitDrafting) PursuitFeature.Start();
                    if (Settings.EnableThreatDefense) AssistFeature.Start();
                    if (Settings.EnableBackup) BackupFeature.Start();
                    BlipService.StartPlayerVehicleMonitoring();

                    // 5. Welcome Notification
                    string keyNotice;
                    if (Settings.MenuKey == Keys.None)
                    {
                        keyNotice = "Menu keybind disabled.";
                    }
                    else
                    {
                        string keyName = Settings.MenuKey == Keys.PageDown ? "PageDown" : Settings.MenuKey.ToString();
                        if (Settings.MenuModifierKey != Keys.None)
                        {
                            string modName = Settings.MenuModifierKey == Keys.PageDown ? "PageDown" : Settings.MenuModifierKey.ToString();
                            keyNotice = $"Press ~y~{modName} + {keyName}~s~ for menu.";
                        }
                        else
                        {
                            keyNotice = $"Press ~y~{keyName}~s~ for menu.";
                        }
                    }
                    Game.DisplayNotification($"~b~Cadence~s~ loaded successfully. {keyNotice}");

                    Logger.Info($"[Main] Cadence OnDuty transition complete. MenuKey: {Settings.MenuKey}, ModifierKey: {Settings.MenuModifierKey}");
                }
                else
                {
                    if (!_isOnDuty) return;
                    _isOnDuty = false;

                    Logger.Info("[Main] Cadence OffDuty transition triggered. Standing down features.");
                    // Note: session summary logging is deferred to Logger.Shutdown() on unload [A13]

                    Teardown(immediateDelete: false);

                    Logger.Info("[Main] All Cadence units, blips, and features stood down.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[Main] Exception encountered during OnDutyStateChanged", ex);
            }
        }
    }
}
