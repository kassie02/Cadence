using System;
using System.Collections.Generic;
using Rage;
using Cadence.Common;

namespace Cadence.Data
{
    public static class ZoneSpawnData
    {
        private static readonly Random _rand = new Random();

        // ====================================================================
        // Curated 1803 Shoulder Pullover Coordinates & Headings
        // ====================================================================

        public static readonly (Vector3 pos, float heading)[] BlaineSandyGrapeseedSenoraFwy = new (Vector3, float)[]
        {
            (new Vector3(2414.94f, 3937.05f, 35.8f), 334.23f),
            (new Vector3(2467.29f, 4386.85f, 35.55f), 160.57f),
            (new Vector3(2661.18f, 4804.91f, 33.1f), 313.96f),
            (new Vector3(2974.36f, 4526.39f, 48.59f), 173.2f),
            (new Vector3(2782.182f, 4537.769f, 55f), 14.626f),
            (new Vector3(2668.8f, 4955.232f, 44f), 11.227f),
            (new Vector3(2603.198f, 5286.061f, 44f), 17.229f),
            (new Vector3(2722.21f, 4377.139f, 47f), 300.105f),
            (new Vector3(2471.31f, 2929.05f, 40.33f), 310.99f),
            (new Vector3(2735.24f, 5010.83f, 37.16f), 198.05f),
            (new Vector3(2472.64f, 5663.7f, 44.89f), 22.88f),
            (new Vector3(2484.76f, 5510.42f, 44.35f), 197.49f),
            (new Vector3(2800.569f, 3410.84f, 55.265f), 334.474f)
        };

        public static readonly (Vector3 pos, float heading)[] BlainePaletoGreatOceanHwy = new (Vector3, float)[]
        {
            (new Vector3(3.77f, 6400.67f, 30.85f), 143.21f),
            (new Vector3(-593.35f, 5673.33f, 37.79f), 159f),
            (new Vector3(-2395.07f, 3827.19f, 23.71f), 50.84f),
            (new Vector3(1842.502f, 6371.044f, 40.054f), 79.999f),
            (new Vector3(1401.671f, 6489.43f, 19.703f), 76.983f),
            (new Vector3(571.797f, 6556.521f, 27.462f), 84.742f),
            (new Vector3(-965.713f, 5417.881f, 38.817f), 118.63f),
            (new Vector3(-1445.328f, 5071.627f, 61.249f), 127.512f),
            (new Vector3(-2244.31f, 4294.07f, 46.78f), 147.23f)
        };

        public static readonly (Vector3 pos, float heading)[] SenoraLocal = new (Vector3, float)[]
        {
            (new Vector3(862.9f, 2705.95f, 40.25f), 88.12f),
            (new Vector3(237.97f, 2963.86f, 42.23f), 5.61f),
            (new Vector3(-673.35f, 2845.63f, 29.48f), 120.68f),
            (new Vector3(1805.17f, 2332.38f, 55.91f), 200.97f),
            (new Vector3(584.89f, 2334.08f, 48.52f), 237.15f),
            (new Vector3(2071.439f, 3055.153f, 45.603f), 243.161f),
            (new Vector3(1913.825f, 3626.379f, 33.324f), 297.283f) // Alhambra Dr / Marina Dr local road
        };

        public static readonly (Vector3 pos, float heading)[] LSSouthPort = new (Vector3, float)[]
        {
            (new Vector3(869.32f, -2092.5f, 30.13f), 352.1f),
            (new Vector3(788.75f, -2113.26f, 28.95f), 354.53f),
            (new Vector3(336.71f, -2224.86f, 10.84f), 257.41f),
            (new Vector3(312.54f, -2498.57f, 5.76f), 185.23f),
            (new Vector3(-55.97f, -2615.07f, 5.79f), 93.26f),
            (new Vector3(-402.12f, -2762.43f, 5.78f), 197.66f)
        };

        public static readonly (Vector3 pos, float heading)[] LSNEHighway = new (Vector3, float)[]
        {
            (new Vector3(1585.724f, -994.692f, 59.576f), 299.646f),
            (new Vector3(2467.166f, -156.913f, 88.435f), 334.078f),
            (new Vector3(2554.416f, 777.331f, 89.473f), 14.106f),
            (new Vector3(2568.631f, 483.713f, 108.058f), 181.073f),
            (new Vector3(1817.543f, 2133.373f, 54.093f), 188.367f)
        };

        public static readonly (Vector3 pos, float heading)[] LSCoastalWest = new (Vector3, float)[]
        {
            (new Vector3(-2438.295f, -234.612f, 15.937f), 242.858f),
            (new Vector3(-3002.615f, 422.426f, 14.522f), 174.479f),
            (new Vector3(-1833.71f, -243.92f, 40.34f), 37.68f),
            (new Vector3(-2109.14f, -293.34f, 12.72f), 260.46f),
            (new Vector3(-2816.33f, 2217.85f, 28.04f), 113.83f)
        };

        public static readonly (Vector3 pos, float heading)[] LSCentral = new (Vector3, float)[]
        {
            (new Vector3(-454.835f, -545.044f, 25.118f), 340.849f),
            (new Vector3(1132.927f, -1876.68f, 30.579f), 202.668f),
            (new Vector3(93.53f, -141.05f, 54.8f), 342.33f),
            (new Vector3(-12.62f, -1147.51f, 27.08f), 271f),
            (new Vector3(-692.38f, -648.07f, 30.68f), 87.9f),
            (new Vector3(85.09f, -1442f, 28.97f), 50.17f)
        };

        public static readonly (Vector3 pos, float heading)[] LSWestSheriffRanger = new (Vector3, float)[]
        {
            (new Vector3(-1698.002f, 867.09f, 146.55f), 330.646f),
            (new Vector3(-1530.274f, 1414.908f, 122.534f), 313.971f),
            (new Vector3(-1472.943f, 2030.788f, 64.13f), 27.114f),
            (new Vector3(-1363.992f, 2161.21f, 51.292f), 329.349f),
            (new Vector3(-1153.751f, 2641.986f, 15.755f), 133.056f),
            (new Vector3(-1940.944f, 657.573f, 123.814f), 152.401f)
        };

        public static readonly (Vector3 pos, float heading)[] LSIA = new (Vector3, float)[]
        {
            (new Vector3(-848.91f, -2232.03f, 17.27f), 127.91f),
            (new Vector3(-1018.27f, -2481.37f, 19.65f), 148.94f),
            (new Vector3(-1020.92f, -2504.28f, 13.35f), 148.61f),
            (new Vector3(-871.255f, -2421.678f, 13.473f), 71.364f),
            (new Vector3(-1047.148f, -2715.13f, 13.209f), 240.53f),
            (new Vector3(-868.342f, -2633.694f, 13.49f), 328.651f),
            (new Vector3(-572.982f, -2211.542f, 5.464f), 139.273f),
            (new Vector3(-1000.008f, -2098.143f, 11.864f), 101.621f),
            (new Vector3(-1093.378f, -2025.239f, 12.553f), 213.849f),
            (new Vector3(-781.195f, -2177.081f, 15.272f), 133.228f),
            (new Vector3(-1081.49f, -2589.566f, 19.605f), 150.086f)
        };

        public static readonly (Vector3 pos, float heading)[] BlaineParkRanger = new (Vector3, float)[]
        {
            (new Vector3(-2205f, 2313.7f, 32.68f), 119.81f),
            (new Vector3(-1398.83f, 5102.84f, 60.52f), 121.63f),
            (new Vector3(-670.55f, 5894.67f, 16.16f), 348.36f),
            (new Vector3(1400.65f, 4485.89f, 51.61f), 262.84f),
            (new Vector3(2941.37f, 5048.36f, 30.17f), 95.21f)
        };

        public static readonly (Vector3 pos, float heading)[] LSUrbanPoliceStations = new (Vector3, float)[]
        {
            (new Vector3(430.73f, -1025.26f, 28.84f), 90.0f),    // Mission Row PD Rear Lot
            (new Vector3(452.12f, -1019.55f, 28.47f), 91.5f),    // Mission Row PD Side Lot
            (new Vector3(377.16f, -1612.33f, 29.29f), 320.0f),   // Davis PD / Sheriff Lot
            (new Vector3(640.42f, 1.43f, 82.79f), 245.0f),       // Vinewood PD Front Lot
            (new Vector3(-574.62f, -145.98f, 37.95f), 29.0f),    // Rockford Hills City Hall PD
            (new Vector3(-1107.52f, -846.75f, 19.32f), 127.0f),  // Vespucci PD Lot
            (new Vector3(1853.25f, 3687.97f, 33.80f), 213.0f),   // Sandy Shores Sheriff Lot
            (new Vector3(-448.24f, 6012.31f, 31.72f), 225.0f),   // Paleto Bay Sheriff Lot
            (new Vector3(-1635.8f, -1020.2f, 13.1f), 50.0f),     // Del Perro Police / Plaza
            (new Vector3(827.4f, -1290.1f, 28.2f), 180.0f)       // La Mesa Precinct Lot
        };

        public static readonly (Vector3 pos, float heading)[] LSCommercialPlazas = new (Vector3, float)[]
        {
            (new Vector3(2555.22f, 380.78f, 108.62f), 90.0f),    // 24/7 Palomino Rest Stop
            (new Vector3(-3040.6f, 583.9f, 7.91f), 19.0f),       // 24/7 Ineseno Banham Canyon
            (new Vector3(1707.9f, 4920.6f, 42.06f), 324.0f),     // 24/7 Senora Fwy / Desert
            (new Vector3(546.4f, 2669.8f, 42.16f), 96.0f),       // 24/7 Route 68 Harmony
            (new Vector3(378.2f, 333.3f, 103.57f), 255.0f),      // 24/7 Clinton Ave Vinewood
            (new Vector3(24.4f, -1346.7f, 29.5f), 270.0f),       // 24/7 Strawberry Innocence
            (new Vector3(-709.6f, -904.2f, 19.22f), 88.0f),      // LTD Gasoline Little Seoul
            (new Vector3(1701.3f, 6416.7f, 32.76f), 245.0f),     // LTD Gasoline Grapeseed
            (new Vector3(-48.4f, -1757.2f, 29.4f), 50.0f),       // LTD Gas Grove Street
            (new Vector3(-1221.1f, -916.2f, 11.33f), 35.0f),     // Robs Liquor Chumash
            (new Vector3(-3154.2f, 1129.8f, 20.86f), 65.0f),     // Chumash Plaza Shopping
            (new Vector3(-438.3f, 1076.1f, 327.68f), 340.0f),    // Galileo Observatory
            (new Vector3(-1680.1f, -1071.3f, 13.15f), 320.0f),   // Del Perro Pier Lot
            (new Vector3(-354.5f, -134.8f, 39.01f), 70.0f),      // LS Customs Burton Lot
            (new Vector3(1043.2f, -776.4f, 57.94f), 140.0f),     // Mirror Park Shopping
            (new Vector3(-253.9f, -2005.1f, 30.15f), 105.0f),    // Maze Bank Arena South
            (new Vector3(-2260.5f, 264.4f, 174.6f), 85.0f),      // Kortz Center Lot
            (new Vector3(155.8f, -3084.2f, 5.9f), 90.0f),        // Port of LS Terminal
            (new Vector3(-1034.4f, -2733.8f, 13.76f), 330.0f),   // LSIA Airport Drop-off
            (new Vector3(-2178.6f, -394.2f, 13.3f), 235.0f),     // Pacific Bluffs Country Club
            (new Vector3(-1818.4f, 794.1f, 138.2f), 130.0f),     // Richman Glen Shopping
            (new Vector3(-680.2f, -245.8f, 36.8f), 35.0f),       // Rockford Hills Mall Lot
            (new Vector3(-1390.5f, -290.4f, 42.9f), 215.0f),     // Morningwood Plaza
            (new Vector3(-150.2f, -860.5f, 35.8f), 160.0f),      // Pillbox Hill Garage
            (new Vector3(215.4f, -870.2f, 30.5f), 70.0f),        // Legion Square Plaza
            (new Vector3(280.8f, -180.5f, 54.2f), 340.0f)        // Hawick Ave Retail Lot
        };

        public static readonly (Vector3 pos, float heading)[] BlaineCountyCommercialAndLots = new (Vector3, float)[]
        {
            // Sandy Shores Sheriff & Medical Hub
            (new Vector3(1853.25f, 3687.97f, 33.80f), 213.0f),   // Sandy Shores Sheriff Front Lot
            (new Vector3(1840.4f, 3698.8f, 33.8f), 30.0f),       // Sandy Shores Sheriff Rear Lot
            (new Vector3(1836.3f, 3672.8f, 34.27f), 210.0f),     // Sandy Shores Medical Clinic Lot
            // Sandy Shores Commercial, Shore & Marina
            (new Vector3(1327.2f, 4268.5f, 31.5f), 190.0f),      // Marina Dr Boat House Lot
            (new Vector3(1707.9f, 3774.5f, 34.5f), 215.0f),      // Marina Dr Store Lot
            (new Vector3(1960.5f, 3740.2f, 32.3f), 300.0f),      // Sandy Shores 24/7 / Liquors Lot
            (new Vector3(1975.2f, 3817.5f, 33.4f), 120.0f),      // Ace Liquor Lot
            // Sandy Shores Airfield Specialized Aprons
            (new Vector3(1738.2f, 3291.5f, 41.1f), 195.0f),      // Airfield West Hangar Apron
            (new Vector3(1710.5f, 3270.8f, 41.1f), 105.0f),      // Trevor's Hangar Turnout
            (new Vector3(1845.8f, 3266.4f, 41.3f), 105.0f),      // Airfield East Apron
            // Route 68 & Grand Senora Desert
            (new Vector3(1982.5f, 3053.8f, 47.2f), 58.0f),       // Route 68 Yellow Jack Inn Lot
            (new Vector3(546.4f, 2669.8f, 42.16f), 96.0f),       // Route 68 Harmony 24/7 & Gas
            (new Vector3(593.2f, 2736.5f, 42.0f), 180.0f),       // Route 68 Harmony Discount Store
            (new Vector3(1198.8f, 2673.5f, 37.8f), 90.0f),       // Route 68 Motel & Truck Stop
            (new Vector3(2530.1f, 2623.4f, 37.9f), 115.0f),      // Senora Way Quarry Overlook
            (new Vector3(1707.9f, 4920.6f, 42.06f), 324.0f),     // Senora Fwy Rest Stop / Diner
            // Grapeseed
            (new Vector3(1701.3f, 6416.7f, 32.76f), 245.0f),     // Grapeseed LTD Gas Station
            (new Vector3(2444.1f, 4976.2f, 46.8f), 45.0f),       // Grapeseed Silos / Farm Turnout
            (new Vector3(1679.5f, 4884.2f, 41.9f), 95.0f),       // Grapeseed Main St Plaza
            // Paleto Bay & Northern Coast
            (new Vector3(-448.24f, 6012.31f, 31.72f), 225.0f),   // Paleto Bay Sheriff Station Lot
            (new Vector3(-139.5f, 6296.8f, 31.5f), 45.0f),       // Paleto Bay Clucking Bell Lot
            (new Vector3(105.8f, 6622.1f, 31.8f), 270.0f),       // Paleto Bay Beeker's Garage
            (new Vector3(-110.2f, 6467.5f, 31.6f), 135.0f),      // Paleto Bay Bank / Shopping Lot
            (new Vector3(-742.1f, 5595.3f, 41.6f), 90.0f),       // Paleto Bay Cable Car Lot
            (new Vector3(-285.5f, 6625.2f, 7.5f), 215.0f),       // Great Ocean Hwy Vista Point (North)
            (new Vector3(-2048.5f, 3144.2f, 32.8f), 240.0f),     // Fort Zancudo Route 68 Gate Turnout
            (new Vector3(-3154.2f, 1129.8f, 20.86f), 65.0f),     // Chumash Plaza Shopping
            (new Vector3(-3040.6f, 583.9f, 7.91f), 19.0f),       // 24/7 Ineseno Banham Canyon
            (new Vector3(2555.22f, 380.78f, 108.62f), 90.0f)     // 24/7 Palomino Rest Stop
        };

        // ====================================================================
        // Vehicle Pools
        // ====================================================================

        public static readonly string[] CivilianVehicles = new string[]
        {
            "premier", "felon", "asterope", "washington", "intruder", "stanier",
            "tailgater", "oracle", "fugitive", "schafter2", "glendale", "primo",
            "ingot", "asea", "emperor", "regina", "stratum", "surge", "cognoscenti"
        };

        public static readonly string[] PoliceVehiclesCity = new string[]
        {
            "police", "police2", "police3", "police4"
        };

        public static readonly string[] PoliceVehiclesSheriff = new string[]
        {
            "sheriff", "sheriff2"
        };

        public static readonly string[] PoliceVehiclesHighway = new string[]
        {
            "police", "police2", "police3", "sheriff"
        };

        // ====================================================================
        // Query & Selection Helpers
        // ====================================================================

        public static List<(Vector3 pos, float heading)[]> GetRoadsidePulloverZones()
        {
            return new List<(Vector3 pos, float heading)[]>
            {
                BlaineSandyGrapeseedSenoraFwy,
                BlainePaletoGreatOceanHwy,
                SenoraLocal,
                LSSouthPort,
                LSNEHighway,
                LSCoastalWest,
                LSCentral,
                LSWestSheriffRanger,
                LSIA,
                BlaineParkRanger
            };
        }

        public static List<(Vector3 pos, float heading)[]> GetStationaryStandbyZones()
        {
            return new List<(Vector3 pos, float heading)[]>
            {
                LSUrbanPoliceStations,
                LSCommercialPlazas,
                BlaineCountyCommercialAndLots
            };
        }

        public static List<(Vector3 pos, float heading)[]> GetAllZones()
        {
            var all = GetRoadsidePulloverZones();
            all.AddRange(GetStationaryStandbyZones());
            return all;
        }

        // ====================================================================
        // Curated Urban / Station / Highway Standby Locations (LivingCity Parity + Urban Additions)
        // ====================================================================

        public static readonly (Vector3 pos, float heading)[] CuratedStandbyLocations = new (Vector3, float)[]
        {
            // LivingCity Imported Classics
            (new Vector3(158.3923f, -1037.217f, 28.82793f), 349.75f),
            (new Vector3(339.248f, -1069.791f, 29.13886f), 319f),
            (new Vector3(-169.1959f, -788.6f, 31.53021f), 160f),
            (new Vector3(-650.3652f, -106.6397f, 37.6001f), 119.2461f),
            (new Vector3(-424.3228f, -252.1425f, 35.9443f), 302.2544f),
            (new Vector3(-622.5905f, -858.6013f, 24.75938f), 333.297f),
            (new Vector3(-1371.671f, -1100.256f, 4.041965f), 216.1536f),
            (new Vector3(383.7046f, 152.3005f, 102.8051f), 90.2484f),
            (new Vector3(-972.1848f, -2403.493f, 13.56046f), 151.065f),
            (new Vector3(777.4424f, -2435.339f, 19.88655f), 86.43924f),
            (new Vector3(-40.49415f, -1653.803f, 28.9792f), 320.4488f),
            (new Vector3(-674.9916f, 1920.143f, 146.5822f), 52.57489f),
            (new Vector3(302.2287f, 2564.622f, 43.78382f), 281.1136f),
            (new Vector3(-1688.955f, 2455.253f, 29.68973f), 143.5546f),
            (new Vector3(1723.315f, 3495.78f, 36.68676f), 144.5651f),
            (new Vector3(1719.981f, 1474.907f, 84.66255f), 350.2014f),
            (new Vector3(157.1846f, 6562.589f, 31.49028f), 296.3776f),
            (new Vector3(-378.4719f, 5977.127f, 31.20931f), 318.4682f),
            (new Vector3(-1328.872f, -396.8892f, 36.12023f), 30.11269f),
            (new Vector3(298.9492f, -268.1412f, 53.68773f), 320.6631f),
            (new Vector3(2166.917f, 4898.333f, 40.45398f), 291.9043f),
            (new Vector3(-405.9179f, 1192.172f, 325.3149f), 94.66913f),
            (new Vector3(2499.215f, 4199.535f, 39.55725f), 165.1826f),
            // Sandy Shores & Blaine County Standby Hubs
            (new Vector3(1853.25f, 3687.97f, 33.80f), 213.0f),   // Sandy Shores Sheriff Lot
            (new Vector3(1836.3f, 3672.8f, 34.27f), 210.0f),     // Sandy Shores Clinic Lot
            (new Vector3(1738.2f, 3291.5f, 41.1f), 195.0f),      // Sandy Airfield West Apron
            (new Vector3(1845.8f, 3266.4f, 41.3f), 105.0f),      // Sandy Airfield East Apron
            (new Vector3(1707.9f, 3774.5f, 34.5f), 215.0f),      // Marina Dr Store Lot
            (new Vector3(1960.5f, 3740.2f, 32.3f), 300.0f),      // Sandy Shores 24/7 Lot
            (new Vector3(1982.5f, 3053.8f, 47.2f), 58.0f),       // Yellow Jack Inn Lot
            (new Vector3(546.4f, 2669.8f, 42.16f), 96.0f),       // Route 68 Harmony Gas
            (new Vector3(1198.8f, 2673.5f, 37.8f), 90.0f),       // Route 68 Motel Lot
            (new Vector3(1701.3f, 6416.7f, 32.76f), 245.0f),     // Grapeseed LTD Lot
            (new Vector3(-448.24f, 6012.31f, 31.72f), 225.0f),   // Paleto Bay Sheriff Lot
            (new Vector3(105.8f, 6622.1f, 31.8f), 270.0f)        // Paleto Bay Beeker's Garage
        };

        public static bool FindBestStandbyLocation(Vector3 playerPos, float minDistance, float maxDistance, out Vector3 spotPos, out float spotHeading)
        {
            spotPos = Vector3.Zero;
            spotHeading = 0f;

            var candidates = new List<(Vector3 pos, float heading)>();

            // Pass 1: Strict range (minDistance to maxDistance)
            CollectStandbyCandidates(playerPos, minDistance, maxDistance, candidates);

            // Pass 2: Progressive expansion if no curated spot in immediate range (up to 450m)
            if (candidates.Count == 0 && maxDistance < 450.0f)
            {
                CollectStandbyCandidates(playerPos, maxDistance, 450.0f, candidates);
            }

            if (candidates.Count > 0)
            {
                int index = _rand.Next(candidates.Count);
                spotPos = candidates[index].pos;
                spotHeading = candidates[index].heading;
                return true;
            }

            return false;
        }

        private static void CollectStandbyCandidates(Vector3 playerPos, float minDistance, float maxDistance, List<(Vector3 pos, float heading)> candidates)
        {
            foreach (var spot in CuratedStandbyLocations)
            {
                float dist = spot.pos.DistanceTo(playerPos);
                if (dist >= minDistance && dist <= maxDistance && WorldUtils.IsAreaClear(spot.pos, 9.0f))
                {
                    candidates.Add(spot);
                }
            }

            foreach (var zone in GetStationaryStandbyZones())
            {
                foreach (var spot in zone)
                {
                    float dist = spot.pos.DistanceTo(playerPos);
                    if (dist >= minDistance && dist <= maxDistance && WorldUtils.IsAreaClear(spot.pos, 9.0f))
                    {
                        candidates.Add(spot);
                    }
                }
            }
        }

        public static bool FindBestPulloverSpot(Vector3 playerPos, float minDistance, float maxDistance, out Vector3 spotPos, out float spotHeading)
        {
            spotPos = Vector3.Zero;
            spotHeading = 0f;

            var candidates = new List<(Vector3 pos, float heading)>();

            // Pass 1: Strict range
            CollectPulloverCandidates(playerPos, minDistance, maxDistance, candidates);

            // Pass 2: Progressive expansion if no spot found (up to 450m)
            if (candidates.Count == 0 && maxDistance < 450.0f)
            {
                CollectPulloverCandidates(playerPos, maxDistance, 450.0f, candidates);
            }

            if (candidates.Count > 0)
            {
                int index = _rand.Next(candidates.Count);
                spotPos = candidates[index].pos;
                spotHeading = candidates[index].heading;
                return true;
            }

            return false;
        }

        private static void CollectPulloverCandidates(Vector3 playerPos, float minDistance, float maxDistance, List<(Vector3 pos, float heading)> candidates)
        {
            foreach (var zone in GetRoadsidePulloverZones())
            {
                foreach (var spot in zone)
                {
                    float dist = spot.pos.DistanceTo(playerPos);
                    if (dist >= minDistance && dist <= maxDistance && WorldUtils.IsAreaClear(spot.pos, 9.0f))
                    {
                        candidates.Add(spot);
                    }
                }
            }
        }

        public static string GetRandomCivilianVehicle()
        {
            return CivilianVehicles[_rand.Next(CivilianVehicles.Length)];
        }

        public static string GetDefaultPoliceVehicleForPosition(Vector3 pos)
        {
            if (WorldUtils.IsHighway(pos))
            {
                return PoliceVehiclesHighway[_rand.Next(PoliceVehiclesHighway.Length)];
            }

            if (WorldUtils.IsBlaineCounty(pos))
            {
                return PoliceVehiclesSheriff[_rand.Next(PoliceVehiclesSheriff.Length)];
            }

            return PoliceVehiclesCity[_rand.Next(PoliceVehiclesCity.Length)];
        }

        public static string GetDefaultCopPedModelForPosition(Vector3 pos)
        {
            if (WorldUtils.IsBlaineCounty(pos))
            {
                return "s_m_y_sheriff_01";
            }

            return "s_m_y_cop_01";
        }

        public static string GetDefaultStatePatrolVehicle()
        {
            return PoliceVehiclesHighway[_rand.Next(PoliceVehiclesHighway.Length)];
        }

        public static string GetDefaultStatePatrolPed()
        {
            return "s_m_y_hwaycop_01";
        }
    }
}
