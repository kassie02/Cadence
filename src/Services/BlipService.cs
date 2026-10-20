using System;
using System.Text.RegularExpressions;
using Rage;
using Rage.Native;
using Cadence.Core;

namespace Cadence.Services
{
    public enum EntityBlipState
    {
        None,
        Patrol,
        TrafficStop,
        StationaryRadar,
        StationaryChilling,
        EmergencyCode3,
        EmergencyEms,
        EmergencyFire,
        Pursuit,
        BackupCallout,
        BackupGroup,
        BackupMenu,
        CombatEngaging,
        Suspect,
        Parked
    }

    public struct BlipStateConfig
    {
        public int Sprite { get; }
        public int Color { get; }
        public float Scale { get; }
        public string Label { get; }
        public bool IsShortRange { get; }
        public int FlashIntervalMs { get; }
        public bool AlternateFlash { get; }

        public BlipStateConfig(int sprite, int color, float scale, string label, bool isShortRange = false, int flashIntervalMs = 0, bool alternateFlash = false)
        {
            Sprite = sprite;
            Color = color;
            Scale = scale;
            Label = label;
            IsShortRange = isShortRange;
            FlashIntervalMs = flashIntervalMs;
            AlternateFlash = alternateFlash;
        }
    }

    public static class BlipService
    {
        // ====================================================================
        // The 4-Step Zero-Leak Destructor Protocol
        // ====================================================================

        /// <summary>
        /// Executes the ironclad 4-step destructor protocol to completely eliminate blip memory leaks:
        /// Step 1: Clear native flash engine state.
        /// Step 2: Stop RPH managed flash background fiber.
        /// Step 3: Delete blip from GTA radar memory pool.
        /// Step 4: Nullify reference to prevent dangling pointer reuse.
        /// </summary>
        public static void SafeDeleteBlip(ref Blip blip)
        {
            if (blip == null) return;

            try
            {
                if (blip.Exists())
                {
                    try { NativeFunction.Natives.SET_BLIP_FLASHES(blip, false); } catch { }
                    try { NativeFunction.Natives.SET_BLIP_FLASHES_ALTERNATE(blip, false); } catch { }
                    try { NativeFunction.Natives.SET_BLIP_FLASH_INTERVAL(blip, 0); } catch { }
                    try { NativeFunction.Natives.SET_BLIP_FLASH_TIMER(blip, 0); } catch { }
                    try { blip.StopFlashing(); } catch { }
                    blip.Delete();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"SafeDeleteBlip suppressed exception: {ex.Message}");
            }
            finally
            {
                blip = null;
            }
        }

        /// <summary>
        /// Safely finds and deletes any attached blip on an entity to prevent ghost / duplicate radar markers.
        /// </summary>
        public static void SafeDeleteEntityBlip(Entity entity)
        {
            if (entity == null || !entity.Exists()) return;
            try
            {
                Blip b = SafeGetAttachedBlip(entity);
                if (b != null)
                {
                    SafeDeleteBlip(ref b);
                }
            }
            catch { }
        }

        /// <summary>
        /// Safely retrieves any currently attached blip on an entity without throwing exceptions.
        /// </summary>
        public static Blip SafeGetAttachedBlip(Entity entity)
        {
            if (entity == null || !entity.Exists() || !entity.IsValid()) return null;

            try
            {
                return entity.GetAttachedBlip();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Checks whether the entity (or, if a ped, their current vehicle) has any active radar blip attached.
        /// Checks both RPH managed blips and native GTA V / LSPDFR blip handles (GET_BLIP_FROM_ENTITY).
        /// Prevents redundant duplicate watchdog blips on suspects and officers already marked by LSPDFR.
        /// </summary>
        public static bool HasAnyActiveRadarMarker(Entity entity)
        {
            if (entity == null || !entity.Exists() || !entity.IsValid()) return false;

            // 1. Check managed RPH blip attached by Cadence
            Blip b = SafeGetAttachedBlip(entity);
            if (b != null && b.Exists()) return true;

            // 2. Check native GTA V / LSPDFR blip handle directly on entity
            try
            {
                int blipHandle = NativeFunction.Natives.GET_BLIP_FROM_ENTITY<int>(entity);
                if (blipHandle != 0 && NativeFunction.Natives.DOES_BLIP_EXIST<bool>(blipHandle))
                {
                    return true;
                }
            }
            catch { }

            // 3. If ped, check current vehicle (both managed and native)
            if (entity is Ped ped)
            {
                try
                {
                    if (ped.IsInAnyVehicle(false) && ped.CurrentVehicle != null && ped.CurrentVehicle.Exists())
                    {
                        Blip vb = SafeGetAttachedBlip(ped.CurrentVehicle);
                        if (vb != null && vb.Exists()) return true;

                        int vehBlipHandle = NativeFunction.Natives.GET_BLIP_FROM_ENTITY<int>(ped.CurrentVehicle);
                        if (vehBlipHandle != 0 && NativeFunction.Natives.DOES_BLIP_EXIST<bool>(vehBlipHandle))
                        {
                            return true;
                        }
                    }
                }
                catch { }
            }

            return false;
        }

        // ====================================================================
        // Centralized EntityBlipState Architecture
        // ====================================================================

        /// <summary>
        /// Unified state mapping method that fetches the sprite, color, scale, and label
        /// directly from Settings for the given EntityBlipState.
        /// </summary>
        public static BlipStateConfig GetStateConfig(EntityBlipState state)
        {
            switch (state)
            {
                case EntityBlipState.Patrol:
                    return new BlipStateConfig(Settings.PatrolBlipSprite, Settings.PatrolBlipColor, Settings.PatrolBlipScale, "PATROL", Settings.ShortRangeBlips, 0, false);
                case EntityBlipState.TrafficStop:
                    return new BlipStateConfig(Settings.TrafficStopBlipSprite, Settings.TrafficStopBlipColor, Settings.TrafficStopBlipScale, "TRAFFIC STOP", Settings.ShortRangeBlips, 0, false);
                case EntityBlipState.StationaryRadar:
                    return new BlipStateConfig(Settings.StationaryBlipSprite, Settings.StationaryBlipColor, Settings.StationaryBlipScale, "RADAR", Settings.ShortRangeBlips, 0, false);
                case EntityBlipState.StationaryChilling:
                    return new BlipStateConfig(Settings.StationaryBlipSprite, Settings.StationaryBlipColor, Settings.StationaryBlipScale, "CHILLING PATROL", Settings.ShortRangeBlips, 0, false);
                case EntityBlipState.EmergencyCode3:
                    return new BlipStateConfig(Settings.EmergencyBlipSprite, Settings.EmergencyBlipColor, Settings.EmergencyBlipScale, "CODE 3", false, 600, false);
                case EntityBlipState.EmergencyEms:
                    return new BlipStateConfig(Settings.EmsBlipSprite, Settings.EmsBlipColor, Settings.EmsBlipScale, "EMS", false, 600, false);
                case EntityBlipState.EmergencyFire:
                    return new BlipStateConfig(Settings.FireBlipSprite, Settings.FireBlipColor, Settings.FireBlipScale, "FIRE", false, 600, false);
                case EntityBlipState.Pursuit:
                    return new BlipStateConfig(Settings.PursuitBlipSprite, Settings.PursuitBlipColor, Settings.PursuitBlipScale, "PURSUIT", false, 400, false);
                case EntityBlipState.BackupCallout:
                    return new BlipStateConfig(Settings.CalloutResponseBlipSprite, Settings.CalloutResponseBlipColor, Settings.CalloutResponseBlipScale, "CALLOUT", false, 600, false);
                case EntityBlipState.BackupGroup:
                    return new BlipStateConfig(Settings.GroupBackupBlipSprite, Settings.GroupBackupBlipColor, Settings.GroupBackupBlipScale, "GROUP", false, 400, false);
                case EntityBlipState.BackupMenu:
                    return new BlipStateConfig(Settings.BackupResponseBlipSprite, Settings.BackupResponseBlipColor, Settings.BackupResponseBlipScale, "BACKUP", false, 500, false);
                case EntityBlipState.CombatEngaging:
                    return new BlipStateConfig(Settings.CombatBlipSprite, Settings.CombatBlipColor, Settings.CombatBlipScale, "ENGAGING", false, 300, false);
                case EntityBlipState.Suspect:
                    return new BlipStateConfig(Settings.SuspectBlipSprite, Settings.SuspectBlipColor, Settings.SuspectBlipScale, "SUSPECT", false, 0, false);
                case EntityBlipState.Parked:
                    return new BlipStateConfig(Settings.ParkedCruiserBlipSprite, Settings.ParkedCruiserBlipColor, Settings.ParkedCruiserBlipScale, "PARKED", Settings.ShortRangeBlips, 0, false);
                case EntityBlipState.None:
                default:
                    return new BlipStateConfig(0, 0, Settings.BlipScale, string.Empty, false, 0, false);
            }
        }

        /// <summary>
        /// Evaluates whether blips are suppressed for the specified state or configuration.
        /// Returns true if radar blips are globally disabled or if the state is EntityBlipState.None.
        /// </summary>
        public static bool IsStateSuppressed(EntityBlipState state)
        {
            return !Settings.ShowRadarBlips || state == EntityBlipState.None;
        }

        /// <summary>
        /// Unified registration and declaration method for vehicle radar blips.
        /// Resolves configuration directly from Settings via GetStateConfig and safely manages blip lifecycle.
        /// </summary>
        public static Blip SetUnitState(Vehicle vehicle, EntityBlipState state, string callsign)
        {
            if (vehicle == null || !vehicle.Exists() || !vehicle.IsValid())
            {
                return null;
            }

            if (IsStateSuppressed(state))
            {
                SafeDeleteEntityBlip(vehicle);
                return null;
            }

            if (state == EntityBlipState.Suspect)
            {
                Blip attached = SafeGetAttachedBlip(vehicle);
                if (attached == null && HasAnyActiveRadarMarker(vehicle))
                {
                    return null;
                }
            }

            BlipStateConfig cfg = GetStateConfig(state);
            return CreateOrUpdateBlip(
                vehicle,
                callsign,
                cfg.Label,
                spriteId: cfg.Sprite,
                colorId: cfg.Color,
                isShortRange: cfg.IsShortRange,
                flashIntervalMs: cfg.FlashIntervalMs,
                alternateFlash: cfg.AlternateFlash,
                scaleOverride: cfg.Scale
            );
        }

        // ====================================================================
        // Universal Blip Attachment Engine
        // ====================================================================

        /// <summary>
        /// Cruising Patrol Unit: Configurable from INI (Default: Sprite 56, Color 38 Navy Blue, Solid, {Callsign} [PATROL]).
        /// </summary>
        public static Blip AttachCruisingBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "PATROL", spriteId: Settings.PatrolBlipSprite, colorId: Settings.PatrolBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.PatrolBlipScale);
        }

        /// <summary>
        /// Alias for AttachCruisingBlip.
        /// </summary>
        public static Blip AttachPatrolBlip(Vehicle vehicle, string callsign) => AttachCruisingBlip(vehicle, callsign);

        /// <summary>
        /// Roadside Traffic Stop: Configurable from INI (Default: Sprite 56, Color 4 Yellow, Solid, {Callsign} [TRAFFIC STOP]).
        /// </summary>
        public static Blip AttachTrafficStopBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "TRAFFIC STOP", spriteId: Settings.TrafficStopBlipSprite, colorId: Settings.TrafficStopBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.TrafficStopBlipScale);
        }

        /// <summary>
        /// Stationary / Radar Trap: Configurable from INI (Default: Sprite 56, Color 2 Mint Green, Solid, {Callsign} [RADAR]).
        /// </summary>
        public static Blip AttachRadarWatchBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "RADAR", spriteId: Settings.StationaryBlipSprite, colorId: Settings.StationaryBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.StationaryBlipScale);
        }

        /// <summary>
        /// Standing Ambient / Chilling Officer: Configurable from INI (Default: Sprite 56, Color 2 Mint Green, Solid, {Callsign} [CHILLING PATROL]).
        /// </summary>
        public static Blip AttachChillingBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "CHILLING PATROL", spriteId: Settings.StationaryBlipSprite, colorId: Settings.StationaryBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.StationaryBlipScale);
        }

        /// <summary>
        /// Stationary / Standby Unit (Generic): Configurable from INI (Default: Sprite 56, Color 2 Mint Green, Solid, {Callsign} [STANDBY]).
        /// </summary>
        public static Blip AttachStationaryBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "STANDBY", spriteId: Settings.StationaryBlipSprite, colorId: Settings.StationaryBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.StationaryBlipScale);
        }

        /// <summary>
        /// Alias for AttachStationaryBlip.
        /// </summary>
        public static Blip AttachStandbyBlip(Vehicle vehicle, string callsign) => AttachStationaryBlip(vehicle, callsign);

        /// <summary>
        /// Emergency Run / Declined Call Takeover: Configurable from INI (Default: Sprite 56, Color 1 Red, Flashing 600ms, {Callsign} [CODE 3]).
        /// </summary>
        public static Blip AttachEmergencyBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "CODE 3", spriteId: Settings.EmergencyBlipSprite, colorId: Settings.EmergencyBlipColor, isShortRange: false, flashIntervalMs: 600, alternateFlash: false, scaleOverride: Settings.EmergencyBlipScale);
        }

        /// <summary>
        /// Active Pursuit Assist: Native Red & Blue alternating flashing police pursuit blip.
        /// Configurable from INI (Default: Sprite 56, Color 38 Navy Blue, Alternate Flashing 400ms Red/Blue).
        /// Replaces the static orange blip with authentic GTA V / LSPDFR alternating emergency police markers.
        /// </summary>
        public static Blip AttachPursuitBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "PURSUIT", spriteId: Settings.PursuitBlipSprite, colorId: Settings.PursuitBlipColor, isShortRange: false, flashIntervalMs: 400, alternateFlash: false, scaleOverride: Settings.PursuitBlipScale);
        }

        /// <summary>
        /// Group Backup Unit: Configurable from INI (Default: Sprite 56, Color 4 Yellow, Flashing 400ms, {Callsign} [GROUP]).
        /// </summary>
        public static Blip AttachGroupBackupBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "GROUP", spriteId: Settings.GroupBackupBlipSprite, colorId: Settings.GroupBackupBlipColor, isShortRange: false, flashIntervalMs: 400, alternateFlash: false, scaleOverride: Settings.GroupBackupBlipScale);
        }

        /// <summary>
        /// Lean Backup Menu Response: Configurable from INI (Default: Sprite 56, Color 18 Cyan/Light Blue, Flashing 500ms, {Callsign} [BACKUP]).
        /// </summary>
        public static Blip AttachBackupResponseBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "BACKUP", spriteId: Settings.BackupResponseBlipSprite, colorId: Settings.BackupResponseBlipColor, isShortRange: false, flashIntervalMs: 500, alternateFlash: false, scaleOverride: Settings.BackupResponseBlipScale);
        }

        /// <summary>
        /// Callout Waypoint Response: Configurable from INI (Default: Sprite 56 PoliceCar, Color 27 Purple, Flashing 600ms, {Callsign} [CALLOUT]).
        /// </summary>
        public static Blip AttachCalloutResponseBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "CALLOUT", spriteId: Settings.CalloutResponseBlipSprite, colorId: Settings.CalloutResponseBlipColor, isShortRange: false, flashIntervalMs: 600, alternateFlash: false, scaleOverride: Settings.CalloutResponseBlipScale);
        }

        /// <summary>
        /// Threat Reaction / Combat: Configurable from INI (Default: Sprite 56, Color 49 Dark Orange, Flashing 300ms, {Callsign} [ENGAGING]).
        /// </summary>
        public static Blip AttachThreatBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "ENGAGING", spriteId: Settings.CombatBlipSprite, colorId: Settings.CombatBlipColor, isShortRange: false, flashIntervalMs: 300, alternateFlash: false, scaleOverride: Settings.CombatBlipScale);
        }

        /// <summary>
        /// Alias for AttachThreatBlip.
        /// </summary>
        public static Blip AttachCombatBlip(Vehicle vehicle, string callsign) => AttachThreatBlip(vehicle, callsign);

        /// <summary>
        /// Dismounted Officer Ped: Configurable from INI (Default: Sprite 58 Badge, Color 38 Navy Blue, Solid, {Callsign} [ON FOOT]).
        /// </summary>
        public static Blip AttachOfficerFootBlip(Ped officer, string callsign)
        {
            if (!Settings.ShowRadarBlips || officer == null || !officer.Exists()) return null;

            try
            {
                Blip blip = SafeGetAttachedBlip(officer);
                if (blip == null || !blip.Exists())
                {
                    blip = officer.AttachBlip();
                }
                if (blip == null || !blip.Exists()) return null;

                blip.Sprite = (BlipSprite)Settings.OfficerFootBlipSprite;
                NativeFunction.Natives.SET_BLIP_COLOUR(blip, Settings.OfficerFootBlipColor);
                blip.Scale = Settings.OfficerFootBlipScale;
                NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE(blip, Settings.ShortRangeBlips);
                NativeFunction.Natives.SET_BLIP_FLASHES(blip, false);
                NativeFunction.Natives.SET_BLIP_FLASHES_ALTERNATE(blip, false);
                NativeFunction.Natives.SET_BLIP_FLASH_INTERVAL(blip, 0);
                NativeFunction.Natives.SET_BLIP_FLASH_TIMER(blip, 0);
                try { blip.StopFlashing(); } catch { }

                string tag = SanitizeCallsign(callsign);
                blip.Name = $"{tag} [ON FOOT]";
                return blip;
            }
            catch (Exception ex)
            {
                Logger.Debug($"AttachOfficerFootBlip error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Dismounted Group Backup Officer Ped: Configurable from INI (Default: Sprite 58 Badge, Color 4 Yellow, Solid, {Callsign} [GROUP ON FOOT]).
        /// </summary>
        public static Blip AttachGroupOfficerFootBlip(Ped officer, string callsign)
        {
            if (!Settings.ShowRadarBlips || officer == null || !officer.Exists()) return null;

            try
            {
                Blip blip = SafeGetAttachedBlip(officer);
                if (blip == null || !blip.Exists())
                {
                    blip = officer.AttachBlip();
                }
                if (blip == null || !blip.Exists()) return null;

                blip.Sprite = (BlipSprite)Settings.GroupOfficerFootBlipSprite;
                NativeFunction.Natives.SET_BLIP_COLOUR(blip, Settings.GroupOfficerFootBlipColor);
                blip.Scale = Settings.GroupOfficerFootBlipScale;
                NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE(blip, Settings.ShortRangeBlips);
                NativeFunction.Natives.SET_BLIP_FLASHES(blip, false);
                NativeFunction.Natives.SET_BLIP_FLASHES_ALTERNATE(blip, false);
                NativeFunction.Natives.SET_BLIP_FLASH_INTERVAL(blip, 0);
                NativeFunction.Natives.SET_BLIP_FLASH_TIMER(blip, 0);
                try { blip.StopFlashing(); } catch { }

                string tag = SanitizeCallsign(callsign);
                blip.Name = $"{tag} [GROUP ON FOOT]";
                return blip;
            }
            catch (Exception ex)
            {
                Logger.Debug($"AttachGroupOfficerFootBlip error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parked Cruiser (dismounted / empty): Configurable from INI (Default: Sprite 56, Color 0 White, Solid, {Callsign} [PARKED]).
        /// </summary>
        public static Blip AttachParkedCruiserBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "PARKED", spriteId: Settings.ParkedCruiserBlipSprite, colorId: Settings.ParkedCruiserBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.ParkedCruiserBlipScale);
        }

        /// <summary>
        /// Group Officer Dismount Protocol:
        /// Empty cruiser turns White (Color 0, Sprite 56, [PARKED]).
        /// Dismounted group officer ped receives Yellow Badge blip (Sprite 58, Color 4 Yellow, [GROUP ON FOOT]).
        /// </summary>
        public static void HandleGroupOfficerDismount(Vehicle cruiser, Ped officer, ref Blip cruiserBlip, ref Blip officerBadgeBlip, string callsign)
        {
            try
            {
                SafeDeleteBlip(ref cruiserBlip);
                if (cruiser != null && cruiser.Exists())
                {
                    cruiserBlip = AttachParkedCruiserBlip(cruiser, callsign);
                }

                if (officer != null && officer.Exists())
                {
                    SafeDeleteBlip(ref officerBadgeBlip);
                    officerBadgeBlip = AttachGroupOfficerFootBlip(officer, callsign);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"HandleGroupOfficerDismount error: {ex.Message}");
            }
        }

        /// <summary>
        /// Officer Dismount Protocol:
        /// Empty cruiser turns White (Color 0, Sprite 56, [PARKED]).
        /// Dismounted officer ped receives Badge blip (Sprite 58, Color 38, [ON FOOT]).
        /// </summary>
        public static void HandleOfficerDismount(Vehicle cruiser, Ped officer, ref Blip cruiserBlip, ref Blip officerBadgeBlip, string callsign)
        {
            try
            {
                SafeDeleteBlip(ref cruiserBlip);
                if (cruiser != null && cruiser.Exists())
                {
                    cruiserBlip = AttachParkedCruiserBlip(cruiser, callsign);
                }

                if (officer != null && officer.Exists())
                {
                    SafeDeleteBlip(ref officerBadgeBlip);
                    officerBadgeBlip = AttachOfficerFootBlip(officer, callsign);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"HandleOfficerDismount error: {ex.Message}");
            }
        }

        /// <summary>
        /// Officer Remount Protocol:
        /// Officer badge blip is cleanly destroyed.
        /// Cruiser blip is cleanly recreated in designated state (e.g. [PATROL] Color 38 Navy Blue)
        /// with zero inherited flash timers or sticky animation loops.
        /// Honors stateLabel and stateColor parameters [E7].
        /// </summary>
        public static void HandleOfficerRemount(Vehicle cruiser, Ped officer, ref Blip cruiserBlip, ref Blip officerBadgeBlip, string callsign, string stateLabel = "PATROL", int stateColor = 38)
        {
            try
            {
                SafeDeleteBlip(ref officerBadgeBlip);
                if (officer != null && officer.Exists())
                {
                    SafeDeleteEntityBlip(officer);
                }

                SafeDeleteBlip(ref cruiserBlip);
                if (cruiser != null && cruiser.Exists())
                {
                    cruiserBlip = CreateOrUpdateBlip(cruiser, callsign, stateLabel, spriteId: Settings.PatrolBlipSprite, colorId: stateColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.PatrolBlipScale);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"HandleOfficerRemount error: {ex.Message}");
            }
        }

        /// <summary>
        /// Active Pursuit Suspect Fallback:
        /// Provides an authentic fallback blip (Sprite 1 / Destination, Color 1 Red, 0.45f scale)
        /// if LSPDFR has not already marked the suspect. Checks HasAnyActiveRadarMarker to prevent duplicates [E5].
        /// </summary>
        public static Blip AttachSuspectBlip(Entity entity, string label = "SUSPECT")
        {
            if (!Settings.ShowRadarBlips || entity == null || !entity.Exists() || !entity.IsValid())
            {
                return null;
            }

            if (HasAnyActiveRadarMarker(entity))
            {
                return null;
            }

            try
            {
                Blip blip = SafeGetAttachedBlip(entity);
                if (blip == null || !blip.Exists())
                {
                    blip = entity.AttachBlip();
                }
                if (blip == null || !blip.Exists()) return null;

                blip.Sprite = (BlipSprite)Settings.SuspectBlipSprite;
                NativeFunction.Natives.SET_BLIP_COLOUR(blip, Settings.SuspectBlipColor);
                blip.Scale = Settings.SuspectBlipScale;
                NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE(blip, false);
                NativeFunction.Natives.SET_BLIP_FLASHES(blip, false);
                NativeFunction.Natives.SET_BLIP_FLASHES_ALTERNATE(blip, false);
                NativeFunction.Natives.SET_BLIP_FLASH_INTERVAL(blip, 0);
                NativeFunction.Natives.SET_BLIP_FLASH_TIMER(blip, 0);
                try { blip.StopFlashing(); } catch { }

                string tag = string.IsNullOrWhiteSpace(label) ? "SUSPECT" : label.Trim();
                blip.Name = tag;
                return blip;
            }
            catch (Exception ex)
            {
                Logger.Debug($"AttachSuspectBlip error: {ex.Message}");
                return null;
            }
        }

        public static Blip AttachSuspectBlip(Ped suspect, string label = "SUSPECT") => AttachSuspectBlip((Entity)suspect, label);

        /// <summary>
        /// EMS / Ambulance Unit: Sprite 61 (Ambulance), Color 1 Red, Flashing 600ms, {Callsign} [EMS] [E17].
        /// </summary>
        public static Blip AttachEmsBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "EMS", spriteId: Settings.EmsBlipSprite, colorId: Settings.EmsBlipColor, isShortRange: false, flashIntervalMs: 600, alternateFlash: false, scaleOverride: Settings.EmsBlipScale);
        }

        /// <summary>
        /// Fire Truck Unit: Sprite 60 (Firetruck), Color 1 Red, Flashing 600ms, {Callsign} [FIRE] [E17].
        /// </summary>
        public static Blip AttachFireBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "FIRE", spriteId: Settings.FireBlipSprite, colorId: Settings.FireBlipColor, isShortRange: false, flashIntervalMs: 600, alternateFlash: false, scaleOverride: Settings.FireBlipScale);
        }

        // ====================================================================
        // Dynamic State Transitions
        // ====================================================================

        public static void TransitionToPatrol(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "PATROL", spriteId: Settings.PatrolBlipSprite, colorId: Settings.PatrolBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.PatrolBlipScale);
        }

        public static void TransitionToTrafficStop(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "TRAFFIC STOP", spriteId: Settings.TrafficStopBlipSprite, colorId: Settings.TrafficStopBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.TrafficStopBlipScale);
        }

        public static void TransitionToRadar(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "RADAR", spriteId: Settings.StationaryBlipSprite, colorId: Settings.StationaryBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.StationaryBlipScale);
        }

        public static void TransitionToChilling(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "CHILLING PATROL", spriteId: Settings.StationaryBlipSprite, colorId: Settings.StationaryBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.StationaryBlipScale);
        }

        public static void TransitionToStationary(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "STANDBY", spriteId: Settings.StationaryBlipSprite, colorId: Settings.StationaryBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.StationaryBlipScale);
        }

        public static void TransitionToEmergency(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "CODE 3", spriteId: Settings.EmergencyBlipSprite, colorId: Settings.EmergencyBlipColor, isShortRange: false, flashIntervalMs: 600, alternateFlash: false, scaleOverride: Settings.EmergencyBlipScale);
        }

        public static void TransitionToEms(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "EMS", spriteId: Settings.EmsBlipSprite, colorId: Settings.EmsBlipColor, isShortRange: false, flashIntervalMs: 600, alternateFlash: false, scaleOverride: Settings.EmsBlipScale);
        }

        public static void TransitionToFire(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "FIRE", spriteId: Settings.FireBlipSprite, colorId: Settings.FireBlipColor, isShortRange: false, flashIntervalMs: 600, alternateFlash: false, scaleOverride: Settings.FireBlipScale);
        }

        public static void TransitionToPursuit(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "PURSUIT", spriteId: Settings.PursuitBlipSprite, colorId: Settings.PursuitBlipColor, isShortRange: false, flashIntervalMs: 400, alternateFlash: false, scaleOverride: Settings.PursuitBlipScale);
        }

        public static void TransitionToCalloutResponse(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "CALLOUT", spriteId: Settings.CalloutResponseBlipSprite, colorId: Settings.CalloutResponseBlipColor, isShortRange: false, flashIntervalMs: 600, alternateFlash: false, scaleOverride: Settings.CalloutResponseBlipScale);
        }

        public static void TransitionToThreat(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "ENGAGING", spriteId: Settings.CombatBlipSprite, colorId: Settings.CombatBlipColor, isShortRange: false, flashIntervalMs: 300, alternateFlash: false, scaleOverride: Settings.CombatBlipScale);
        }

        public static void TransitionToCombat(Blip blip, string callsign) => TransitionToThreat(blip, callsign);
        public static void TransitionToParked(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "PARKED", spriteId: Settings.ParkedCruiserBlipSprite, colorId: Settings.ParkedCruiserBlipColor, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0, alternateFlash: false, scaleOverride: Settings.ParkedCruiserBlipScale);
        }

        public static void TransitionToEmptyCruiser(Blip blip, string callsign) => TransitionToParked(blip, callsign);

        // ====================================================================
        // Player Personal Vehicle Blip Management
        // ====================================================================

        private static readonly object _playerVehicleLock = new object();
        private static GameFiber _playerVehicleFiber;
        private static volatile bool _playerVehicleMonitoringActive;
        private static Blip _playerVehicleBlip;
        private static Vehicle _lastPlayerVehicle;

        public static void StartPlayerVehicleMonitoring()
        {
            lock (_playerVehicleLock)
            {
                if (_playerVehicleMonitoringActive) return;
                _playerVehicleMonitoringActive = true;
                _playerVehicleFiber = GameFiber.StartNew(PlayerVehicleMonitoringLoop, "Cadence - Player Vehicle Blip Fiber");
            }
        }

        public static void StopPlayerVehicleMonitoring()
        {
            lock (_playerVehicleLock)
            {
                if (!_playerVehicleMonitoringActive && _playerVehicleFiber == null) return;
                _playerVehicleMonitoringActive = false;

                if (_playerVehicleFiber != null && _playerVehicleFiber.IsAlive)
                {
                    int waited = 0;
                    while (_playerVehicleFiber.IsAlive && waited < 200)
                    {
                        GameFiber.Sleep(20);
                        waited += 20;
                    }
                    if (_playerVehicleFiber.IsAlive)
                    {
                        try { _playerVehicleFiber.Abort(); } catch { }
                    }
                }
                _playerVehicleFiber = null;

                SafeDeleteBlip(ref _playerVehicleBlip);
                _lastPlayerVehicle = null;
            }
        }

        private static void PlayerVehicleMonitoringLoop()
        {
            while (_playerVehicleMonitoringActive)
            {
                try
                {
                    UpdatePlayerVehicleBlip();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"PlayerVehicleMonitoringLoop error: {ex.Message}");
                }

                GameFiber.Sleep(300);
            }
        }

        public static void UpdatePlayerVehicleBlip()
        {
            if (!Settings.EnablePlugin || !Settings.ShowRadarBlips || !Settings.EnablePlayerVehicleBlip)
            {
                SafeDeleteBlip(ref _playerVehicleBlip);
                return;
            }

            Ped player = Game.LocalPlayer?.Character;
            if (player == null || !player.Exists() || !player.IsAlive)
            {
                return;
            }

            // Track player's current vehicle
            if (player.IsInAnyVehicle(false) && player.CurrentVehicle != null && player.CurrentVehicle.Exists())
            {
                _lastPlayerVehicle = player.CurrentVehicle;
                if (_playerVehicleBlip != null && _playerVehicleBlip.Exists())
                {
                    SafeDeleteBlip(ref _playerVehicleBlip);
                }
                return;
            }

            // Player is outside on foot: target is their personal cruiser
            Vehicle targetVeh = _lastPlayerVehicle;
            if (targetVeh == null || !targetVeh.Exists() || !targetVeh.IsDriveable)
            {
                targetVeh = player.LastVehicle;
            }

            if (targetVeh == null || !targetVeh.Exists() || !targetVeh.IsDriveable)
            {
                SafeDeleteBlip(ref _playerVehicleBlip);
                return;
            }

            float customScale = Settings.PlayerVehicleBlipScale;
            int customColor = Settings.PlayerVehicleBlipColor;
            int customSprite = Settings.PlayerVehicleBlipSprite;

            // 1. If an external blip (e.g. LSPDFR native marker) already exists on the vehicle,
            // do NOT mutate native blip handles in-place to avoid corrupting LSPDFR state [E4].
            int nativeHandle = 0;
            try { nativeHandle = NativeFunction.Natives.GET_BLIP_FROM_ENTITY<int>(targetVeh); } catch { }

            uint ownHandle = (_playerVehicleBlip != null && _playerVehicleBlip.Exists()) ? _playerVehicleBlip.Handle.Value : 0;

            if (nativeHandle != 0 && (uint)nativeHandle != ownHandle && NativeFunction.Natives.DOES_BLIP_EXIST<bool>(nativeHandle))
            {
                SafeDeleteBlip(ref _playerVehicleBlip);
                return;
            }

            // 2. If no external blip exists, create and maintain Cadence's dedicated player cruiser blip
            if (_playerVehicleBlip == null || !_playerVehicleBlip.Exists() || _playerVehicleBlip.Entity != targetVeh)
            {
                SafeDeleteBlip(ref _playerVehicleBlip);
                _playerVehicleBlip = targetVeh.AttachBlip();
            }

            if (_playerVehicleBlip != null && _playerVehicleBlip.Exists())
            {
                _playerVehicleBlip.Sprite = (BlipSprite)customSprite;
                NativeFunction.Natives.SET_BLIP_COLOUR(_playerVehicleBlip, customColor);
                _playerVehicleBlip.Scale = customScale;
                NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE(_playerVehicleBlip, false);
                _playerVehicleBlip.Name = string.IsNullOrWhiteSpace(Settings.PlayerVehicleBlipName) ? "Personal Cruiser" : Settings.PlayerVehicleBlipName;
            }
        }

        // ====================================================================
        // Internal Helpers
        // ====================================================================

        private static Blip CreateOrUpdateBlip(Entity entity, string callsign, string suffix, int spriteId, int colorId, bool isShortRange, int flashIntervalMs, bool alternateFlash = false, float? scaleOverride = null)
        {
            if (!Settings.ShowRadarBlips || entity == null || !entity.Exists() || !entity.IsValid())
            {
                return null;
            }

            try
            {
                Blip blip = SafeGetAttachedBlip(entity);
                if (blip != null && blip.Exists())
                {
                    // In GTA V native blip engine, once a blip has been set to flash (e.g. Code 3 / Callout),
                    // SET_BLIP_FLASHES(blip, false) fails to clear the flashing state on the existing blip handle.
                    // If target state is solid (flashIntervalMs == 0) and the blip is actively flashing, we cleanly destroy
                    // the old blip and attach a fresh one to guarantee an authentic, solid, non-flashing radar icon.
                    if (flashIntervalMs == 0)
                    {
                        bool isFlashing = false;
                        try { isFlashing = NativeFunction.Natives.IS_BLIP_FLASHING<bool>(blip); } catch { }
                        if (isFlashing)
                        {
                            SafeDeleteBlip(ref blip);
                        }
                    }
                }

                if (blip == null || !blip.Exists())
                {
                    blip = entity.AttachBlip();
                }
                if (blip == null || !blip.Exists()) return null;

                ApplyBlipState(blip, callsign, suffix, spriteId, colorId, isShortRange, flashIntervalMs, alternateFlash, scaleOverride);
                return blip;
            }
            catch (Exception ex)
            {
                Logger.Debug($"CreateOrUpdateBlip error: {ex.Message}");
                return null;
            }
        }

        private static void ApplyBlipState(Blip blip, string callsign, string suffix, int spriteId, int colorId, bool isShortRange, int flashIntervalMs, bool alternateFlash = false, float? scaleOverride = null)
        {
            if (blip == null || !blip.Exists()) return;

            try
            {
                blip.Sprite = (BlipSprite)spriteId;
                NativeFunction.Natives.SET_BLIP_COLOUR(blip, colorId);
                blip.Scale = scaleOverride ?? Settings.BlipScale;
                NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE(blip, isShortRange);

                if (flashIntervalMs > 0)
                {
                    NativeFunction.Natives.SET_BLIP_FLASHES(blip, true);
                    bool canAlternate = alternateFlash;
                    NativeFunction.Natives.SET_BLIP_FLASHES_ALTERNATE(blip, canAlternate);
                    NativeFunction.Natives.SET_BLIP_FLASH_INTERVAL(blip, flashIntervalMs);
                }
                else
                {
                    NativeFunction.Natives.SET_BLIP_FLASHES(blip, false);
                    NativeFunction.Natives.SET_BLIP_FLASHES_ALTERNATE(blip, false);
                    NativeFunction.Natives.SET_BLIP_FLASH_INTERVAL(blip, 0);
                    NativeFunction.Natives.SET_BLIP_FLASH_TIMER(blip, 0);
                    try { blip.StopFlashing(); } catch { }
                }

                string tag = SanitizeCallsign(callsign);
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    string suffixTag = $"[{suffix}]";
                    if (tag.EndsWith(suffixTag, StringComparison.OrdinalIgnoreCase))
                    {
                        blip.Name = tag;
                    }
                    else if (suffix.Equals("SUSPECT", StringComparison.OrdinalIgnoreCase) &&
                             (string.IsNullOrWhiteSpace(callsign) || tag.Equals("Police", StringComparison.OrdinalIgnoreCase)))
                    {
                        blip.Name = "SUSPECT";
                    }
                    else
                    {
                        blip.Name = $"{tag} {suffixTag}";
                    }
                }
                else
                {
                    blip.Name = tag;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"ApplyBlipState error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sanitizes a callsign string by stripping transient status tags (e.g. [PATROL], [CODE 3])
        /// while preserving specialized emergency service tags like [EMS] and [FIRE].
        /// </summary>
        public static string SanitizeCallsign(string callsign)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return "Police";
            string clean = callsign.Trim();

            clean = Regex.Replace(clean, @"\[([^\]]+)\]", match =>
            {
                string tag = match.Groups[1].Value.Trim();
                if (tag.Equals("EMS", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("FIRE", StringComparison.OrdinalIgnoreCase) ||
                    tag.Equals("AMBULANCE", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }
                return string.Empty;
            });

            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(clean) ? "Police" : clean;
        }
    }
}
