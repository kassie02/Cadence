# Cadence BackupFeature.cs Part 2 Audit (Lines ~1650 - 3814)

- Workspace: C:\Users\User\Pictures\test-noedit\Cadence
- Source: src\Features\BackupFeature.cs (3814 lines total)
- Scope: Lines 1650 - 3814 (part 2). Overlap with tail of part 1 is fine.
- Read-only audit; write-only delivery.

## Region map (lines 1650-end)
- 1650-1812: `FindCandidateAmbientVehicle` - candidate filtering, agency/occupant checks, pick best candidate
- 1814-1851: `ReleaseFromOtherFeatures`, `MobilizeStandingOfficer`
- 1852-1873: `GetRoadsideCurbPosition`
- 1875-1964: `CommandeerAmbientVehicle` - resolve driver, make persistent, configure session
- 1966-2009: `TryFindStaggeredBackupSpawnPoint` - pursuit/spawn placement, heading calculation
- 2011-2206: `SpawnFallbackBackupUnit` - UB native fallback, ambulance/fire/police, state/local
- 2208-2290: `ConfigureBackupSession` - persist entities, sirens, tasking, blips, session construction
- 2292-2355: Audio/animation: `PlayPanicButtonSound` (SoundPlayer, async fiber), `PlayPlayerRadioAnimation`
- 2357-2363: `GenerateCallsign` (uses `_callsignCounter`, `_rand`)
- 2365-3760: `BackupUnitSession` class (state machine: EnRoute/ArrivedOnScene/EngagingThreat/Dismissing/Completed)
- 3761-3812: `BackupUnitSession` helpers (`CreateFootBlips`, `UpdateBlipStates`, `ClearFootBlips`, `Dispose`)
- 3813-3814: end

## Severity: Critical (CR)

- **CR-1 B2-1** | severity: Critical | category: Concurrency/Lock discipline | file: `BackupFeature.cs:1488-1541` (TryReengageDismissingUnit) | **Reengage under lock**  
  - **Description:** `lock (_syncLock)` spans lines 1488-1541 and calls `bestCandidate.Reengage(type, targetLocation, isCode3, isCallout)` (1538) while holding the monitor. Reengage can `GameFiber.StartNew` (2556, 2704 in OrderOfficersEnterVehicle) and does native/blip work.  
  - **Why:** Zero-Lock-Across-Yields rule not directly violated (no `Yield/Sleep/LoadAndWait`), but starting fibers and performing heavy native ops while holding a global lock is high-risk for deadlock/contention and violates zero-lock-across-asynchronous boundaries.  
  - **Recommendation:** Snapshot candidates under lock (Cruiser reference + state), release lock, then call `Reengage` on the selected unit(s).

- **CR-2 B2-2** | severity: Critical | category: Dual ownership/reservation race | file: `BackupFeature.cs:1130-1147, 1571-1588, 937-961, 1074-1097` | **_reservedCruisers race window**  
  - **Description:** After adding candidate to `_reservedCruisers`, `CommandeerAmbientVehicle` is called; in `finally` (1146/1587) `_reservedCruisers.Remove(candidate)` runs before the dispatcher adds the resulting session to `_activeUnits` (lines 937/960/1074/1096). Also `_reservedCruisers` only protects `FindCandidateAmbientVehicle` (line 1665).  
  - **Why:** During the window, the commandeered vehicle is neither reserved nor tracked in `_activeUnits` → another concurrent dispatch can commandeer the same cruiser. Can produce dual-ownership, double dispatch, or task/wipe conflicts.  
  - **Recommendation:** Keep vehicle reserved until the session is successfully added to `_activeUnits` (move Remove to after Add), or use a `CommandeerResult` + add under lock, or extend reservation lifecycle; also ensure other features consult reservation or add a session before releasing reservation.

## Severity: High

- **H-1 B2-3** | severity: High | category: Settings/Strictness (UB vs Fallback) | file: `BackupFeature.cs:2064-2068, 2116-2120, 2171-2175, 1215-1219, 1266-1270, 1324-1328` | **UB abort ignores IsAvailable=false**  
  - **Description:** When `Settings.UseUltimateBackup=true` but `UltimateBackupService.IsAvailable==false`, the UB spawn block is skipped; the code still aborts with `if (Settings.UseUltimateBackup && !Settings.AllowFallback) return null;` and never reaches native/Cadence fallback. Police spawner in `SpawnFallbackBackupUnit` also lacks try/catch around UB call.  
  - **Why:** If UB is installed but detection fails, or user sets UseUltimateBackup=true without UB available and AllowFallback=false, all backup dispatch is silently aborted — violates "AllowFallback strictness" and breaks resilience.  
  - **Recommendation:** Change condition to `if (Settings.UseUltimateBackup && UltimateBackupService.IsAvailable && !Settings.AllowFallback) return null;` or invert logic; add try/catch around UB calls for police path; consider treating native as fallback path when UB unavailable.

- **H-2 B2-4** | severity: High | category: Audio strings (scanner/audio) | file: `BackupFeature.cs:875, 881, 887, 893, 1006, 1010, 751` | **Potentially invalid scanner audio strings**  
  - **Description:** Uses `"AMBULANCE ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION"` (875,881) and `"FIRETRUCK ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION"` (887,893,1006,1010) instead of a verified safe string; `Functions.PlayScannerAudioUsingPosition` with composite strings can crash (documented history). Panic uses verified-ish `"ASSISTANCE_REQUIRED IN_OR_ON_POSITION"`.  
  - **Why:** Known crash source (CLR exception 0xe0434352) from scanner audio hooks/strings.  
  - **Recommendation:** Validate strings against verified UB/LSPDFR audio bank; fallback to generic `"ASSISTANCE_REQUIRED IN_OR_ON_POSITION"` on failure; guard with try/catch and avoid constructing long composite strings.

- **H-3 B2-5** | severity: High | category: Siren/lightbar logic (Code 2 reengage, en route) | file: `BackupFeature.cs:2492-2500, 2779-2784, 2231-2252, 2257` | **Code 2 drives with no lights; reengage inconsistent**  
  - **Description:** Reengage (non-alreadyOnScene path, line 2574-2590) for non-Code3 sets `Cruiser.IsSirenOn=false; Cruiser.IsSirenSilent=false;` then `NativeTasks.SetSilentEmergencyLights(Cruiser,false)` (line 2494-2500) — turns all emergency lights off. `UpdateEnRoute` only enables lights if `IsCode3` (line 2779-2784). Also Code 2 already-on-scene in ConfigureBackupSession sets silent lightbar.  
  - **Why:** Code 2 (silent lightbar) re-engaged en route will drive dark, contradicting silent lightbar requirement. Inconsistent siren policy.  
  - **Recommendation:** For Code 2, always set silent lightbar (IsSirenOn=true, IsSirenSilent=true, SetSilentEmergencyLights true); for Code 3, audible. Ensure `UpdateEnRoute` respects Code 2 (enable silent lightbar, don’t force audible). Also don’t clear lights unconditionally on re-engage.

- **H-4 B2-6** | severity: High | category: Commandeered officer/ped dual ownership | file: `BackupFeature.cs:1875-1964, 1814-1829, 1764` | **Officer not released from other features; civilian passenger risk**  
  - **Description:** `ReleaseFromOtherFeatures` only relinquishes cruiser from Stationary/Patrol/Response/TrafficStop. `CommandeerAmbientVehicle` resolves driver (including via GetAssignedOfficer from TrafficStop/Stationary, line 1906) and later makes driver/persistent and adds to session officers; `passenger` is taken from `GetPedOnSeat(0)` without verifying cop (line 1939-1951) and may be armed/followed. No call to release that officer from other feature officer lists.  
  - **Why:** Clearing tasks while the officer remains owned by another feature’s state can violate feature invariants; arming a civilian ride-along (seat 0) is a safety/logic bug.  
  - **Recommendation:** When commandeering a designated officer, call corresponding `RelinquishOfficer` on features (or have features ignore officers owned by BackupFeature). Validate passenger is a cop (`Functions.IsPedACop`/RG relation) before treating as officer; do not add civilians to officer set.

- **H-5 B2-7** | severity: High | category: Blip visibility toggle leak | file: `BackupFeature.cs:3642-3760` | **Radar blips not deleted when ShowRadarBlips toggles off**  
  - **Description:** `UpdateBlipStates` early-returns at 3644-3647 if `!Settings.ShowRadarBlips`; it never deletes existing `RadarBlip` or `FootBlips`. Also no cleanup when disabling mid-session.  
  - **Why:** Persistent stale blips after config change; resource/state leak.  
  - **Recommendation:** When ShowRadarBlips becomes false, delete all blips immediately (SafeDeleteBlip on RadarBlip, clear all foot blips, ClearFootBlips) and still allow cleanup; don’t early-return without deletion.

## Severity: Medium

- **M-1 B2-8** | severity: Medium | category: Concurrency/thread-safety | file: `BackupFeature.cs:53-57, 2357-2363` | **Random & counter race**  
  - **Description:** `_rand` (Random) shared across multiple dispatch fibers; `_callsignCounter` incremented unsynchronized (`_callsignCounter++`).  
  - **Why:** `Random` is not thread-safe → can return 0/corruption; counter race can produce duplicate callsigns.  
  - **Recommendation:** Use thread-safe rng (`Random.Shared` or `lock(_rand)`); use `Interlocked.Increment` for counter; or move per-fiber.

- **M-2 B2-9** | severity: Medium | category: Seat assignment / egress logic | file: `BackupFeature.cs:2439-2443, 2696-2735, 2404-2406` | **GetSeatIndexForPed correctness**  
  - **Description:** `GetSeatIndexForPed(i)` returns -1 for i==0 (driver). Officers[0] is driver. If driver is dead/unmounted context, index assignment maps passenger to seat 0 (passenger) rather than driver. Also no bounds checking for vehicle seat capacity. WarpIntoVehicle to invalid seat may fail silently.  
  - **Why:** Re-engage/dismiss boarding could fail; dead-driver scenarios not handled.  
  - **Recommendation:** Always assign living officers to appropriate available seats; if current driver slot occupied by dead ped, reassign; check seat count before warp.

- **M-3 B2-10** | severity: Medium | category: Pursuit disenrollment coverage | file: `BackupFeature.cs:3463, 3512, 1831` | **Tasks.Clear without disenrollment in some paths**  
  - **Description:** `EngageThreat` (3463) and `UpdateThreatEngagement` (3512) call `ofc.Tasks.Clear()` without `Functions.RemovePedFromPursuit(ofc)`. `MobilizeStandingOfficer` (1831) also clears tasks without disenrollment. These guards by “not in pursuit” checks but still risk inconsistency; also standard requires disenroll before clearing.  
  - **Why:** If pursuit check races, or logic changes, can violate “remove from pursuit before Tasks.Clear”.  
  - **Recommendation:** Always `RemovePedFromPursuit` before `Tasks.Clear()` if possibly in pursuit; guard with IsPedInPursuit check.

- **M-4 B2-11** | severity: Medium | category: Buddy following / navmesh task reuse | file: `BackupFeature.cs:3277, 3388, 3300-3373` | **TASK_FOLLOW_NAV_MESH_TO_COORD spam throttle**  
  - **Description:** Buddy following uses `_lastBuddyFollowUpdate` >=1300ms (3307), but also sets speed/offset each time. Also when officer <3.5m it `Tasks.Clear()` to stop (3346-3350) — may override scenario; however throttle OK. Minor issue: `TaskFollowPlayer` falls back to `Tasks.FollowNavigationMeshToPosition` (3382-3392) — could conflict with pending navmesh tasks.  
  - **Why:** 1300ms throttle is reasonable but could still thrash on corners; not directly a crash, quality issue.

- **M-5 B2-12** | severity: Medium | category: Blip state transitions (alternate flash on police vehicle) | file: `BackupFeature.cs:3743, 2796, 3191` | **Pursuit uses alternate flashing on police cruiser (Sprite 56)**  
  - **Description:** `BlipService.AttachPursuitBlip` called with `alternateFlash: true` on a police vehicle (Sprite 56). Standards say “Never use ... alternateFlash: true on police vehicles.” BackupFeature triggers this via TransitionToPursuit.  
  - **Why:** Visual/policy violation; can create red/blue flashing that conflicts with LSPDFR markers.  
  - **Recommendation:** Don’t use alternateFlash on police vehicle sprites; use single-flash Code 3 or let pursuit use native LSPDFR markers if possible.

- **M-6 B2-13** | severity: Medium | category: Threat engagement cleanup | file: `BackupFeature.cs:3473-3540` | **Threat neutralization restores patrol blip (may recreate)**  
  - **Description:** On threat neutralized (3498), deletes current `RadarBlip` (ref) and attaches new `AttachPatrolBlip` (3520-3523) — may recreate a solid patrol blip. Also if officers still on foot, `UpdateBlipStates` later will convert cruiser to PARKED/restore state. Fine, but could momentarily flicker. Not critical.

- **M-7 B2-14** | severity: Medium | category: Departure fiber lifetime | file: `BackupFeature.cs:3575-3623` | **Departure fiber spawned once per unit; references mutable state**  
  - **Description:** `_departureInitiated` guards one-shot; fiber checks `State != Dismissing` and `Cruiser.Exists()`/driver valid. If `Cruiser.Dismiss()` called elsewhere or `Dispose`, guards prevent tasking. OK. Minor: could start fiber while unit disposed? Guards protect.

- **M-8 B2-15** | severity: Medium | category: Foot blip dictionary thread-safety on teardown | file: `BackupFeature.cs:2379-2381, 3642-3770, 3800-3812` | **Internal dict mutated across threads**  
  - **Description:** `_officerFootBlips` is a `Dictionary<PoolHandle,Blip>` accessed from `UpdateBlipStates` (fiber) and `ClearFootBlips`/`Dispose` (fiber or main thread during PurgeAll). Not synchronized.  
  - **Why:** Potential ConcurrentModification/invalid key access during shutdown (PurgeAll at Main.Finally runs; feature fibers may still be shutting down).  
  - **Recommendation:** Synchronize access to `_officerFootBlips` with `_syncLock` or a separate lock; snapshot keys before enumeration.

## Severity: Low

- **L-1 B2-16** | severity: Low | category: Dead code | file: `BackupFeature.cs:30` | **Unused constant `WeaponCarbineRifle`**  
  - **Description:** `private const uint WeaponCarbineRifle = 0x83BF0278;` declared but never used in part 2 or obvious paths.  
  - **Recommendation:** Remove to reduce noise; or document intent.

- **L-2 B2-17** | severity: Low | category: Resource hygiene | file: `BackupFeature.cs:2296-2326` | **SoundPlayer not disposed**  
  - **Description:** `new SoundPlayer(wavPath)` never `Dispose()`d; `Play()` is async but instance held by GC.  
  - **Recommendation:** Use `using` or call `Dispose()` after playback completes; or use `SoundPlayer` as local and let GC, but better `using`.

- **L-3 B2-18** | severity: Low | category: Panic sound fiber leakage | file: `BackupFeature.cs:2311-2325` | **Untracked sound fibers on rapid panic**  
  - **Description:** Each panic that finds wavPath starts a new fiber with name "Cadence - Panic Sound Player Fiber"; no tracking/cancellation. Debounced 2.5s, but still uncoordinated.  
  - **Recommendation:** Track last sound fiber or allow only one active panic sound.

- **L-4 B2-19** | severity: Low | category: Double Dispose | file: `BackupFeature.cs:3546-3640, 436-476` | **`Dispose` called twice when completing**  
  - **Description:** In `UpdateDismissing`, when conditions met it calls `unit.Dispose(immediateDelete:false)` (3632) then sets `State=Completed`; `UpdateActiveUnits` later sees `State==Completed` and calls `Dispose` again (461).  
  - **Why:** Harmless (idempotent), but unnecessary.  
  - **Recommendation:** Set state to Completed without re-Disposing if already disposed; or track `IsDisposed`.

- **L-5 B2-20** | severity: Low | category: Magic numbers | file: `BackupFeature.cs:multiple (249-254, 2563-2568, 2844, 2850, 2873, 2886-2887, 2906-2910, 3628, etc.)` | **Hardcoded thresholds**  
  - **Description:** Numerous magic values (proximity 25m/35m/85m/120m/180m/280m/300m, timeouts 4000/8000/15000/35000/50000/180000ms, speeds 1.8/2.5/10/20/22/32/35 etc.).  
  - **Recommendation:** Extract to named constants (e.g. `MinPanicReengageDist=120f`, `DismissMinDistAway=85f`, `EnRouteTimeoutMs=75000`) for tunability/testability.

- **L-6 B2-21** | severity: Low | category: UB strictness edge case | file: `BackupFeature.cs:2044-2068` | **Ambulance/fire also ignore IsAvailable in guard?**  
  - **Description:** Ambulance/fire UB block uses `Settings.UseUltimateBackup && UltimateBackupService.IsAvailable` (2044) before calling; if UB returns null and condition `Settings.UseUltimateBackup && !Settings.AllowFallback` holds, it aborts. Same semantic issue: aborts even if native fallback intended. Police path is worse (no IsAvailable in the second condition).  
  - **Recommendation:** Apply consistent guard as H-1.

## Open Questions
1. **IsBackupCruiser cross-feature consultation:** Does PursuitFeature/others consult `_reservedCruisers`? (grep shows `BackupFeature.IsBackupCruiser` used; not clear if reservation queried by others.) Consider exposing reservation check.
2. **Commandeered officer persistence:** Are officers that were “assigned” by TrafficStop/Stationary allowed to be added to a Backup session? What is the intended ownership transfer? (ReleaseFromOtherFeatures only cruiser.)
3. **Code 2 re-engage behavior:** Should a dismissing Code 2 unit be re-tasked with silent lightbar on or remain dark? Current code clears lights entirely (H-3). Confirm design intent.
4. **Scanner audio bank:** Which exact strings are safe for AMBULANCE/FIRETRUCK in this build? Consider centralizing a `SafePlayScannerAudioUsingPosition(...)` helper with fallbacks and exception guards (addresses H-4).

---

**Summary (high-level):** Main risks are (a) lock held across Reengage, (b) reservation race window, (c) UB/fallback abort condition, (d) Code 2 lightbar logic, and (e) scanner audio string crashes. Several concurrency/safety nits (Random/counter, foot-blip dict, double Dispose) are medium/low but worth hardening.
