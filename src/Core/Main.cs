using System;
using System.Windows.Forms;
using Rage;
using LSPD_First_Response.Mod.API;
using Cadence.UI;
using Cadence.Features;
using Cadence.Services;

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
            catch { }

            return false;
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
                catch { }

                _isOnDuty = false;
                CadenceMenu.PurgeAllFeatures(immediateDelete: true);
                CadenceMenu.StopAllFeatures();
                CadenceMenu.Stop();

                Logger.Info("[Main] Cadence successfully unloaded.");
                Logger.Shutdown();
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

                    // 3. Bootstrap Enabled Features (Staggered Ambient Pacing)
                    Logger.Info("[Main] Bootstrapping features with staggered spawn pacing: Patrol (T+15s), Stationary (T+45s), Traffic Stops (T+75s), Response (T+105s).");
                    if (Settings.EnablePatrol) PatrolFeature.Start();
                    if (Settings.EnableStationary) StationaryFeature.Start();
                    if (Settings.EnableTrafficStops) TrafficStopFeature.Start();
                    if (Settings.EnableEmergencyRuns || Settings.EnableRejectedCallouts) ResponseFeature.Start();
                    if (Settings.EnablePursuitDrafting) PursuitFeature.Start();
                    if (Settings.EnableThreatDefense) AssistFeature.Start();
                    if (Settings.EnableBackup) BackupFeature.Start();

                    // 4. Subtle Welcome Notification
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

                    Logger.Info("[Main] Cadence OffDuty transition triggered. Stopping features and purging units.");
                    SessionMetrics.LogSummary();

                    CadenceMenu.Close();
                    CadenceMenu.PurgeAllFeatures();
                    CadenceMenu.StopAllFeatures();

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
