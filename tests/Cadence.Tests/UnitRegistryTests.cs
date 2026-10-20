using System;
using System.Collections.Generic;
using System.Threading;
using Cadence.Common;

namespace Cadence.Tests
{
    public static class UnitRegistryTests
    {
        public static void RunAll()
        {
            Console.WriteLine("\n[RUNNING] UnitRegistryTests Suite");

            UnitRegistry.Clear();
            TestTryClaimAndIsClaimed();
            TestIdempotentClaim();
            TestConflictingClaimRejected();
            TestInvalidClaimArguments();
            TestReleaseByOwner();
            TestReleaseUnconditional();
            TestReleaseAll();
            TestConcurrentAtomicClaimContention();
            TestHighThroughputConcurrency();
            TestCrossFeatureDraftingAndRelinquishGuards();

            UnitRegistry.Clear();
            Console.WriteLine("[PASSED] All UnitRegistryTests passed successfully.");
        }

        private static void TestTryClaimAndIsClaimed()
        {
            UnitRegistry.Clear();
            const uint handle = 1001u;

            Assert.IsFalse(UnitRegistry.IsClaimed(handle), "Entity should not be claimed initially");

            bool claimed = UnitRegistry.TryClaim(handle, UnitRegistry.OwnerBackup);
            Assert.IsTrue(claimed, "Initial claim by Backup should succeed");

            Assert.IsTrue(UnitRegistry.IsClaimed(handle), "IsClaimed should return true");
            Assert.IsTrue(UnitRegistry.IsClaimedBy(handle, UnitRegistry.OwnerBackup), "IsClaimedBy Backup should be true");
            Assert.IsFalse(UnitRegistry.IsClaimedBy(handle, UnitRegistry.OwnerPursuit), "IsClaimedBy Pursuit should be false");
            Assert.IsFalse(UnitRegistry.IsClaimedByOthers(handle, UnitRegistry.OwnerBackup), "IsClaimedByOthers Backup should be false");
            Assert.IsTrue(UnitRegistry.IsClaimedByOthers(handle, UnitRegistry.OwnerPursuit), "IsClaimedByOthers Pursuit should be true");
            Assert.AreEqual(UnitRegistry.OwnerBackup, UnitRegistry.GetOwner(handle), "Owner should be Backup");
        }

        private static void TestIdempotentClaim()
        {
            UnitRegistry.Clear();
            const uint handle = 1002u;

            bool firstClaim = UnitRegistry.TryClaim(handle, UnitRegistry.OwnerPatrol);
            Assert.IsTrue(firstClaim, "First claim should succeed");
            Assert.AreEqual(1, UnitRegistry.Count, "Count should be 1");

            bool secondClaim = UnitRegistry.TryClaim(handle, UnitRegistry.OwnerPatrol);
            Assert.IsTrue(secondClaim, "Idempotent re-claim by same owner should return true");
            Assert.AreEqual(1, UnitRegistry.Count, "Count should still be 1 after idempotent claim");
        }

        private static void TestConflictingClaimRejected()
        {
            UnitRegistry.Clear();
            const uint handle = 1003u;

            bool claimBackup = UnitRegistry.TryClaim(handle, UnitRegistry.OwnerBackup);
            Assert.IsTrue(claimBackup, "Backup claim should succeed");

            bool claimPursuit = UnitRegistry.TryClaim(handle, UnitRegistry.OwnerPursuit);
            Assert.IsFalse(claimPursuit, "Pursuit claim on Backup-owned handle must be rejected");

            Assert.AreEqual(UnitRegistry.OwnerBackup, UnitRegistry.GetOwner(handle), "Owner must remain Backup");
            Assert.IsTrue(UnitRegistry.IsClaimedByOthers(handle, UnitRegistry.OwnerPursuit), "Must be claimed by others relative to Pursuit");
        }

        private static void TestInvalidClaimArguments()
        {
            UnitRegistry.Clear();

            Assert.IsFalse(UnitRegistry.TryClaim(0u, UnitRegistry.OwnerBackup), "Handle 0 must be rejected");
            Assert.IsFalse(UnitRegistry.TryClaim(1004u, null), "Null owner must be rejected");
            Assert.IsFalse(UnitRegistry.TryClaim(1004u, string.Empty), "Empty owner must be rejected");
            Assert.AreEqual(0, UnitRegistry.Count, "No claims should have been registered");
        }

        private static void TestReleaseByOwner()
        {
            UnitRegistry.Clear();
            const uint handle = 1005u;

            UnitRegistry.TryClaim(handle, UnitRegistry.OwnerStationary);

            // Wrong owner attempt to release
            bool wrongRelease = UnitRegistry.Release(handle, UnitRegistry.OwnerTrafficStop);
            Assert.IsFalse(wrongRelease, "Release by non-owner must return false");
            Assert.IsTrue(UnitRegistry.IsClaimed(handle), "Entity must still be claimed");

            // Rightful owner release
            bool rightRelease = UnitRegistry.Release(handle, UnitRegistry.OwnerStationary);
            Assert.IsTrue(rightRelease, "Release by rightful owner must return true");
            Assert.IsFalse(UnitRegistry.IsClaimed(handle), "Entity must now be unclaimed");

            // Re-release after freed
            bool reRelease = UnitRegistry.Release(handle, UnitRegistry.OwnerStationary);
            Assert.IsFalse(reRelease, "Re-release of unclaimed entity must return false");
        }

        private static void TestReleaseUnconditional()
        {
            UnitRegistry.Clear();
            const uint handle = 1006u;

            UnitRegistry.TryClaim(handle, UnitRegistry.OwnerResponse);
            Assert.IsTrue(UnitRegistry.IsClaimed(handle), "Entity should be claimed");

            bool released = UnitRegistry.Release(handle);
            Assert.IsTrue(released, "Unconditional release should return true");
            Assert.IsFalse(UnitRegistry.IsClaimed(handle), "Entity should be unclaimed");

            bool reRelease = UnitRegistry.Release(handle);
            Assert.IsFalse(reRelease, "Unconditional release of unclaimed entity should return false");
        }

        private static void TestReleaseAll()
        {
            UnitRegistry.Clear();

            UnitRegistry.TryClaim(101u, UnitRegistry.OwnerBackup);
            UnitRegistry.TryClaim(102u, UnitRegistry.OwnerBackup);
            UnitRegistry.TryClaim(201u, UnitRegistry.OwnerPatrol);
            UnitRegistry.TryClaim(202u, UnitRegistry.OwnerPatrol);
            UnitRegistry.TryClaim(301u, UnitRegistry.OwnerPursuit);

            Assert.AreEqual(5, UnitRegistry.Count, "Count should be 5");

            UnitRegistry.ReleaseAll(UnitRegistry.OwnerPatrol);

            Assert.AreEqual(3, UnitRegistry.Count, "Count should be 3 after releasing Patrol");
            Assert.IsFalse(UnitRegistry.IsClaimed(201u), "201 should be released");
            Assert.IsFalse(UnitRegistry.IsClaimed(202u), "202 should be released");
            Assert.IsTrue(UnitRegistry.IsClaimed(101u), "101 Backup should remain claimed");
            Assert.IsTrue(UnitRegistry.IsClaimed(102u), "102 Backup should remain claimed");
            Assert.IsTrue(UnitRegistry.IsClaimed(301u), "301 Pursuit should remain claimed");
        }

        private static void TestConcurrentAtomicClaimContention()
        {
            UnitRegistry.Clear();
            const uint targetHandle = 5000u;
            const int threadCount = 20;

            var startBarrier = new ManualResetEventSlim(false);
            var threads = new Thread[threadCount];
            var results = new bool[threadCount];
            var claimedOwners = new string[threadCount];

            for (int i = 0; i < threadCount; i++)
            {
                int index = i;
                string owner = $"Contender_{index}";
                claimedOwners[index] = owner;

                threads[index] = new Thread(() =>
                {
                    startBarrier.Wait();
                    results[index] = UnitRegistry.TryClaim(targetHandle, owner);
                });
                threads[index].Start();
            }

            // Release all threads simultaneously
            startBarrier.Set();

            for (int i = 0; i < threadCount; i++)
            {
                threads[i].Join();
            }

            int successCount = 0;
            string winnerOwner = null;
            for (int i = 0; i < threadCount; i++)
            {
                if (results[i])
                {
                    successCount++;
                    winnerOwner = claimedOwners[i];
                }
            }

            Assert.AreEqual(1, successCount, "Exactly ONE thread must succeed in atomic claim contention");
            Assert.IsNotNull(winnerOwner, "Winner owner must be non-null");
            Assert.AreEqual(winnerOwner, UnitRegistry.GetOwner(targetHandle), "Registered owner must match the single winning thread");
            Assert.AreEqual(1, UnitRegistry.Count, "Registry must only hold 1 claim");
        }

        private static void TestHighThroughputConcurrency()
        {
            UnitRegistry.Clear();
            const int iterationsPerThread = 500;
            const int threadCount = 8;
            var threads = new Thread[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                string owner = $"Worker_{threadId}";

                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        uint handle = (uint)((threadId * 10000) + i + 1);
                        bool claimed = UnitRegistry.TryClaim(handle, owner);
                        if (claimed)
                        {
                            bool isMine = UnitRegistry.IsClaimedBy(handle, owner);
                            if (!isMine) throw new Exception("Claim invariant broken");
                            UnitRegistry.Release(handle, owner);
                        }
                    }
                });
                threads[t].Start();
            }

            for (int t = 0; t < threadCount; t++)
            {
                threads[t].Join();
            }

            Assert.AreEqual(0, UnitRegistry.Count, "All claims should be cleaned up after high throughput test");
        }

        private static void TestCrossFeatureDraftingAndRelinquishGuards()
        {
            UnitRegistry.Clear();

            const uint patrolCruiser = 2001u;
            const uint patrolDriver = 2002u;
            const uint patrolPassenger = 2003u;

            const uint trafficStopCruiser = 3001u;
            const uint stationaryCruiser = 3002u;
            const uint responseCruiser = 3003u;
            const uint assistCruiser = 3004u;

            // Register units
            Assert.IsTrue(UnitRegistry.TryClaim(patrolCruiser, UnitRegistry.OwnerPatrol));
            Assert.IsTrue(UnitRegistry.TryClaim(patrolDriver, UnitRegistry.OwnerPatrol));
            Assert.IsTrue(UnitRegistry.TryClaim(patrolPassenger, UnitRegistry.OwnerPatrol));

            Assert.IsTrue(UnitRegistry.TryClaim(trafficStopCruiser, UnitRegistry.OwnerTrafficStop));
            Assert.IsTrue(UnitRegistry.TryClaim(stationaryCruiser, UnitRegistry.OwnerStationary));
            Assert.IsTrue(UnitRegistry.TryClaim(responseCruiser, UnitRegistry.OwnerResponse));
            Assert.IsTrue(UnitRegistry.TryClaim(assistCruiser, UnitRegistry.OwnerAssist));

            // Pursuit drafting screening: allows drafting patrol units
            Assert.IsFalse(UnitRegistry.IsClaimedByOthers(patrolCruiser, UnitRegistry.OwnerPatrol), "Patrol cruiser should not be claimed by others relative to OwnerPatrol");
            Assert.IsFalse(UnitRegistry.IsClaimedByOthers(patrolDriver, UnitRegistry.OwnerPatrol), "Patrol driver should not be claimed by others relative to OwnerPatrol");

            // Pursuit drafting screening: rejects traffic stop, stationary, response, assist
            Assert.IsTrue(UnitRegistry.IsClaimedByOthers(trafficStopCruiser, UnitRegistry.OwnerPatrol), "Traffic stop must be rejected by pursuit drafting screen");
            Assert.IsTrue(UnitRegistry.IsClaimedByOthers(stationaryCruiser, UnitRegistry.OwnerPatrol), "Stationary must be rejected by pursuit drafting screen");
            Assert.IsTrue(UnitRegistry.IsClaimedByOthers(responseCruiser, UnitRegistry.OwnerPatrol), "Response must be rejected by pursuit drafting screen");
            Assert.IsTrue(UnitRegistry.IsClaimedByOthers(assistCruiser, UnitRegistry.OwnerPatrol), "Assist must be rejected by pursuit drafting screen");

            // Backup drafting screening: only ambient patrol units drafted, others protected
            bool CanDraftIntoBackup(uint v)
            {
                if (UnitRegistry.IsClaimedBy(v, UnitRegistry.OwnerBackup)) return false;
                if (UnitRegistry.IsClaimedByOthers(v, UnitRegistry.OwnerBackup))
                {
                    string owner = UnitRegistry.GetOwner(v);
                    if (!string.Equals(owner, UnitRegistry.OwnerPatrol, StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }

            Assert.IsTrue(CanDraftIntoBackup(patrolCruiser), "Patrol unit must be eligible for backup drafting");
            Assert.IsFalse(CanDraftIntoBackup(trafficStopCruiser), "TrafficStop unit must NEVER be drafted into backup");
            Assert.IsFalse(CanDraftIntoBackup(stationaryCruiser), "Stationary unit must NEVER be drafted into backup");
            Assert.IsFalse(CanDraftIntoBackup(responseCruiser), "Response unit must NEVER be drafted into backup");
            Assert.IsFalse(CanDraftIntoBackup(assistCruiser), "Assist unit must NEVER be drafted into backup");

            // Multi-entity Relinquish protocol: cruiser, driver, passenger released cleanly
            UnitRegistry.Release(patrolDriver);
            UnitRegistry.Release(patrolPassenger);
            UnitRegistry.Release(patrolCruiser);

            Assert.IsFalse(UnitRegistry.IsClaimed(patrolCruiser), "Patrol cruiser must be unclaimed after relinquish");
            Assert.IsFalse(UnitRegistry.IsClaimed(patrolDriver), "Patrol driver must be unclaimed after relinquish");
            Assert.IsFalse(UnitRegistry.IsClaimed(patrolPassenger), "Patrol passenger must be unclaimed after relinquish");
        }
    }
}
