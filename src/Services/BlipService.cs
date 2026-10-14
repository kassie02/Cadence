using System;
using Rage;
using Rage.Native;
using Cadence.Core;

namespace Cadence.Services
{
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

        // ====================================================================
        // Universal Blip Attachment Engine
        // ====================================================================

        /// <summary>
        /// Cruising Patrol Unit: Sprite 56, Color 38 (Navy Blue), Solid, {Callsign} [PATROL].
        /// </summary>
        public static Blip AttachCruisingBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "PATROL", spriteId: 56, colorId: 38, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        /// <summary>
        /// Alias for AttachCruisingBlip.
        /// </summary>
        public static Blip AttachPatrolBlip(Vehicle vehicle, string callsign) => AttachCruisingBlip(vehicle, callsign);

        /// <summary>
        /// Roadside Traffic Stop: Sprite 56, Color 4 (Yellow), Solid, {Callsign} [TRAFFIC STOP].
        /// </summary>
        public static Blip AttachTrafficStopBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "TRAFFIC STOP", spriteId: 56, colorId: 4, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        /// <summary>
        /// Stationary / Radar Trap: Sprite 56, Color 2 (Mint Green), Solid, {Callsign} [RADAR].
        /// </summary>
        public static Blip AttachRadarWatchBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "RADAR", spriteId: 56, colorId: 2, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        /// <summary>
        /// Standing Ambient / Chilling Officer: Sprite 56, Color 2 (Mint Green), Solid, {Callsign} [CHILLING PATROL].
        /// </summary>
        public static Blip AttachChillingBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "CHILLING PATROL", spriteId: 56, colorId: 2, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        /// <summary>
        /// Stationary / Standby Unit (Generic): Sprite 56, Color 2 (Mint Green), Solid, {Callsign} [STANDBY].
        /// </summary>
        public static Blip AttachStationaryBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "STANDBY", spriteId: 56, colorId: 2, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        /// <summary>
        /// Alias for AttachStationaryBlip.
        /// </summary>
        public static Blip AttachStandbyBlip(Vehicle vehicle, string callsign) => AttachStationaryBlip(vehicle, callsign);

        /// <summary>
        /// Emergency Run / Declined Call Takeover: Sprite 56, Color 1 (Red), Flashing 600ms, {Callsign} [CODE 3].
        /// </summary>
        public static Blip AttachEmergencyBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "CODE 3", spriteId: 56, colorId: 1, isShortRange: false, flashIntervalMs: 600);
        }

        /// <summary>
        /// Active Pursuit Assist: Sprite 56, Color 17 (Orange), Flashing 400ms, {Callsign} [PURSUIT].
        /// </summary>
        public static Blip AttachPursuitBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "PURSUIT", spriteId: 56, colorId: 17, isShortRange: false, flashIntervalMs: 400);
        }

        /// <summary>
        /// Threat Reaction / Combat: Sprite 56, Color 49 (Dark Orange), Flashing 300ms, {Callsign} [ENGAGING].
        /// </summary>
        public static Blip AttachThreatBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "ENGAGING", spriteId: 56, colorId: 49, isShortRange: false, flashIntervalMs: 300);
        }

        /// <summary>
        /// Alias for AttachThreatBlip.
        /// </summary>
        public static Blip AttachCombatBlip(Vehicle vehicle, string callsign) => AttachThreatBlip(vehicle, callsign);

        /// <summary>
        /// Dismounted Officer Ped: Sprite 58 (Badge), Color 38 (Navy Blue), Solid, {Callsign} [ON FOOT].
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

                blip.Sprite = (BlipSprite)58; // Badge
                NativeFunction.Natives.SET_BLIP_COLOUR(blip, 38);
                blip.Scale = Settings.BlipScale * 0.90f;
                NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE(blip, Settings.ShortRangeBlips);
                NativeFunction.Natives.SET_BLIP_FLASHES(blip, false);
                blip.StopFlashing();

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
        /// Parked Cruiser (dismounted): Sprite 56, Color 62 (Slate Gray), Solid, {Callsign} [PARKED].
        /// </summary>
        public static Blip AttachParkedCruiserBlip(Vehicle vehicle, string callsign)
        {
            return CreateOrUpdateBlip(vehicle, callsign, "PARKED", spriteId: 56, colorId: 62, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        // ====================================================================
        // Dynamic State Transitions
        // ====================================================================

        public static void TransitionToPatrol(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "PATROL", spriteId: 56, colorId: 38, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        public static void TransitionToTrafficStop(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "TRAFFIC STOP", spriteId: 56, colorId: 4, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        public static void TransitionToRadar(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "RADAR", spriteId: 56, colorId: 2, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        public static void TransitionToChilling(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "CHILLING PATROL", spriteId: 56, colorId: 2, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        public static void TransitionToStationary(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "STANDBY", spriteId: 56, colorId: 2, isShortRange: Settings.ShortRangeBlips, flashIntervalMs: 0);
        }

        public static void TransitionToEmergency(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "CODE 3", spriteId: 56, colorId: 1, isShortRange: false, flashIntervalMs: 600);
        }

        public static void TransitionToPursuit(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "PURSUIT", spriteId: 56, colorId: 17, isShortRange: false, flashIntervalMs: 400);
        }

        public static void TransitionToThreat(Blip blip, string callsign)
        {
            ApplyBlipState(blip, callsign, "ENGAGING", spriteId: 56, colorId: 49, isShortRange: false, flashIntervalMs: 300);
        }

        public static void TransitionToCombat(Blip blip, string callsign) => TransitionToThreat(blip, callsign);

        // ====================================================================
        // Internal Helpers
        // ====================================================================

        private static Blip CreateOrUpdateBlip(Entity entity, string callsign, string suffix, int spriteId, int colorId, bool isShortRange, int flashIntervalMs)
        {
            if (!Settings.ShowRadarBlips || entity == null || !entity.Exists() || !entity.IsValid())
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

                ApplyBlipState(blip, callsign, suffix, spriteId, colorId, isShortRange, flashIntervalMs);
                return blip;
            }
            catch (Exception ex)
            {
                Logger.Debug($"CreateOrUpdateBlip error: {ex.Message}");
                return null;
            }
        }

        private static void ApplyBlipState(Blip blip, string callsign, string suffix, int spriteId, int colorId, bool isShortRange, int flashIntervalMs)
        {
            if (blip == null || !blip.Exists()) return;

            try
            {
                blip.Sprite = (BlipSprite)spriteId;
                NativeFunction.Natives.SET_BLIP_COLOUR(blip, colorId);
                blip.Scale = Settings.BlipScale;
                NativeFunction.Natives.SET_BLIP_AS_SHORT_RANGE(blip, isShortRange);

                blip.StopFlashing();
                if (flashIntervalMs > 0)
                {
                    NativeFunction.Natives.SET_BLIP_FLASHES(blip, true);
                    NativeFunction.Natives.SET_BLIP_FLASH_INTERVAL(blip, flashIntervalMs);
                }
                else
                {
                    NativeFunction.Natives.SET_BLIP_FLASHES(blip, false);
                }

                string tag = SanitizeCallsign(callsign);
                blip.Name = $"{tag} [{suffix}]";
            }
            catch (Exception ex)
            {
                Logger.Debug($"ApplyBlipState error: {ex.Message}");
            }
        }

        private static string SanitizeCallsign(string callsign)
        {
            if (string.IsNullOrWhiteSpace(callsign)) return "Police";
            string clean = callsign.Trim();
            int bracketIdx = clean.IndexOf('[');
            if (bracketIdx > 0)
            {
                clean = clean.Substring(0, bracketIdx).Trim();
            }
            return string.IsNullOrWhiteSpace(clean) ? "Police" : clean;
        }
    }
}
