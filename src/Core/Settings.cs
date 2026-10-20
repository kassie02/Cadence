using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Cadence.Core
{
    public static class Settings
    {
        private static readonly object _saveLock = new object();

        // General
        public static bool EnablePlugin { get; set; } = true;
        public static bool PluginActive
        {
            get => EnablePlugin;
            set => EnablePlugin = value;
        }
        public static bool EnableFileLogging { get; set; } = true;
        public static bool EnableDebugLogging { get; set; } = false;
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

        // Reconciled fallback flags [A8, D15, E23, F14]: AllowFallbackSpawning maps to AllowFallback
        public static bool AllowFallback { get; set; } = false;
        public static bool AllowFallbackSpawning
        {
            get => AllowFallback;
            set => AllowFallback = value;
        }

        // Limits & Caps (Default: Realistic production baseline)
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
        public static bool ShortRangeBlips { get; set; } = false;
        public static bool EnablePlayerVehicleBlip { get; set; } = true;
        public static int PlayerVehicleBlipColor { get; set; } = 27; // Purple
        public static float PlayerVehicleBlipScale { get; set; } = 0.38f; // Compact: smaller than AI (0.45f) and much smaller than LSPDFR (1.0f)
        public static int PlayerVehicleBlipSprite { get; set; } = 56; // PoliceCar icon
        public static string PlayerVehicleBlipName { get; set; } = "Personal Cruiser";

        public static int PatrolBlipColor { get; set; } = 38;
        public static float PatrolBlipScale { get; set; } = 0.45f;
        public static int PatrolBlipSprite { get; set; } = 56;

        public static int TrafficStopBlipColor { get; set; } = 4;
        public static float TrafficStopBlipScale { get; set; } = 0.45f;
        public static int TrafficStopBlipSprite { get; set; } = 56;

        public static int StationaryBlipColor { get; set; } = 2;
        public static float StationaryBlipScale { get; set; } = 0.45f;
        public static int StationaryBlipSprite { get; set; } = 56;

        public static int EmergencyBlipColor { get; set; } = 1;
        public static float EmergencyBlipScale { get; set; } = 0.45f;
        public static int EmergencyBlipSprite { get; set; } = 56;

        public static int EmsBlipColor { get; set; } = 1;
        public static float EmsBlipScale { get; set; } = 0.45f;
        public static int EmsBlipSprite { get; set; } = 61;

        public static int FireBlipColor { get; set; } = 1;
        public static float FireBlipScale { get; set; } = 0.45f;
        public static int FireBlipSprite { get; set; } = 60;

        public static int PursuitBlipColor { get; set; } = 38;
        public static float PursuitBlipScale { get; set; } = 0.45f;
        public static int PursuitBlipSprite { get; set; } = 56;

        public static int CalloutResponseBlipColor { get; set; } = 27;
        public static float CalloutResponseBlipScale { get; set; } = 0.45f;
        public static int CalloutResponseBlipSprite { get; set; } = 56;

        public static int CombatBlipColor { get; set; } = 49;
        public static float CombatBlipScale { get; set; } = 0.45f;
        public static int CombatBlipSprite { get; set; } = 56;

        public static int OfficerFootBlipColor { get; set; } = 38;
        public static float OfficerFootBlipScale { get; set; } = 0.40f;
        public static int OfficerFootBlipSprite { get; set; } = 58;

        public static int ParkedCruiserBlipColor { get; set; } = 0;
        public static float ParkedCruiserBlipScale { get; set; } = 0.45f;
        public static int ParkedCruiserBlipSprite { get; set; } = 56;

        public static int GroupBackupBlipColor { get; set; } = 4; // Yellow
        public static float GroupBackupBlipScale { get; set; } = 0.45f;
        public static int GroupBackupBlipSprite { get; set; } = 56;

        public static int GroupOfficerFootBlipColor { get; set; } = 4; // Yellow
        public static float GroupOfficerFootBlipScale { get; set; } = 0.40f;
        public static int GroupOfficerFootBlipSprite { get; set; } = 58;

        public static int BackupResponseBlipColor { get; set; } = 18; // Cyan / Light Blue
        public static float BackupResponseBlipScale { get; set; } = 0.45f;
        public static int BackupResponseBlipSprite { get; set; } = 56;

        public static int SuspectBlipColor { get; set; } = 1;
        public static float SuspectBlipScale { get; set; } = 0.45f;
        public static int SuspectBlipSprite { get; set; } = 1;

        // Emergency Run Fine-Grained Customization
        public static int EmergencyRunPoliceWeight { get; set; } = 50;
        public static int EmergencyRunFireWeight { get; set; } = 25;
        public static int EmergencyRunAmbulanceWeight { get; set; } = 25;
        public static int MaxPoliceRuns { get; set; } = 2;
        public static int MaxFireRuns { get; set; } = 1;
        public static int MaxAmbulanceRuns { get; set; } = 1;

        // Integration
        public static bool UseUltimateBackup { get; set; } = true;
        public static bool EnableEupOutfits { get; set; } = true;
        public static bool MuteTrafficStopSiren { get; set; } = true;
        public static bool RequireLightsForSiren { get; set; } = true;
        public static bool EnableSirenToneVariation { get; set; } = true;

        /// <summary>
        /// Validates relational distance invariants per CODING_STANDARDS.md [A9]:
        /// SpawnDistanceMin < SpawnDistanceMax < DespawnDistance with +50m minimum margins.
        /// Rejects NaN and non-finite values.
        /// </summary>
        public static void ValidateDistances()
        {
            if (float.IsNaN(SpawnDistanceMin) || float.IsInfinity(SpawnDistanceMin) || SpawnDistanceMin < 10.0f)
            {
                SpawnDistanceMin = 70.0f;
            }

            if (float.IsNaN(SpawnDistanceMax) || float.IsInfinity(SpawnDistanceMax) || SpawnDistanceMax < SpawnDistanceMin + 50.0f)
            {
                SpawnDistanceMax = SpawnDistanceMin + 50.0f;
            }

            if (float.IsNaN(DespawnDistance) || float.IsInfinity(DespawnDistance) || DespawnDistance < SpawnDistanceMax + 50.0f)
            {
                DespawnDistance = SpawnDistanceMax + 50.0f;
            }
        }

        public static void ApplyPreset(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName)) return;

            string p = presetName.Trim().ToLowerInvariant();

            if (p.StartsWith("6") || p == "6" || p.Contains("warzone") || p.Contains("lockdown"))
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
            else if (p.StartsWith("5") || p == "5" || p.Contains("chaos") || p.Contains("metropolis") || p.Contains("livingcity"))
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
            else if (p.StartsWith("4") || p == "4" || p.Contains("highway") || p.Contains("interdiction") || p.Contains("sahp"))
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
            else if (p.StartsWith("3") || p == "3" || p.Contains("metro") || p.Contains("downtown"))
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
            else if (p.StartsWith("1") || p == "1" || p.Contains("rural") || p.Contains("blaine"))
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
            else // "Realistic", "2", "2 - authentic realistic (balanced)", or standard baseline
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

            ValidateDistances();
            Logger.Info($"[Settings] Applied preset: {Preset} (Chaos: {ChaosMode}, Patrol: {MaxPatrolUnits}, Stops: {MaxTrafficStops}, Stationary: {MaxStationaryUnits}, Emergency: {MaxEmergencyRuns}, Pursuit: {MaxPursuitUnits})");
        }

        public static void ApplyChaosPreset(bool enableChaos)
        {
            ApplyPreset(enableChaos ? "Chaos" : "Realistic");
        }

        public static string ResolvedIniPath { get; private set; }

        /// <summary>
        /// Resets all settings to their baseline defaults [E28] before loading or reloading.
        /// Prevents stale values from surviving when keys are removed from the INI file.
        /// </summary>
        public static void ResetToDefaults()
        {
            EnablePlugin = true;
            EnableFileLogging = true;
            EnableDebugLogging = false;
            ChaosMode = false;
            Preset = "Realistic";

            MenuKey = Keys.PageDown;
            MenuModifierKey = Keys.Q;

            EnablePatrol = true;
            EnableTrafficStops = true;
            EnableStationary = true;
            EnableEmergencyRuns = true;
            EnableRejectedCallouts = true;
            EnablePursuitDrafting = true;
            EnableThreatDefense = true;

            EnableBackup = true;
            PanicButtonKey = Keys.Z;
            PanicButtonModifierKey = Keys.LControlKey;
            GroupBackupKey = Keys.C;
            GroupBackupModifierKey = Keys.LControlKey;
            BackupMenuKey = Keys.PageDown;
            BackupMenuModifierKey = Keys.X;
            DismissBackupKey = Keys.Enter;

            PanicButtonUnitsMin = 4;
            PanicButtonUnitsMax = 7;
            GroupBackupUnitsMin = 2;
            GroupBackupUnitsMax = 3;
            GroupBackupCode3 = true;
            AllowAmbientCommandeer = true;
            AllowFallback = false;

            MaxPatrolUnits = 4;
            MaxTrafficStops = 2;
            MaxStationaryUnits = 2;
            MaxEmergencyRuns = 2;
            MaxPursuitUnits = 4;
            DespawnDistance = 450.0f;
            SpawnDistanceMin = 80.0f;
            SpawnDistanceMax = 250.0f;

            EnableEmergencyVariety = true;
            AllowPoliceRuns = true;
            AllowFireRuns = true;
            AllowAmbulanceRuns = true;

            HighwayPatrolRatio = 50;

            ShowRadarBlips = true;
            BlipScale = 0.45f;
            ShortRangeBlips = false;
            EnablePlayerVehicleBlip = true;
            PlayerVehicleBlipColor = 27;
            PlayerVehicleBlipScale = 0.38f;
            PlayerVehicleBlipSprite = 56;
            PlayerVehicleBlipName = "Personal Cruiser";

            PatrolBlipColor = 38;
            PatrolBlipScale = 0.45f;
            PatrolBlipSprite = 56;

            TrafficStopBlipColor = 4;
            TrafficStopBlipScale = 0.45f;
            TrafficStopBlipSprite = 56;

            StationaryBlipColor = 2;
            StationaryBlipScale = 0.45f;
            StationaryBlipSprite = 56;

            EmergencyBlipColor = 1;
            EmergencyBlipScale = 0.45f;
            EmergencyBlipSprite = 56;

            EmsBlipColor = 1;
            EmsBlipScale = 0.45f;
            EmsBlipSprite = 61;

            FireBlipColor = 1;
            FireBlipScale = 0.45f;
            FireBlipSprite = 60;

            PursuitBlipColor = 38;
            PursuitBlipScale = 0.45f;
            PursuitBlipSprite = 56;

            CalloutResponseBlipColor = 27;
            CalloutResponseBlipScale = 0.45f;
            CalloutResponseBlipSprite = 56;

            CombatBlipColor = 49;
            CombatBlipScale = 0.45f;
            CombatBlipSprite = 56;

            OfficerFootBlipColor = 38;
            OfficerFootBlipScale = 0.40f;
            OfficerFootBlipSprite = 58;

            ParkedCruiserBlipColor = 0;
            ParkedCruiserBlipScale = 0.45f;
            ParkedCruiserBlipSprite = 56;

            GroupBackupBlipColor = 4;
            GroupBackupBlipScale = 0.45f;
            GroupBackupBlipSprite = 56;

            GroupOfficerFootBlipColor = 4;
            GroupOfficerFootBlipScale = 0.40f;
            GroupOfficerFootBlipSprite = 58;

            BackupResponseBlipColor = 18;
            BackupResponseBlipScale = 0.45f;
            BackupResponseBlipSprite = 56;

            SuspectBlipColor = 1;
            SuspectBlipScale = 0.45f;
            SuspectBlipSprite = 1;

            EmergencyRunPoliceWeight = 50;
            EmergencyRunFireWeight = 25;
            EmergencyRunAmbulanceWeight = 25;
            MaxPoliceRuns = 2;
            MaxFireRuns = 1;
            MaxAmbulanceRuns = 1;

            UseUltimateBackup = true;
            EnableEupOutfits = true;
            MuteTrafficStopSiren = true;
            RequireLightsForSiren = true;
            EnableSirenToneVariation = true;
        }

        public static void Load(string customPath = null)
        {
            ResetToDefaults(); // [E28] Reset to defaults before parsing

            string iniPath = ResolveIniPath(customPath);
            ResolvedIniPath = iniPath;

            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath))
            {
                ValidateDistances();
                Logger.Warn($"Cadence.ini not found. Using default internal settings. Checked: {iniPath}");
                return;
            }

            Logger.Info($"Loading configuration from: {iniPath}");

            var unknownEntries = new List<string>();

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

                    bool recognized = ParseEntry(currentSection, key, value);
                    if (!recognized)
                    {
                        unknownEntries.Add($"[{currentSection}] {key}={value}");
                    }
                }

                ValidateDistances(); // [A9] Strict invariant verification

                if (PanicButtonUnitsMin > PanicButtonUnitsMax) PanicButtonUnitsMax = PanicButtonUnitsMin;
                if (GroupBackupUnitsMin > GroupBackupUnitsMax) GroupBackupUnitsMax = GroupBackupUnitsMin;

                // [E27] Apply logger settings
                Logger.EnableFileLogging = EnableFileLogging;
                Logger.EnableDebugLogging = EnableDebugLogging;

                Logger.Info($"[Settings] Configuration loaded successfully. Debug: {EnableDebugLogging}, FileLogging: {EnableFileLogging}, MenuKey: {MenuKey}, Modifier: {MenuModifierKey}");
                Logger.Debug($"[Settings] Limits -> MaxPatrol: {MaxPatrolUnits}, MaxStops: {MaxTrafficStops}, MaxStationary: {MaxStationaryUnits}, MaxEmergency: {MaxEmergencyRuns}, MaxPursuit: {MaxPursuitUnits}, DespawnDist: {DespawnDistance:F0}m, Preset: {Preset}");
                Logger.Debug($"[Settings] Backup -> PanicUnits: {PanicButtonUnitsMin}-{PanicButtonUnitsMax}, GroupUnits: {GroupBackupUnitsMin}-{GroupBackupUnitsMax}, Commandeer: {AllowAmbientCommandeer}");
                Logger.Debug($"[Settings] Integration -> UltimateBackup: {UseUltimateBackup}, Fallback: {AllowFallback}, Variety: {EnableEmergencyVariety}, HwyRatio: {HighwayPatrolRatio}%, EUP: {EnableEupOutfits}, MuteSiren: {MuteTrafficStopSiren}");

                // [F15] Track and report unknown/rejected keys in a single warning
                if (unknownEntries.Count > 0)
                {
                    Logger.Warn($"[Settings] {unknownEntries.Count} unknown or deprecated configuration entries detected in {iniPath}: {string.Join(", ", unknownEntries)}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading settings from {iniPath}", ex);
                ValidateDistances();
            }
        }

        /// <summary>
        /// Thread-safe persistence method writing back all configuration settings to Cadence.ini [E3].
        /// </summary>
        public static void Save(string customPath = null)
        {
            lock (_saveLock)
            {
                try
                {
                    string iniPath = customPath ?? ResolvedIniPath ?? ResolveIniPath(null);
                    if (string.IsNullOrEmpty(iniPath))
                    {
                        iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "LSPDFR", "Cadence.ini");
                    }

                    string dir = Path.GetDirectoryName(iniPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    ValidateDistances();

                    var sb = new StringBuilder();
                    sb.AppendLine("// ==============================================================================");
                    sb.AppendLine("// Cadence - Ambient Policing System for LSPDFR");
                    sb.AppendLine("// Complete Configuration File");
                    sb.AppendLine("// ==============================================================================");
                    sb.AppendLine();

                    // [General]
                    sb.AppendLine("[General]");
                    sb.AppendLine($"EnablePlugin = {EnablePlugin.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"PluginActive = {EnablePlugin.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnableFileLogging = {EnableFileLogging.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"Debug = {EnableDebugLogging.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"Preset = {Preset}");
                    sb.AppendLine($"ChaosMode = {ChaosMode.ToString().ToLowerInvariant()}");
                    sb.AppendLine();

                    // [Keybinds]
                    sb.AppendLine("[Keybinds]");
                    sb.AppendLine($"MenuKey = {MenuKey}");
                    sb.AppendLine($"MenuModifierKey = {MenuModifierKey}");
                    sb.AppendLine($"PanicButtonKey = {PanicButtonKey}");
                    sb.AppendLine($"PanicButtonModifierKey = {PanicButtonModifierKey}");
                    sb.AppendLine($"GroupBackupKey = {GroupBackupKey}");
                    sb.AppendLine($"GroupBackupModifierKey = {GroupBackupModifierKey}");
                    sb.AppendLine($"BackupMenuKey = {BackupMenuKey}");
                    sb.AppendLine($"BackupMenuModifierKey = {BackupMenuModifierKey}");
                    sb.AppendLine($"DismissBackupKey = {DismissBackupKey}");
                    sb.AppendLine();

                    // [Features]
                    sb.AppendLine("[Features]");
                    sb.AppendLine($"EnablePatrol = {EnablePatrol.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnableTrafficStops = {EnableTrafficStops.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnableStationary = {EnableStationary.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnableEmergencyRuns = {EnableEmergencyRuns.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnableRejectedCallouts = {EnableRejectedCallouts.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnablePursuitDrafting = {EnablePursuitDrafting.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnableThreatDefense = {EnableThreatDefense.ToString().ToLowerInvariant()}");
                    sb.AppendLine();

                    // [Limits]
                    sb.AppendLine("[Limits]");
                    sb.AppendLine($"MaxPatrolUnits = {MaxPatrolUnits}");
                    sb.AppendLine($"MaxTrafficStops = {MaxTrafficStops}");
                    sb.AppendLine($"MaxStationaryUnits = {MaxStationaryUnits}");
                    sb.AppendLine($"MaxEmergencyRuns = {MaxEmergencyRuns}");
                    sb.AppendLine($"MaxPursuitUnits = {MaxPursuitUnits}");
                    sb.AppendLine($"DespawnDistance = {DespawnDistance:F1}");
                    sb.AppendLine($"SpawnDistanceMin = {SpawnDistanceMin:F1}");
                    sb.AppendLine($"SpawnDistanceMax = {SpawnDistanceMax:F1}");
                    sb.AppendLine();

                    // [EmergencyServices]
                    sb.AppendLine("[EmergencyServices]");
                    sb.AppendLine($"EnableEmergencyVariety = {EnableEmergencyVariety.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"AllowPoliceRuns = {AllowPoliceRuns.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"AllowFireRuns = {AllowFireRuns.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"AllowAmbulanceRuns = {AllowAmbulanceRuns.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EmergencyRunPoliceWeight = {EmergencyRunPoliceWeight}");
                    sb.AppendLine($"EmergencyRunFireWeight = {EmergencyRunFireWeight}");
                    sb.AppendLine($"EmergencyRunAmbulanceWeight = {EmergencyRunAmbulanceWeight}");
                    sb.AppendLine($"MaxPoliceRuns = {MaxPoliceRuns}");
                    sb.AppendLine($"MaxFireRuns = {MaxFireRuns}");
                    sb.AppendLine($"MaxAmbulanceRuns = {MaxAmbulanceRuns}");
                    sb.AppendLine();

                    // [Jurisdiction]
                    sb.AppendLine("[Jurisdiction]");
                    sb.AppendLine($"HighwayPatrolRatio = {HighwayPatrolRatio}");
                    sb.AppendLine();

                    // [Radar]
                    sb.AppendLine("[Radar]");
                    sb.AppendLine($"ShowRadarBlips = {ShowRadarBlips.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"BlipScale = {BlipScale:F2}");
                    sb.AppendLine($"ShortRangeBlips = {ShortRangeBlips.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnablePlayerVehicleBlip = {EnablePlayerVehicleBlip.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"PlayerVehicleBlipColor = {PlayerVehicleBlipColor}");
                    sb.AppendLine($"PlayerVehicleBlipScale = {PlayerVehicleBlipScale:F2}");
                    sb.AppendLine($"PlayerVehicleBlipSprite = {PlayerVehicleBlipSprite}");
                    sb.AppendLine($"PlayerVehicleBlipName = {PlayerVehicleBlipName}");
                    sb.AppendLine($"PatrolBlipColor = {PatrolBlipColor}");
                    sb.AppendLine($"PatrolBlipScale = {PatrolBlipScale:F2}");
                    sb.AppendLine($"PatrolBlipSprite = {PatrolBlipSprite}");
                    sb.AppendLine($"TrafficStopBlipColor = {TrafficStopBlipColor}");
                    sb.AppendLine($"TrafficStopBlipScale = {TrafficStopBlipScale:F2}");
                    sb.AppendLine($"TrafficStopBlipSprite = {TrafficStopBlipSprite}");
                    sb.AppendLine($"StationaryBlipColor = {StationaryBlipColor}");
                    sb.AppendLine($"StationaryBlipScale = {StationaryBlipScale:F2}");
                    sb.AppendLine($"StationaryBlipSprite = {StationaryBlipSprite}");
                    sb.AppendLine($"EmergencyBlipColor = {EmergencyBlipColor}");
                    sb.AppendLine($"EmergencyBlipScale = {EmergencyBlipScale:F2}");
                    sb.AppendLine($"EmergencyBlipSprite = {EmergencyBlipSprite}");
                    sb.AppendLine($"EmsBlipColor = {EmsBlipColor}");
                    sb.AppendLine($"EmsBlipScale = {EmsBlipScale:F2}");
                    sb.AppendLine($"EmsBlipSprite = {EmsBlipSprite}");
                    sb.AppendLine($"FireBlipColor = {FireBlipColor}");
                    sb.AppendLine($"FireBlipScale = {FireBlipScale:F2}");
                    sb.AppendLine($"FireBlipSprite = {FireBlipSprite}");
                    sb.AppendLine($"PursuitBlipColor = {PursuitBlipColor}");
                    sb.AppendLine($"PursuitBlipScale = {PursuitBlipScale:F2}");
                    sb.AppendLine($"PursuitBlipSprite = {PursuitBlipSprite}");
                    sb.AppendLine($"CalloutResponseBlipColor = {CalloutResponseBlipColor}");
                    sb.AppendLine($"CalloutResponseBlipScale = {CalloutResponseBlipScale:F2}");
                    sb.AppendLine($"CalloutResponseBlipSprite = {CalloutResponseBlipSprite}");
                    sb.AppendLine($"CombatBlipColor = {CombatBlipColor}");
                    sb.AppendLine($"CombatBlipScale = {CombatBlipScale:F2}");
                    sb.AppendLine($"CombatBlipSprite = {CombatBlipSprite}");
                    sb.AppendLine($"OfficerFootBlipColor = {OfficerFootBlipColor}");
                    sb.AppendLine($"OfficerFootBlipScale = {OfficerFootBlipScale:F2}");
                    sb.AppendLine($"OfficerFootBlipSprite = {OfficerFootBlipSprite}");
                    sb.AppendLine($"ParkedCruiserBlipColor = {ParkedCruiserBlipColor}");
                    sb.AppendLine($"ParkedCruiserBlipScale = {ParkedCruiserBlipScale:F2}");
                    sb.AppendLine($"ParkedCruiserBlipSprite = {ParkedCruiserBlipSprite}");
                    sb.AppendLine($"GroupBackupBlipColor = {GroupBackupBlipColor}");
                    sb.AppendLine($"GroupBackupBlipScale = {GroupBackupBlipScale:F2}");
                    sb.AppendLine($"GroupBackupBlipSprite = {GroupBackupBlipSprite}");
                    sb.AppendLine($"GroupOfficerFootBlipColor = {GroupOfficerFootBlipColor}");
                    sb.AppendLine($"GroupOfficerFootBlipScale = {GroupOfficerFootBlipScale:F2}");
                    sb.AppendLine($"GroupOfficerFootBlipSprite = {GroupOfficerFootBlipSprite}");
                    sb.AppendLine($"BackupResponseBlipColor = {BackupResponseBlipColor}");
                    sb.AppendLine($"BackupResponseBlipScale = {BackupResponseBlipScale:F2}");
                    sb.AppendLine($"BackupResponseBlipSprite = {BackupResponseBlipSprite}");
                    sb.AppendLine($"SuspectBlipColor = {SuspectBlipColor}");
                    sb.AppendLine($"SuspectBlipScale = {SuspectBlipScale:F2}");
                    sb.AppendLine($"SuspectBlipSprite = {SuspectBlipSprite}");
                    sb.AppendLine();

                    // [Integration]
                    sb.AppendLine("[Integration]");
                    sb.AppendLine($"UseUltimateBackup = {UseUltimateBackup.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"Fallback = {AllowFallback.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"AllowFallback = {AllowFallback.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnableEupOutfits = {EnableEupOutfits.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"MuteTrafficStopSiren = {MuteTrafficStopSiren.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"RequireLightsForSiren = {RequireLightsForSiren.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"EnableSirenToneVariation = {EnableSirenToneVariation.ToString().ToLowerInvariant()}");
                    sb.AppendLine();

                    // [Backup]
                    sb.AppendLine("[Backup]");
                    sb.AppendLine($"EnableBackup = {EnableBackup.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"PanicButtonUnits = {PanicButtonUnitsMin}-{PanicButtonUnitsMax}");
                    sb.AppendLine($"GroupBackupUnits = {GroupBackupUnitsMin}-{GroupBackupUnitsMax}");
                    sb.AppendLine($"GroupBackupCode3 = {GroupBackupCode3.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"AllowAmbientCommandeer = {AllowAmbientCommandeer.ToString().ToLowerInvariant()}");
                    sb.AppendLine($"AllowFallbackSpawning = {AllowFallback.ToString().ToLowerInvariant()}");
                    sb.AppendLine();

                    File.WriteAllText(iniPath, sb.ToString(), Encoding.UTF8);
                    ResolvedIniPath = iniPath;
                    Logger.Info($"[Settings] Configuration saved successfully to: {iniPath}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Settings] Failed to save configuration to {ResolvedIniPath}", ex);
                }
            }
        }

        private static bool ParseEntry(string section, string key, string value)
        {
            string sec = section.ToLowerInvariant();
            string k = key.ToLowerInvariant();

            switch (sec)
            {
                case "general":
                    if (k == "enableplugin" || k == "pluginactive" || k == "active" || k == "activated") { EnablePlugin = ParseBool(value, EnablePlugin); return true; }
                    if (k == "enablefilelogging")
                    {
                        EnableFileLogging = ParseBool(value, EnableFileLogging);
                        Logger.EnableFileLogging = EnableFileLogging; // [E27]
                        return true;
                    }
                    if (k == "debug" || k == "enabledebuglogging" || k == "debuglogging")
                    {
                        EnableDebugLogging = ParseBool(value, EnableDebugLogging);
                        Logger.EnableDebugLogging = EnableDebugLogging; // [E27]
                        return true;
                    }
                    if (k == "chaosmode" || k == "chaos")
                    {
                        bool isChaos = ParseBool(value, ChaosMode);
                        ApplyChaosPreset(isChaos);
                        return true;
                    }
                    if (k == "preset")
                    {
                        ApplyPreset(value);
                        return true;
                    }
                    return false;

                case "keybinds":
                    if (k == "menukey") { MenuKey = ParseKey(value, MenuKey); return true; }
                    if (k == "menumodifierkey" || k == "menumodifier") { MenuModifierKey = ParseModifierKey(value, MenuModifierKey); return true; }
                    if (k == "panicbuttonkey" || k == "panickey") { PanicButtonKey = ParseKey(value, PanicButtonKey); return true; }
                    if (k == "panicbuttonmodifierkey" || k == "panicmodifier") { PanicButtonModifierKey = ParseModifierKey(value, PanicButtonModifierKey); return true; }
                    if (k == "groupbackupkey" || k == "groupkey") { GroupBackupKey = ParseKey(value, GroupBackupKey); return true; }
                    if (k == "groupbackupmodifierkey" || k == "groupmodifier") { GroupBackupModifierKey = ParseModifierKey(value, GroupBackupModifierKey); return true; }
                    if (k == "backupmenukey") { BackupMenuKey = ParseKey(value, BackupMenuKey); return true; }
                    if (k == "backupmenumodifierkey" || k == "backupmenumodifier") { BackupMenuModifierKey = ParseModifierKey(value, BackupMenuModifierKey); return true; }
                    if (k == "dismissbackupkey" || k == "dismisskey") { DismissBackupKey = ParseKey(value, DismissBackupKey); return true; }
                    return false;

                case "features":
                    if (k == "enablepatrol" || k == "patrol") { EnablePatrol = ParseBool(value, EnablePatrol); return true; }
                    if (k == "enabletrafficstops" || k == "trafficstops" || k == "trafficstop") { EnableTrafficStops = ParseBool(value, EnableTrafficStops); return true; }
                    if (k == "enablestationary" || k == "stationary") { EnableStationary = ParseBool(value, EnableStationary); return true; }
                    if (k == "enableemergencyruns" || k == "emergencyruns") { EnableEmergencyRuns = ParseBool(value, EnableEmergencyRuns); return true; }
                    if (k == "enablerejectedcallouts" || k == "rejectedcallouts" || k == "enablecallouttakeover") { EnableRejectedCallouts = ParseBool(value, EnableRejectedCallouts); return true; }
                    if (k == "enablepursuitdrafting" || k == "pursuitdrafting" || k == "enablepursuitassist") { EnablePursuitDrafting = ParseBool(value, EnablePursuitDrafting); return true; }
                    if (k == "enablethreatdefense" || k == "threatdefense" || k == "enablethreatassist") { EnableThreatDefense = ParseBool(value, EnableThreatDefense); return true; }
                    if (k == "enablebackup" || k == "backup") { EnableBackup = ParseBool(value, EnableBackup); return true; }
                    return false;

                case "backup":
                    if (k == "enablebackup") { EnableBackup = ParseBool(value, EnableBackup); return true; }
                    if (k == "panicbuttonunits" || k == "panicunits")
                    {
                        ParseRange(value, out int pMin, out int pMax, 1, 16, 4, 7);
                        PanicButtonUnitsMin = pMin;
                        PanicButtonUnitsMax = pMax;
                        return true;
                    }
                    if (k == "groupbackupunits" || k == "groupunits")
                    {
                        ParseRange(value, out int gMin, out int gMax, 1, 16, 2, 3);
                        GroupBackupUnitsMin = gMin;
                        GroupBackupUnitsMax = gMax;
                        return true;
                    }
                    if (k == "groupbackupcode3" || k == "groupcode3") { GroupBackupCode3 = ParseBool(value, GroupBackupCode3); return true; }
                    if (k == "allowambientcommandeer" || k == "commandeer") { AllowAmbientCommandeer = ParseBool(value, AllowAmbientCommandeer); return true; }
                    if (k == "allowfallbackspawning" || k == "fallbackspawning" || k == "allowfallback" || k == "fallback")
                    {
                        // [A8, D15, E23, F14] Reconcile to AllowFallback
                        AllowFallback = ParseBool(value, AllowFallback);
                        return true;
                    }
                    return false;

                case "limits":
                    if (k == "maxpatrolunits" || k == "maxpatrol") { MaxPatrolUnits = Math.Max(1, Math.Min(16, ParseInt(value, MaxPatrolUnits))); return true; }
                    if (k == "maxtrafficstops" || k == "maxstops") { MaxTrafficStops = Math.Max(0, Math.Min(8, ParseInt(value, MaxTrafficStops))); return true; }
                    if (k == "maxstationaryunits" || k == "maxstationary") { MaxStationaryUnits = Math.Max(0, Math.Min(8, ParseInt(value, MaxStationaryUnits))); return true; }
                    if (k == "maxemergencyruns" || k == "maxemergency") { MaxEmergencyRuns = Math.Max(0, Math.Min(8, ParseInt(value, MaxEmergencyRuns))); return true; }
                    if (k == "maxpursuitunits" || k == "maxpursuit") { MaxPursuitUnits = Math.Max(1, Math.Min(12, ParseInt(value, MaxPursuitUnits))); return true; }
                    if (k == "despawndistance") { DespawnDistance = Math.Max(150.0f, Math.Min(1500.0f, ParseFloat(value, DespawnDistance))); return true; }
                    if (k == "spawndistancemin") { SpawnDistanceMin = Math.Max(30.0f, Math.Min(300.0f, ParseFloat(value, SpawnDistanceMin))); return true; }
                    if (k == "spawndistancemax") { SpawnDistanceMax = Math.Max(100.0f, Math.Min(600.0f, ParseFloat(value, SpawnDistanceMax))); return true; }
                    return false;

                case "emergencyservices":
                    if (k == "enableemergencyvariety" || k == "emergencyvariety") { EnableEmergencyVariety = ParseBool(value, EnableEmergencyVariety); return true; }
                    if (k == "allowpoliceruns" || k == "policeruns") { AllowPoliceRuns = ParseBool(value, AllowPoliceRuns); return true; }
                    if (k == "allowfireruns" || k == "fireruns") { AllowFireRuns = ParseBool(value, AllowFireRuns); return true; }
                    if (k == "allowambulanceruns" || k == "ambulanceruns") { AllowAmbulanceRuns = ParseBool(value, AllowAmbulanceRuns); return true; }
                    if (k == "emergencyrunpoliceweight" || k == "policeweight") { EmergencyRunPoliceWeight = Math.Max(0, ParseInt(value, EmergencyRunPoliceWeight)); return true; }
                    if (k == "emergencyrunfireweight" || k == "fireweight") { EmergencyRunFireWeight = Math.Max(0, ParseInt(value, EmergencyRunFireWeight)); return true; }
                    if (k == "emergencyrunambulanceweight" || k == "ambulanceweight") { EmergencyRunAmbulanceWeight = Math.Max(0, ParseInt(value, EmergencyRunAmbulanceWeight)); return true; }
                    if (k == "maxpoliceruns") { MaxPoliceRuns = Math.Max(0, ParseInt(value, MaxPoliceRuns)); return true; }
                    if (k == "maxfireruns") { MaxFireRuns = Math.Max(0, ParseInt(value, MaxFireRuns)); return true; }
                    if (k == "maxambulanceruns") { MaxAmbulanceRuns = Math.Max(0, ParseInt(value, MaxAmbulanceRuns)); return true; }
                    return false;

                case "jurisdiction":
                    if (k == "highwaypatrolratio" || k == "highwayratio" || k == "sahpratio") { HighwayPatrolRatio = Math.Max(0, Math.Min(100, ParseInt(value, HighwayPatrolRatio))); return true; }
                    return false;

                case "radar":
                    if (k == "showradarblips" || k == "radarblips") { ShowRadarBlips = ParseBool(value, ShowRadarBlips); return true; }
                    if (k == "blipscale") { BlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, BlipScale))); return true; }
                    if (k == "shortrangeblips" || k == "shortrange") { ShortRangeBlips = ParseBool(value, ShortRangeBlips); return true; }
                    if (k == "enableplayervehicleblip" || k == "playervehicleblip") { EnablePlayerVehicleBlip = ParseBool(value, EnablePlayerVehicleBlip); return true; }
                    if (k == "playervehicleblipcolor") { PlayerVehicleBlipColor = ParseInt(value, PlayerVehicleBlipColor); return true; }
                    if (k == "playervehicleblipscale") { PlayerVehicleBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, PlayerVehicleBlipScale))); return true; }
                    if (k == "playervehicleblipsprite") { PlayerVehicleBlipSprite = ParseInt(value, PlayerVehicleBlipSprite); return true; }
                    if (k == "playervehicleblipname") { PlayerVehicleBlipName = string.IsNullOrWhiteSpace(value) ? "Personal Cruiser" : value.Trim(); return true; }

                    if (k == "patrolblipcolor") { PatrolBlipColor = ParseInt(value, PatrolBlipColor); return true; }
                    if (k == "patrolblipscale") { PatrolBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, PatrolBlipScale))); return true; }
                    if (k == "patrolblipsprite") { PatrolBlipSprite = ParseInt(value, PatrolBlipSprite); return true; }

                    if (k == "trafficstopblipcolor") { TrafficStopBlipColor = ParseInt(value, TrafficStopBlipColor); return true; }
                    if (k == "trafficstopblipscale") { TrafficStopBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, TrafficStopBlipScale))); return true; }
                    if (k == "trafficstopblipsprite") { TrafficStopBlipSprite = ParseInt(value, TrafficStopBlipSprite); return true; }

                    if (k == "stationaryblipcolor") { StationaryBlipColor = ParseInt(value, StationaryBlipColor); return true; }
                    if (k == "stationaryblipscale") { StationaryBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, StationaryBlipScale))); return true; }
                    if (k == "stationaryblipsprite") { StationaryBlipSprite = ParseInt(value, StationaryBlipSprite); return true; }

                    if (k == "emergencyblipcolor") { EmergencyBlipColor = ParseInt(value, EmergencyBlipColor); return true; }
                    if (k == "emergencyblipscale") { EmergencyBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, EmergencyBlipScale))); return true; }
                    if (k == "emergencyblipsprite") { EmergencyBlipSprite = ParseInt(value, EmergencyBlipSprite); return true; }

                    if (k == "emsblipcolor") { EmsBlipColor = ParseInt(value, EmsBlipColor); return true; }
                    if (k == "emsblipscale") { EmsBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, EmsBlipScale))); return true; }
                    if (k == "emsblipsprite") { EmsBlipSprite = ParseInt(value, EmsBlipSprite); return true; }

                    if (k == "fireblipcolor") { FireBlipColor = ParseInt(value, FireBlipColor); return true; }
                    if (k == "fireblipscale") { FireBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, FireBlipScale))); return true; }
                    if (k == "fireblipsprite") { FireBlipSprite = ParseInt(value, FireBlipSprite); return true; }

                    if (k == "pursuitblipcolor") { PursuitBlipColor = ParseInt(value, PursuitBlipColor); return true; }
                    if (k == "pursuitblipscale") { PursuitBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, PursuitBlipScale))); return true; }
                    if (k == "pursuitblipsprite") { PursuitBlipSprite = ParseInt(value, PursuitBlipSprite); return true; }

                    if (k == "calloutresponseblipcolor") { CalloutResponseBlipColor = ParseInt(value, CalloutResponseBlipColor); return true; }
                    if (k == "calloutresponseblipscale") { CalloutResponseBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, CalloutResponseBlipScale))); return true; }
                    if (k == "calloutresponseblipsprite") { CalloutResponseBlipSprite = ParseInt(value, CalloutResponseBlipSprite); return true; }

                    if (k == "combatblipcolor") { CombatBlipColor = ParseInt(value, CombatBlipColor); return true; }
                    if (k == "combatblipscale") { CombatBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, CombatBlipScale))); return true; }
                    if (k == "combatblipsprite") { CombatBlipSprite = ParseInt(value, CombatBlipSprite); return true; }

                    if (k == "officerfootblipcolor") { OfficerFootBlipColor = ParseInt(value, OfficerFootBlipColor); return true; }
                    if (k == "officerfootblipscale") { OfficerFootBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, OfficerFootBlipScale))); return true; }
                    if (k == "officerfootblipsprite") { OfficerFootBlipSprite = ParseInt(value, OfficerFootBlipSprite); return true; }

                    if (k == "parkedcruiserblipcolor") { ParkedCruiserBlipColor = ParseInt(value, ParkedCruiserBlipColor); return true; }
                    if (k == "parkedcruiserblipscale") { ParkedCruiserBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, ParkedCruiserBlipScale))); return true; }
                    if (k == "parkedcruiserblipsprite") { ParkedCruiserBlipSprite = ParseInt(value, ParkedCruiserBlipSprite); return true; }

                    if (k == "groupbackupblipcolor") { GroupBackupBlipColor = ParseInt(value, GroupBackupBlipColor); return true; }
                    if (k == "groupbackupblipscale") { GroupBackupBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, GroupBackupBlipScale))); return true; }
                    if (k == "groupbackupblipsprite") { GroupBackupBlipSprite = ParseInt(value, GroupBackupBlipSprite); return true; }

                    if (k == "groupofficerfootblipcolor") { GroupOfficerFootBlipColor = ParseInt(value, GroupOfficerFootBlipColor); return true; }
                    if (k == "groupofficerfootblipscale") { GroupOfficerFootBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, GroupOfficerFootBlipScale))); return true; }
                    if (k == "groupofficerfootblipsprite") { GroupOfficerFootBlipSprite = ParseInt(value, GroupOfficerFootBlipSprite); return true; }

                    if (k == "backupresponseblipcolor") { BackupResponseBlipColor = ParseInt(value, BackupResponseBlipColor); return true; }
                    if (k == "backupresponseblipscale") { BackupResponseBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, BackupResponseBlipScale))); return true; }
                    if (k == "backupresponseblipsprite") { BackupResponseBlipSprite = ParseInt(value, BackupResponseBlipSprite); return true; }

                    if (k == "suspectblipcolor") { SuspectBlipColor = ParseInt(value, SuspectBlipColor); return true; }
                    if (k == "suspectblipscale") { SuspectBlipScale = Math.Max(0.1f, Math.Min(2.0f, ParseFloat(value, SuspectBlipScale))); return true; }
                    if (k == "suspectblipsprite") { SuspectBlipSprite = ParseInt(value, SuspectBlipSprite); return true; }
                    return false;

                case "integration":
                    if (k == "useultimatebackup" || k == "ultimatebackup") { UseUltimateBackup = ParseBool(value, UseUltimateBackup); return true; }
                    if (k == "fallback" || k == "allowfallback" || k == "allowultimatebackupfallback")
                    {
                        AllowFallback = ParseBool(value, AllowFallback);
                        return true;
                    }
                    if (k == "enableeupoutfits" || k == "enableeup" || k == "eupoutfits") { EnableEupOutfits = ParseBool(value, EnableEupOutfits); return true; }
                    if (k == "mutetrafficstopsiren" || k == "mutesiren") { MuteTrafficStopSiren = ParseBool(value, MuteTrafficStopSiren); return true; }
                    if (k == "requirelightsforsiren" || k == "requirelights") { RequireLightsForSiren = ParseBool(value, RequireLightsForSiren); return true; }
                    if (k == "enablesirentonevariation" || k == "sirentonevariation" || k == "cyclesirens") { EnableSirenToneVariation = ParseBool(value, EnableSirenToneVariation); return true; }
                    return false;
            }

            return false;
        }

        private static Keys ParseKey(string value, Keys defaultKey)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultKey;
            string clean = value.Trim();

            if (clean.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.None;
            }

            if (clean.Equals("pagedown", StringComparison.OrdinalIgnoreCase) || clean.Equals("pgdn", StringComparison.OrdinalIgnoreCase)) return Keys.PageDown;
            if (clean.Equals("pageup", StringComparison.OrdinalIgnoreCase) || clean.Equals("pgup", StringComparison.OrdinalIgnoreCase)) return Keys.PageUp;

            if (Enum.TryParse(clean, true, out Keys parsed))
            {
                return parsed;
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
                clean.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return Keys.None;
            }

            if (clean.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || clean.Equals("control", StringComparison.OrdinalIgnoreCase)) return Keys.ControlKey;
            if (clean.Equals("lctrl", StringComparison.OrdinalIgnoreCase) || clean.Equals("lcontrol", StringComparison.OrdinalIgnoreCase)) return Keys.LControlKey;
            if (clean.Equals("shift", StringComparison.OrdinalIgnoreCase) || clean.Equals("lshift", StringComparison.OrdinalIgnoreCase)) return Keys.LShiftKey;
            if (clean.Equals("alt", StringComparison.OrdinalIgnoreCase) || clean.Equals("lalt", StringComparison.OrdinalIgnoreCase)) return Keys.LMenu;
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

        /// <summary>
        /// Parses floating point value, strictly rejecting NaN and Infinity [A3].
        /// </summary>
        private static float ParseFloat(string value, float defaultVal)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultVal;
            string normalized = value.Replace(',', '.');
            if (float.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float res))
            {
                if (float.IsNaN(res) || float.IsInfinity(res))
                {
                    return defaultVal;
                }
                return res;
            }
            return defaultVal;
        }

        /// <summary>
        /// Tokenizes range string (e.g. "4-7", "2 to 3") without splitting on decimals [A22].
        /// </summary>
        private static void ParseRange(string value, out int min, out int max, int clampMin, int clampMax, int defaultMin, int defaultMax)
        {
            min = defaultMin;
            max = defaultMax;
            if (string.IsNullOrWhiteSpace(value)) return;

            string clean = value.Trim();
            string[] parts = clean.Split(new[] { '-', ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 1 && clean.IndexOf("to", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts = System.Text.RegularExpressions.Regex.Split(clean, @"\s*to\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out int p1) && int.TryParse(parts[parts.Length - 1].Trim(), out int p2))
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
