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

                // Save to file
                Settings.Save(tempIni);
                Assert.IsTrue(File.Exists(tempIni), "Saved file should exist on disk");

                // Clear memory settings by resetting to defaults
                Settings.ResetToDefaults();
                Assert.AreNotEqual(Keys.F11, Settings.MenuKey, "Settings should have reset");

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
                Settings.GroupBackupBlipColor = 4;
                Settings.GroupOfficerFootBlipColor = 4;
                Settings.BackupResponseBlipColor = 18;
                Settings.MaxPoliceRuns = 3;
                Settings.MaxFireRuns = 2;
                Settings.MaxAmbulanceRuns = 2;
                Settings.EmergencyRunPoliceWeight = 2;
                Settings.EmergencyRunFireWeight = 1;
                Settings.EmergencyRunAmbulanceWeight = 1;
                Settings.EnableSirenToneVariation = true;
                Settings.AllowFallback = false;

                Settings.Save(tempIni);
                Assert.IsTrue(File.Exists(tempIni));

                Settings.GroupBackupBlipColor = 99;
                Settings.BackupResponseBlipColor = 99;
                Settings.MaxPoliceRuns = 99;
                Settings.EnableSirenToneVariation = false;
                Settings.AllowFallback = true;

                Settings.Load(tempIni);

                Assert.AreEqual(4, Settings.GroupBackupBlipColor, "GroupBackupBlipColor should be 4");
                Assert.AreEqual(4, Settings.GroupOfficerFootBlipColor, "GroupOfficerFootBlipColor should be 4");
                Assert.AreEqual(18, Settings.BackupResponseBlipColor, "BackupResponseBlipColor should be 18");
                Assert.AreEqual(3, Settings.MaxPoliceRuns, "MaxPoliceRuns should be 3");
                Assert.AreEqual(2, Settings.MaxFireRuns, "MaxFireRuns should be 2");
                Assert.AreEqual(2, Settings.MaxAmbulanceRuns, "MaxAmbulanceRuns should be 2");
                Assert.AreEqual(2, Settings.EmergencyRunPoliceWeight, "EmergencyRunPoliceWeight should be 2");
                Assert.AreEqual(1, Settings.EmergencyRunFireWeight, "EmergencyRunFireWeight should be 1");
                Assert.AreEqual(1, Settings.EmergencyRunAmbulanceWeight, "EmergencyRunAmbulanceWeight should be 1");
                Assert.IsTrue(Settings.EnableSirenToneVariation, "EnableSirenToneVariation should be true");
                Assert.IsFalse(Settings.AllowFallback, "AllowFallback should be false");
            }
            finally
            {
                if (File.Exists(tempIni)) File.Delete(tempIni);
            }
        }
    }
}
