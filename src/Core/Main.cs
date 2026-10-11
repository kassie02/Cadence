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

                Logger.Info("[Main] Cadence hook registered successfully.");
            }
            catch (Exception ex)
            {
                Logger.Error("[Main] Fatal exception during Main.Initialize()", ex);
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
                catch { }

                _isOnDuty = false;
                CadenceMenu.Stop();
                CadenceMenu.StopAllFeatures();
                CadenceMenu.PurgeAllFeatures();

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

                    // 2. Initialize Ultimate Backup Integration
                    UltimateBackupService.Initialize();

                    // 3. Initialize In-Game Menu
                    CadenceMenu.Initialize();

                    // 3. Bootstrap Enabled Features (Thin Ponytail Strands)
                    if (Settings.EnablePatrol) PatrolFeature.Start();
                    if (Settings.EnableTrafficStops) TrafficStopFeature.Start();
                    if (Settings.EnableStationary) StationaryFeature.Start();
                    if (Settings.EnableEmergencyRuns || Settings.EnableRejectedCallouts) ResponseFeature.Start();
                    if (Settings.EnablePursuitDrafting) PursuitFeature.Start();
                    if (Settings.EnableThreatDefense) AssistFeature.Start();

                    // 4. Subtle Welcome Notification
                    string keyNotice = Settings.MenuKey == Keys.None
                        ? "Menu keybind disabled."
                        : $"Press ~y~{Settings.MenuKey}~s~ for menu.";
                    Game.DisplayNotification($"~b~Cadence~s~ loaded successfully. {keyNotice}");

                    Logger.Info($"[Main] Cadence OnDuty transition complete. MenuKey: {Settings.MenuKey}");
                }
                else
                {
                    if (!_isOnDuty) return;
                    _isOnDuty = false;

                    Logger.Info("[Main] Cadence OffDuty transition triggered. Stopping features and purging units.");

                    CadenceMenu.Close();
                    CadenceMenu.StopAllFeatures();
                    CadenceMenu.PurgeAllFeatures();

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
