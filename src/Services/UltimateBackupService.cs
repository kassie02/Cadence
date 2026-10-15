using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security;
using Rage;
using Rage.Native;
using Cadence.Core;
using Cadence.Common;
using LspdFunctions = LSPD_First_Response.Mod.API.Functions;
using UBFunctions = UltimateBackup.API.Functions;

namespace Cadence.Services
{
    public static class UltimateBackupService
    {
        private static bool _initialized;
        private static bool _isAvailable;
        private static bool _isUbRequestInProgress;

        private static bool AcquireUbGate(int timeoutMs = 8000)
        {
            uint start = Game.GameTime;
            while (_isUbRequestInProgress)
            {
                GameFiber.Yield();
                if (Game.GameTime - start > timeoutMs)
                {
                    Logger.Warn("[UB] Timeout waiting for UB request gate. Proceeding cautiously.");
                    break;
                }
            }
            _isUbRequestInProgress = true;
            return true;
        }

        private static void ReleaseUbGate()
        {
            _isUbRequestInProgress = false;
        }

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable) return true;
                return Detect();
            }
            private set => _isAvailable = value;
        }

        /// <summary>
        /// Safe detection of Ultimate Backup assembly via AppDomain inspection, LSPDFR user plugins list,
        /// or plugin DLL file presence. Guaranteed not to throw early boot JIT or null reference errors.
        /// </summary>
        public static bool Detect()
        {
            if (_isAvailable) return true;

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
                                _isAvailable = true;
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
                                    _isAvailable = true;
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

                // Check 3: File check in plugins\LSPDFR
                try
                {
                    string ubDll = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "LSPDFR", "UltimateBackup.dll");
                    if (System.IO.File.Exists(ubDll))
                    {
                        _isAvailable = true;
                        Logger.Info("[UB] Ultimate Backup detected via plugins/LSPDFR/UltimateBackup.dll file check!");
                        return true;
                    }
                }
                catch { }

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
            _initialized = true;
            bool found = Detect();
            Logger.Info($"[UB] Ultimate Backup integration initialized: {(found ? "Available (Active)" : "Not Found (Native Fallback Active)")}");
        }

        // ====================================================================
        // Safe UB API Hooks (Strict NoInlining to prevent JIT TypeLoad crashes)
        // ====================================================================

        [MethodImpl(MethodImplOptions.NoInlining)]
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public static Tuple<Vehicle, List<Ped>> SpawnLocalPatrol(Vector3 location, int numPeds)
        {
            if (!IsAvailable) return null;

            try
            {
                AcquireUbGate();
                try
                {
                    Logger.Debug("UB", $"Requesting UB Local Patrol unit at {location} (peds: {numPeds})");
                    Tuple<Vehicle, List<Ped>> result = UBFunctions.getLocalPatrolUnit(location, Math.Max(1, numPeds));
                    return PrepareUltimateBackupUnit(result);
                }
                finally
                {
                    ReleaseUbGate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnLocalPatrol error", ex);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public static Tuple<Vehicle, List<Ped>> SpawnStatePatrol(Vector3 location, int numPeds)
        {
            if (!IsAvailable) return null;

            try
            {
                AcquireUbGate();
                try
                {
                    Logger.Debug("UB", $"Requesting UB State Patrol unit at {location} (peds: {numPeds})");
                    Tuple<Vehicle, List<Ped>> result = UBFunctions.getStatePatrolUnit(location, Math.Max(1, numPeds));
                    return PrepareUltimateBackupUnit(result);
                }
                finally
                {
                    ReleaseUbGate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnStatePatrol error", ex);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public static Tuple<Vehicle, List<Ped>> SpawnAmbulanceUnit(Vector3 location, int numPeds)
        {
            if (!IsAvailable) return null;

            try
            {
                AcquireUbGate();
                try
                {
                    Logger.Debug("UB", $"Requesting UB Ambulance unit at {location} (peds: {numPeds})");
                    Tuple<Vehicle, List<Ped>> result = UBFunctions.getAmbulanceUnit(location, Math.Max(1, numPeds));
                    return PrepareUltimateBackupUnit(result, isPolice: false);
                }
                finally
                {
                    ReleaseUbGate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnAmbulanceUnit error", ex);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public static Tuple<Vehicle, List<Ped>> SpawnFireTruckUnit(Vector3 location, int numPeds)
        {
            if (!IsAvailable) return null;

            try
            {
                AcquireUbGate();
                try
                {
                    Logger.Debug("UB", $"Requesting UB Fire Truck unit at {location} (peds: {numPeds})");
                    Tuple<Vehicle, List<Ped>> result = UBFunctions.getFireTruckUnit(location, Math.Max(1, numPeds));
                    return PrepareUltimateBackupUnit(result, isPolice: false);
                }
                finally
                {
                    ReleaseUbGate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnFireTruckUnit error", ex);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public static Ped SpawnLocalPed(Vector3 location, float heading)
        {
            if (!IsAvailable) return null;

            try
            {
                AcquireUbGate();
                try
                {
                    Ped ped = UBFunctions.getLocalPatrolPed(location, heading);
                    if (ped != null && ped.Exists())
                    {
                        ped.MakePersistent();
                        ped.BlockPermanentEvents = false;
                        try { LspdFunctions.SetPedAsCop(ped); } catch { }
                        return ped;
                    }
                }
                finally
                {
                    ReleaseUbGate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnLocalPed error", ex);
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public static Ped SpawnStatePed(Vector3 location, float heading)
        {
            if (!IsAvailable) return null;

            try
            {
                AcquireUbGate();
                try
                {
                    Ped ped = UBFunctions.geStatePatrolPed(location, heading);
                    if (ped != null && ped.Exists())
                    {
                        ped.MakePersistent();
                        ped.BlockPermanentEvents = false;
                        try { LspdFunctions.SetPedAsCop(ped); } catch { }
                        return ped;
                    }
                }
                finally
                {
                    ReleaseUbGate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnStatePed error", ex);
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
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

        /// <summary>
        /// Prepares units spawned by Ultimate Backup: keeps entities persistent during their active
        /// lifecycle, sets engines on, places on ground properly, registers officers with LSPDFR, and seats them.
        /// </summary>
        private static Tuple<Vehicle, List<Ped>> PrepareUltimateBackupUnit(Tuple<Vehicle, List<Ped>> result, bool isPolice = true)
        {
            if (result == null || result.Item1 == null || !result.Item1.Exists())
            {
                return null;
            }

            Vehicle cruiser = result.Item1;
            cruiser.MakePersistent();
            cruiser.IsEngineOn = true;

            try
            {
                NativeFunction.Natives.SET_VEHICLE_ENGINE_ON(cruiser, true, true, false);
                NativeFunction.Natives.SET_VEHICLE_HANDBRAKE(cruiser, false);
                cruiser.PlaceOnGroundProperly();
            }
            catch { }

            if (result.Item2 != null && result.Item2.Count > 0)
            {
                Ped driver = result.Item2.FirstOrDefault(p => p != null && p.Exists());
                if (driver != null)
                {
                    driver.MakePersistent();
                    if (driver.CurrentVehicle != cruiser)
                    {
                        driver.WarpIntoVehicle(cruiser, -1);
                    }
                    driver.BlockPermanentEvents = false;
                    if (isPolice)
                    {
                        try { LspdFunctions.SetPedAsCop(driver); } catch { }
                    }
                }

                int maxSeats = 4;
                try { maxSeats = cruiser.PassengerCapacity; } catch { }

                for (int pIdx = 0; pIdx < result.Item2.Count; pIdx++)
                {
                    Ped pass = result.Item2[pIdx];
                    if (pass != null && pass.Exists() && pass != driver)
                    {
                        pass.MakePersistent();
                        if (pass.CurrentVehicle != cruiser)
                        {
                            int targetSeat = -1;
                            for (int s = 0; s < maxSeats; s++)
                            {
                                try { if (cruiser.IsSeatFree(s)) { targetSeat = s; break; } } catch { }
                            }
                            if (targetSeat != -1)
                            {
                                try { pass.WarpIntoVehicle(cruiser, targetSeat); } catch { }
                            }
                        }
                        pass.BlockPermanentEvents = false;
                        if (isPolice)
                        {
                            try { LspdFunctions.SetPedAsCop(pass); } catch { }
                        }
                    }
                }
            }

            string vName = cruiser != null && cruiser.Exists() ? cruiser.Model.Name : "Unknown";
            int pedCount = result.Item2 != null ? result.Item2.Count : 0;
            Logger.Debug("UB", $"Prepared UB Unit: Vehicle={vName} (0x{cruiser?.Handle:X}), TotalPeds={pedCount}, EngineOn={cruiser?.IsEngineOn}, IsPolice={isPolice}");

            return result;
        }

        // ====================================================================
        // Native GTA / LSPDFR Fallback Spawners (Zero UB dependency)
        // ====================================================================

        /// <summary>
        /// Spawns a native RPH police cruiser with an ambient officer, guaranteed to work when UB is absent.
        /// Keeps entities persistent throughout active management to prevent game engine garbage collection.
        /// </summary>
        public static Tuple<Vehicle, List<Ped>> SpawnNativePatrolUnit(Vector3 location, float heading, string vehicleModel = "police", string copModel = "s_m_y_cop_01")
        {
            if (Settings.UseUltimateBackup && !Settings.AllowFallback)
            {
                Logger.Debug("UB", $"SpawnNativePatrolUnit rejected: Fallback is disabled in settings (Fallback=false).");
                return null;
            }

            try
            {
                Logger.Debug("UB", $"Spawning Native Patrol fallback: Vehicle={vehicleModel}, Cop={copModel} at {location} (Heading: {heading:F1})");

                Model vModel = new Model(vehicleModel);
                Model pModel = new Model(copModel);

                if (!vModel.SafeLoad(4000) || !pModel.SafeLoad(4000))
                {
                    Logger.Warn($"[UB] SafeLoad timed out loading models {vehicleModel} or {copModel}.");
                    vModel.Dismiss();
                    pModel.Dismiss();
                    return null;
                }

                Vehicle cruiser = new Vehicle(vModel, location, heading);
                cruiser.MakePersistent();
                cruiser.Heading = heading;
                cruiser.IsEngineOn = true;
                try { cruiser.PlaceOnGroundProperly(); } catch { }
                WorldUtils.SecureEntityGround(cruiser);

                Ped officer = new Ped(pModel, location, heading);
                officer.MakePersistent();
                officer.WarpIntoVehicle(cruiser, -1); // Driver seat
                officer.BlockPermanentEvents = false;
                try { LspdFunctions.SetPedAsCop(officer); } catch { }

                vModel.Dismiss();
                pModel.Dismiss();

                NativeTasks.ConfigurePatrolDriver(officer, cruiser);

                var peds = new List<Ped> { officer };
                Logger.Debug("UB", $"Successfully spawned Native Patrol unit: Cruiser 0x{cruiser.Handle:X}, Officer 0x{officer.Handle:X}");
                return new Tuple<Vehicle, List<Ped>>(cruiser, peds);
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnNativePatrolUnit error", ex);
                return null;
            }
        }

        public static Tuple<Vehicle, List<Ped>> SpawnNativeAmbulanceUnit(Vector3 location, float heading, string vehicleModel = "ambulance", string pedModel = "s_m_m_paramedic_01")
        {
            if (Settings.UseUltimateBackup && !Settings.AllowFallback)
            {
                Logger.Debug("UB", "SpawnNativeAmbulanceUnit rejected: Fallback is disabled in settings (Fallback=false).");
                return null;
            }

            try
            {
                Logger.Debug("UB", $"Spawning Native Ambulance fallback: Vehicle={vehicleModel}, Ped={pedModel} at {location} (Heading: {heading:F1})");

                Model vModel = new Model(vehicleModel);
                Model pModel = new Model(pedModel);

                if (!vModel.SafeLoad(4000) || !pModel.SafeLoad(4000))
                {
                    Logger.Warn($"[UB] SafeLoad timed out loading models {vehicleModel} or {pedModel}.");
                    vModel.Dismiss();
                    pModel.Dismiss();
                    return null;
                }

                Vehicle amb = new Vehicle(vModel, location, heading);
                amb.MakePersistent();
                amb.Heading = heading;
                amb.IsEngineOn = true;
                try { amb.PlaceOnGroundProperly(); } catch { }
                WorldUtils.SecureEntityGround(amb);

                Ped medic = new Ped(pModel, location, heading);
                medic.MakePersistent();
                medic.WarpIntoVehicle(amb, -1);
                medic.BlockPermanentEvents = false;

                vModel.Dismiss();
                pModel.Dismiss();

                var peds = new List<Ped> { medic };
                return new Tuple<Vehicle, List<Ped>>(amb, peds);
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnNativeAmbulanceUnit error", ex);
                return null;
            }
        }

        public static Tuple<Vehicle, List<Ped>> SpawnNativeFireUnit(Vector3 location, float heading, string vehicleModel = "firetruk", string pedModel = "s_m_y_fireman_01")
        {
            if (Settings.UseUltimateBackup && !Settings.AllowFallback)
            {
                Logger.Debug("UB", "SpawnNativeFireUnit rejected: Fallback is disabled in settings (Fallback=false).");
                return null;
            }

            try
            {
                Logger.Debug("UB", $"Spawning Native Fire fallback: Vehicle={vehicleModel}, Ped={pedModel} at {location} (Heading: {heading:F1})");

                Model vModel = new Model(vehicleModel);
                Model pModel = new Model(pedModel);

                if (!vModel.SafeLoad(4000) || !pModel.SafeLoad(4000))
                {
                    Logger.Warn($"[UB] SafeLoad timed out loading models {vehicleModel} or {pedModel}.");
                    vModel.Dismiss();
                    pModel.Dismiss();
                    return null;
                }

                Vehicle fire = new Vehicle(vModel, location, heading);
                fire.MakePersistent();
                fire.Heading = heading;
                fire.IsEngineOn = true;
                try { fire.PlaceOnGroundProperly(); } catch { }
                WorldUtils.SecureEntityGround(fire);

                Ped fireman = new Ped(pModel, location, heading);
                fireman.MakePersistent();
                fireman.WarpIntoVehicle(fire, -1);
                fireman.BlockPermanentEvents = false;

                vModel.Dismiss();
                pModel.Dismiss();

                var peds = new List<Ped> { fireman };
                return new Tuple<Vehicle, List<Ped>>(fire, peds);
            }
            catch (Exception ex)
            {
                Logger.Error("UltimateBackupService.SpawnNativeFireUnit error", ex);
                return null;
            }
        }
    }
}
