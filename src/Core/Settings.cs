using System;
using System.IO;
using System.Windows.Forms;

namespace Cadence.Core
{
    public static class Settings
    {
        // General
        public static bool EnablePlugin { get; set; } = true;
        public static bool EnableFileLogging { get; set; } = true;
        public static bool EnableDebugLogging { get; set; } = true;
        public static bool ChaosMode { get; set; } = false;
        public static string Preset { get; set; } = "Realistic";

        // Keybinds (with 0/None fallback)
        public static Keys MenuKey { get; set; } = Keys.PageDown;
        public static Keys MenuModifierKey { get; set; } = Keys.Q;

        // Features
        public static bool EnablePatrol { get; set; } = true;
        public static bool EnableTrafficStops { get; set; } = true;
        public static bool EnableStationary { get; set; } = true;
        public static bool EnableEmergencyRuns { get; set; } = true;
        public static bool EnableRejectedCallouts { get; set; } = true;
        public static bool EnableCalloutTakeover
        {
            get => EnableRejectedCallouts;
            set => EnableRejectedCallouts = value;
        }
        public static bool EnablePursuitDrafting { get; set; } = true;
        public static bool EnablePursuitAssist
        {
            get => EnablePursuitDrafting;
            set => EnablePursuitDrafting = value;
        }
        public static bool EnableThreatDefense { get; set; } = true;
        public static bool EnableThreatAssist
        {
            get => EnableThreatDefense;
            set => EnableThreatDefense = value;
        }

        // Limits & Caps
        public static int MaxPatrolUnits { get; set; } = 4;
        public static int MaxTrafficStops { get; set; } = 2;
        public static int MaxStationaryUnits { get; set; } = 2;
        public static int MaxEmergencyRuns { get; set; } = 2;
        public static int MaxPursuitUnits { get; set; } = 4;
        public static float DespawnDistance { get; set; } = 450.0f;
        public static float SpawnDistanceMin { get; set; } = 80.0f;
        public static float SpawnDistanceMax { get; set; } = 250.0f;

        // Emergency Services Variety
        public static bool EnableEmergencyVariety { get; set; } = true;
        public static bool AllowPoliceRuns { get; set; } = true;
        public static bool AllowFireRuns { get; set; } = true;
        public static bool AllowAmbulanceRuns { get; set; } = true;

        // Jurisdiction Balance (0 - 100, 50 = Balanced, 0 = Only Local, 100 = Only Highway)
        public static int HighwayPatrolRatio { get; set; } = 50;

        // Radar
        public static bool ShowRadarBlips { get; set; } = true;
        public static float BlipScale { get; set; } = 0.45f;
        public static bool ShortRangeBlips { get; set; } = true;

        // Integration
        public static bool UseUltimateBackup { get; set; } = true;
        public static bool AllowFallback { get; set; } = false;
        public static bool EnableEupOutfits { get; set; } = true;
        public static bool MuteTrafficStopSiren { get; set; } = true;

        public static void ApplyChaosPreset(bool enableChaos)
        {
            ChaosMode = enableChaos;
            Preset = enableChaos ? "Chaos" : "Realistic";
            if (enableChaos)
            {
                MaxPatrolUnits = 8;
                MaxTrafficStops = 4;
                MaxStationaryUnits = 4;
                MaxEmergencyRuns = 4;
            }
            else
            {
                MaxPatrolUnits = 4;
                MaxTrafficStops = 2;
                MaxStationaryUnits = 2;
                MaxEmergencyRuns = 2;
            }
            Logger.Info($"[Settings] Applied preset: {Preset} (Patrol: {MaxPatrolUnits}, Stops: {MaxTrafficStops}, Stationary: {MaxStationaryUnits}, Emergency: {MaxEmergencyRuns})");
        }

        public static string ResolvedIniPath { get; private set; }

        public static void Load(string customPath = null)
        {
            string iniPath = ResolveIniPath(customPath);
            ResolvedIniPath = iniPath;

            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath))
            {
                Logger.Warn($"Cadence.ini not found. Using default internal settings. Checked: {iniPath}");
                return;
            }

            Logger.Info($"Loading configuration from: {iniPath}");

            try
            {
                string currentSection = string.Empty;
                string[] lines = File.ReadAllLines(iniPath);

                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("//"))
                    {
                        continue;
                    }

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }

                    int equalsIdx = line.IndexOf('=');
                    if (equalsIdx < 0) continue;

                    string key = line.Substring(0, equalsIdx).Trim();
                    string value = CleanIniValue(line.Substring(equalsIdx + 1));

                    ParseEntry(currentSection, key, value);
                }

                Logger.EnableFileLogging = EnableFileLogging;
                Logger.EnableDebugLogging = EnableDebugLogging;
                Logger.Info($"[Settings] Configuration loaded successfully. Debug: {EnableDebugLogging}, FileLogging: {EnableFileLogging}, MenuKey: {MenuKey}, Modifier: {MenuModifierKey}");
                Logger.Debug($"[Settings] Limits -> MaxPatrol: {MaxPatrolUnits}, MaxStops: {MaxTrafficStops}, MaxStationary: {MaxStationaryUnits}, MaxEmergency: {MaxEmergencyRuns}, MaxPursuit: {MaxPursuitUnits}, DespawnDist: {DespawnDistance:F0}m, Preset: {Preset}");
                Logger.Debug($"[Settings] Integration -> UltimateBackup: {UseUltimateBackup}, Fallback: {AllowFallback}, Variety: {EnableEmergencyVariety}, HwyRatio: {HighwayPatrolRatio}%, EUP: {EnableEupOutfits}, MuteSiren: {MuteTrafficStopSiren}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading settings from {iniPath}", ex);
            }
        }

        private static void ParseEntry(string section, string key, string value)
        {
            string sec = section.ToLowerInvariant();
            string k = key.ToLowerInvariant();

            switch (sec)
            {
                case "general":
                    if (k == "enableplugin") EnablePlugin = ParseBool(value, EnablePlugin);
                    else if (k == "enablefilelogging") EnableFileLogging = ParseBool(value, EnableFileLogging);
                    else if (k == "debug" || k == "enabledebuglogging" || k == "debuglogging") EnableDebugLogging = ParseBool(value, EnableDebugLogging);
                    else if (k == "chaosmode" || k == "chaos")
                    {
                        bool isChaos = ParseBool(value, ChaosMode);
                        ApplyChaosPreset(isChaos);
                    }
                    else if (k == "preset")
                    {
                        if (value.IndexOf("chaos", StringComparison.OrdinalIgnoreCase) >= 0) ApplyChaosPreset(true);
                        else ApplyChaosPreset(false);
                    }
                    break;

                case "keybinds":
                    if (k == "menukey") MenuKey = ParseKey(value, Keys.PageDown);
                    else if (k == "menumodifierkey") MenuModifierKey = ParseModifierKey(value, Keys.Q);
                    break;

                case "features":
                    if (k == "enablepatrol") EnablePatrol = ParseBool(value, EnablePatrol);
                    else if (k == "enabletrafficstops") EnableTrafficStops = ParseBool(value, EnableTrafficStops);
                    else if (k == "enablestationary") EnableStationary = ParseBool(value, EnableStationary);
                    else if (k == "enableemergencyruns") EnableEmergencyRuns = ParseBool(value, EnableEmergencyRuns);
                    else if (k == "enablerejectedcallouts" || k == "enablecallouttakeover") EnableRejectedCallouts = ParseBool(value, EnableRejectedCallouts);
                    else if (k == "enablepursuitdrafting" || k == "enablepursuitassist") EnablePursuitDrafting = ParseBool(value, EnablePursuitDrafting);
                    else if (k == "enablethreatdefense" || k == "enablethreatassist") EnableThreatDefense = ParseBool(value, EnableThreatDefense);
                    break;

                case "limits":
                    if (k == "maxpatrolunits") MaxPatrolUnits = Math.Max(1, Math.Min(16, ParseInt(value, MaxPatrolUnits)));
                    else if (k == "maxtrafficstops") MaxTrafficStops = Math.Max(0, Math.Min(8, ParseInt(value, MaxTrafficStops)));
                    else if (k == "maxstationaryunits") MaxStationaryUnits = Math.Max(0, Math.Min(8, ParseInt(value, MaxStationaryUnits)));
                    else if (k == "maxemergencyruns" || k == "maxemergency") MaxEmergencyRuns = Math.Max(0, Math.Min(8, ParseInt(value, MaxEmergencyRuns)));
                    else if (k == "maxpursuitunits") MaxPursuitUnits = Math.Max(1, Math.Min(12, ParseInt(value, MaxPursuitUnits)));
                    else if (k == "despawndistance") DespawnDistance = Math.Max(150.0f, Math.Min(1500.0f, ParseFloat(value, DespawnDistance)));
                    else if (k == "spawndistancemin") SpawnDistanceMin = Math.Max(30.0f, Math.Min(300.0f, ParseFloat(value, SpawnDistanceMin)));
                    else if (k == "spawndistancemax") SpawnDistanceMax = Math.Max(100.0f, Math.Min(600.0f, ParseFloat(value, SpawnDistanceMax)));
                    break;

                case "emergencyservices":
                case "emergency":
                    if (k == "enableemergencyvariety" || k == "enableemergencyrunvariety") EnableEmergencyVariety = ParseBool(value, EnableEmergencyVariety);
                    else if (k == "allowpoliceruns" || k == "allowpolice") AllowPoliceRuns = ParseBool(value, AllowPoliceRuns);
                    else if (k == "allowfireruns" || k == "allowfire") AllowFireRuns = ParseBool(value, AllowFireRuns);
                    else if (k == "allowambulanceruns" || k == "allowambulance" || k == "allowemsruns") AllowAmbulanceRuns = ParseBool(value, AllowAmbulanceRuns);
                    break;

                case "jurisdiction":
                    if (k == "highwaypatrolratio" || k == "highwayratio" || k == "statepatrolratio") HighwayPatrolRatio = Math.Max(0, Math.Min(100, ParseInt(value, HighwayPatrolRatio)));
                    break;

                case "radar":
                    if (k == "showradarblips") ShowRadarBlips = ParseBool(value, ShowRadarBlips);
                    else if (k == "blipscale") BlipScale = Math.Max(0.2f, Math.Min(1.5f, ParseFloat(value, BlipScale)));
                    else if (k == "shortrangeblips") ShortRangeBlips = ParseBool(value, ShortRangeBlips);
                    break;

                case "integration":
                    if (k == "useultimatebackup" || k == "ultimatebackup") UseUltimateBackup = ParseBool(value, UseUltimateBackup);
                    else if (k == "fallback" || k == "allowfallback") AllowFallback = ParseBool(value, AllowFallback);
                    else if (k == "enableeupoutfits") EnableEupOutfits = ParseBool(value, EnableEupOutfits);
                    else if (k == "mutetrafficstopsiren") MuteTrafficStopSiren = ParseBool(value, MuteTrafficStopSiren);
                    break;
            }
        }

        public static Keys ParseKey(string value, Keys defaultKey)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultKey;

            string clean = value.Trim();

            // 0 or None fallback requirement: completely disables keybind
            if (clean.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.None;
            }

            if (clean.Equals("pagedown", StringComparison.OrdinalIgnoreCase) || clean.Equals("pgdn", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.PageDown;
            }

            if (clean.Equals("pageup", StringComparison.OrdinalIgnoreCase) || clean.Equals("pgup", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.PageUp;
            }

            if (Enum.TryParse(clean, true, out Keys parsedKey))
            {
                return parsedKey;
            }

            return defaultKey;
        }

        private static Keys ParseModifierKey(string value, Keys defaultModifier)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultModifier;

            string clean = value.Trim();
            if (clean.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.None;
            }

            if (clean.Equals("ctrl", StringComparison.OrdinalIgnoreCase)) clean = "Control";
            if (clean.Equals("alt", StringComparison.OrdinalIgnoreCase)) clean = "Menu";
            if (clean.Equals("pagedown", StringComparison.OrdinalIgnoreCase) || clean.Equals("pgdn", StringComparison.OrdinalIgnoreCase)) return Keys.PageDown;
            if (clean.Equals("pageup", StringComparison.OrdinalIgnoreCase) || clean.Equals("pgup", StringComparison.OrdinalIgnoreCase)) return Keys.PageUp;

            if (Enum.TryParse(clean, true, out Keys parsedMod))
            {
                return parsedMod;
            }

            return defaultModifier;
        }

        private static bool ParseBool(string value, bool defaultVal)
        {
            if (bool.TryParse(value, out bool res)) return res;
            if (value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("0", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultVal;
        }

        private static int ParseInt(string value, int defaultVal)
        {
            return int.TryParse(value, out int res) ? res : defaultVal;
        }

        private static float ParseFloat(string value, float defaultVal)
        {
            return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float res) ? res : defaultVal;
        }

        private static string CleanIniValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return string.Empty;
            int commentIdx = val.IndexOf(';');
            if (commentIdx >= 0) val = val.Substring(0, commentIdx);
            int hashIdx = val.IndexOf('#');
            if (hashIdx >= 0) val = val.Substring(0, hashIdx);
            int slashIdx = val.IndexOf("//", StringComparison.Ordinal);
            if (slashIdx >= 0) val = val.Substring(0, slashIdx);
            return val.Trim();
        }

        private static string ResolveIniPath(string customPath)
        {
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            {
                return customPath;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. Primary standard LSPDFR location: plugins/LSPDFR/Cadence.ini
            string lspdfrPath = Path.Combine(baseDir, "plugins", "LSPDFR", "Cadence.ini");
            if (File.Exists(lspdfrPath)) return lspdfrPath;

            // 2. Root game directory: Cadence.ini
            string rootPath = Path.Combine(baseDir, "Cadence.ini");
            if (File.Exists(rootPath)) return rootPath;

            // 3. Fallback relative to current working directory
            string relLspdfr = Path.Combine("plugins", "LSPDFR", "Cadence.ini");
            if (File.Exists(relLspdfr)) return relLspdfr;

            if (File.Exists("Cadence.ini")) return "Cadence.ini";

            return lspdfrPath;
        }
    }
}
