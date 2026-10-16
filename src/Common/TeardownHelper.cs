using System;
using System.Collections.Generic;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;
using Cadence.Core;

namespace Cadence.Common
{
    /// <summary>
    /// Centralized safe teardown and entity dismissal helper.
    /// Guarantees that:
    /// 1. Officers enrolled in LSPDFR pursuit AI are disenrolled BEFORE tasks are cleared or entities deleted.
    /// 2. Vehicles completely neutralize both visual emergency lights and audible sirens.
    /// 3. All dismissed entities are released from UnitRegistry to prevent memory or ownership leaks.
    /// </summary>
    public static class TeardownHelper
    {
        /// <summary>
        /// Safely dismisses or deletes an officer ped.
        /// If the ped is enrolled in an active LSPDFR pursuit, disenrolls them before task clearing or deleting
        /// to prevent NullReferenceException crashes inside LSPDFR's TaskCopChasePedAdv.
        /// </summary>
        /// <param name="ped">The officer ped to dismiss or delete.</param>
        /// <param name="delete">If true, deletes the ped entity immediately; otherwise clears tasks and dismisses to ambient.</param>
        public static void SafeDismissPed(Ped ped, bool delete = false)
        {
            if (ped == null) return;

            try
            {
                bool exists;
                try
                {
                    exists = ped.Exists();
                }
                catch
                {
                    exists = false;
                }

                if (!exists)
                {
                    UnitRegistry.Release(ped);
                    return;
                }

                // 1. LSPDFR pursuit disenrollment MUST happen before clearing tasks or deleting
                try
                {
                    if (Functions.IsPedInPursuit(ped))
                    {
                        Functions.RemovePedFromPursuit(ped);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[TeardownHelper] Failed to disenroll ped from pursuit: {ex.Message}");
                }

                // 2. Release from central UnitRegistry
                UnitRegistry.Release(ped);

                // 3. Delete or perform clean dismissal
                if (delete)
                {
                    try
                    {
                        if (ped.Exists())
                        {
                            ped.Delete();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[TeardownHelper] Failed to delete ped: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        if (ped.Exists())
                        {
                            ped.Tasks.Clear();
                            ped.BlockPermanentEvents = false;
                            ped.Dismiss();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[TeardownHelper] Failed to cleanly dismiss ped: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[TeardownHelper] SafeDismissPed error", ex);
            }
        }

        /// <summary>
        /// Safely dismisses or deletes a vehicle.
        /// Neutralizes all siren and lightbar states (both visual and audible) and releases from UnitRegistry.
        /// </summary>
        /// <param name="veh">The vehicle to dismiss or delete.</param>
        /// <param name="delete">If true, deletes the vehicle immediately; otherwise dismisses to ambient.</param>
        public static void SafeDismissVehicle(Vehicle veh, bool delete = false)
        {
            if (veh == null) return;

            try
            {
                bool exists;
                try
                {
                    exists = veh.Exists();
                }
                catch
                {
                    exists = false;
                }

                if (!exists)
                {
                    UnitRegistry.Release(veh);
                    return;
                }

                // 1. Deactivate siren and lights via standard protocol
                try
                {
                    veh.IsSirenOn = false;
                    veh.IsSirenSilent = false;
                    NativeTasks.SET_VEHICLE_SIREN(veh, false);
                    NativeTasks.SET_VEHICLE_HAS_MUTED_SIRENS(veh, false);
                    NativeTasks.SET_SIREN_WITH_NO_DRIVER(veh, false);
                    veh.ShouldVehiclesYieldToThisVehicle = false;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[TeardownHelper] Failed to deactivate siren/lights: {ex.Message}");
                }

                // 2. Release from central UnitRegistry
                UnitRegistry.Release(veh);

                // 3. Delete or dismiss
                if (delete)
                {
                    try
                    {
                        if (veh.Exists())
                        {
                            veh.Delete();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[TeardownHelper] Failed to delete vehicle: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        if (veh.Exists())
                        {
                            veh.Dismiss();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[TeardownHelper] Failed to dismiss vehicle: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[TeardownHelper] SafeDismissVehicle error", ex);
            }
        }

        /// <summary>
        /// Safely dismisses or deletes an officer and their assigned cruiser.
        /// </summary>
        public static void SafeDismissUnit(Ped ofc, Vehicle cruiser, bool delete = false)
        {
            SafeDismissPed(ofc, delete);
            SafeDismissVehicle(cruiser, delete);
        }

        /// <summary>
        /// Safely dismisses or deletes a group of officers and their assigned cruiser.
        /// </summary>
        public static void SafeDismissUnit(IEnumerable<Ped> officers, Vehicle cruiser, bool delete = false)
        {
            if (officers != null)
            {
                foreach (var ofc in officers)
                {
                    SafeDismissPed(ofc, delete);
                }
            }

            SafeDismissVehicle(cruiser, delete);
        }
    }
}
