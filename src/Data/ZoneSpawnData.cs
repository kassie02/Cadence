using System;
using System.Collections.Generic;
using Rage;

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
            (new Vector3(2800.569f, 3410.84f, 55.265f), 334.474f),
            (new Vector3(1913.825f, 3626.379f, 33.324f), 297.283f)
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
            (new Vector3(-2244.31f, 4294.07f, 46.78f), 147.23f),
            (new Vector3(-2231.715f, 4319.681f, 47.999f), 158.729f)
        };

        public static readonly (Vector3 pos, float heading)[] SenoraLocal = new (Vector3, float)[]
        {
            (new Vector3(862.9f, 2705.95f, 40.25f), 88.12f),
            (new Vector3(237.97f, 2963.86f, 42.23f), 5.61f),
            (new Vector3(-673.35f, 2845.63f, 29.48f), 120.68f),
            (new Vector3(1805.17f, 2332.38f, 55.91f), 200.97f),
            (new Vector3(584.89f, 2334.08f, 48.52f), 237.15f),
            (new Vector3(2071.439f, 3055.153f, 45.603f), 243.161f)
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
            (new Vector3(1377.448f, 688.062f, 78.825f), 140.195f),
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

        // ====================================================================
        // Vehicle Pools
        // ====================================================================

        public static readonly string[] CivilianVehicles = new string[]
        {
            "premier", "felon", "asterope", "washington", "intruder", "stanier",
            "tailgater", "oracle", "fugitive", "schafter", "glendale", "primo",
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

        public static List<(Vector3 pos, float heading)[]> GetAllZones()
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

        public static bool FindBestPulloverSpot(Vector3 playerPos, float minDistance, float maxDistance, out Vector3 spotPos, out float spotHeading)
        {
            spotPos = Vector3.Zero;
            spotHeading = 0f;

            var candidates = new List<(Vector3 pos, float heading)>();

            foreach (var zone in GetAllZones())
            {
                foreach (var spot in zone)
                {
                    float dist = spot.pos.DistanceTo(playerPos);
                    if (dist >= minDistance && dist <= maxDistance)
                    {
                        candidates.Add(spot);
                    }
                }
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

        public static string GetRandomCivilianVehicle()
        {
            return CivilianVehicles[_rand.Next(CivilianVehicles.Length)];
        }

        public static string GetDefaultPoliceVehicleForPosition(Vector3 pos)
        {
            // If in Blaine County / Paleto Bay / Sandy Shores (Y > 1000)
            if (pos.Y > 1000f)
            {
                return PoliceVehiclesSheriff[_rand.Next(PoliceVehiclesSheriff.Length)];
            }

            return PoliceVehiclesCity[_rand.Next(PoliceVehiclesCity.Length)];
        }
    }
}
