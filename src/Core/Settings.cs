using System;
using System.IO;
using System.Windows.Forms;

namespace Cadence.Core
{
    public static class Settings
    {
        // General
        public static bool EnablePlugin { get; set; } = true;
        public static bool PluginActive
        {
            get => EnablePlugin;
            set => EnablePlugin = value;
        }
        public static bool EnableFileLogging { get; set; } = true;
        public static bool EnableDebugLogging { get; set; } = true;
        public static bool ChaosMode { get; set; } = true;
        public static string Preset { get; set; } = "Chaos";

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

        // Ambient Backup & Response Systems
        public static bool EnableBackup { get; set; } = true;
        public static Keys PanicButtonKey { get; set; } = Keys.Z;
        public static Keys PanicButtonModifierKey { get; set; } = Keys.LControlKey;
        public static Keys GroupBackupKey { get; set; } = Keys.C;
        public static Keys GroupBackupModifierKey { get; set; } = Keys.LControlKey;
        public static Keys BackupMenuKey { get; set; } = Keys.PageDown;
        public static Keys BackupMenuModifierKey { get; set; } = Keys.X;
        public static Keys DismissBackupKey { get; set; } = Keys.Enter;

        private static readonly Random _rand = new Random();
        public static int PanicButtonUnitsMin { get; set; } = 4;
        public static int PanicButtonUnitsMax { get; set; } = 7;
        public static int GroupBackupUnitsMin { get; set; } = 2;
        public static int GroupBackupUnitsMax { get; set; } = 3;

        public static int PanicButtonUnits
        {
            get
            {
                lock (_rand)
                {
                    if (PanicButtonUnitsMin >= PanicButtonUnitsMax) return PanicButtonUnitsMin;
                    return _rand.Next(PanicButtonUnitsMin, PanicButtonUnitsMax + 1);
                }
            }
            set
            {
                PanicButtonUnitsMin = value;
                PanicButtonUnitsMax = value;
            }
        }

        public static int GroupBackupUnits
        {
            get
            {
                lock (_rand)
                {
                    if (GroupBackupUnitsMin >= GroupBackupUnitsMax) return GroupBackupUnitsMin;
                    return _rand.Next(GroupBackupUnitsMin, GroupBackupUnitsMax + 1);
                }
            }
            set
            {
                GroupBackupUnitsMin = value;
                GroupBackupUnitsMax = value;
            }
        }

        public static bool GroupBackupCode3 { get; set; } = true;
        public static bool AllowAmbientCommandeer { get; set; } = true;
        public static bool AllowFallbackSpawning { get; set; } = true;

        // Limits & Caps (Default: Chaos / LA Living City)
        public static int MaxPatrolUnits { get; set; } = 8;
        public static int MaxTrafficStops { get; set; } = 4;
        public static int MaxStationaryUnits { get; set; } = 4;
        public static int MaxEmergencyRuns { get; set; } = 4;
        public static int MaxPursuitUnits { get; set; } = 6;
        public static float DespawnDistance { get; set; } = 480.0f;
        public static float SpawnDistanceMin { get; set; } = 70.0f;
        public static float SpawnDistanceMax { get; set; } = 220.0f;

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
        public static bool ShortRangeBlips { get; set; } = false;

        // Integration
        public static bool UseUltimateBackup { get; set; } = true;
        public static bool AllowFallback { get; set; } = false;
        public static bool EnableEupOutfits { get; set; } = true;
        public static bool MuteTrafficStopSiren { get; set; } = true;

        public static void ApplyPreset(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName)) return;

            string p = presetName.Trim().ToLowerInvariant();

            if (p.Contains("chaos") || p.Contains("la") || p.Contains("livingcity"))
            {
                Preset = "Chaos";
                ChaosMode = true;
                MaxPatrolUnits = 8;
                MaxTrafficStops = 4;
                MaxStationaryUnits = 4;
                MaxEmergencyRuns = 4;
                MaxPursuitUnits = 6;
                SpawnDistanceMin = 70.0f;
                SpawnDistanceMax = 220.0f;
                DespawnDistance = 480.0f;
                HighwayPatrolRatio = 50;
            }
            else if (p.Contains("metro") || p.Contains("urban") || p.Contains("downtown"))
            {
                Preset = "Metro";
                ChaosMode = true;
                MaxPatrolUnits = 6;
                MaxTrafficStops = 3;
                MaxStationaryUnits = 2;
                MaxEmergencyRuns = 3;
                MaxPursuitUnits = 5;
                SpawnDistanceMin = 75.0f;
                SpawnDistanceMax = 240.0f;
                DespawnDistance = 450.0f;
                HighwayPatrolRatio = 40;
            }
            else if (p.Contains("highway") || p.Contains("sahp") || p.Contains("freeway") || p.Contains("state"))
            {
                Preset = "Highway";
                ChaosMode = false;
                MaxPatrolUnits = 5;
                MaxTrafficStops = 3;
                MaxStationaryUnits = 5;
                MaxEmergencyRuns = 2;
                MaxPursuitUnits = 6;
                SpawnDistanceMin = 90.0f;
                SpawnDistanceMax = 300.0f;
                DespawnDistance = 600.0f;
                HighwayPatrolRatio = 80;
            }
            else if (p.Contains("rural") || p.Contains("sheriff") || p.Contains("county") || p.Contains("blaine"))
            {
                Preset = "Rural";
                ChaosMode = false;
                MaxPatrolUnits = 2;
                MaxTrafficStops = 1;
                MaxStationaryUnits = 1;
                MaxEmergencyRuns = 1;
                MaxPursuitUnits = 3;
                SpawnDistanceMin = 100.0f;
                SpawnDistanceMax = 350.0f;
                DespawnDistance = 650.0f;
                HighwayPatrolRatio = 25;
            }
            else if (p.Contains("warzone") || p.Contains("lockdown") || p.Contains("extreme"))
            {
                Preset = "Warzone";
                ChaosMode = true;
                MaxPatrolUnits = 12;
                MaxTrafficStops = 6;
                MaxStationaryUnits = 4;
                MaxEmergencyRuns = 6;
                MaxPursuitUnits = 8;
                SpawnDistanceMin = 60.0f;
                SpawnDistanceMax = 200.0f;
                DespawnDistance = 450.0f;
                HighwayPatrolRatio = 50;
            }
            else // "Realistic" or standard baseline
            {
                Preset = "Realistic";
                ChaosMode = false;
                MaxPatrolUnits = 4;
                MaxTrafficStops = 2;
                MaxStationaryUnits = 2;
                MaxEmergencyRuns = 2;
                MaxPursuitUnits = 4;
                SpawnDistanceMin = 80.0f;
                SpawnDistanceMax = 250.0f;
                DespawnDistance = 450.0f;
                HighwayPatrolRatio = 50;
            }

            Logger.Info($"[Settings] Applied preset: {Preset} (Chaos: {ChaosMode}, Patrol: {MaxPatrolUnits}, Stops: {MaxTrafficStops}, Stationary: {MaxStationaryUnits}, Emergency: {MaxEmergencyRuns}, Pursuit: {MaxPursuitUnits})");
        }

        public static void ApplyChaosPreset(bool enableChaos)
        {
            ApplyPreset(enableChaos ? "Chaos" : "Realistic");
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

                    int closeBracket = line.IndexOf(']');
                    if (line.StartsWith("[") && closeBracket > 1)
                    {
                        currentSection = line.Substring(1, closeBracket - 1).Trim();
                        continue;
                    }

                    int equalsIdx = line.IndexOf('=');
                    if (equalsIdx < 0) continue;

                    string key = line.Substring(0, equalsIdx).Trim();
                    string value = CleanIniValue(line.Substring(equalsIdx + 1));

                    ParseEntry(currentSection, key, value);
                }

                if (SpawnDistanceMin >= SpawnDistanceMax) SpawnDistanceMax = SpawnDistanceMin + 50.0f;
                if (DespawnDistance <= SpawnDistanceMax) DespawnDistance = SpawnDistanceMax + 100.0f;

                if (PanicButtonUnitsMin > PanicButtonUnitsMax) PanicButtonUnitsMax = PanicButtonUnitsMin;
                if (GroupBackupUnitsMin > GroupBackupUnitsMax) GroupBackupUnitsMax = GroupBackupUnitsMin;

                Logger.EnableFileLogging = EnableFileLogging;
                Logger.EnableDebugLogging = EnableDebugLogging;
                Logger.Info($"[Settings] Configuration loaded successfully. Debug: {EnableDebugLogging}, FileLogging: {EnableFileLogging}, MenuKey: {MenuKey}, Modifier: {MenuModifierKey}");
                Logger.Debug($"[Settings] Limits -> MaxPatrol: {MaxPatrolUnits}, MaxStops: {MaxTrafficStops}, MaxStationary: {MaxStationaryUnits}, MaxEmergency: {MaxEmergencyRuns}, MaxPursuit: {MaxPursuitUnits}, DespawnDist: {DespawnDistance:F0}m, Preset: {Preset}");
                Logger.Debug($"[Settings] Backup -> PanicUnits: {PanicButtonUnitsMin}-{PanicButtonUnitsMax}, GroupUnits: {GroupBackupUnitsMin}-{GroupBackupUnitsMax}, Commandeer: {AllowAmbientCommandeer}");
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
                    if (k == "enableplugin" || k == "pluginactive" || k == "active" || k == "activated") EnablePlugin = ParseBool(value, EnablePlugin);
                    else if (k == "enablefilelogging") EnableFileLogging = ParseBool(value, EnableFileLogging);
                    else if (k == "debug" || k == "enabledebuglogging" || k == "debuglogging") EnableDebugLogging = ParseBool(value, EnableDebugLogging);
                    else if (k == "chaosmode" || k == "chaos")
                    {
                        bool isChaos = ParseBool(value, ChaosMode);
                        ApplyChaosPreset(isChaos);
                    }
                    else if (k == "preset")
                    {
                        ApplyPreset(value);
                    }
                    break;

                case "keybinds":
                    if (k == "menukey") MenuKey = ParseKey(value, Keys.PageDown);
                    else if (k == "menumodifierkey") MenuModifierKey = ParseModifierKey(value, Keys.Q);
                    else if (k == "panicbuttonkey") PanicButtonKey = ParseKey(value, PanicButtonKey);
                    else if (k == "panicbuttonmodifierkey" || k == "panicbuttonmodifier") PanicButtonModifierKey = ParseModifierKey(value, PanicButtonModifierKey);
                    else if (k == "groupbackupkey") GroupBackupKey = ParseKey(value, GroupBackupKey);
                    else if (k == "groupbackupmodifierkey" || k == "groupbackupmodifier") GroupBackupModifierKey = ParseModifierKey(value, GroupBackupModifierKey);
                    else if (k == "backupmenukey") BackupMenuKey = ParseKey(value, BackupMenuKey);
                    else if (k == "backupmenumodifierkey" || k == "backupmenumodifier") BackupMenuModifierKey = ParseModifierKey(value, BackupMenuModifierKey);
                    else if (k == "dismissbackupkey" || k == "dismissallbackupkey") DismissBackupKey = ParseKey(value, DismissBackupKey);
                    break;

                case "backup":
                    if (k == "enablebackup") EnableBackup = ParseBool(value, EnableBackup);
                    else if (k == "panicbuttonunits" || k == "panicunits")
                    {
                        ParseRange(value, out int min, out int max, 1, 12, PanicButtonUnitsMin, PanicButtonUnitsMax);
                        PanicButtonUnitsMin = min;
                        PanicButtonUnitsMax = max;
                    }
                    else if (k == "panicbuttonunitsmin" || k == "panicunitsmin") PanicButtonUnitsMin = Math.Max(1, Math.Min(12, ParseInt(value, PanicButtonUnitsMin)));
                    else if (k == "panicbuttonunitsmax" || k == "panicunitsmax") PanicButtonUnitsMax = Math.Max(1, Math.Min(12, ParseInt(value, PanicButtonUnitsMax)));
                    else if (k == "groupbackupunits" || k == "groupunits")
                    {
                        ParseRange(value, out int min, out int max, 1, 12, GroupBackupUnitsMin, GroupBackupUnitsMax);
                        GroupBackupUnitsMin = min;
                        GroupBackupUnitsMax = max;
                    }
                    else if (k == "groupbackupunitsmin" || k == "groupunitsmin") GroupBackupUnitsMin = Math.Max(1, Math.Min(12, ParseInt(value, GroupBackupUnitsMin)));
                    else if (k == "groupbackupunitsmax" || k == "groupunitsmax") GroupBackupUnitsMax = Math.Max(1, Math.Min(12, ParseInt(value, GroupBackupUnitsMax)));
                    else if (k == "groupbackupcode3") GroupBackupCode3 = ParseBool(value, GroupBackupCode3);
                    else if (k == "allowambientcommandeer" || k == "ambientcommandeer") AllowAmbientCommandeer = ParseBool(value, AllowAmbientCommandeer);
                    else if (k == "allowfallbackspawning" || k == "fallbackspawning") AllowFallbackSpawning = ParseBool(value, AllowFallbackSpawning);
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

            if (clean.Equals("enter", StringComparison.OrdinalIgnoreCase) || clean.Equals("return", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.Enter;
            }

            if (clean.Equals("rcontrol", StringComparison.OrdinalIgnoreCase) || clean.Equals("rcontrolkey", StringComparison.OrdinalIgnoreCase) || clean.Equals("rightcontrol", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.RControlKey;
            }

            if (clean.Equals("lcontrol", StringComparison.OrdinalIgnoreCase) || clean.Equals("lcontrolkey", StringComparison.OrdinalIgnoreCase) || clean.Equals("leftcontrol", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.LControlKey;
            }

            if (clean.Equals("numpad0", StringComparison.OrdinalIgnoreCase) || clean.Equals("num0", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.NumPad0;
            }

            if (clean.Equals("numpad1", StringComparison.OrdinalIgnoreCase) || clean.Equals("num1", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.NumPad1;
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
                clean.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.None;
            }

            if (clean.Equals("ctrl", StringComparison.OrdinalIgnoreCase)) clean = "Control";
            if (clean.Equals("alt", StringComparison.OrdinalIgnoreCase)) clean = "Menu";
            if (clean.Equals("rcontrol", StringComparison.OrdinalIgnoreCase) || clean.Equals("rcontrolkey", StringComparison.OrdinalIgnoreCase) || clean.Equals("rctrl", StringComparison.OrdinalIgnoreCase) || clean.Equals("rightcontrol", StringComparison.OrdinalIgnoreCase)) return Keys.RControlKey;
            if (clean.Equals("lcontrol", StringComparison.OrdinalIgnoreCase) || clean.Equals("lcontrolkey", StringComparison.OrdinalIgnoreCase) || clean.Equals("lctrl", StringComparison.OrdinalIgnoreCase) || clean.Equals("leftcontrol", StringComparison.OrdinalIgnoreCase)) return Keys.LControlKey;
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
            if (string.IsNullOrWhiteSpace(value)) return defaultVal;
            string normalized = value.Replace(',', '.');
            return float.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float res) ? res : defaultVal;
        }

        private static void ParseRange(string value, out int min, out int max, int clampMin, int clampMax, int defaultMin, int defaultMax)
        {
            min = defaultMin;
            max = defaultMax;
            if (string.IsNullOrWhiteSpace(value)) return;

            string clean = value.Trim();
            string[] parts = clean.Split(new[] { '-', ',', ' ', '/', 't', 'o', 'T', 'O', '.' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 && int.TryParse(parts[0], out int p1) && int.TryParse(parts[parts.Length - 1], out int p2))
            {
                min = Math.Min(p1, p2);
                max = Math.Max(p1, p2);
            }
            else if (int.TryParse(clean, out int single))
            {
                min = single;
                max = single;
            }

            min = Math.Max(clampMin, Math.Min(clampMax, min));
            max = Math.Max(clampMin, Math.Min(clampMax, max));
            if (min > max)
            {
                int tmp = min;
                min = max;
                max = tmp;
            }
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
