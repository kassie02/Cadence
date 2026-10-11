using System;
using Rage;
using Rage.Native;

namespace Cadence.Common
{
    public static class WorldUtils
    {
        private const float MinValidGroundZ = -50.0f;
        private const float MaxGroundDelta = 3.5f;

        /// <summary>
        /// Watchdog to verify if a coordinate is trapped in a subterranean void or invalid elevation.
        /// </summary>
        public static bool IsSubterraneanOrVoid(Vector3 pos)
        {
            if (float.IsNaN(pos.Z) || float.IsInfinity(pos.Z)) return true;
            return pos.Z < MinValidGroundZ;
        }

        /// <summary>
        /// Ironclad ground elevation watchdog for peds and vehicles.
        /// Prevents subterranean falling, roof snapping, or geometry clipping.
        /// </summary>
        public static void SecureEntityGround(Entity entity, bool freezeTemporarily = false)
        {
            if (entity == null || !entity.Exists() || !entity.IsValid()) return;

            try
            {
                // Skip peds sitting inside vehicles as the vehicle manages their position
                if (entity is Ped ped && (ped.IsInAnyVehicle(false) || ped.CurrentVehicle != null))
                {
                    return;
                }

                Vector3 pos = entity.Position;
                NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(pos.X, pos.Y, pos.Z);

                float groundZ = 0f;
                // Probes tightly above origin: 2.0m for vehicles, 1.2m for peds.
                // Avoids pulling rooftop / bridge elevation 15m above.
                float probeZ = entity is Vehicle ? (pos.Z + 2.0f) : (pos.Z + 1.2f);
                bool foundZ = NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(pos.X, pos.Y, probeZ, out groundZ, false);

                if (!foundZ || groundZ <= MinValidGroundZ || Math.Abs(groundZ - pos.Z) > MaxGroundDelta)
                {
                    float? rphZ = World.GetGroundZ(pos, true, false);
                    if (rphZ.HasValue && rphZ.Value > MinValidGroundZ && Math.Abs(rphZ.Value - pos.Z) <= MaxGroundDelta)
                    {
                        groundZ = rphZ.Value;
                        foundZ = true;
                    }
                }

                if (foundZ && groundZ > MinValidGroundZ && Math.Abs(groundZ - pos.Z) <= MaxGroundDelta)
                {
                    // Ped origin is pelvis (~0.95m above ground), adding 1.0f rests feet on ground surface.
                    // Vehicle origin is chassis center, adding 0.35f sets wheels flush with pavement.
                    entity.Position = new Vector3(pos.X, pos.Y, groundZ + (entity is Vehicle ? 0.35f : 1.0f));
                }

                if (entity is Vehicle veh)
                {
                    NativeFunction.Natives.SET_VEHICLE_ON_GROUND_PROPERLY(veh, 5.0f);
                }

                if (freezeTemporarily)
                {
                    entity.IsPositionFrozen = true;
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"SecureEntityGround exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Releases entity from temporary position freeze after ground stabilization.
        /// </summary>
        public static void ReleaseEntityGround(Entity entity)
        {
            if (entity == null || !entity.Exists() || !entity.IsValid()) return;

            try
            {
                SecureEntityGround(entity, freezeTemporarily: false);
                entity.IsPositionFrozen = false;
            }
            catch { }
        }

        /// <summary>
        /// Probes and validates ground elevation for an arbitrary 3D coordinate prior to entity creation.
        /// </summary>
        public static bool GetAccurateGroundZ(Vector3 queryPos, out float safeGroundZ)
        {
            safeGroundZ = queryPos.Z;
            try
            {
                NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(queryPos.X, queryPos.Y, queryPos.Z);
                float probeZ = queryPos.Z + 2.5f;

                if (NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(queryPos.X, queryPos.Y, probeZ, out float nativeZ, false))
                {
                    if (nativeZ > MinValidGroundZ && Math.Abs(nativeZ - queryPos.Z) <= MaxGroundDelta)
                    {
                        safeGroundZ = nativeZ;
                        return true;
                    }
                }

                float? rphZ = World.GetGroundZ(queryPos, true, false);
                if (rphZ.HasValue && rphZ.Value > MinValidGroundZ && Math.Abs(rphZ.Value - queryPos.Z) <= MaxGroundDelta)
                {
                    safeGroundZ = rphZ.Value;
                    return true;
                }
            }
            catch { }

            return !IsSubterraneanOrVoid(queryPos);
        }

        /// <summary>
        /// Finds the nearest vehicle node and road heading to a specified world position.
        /// </summary>
        public static bool GetSafeStreetPosition(Vector3 searchCenter, int nthNode, out Vector3 streetPos, out float streetHeading)
        {
            streetPos = Vector3.Zero;
            streetHeading = 0f;

            try
            {
                // Native: GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING (nodeFlags: 1 = main roads, 3.0f = tolerance)
                bool found = NativeFunction.Natives.GET_NTH_CLOSEST_VEHICLE_NODE_WITH_HEADING<bool>(
                    searchCenter.X, searchCenter.Y, searchCenter.Z,
                    Math.Max(1, nthNode),
                    out Vector3 nodePos,
                    out float nodeHeading,
                    1, 3.0f, 0
                );

                if (found && !IsSubterraneanOrVoid(nodePos))
                {
                    streetPos = nodePos;
                    streetHeading = nodeHeading;
                    return true;
                }

                // Fallback to RPH street lookup
                Vector3 rphStreet = World.GetNextPositionOnStreet(searchCenter);
                if (rphStreet != Vector3.Zero && !IsSubterraneanOrVoid(rphStreet))
                {
                    streetPos = rphStreet;
                    streetHeading = 0f;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"GetSafeStreetPosition exception: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Checks whether a given position has sufficient clearance from existing vehicles, peds, and obstructions.
        /// </summary>
        public static bool IsAreaClear(Vector3 pos, float clearanceRadius, bool checkVehicles = true, bool checkPeds = true)
        {
            try
            {
                if (checkVehicles)
                {
                    Entity[] nearbyVehicles = World.GetEntities(pos, clearanceRadius, GetEntitiesFlags.ConsiderAllVehicles);
                    if (nearbyVehicles != null && nearbyVehicles.Length > 0)
                    {
                        foreach (Entity veh in nearbyVehicles)
                        {
                            if (veh != null && veh.Exists() && veh.DistanceTo(pos) < clearanceRadius)
                            {
                                return false;
                            }
                        }
                    }
                }

                if (checkPeds)
                {
                    Entity[] nearbyPeds = World.GetEntities(pos, clearanceRadius, GetEntitiesFlags.ConsiderAllPeds);
                    if (nearbyPeds != null && nearbyPeds.Length > 0)
                    {
                        foreach (Entity ped in nearbyPeds)
                        {
                            if (ped != null && ped.Exists() && ped.DistanceTo(pos) < clearanceRadius)
                            {
                                return false;
                            }
                        }
                    }
                }

                // Obscured point native check
                bool obscured = NativeFunction.Natives.IS_POINT_OBSCURED_BY_A_MISSION_ENTITY<bool>(pos.X, pos.Y, pos.Z, clearanceRadius, clearanceRadius, clearanceRadius, 0);
                if (obscured) return false;

                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Tests if a coordinate is within line-of-sight or minimum distance of the local player character.
        /// </summary>
        public static bool IsNearPlayer(Vector3 pos, float minDistance)
        {
            try
            {
                Ped player = Game.LocalPlayer?.Character;
                if (player == null || !player.Exists()) return false;
                return player.DistanceTo(pos) <= minDistance;
            }
            catch
            {
                return false;
            }
        }
    }
}
