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
        /// Standard ambient cruising: obeys traffic signals, stops for sudden road hazards, but actively steers
        /// around stationary/parked vehicles (8), pedestrians (16), objects (32), and changes lanes around obstructions (524288).
        /// </summary>
        public const VehicleDrivingFlags CruisingFlags = (VehicleDrivingFlags)786603; // Standard authentic ambient driving with obstacle avoidance

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
        /// Long-range emergency response flag (0xC023C = 787004L):
        /// Swerve around vehicles (4), SteerAroundStationaryVehicles (8), SteerAroundPeds (16), SteerAroundObjects (32),
        /// DriveIntoOncomingTraffic (512), UseShortCutLinks (262144), and ChangeLanesAroundObstructions (524288).
        /// Flawless forward emergency pathfinding over long distances without reversing or jamming at intersections.
        /// </summary>
        public const long LongRangeEmergencyDrivingFlag = 787004L;

        /// <summary>
        /// Normal long-range ambient traffic flag (786603L): stops for signals and peds, but changes lanes around obstructions
        /// and steers around parked cars and roadside pedestrians.
        /// </summary>
        public const long NormalDrivingFlag = 786603L;

        /// <summary>
        /// Commands a driver to navigate an emergency vehicle to destination coordinates using the long-range pathing engine.
        /// Aggressiveness is set to 0.8f, ability to 1.0f, and chase behavior flag 8 enabled for assertive response driving and overtaking.
        /// Explicitly enforces SET_DRIVE_TASK_DRIVING_STYLE and SET_DRIVE_TASK_CRUISE_SPEED so AI actively navigates around obstacles.
        /// </summary>
        public static void TaskLongRangeEmergencyDrive(Ped driver, Vehicle vehicle, Vector3 destination, float speedMps = 32.0f, float stoppingRadius = 12.0f)
        {
            if (driver == null || !driver.Exists() || vehicle == null || !vehicle.Exists()) return;

            try
            {
                NativeFunction.Natives.SET_DRIVER_ABILITY(driver, 1.0f);
                NativeFunction.Natives.SET_DRIVER_AGGRESSIVENESS(driver, 0.8f);
                NativeFunction.Natives.SET_DRIVER_RACING_MODIFIER(driver, 1.0f);
                NativeFunction.Natives.SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG(driver, 8, true);
                driver.BlockPermanentEvents = true;

                vehicle.ShouldVehiclesYieldToThisVehicle = true;

                try
                {
                    NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(
                        driver,
                        vehicle,
                        destination.X,
                        destination.Y,
                        destination.Z,
                        speedMps,
                        (int)LongRangeEmergencyDrivingFlag,
                        stoppingRadius
                    );

                    NativeFunction.Natives.SET_DRIVE_TASK_DRIVING_STYLE(driver, (int)LongRangeEmergencyDrivingFlag);
                    NativeFunction.Natives.SET_DRIVE_TASK_CRUISE_SPEED(driver, speedMps);
                }
                catch
                {
                    // Fallback to managed RPH driving task if native name resolution fails
                    driver.Tasks.DriveToPosition(vehicle, destination, speedMps, (VehicleDrivingFlags)LongRangeEmergencyDrivingFlag, stoppingRadius);
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Error("[NativeTasks] TaskLongRangeEmergencyDrive error", ex);
            }
        }

        /// <summary>
        /// General long-range vehicle driving method with configurable driving flags.
        /// </summary>
        public static void DriveToPositionLongRange(Ped driver, Vehicle vehicle, Vector3 destination, float speedMps, long drivingFlags, float stoppingRadius = 25.0f)
        {
            if (driver == null || !driver.Exists() || vehicle == null || !vehicle.Exists()) return;

            try
            {
                NativeFunction.Natives.SET_DRIVER_ABILITY(driver, 1.0f);
                NativeFunction.Natives.SET_DRIVER_AGGRESSIVENESS(driver, 0.2f);
                driver.BlockPermanentEvents = true;

                try
                {
                    NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(
                        driver,
                        vehicle,
                        destination.X,
                        destination.Y,
                        destination.Z,
                        speedMps,
                        (int)drivingFlags,
                        stoppingRadius
                    );

                    NativeFunction.Natives.SET_DRIVE_TASK_DRIVING_STYLE(driver, (int)drivingFlags);
                    NativeFunction.Natives.SET_DRIVE_TASK_CRUISE_SPEED(driver, speedMps);
                }
                catch
                {
                    // Fallback to managed RPH driving task if native name resolution fails
                    driver.Tasks.DriveToPosition(vehicle, destination, speedMps, (VehicleDrivingFlags)drivingFlags, stoppingRadius);
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Error("[NativeTasks] DriveToPositionLongRange error", ex);
            }
        }

        /// <summary>
        /// Configures high-performance emergency driving capabilities on police drivers.
        /// Aggressiveness is set to 0.6f, ability to 1.0f, and chase behavior flag 8 is enabled for assertive pursuing, overtaking, and U-turns.
        /// </summary>
        public static void ConfigureEmergencyDriver(Ped driver, Vehicle cruiser = null)
        {
            if (driver == null || !driver.Exists()) return;

            try
            {
                NativeFunction.Natives.SET_DRIVER_ABILITY(driver, 1.0f);
                NativeFunction.Natives.SET_DRIVER_AGGRESSIVENESS(driver, 0.6f);
                NativeFunction.Natives.SET_DRIVER_RACING_MODIFIER(driver, 1.0f);
                NativeFunction.Natives.SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG(driver, 8, true);
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
        /// Commands a driver in a vehicle to aggressively pursue a fleeing target entity using GTA V's native chase AI.
        /// </summary>
        public static void TaskVehicleChase(Ped driver, Entity target)
        {
            if (driver == null || !driver.Exists() || target == null || !target.Exists()) return;

            try
            {
                driver.BlockPermanentEvents = true;
                NativeFunction.Natives.SET_DRIVER_ABILITY(driver, 1.0f);
                NativeFunction.Natives.SET_DRIVER_AGGRESSIVENESS(driver, 0.7f);
                NativeFunction.Natives.SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG(driver, 8, true);
                NativeFunction.Natives.TASK_VEHICLE_CHASE(driver, target);
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"[NativeTasks] TaskVehicleChase error: {ex.Message}");
            }
        }

        /// <summary>
        /// Configures calm, organic ambient driving parameters for routine cruisers.
        /// Driver keeps BlockPermanentEvents = false so GTA V's native traffic decision-making operates naturally.
        /// </summary>
        public static void ConfigurePatrolDriver(Ped driver, Vehicle cruiser = null)
        {
            if (driver == null || !driver.Exists()) return;

            try
            {
                NativeFunction.Natives.SET_DRIVER_ABILITY(driver, 1.0f);
                NativeFunction.Natives.SET_DRIVER_AGGRESSIVENESS(driver, 0.0f);
                NativeFunction.Natives.SET_DRIVER_RACING_MODIFIER(driver, 0.0f);
                NativeFunction.Natives.SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG(driver, 8, false);
                driver.BlockPermanentEvents = false;

                if (cruiser != null && cruiser.Exists())
                {
                    cruiser.ShouldVehiclesYieldToThisVehicle = false;
                    ShutOffSirenAndLights(cruiser);
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
                }
                else
                {
                    ShutOffSirenAndLights(vehicle);
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"SetSilentEmergencyLights error: {ex.Message}");
            }
        }

        /// <summary>
        /// Completely terminates all audible sirens and visual emergency lights on a vehicle.
        /// Cleanly neutralizes siren audio and emergency yield state without leaving muted sirens armed.
        /// </summary>
        public static void ShutOffSirenAndLights(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists()) return;

            try
            {
                vehicle.IsSirenOn = false;
                vehicle.IsSirenSilent = false;
                NativeFunction.Natives.SET_VEHICLE_SIREN(vehicle, false);
                NativeFunction.Natives.SET_VEHICLE_HAS_MUTED_SIRENS(vehicle, false);
                NativeFunction.Natives.SET_SIREN_WITH_NO_DRIVER(vehicle, false);
                vehicle.ShouldVehiclesYieldToThisVehicle = false;
            }
            catch { }
        }

        /// <summary>
        /// Enforces the global siren policy: if emergency lights are turned off, siren audio is strictly killed.
        /// </summary>
        public static void EnforceSirenLightbarPolicy(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists()) return;

            try
            {
                if (!Core.Settings.RequireLightsForSiren) return;

                bool isLightOn = vehicle.IsSirenOn;
                try
                {
                    isLightOn = isLightOn || NativeFunction.Natives.IS_VEHICLE_SIREN_ON<bool>(vehicle);
                }
                catch { }

                if (!isLightOn)
                {
                    bool isAudioOn = false;
                    try
                    {
                        isAudioOn = NativeFunction.Natives.IS_VEHICLE_SIREN_AUDIO_ON<bool>(vehicle);
                    }
                    catch { }

                    if (isAudioOn || !vehicle.IsSirenSilent)
                    {
                        vehicle.IsSirenSilent = true;
                        NativeFunction.Natives.SET_VEHICLE_HAS_MUTED_SIRENS(vehicle, true);
                        NativeFunction.Natives.SET_SIREN_WITH_NO_DRIVER(vehicle, false);
                        NativeFunction.Natives.SET_VEHICLE_SIREN(vehicle, false);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Alias for SetSilentEmergencyLights.
        /// </summary>
        public static void SetSilentSiren(Vehicle vehicle, bool enabled) => SetSilentEmergencyLights(vehicle, enabled);

        /// <summary>
        /// Native wrapper for SET_VEHICLE_SIREN.
        /// </summary>
        public static void SET_VEHICLE_SIREN(Vehicle vehicle, bool toggle)
        {
            if (vehicle != null && vehicle.Exists())
            {
                NativeFunction.Natives.SET_VEHICLE_SIREN(vehicle, toggle);
            }
        }

        /// <summary>
        /// Native wrapper for SET_VEHICLE_HAS_MUTED_SIRENS.
        /// </summary>
        public static void SET_VEHICLE_HAS_MUTED_SIRENS(Vehicle vehicle, bool toggle)
        {
            if (vehicle != null && vehicle.Exists())
            {
                NativeFunction.Natives.SET_VEHICLE_HAS_MUTED_SIRENS(vehicle, toggle);
            }
        }

        /// <summary>
        /// Native wrapper for SET_SIREN_WITH_NO_DRIVER.
        /// </summary>
        public static void SET_SIREN_WITH_NO_DRIVER(Vehicle vehicle, bool toggle)
        {
            if (vehicle != null && vehicle.Exists())
            {
                NativeFunction.Natives.SET_SIREN_WITH_NO_DRIVER(vehicle, toggle);
            }
        }

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
                        try
                        {
                            NativeFunction.Natives.REMOVE_ANIM_DICT(animDict);
                        }
                        catch { }
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
