using System;
using Rage;
using Rage.Native;

namespace Cadence.Common
{
    public static class NativeTasks
    {
        // ====================================================================
        // GTA V AI Driving Flags Matrix
        // ====================================================================

        /// <summary>
        /// Standard ambient cruising: obeys traffic signals, stops for pedestrians and cars.
        /// </summary>
        public const VehicleDrivingFlags CruisingFlags = (VehicleDrivingFlags)786603;

        /// <summary>
        /// Code 3 emergency response: bypasses traffic signals, aggressive lane overtaking, yields to obstacles when forced.
        /// </summary>
        public const VehicleDrivingFlags EmergencyFlags = (VehicleDrivingFlags)1074528293;

        /// <summary>
        /// Gentle roadside pullover: cautious maneuvering to highway shoulder or curb.
        /// </summary>
        public const VehicleDrivingFlags PulloverFlags = (VehicleDrivingFlags)262144;

        /// <summary>
        /// Aggressive pursuit: cuts corners, ignores red lights, weaves through traffic.
        /// </summary>
        public const VehicleDrivingFlags PursuitFlags = (VehicleDrivingFlags)1074528311;

        /// <summary>
        /// Configures high-performance emergency driving capabilities on police drivers.
        /// </summary>
        public static void ConfigureEmergencyDriver(Ped driver, Vehicle cruiser = null)
        {
            if (driver == null || !driver.Exists()) return;

            try
            {
                NativeFunction.Natives.SET_DRIVER_ABILITY(driver, 1.0f);
                NativeFunction.Natives.SET_DRIVER_AGGRESSIVENESS(driver, 0.85f);
                NativeFunction.Natives.SET_DRIVER_RACING_MODIFIER(driver, 1.0f);
                driver.BlockPermanentEvents = true;

                if (cruiser != null && cruiser.Exists())
                {
                    cruiser.ShouldVehiclesYieldToThisVehicle = true;
                    NativeFunction.Natives.SET_VEHICLE_HAS_STRONG_AXLES(cruiser, true);
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"ConfigureEmergencyDriver error: {ex.Message}");
            }
        }

        /// <summary>
        /// Configures calm, organic ambient driving parameters for routine cruisers.
        /// </summary>
        public static void ConfigurePatrolDriver(Ped driver, Vehicle cruiser = null)
        {
            if (driver == null || !driver.Exists()) return;

            try
            {
                NativeFunction.Natives.SET_DRIVER_ABILITY(driver, 0.90f);
                NativeFunction.Natives.SET_DRIVER_AGGRESSIVENESS(driver, 0.0f);
                driver.BlockPermanentEvents = true;

                if (cruiser != null && cruiser.Exists())
                {
                    cruiser.ShouldVehiclesYieldToThisVehicle = false;
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"ConfigurePatrolDriver error: {ex.Message}");
            }
        }

        /// <summary>
        /// Activates silent emergency lightbar protocol (flashing strobe lights with muted siren audio).
        /// Used during roadside pullover traffic stops.
        /// </summary>
        public static void SetSilentEmergencyLights(Vehicle vehicle, bool enabled)
        {
            if (vehicle == null || !vehicle.Exists()) return;

            try
            {
                if (enabled)
                {
                    vehicle.IsSirenOn = true;
                    vehicle.IsSirenSilent = true;
                    NativeFunction.Natives.SET_VEHICLE_SIREN(vehicle, true);
                    NativeFunction.Natives.SET_VEHICLE_HAS_MUTED_SIRENS(vehicle, true);
                    NativeFunction.Natives.x2497C4717C8B881E(vehicle, true, true, false); // SET_SIREN_WITH_NO_SOUND_ACTIVE
                    NativeFunction.Natives.xF4924635A19EB37D(vehicle, true);               // SET_VEHICLE_HAS_MUTED_SIRENS
                    NativeFunction.Natives.xD8050E0EB60CF274(vehicle, true);               // SET_VEHICLE_SIREN
                }
                else
                {
                    vehicle.IsSirenOn = false;
                    vehicle.IsSirenSilent = false;
                    NativeFunction.Natives.SET_VEHICLE_SIREN(vehicle, false);
                    NativeFunction.Natives.xF4924635A19EB37D(vehicle, false);
                    NativeFunction.Natives.xD8050E0EB60CF274(vehicle, false);
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"SetSilentEmergencyLights error: {ex.Message}");
            }
        }

        /// <summary>
        /// Alias for SetSilentEmergencyLights.
        /// </summary>
        public static void SetSilentSiren(Vehicle vehicle, bool enabled) => SetSilentEmergencyLights(vehicle, enabled);

        /// <summary>
        /// Streams an animation dictionary into memory with timeout protection.
        /// </summary>
        public static bool RequestAnimDict(string animDict, int timeoutMs = 3000)
        {
            if (string.IsNullOrEmpty(animDict)) return false;

            try
            {
                if (NativeFunction.Natives.HAS_ANIM_DICT_LOADED<bool>(animDict))
                {
                    return true;
                }

                NativeFunction.Natives.REQUEST_ANIM_DICT(animDict);
                uint startTime = Game.GameTime;

                while (!NativeFunction.Natives.HAS_ANIM_DICT_LOADED<bool>(animDict))
                {
                    GameFiber.Yield();
                    if (Game.GameTime - startTime > timeoutMs)
                    {
                        Core.Logger.Warn($"Animation dictionary '{animDict}' timed out loading after {timeoutMs}ms");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Core.Logger.Error($"RequestAnimDict error for '{animDict}'", ex);
                return false;
            }
        }

        /// <summary>
        /// Releases an animation dictionary from memory when no longer needed.
        /// </summary>
        public static void RemoveAnimDict(string animDict)
        {
            if (string.IsNullOrEmpty(animDict)) return;

            try
            {
                if (NativeFunction.Natives.HAS_ANIM_DICT_LOADED<bool>(animDict))
                {
                    NativeFunction.Natives.REMOVE_ANIM_DICT(animDict);
                }
            }
            catch { }
        }

        /// <summary>
        /// Safely disposes and cleans up an RPH TaskSequence.
        /// </summary>
        public static void SafeDispose(this TaskSequence sequence)
        {
            if (sequence == null) return;
            try
            {
                sequence.Dispose();
            }
            catch { }
        }
    }
}
