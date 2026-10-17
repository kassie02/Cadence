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
        /// Watchdog to verify if a coordinate is trapped in a subterranean void or invalid elevation [A18].
        /// </summary>
        public static bool IsSubterraneanOrVoid(Vector3 pos)
        {
            if (float.IsNaN(pos.X) || float.IsInfinity(pos.X) ||
                float.IsNaN(pos.Y) || float.IsInfinity(pos.Y) ||
                float.IsNaN(pos.Z) || float.IsInfinity(pos.Z))
            {
                return true;
            }

            if (pos == Vector3.Zero || (Math.Abs(pos.X) < 0.001f && Math.Abs(pos.Y) < 0.001f && Math.Abs(pos.Z) < 0.001f)) return true;
            return pos.Z < MinValidGroundZ;
        }

        /// <summary>
        /// Ironclad water watchdog: Checks whether a given position is submerged, on water, or in a riverbed/water body.
        /// Uses GET_WATER_HEIGHT, TEST_VERTICAL_PROBE_AGAINST_ALL_WATER, and Z bounds to prevent river/water spawns.
        /// </summary>
        public static bool IsPositionInWater(Vector3 pos)
        {
            if (IsSubterraneanOrVoid(pos)) return false;

            try
            {
                // 1. Native GET_WATER_HEIGHT (0xF6829842C06AE524)
                float waterZ = 0f;
                bool hasWater = NativeFunction.Natives.GET_WATER_HEIGHT<bool>(pos.X, pos.Y, pos.Z, out waterZ);
                if (hasWater && pos.Z <= (waterZ + 0.75f))
                {
                    return true;
                }

                // 2. Native TEST_VERTICAL_PROBE_AGAINST_ALL_WATER (0x2B3451FA1E3142E2)
                // Probes from 5m above to detect rivers, rapids, creeks, canals, and shallow streams
                float probeWaterZ = 0f;
                int probeResult = NativeFunction.Natives.TEST_VERTICAL_PROBE_AGAINST_ALL_WATER<int>(pos.X, pos.Y, pos.Z + 5.0f, 0, out probeWaterZ);
                // 1 == SCRIPT_WATER_TEST_RESULT_WATER
                if (probeResult == 1 && pos.Z <= (probeWaterZ + 0.75f))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"IsPositionInWater exception: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Validates whether an entity is currently submerged in or interacting with water.
        /// </summary>
        public static bool IsEntityInWater(Entity entity)
        {
            if (entity == null || !entity.Exists() || !entity.IsValid()) return false;

            try
            {
                if (NativeFunction.Natives.IS_ENTITY_IN_WATER<bool>(entity))
                {
                    return true;
                }
                return IsPositionInWater(entity.Position);
            }
            catch
            {
                return false;
            }
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
                bool foundZ = NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(pos.X, pos.Y, probeZ, out groundZ, false, false);

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
                    try
                    {
                        veh.PlaceOnGroundProperly();
                    }
                    catch { }
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
        /// Safe extension method to set vehicle wheels flush with pavement properly.
        /// </summary>
        public static void PlaceOnGroundProperly(this Vehicle vehicle)
        {
            if (vehicle != null && vehicle.Exists())
            {
                try
                {
                    NativeFunction.Natives.SET_VEHICLE_ON_GROUND_PROPERLY(vehicle, 5.0f);
                }
                catch { }
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
        /// Probes and validates ground elevation for an arbitrary 3D coordinate prior to entity creation [A21].
        /// Returns true only when ground elevation is strictly verified against native/RPH probes.
        /// </summary>
        public static bool GetAccurateGroundZ(Vector3 queryPos, out float safeGroundZ)
        {
            safeGroundZ = queryPos.Z;
            try
            {
                if (IsSubterraneanOrVoid(queryPos) || IsPositionInWater(queryPos))
                {
                    return false;
                }

                NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(queryPos.X, queryPos.Y, queryPos.Z);
                float probeZ = queryPos.Z + 2.5f;

                if (NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(queryPos.X, queryPos.Y, probeZ, out float nativeZ, false, false))
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
            catch (Exception ex)
            {
                Core.Logger.Debug($"GetAccurateGroundZ exception: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Finds the nearest safe vehicle node and road heading to a specified world position.
        /// Uses RPH World.GetNextPositionOnStreet combined with GET_CLOSEST_VEHICLE_NODE_WITH_HEADING
        /// for rock-solid stability and zero native access violations.
        /// </summary>
        public static bool GetSafeStreetPosition(Vector3 searchCenter, int nthNode, out Vector3 streetPos, out float streetHeading)
        {
            streetPos = Vector3.Zero;
            streetHeading = 0f;

            try
            {
                // Primary: RPH fast managed street lookup
                Vector3 rphStreet = World.GetNextPositionOnStreet(searchCenter);
                if (rphStreet != Vector3.Zero && !IsSubterraneanOrVoid(rphStreet) && !IsPositionInWater(rphStreet))
                {
                    try
                    {
                        // Native: GET_CLOSEST_VEHICLE_NODE_WITH_HEADING (hash 0xFF071FB798B803B0)
                        // Signature: BOOL (float x, float y, float z, out Vector3 outPos, out float outHeading, int nodeType, float p6, float p7)
                        bool foundNode = NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING<bool>(
                            rphStreet.X, rphStreet.Y, rphStreet.Z,
                            out Vector3 nodePos,
                            out float nodeHeading,
                            1, 3.0f, 0f
                        );

                        if (foundNode && nodePos != Vector3.Zero && !IsSubterraneanOrVoid(nodePos) && !IsPositionInWater(nodePos))
                        {
                            streetPos = nodePos;
                            streetHeading = nodeHeading;
                            return true;
                        }
                    }
                    catch { }

                    streetPos = rphStreet;
                    streetHeading = 0f;
                    return true;
                }

                // Secondary Fallback: probe directly around searchCenter
                try
                {
                    bool foundDirect = NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING<bool>(
                        searchCenter.X, searchCenter.Y, searchCenter.Z,
                        out Vector3 directNode,
                        out float directHeading,
                        1, 3.0f, 0f
                    );

                    if (foundDirect && directNode != Vector3.Zero && !IsSubterraneanOrVoid(directNode) && !IsPositionInWater(directNode))
                    {
                        streetPos = directNode;
                        streetHeading = directHeading;
                        return true;
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"GetSafeStreetPosition exception: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Checks whether a given position has sufficient clearance from existing vehicles, peds, and obstructions.
        /// Excludes the local player and Cadence-owned units [A6] so existing units do not starve new spawns.
        /// Fails closed (returns false) on unexpected exceptions.
        /// </summary>
        public static bool IsAreaClear(Vector3 pos, float clearanceRadius, bool checkVehicles = true, bool checkPeds = true)
        {
            if (IsSubterraneanOrVoid(pos) || IsPositionInWater(pos) || clearanceRadius <= 0.0f)
            {
                return false;
            }

            try
            {
                Ped playerPed = null;
                Vehicle playerVeh = null;
                try
                {
                    playerPed = Game.LocalPlayer?.Character;
                    if (playerPed != null && playerPed.Exists())
                    {
                        playerVeh = playerPed.CurrentVehicle ?? playerPed.LastVehicle;
                    }
                }
                catch { }

                if (checkVehicles)
                {
                    Entity[] nearbyVehicles = World.GetEntities(pos, clearanceRadius, GetEntitiesFlags.ConsiderAllVehicles);
                    if (nearbyVehicles != null && nearbyVehicles.Length > 0)
                    {
                        foreach (Entity veh in nearbyVehicles)
                        {
                            if (veh == null || !veh.Exists()) continue;

                            // Exclude local player vehicle
                            if (playerVeh != null && playerVeh.Exists() && veh.Handle == playerVeh.Handle) continue;

                            // Exclude Cadence-owned units so ambient density isn't self-starved [A6]
                            if (UnitRegistry.IsClaimed(veh)) continue;

                            if (veh.DistanceTo(pos) < clearanceRadius)
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
                            if (ped == null || !ped.Exists()) continue;

                            // Exclude local player character
                            if (playerPed != null && playerPed.Exists() && ped.Handle == playerPed.Handle) continue;

                            // Exclude Cadence-owned officers [A6]
                            if (UnitRegistry.IsClaimed(ped)) continue;

                            if (ped.DistanceTo(pos) < clearanceRadius)
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
            catch (Exception ex)
            {
                Core.Logger.Debug($"IsAreaClear encountered exception (failing closed) [A6]: {ex.Message}");
                return false;
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

        /// <summary>
        /// Identifies if a position is in Blaine County / Country (Sandy Shores, Grapeseed, Paleto Bay, etc.).
        /// Prevents city police (LAPD/LSPD) from being placed in rural Blaine County jurisdictions.
        /// </summary>
        public static bool IsBlaineCounty(Vector3 pos)
        {
            // Vinewood Hills boundary is roughly Y = 900 - 1000
            if (pos.Y > 950.0f) return true;

            try
            {
                string zone = NativeFunction.Natives.GET_NAME_OF_ZONE<string>(pos.X, pos.Y, pos.Z);
                if (!string.IsNullOrEmpty(zone))
                {
                    zone = zone.ToUpperInvariant();
                    if (zone == "SANDY" || zone == "GRAPES" || zone == "PALETO" || zone == "CANNY" ||
                        zone == "TONGVA" || zone == "ALAMO" || zone == "DESRT" || zone == "CHIL" ||
                        zone == "MTGORDO" || zone == "MTCHIL" || zone == "PALCOV" || zone == "PALFOR" ||
                        zone == "BRADP" || zone == "HARMO" || zone == "JAIL" || zone == "ZANCUDO" ||
                        zone == "PROCOB" || zone == "SANCHIA")
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Detects whether the coordinate is on a major highway or freeway.
        /// </summary>
        public static bool IsHighway(Vector3 pos)
        {
            try
            {
                string street = World.GetStreetName(pos);
                if (!string.IsNullOrEmpty(street))
                {
                    string s = street.ToLowerInvariant();
                    if (s.Contains("fwy") || s.Contains("freeway") || s.Contains("hwy") ||
                        s.Contains("highway") || s.Contains("route 68") || s.Contains("bypass") ||
                        s.Contains("los santos fwy") || s.Contains("del perro fwy") ||
                        s.Contains("senora fwy") || s.Contains("great ocean hwy") ||
                        s.Contains("olympic fwy") || s.Contains("la puerta fwy") ||
                        s.Contains("palomino fwy") || s.Contains("elysian fields"))
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static readonly object _randLock = new object();
        private static readonly Random _rand = new Random();

        /// <summary>
        /// Determines whether to spawn a Highway Patrol unit (State Patrol) or Local Police/Sheriff
        /// based on highway detection and the user's configured balance setting (0 - 100).
        /// Thread-safe locked Random [A10].
        /// </summary>
        public static bool ShouldSpawnHighwayUnit(Vector3 pos)
        {
            int ratio = Core.Settings.HighwayPatrolRatio;
            if (ratio <= 0) return false;
            if (ratio >= 100) return true;

            bool isHwy = IsHighway(pos);
            int roll;
            lock (_randLock)
            {
                roll = _rand.Next(100);
            }

            if (isHwy)
            {
                // On highway: base 75% highway, scaled by ratio (50 -> 75%)
                int hwyThreshold = (int)(ratio * 0.75f + 37.5f);
                return roll < Math.Min(95, Math.Max(10, hwyThreshold));
            }
            else
            {
                // On local streets/roads: base 15% highway, scaled by ratio (50 -> 15%)
                int localHwyThreshold = (int)(ratio * 0.30f);
                return roll < Math.Min(50, Math.Max(2, localHwyThreshold));
            }
        }

        /// <summary>
        /// Safe model streaming loader with timeout protection to prevent game freezes.
        /// Releases pending model requests on timeout [A15].
        /// </summary>
        public static bool SafeLoad(this Model model, int timeoutMs = 4000)
        {
            if (!model.IsValid) return false;
            try
            {
                if (model.IsLoaded) return true;
                model.Load();
                uint start = Game.GameTime;
                while (!model.IsLoaded)
                {
                    GameFiber.Yield();
                    if (Game.GameTime - start > timeoutMs)
                    {
                        Core.Logger.Warn($"Model '{model.Name}' (0x{model.Hash:X}) timed out loading after {timeoutMs}ms");
                        try
                        {
                            model.Dismiss(); // [A15] Release pending stream request on timeout
                        }
                        catch { }
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Core.Logger.Error($"SafeLoad error for model '{model.Name}'", ex);
                try { model.Dismiss(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Detects whether a coordinate is located on an elevated building rooftop, parking deck, or overhead bridge.
        /// Compares the candidate's elevation against the nearest paved ground road node (nodeType 0 or 1).
        /// If candidate is farther than 25m from a road or elevated by more than 2.2m above street level, it is flagged as rooftop/elevated.
        /// Handles legitimate underpass geometry [A20].
        /// </summary>
        public static bool IsRooftopOrElevatedStructure(Vector3 pos)
        {
            if (pos == Vector3.Zero || IsSubterraneanOrVoid(pos)) return true;

            try
            {
                // Find nearest ground-level paved road node
                bool foundRoad = NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING<bool>(
                    pos.X, pos.Y, pos.Z,
                    out Vector3 roadNode,
                    out float roadHeading,
                    0, 3.0f, 0f
                );

                if (!foundRoad || roadNode == Vector3.Zero)
                {
                    // Fallback test: any road (nodeType 1)
                    foundRoad = NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING<bool>(
                        pos.X, pos.Y, pos.Z,
                        out roadNode,
                        out roadHeading,
                        1, 3.0f, 0f
                    );
                }

                if (!foundRoad || roadNode == Vector3.Zero)
                {
                    // No road node exists within probing range -> definitely not a safe roadside location
                    return true;
                }

                // If candidate is too far horizontally from the road (e.g. interior building roof), reject
                float horizontalDist = Vector3.Distance(new Vector3(pos.X, pos.Y, 0f), new Vector3(roadNode.X, roadNode.Y, 0f));
                if (horizontalDist > 25.0f)
                {
                    return true;
                }

                // If candidate elevation is higher than the road by > 2.2m, it's on a roof or elevated parking deck.
                // For lower bound, allow underpass geometry unless extreme drop (> 15m) into void/water [A20].
                float deltaZ = pos.Z - roadNode.Z;
                if (deltaZ > 2.2f || deltaZ < -15.0f)
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"IsRooftopOrElevatedStructure exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds a safe off-road parking lot, turnout, or shoulder position away from traffic travel lanes.
        /// Uses off-road vehicle nodes (nodeType 3) and road boundary detection, strictly rejecting rooftops.
        /// </summary>
        public static bool GetSafeOffroadPosition(Vector3 searchCenter, float minDistance, float maxDistance, out Vector3 offroadPos, out float offroadHeading)
        {
            offroadPos = Vector3.Zero;
            offroadHeading = 0f;

            try
            {
                // Priority 1: Check for off-road / parking lot / turnout vehicle nodes (nodeType = 3)
                try
                {
                    bool foundOffroad = NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING<bool>(
                        searchCenter.X, searchCenter.Y, searchCenter.Z,
                        out Vector3 offroadNode,
                        out float offHeading,
                        3, 3.0f, 0f
                    );

                    if (foundOffroad && offroadNode != Vector3.Zero && !IsSubterraneanOrVoid(offroadNode) && !IsRooftopOrElevatedStructure(offroadNode))
                    {
                        float dist = offroadNode.DistanceTo(searchCenter);
                        if (dist >= minDistance && dist <= maxDistance && IsAreaClear(offroadNode, 8.0f))
                        {
                            offroadPos = offroadNode;
                            offroadHeading = offHeading;
                            return true;
                        }
                    }
                }
                catch { }

                // Priority 2: Use road boundary detection to step completely beyond the asphalt into shoulder / turnout
                if (GetSafeStreetPosition(searchCenter, 1, out Vector3 streetPos, out float streetHeading))
                {
                    try
                    {
                        bool foundBoundary = NativeFunction.Natives.GET_ROAD_BOUNDARY_USING_HEADING<bool>(
                            streetPos.X, streetPos.Y, streetPos.Z,
                            streetHeading,
                            out Vector3 boundaryPos
                        );

                        if (foundBoundary && boundaryPos != Vector3.Zero && !IsSubterraneanOrVoid(boundaryPos))
                        {
                            float hRad = streetHeading * (float)(Math.PI / 180.0);
                            Vector3 rightDir = new Vector3((float)Math.Cos(hRad), (float)Math.Sin(hRad), 0f);

                            // Push 3.0m beyond the outer road edge into dirt / shoulder
                            Vector3 shoulderSpot = boundaryPos + rightDir * 3.0f;
                            if (GetAccurateGroundZ(shoulderSpot, out float safeGZ))
                            {
                                shoulderSpot.Z = safeGZ + 0.35f;
                            }

                            if (!IsSubterraneanOrVoid(shoulderSpot) && !IsRooftopOrElevatedStructure(shoulderSpot) && IsAreaClear(shoulderSpot, 8.0f))
                            {
                                offroadPos = shoulderSpot;
                                offroadHeading = streetHeading;
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Core.Logger.Debug($"GetSafeOffroadPosition error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Checks whether a given position is located within an active vehicle travel lane or too close to street centerline.
        /// </summary>
        public static bool IsOnActiveRoadOrLane(Vector3 pos, float laneThreshold = 4.8f)
        {
            try
            {
                if (GetSafeStreetPosition(pos, 1, out Vector3 nodePos, out _))
                {
                    if (nodePos != Vector3.Zero && pos.DistanceTo(nodePos) < laneThreshold)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
