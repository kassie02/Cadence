using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Rage;
using Cadence.Core;
using Cadence.Common;
using LspdFunctions = LSPD_First_Response.Mod.API.Functions;
using UBFunctions = UltimateBackup.API.Functions;

namespace Cadence.Services
{
    public static class UltimateBackupService
    {
        private static bool _initialized;
        public static bool IsAvailable { get; private set; }

        /// <summary>
        /// Safe detection of Ultimate Backup assembly via AppDomain inspection and LSPDFR user plugins list.
        /// Guaranteed not to throw early boot JIT or null reference errors.
        /// </summary>
        public static bool Detect()
        {
            if (IsAvailable) return true;

            try
            {
                // Check 1: In-memory loaded assemblies (safe, fast, early-boot compatible)
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                if (loaded != null)
                {
                    foreach (Assembly asm in loaded)
                    {
                        try
                        {
                            if (asm != null && string.Equals(asm.GetName()?.Name, "UltimateBackup", StringComparison.OrdinalIgnoreCase))
                            {
                                IsAvailable = true;
                                Version ver = asm.GetName()?.Version;
                                Logger.Info($"[UB] Ultimate Backup detected via AppDomain! Version: {ver}");
                                return true;
                            }
                        }
                        catch { }
                    }
                }

                // Check 2: LSPDFR plugin manager registry
                try
                {
                    Assembly[] lspdfrPlugins = LspdFunctions.GetAllUserPlugins();
                    if (lspdfrPlugins != null)
                    {
                        foreach (Assembly asm in lspdfrPlugins)
                        {
                            try
                            {
                                if (asm != null && string.Equals(asm.GetName()?.Name, "UltimateBackup", StringComparison.OrdinalIgnoreCase))
                                {
                                    IsAvailable = true;
                                    Version ver = asm.GetName()?.Version;
                                    Logger.Info($"[UB] Ultimate Backup detected via LSPDFR API! Version: {ver}");
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch
                {
                    // LspdFunctions.GetAllUserPlugins() can throw if LSPDFR plugin manager is still initialising
                }

                Logger.Debug("[UB] Ultimate Backup is not installed or not yet loaded.");
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.Detect error", ex);
            }

            return false;
        }

        public static void Initialize()
        {
            if (_initialized) return;
            Detect();
            _initialized = true;
        }

        // ====================================================================
        // Safe UB API Hooks (Strict NoInlining to prevent JIT TypeLoad crashes)
        // ====================================================================

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Tuple<Vehicle, List<Ped>> SpawnLocalPatrol(Vector3 location, int numPeds)
        {
            if (!IsAvailable && !Detect()) return null;

            try
            {
                return UBFunctions.getLocalPatrolUnit(location, Math.Max(1, numPeds));
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnLocalPatrol error", ex);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Tuple<Vehicle, List<Ped>> SpawnStatePatrol(Vector3 location, int numPeds)
        {
            if (!IsAvailable && !Detect()) return null;

            try
            {
                return UBFunctions.getStatePatrolUnit(location, Math.Max(1, numPeds));
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnStatePatrol error", ex);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Ped SpawnLocalPed(Vector3 location, float heading)
        {
            if (!IsAvailable && !Detect()) return null;

            try
            {
                return UBFunctions.getLocalPatrolPed(location, heading);
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnLocalPed error", ex);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Ped SpawnStatePed(Vector3 location, float heading)
        {
            if (!IsAvailable && !Detect()) return null;

            try
            {
                return UBFunctions.geStatePatrolPed(location, heading);
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnStatePed error", ex);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsUltimateBackupCop(Ped ped)
        {
            if (!IsAvailable || ped == null || !ped.Exists()) return false;

            try
            {
                return UBFunctions.isUltimateBackupCop(ped);
            }
            catch
            {
                return false;
            }
        }

        // ====================================================================
        // Native GTA / LSPDFR Fallback Spawners (Zero UB dependency)
        // ====================================================================

        /// <summary>
        /// Spawns a native RPH police cruiser with an ambient officer, guaranteed to work when UB is absent.
        /// </summary>
        public static Tuple<Vehicle, List<Ped>> SpawnNativePatrolUnit(Vector3 location, float heading, string vehicleModel = "police", string copModel = "s_m_y_cop_01")
        {
            try
            {
                Model vModel = new Model(vehicleModel);
                Model pModel = new Model(copModel);

                vModel.LoadAndWait();
                pModel.LoadAndWait();

                Vehicle cruiser = new Vehicle(vModel, location, heading);
                WorldUtils.SecureEntityGround(cruiser);

                Ped officer = new Ped(pModel, location, heading);
                officer.WarpIntoVehicle(cruiser, -1); // Driver seat
                officer.BlockPermanentEvents = true;

                NativeTasks.ConfigurePatrolDriver(officer, cruiser);

                var peds = new List<Ped> { officer };
                return new Tuple<Vehicle, List<Ped>>(cruiser, peds);
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnNativePatrolUnit error", ex);
                return null;
            }
        }
    }
}
