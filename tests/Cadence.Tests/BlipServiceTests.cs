using System;
using Cadence.Core;
using Cadence.Services;

namespace Cadence.Tests
{
    public static class BlipServiceTests
    {
        public static void RunAll()
        {
            Console.WriteLine("\n[RUNNING] BlipServiceTests Suite");

            TestSanitizeCallsignSpecializedServiceTags();
            TestGetStateConfigAllStates();
            TestSetUnitStateHandlesNone();

            Console.WriteLine("[PASSED] All BlipServiceTests passed successfully.");
        }

        private static void TestSanitizeCallsignSpecializedServiceTags()
        {
            // Normal police callsigns
            Assert.AreEqual("1-LINCOLN-18", BlipService.SanitizeCallsign("1-LINCOLN-18"), "Plain callsign preserved");
            Assert.AreEqual("1-LINCOLN-18", BlipService.SanitizeCallsign("1-LINCOLN-18 [PATROL]"), "Status tag stripped");
            Assert.AreEqual("1-ADAM-12", BlipService.SanitizeCallsign("1-ADAM-12 [TRAFFIC STOP]"), "Status tag stripped");
            Assert.AreEqual("1-MARY-4", BlipService.SanitizeCallsign("1-MARY-4 [CODE 3]"), "Status tag stripped");

            // Specialized service tags [EMS] and [FIRE] preserved
            Assert.AreEqual("MEDIC-1 [EMS]", BlipService.SanitizeCallsign("MEDIC-1 [EMS]"), "Specialized service tag [EMS] must be preserved");
            Assert.AreEqual("ENGINE-4 [FIRE]", BlipService.SanitizeCallsign("ENGINE-4 [FIRE]"), "Specialized service tag [FIRE] must be preserved");
            Assert.AreEqual("AMBULANCE-2 [EMS]", BlipService.SanitizeCallsign("AMBULANCE-2 [EMS]"), "Specialized service tag [EMS] must be preserved");
            Assert.AreEqual("[EMS] MEDIC-1", BlipService.SanitizeCallsign("[EMS] MEDIC-1"), "Prefix tag [EMS] must be preserved");
            Assert.AreEqual("[FIRE] ENGINE-4", BlipService.SanitizeCallsign("[FIRE] ENGINE-4"), "Prefix tag [FIRE] must be preserved");

            // Combined service tag and status tag: service tag preserved, status tag stripped
            Assert.AreEqual("MEDIC-1 [EMS]", BlipService.SanitizeCallsign("MEDIC-1 [EMS] [CODE 3]"), "Transient status tag [CODE 3] stripped while [EMS] preserved");
            Assert.AreEqual("ENGINE-4 [FIRE]", BlipService.SanitizeCallsign("ENGINE-4 [FIRE] [CODE 3]"), "Transient status tag [CODE 3] stripped while [FIRE] preserved");
            Assert.AreEqual("MEDIC-1 [EMS]", BlipService.SanitizeCallsign("MEDIC-1 [EMS] [PATROL]"), "Transient status tag [PATROL] stripped while [EMS] preserved");

            // Edge cases
            Assert.AreEqual("Police", BlipService.SanitizeCallsign(null), "Null defaults to Police");
            Assert.AreEqual("Police", BlipService.SanitizeCallsign(""), "Empty defaults to Police");
            Assert.AreEqual("Police", BlipService.SanitizeCallsign("   "), "Whitespace defaults to Police");
            Assert.AreEqual("Police", BlipService.SanitizeCallsign("[CODE 3]"), "Sole transient status tag results in Police");
            Assert.AreEqual("[EMS]", BlipService.SanitizeCallsign("[EMS]"), "Sole service tag preserved");
            Assert.AreEqual("[FIRE]", BlipService.SanitizeCallsign("[FIRE]"), "Sole service tag preserved");
        }

        private static void TestGetStateConfigAllStates()
        {
            Settings.ResetToDefaults();

            // None
            var none = BlipService.GetStateConfig(EntityBlipState.None);
            Assert.AreEqual(0, none.Sprite, "None sprite");
            Assert.AreEqual(0, none.Color, "None color");
            Assert.AreEqual(string.Empty, none.Label, "None label");

            // Patrol
            var patrol = BlipService.GetStateConfig(EntityBlipState.Patrol);
            Assert.AreEqual(Settings.PatrolBlipSprite, patrol.Sprite, "Patrol sprite matches Settings");
            Assert.AreEqual(56, patrol.Sprite, "Patrol sprite default is 56");
            Assert.AreEqual(Settings.PatrolBlipColor, patrol.Color, "Patrol color matches Settings");
            Assert.AreEqual(Settings.PatrolBlipScale, patrol.Scale, "Patrol scale matches Settings");
            Assert.AreEqual("PATROL", patrol.Label, "Patrol label");

            // TrafficStop
            var stop = BlipService.GetStateConfig(EntityBlipState.TrafficStop);
            Assert.AreEqual(Settings.TrafficStopBlipSprite, stop.Sprite, "TrafficStop sprite matches Settings");
            Assert.AreEqual(Settings.TrafficStopBlipColor, stop.Color, "TrafficStop color matches Settings");
            Assert.AreEqual(Settings.TrafficStopBlipScale, stop.Scale, "TrafficStop scale matches Settings");
            Assert.AreEqual("TRAFFIC STOP", stop.Label, "TrafficStop label");

            // StationaryRadar
            var radar = BlipService.GetStateConfig(EntityBlipState.StationaryRadar);
            Assert.AreEqual(Settings.StationaryBlipSprite, radar.Sprite, "Radar sprite matches Settings");
            Assert.AreEqual(Settings.StationaryBlipColor, radar.Color, "Radar color matches Settings");
            Assert.AreEqual(Settings.StationaryBlipScale, radar.Scale, "Radar scale matches Settings");
            Assert.AreEqual("RADAR", radar.Label, "Radar label");

            // StationaryChilling
            var chill = BlipService.GetStateConfig(EntityBlipState.StationaryChilling);
            Assert.AreEqual(Settings.StationaryBlipSprite, chill.Sprite, "Chilling sprite matches Settings");
            Assert.AreEqual(Settings.StationaryBlipColor, chill.Color, "Chilling color matches Settings");
            Assert.AreEqual(Settings.StationaryBlipScale, chill.Scale, "Chilling scale matches Settings");
            Assert.AreEqual("CHILLING PATROL", chill.Label, "Chilling label");

            // EmergencyCode3
            var code3 = BlipService.GetStateConfig(EntityBlipState.EmergencyCode3);
            Assert.AreEqual(Settings.EmergencyBlipSprite, code3.Sprite, "EmergencyCode3 sprite matches Settings");
            Assert.AreEqual(Settings.EmergencyBlipColor, code3.Color, "EmergencyCode3 color matches Settings");
            Assert.AreEqual(Settings.EmergencyBlipScale, code3.Scale, "EmergencyCode3 scale matches Settings");
            Assert.AreEqual("CODE 3", code3.Label, "EmergencyCode3 label");

            // EmergencyEms
            var ems = BlipService.GetStateConfig(EntityBlipState.EmergencyEms);
            Assert.AreEqual(Settings.EmsBlipSprite, ems.Sprite, "EmergencyEms sprite matches Settings (61)");
            Assert.AreEqual(61, ems.Sprite, "EmergencyEms sprite default is 61");
            Assert.AreEqual(Settings.EmsBlipColor, ems.Color, "EmergencyEms color matches Settings (1)");
            Assert.AreEqual(Settings.EmsBlipScale, ems.Scale, "EmergencyEms scale matches Settings (0.45)");
            Assert.AreEqual("EMS", ems.Label, "EmergencyEms label");

            // EmergencyFire
            var fire = BlipService.GetStateConfig(EntityBlipState.EmergencyFire);
            Assert.AreEqual(Settings.FireBlipSprite, fire.Sprite, "EmergencyFire sprite matches Settings (60)");
            Assert.AreEqual(60, fire.Sprite, "EmergencyFire sprite default is 60");
            Assert.AreEqual(Settings.FireBlipColor, fire.Color, "EmergencyFire color matches Settings (1)");
            Assert.AreEqual(Settings.FireBlipScale, fire.Scale, "EmergencyFire scale matches Settings (0.45)");
            Assert.AreEqual("FIRE", fire.Label, "EmergencyFire label");

            // Pursuit
            var pursuit = BlipService.GetStateConfig(EntityBlipState.Pursuit);
            Assert.AreEqual(Settings.PursuitBlipSprite, pursuit.Sprite, "Pursuit sprite matches Settings");
            Assert.AreEqual(Settings.PursuitBlipColor, pursuit.Color, "Pursuit color matches Settings");
            Assert.AreEqual(Settings.PursuitBlipScale, pursuit.Scale, "Pursuit scale matches Settings");
            Assert.AreEqual("PURSUIT", pursuit.Label, "Pursuit label");

            // BackupCallout
            var callout = BlipService.GetStateConfig(EntityBlipState.BackupCallout);
            Assert.AreEqual(Settings.CalloutResponseBlipSprite, callout.Sprite, "BackupCallout sprite matches Settings");
            Assert.AreEqual(Settings.CalloutResponseBlipColor, callout.Color, "BackupCallout color matches Settings");
            Assert.AreEqual(Settings.CalloutResponseBlipScale, callout.Scale, "BackupCallout scale matches Settings");
            Assert.AreEqual("CALLOUT", callout.Label, "BackupCallout label");

            // BackupGroup
            var group = BlipService.GetStateConfig(EntityBlipState.BackupGroup);
            Assert.AreEqual(Settings.GroupBackupBlipSprite, group.Sprite, "BackupGroup sprite matches Settings");
            Assert.AreEqual(Settings.GroupBackupBlipColor, group.Color, "BackupGroup color matches Settings");
            Assert.AreEqual(Settings.GroupBackupBlipScale, group.Scale, "BackupGroup scale matches Settings");
            Assert.AreEqual("GROUP", group.Label, "BackupGroup label");

            // BackupMenu
            var menu = BlipService.GetStateConfig(EntityBlipState.BackupMenu);
            Assert.AreEqual(Settings.BackupResponseBlipSprite, menu.Sprite, "BackupMenu sprite matches Settings");
            Assert.AreEqual(Settings.BackupResponseBlipColor, menu.Color, "BackupMenu color matches Settings");
            Assert.AreEqual(Settings.BackupResponseBlipScale, menu.Scale, "BackupMenu scale matches Settings");
            Assert.AreEqual("BACKUP", menu.Label, "BackupMenu label");

            // CombatEngaging
            var combat = BlipService.GetStateConfig(EntityBlipState.CombatEngaging);
            Assert.AreEqual(Settings.CombatBlipSprite, combat.Sprite, "Combat sprite matches Settings");
            Assert.AreEqual(Settings.CombatBlipColor, combat.Color, "Combat color matches Settings");
            Assert.AreEqual(Settings.CombatBlipScale, combat.Scale, "Combat scale matches Settings");
            Assert.AreEqual("ENGAGING", combat.Label, "Combat label");

            // Suspect
            var suspect = BlipService.GetStateConfig(EntityBlipState.Suspect);
            Assert.AreEqual(Settings.SuspectBlipSprite, suspect.Sprite, "Suspect sprite matches Settings (1)");
            Assert.AreEqual(1, suspect.Sprite, "Suspect sprite default is 1");
            Assert.AreEqual(Settings.SuspectBlipColor, suspect.Color, "Suspect color matches Settings (1)");
            Assert.AreEqual(Settings.SuspectBlipScale, suspect.Scale, "Suspect scale matches Settings (0.45)");
            Assert.AreEqual("SUSPECT", suspect.Label, "Suspect label");

            // Parked
            var parked = BlipService.GetStateConfig(EntityBlipState.Parked);
            Assert.AreEqual(Settings.ParkedCruiserBlipSprite, parked.Sprite, "Parked sprite matches Settings");
            Assert.AreEqual(Settings.ParkedCruiserBlipColor, parked.Color, "Parked color matches Settings");
            Assert.AreEqual(Settings.ParkedCruiserBlipScale, parked.Scale, "Parked scale matches Settings");
            Assert.AreEqual("PARKED", parked.Label, "Parked label");
        }

        private static void TestSetUnitStateHandlesNone()
        {
            Settings.ResetToDefaults();

            // 1. Verify EntityBlipState.None config produces 0 sprite, 0 color, and empty label
            var noneConfig = BlipService.GetStateConfig(EntityBlipState.None);
            Assert.AreEqual(0, noneConfig.Sprite, "None config sprite must be 0");
            Assert.AreEqual(0, noneConfig.Color, "None config color must be 0");
            Assert.AreEqual(string.Empty, noneConfig.Label, "None config label must be empty string");

            // 2. Verify state suppression logic handles EntityBlipState.None cleanly
            Assert.IsTrue(BlipService.IsStateSuppressed(EntityBlipState.None), "EntityBlipState.None must be suppressed");
            Assert.IsFalse(BlipService.IsStateSuppressed(EntityBlipState.Patrol), "Active states must not be suppressed when ShowRadarBlips is true");

            // 3. Verify SetUnitState suppression when ShowRadarBlips is disabled
            Settings.ShowRadarBlips = false;
            Assert.IsTrue(BlipService.IsStateSuppressed(EntityBlipState.Patrol), "Patrol must be suppressed when ShowRadarBlips is false");
            Assert.IsTrue(BlipService.IsStateSuppressed(EntityBlipState.None), "None must be suppressed when ShowRadarBlips is false");

            Settings.ResetToDefaults();
        }
    }
}
