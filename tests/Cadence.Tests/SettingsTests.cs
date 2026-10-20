using System;
using System.IO;
using System.Windows.Forms;
using Cadence.Core;

namespace Cadence.Tests
{
    public static class SettingsTests
    {
        public static void RunAll()
        {
            Console.WriteLine("\n[RUNNING] SettingsTests Suite");

            TestResetToDefaults();
            TestLoadFromIni();
            TestLoadIgnoresCommentsAndWhitespace();
            TestLoadResetsRemovedKeysToDefaults();
            TestSaveRoundtrip();
            TestValidateDistancesEnforcesMargins();
            TestValidateDistancesRejectsNaN();
            TestValidateDistancesRejectsInfinity();
            TestApplyPresetRealistic();
            TestApplyPresetChaos();
            TestApplyPresetMetro();
            TestApplyPresetHighway();
            TestApplyPresetRural();
            TestApplyPresetWarzone();
            TestApplyPresetNumbered();
            TestNewSettingsRoundtrip();

            Console.WriteLine("[PASSED] All SettingsTests passed successfully.");
        }

        private static void TestResetToDefaults()
        {
            Settings.MaxPatrolUnits = 99;
            Settings.Preset = "NonExistent";
            Settings.ResetToDefaults();

            Assert.AreEqual("Realistic", Settings.Preset, "Preset should reset to default baseline");
            Assert.AreEqual(4, Settings.MaxPatrolUnits, "MaxPatrolUnits should reset to default baseline");
            Assert.IsTrue(Settings.EnablePlugin, "EnablePlugin should be true by default");
        }

        private static void TestLoadFromIni()
        {
            string tempIni = Path.Combine(Path.GetTempPath(), $"Cadence_Test_{Guid.NewGuid():N}.ini");
            try
            {
                File.WriteAllText(tempIni, @"
[General]
EnablePlugin = true
PluginActive = true
EnableFileLogging = true
Debug = false
Preset = Realistic
ChaosMode = false

[Keybinds]
MenuKey = PageDown
MenuModifierKey = Q
PanicButtonKey = Z
PanicButtonModifierKey = LControlKey
GroupBackupKey = C
GroupBackupModifierKey = LControlKey
BackupMenuKey = PageDown
BackupMenuModifierKey = X
DismissBackupKey = Enter

[Features]
EnablePatrol = true
EnableTrafficStops = false
EnableStationary = true
EnableEmergencyRuns = false
EnableRejectedCallouts = true
EnablePursuitDrafting = true
EnableThreatDefense = false

[Limits]
MaxPatrolUnits = 3
MaxTrafficStops = 1
MaxStationaryUnits = 2
MaxEmergencyRuns = 1
MaxPursuitUnits = 5
DespawnDistance = 500.0
SpawnDistanceMin = 80.0
SpawnDistanceMax = 250.0

[EmergencyServices]
EnableEmergencyVariety = false
AllowPoliceRuns = true
AllowFireRuns = false
AllowAmbulanceRuns = true

[Jurisdiction]
HighwayPatrolRatio = 60

[Integration]
UseUltimateBackup = true
Fallback = false
EnableEupOutfits = false
MuteTrafficStopSiren = true
RequireLightsForSiren = true
");

                Settings.Load(tempIni);

                Assert.IsTrue(Settings.EnablePlugin, "EnablePlugin should be true");
                Assert.IsFalse(Settings.EnableDebugLogging, "Debug should be false");
                Assert.AreEqual("Realistic", Settings.Preset, "Preset should be Realistic");
                Assert.IsFalse(Settings.ChaosMode, "ChaosMode should be false");
                Assert.AreEqual(Keys.PageDown, Settings.MenuKey, "MenuKey should be PageDown");
                Assert.AreEqual(Keys.Q, Settings.MenuModifierKey, "MenuModifierKey should be Q");
                Assert.AreEqual(Keys.Z, Settings.PanicButtonKey, "PanicButtonKey should be Z");
                Assert.AreEqual(Keys.LControlKey, Settings.PanicButtonModifierKey, "PanicButtonModifierKey should be LControlKey");

                Assert.IsTrue(Settings.EnablePatrol, "EnablePatrol should be true");
                Assert.IsFalse(Settings.EnableTrafficStops, "EnableTrafficStops should be false");
                Assert.IsTrue(Settings.EnableStationary, "EnableStationary should be true");
                Assert.IsFalse(Settings.EnableEmergencyRuns, "EnableEmergencyRuns should be false");

                Assert.AreEqual(3, Settings.MaxPatrolUnits, "MaxPatrolUnits should be 3");
                Assert.AreEqual(1, Settings.MaxTrafficStops, "MaxTrafficStops should be 1");
                Assert.AreEqual(2, Settings.MaxStationaryUnits, "MaxStationaryUnits should be 2");
                Assert.AreEqual(5, Settings.MaxPursuitUnits, "MaxPursuitUnits should be 5");

                Assert.AreEqual(80.0f, Settings.SpawnDistanceMin, "SpawnDistanceMin should be 80.0");
                Assert.AreEqual(250.0f, Settings.SpawnDistanceMax, "SpawnDistanceMax should be 250.0");
                Assert.AreEqual(500.0f, Settings.DespawnDistance, "DespawnDistance should be 500.0");

                Assert.IsFalse(Settings.EnableEmergencyVariety, "EnableEmergencyVariety should be false");
                Assert.IsTrue(Settings.AllowPoliceRuns, "AllowPoliceRuns should be true");
                Assert.IsFalse(Settings.AllowFireRuns, "AllowFireRuns should be false");
                Assert.IsTrue(Settings.AllowAmbulanceRuns, "AllowAmbulanceRuns should be true");
                Assert.AreEqual(60, Settings.HighwayPatrolRatio, "HighwayPatrolRatio should be 60");
                Assert.IsFalse(Settings.EnableEupOutfits, "EnableEupOutfits should be false");
            }
            finally
            {
                if (File.Exists(tempIni)) File.Delete(tempIni);
            }
        }

        private static void TestLoadIgnoresCommentsAndWhitespace()
        {
            string tempIni = Path.Combine(Path.GetTempPath(), $"Cadence_Test_{Guid.NewGuid():N}.ini");
            try
            {
                File.WriteAllText(tempIni, @"
// Full line comment
# Another comment style
; Semicolon comment

   [Limits]   
   // Indented comment
   MaxPatrolUnits   =   7   
   DespawnDistance = 490.0  
");

                Settings.Load(tempIni);
                Assert.AreEqual(7, Settings.MaxPatrolUnits, "MaxPatrolUnits with whitespace padding should parse correctly");
                Assert.AreEqual(490.0f, Settings.DespawnDistance, "DespawnDistance with whitespace should parse correctly");
            }
            finally
            {
                if (File.Exists(tempIni)) File.Delete(tempIni);
            }
        }

        private static void TestLoadResetsRemovedKeysToDefaults()
        {
            string tempIni1 = Path.Combine(Path.GetTempPath(), $"Cadence_Test_{Guid.NewGuid():N}.ini");
            string tempIni2 = Path.Combine(Path.GetTempPath(), $"Cadence_Test_{Guid.NewGuid():N}.ini");
            try
            {
                // INI 1 sets MaxPatrolUnits to 11
                File.WriteAllText(tempIni1, "[Limits]\nMaxPatrolUnits = 11\n");
                Settings.Load(tempIni1);
                Assert.AreEqual(11, Settings.MaxPatrolUnits, "Initial load should set 11");

                // INI 2 has empty limits; MaxPatrolUnits was removed
                File.WriteAllText(tempIni2, "[General]\nDebug = false\n");
                Settings.Load(tempIni2);
                Assert.AreEqual(4, Settings.MaxPatrolUnits, "Removed key must reset to baseline default [E28]");
            }
            finally
            {
                if (File.Exists(tempIni1)) File.Delete(tempIni1);
                if (File.Exists(tempIni2)) File.Delete(tempIni2);
            }
        }

        private static void TestSaveRoundtrip()
        {
            string tempIni = Path.Combine(Path.GetTempPath(), $"Cadence_SaveRoundtrip_{Guid.NewGuid():N}.ini");
            try
            {
                Settings.ResetToDefaults();
                Settings.EnablePlugin = true;
                Settings.EnableDebugLogging = false;
                Settings.Preset = "Realistic";
                Settings.ChaosMode = false;
                Settings.MaxPatrolUnits = 4;
                Settings.MaxTrafficStops = 2;
                Settings.MaxStationaryUnits = 2;
                Settings.MaxEmergencyRuns = 2;
                Settings.MaxPursuitUnits = 4;
                Settings.SpawnDistanceMin = 80.0f;
                Settings.SpawnDistanceMax = 250.0f;
                Settings.DespawnDistance = 450.0f;
                Settings.HighwayPatrolRatio = 45;
                Settings.MenuKey = Keys.F11;
                Settings.MenuModifierKey = Keys.None;

                // Explicitly configure all Radar blip properties
                Settings.ShowRadarBlips = false;
                Settings.BlipScale = 0.60f;
                Settings.ShortRangeBlips = true;
                Settings.EnablePlayerVehicleBlip = false;
                Settings.PlayerVehicleBlipColor = 3;
                Settings.PlayerVehicleBlipScale = 0.50f;
                Settings.PlayerVehicleBlipSprite = 60;
                Settings.PlayerVehicleBlipName = "Custom Interceptor";

                Settings.PatrolBlipColor = 5;
                Settings.PatrolBlipScale = 0.55f;
                Settings.PatrolBlipSprite = 57;

                Settings.TrafficStopBlipColor = 6;
                Settings.TrafficStopBlipScale = 0.56f;
                Settings.TrafficStopBlipSprite = 58;

                Settings.StationaryBlipColor = 7;
                Settings.StationaryBlipScale = 0.57f;
                Settings.StationaryBlipSprite = 59;

                Settings.EmergencyBlipColor = 8;
                Settings.EmergencyBlipScale = 0.58f;
                Settings.EmergencyBlipSprite = 62;

                Settings.EmsBlipColor = 9;
                Settings.EmsBlipScale = 0.59f;
                Settings.EmsBlipSprite = 63;

                Settings.FireBlipColor = 10;
                Settings.FireBlipScale = 0.61f;
                Settings.FireBlipSprite = 64;

                Settings.PursuitBlipColor = 11;
                Settings.PursuitBlipScale = 0.62f;
                Settings.PursuitBlipSprite = 65;

                Settings.CalloutResponseBlipColor = 12;
                Settings.CalloutResponseBlipScale = 0.63f;
                Settings.CalloutResponseBlipSprite = 66;

                Settings.CombatBlipColor = 13;
                Settings.CombatBlipScale = 0.64f;
                Settings.CombatBlipSprite = 67;

                Settings.OfficerFootBlipColor = 14;
                Settings.OfficerFootBlipScale = 0.42f;
                Settings.OfficerFootBlipSprite = 68;

                Settings.ParkedCruiserBlipColor = 15;
                Settings.ParkedCruiserBlipScale = 0.65f;
                Settings.ParkedCruiserBlipSprite = 69;

                Settings.GroupBackupBlipColor = 16;
                Settings.GroupBackupBlipScale = 0.66f;
                Settings.GroupBackupBlipSprite = 70;

                Settings.GroupOfficerFootBlipColor = 17;
                Settings.GroupOfficerFootBlipScale = 0.44f;
                Settings.GroupOfficerFootBlipSprite = 71;

                Settings.BackupResponseBlipColor = 19;
                Settings.BackupResponseBlipScale = 0.67f;
                Settings.BackupResponseBlipSprite = 72;

                Settings.SuspectBlipColor = 20;
                Settings.SuspectBlipScale = 0.68f;
                Settings.SuspectBlipSprite = 73;

                // Save to file
                Settings.Save(tempIni);
                Assert.IsTrue(File.Exists(tempIni), "Saved file should exist on disk");

                // Clear memory settings by resetting to defaults
                Settings.ResetToDefaults();
                Assert.AreNotEqual(Keys.F11, Settings.MenuKey, "Settings should have reset");
                Assert.IsTrue(Settings.ShowRadarBlips, "ShowRadarBlips should have reset to default true");

                // Reload from saved file
                Settings.Load(tempIni);

                Assert.AreEqual(false, Settings.EnableDebugLogging, "Roundtripped Debug should match");
                Assert.AreEqual("Realistic", Settings.Preset, "Roundtripped Preset should match");
                Assert.AreEqual(4, Settings.MaxPatrolUnits, "Roundtripped MaxPatrolUnits should match");
                Assert.AreEqual(2, Settings.MaxTrafficStops, "Roundtripped MaxTrafficStops should match");
                Assert.AreEqual(80.0f, Settings.SpawnDistanceMin, "Roundtripped SpawnDistanceMin should match");
                Assert.AreEqual(250.0f, Settings.SpawnDistanceMax, "Roundtripped SpawnDistanceMax should match");
                Assert.AreEqual(450.0f, Settings.DespawnDistance, "Roundtripped DespawnDistance should match");
                Assert.AreEqual(45, Settings.HighwayPatrolRatio, "Roundtripped HighwayPatrolRatio should match");
                Assert.AreEqual(Keys.F11, Settings.MenuKey, "Roundtripped MenuKey should match");
                Assert.AreEqual(Keys.None, Settings.MenuModifierKey, "Roundtripped MenuModifierKey should match");

                // Full assertions for all Radar properties
                Assert.IsFalse(Settings.ShowRadarBlips, "Roundtripped ShowRadarBlips should match");
                Assert.AreEqual(0.60f, Settings.BlipScale, "Roundtripped BlipScale should match");
                Assert.IsTrue(Settings.ShortRangeBlips, "Roundtripped ShortRangeBlips should match");
                Assert.IsFalse(Settings.EnablePlayerVehicleBlip, "Roundtripped EnablePlayerVehicleBlip should match");
                Assert.AreEqual(3, Settings.PlayerVehicleBlipColor, "Roundtripped PlayerVehicleBlipColor should match");
                Assert.AreEqual(0.50f, Settings.PlayerVehicleBlipScale, "Roundtripped PlayerVehicleBlipScale should match");
                Assert.AreEqual(60, Settings.PlayerVehicleBlipSprite, "Roundtripped PlayerVehicleBlipSprite should match");
                Assert.AreEqual("Custom Interceptor", Settings.PlayerVehicleBlipName, "Roundtripped PlayerVehicleBlipName should match");

                Assert.AreEqual(5, Settings.PatrolBlipColor, "Roundtripped PatrolBlipColor should match");
                Assert.AreEqual(0.55f, Settings.PatrolBlipScale, "Roundtripped PatrolBlipScale should match");
                Assert.AreEqual(57, Settings.PatrolBlipSprite, "Roundtripped PatrolBlipSprite should match");

                Assert.AreEqual(6, Settings.TrafficStopBlipColor, "Roundtripped TrafficStopBlipColor should match");
                Assert.AreEqual(0.56f, Settings.TrafficStopBlipScale, "Roundtripped TrafficStopBlipScale should match");
                Assert.AreEqual(58, Settings.TrafficStopBlipSprite, "Roundtripped TrafficStopBlipSprite should match");

                Assert.AreEqual(7, Settings.StationaryBlipColor, "Roundtripped StationaryBlipColor should match");
                Assert.AreEqual(0.57f, Settings.StationaryBlipScale, "Roundtripped StationaryBlipScale should match");
                Assert.AreEqual(59, Settings.StationaryBlipSprite, "Roundtripped StationaryBlipSprite should match");

                Assert.AreEqual(8, Settings.EmergencyBlipColor, "Roundtripped EmergencyBlipColor should match");
                Assert.AreEqual(0.58f, Settings.EmergencyBlipScale, "Roundtripped EmergencyBlipScale should match");
                Assert.AreEqual(62, Settings.EmergencyBlipSprite, "Roundtripped EmergencyBlipSprite should match");

                Assert.AreEqual(9, Settings.EmsBlipColor, "Roundtripped EmsBlipColor should match");
                Assert.AreEqual(0.59f, Settings.EmsBlipScale, "Roundtripped EmsBlipScale should match");
                Assert.AreEqual(63, Settings.EmsBlipSprite, "Roundtripped EmsBlipSprite should match");

                Assert.AreEqual(10, Settings.FireBlipColor, "Roundtripped FireBlipColor should match");
                Assert.AreEqual(0.61f, Settings.FireBlipScale, "Roundtripped FireBlipScale should match");
                Assert.AreEqual(64, Settings.FireBlipSprite, "Roundtripped FireBlipSprite should match");

                Assert.AreEqual(11, Settings.PursuitBlipColor, "Roundtripped PursuitBlipColor should match");
                Assert.AreEqual(0.62f, Settings.PursuitBlipScale, "Roundtripped PursuitBlipScale should match");
                Assert.AreEqual(65, Settings.PursuitBlipSprite, "Roundtripped PursuitBlipSprite should match");

                Assert.AreEqual(12, Settings.CalloutResponseBlipColor, "Roundtripped CalloutResponseBlipColor should match");
                Assert.AreEqual(0.63f, Settings.CalloutResponseBlipScale, "Roundtripped CalloutResponseBlipScale should match");
                Assert.AreEqual(66, Settings.CalloutResponseBlipSprite, "Roundtripped CalloutResponseBlipSprite should match");

                Assert.AreEqual(13, Settings.CombatBlipColor, "Roundtripped CombatBlipColor should match");
                Assert.AreEqual(0.64f, Settings.CombatBlipScale, "Roundtripped CombatBlipScale should match");
                Assert.AreEqual(67, Settings.CombatBlipSprite, "Roundtripped CombatBlipSprite should match");

                Assert.AreEqual(14, Settings.OfficerFootBlipColor, "Roundtripped OfficerFootBlipColor should match");
                Assert.AreEqual(0.42f, Settings.OfficerFootBlipScale, "Roundtripped OfficerFootBlipScale should match");
                Assert.AreEqual(68, Settings.OfficerFootBlipSprite, "Roundtripped OfficerFootBlipSprite should match");

                Assert.AreEqual(15, Settings.ParkedCruiserBlipColor, "Roundtripped ParkedCruiserBlipColor should match");
                Assert.AreEqual(0.65f, Settings.ParkedCruiserBlipScale, "Roundtripped ParkedCruiserBlipScale should match");
                Assert.AreEqual(69, Settings.ParkedCruiserBlipSprite, "Roundtripped ParkedCruiserBlipSprite should match");

                Assert.AreEqual(16, Settings.GroupBackupBlipColor, "Roundtripped GroupBackupBlipColor should match");
                Assert.AreEqual(0.66f, Settings.GroupBackupBlipScale, "Roundtripped GroupBackupBlipScale should match");
                Assert.AreEqual(70, Settings.GroupBackupBlipSprite, "Roundtripped GroupBackupBlipSprite should match");

                Assert.AreEqual(17, Settings.GroupOfficerFootBlipColor, "Roundtripped GroupOfficerFootBlipColor should match");
                Assert.AreEqual(0.44f, Settings.GroupOfficerFootBlipScale, "Roundtripped GroupOfficerFootBlipScale should match");
                Assert.AreEqual(71, Settings.GroupOfficerFootBlipSprite, "Roundtripped GroupOfficerFootBlipSprite should match");

                Assert.AreEqual(19, Settings.BackupResponseBlipColor, "Roundtripped BackupResponseBlipColor should match");
                Assert.AreEqual(0.67f, Settings.BackupResponseBlipScale, "Roundtripped BackupResponseBlipScale should match");
                Assert.AreEqual(72, Settings.BackupResponseBlipSprite, "Roundtripped BackupResponseBlipSprite should match");

                Assert.AreEqual(20, Settings.SuspectBlipColor, "Roundtripped SuspectBlipColor should match");
                Assert.AreEqual(0.68f, Settings.SuspectBlipScale, "Roundtripped SuspectBlipScale should match");
                Assert.AreEqual(73, Settings.SuspectBlipSprite, "Roundtripped SuspectBlipSprite should match");
            }
            finally
            {
                if (File.Exists(tempIni)) File.Delete(tempIni);
            }
        }

        private static void TestValidateDistancesEnforcesMargins()
        {
            // Case 1: Inverted min and max (min > max)
            Settings.SpawnDistanceMin = 200.0f;
            Settings.SpawnDistanceMax = 150.0f;
            Settings.DespawnDistance = 220.0f;

            Settings.ValidateDistances();

            Assert.IsTrue(Settings.SpawnDistanceMax >= Settings.SpawnDistanceMin + 50.0f,
                $"SpawnDistanceMax ({Settings.SpawnDistanceMax}) must be >= Min ({Settings.SpawnDistanceMin}) + 50m");
            Assert.IsTrue(Settings.DespawnDistance >= Settings.SpawnDistanceMax + 50.0f,
                $"DespawnDistance ({Settings.DespawnDistance}) must be >= Max ({Settings.SpawnDistanceMax}) + 50m");

            // Case 2: Max >= Despawn
            Settings.SpawnDistanceMin = 50.0f;
            Settings.SpawnDistanceMax = 300.0f;
            Settings.DespawnDistance = 250.0f;

            Settings.ValidateDistances();

            Assert.IsTrue(Settings.DespawnDistance >= Settings.SpawnDistanceMax + 50.0f,
                $"DespawnDistance ({Settings.DespawnDistance}) must be >= Max ({Settings.SpawnDistanceMax}) + 50m");
        }

        private static void TestValidateDistancesRejectsNaN()
        {
            Settings.SpawnDistanceMin = float.NaN;
            Settings.SpawnDistanceMax = float.NaN;
            Settings.DespawnDistance = float.NaN;

            Settings.ValidateDistances();

            Assert.IsFalse(float.IsNaN(Settings.SpawnDistanceMin), "SpawnDistanceMin must not be NaN");
            Assert.IsFalse(float.IsNaN(Settings.SpawnDistanceMax), "SpawnDistanceMax must not be NaN");
            Assert.IsFalse(float.IsNaN(Settings.DespawnDistance), "DespawnDistance must not be NaN");

            Assert.IsTrue(Settings.SpawnDistanceMin >= 10.0f, "SpawnDistanceMin must be >= 10m");
            Assert.IsTrue(Settings.SpawnDistanceMax >= Settings.SpawnDistanceMin + 50.0f, "SpawnDistanceMax must be >= Min + 50m");
            Assert.IsTrue(Settings.DespawnDistance >= Settings.SpawnDistanceMax + 50.0f, "DespawnDistance must be >= Max + 50m");
        }

        private static void TestValidateDistancesRejectsInfinity()
        {
            Settings.SpawnDistanceMin = float.NegativeInfinity;
            Settings.SpawnDistanceMax = float.PositiveInfinity;
            Settings.DespawnDistance = float.PositiveInfinity;

            Settings.ValidateDistances();

            Assert.IsFalse(float.IsInfinity(Settings.SpawnDistanceMin), "SpawnDistanceMin must not be Infinity");
            Assert.IsFalse(float.IsInfinity(Settings.SpawnDistanceMax), "SpawnDistanceMax must not be Infinity");
            Assert.IsFalse(float.IsInfinity(Settings.DespawnDistance), "DespawnDistance must not be Infinity");

            Assert.IsTrue(Settings.SpawnDistanceMin >= 10.0f, "SpawnDistanceMin must be valid");
            Assert.IsTrue(Settings.SpawnDistanceMax >= Settings.SpawnDistanceMin + 50.0f, "SpawnDistanceMax must maintain +50m margin");
            Assert.IsTrue(Settings.DespawnDistance >= Settings.SpawnDistanceMax + 50.0f, "DespawnDistance must maintain +50m margin");
        }

        private static void TestApplyPresetRealistic()
        {
            Settings.ApplyPreset("Realistic");

            Assert.AreEqual("Realistic", Settings.Preset, "Preset name should be Realistic");
            Assert.IsFalse(Settings.ChaosMode, "ChaosMode should be false for Realistic");
            Assert.AreEqual(4, Settings.MaxPatrolUnits, "MaxPatrolUnits should be 4");
            Assert.AreEqual(2, Settings.MaxTrafficStops, "MaxTrafficStops should be 2");
            Assert.AreEqual(2, Settings.MaxStationaryUnits, "MaxStationaryUnits should be 2");
            Assert.AreEqual(2, Settings.MaxEmergencyRuns, "MaxEmergencyRuns should be 2");
            Assert.AreEqual(4, Settings.MaxPursuitUnits, "MaxPursuitUnits should be 4");
            Assert.AreEqual(80.0f, Settings.SpawnDistanceMin, "SpawnDistanceMin should be 80.0");
            Assert.AreEqual(250.0f, Settings.SpawnDistanceMax, "SpawnDistanceMax should be 250.0");
            Assert.AreEqual(450.0f, Settings.DespawnDistance, "DespawnDistance should be 450.0");
        }

        private static void TestApplyPresetChaos()
        {
            Settings.ApplyPreset("Chaos");

            Assert.AreEqual("Chaos", Settings.Preset, "Preset name should be Chaos");
            Assert.IsTrue(Settings.ChaosMode, "ChaosMode should be true for Chaos");
            Assert.AreEqual(8, Settings.MaxPatrolUnits, "MaxPatrolUnits should be 8");
            Assert.AreEqual(4, Settings.MaxTrafficStops, "MaxTrafficStops should be 4");
            Assert.AreEqual(4, Settings.MaxStationaryUnits, "MaxStationaryUnits should be 4");
            Assert.AreEqual(4, Settings.MaxEmergencyRuns, "MaxEmergencyRuns should be 4");
            Assert.AreEqual(6, Settings.MaxPursuitUnits, "MaxPursuitUnits should be 6");
            Assert.AreEqual(70.0f, Settings.SpawnDistanceMin, "SpawnDistanceMin should be 70.0");
            Assert.AreEqual(220.0f, Settings.SpawnDistanceMax, "SpawnDistanceMax should be 220.0");
            Assert.AreEqual(480.0f, Settings.DespawnDistance, "DespawnDistance should be 480.0");
        }

        private static void TestApplyPresetMetro()
        {
            Settings.ApplyPreset("Metro");

            Assert.AreEqual("Metro", Settings.Preset, "Preset name should be Metro");
            Assert.IsTrue(Settings.ChaosMode, "ChaosMode should be true for Metro");
            Assert.AreEqual(6, Settings.MaxPatrolUnits, "MaxPatrolUnits should be 6");
            Assert.AreEqual(3, Settings.MaxTrafficStops, "MaxTrafficStops should be 3");
            Assert.AreEqual(2, Settings.MaxStationaryUnits, "MaxStationaryUnits should be 2");
            Assert.AreEqual(3, Settings.MaxEmergencyRuns, "MaxEmergencyRuns should be 3");
            Assert.AreEqual(5, Settings.MaxPursuitUnits, "MaxPursuitUnits should be 5");
        }

        private static void TestApplyPresetHighway()
        {
            Settings.ApplyPreset("Highway");

            Assert.AreEqual("Highway", Settings.Preset, "Preset name should be Highway");
            Assert.IsFalse(Settings.ChaosMode, "ChaosMode should be false for Highway");
            Assert.AreEqual(5, Settings.MaxPatrolUnits, "MaxPatrolUnits should be 5");
            Assert.AreEqual(3, Settings.MaxTrafficStops, "MaxTrafficStops should be 3");
            Assert.AreEqual(5, Settings.MaxStationaryUnits, "MaxStationaryUnits should be 5");
            Assert.AreEqual(2, Settings.MaxEmergencyRuns, "MaxEmergencyRuns should be 2");
            Assert.AreEqual(6, Settings.MaxPursuitUnits, "MaxPursuitUnits should be 6");
            Assert.AreEqual(80, Settings.HighwayPatrolRatio, "HighwayPatrolRatio should be 80");
        }

        private static void TestApplyPresetRural()
        {
            Settings.ApplyPreset("Rural");

            Assert.AreEqual("Rural", Settings.Preset, "Preset name should be Rural");
            Assert.IsFalse(Settings.ChaosMode, "ChaosMode should be false for Rural");
            Assert.AreEqual(2, Settings.MaxPatrolUnits, "MaxPatrolUnits should be 2");
            Assert.AreEqual(1, Settings.MaxTrafficStops, "MaxTrafficStops should be 1");
            Assert.AreEqual(1, Settings.MaxStationaryUnits, "MaxStationaryUnits should be 1");
            Assert.AreEqual(1, Settings.MaxEmergencyRuns, "MaxEmergencyRuns should be 1");
            Assert.AreEqual(3, Settings.MaxPursuitUnits, "MaxPursuitUnits should be 3");
            Assert.AreEqual(25, Settings.HighwayPatrolRatio, "HighwayPatrolRatio should be 25");
        }

        private static void TestApplyPresetWarzone()
        {
            Settings.ApplyPreset("Warzone");

            Assert.AreEqual("Warzone", Settings.Preset, "Preset name should be Warzone");
            Assert.IsTrue(Settings.ChaosMode, "ChaosMode should be true for Warzone");
            Assert.AreEqual(12, Settings.MaxPatrolUnits, "MaxPatrolUnits should be 12");
            Assert.AreEqual(6, Settings.MaxTrafficStops, "MaxTrafficStops should be 6");
            Assert.AreEqual(4, Settings.MaxStationaryUnits, "MaxStationaryUnits should be 4");
            Assert.AreEqual(6, Settings.MaxEmergencyRuns, "MaxEmergencyRuns should be 6");
            Assert.AreEqual(8, Settings.MaxPursuitUnits, "MaxPursuitUnits should be 8");
        }

        private static void TestApplyPresetNumbered()
        {
            Settings.ApplyPreset("1");
            Assert.AreEqual("Rural", Settings.Preset, "Preset 1 should map to Rural");

            Settings.ApplyPreset("2 - Authentic Realistic (Balanced)");
            Assert.AreEqual("Realistic", Settings.Preset, "Preset 2 label should map to Realistic");

            Settings.ApplyPreset("3");
            Assert.AreEqual("Metro", Settings.Preset, "Preset 3 should map to Metro");

            Settings.ApplyPreset("4");
            Assert.AreEqual("Highway", Settings.Preset, "Preset 4 should map to Highway");

            Settings.ApplyPreset("5");
            Assert.AreEqual("Chaos", Settings.Preset, "Preset 5 should map to Chaos");

            Settings.ApplyPreset("6 - Warzone Lockdown (Maximum Tactical)");
            Assert.AreEqual("Warzone", Settings.Preset, "Preset 6 label should map to Warzone");
        }

        private static void TestNewSettingsRoundtrip()
        {
            string tempIni = Path.Combine(Path.GetTempPath(), $"Cadence_Test_NewSettings_{Guid.NewGuid():N}.ini");
            try
            {
                // General & emergency service customizations
                Settings.MaxPoliceRuns = 3;
                Settings.MaxFireRuns = 2;
                Settings.MaxAmbulanceRuns = 2;
                Settings.EmergencyRunPoliceWeight = 2;
                Settings.EmergencyRunFireWeight = 1;
                Settings.EmergencyRunAmbulanceWeight = 1;
                Settings.EnableSirenToneVariation = true;
                Settings.AllowFallback = false;

                // Explicitly test distinct values for every radar blip property
                Settings.ShowRadarBlips = true;
                Settings.BlipScale = 0.52f;
                Settings.ShortRangeBlips = false;
                Settings.EnablePlayerVehicleBlip = true;
                Settings.PlayerVehicleBlipColor = 27;
                Settings.PlayerVehicleBlipScale = 0.38f;
                Settings.PlayerVehicleBlipSprite = 56;
                Settings.PlayerVehicleBlipName = "Personal Interceptor";

                Settings.PatrolBlipColor = 38;
                Settings.PatrolBlipScale = 0.45f;
                Settings.PatrolBlipSprite = 56;

                Settings.TrafficStopBlipColor = 4;
                Settings.TrafficStopBlipScale = 0.45f;
                Settings.TrafficStopBlipSprite = 56;

                Settings.StationaryBlipColor = 2;
                Settings.StationaryBlipScale = 0.45f;
                Settings.StationaryBlipSprite = 56;

                Settings.EmergencyBlipColor = 1;
                Settings.EmergencyBlipScale = 0.45f;
                Settings.EmergencyBlipSprite = 56;

                Settings.EmsBlipColor = 1;
                Settings.EmsBlipScale = 0.45f;
                Settings.EmsBlipSprite = 61;

                Settings.FireBlipColor = 1;
                Settings.FireBlipScale = 0.45f;
                Settings.FireBlipSprite = 60;

                Settings.PursuitBlipColor = 38;
                Settings.PursuitBlipScale = 0.45f;
                Settings.PursuitBlipSprite = 56;

                Settings.CalloutResponseBlipColor = 27;
                Settings.CalloutResponseBlipScale = 0.45f;
                Settings.CalloutResponseBlipSprite = 56;

                Settings.CombatBlipColor = 49;
                Settings.CombatBlipScale = 0.45f;
                Settings.CombatBlipSprite = 56;

                Settings.OfficerFootBlipColor = 38;
                Settings.OfficerFootBlipScale = 0.40f;
                Settings.OfficerFootBlipSprite = 58;

                Settings.ParkedCruiserBlipColor = 0;
                Settings.ParkedCruiserBlipScale = 0.45f;
                Settings.ParkedCruiserBlipSprite = 56;

                Settings.GroupBackupBlipColor = 4;
                Settings.GroupBackupBlipScale = 0.45f;
                Settings.GroupBackupBlipSprite = 56;

                Settings.GroupOfficerFootBlipColor = 4;
                Settings.GroupOfficerFootBlipScale = 0.40f;
                Settings.GroupOfficerFootBlipSprite = 58;

                Settings.BackupResponseBlipColor = 18;
                Settings.BackupResponseBlipScale = 0.45f;
                Settings.BackupResponseBlipSprite = 56;

                Settings.SuspectBlipColor = 1;
                Settings.SuspectBlipScale = 0.45f;
                Settings.SuspectBlipSprite = 1;

                Settings.Save(tempIni);
                Assert.IsTrue(File.Exists(tempIni));

                // Mutate all values away from saved state
                Settings.MaxPoliceRuns = 99;
                Settings.MaxFireRuns = 99;
                Settings.MaxAmbulanceRuns = 99;
                Settings.EmergencyRunPoliceWeight = 99;
                Settings.EmergencyRunFireWeight = 99;
                Settings.EmergencyRunAmbulanceWeight = 99;
                Settings.EnableSirenToneVariation = false;
                Settings.AllowFallback = true;

                Settings.ShowRadarBlips = false;
                Settings.BlipScale = 1.9f;
                Settings.ShortRangeBlips = true;
                Settings.EnablePlayerVehicleBlip = false;
                Settings.PlayerVehicleBlipColor = 99;
                Settings.PlayerVehicleBlipScale = 1.0f;
                Settings.PlayerVehicleBlipSprite = 99;
                Settings.PlayerVehicleBlipName = "Reset Name";

                Settings.PatrolBlipColor = 99;
                Settings.PatrolBlipScale = 1.0f;
                Settings.PatrolBlipSprite = 99;

                Settings.TrafficStopBlipColor = 99;
                Settings.TrafficStopBlipScale = 1.0f;
                Settings.TrafficStopBlipSprite = 99;

                Settings.StationaryBlipColor = 99;
                Settings.StationaryBlipScale = 1.0f;
                Settings.StationaryBlipSprite = 99;

                Settings.EmergencyBlipColor = 99;
                Settings.EmergencyBlipScale = 1.0f;
                Settings.EmergencyBlipSprite = 99;

                Settings.EmsBlipColor = 99;
                Settings.EmsBlipScale = 1.0f;
                Settings.EmsBlipSprite = 99;

                Settings.FireBlipColor = 99;
                Settings.FireBlipScale = 1.0f;
                Settings.FireBlipSprite = 99;

                Settings.PursuitBlipColor = 99;
                Settings.PursuitBlipScale = 1.0f;
                Settings.PursuitBlipSprite = 99;

                Settings.CalloutResponseBlipColor = 99;
                Settings.CalloutResponseBlipScale = 1.0f;
                Settings.CalloutResponseBlipSprite = 99;

                Settings.CombatBlipColor = 99;
                Settings.CombatBlipScale = 1.0f;
                Settings.CombatBlipSprite = 99;

                Settings.OfficerFootBlipColor = 99;
                Settings.OfficerFootBlipScale = 1.0f;
                Settings.OfficerFootBlipSprite = 99;

                Settings.ParkedCruiserBlipColor = 99;
                Settings.ParkedCruiserBlipScale = 1.0f;
                Settings.ParkedCruiserBlipSprite = 99;

                Settings.GroupBackupBlipColor = 99;
                Settings.GroupBackupBlipScale = 1.0f;
                Settings.GroupBackupBlipSprite = 99;

                Settings.GroupOfficerFootBlipColor = 99;
                Settings.GroupOfficerFootBlipScale = 1.0f;
                Settings.GroupOfficerFootBlipSprite = 99;

                Settings.BackupResponseBlipColor = 99;
                Settings.BackupResponseBlipScale = 1.0f;
                Settings.BackupResponseBlipSprite = 99;

                Settings.SuspectBlipColor = 99;
                Settings.SuspectBlipScale = 1.0f;
                Settings.SuspectBlipSprite = 99;

                Settings.Load(tempIni);

                Assert.AreEqual(3, Settings.MaxPoliceRuns, "MaxPoliceRuns should be 3");
                Assert.AreEqual(2, Settings.MaxFireRuns, "MaxFireRuns should be 2");
                Assert.AreEqual(2, Settings.MaxAmbulanceRuns, "MaxAmbulanceRuns should be 2");
                Assert.AreEqual(2, Settings.EmergencyRunPoliceWeight, "EmergencyRunPoliceWeight should be 2");
                Assert.AreEqual(1, Settings.EmergencyRunFireWeight, "EmergencyRunFireWeight should be 1");
                Assert.AreEqual(1, Settings.EmergencyRunAmbulanceWeight, "EmergencyRunAmbulanceWeight should be 1");
                Assert.IsTrue(Settings.EnableSirenToneVariation, "EnableSirenToneVariation should be true");
                Assert.IsFalse(Settings.AllowFallback, "AllowFallback should be false");

                // Full assertions for all Radar properties
                Assert.IsTrue(Settings.ShowRadarBlips, "ShowRadarBlips should match");
                Assert.AreEqual(0.52f, Settings.BlipScale, "BlipScale should match");
                Assert.IsFalse(Settings.ShortRangeBlips, "ShortRangeBlips should match");
                Assert.IsTrue(Settings.EnablePlayerVehicleBlip, "EnablePlayerVehicleBlip should match");
                Assert.AreEqual(27, Settings.PlayerVehicleBlipColor, "PlayerVehicleBlipColor should match");
                Assert.AreEqual(0.38f, Settings.PlayerVehicleBlipScale, "PlayerVehicleBlipScale should match");
                Assert.AreEqual(56, Settings.PlayerVehicleBlipSprite, "PlayerVehicleBlipSprite should match");
                Assert.AreEqual("Personal Interceptor", Settings.PlayerVehicleBlipName, "PlayerVehicleBlipName should match");

                Assert.AreEqual(38, Settings.PatrolBlipColor, "PatrolBlipColor should match");
                Assert.AreEqual(0.45f, Settings.PatrolBlipScale, "PatrolBlipScale should match");
                Assert.AreEqual(56, Settings.PatrolBlipSprite, "PatrolBlipSprite should match");

                Assert.AreEqual(4, Settings.TrafficStopBlipColor, "TrafficStopBlipColor should match");
                Assert.AreEqual(0.45f, Settings.TrafficStopBlipScale, "TrafficStopBlipScale should match");
                Assert.AreEqual(56, Settings.TrafficStopBlipSprite, "TrafficStopBlipSprite should match");

                Assert.AreEqual(2, Settings.StationaryBlipColor, "StationaryBlipColor should match");
                Assert.AreEqual(0.45f, Settings.StationaryBlipScale, "StationaryBlipScale should match");
                Assert.AreEqual(56, Settings.StationaryBlipSprite, "StationaryBlipSprite should match");

                Assert.AreEqual(1, Settings.EmergencyBlipColor, "EmergencyBlipColor should match");
                Assert.AreEqual(0.45f, Settings.EmergencyBlipScale, "EmergencyBlipScale should match");
                Assert.AreEqual(56, Settings.EmergencyBlipSprite, "EmergencyBlipSprite should match");

                Assert.AreEqual(61, Settings.EmsBlipSprite, "EmsBlipSprite should match");
                Assert.AreEqual(1, Settings.EmsBlipColor, "EmsBlipColor should match");
                Assert.AreEqual(0.45f, Settings.EmsBlipScale, "EmsBlipScale should match");

                Assert.AreEqual(60, Settings.FireBlipSprite, "FireBlipSprite should match");
                Assert.AreEqual(1, Settings.FireBlipColor, "FireBlipColor should match");
                Assert.AreEqual(0.45f, Settings.FireBlipScale, "FireBlipScale should match");

                Assert.AreEqual(38, Settings.PursuitBlipColor, "PursuitBlipColor should match");
                Assert.AreEqual(0.45f, Settings.PursuitBlipScale, "PursuitBlipScale should match");
                Assert.AreEqual(56, Settings.PursuitBlipSprite, "PursuitBlipSprite should match");

                Assert.AreEqual(27, Settings.CalloutResponseBlipColor, "CalloutResponseBlipColor should match");
                Assert.AreEqual(0.45f, Settings.CalloutResponseBlipScale, "CalloutResponseBlipScale should match");
                Assert.AreEqual(56, Settings.CalloutResponseBlipSprite, "CalloutResponseBlipSprite should match");

                Assert.AreEqual(49, Settings.CombatBlipColor, "CombatBlipColor should match");
                Assert.AreEqual(0.45f, Settings.CombatBlipScale, "CombatBlipScale should match");
                Assert.AreEqual(56, Settings.CombatBlipSprite, "CombatBlipSprite should match");

                Assert.AreEqual(38, Settings.OfficerFootBlipColor, "OfficerFootBlipColor should match");
                Assert.AreEqual(0.40f, Settings.OfficerFootBlipScale, "OfficerFootBlipScale should match");
                Assert.AreEqual(58, Settings.OfficerFootBlipSprite, "OfficerFootBlipSprite should match");

                Assert.AreEqual(0, Settings.ParkedCruiserBlipColor, "ParkedCruiserBlipColor should match");
                Assert.AreEqual(0.45f, Settings.ParkedCruiserBlipScale, "ParkedCruiserBlipScale should match");
                Assert.AreEqual(56, Settings.ParkedCruiserBlipSprite, "ParkedCruiserBlipSprite should match");

                Assert.AreEqual(4, Settings.GroupBackupBlipColor, "GroupBackupBlipColor should match");
                Assert.AreEqual(0.45f, Settings.GroupBackupBlipScale, "GroupBackupBlipScale should match");
                Assert.AreEqual(56, Settings.GroupBackupBlipSprite, "GroupBackupBlipSprite should match");

                Assert.AreEqual(4, Settings.GroupOfficerFootBlipColor, "GroupOfficerFootBlipColor should match");
                Assert.AreEqual(0.40f, Settings.GroupOfficerFootBlipScale, "GroupOfficerFootBlipScale should match");
                Assert.AreEqual(58, Settings.GroupOfficerFootBlipSprite, "GroupOfficerFootBlipSprite should match");

                Assert.AreEqual(18, Settings.BackupResponseBlipColor, "BackupResponseBlipColor should match");
                Assert.AreEqual(0.45f, Settings.BackupResponseBlipScale, "BackupResponseBlipScale should match");
                Assert.AreEqual(56, Settings.BackupResponseBlipSprite, "BackupResponseBlipSprite should match");

                Assert.AreEqual(1, Settings.SuspectBlipSprite, "SuspectBlipSprite should match");
                Assert.AreEqual(1, Settings.SuspectBlipColor, "SuspectBlipColor should match");
                Assert.AreEqual(0.45f, Settings.SuspectBlipScale, "SuspectBlipScale should match");
            }
            finally
            {
                if (File.Exists(tempIni)) File.Delete(tempIni);
            }
        }
    }
}
