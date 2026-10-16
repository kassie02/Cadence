# AUDIT: PursuitFeature, PatrolFeature, AssistFeature
Files covered: src\Features\PursuitFeature.cs, src\Features\PatrolFeature.cs, src\Features\AssistFeature.cs
CODING_STANDARDS.md: Read in full as baseline.

## Severity Definitions
- CRITICAL: Crash risk, game-breaking, violates rules causing NREs or deadlocks
- HIGH: Correctness, LSPDFR compliance, resource leaks, cross-feature ownership
- MEDIUM: Native correctness, subtle logic, configuration
- LOW: Style, minor inefficiencies

## CRITICAL

### C1 - Pursuit disenrollment ordering / TaskClear before RemovePedFromPursuit (potential NRE)
- Severity: Critical
- Category: LSPDFR integration / Pursuit disenrollment
- File: src\Features\PursuitFeature.cs:1302-1318 (DismissFromScene) and 1398-1415 (BeginStandDown)
- Description: Dismissing/removing units from pursuit calls Functions.RemovePedFromPursuit(Driver) and Functions.RemovePedFromPursuit(Passenger) (1310, 1314, 1406, 1410) while tasks still active or right before entering/cruising. However, critical rule: "Whenever an ambient or backup unit is dismissed, all enrolled officers must be removed from active pursuit AI via Functions.RemovePedFromPursuit(ofc) before tasks are cleared (ofc.Tasks.Clear())". 
- Why: Clearing tasks while officers are still managed by LSPDFR pursuit AI causes unhandled NullReferenceException inside LSPDFR (TaskCopChasePedAdv.cs:1515). 
- Recommendation: Before calling Tasks.Clear() (or before re-tasking to enter/cruise in a way that LSPDFR loses ownership predictably), ensure RemovePedFromPursuit was called for any officer who was in pursuit. The code does call RemovePedFromPursuit in DismissFromScene/BeginStandDown (good), but verify: in DismissFromScene (1317 onward) after removing from pursuit, code may call Driver.Tasks.EnterVehicle/CruiseWithVehicle etc - but it does NOT call Tasks.Clear() explicitly; it uses Task chains. Need to verify no path clears tasks after pursuit context lost incorrectly. Also note pursuit was ended? Active pursuit may still exist. Also AssistFeature/PursuitUnitSession dismissal order: in PursuitUnitSession.DismissFromScene, the fiber does RemovePedFromPursuit first (1302-1318), then clears BlockPermanentEvents, tasks them to enter vehicle, then later cruises. The critical ordering rule is "before tasks are cleared" - calling EnterVehicle is a new task; that’s fine. But we must confirm no code path does Tasks.Clear() prior to RemovePedFromPursuit when enrolled. Look for Tasks.Clear() in Pursuit contexts: in DraftCruiserIntoPursuit (1001, 1008) it does driver.Tasks.Clear() before AddCopToPursuit - that’s correct (clearing old ambient tasks before enrollment). No obvious violation in pursuit dismissal paths shown. However, this is explicitly called out in standards as common crash - need to flag as potential issue if any other path exists (e.g. ReleaseAllPursuitUnits doesn’t call RemovePedFromPursuit; Dispose just dismisses/deletes entities). 
- Needs verification: Purge/Dispose paths - if units are still enrolled when plugin stops/unloads, no RemovePedFromPursuit called; LSPDFR may crash on shutdown? Also Stop() calls ReleaseAllPursuitUnits(immediateDelete:false) - session.Dispose doesn’t remove from pursuit. That’s a CRITICAL issue on teardown.

### C2 - PursuitFeature.Stop()/PurgeAll() do not remove officers from LSPDFR pursuit on teardown
- Severity: Critical
- Category: LSPDFR integration / Teardown
- File: src\Features\PursuitFeature.cs:56-88 (Stop/PurgeAll), 1034-1047 (ReleaseAllPursuitUnits), 1447-1501 (Dispose)
- Description: Stop() does ReleaseAllPursuitUnits(immediateDelete: false); PurgeAll does immediateDelete true. Neither attempts to remove enrolled officers from active pursuit via Functions.RemovePedFromPursuit. Dispose() only cleans blips/entities, sets sirens off - no disenrollment.
- Why: If officers are still in an active LSPDFR pursuit when plugin unloads/feature stops, LSPDFR’s AI may continue referencing peds/tasks; combined with entity deletion/dismissal, can trigger NREs (TaskCopChasePedAdv.cs:1515) on shutdown. Standards explicitly require removal before clearing tasks; on teardown same risk applies.
- Recommendation: In ReleaseAllPursuitUnits (or before disposing sessions), iterate sessions and for any officer currently in pursuit call Functions.RemovePedFromPursuit (safely wrapped). Do this even if pursuit ended? Or query active pursuit. Also consider calling for driver/passenger if IsPedInPursuit. Must do before Dispose’s entity cleanup that might interfere; but ordering: disenroll first, then clear tasks/dismiss entities.

## HIGH

### H1 - Cross-feature relinquish/drafting ownership race (ghost drafting prevention incomplete)
- Severity: High
- Category: Cross-feature ownership / Ghost drafting
- File: src\Features\PursuitFeature.cs:930-976 (FindCandidateAmbientCruiser), 944-968 (filters)
- Description: Filters exclude TrafficStopFeature.IsActiveCruiser, ResponseFeature.IsActiveCruiser, BackupFeature.IsBackupCruiser. Also excludes patrol/stationary via PatrolFeature.IsActiveCruiser(v) || StationaryFeature.IsActiveCruiser(v) (951). Correct. But when drafting from ambient pool, code calls PatrolFeature.RelinquishCruiser(cruiser) etc only if !wasSpawned (982-988). Also sets MakePersistent on cruiser/driver/passenger (992-994). Good. 
- Issue: Race between features - a cruiser might be transitioning between states between the time filter passes and DraftCruiserIntoPursuit runs. Also AssistFeature may be about to engage same cruiser? Assist scans separately; pursuit doesn’t check Assist ownership. Assist checks Backup/Pursuit/TrafficStop before engaging (183-187 in Assist) - but Pursuit doesn’t check Assist. That’s asymmetric.
- Why: Could cause dual ownership (Assist engages cruiser while Pursuit drafts it) leading to conflicting tasks, blip ownership conflicts, or invalid session states.
- Recommendation: PursuitFeature.FindCandidateAmbientCruiser should also check AssistFeature.IsActiveCruiser(cruiser) (or equivalent). Verify AssistFeature exposes such check? AssistFeature tracks sessions by cruiser - no public IsActiveCruiser exposed in shown code; it does lock(_syncLock){ if(_activeSessions.Any(s=>s.Cruiser==cruiser)) continue; } internally (204-208). So Pursuit cannot see it. Add symmetric ownership check (either add public API in AssistFeature, or have a shared ownership registry).

### H2 - Lock held across fiber operations? Check syncLock usage
- Severity: High (per CODING_STANDARDS "Zero-Lock-Across-Yields Rule")
- Category: Concurrency & fiber safety
- File: multiple locations
- Description: Standard requires never hold C# monitor lock across GameFiber.Yield(), GameFiber.Sleep(), model.LoadAndWait(), streaming ops. 
- Check Pursuit: Start() holds lock while creating fiber (46-54) - fine (no yield). Stop() holds lock while aborting fiber (59-79). GetActiveCount() holds lock while iterating/removing (92-97) - no yield. DraftCruiserIntoPursuit locks when adding session (1019-1023) - no yield. Many places lock for short reads/writes. Session.Update() runs inside locked loop? In PursuitProcessLoop: after transition checks, does lock(_syncLock){ foreach(var s in _activeSessions){ ... s.UpdatePursuitBlip... s.UpdateBlipStates()... } } (304-317) - calling instance methods while holding lock; those methods don’t yield (just native calls). Also PruneAndWatchSessions locks entire loop (586-627). ProcessStandbyDismissal copies snapshot (669-672) - releases lock before further work (good). DismissStandbyUnits locks while iterating (718-735). IsPursuitAtMaxCapacity locks to count drafted (741-745). 
- Potential issue: calling methods that might spawn fibers? DraftCruiserIntoPursuit does NOT spawn fiber; DismissFromScene spawns GameFiber.StartNew(...) (1297-1378) AFTER releasing locks? In DismissStandbyUnits: locks, sets anyDismissed true (724-728); if true displays notification after lock? (730-734) - after lock ends (lock block ends at 729). In DismissFromScene (the session method), when called from locked context? DismissStandbyUnits calls s.DismissFromScene() while holding lock (723-728) - so it calls the method while lock held; the method spawns a new GameFiber (1297) - spawning fiber is not yielding/sleeping, so lock not held across yield. That’s OK. But be careful if fiber later accesses shared state? The spawned fiber accesses cruiser/driver etc (reads) but doesn’t take _syncLock in shown code - it accesses session fields directly. That’s a potential race with other code modifying session state (session methods called from locked contexts elsewhere). Also UpdateBlipStates called from locked loop (314) - fine.
- PatrolFeature: similar patterns - RunWatchdog takes snapshot (260-263, releases lock) then iterates snapshot (good). RunSpawningCycle reads count under lock (342-345), releases before spawning? spawning happens after lock (364-375) - good. RelinquishCruiser holds lock while modifying list (179-192). Ensure no lock held across yields - Patrol loops Sleep in fiber (226) outside locks; all locked sections are short.
- AssistFeature: locks for adds/removes (204-208, 370-372, 144-163) - short. UpdateActiveEngagements locks while calling session.Update() and removing (143-163). session.Update() doesn’t yield - OK.
- Overall: lock usage appears mostly compliant. No obvious Yield/Sleep inside locked blocks. Good.
- Recommendation: continue to enforce snapshot pattern (as done). Also be cautious spawning fibers from locked context (DismissFromScene does it while caller may hold lock? caller is DismissStandbyUnits which holds lock when calling s.DismissFromScene() - so fiber spawn happens while lock held; spawn is cheap, no yield - technically allowed? "Never hold lock across fiber yield or blocking operation" - GameFiber.StartNew is not yield/sleep; it’s starting fiber. Probably fine. But better to avoid spawning while holding lock.)

### H3 - PatrolFeature.RelinquishCruiser/others clear tasks but not remove from pursuit if enrolled
- Severity: High
- Category: LSPDFR / Cross-feature
- File: src\Features\PatrolFeature.cs:174-194 (RelinquishCruiser), 677-708 (Relinquish), 710-753 (Dispose)
- Description: Relinquish() clears Driver/Passenger tasks (698-706) and deletes entity blips; Dispose also cleans up. But if the officer was drafted into LSPDFR pursuit/backup by external means while Cadence still thinks it “owns” in some cases? Or more directly: when external feature takes cruiser (Pursuit drafts ambient cruiser), Pursuit does RelinquishCruiser on other features (982-988). That’s correct - relinquishing means “drop ownership”. But if the relinquished officer was enrolled in LSPDFR pursuit by someone else? Probably not the case here. Also Patrol units can be “drafted by LSPDFR” (watchdog checks IsCopBusy/InPursuit and skips despawn - 296-317 in RunWatchdog) - so Cadence preserves them. But if they remain owned by PatrolFeature while in LSPDFR pursuit, calling Dispose on them later (despawn when far away) would dismiss/delete without calling RemovePedFromPursuit - same C2 issue.
- Why: Any code path that dismisses/deletes officers who may be enrolled in LSPDFR pursuit must remove them first.
- Recommendation: Before deleting/dismissing officers in any feature’s Dispose/cleanup, check if Functions.IsPedInPursuit(driver/passenger) is true and if so call Functions.RemovePedFromPursuit. Wrap safely. Apply to Patrol.Dispose, Assist.Dispose, Pursuit.Dispose, etc.

### H4 - AssistFeature does not relinquish ownership from Patrol/Stationary/Pursuit before taking cruiser (good), but also doesn’t check if cruiser is in Assist? And doesn’t remove from pursuit on cleanup
- Severity: High
- Category: Ownership + LSPDFR
- File: src\Features\AssistFeature.cs:302-382 (MobilizeCruiserDefense), 592-636 (Dispose)
- Description: MobilizeCruiserDefense explicitly relinquishes from Patrol/Stationary/TrafficStop/Response (307-310) - good. Checks PursuitFeature.IsPursuitCruiser (183-187) - good (won’t take pursuit cruisers). But doesn’t check if already in Assist (it does check internal sessions - 204-208, good). 
- Issue: On Dispose (592-636), AssistFeature stops bringing vehicle to halt, releases handbrake, sets silent emergency lights false (606-610), clears BlockPermanentEvents for officers, dismisses/deletes (614-626). No RemovePedFromPursuit call. If officers somehow got enrolled in LSPDFR pursuit during combat (rare but possible), this could cause NRE on dismissal.
- Recommendation: In Assist.Dispose, before dismissing officers, check Functions.IsPedInPursuit(ofc) and call RemovePedFromPursuit(ofc). Same for other features.

### H5 - Weapon hash integrity: hardcoded WeaponCombatPistol = 0x5EF9FEC4
- Severity: High
- Category: Weapon hash integrity (per CODING_STANDARDS)
- File: src\Features\PursuitFeature.cs:27 (private const uint WeaponCombatPistol = 0x5EF9FEC4;), AssistFeature.cs:24-27 (has WeaponCombatPistol, WeaponPistol, WeaponUnarmed correct 0xA2719263)
- Description: Standards say "All weapon hashes must match alloc8or / GTA V Jenkins One-at-a-Time (joaat) calculations. WEAPON_UNARMED is strictly 0xA2719263 (joaat("weapon_unarmed")). The corrupted hash 0xA2719248 is permanently banned. Holstering must always be verified via SET_CURRENT_PED_WEAPON(officer, 0xA2719263, true)."
- Check: 0x5EF9FEC4 for WEAPON_COMBATPISTOL - joaat("weapon_combatpistol") gives 0x5EF9FEC4 (correct). Assist also defines WeaponPistol 0x1B06D571 (correct). WeaponUnarmed correct. 
- Issue: PursuitFeature defines WeaponCombatPistol but never uses it (1244-1246, 1271-1273 in PursuitUnitSession use it - yes it’s in same class scope). Also AssistFeature uses it. Looks correct. But the “corrupted hash 0xA2719248 banned” rule means we must scan for any occurrence. Let us verify no banned hash appears. 
- Scan check needed: search for 0xA2719248. (Not seen in these files.)
- But also “Holstering must always be verified via SET_CURRENT_PED_WEAPON(officer, 0xA2719263, true)”. Assist does this on InitiateStandDown (538). Pursuit doesn’t holster to unarmed on dismissal - it just tasks to enter/cruise; officers may keep weapons drawn. Is that required? Standards say “Holstering must always be verified...” - this is stated as a rule for holstering operations. So when holstering, must use that exact call. If feature doesn’t holster, rule doesn’t apply. But worth noting. Not necessarily violation.
- Overall: hashes look correct.

## MEDIUM

### M1 - Native invocation style / generic return type abuse risk? Check calls
- Severity: Medium
- Category: Native invocation correctness
- File: PursuitFeature: 1234-1238, 1244-1246, 1261-1267, 1271-1273 use NativeFunction.Natives.IS_PED_USING_ANY_SCENARIO<bool>(Driver), TASK_START_SCENARIO_IN_PLACE, GIVE_WEAPON_TO_PED, SET_CURRENT_PED_WEAPON - all called as method-style with explicit generic for bool where appropriate? IS_PED_USING_ANY_SCENARIO<bool> is fine (returns bool). Also uses NativeTasks.* helpers in many places (NativeTasks.SetSilentEmergencyLights etc) - that’s fine if they follow standards. 
- Also: NativeFunction.Natives.GIVE_WEAPON_TO_PED(Driver, WeaponCombatPistol, 120, false, true) - parameter types look right (int count 120 is fine as int/uint in RPH). Float literals: 16.0f used (1354, 1422) - correct explicit float.
- No void-native called with generic return type args visible. No raw hex calls. Canonical names used via NativeFunction.Natives with proper names? e.g. IS_PED_USING_ANY_SCENARIO - that’s canonical name. Looks compliant.
- PatrolFeature: uses NativeFunction.Natives for SET_VEHICLE_FORWARD_SPEED (499), TASK_PAUSE (494), etc - OK.
- AssistFeature: uses many NativeFunction.Natives calls (IS_PED_ARMED<bool>, IS_PED_IN_COMBAT<bool>, IS_PED_CUFFED<bool>, BRING_VEHICLE_TO_HALT, SET_VEHICLE_HANDBRAKE, SET_PED_COMBAT_ATTRIBUTES, SET_PED_COMBAT_ABILITY, SET_PED_COMBAT_RANGE, SET_CURRENT_PED_WEAPON with unarmed, STOP_BRINGING_VEHICLE_TO_HALT) - all look correct. No obvious violations.

### M2 - Blip lifecycle: uses BlipService.SafeDeleteBlip(ref Blip) - correct pattern (pass by ref)
- Severity: Medium
- Category: Blip lifecycle
- File: Pursuit (481-484, 553-554, 1160, 1173, 1182, 1191, 1294-1295, 1366-1371, 1386-1387, 1434-1439, 1450-1455), Patrol (679-683, 713-716), Assist (600)
- Description: All SafeDeleteBlip calls pass ef (e.g. 481 BlipService.SafeDeleteBlip(ref b), 553 same, 1160 ef DriverBadgeBlip etc). Also sets to null via service? SafeDeleteBlip takes ref and should null after deletion per standards. Correct.
- Also DismissFromScene does: delete RadarBlip, then recreate AttachPatrolBlip (1366-1371) - matches “state downgrade blip recreation” to avoid sticky flash timers. Good.
- PursuitUnitSession.Dispose also calls SafeDeleteEntityBlip for Cruiser/Driver/Passenger (1453-1455) - that’s additional cleanup. OK.
- Standards also require: halt flashing via SET_BLIP_FLASHES false/alternate false, reset flash interval/timer to 0, stop managed fibers, delete, null in finally. We must trust BlipService.SafeDeleteBlip implements full 4-step protocol. But codebase dependency - assuming compliant. Not a direct code violation in these files.

### M3 - Suspect blip handling: uses native detection via BlipService.HasAnyActiveRadarMarker and also GetBlipFromEntity awareness implied
- Severity: Medium
- Category: Suspect blip inviolability
- File: PursuitFeature.cs:443-544 (UpdateSuspectRadarMarkers), 516-520 checks HasAnyActiveRadarMarker, 499-502 skips if tracked
- Description: Correctly avoids touching LSPDFR-managed suspect blips - checks HasAnyActiveRadarMarker before attaching fallback (516-520). Tracked in dictionary by PoolHandle (25, 452-488, 535). Cleans up when suspect neutralized/arrested/cuffed/dead (456-484). 
- Potential issue: the check uses alidSuspects.FirstOrDefault(s => s.Handle == handle) when cleaning (457-462) - but Handle is PoolHandle (int-based in Rage). Dictionary key is PoolHandle. Also when adding, stores suspect.Handle (535). That’s fine. But if a ped is deleted/replaced, handle may be reused? Less common. Also the cleanup removes if ped becomes null/non-existent or arrested/cuffed - correct.
- Also when suspect is in vehicle, attaches blip to vehicle (524-531) - that’s reasonable. Good adherence to “never duplicate/overwrite/delete suspect blips managed by LSPDFR”.

### M4 - Pursuit capacity calculation may double-count
- Severity: Medium
- Category: Logic / capacity
- File: PursuitFeature.cs:737-754 (IsPursuitAtMaxCapacity), 376-398 (GetActivePursuitCruiserCount), 400-425 (GetActivePursuitUnitCount)
- Description: IsPursuitAtMaxCapacity does:
`csharp
int totalInvolved = Math.Max(totalCopsInPursuit, Math.Max(distinctCruisers, draftedCount)) + backupResponders;
return totalInvolved >= Settings.MaxPursuitUnits || (draftedCount + backupResponders) >= Settings.MaxPursuitUnits;
`
- totalCopsInPursuit is count of cops in pursuit (alive). distinctCruisers from GetActivePursuitCruiserCount: counts vehicles with cops in them (vehicleCruisers = distinct vehicle handles) plus foot cops contribution ((footCops+1)/2 integer division). So for mixed foot/vehicle, adds partial. Then takes max with totalCopsInPursuit and draftedCount, adds backupResponders. Also OR with (draftedCount+backupResponders)>=cap. 
- This is a bit convoluted - could over/under-count in edge cases (foot officers vs vehicles). But goal is to be conservative? The OR with (draftedCount+backupResponders) is a strict local cap. That aligns with “strict pursuit capacity enforcement”. 
- Also draftedCount is sessions in InPursuit state (328, 418, 743). That’s Cadence-drafted units. Good.
- BackupFeature.GetActivePoliceResponderCount() included - correct for global cap. 
- No obvious off-by-one causing premature blocking; logic is defensive.

### M5 - Settings validation not visible here; distance invariants assumed
- Severity: Medium
- Category: Settings/config
- File: all three
- Description: Standards require SpawnDistanceMin < SpawnDistanceMax < DespawnDistance enforced at boot with clamps. Also MaxPursuitUnits enforcement. Code uses Settings.* values (DespawnDistance 606, 614 in Pursuit; 293-317 in Patrol; 512 in Assist). No validation code in these files - likely in Settings class. Not a violation in these files, but must be enforced. Flag as needs verification.
- Patrol spawn uses Settings.SpawnDistanceMin/Max (385-387) correctly. Good.

### M6 - Random used without thread-safety consideration? 
- Severity: Low-Medium
- Category: Concurrency
- File: Pursuit has static Random _rand (26), Patrol (23), Assist (30)
- Description: Random is not thread-safe. Multiple fibers accessing same Random instance (all these features run on their own fibers; also RequestGroupPursuit spawns fiber that uses Random via TryFindRoadSpawnPosition?). TryFindRoadSpawnPosition uses _rand (1056-1063) - called from PursuitProcessLoop fiber (main pursuit fiber) and from RequestGroupPursuit’s spawned fiber (852-860) - so two different fibers can call _rand concurrently ? potential race/corruption. Same for Patrol (uses _rand in cooldown 353, angle calc 391-394) - runs on patrol fiber only? PatrolProcessLoop is single fiber; SpawnTestUnit could be called from UI/thread? Maybe from menu on main thread - possible cross-thread access. Assist uses _rand for callsign (398) - AssistProcessLoop fiber only.
- Recommendation: use thread-safe random (e.g. ThreadLocal<Random> or lock around Random access). Or use Rage.Game.GetRandomFloat/other Rage helpers? Or simple lock. Low risk in practice but easy to fix.

### M7 - Division/edge cases in GetActivePursuitCruiserCount
- Severity: Medium
- Category: Logic
- File: PursuitFeature.cs:383-398
- Description: int footCops = pursuitCops.Count(p => p != null && p.Exists() && (p.CurrentVehicle == null || !p.CurrentVehicle.Exists())); then returns ehicleCruisers + (footCops>0 ? (footCops+1)/2 : 0). Integer division truncates - (1+1)/2=1, (2+1)/2=1 (integer), (3+1)/2=2. That’s a rough heuristic (treating foot cops as contributing ~0.5 units each, rounding up in pairs?). Unusual. But comment not given; purpose is estimating cruiser-equivalent count. May undercount if many foot officers. But used in capacity calc - could be conservative or not. Needs understanding of LSPDFR semantics. Not obviously wrong, but odd.
- Recommendation: document rationale or use clearer formula. Also consider just using total cop count as fallback.

### M8 - AssistFeature uses hardcoded weapon constants and also tries CombatPistol then maybe relies on holstering to unarmed
- Severity: Medium
- Category: Correctness
- File: AssistFeature.cs:350-352 (give CombatPistol, set current), 538 (set current to WeaponUnarmed)
- Description: When mobilizing, gives CombatPistol and sets current - good. On stand-down holsters to unarmed via SET_CURRENT_PED_WEAPON with WeaponUnarmed (0xA2719263) and true - matches standards. Also has WeaponPistol constant defined but never used (25) - dead code. Remove or use.
- Also in FinishStandDown (568-569): sets handbrake false, stops bringing to halt, then NativeTasks.SetSilentSiren(cruiser,false). Also earlier in Dispose does similar (609 sets SilentEmergencyLights false). Ensure consistent siren/lightbar state. Looks OK.

## LOW

### L1 - PatrolFeature passenger paused with TASK_PAUSE for -1 (infinite pause)
- Severity: Low
- Category: AI/tasking
- File: PatrolFeature.cs:494 (	ry { NativeFunction.Natives.TASK_PAUSE(passenger, -1); } catch { })
- Description: Passenger in patrol cruiser gets paused indefinitely. Also BlockPermanentEvents set true (492). That’s fine for passengers who are meant to ride along passively? But if they need to exit/react, paused tasks might interfere. Also in Relinquish/Dispose BlockPermanentEvents cleared and tasks cleared - correct cleanup. Probably intentional. Not a bug, but worth noting.

### L2 - AssistFeature scans up to 200m for cruisers, detects threats up to 85m - OK per design (60-90m stated)
- Severity: Low
- Category: Design
- File: AssistFeature: 171 (200m), 231 (85m), 210 comment says “60m - 90m radius” but code uses 85m. Close enough.

### L3 - Potential re-entrancy: RequestGroupPursuit spawns fiber that calls RequestPursuitInterceptor in loop
- Severity: Low
- Category: Reentrancy
- File: PursuitFeature.cs:850-860
- Description: Spawns new GameFiber and loops, sleeping 1500ms between calls. Each call checks state (active pursuit running, capacity, etc.). No shared state modified unsafely from multiple fibers except _lastJoinTime and _activeSessions - both protected by lock. Also _rand accessed from this fiber too (via TryFindRoadSpawnPosition) - see M6. OK.

### L4 - Dead/unreachable patterns, TODOs: none visible
- Severity: Low
- Category: Code quality
- No TODOs/stubs seen. WeaponPistol constant unused in Assist (L3 cleanup). Minor.

### L5 - Fiber.Abort() used on shutdown
- Severity: Low
- Category: Lifecycle
- File: Pursuit (66-78), Patrol (62-75), Assist (67-80)
- Description: Uses _pursuitFiber.Abort() in Stop(). GameFiber.Abort() is generally discouraged (can corrupt state), but on plugin shutdown it’s often used as last resort. Also wrapped in try/catch/finally. Standards don’t explicitly forbid; lifecycle shows “stop background loops” - Abort is acceptable for emergency unload. Not ideal but probably fine for this context.

## Per-file summary

**PursuitFeature.cs (~1503 lines)**
- Critical: C1 (dismissal ordering/teardown disenrollment missing), C2 (Stop/PurgeAll don’t remove from pursuit). 
- High: H1 (no Assist ownership check), H3 relates to relinquish paths. 
- Medium: M4 (capacity calc), M6 (Random cross-fiber), M7 (foot cop heuristic). 
- Good: uses SafeDeleteBlip with ref, suspect blip handling respects LSPDFR, calls RemovePedFromPursuit in DismissFromScene/BeginStandDown (correct order there), checks BackupFeature.IsBackupCruiser, enforces capacity checks at entry points.

**PatrolFeature.cs (~755 lines)**
- Critical: H3 extension - Dispose/Relinquish don’t remove from pursuit if enrolled (could be C2-class on teardown). 
- Medium: M6 (Random used; Patrol fiber only mostly, but SpawnTestUnit could be cross-thread). 
- Good: relinquishes to external features via RelinquishCruiser, checks IsCopBusy/InPursuit before despawning (preserves LSPDFR-drafted units), proper blip cleanup, snapshot pattern in watchdog.

**AssistFeature.cs (~639 lines)**
- High: H4 (no RemovePedFromPursuit on Dispose). 
- Medium: M8 (unused WeaponPistol). 
- Good: checks ownership (won’t take pursuit/backup/trafficstop cruisers), holsters to unarmed correctly, releases vehicle state on cleanup, uses SafeDeleteBlip.

## Open questions / needs verification

1. **BlipService implementation**: Does SafeDeleteBlip implement full 4-step protocol (flash off, reset flash interval/timer to 0, stop managed fibers, delete, null in finally)? These files rely heavily on it. Must verify against standards.
2. **Settings validation**: Is distance invariant enforced at boot (SpawnDistanceMin<SpawnDistanceMax<DespawnDistance with clamps)? MaxPursuitUnits validation?
3. **AssistFeature.IsActiveCruiser API**: Pursuit needs to check Assist ownership to prevent dual-drafting. Does AssistFeature expose public IsActiveCruiser? If not, add it.
4. **Cross-thread Random**: SpawnTestUnit called from UI/menu (main thread) while Patrol fiber runs - concurrent access to _rand. Risky.
5. **Teardown order in Main**: Standards say “On plugin shutdown (Main.Finally or emergency unload), entity cleanup must precede fiber suspension: 1) PurgeAllFeatures(immediateDelete:true) 2) StopAllFeatures() 3) Unhook events”. Do these features’ Stop() get called after purge? If purge doesn’t disenroll from pursuit, crash still possible. Also need to ensure purge/dispose paths disenroll.
6. **Functions.IsPedInPursuit/RemovePedFromPursuit availability**: All calls wrapped in try-catch in places (good defensive programming). OK.
7. **NativeTasks.SetSilentEmergencyLights vs SetSilentSiren**: Assist uses SetSilentSiren in places (320, 569) and SetSilentEmergencyLights in Dispose (609). Need to confirm semantics match intended “silent lightbar” behavior per standards (standards specify SET_VEHICLE_SIREN true + SET_VEHICLE_HAS_MUTED_SIRENS true). If NativeTasks helpers wrap those correctly, fine.
8. **PursuitUnitSession.DismissFromScene spawns fiber while lock may be held by caller** (DismissStandbyUnits holds lock) - minor concurrency style issue.
9. **Passenger seat indexing in Assist (541, 562)**: for multiple officers beyond driver+1 passenger, seatIdx becomes (i-1); i=2 means seat 1 (front passenger) - correct if first is driver. i=3 means seat 2 (rear) etc. Depends on cruiser seating. OK if cruisers have those seats.

**Most important fixes (priority order):**
1. Add RemovePedFromPursuit in Dispose/teardown paths for all features (Pursuit, Patrol, Assist) before deleting/dismissing officers. Also in ReleaseAllPursuitUnits before session.Dispose.
2. Add Assist ownership check to Pursuit.FindCandidateAmbientCruiser (need public API in AssistFeature).
3. Address Random thread-safety (lock or ThreadLocal).
4. Review capacity calc (M4) for correctness; at minimum document.
