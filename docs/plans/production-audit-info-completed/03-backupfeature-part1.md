# Cadence — BackupFeature.cs Audit — PART 1 (lines 1–1700)

- **Target**: `src/Features/BackupFeature.cs` (file is 3814 lines total; this note covers lines 1–1700 plus the directly-referenced helpers below/after the cut that this region calls into).
- **Baseline**: `CODING_STANDARDS.md` (read in full).
- **Mode**: read-only audit. No workspace file modified.
- **Context read**: `BackupUnitSession` (2369–3814), `Settings.cs`, `UltimateBackupService.cs`, `BlipService.cs`, `CadenceMenu.cs` (teardown), `Main.cs` (Finally).

## Region Map (lines 1–1700)

| Lines | Contents |
|---|---|
| 1–107 | usings; class header; constants (`WeaponCombatPistol`, `WeaponCarbineRifle`, `WeaponUnarmed=0xA2719263`); `BackupType` enum; static state (`_syncLock`, `_activeUnits`, `_reservedCruisers`, `_rand`, `_backupFiber`, debounce timers); lean-menu fields; `Divisions`. |
| 113–319 | Public API: `Start`, `Stop`, `PurgeAll`, `GetActiveCount`, `GetActivePoliceResponderCount`, `IsBackupOfficer`, `IsBackupCruiser`, `TriggerPanicButton`, `TriggerGroupBackup`, `TriggerCalloutResponse`, `OnLspdfrCalloutFinished`, `DismissAll`. |
| 325–494 | `BackupProcessLoop` (menu processing, keybinds, unit updates); `ProcessKeybinds` (panic/group/menu/dismiss + control suppression); `UpdateActiveUnits`; `PurgeAllInternal`. |
| 500–716 | `BuildBackupMenu` (RAGENativeUI), `RebuildActionsForMode`, `ToggleMenu`, `CloseMenu`. |
| 722–973 | `RequestBackup` orchestrator (per-type switch, panic/group debounce+re-engage, scanner audio, `DispatchSingleUnit`, `DispatchUnitsSequence`). |
| 979–1160 | `RequestCalloutBackup`, `RequestCalloutGroupBackup`, `DispatchCalloutUnit(sSequence)`, `ResolveOrSpawnCalloutUnit`. |
| 1162–1360 | `SpawnFallbackCalloutUnit` (EMS/Fire/Police UB→native spawn paths). |
| 1362–1471 | `TryGetCalloutOrWaypointPosition`, `GetCalloutFromHandle` (reflection), `GetBackupTypeDisplayTitle`. |
| 1477–1601 | `TryReengageDismissingUnit`, `ResolveOrSpawnUnit`. |
| 1603–1700+ | `FindCandidateAmbientVehicle` (candidate pool, agency/service filters, prisoner heuristic, driver verification). |

Referenced-but-later helpers used by this region: `ReleaseFromOtherFeatures` (1814), `MobilizeStandingOfficer` (1831), `GetRoadsideCurbPosition` (1852), `CommandeerAmbientVehicle` (1875), `TryFindStaggeredBackupSpawnPoint` (1966), `SpawnFallbackBackupUnit` (2011), `ConfigureBackupSession` (2208), `PlayPanicButtonSound` (2296), `PlayPlayerRadioAnimation` (2328), `GenerateCallsign` (2357), `BackupUnitSession` (2369).

---

## Findings

### CRITICAL

#### B1-1 — Pursuit disenrollment skipped on purge/dispose (LSPDFR crash risk)
- **Severity**: Critical · **Category**: Teardown / crash
- **Where**: `BackupFeature.cs:171-178` (`PurgeAll`), `137-169` (`Stop`), `478-494` (`PurgeAllInternal`), `450-463` (`UpdateActiveUnits` invalid/Completed path) → `BackupUnitSession.Dispose` `3782-3811`.
- **Description**: `Dismiss()` (2632-2694) is the only path that calls `Functions.RemovePedFromPursuit(ofc)` before clearing tasks (standards §9.1). `PurgeAll`/`Stop`/`UpdateActiveUnits` all bypass `Dismiss()` and go straight to `Dispose()`, which `Cruiser.Delete()/Dismiss()` and `officer.Delete()/Dismiss()` with no pursuit disenrollment (and no `Tasks.Clear()`).
- **Why it matters**: Standards §9.1 state clearing/removing officers while still enrolled in LSPDFR pursuit AI causes unhandled `NullReferenceException` inside LSPDFR (`TaskCopChasePedAdv.cs:1515`). Deleting the peds is at least as dangerous and matches the known "CLR exception 0xe0434352" crash history. Triggered on plugin unload (`Main.Finally` → `PurgeAllFeatures(true)` → `BackupFeature.PurgeAll`), feature toggle-off, and any unit going invalid while enrolled.
- **Recommendation**: Route every disposal path through a single `Dismiss()`-equivalent that disenrolls all `Officers` from pursuit (`Functions.IsPedInPursuit` → `RemovePedFromPursuit`) and clears tasks before delete/release; call it from `Dispose(immediateDelete:true)` too.

### HIGH

#### B1-2 — Dual ownership: dismissing sessions' cruisers remain commandeer candidates
- **Severity**: High · **Category**: Dual-ownership race
- **Where**: `BackupFeature.cs:1665` (`_activeUnits.Any(u => u.Cruiser == veh && u.State != Dismissing && u.State != Completed)`), interacting with `TryReengageDismissingUnit` (1477-1549, ≤300 m, agency-matched) vs commandeering (≤350 m).
- **Description**: The exclusion deliberately ignores sessions in `Dismissing` state. `TryReengageDismissingUnit` only claims within 300 m and only for matching agency/service; a dismissing cruiser that is rejected there (agency mismatch or 300–350 m band) is still picked up by `FindCandidateAmbientVehicle` and a **second** `BackupUnitSession` is created for the same vehicle while the first session still owns it (and its boarding fibers are active).
- **Why it matters**: Two sessions task the same cruiser/officers → task fighting, double blip churn, double `Dispose`, and "dual-ownership of commandeered vehicles" from the known-history list.
- **Recommendation**: Exclude *all* sessions that still reference the vehicle (including `Dismissing`) from candidate selection; only allow reuse via the explicit re-engage path, or atomically transfer ownership (remove/complete old session first).

#### B1-3 — Reservation is check-then-act (TOCTOU) across concurrent dispatch fibers
- **Severity**: High · **Category**: Reservation race
- **Where**: `BackupFeature.cs:1663-1666` (check under lock) then `1569-1571` / `1128-1131` (`lock { _reservedCruisers.Add(candidate); }`).
- **Description**: `FindCandidateAmbientVehicle` checks `_reservedCruisers` under one lock acquisition; the caller reserves under a *second* lock acquisition. Every `RequestBackup`/`RequestCalloutBackup`/`RequestGroup…` starts a brand-new untracked `GameFiber` (724, 981, 1028), and the menu allows repeated clicks, so two fibers can both select and reserve the same vehicle (`HashSet.Add` return value is ignored).
- **Why it matters**: Both fibers call `CommandeerAmbientVehicle` on the same cruiser → duplicate sessions/dual ownership; the "reservation" scheme does not actually guarantee exclusivity.
- **Recommendation**: Make a single atomic `TryReserve(Vehicle)` that checks-and-adds under one lock (and returns false if already present); reserve inside `FindCandidateAmbientVehicle` or immediately on return before releasing.

#### B1-4 — Spawn→register gap leaks untracked, persistent entities and blips
- **Severity**: High · **Category**: Resource leak / ownership
- **Where**: `BackupFeature.cs:930-941` (`DispatchSingleUnit`), `1067-1078` (`DispatchCalloutUnit`), `1081-1103` (`DispatchCalloutUnitsSequence`), `1576-1589` (reservation released in `finally` before caller registers), `2285-2289` (`ConfigureBackupSession` catch → `return null`).
- **Description**: Spawned/commandeered sessions are only added to `_activeUnits` by the caller *after* `ResolveOrSpawn*` returns and after the reservation is released. If `unit != null && !unit.IsValid()`, the session is dropped without `Dispose()` (line 931/1068/1090 guard). If `ConfigureBackupSession` throws after `MakePersistent()`/blip attach, it returns `null` and the already-persistent cruiser/peds and attached blip are never tracked or cleaned.
- **Why it matters**: Persistent ghost vehicles/peds and leaked blips (nothing references them) — exactly the "ghost entities"/blip-leak classes. Also opens a window where `IsBackupCruiser` returns false, enabling ghost drafting.
- **Recommendation**: Register the session into `_activeUnits` inside the spawn/configure step (before returning), and `Dispose()` any session that fails `IsValid()`; wrap `ConfigureBackupSession` so partial entities are cleaned on failure.

#### B1-5 — EMS/Fire requests can commandeer any emergency vehicle (and cops as medics)
- **Severity**: High · **Category**: Logic / strict-validation
- **Where**: `BackupFeature.cs:1669-1683` (`isAmbulance`: `!veh.Model.IsEmergencyVehicle && !vName.Contains(...)`; `isFire` same), and `1744` (`if (!isAmbulance && !isFire)` cop check skipped).
- **Description**: A police cruiser has `Model.IsEmergencyVehicle == true` and therefore passes the ambulance *and* fire filters. The service/agency checks are skipped for EMS/Fire, so a civilian/police driver is accepted as "medic"/"fireman". With the nearest-emergency-vehicle selection (usually a police car), an Ambulance/Fire request can resolve to a police cruiser crewed by a cop and be typed as `AmbulanceCode2/3` or `FireDept…`.
- **Why it matters**: Standards Core Principle 3 ("strict validation", no loose heuristics) and spawning rules are violated: no medics/fire crew, wrong vehicle semantics, incorrect blips/behavior.
- **Recommendation**: Whitelist ambulance model names for EMS and fire models for Fire (do not accept generic `IsEmergencyVehicle`); require appropriate service/driver relationship (medic/fireman) before accepting a candidate.

#### B1-6 — Prisoner heuristic rejects EMS/Fire crews (blocks commandeering)
- **Severity**: High · **Category**: Logic
- **Where**: `BackupFeature.cs:1686-1709` (`occ != veh.Driver && !Functions.IsPedACop(occ) && occ.RelationshipGroup != "COP"` → `hasPrisoner = true`).
- **Description**: The prisoner check runs for all service types. For an ambulance/firetruck with a two-person crew, the passenger (medic/fireman) is not the driver and is not a COP → flagged as a prisoner → candidate rejected. Only single-occupant or driver-only EMS/Fire units can ever be commandeered.
- **Why it matters**: Inverts intended behavior: EMS/Fire that *should* be commandeered are systematically rejected (while police cars are wrongly accepted per B1-5).
- **Recommendation**: Apply the prisoner/civilian-passenger heuristic only to police candidates; for EMS/Fire verify crew membership (medic/fireman relationship) instead.

#### B1-7 — Scanner audio strings are unvalidated, duplicated, and can abort dispatch
- **Severity**: High · **Category**: Scanner audio / crash
- **Where**: `BackupFeature.cs:750, 805, 850, 856, 863, 869, 875, 881, 887, 893, 899, 905, 912, 1006, 1010, 1014, 1050`.
- **Description**: 17 inline string literals: `"ASSISTANCE_REQUIRED IN_OR_ON_POSITION"`, and the unverified `"AMBULANCE ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION"` / `"FIRETRUCK ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION"`. Each call sits *before* the spawn call inside the same `try` (the whole `RequestBackup` switch is wrapped at 726/917). An unknown/renamed audio name either throws (caught at 917 → dispatch silently aborted, no unit dispatched) or throws on LSPDFR's own audio thread where Cadence cannot catch it.
- **Why it matters**: This aligns with the known "scanner audio string crashes". Even non-fatal throws make the panic/group/EMS/Fire buttons do nothing (notification shown, no unit). Strings are duplicated with no single source of truth or validation.
- **Recommendation**: Centralize audio names as validated constants (against the LSPDFR audio-name list), wrap each `PlayScannerAudioUsingPosition` in its own local try/catch, and dispatch units *before* (or independent of) the audio call so audio failure cannot cancel the spawn. Verify the `_01` variants exist.

#### B1-8 — Teardown: untracked dispatch/audio/boarding fibers survive Stop/Purge; `Abort()` under lock
- **Severity**: High · **Category**: Teardown / ghost entities
- **Where**: `BackupFeature.cs:137-169` (`Stop`), `171-178` (`PurgeAll`), untracked fibers at `724, 981, 1028, 2298, 2556, 2704`.
- **Description**: Only `_backupFiber` is tracked. In-flight dispatch fibers (`DispatchUnitsSequence`, `DispatchCalloutUnitsSequence`, sound, boarding/egress) are never cancelled. `PurgeAll`/`Stop` snapshot+clear `_activeUnits`, but a dispatch fiber mid-run re-adds freshly spawned units *after* the purge, and `Stop` then aborts only `_backupFiber` — the re-added units are never updated or disposed. `Main.Finally` runs `PurgeAllFeatures(true)` then `StopAllFeatures()` (`Stop` → purge again), so units re-added between the two purges leak as persistent ghosts. `Stop` also calls `_backupFiber.Abort()` while holding `_syncLock` (line 160).
- **Why it matters**: Ghost/leaked persistent entities on unload/toggle — a core standards concern (§Lifecycle), and `GameFiber.Abort()` is discouraged (can interrupt native transitions; a likely contributor to 0xe0434352 reports).
- **Recommendation**: Track dispatch/child fibers (or a single guarded "dispatching" flag/cancellation token) and stop them cooperatively before purge; drop the `Abort()` in favour of `_isRunning=false` (the loop already exits within ~25 ms). Never abort while holding `_syncLock`.

#### B1-9 — No global active-unit cap; panic/group math bypasses pursuit capacity
- **Severity**: High · **Category**: Capacity / rate-limit (standards §1, §9.3)
- **Where**: `BackupFeature.cs:741` (`int activePolice = GetActivePoliceResponderCount();` — unused), `797` (`grpActivePolice` — unused), `745-747` (panic), `801-803` (group).
- **Description**: `GetActivePoliceResponderCount()` is computed and discarded in both panic and group branches — evidence of an unwired global cap. Panic: `Math.Max(1, Math.Min(configuredPanic, Math.Max(PanicButtonUnitsMin, MaxPursuitUnits - pursuitCops)))` — when pursuit is full, `MaxPursuitUnits - pursuitCops ≤ PanicButtonUnitsMin` makes the clamp a no-op and still dispatches `PanicButtonUnitsMin` (default 4). Group: `Math.Max(1, …)` forces ≥1 even at/over capacity. Non-pursuit calls use `configuredPanic`/`configuredGroup` directly. With a 2.5 s debounce, panic can be spammed → unbounded `_activeUnits` growth (up to 12 per press per Settings clamp).
- **Why it matters**: Violates "global ambient density caps must be strictly respected" and §9.3 capacity enforcement for pursuit-adjacent dispatches; can flood the world with emergency units.
- **Recommendation**: Enforce a real `MaxBackupUnits`/global cap using the already-computed active count; make the capacity term authoritative (no `Math.Max(min,…)` override) or explicitly document "at least N on panic" and cap total.

#### B1-10 — Commandeering ignores pursuit cruisers (`IsPursuitCruiser`) and doesn't relinquish from PursuitFeature
- **Severity**: High · **Category**: Dual-ownership / cross-feature
- **Where**: `BackupFeature.cs:1603-1812` (candidate pool), `1814-1829` (`ReleaseFromOtherFeatures` — Stationary/Patrol/Response/TrafficStop only).
- **Description**: `FindCandidateAmbientVehicle` screens only BackupFeature state (`_activeUnits`, `_reservedCruisers`); it never calls `PursuitFeature.IsPursuitCruiser(v)` (contrast `AssistFeature.cs:183` and `ResponseFeature.cs:766`, which do). `ReleaseFromOtherFeatures` also omits `PursuitFeature.RelinquishCruiser`. A pursuit standby/interceptor cruiser (not yet `IsPedInPursuit`, `IsCopBusy` false) can be selected and commandeered while PursuitFeature still owns it.
- **Why it matters**: Cross-feature dual ownership of a cruiser (standards §Ghost Drafting spirit; known "dual-ownership" history).
- **Recommendation**: Exclude `PursuitFeature.IsPursuitCruiser(v)` from candidates and add `try { PursuitFeature.RelinquishCruiser(cruiser); } catch { }` to `ReleaseFromOtherFeatures`.

### MEDIUM

#### B1-11 — `_syncLock` held across the whole heavy `Reengage` call
- **Severity**: Medium · **Category**: Concurrency / Zero-Lock rule
- **Where**: `BackupFeature.cs:1488-1541` (`TryReengageDismissingUnit`: `lock` then `bestCandidate.Reengage(...)` at 1538).
- **Description**: `Reengage` does weapon/native ops, starts fibers, and calls `UpdateBlipStates()` (blip natives). No `GameFiber.Sleep/Yield` or `LoadAndWait` are on that path today (verified), so it is not a *current* strict violation, but it holds `_syncLock` across substantial non-trivial work and blocks `GetActiveCount`, `IsBackupCruiser`/`IsBackupOfficer` (called on Pursuit/Assist hot paths) for the duration.
- **Why it matters**: Fragile w.r.t. the Zero-Lock-Across-Yields rule — any future yield added to `Reengage`/`UpdateBlipStates`/order-officers becomes a deadlock. Also a periodic stall for other fibers.
- **Recommendation**: Select `bestCandidate` under the lock, release, then call `Reengage` outside the lock (snapshot pattern already used elsewhere in the file).

#### B1-12 — `BackupUnitSession` state mutated concurrently from three fibers without synchronization
- **Severity**: Medium · **Category**: Unsynchronized shared state
- **Where**: `BackupFeature.cs:270-293` (`OnLspdfrCalloutFinished` → `HoldOnSceneAfterCallout`), `457` (backup fiber → `unit.Update`), `764-769`/`819-824` (dispatch fibers → `Reengage`), `1536-1540` (under `_syncLock` `Reengage`).
- **Description**: `_activeUnits` is lock-protected, but the sessions it holds are not. LSPDFR's callout-finished event, a per-request dispatch fiber, and the backup update fiber can all mutate the same session's `State`/tasks (`HoldOnSceneAfterCallout` sets `ArrivedOnScene`; `Reengage` resets state and re-tasks) while `Update` is mid-switch.
- **Why it matters**: Inconsistent state transitions, duplicated tasking, wrong blips; potential logic corruption (not necessarily a hard crash, but a correctness risk central to this feature).
- **Recommendation**: Serialize session mutations (queue re-engage/hold requests to the backup fiber, or guard each session with its own lock and make `Update` snapshot state).

#### B1-13 — Settings strictness trap: defaults + missing UB silently disable all fallback spawns
- **Severity**: Medium · **Category**: Settings validation
- **Where**: `BackupFeature.cs:1153, 1215-1219, 1266-1270, 1324-1328`; `UltimateBackupService.cs:430-434, 482-486, 530-534`.
- **Description**: Defaults are `UseUltimateBackup=true, AllowFallback=false`. If UB is not installed (`IsAvailable == false`) and `AllowFallback=false`, the guard `Settings.UseUltimateBackup && !Settings.AllowFallback` aborts the native fallback in every spawn path, logging only at `Debug`. The native spawners also re-reject on `Settings.UseUltimateBackup && !Settings.AllowFallback`. Net default behavior on a non-UB install: only ambient commandeering works; if commandeering also fails/disabled, dispatch never spawns anything, with no user-facing warning. There are also two overlapping, unvalidated flags — `AllowFallback` (integration) vs `AllowFallbackSpawning` (backup) — with no boot-time cross-check.
- **Why it matters**: Standards §Settings "strict validation"; silent no-op behavior is a support/correctness hazard. Also, `UseUltimateBackup=false` (user intent: use native) is still blocked by `AllowFallback=false`.
- **Recommendation**: At boot, if `UseUltimateBackup && !UB.IsAvailable`, auto-downgrade (permit native fallback) and log/notify; when `UseUltimateBackup=false`, permit native spawns as the primary path; validate/clarify the two fallback flags.

#### B1-14 — No per-unit exception isolation in `UpdateActiveUnits`
- **Severity**: Medium · **Category**: Robustness
- **Where**: `BackupFeature.cs:436-476`.
- **Description**: The loop calls `unit.Update(...)` with no per-unit try/catch. Any exception propagates to `BackupProcessLoop`'s catch (359-362), aborting the remainder of the tick and skipping the `toRemove` cleanup — so a single misbehaving unit can starve all later units and prevent Completed/invalid units from being removed, every tick.
- **Why it matters**: One bad entity degrades the entire feature rather than one unit.
- **Recommendation**: Wrap each `unit.Update` (and its disposal) in try/catch that marks the unit Completed/removes it.

#### B1-15 — O(N) nested world scans per candidate (hitching)
- **Severity**: Medium · **Category**: Performance
- **Where**: `BackupFeature.cs:1713` (`World.GetEntities(veh.Position, 15.0f, …)` per candidate), `1769` (`World.GetEntities(veh.Position, 20.0f, …)` per driverless candidate), inside a pool seeded from `World.GetEntities(playerPos, 350f, ConsiderAllVehicles)` (1635).
- **Description**: For each candidate the code issues additional radius scans (prisoner check + designated-officer search). In dense traffic the 350 m pool can be large, producing many sub-scans per dispatch on the fiber.
- **Why it matters**: Frame hitching on panic/group dispatch in busy areas.
- **Recommendation**: Reuse a single nearby-entity scan, budget the candidate count, or defer the occupant/designated-officer scans to only the near-best candidates.

#### B1-16 — `GetCalloutFromHandle` reflection is fragile and swallows all failures
- **Severity**: Medium · **Category**: Robustness / reflection
- **Where**: `BackupFeature.cs:1415-1440`.
- **Description**: A single `try` wraps both the `"Object"` property probe and the `GetRuntimeProperties()` loop. `GetRuntimeProperties()` includes indexers; `GetValue(handle)` on an indexer throws `TargetParameterCountException`, which aborts the entire scan → helper returns `null` even if a later property would have yielded the `Callout`. All exceptions are swallowed (`catch { }`).
- **Why it matters**: Callout position/friendly-name resolution silently degrades to waypoint-only behavior; hard to diagnose.
- **Recommendation**: Probe properties individually with `GetIndexParameters().Length == 0`, catch per-property, and log at Debug on total failure.

#### B1-17 — UB partial-success leaks the UB vehicle (and mis-pairs crew)
- **Severity**: Medium · **Category**: Resource leak
- **Where**: `BackupFeature.cs:1195-1227` (ambulance), `1246-1278` (fire).
- **Description**: `amb`/`fire` (and `passMedic`/`passFireman`) are assigned from the UB result when the vehicle exists and the list is non-empty, *before* validating the crew. If the vehicle is valid but the crew is not, the code falls through to the native spawn while the UB vehicle (persistent) is abandoned, and any UB-provided `passMedic`/`passFireman` can be attached to the unrelated native session.
- **Why it matters**: Leaked persistent UB vehicle; crew/vehicle mismatch.
- **Recommendation**: Only accept the UB result if both vehicle *and* all crew validate; otherwise `Dismiss`/clean up the UB vehicle before falling back, and re-derive the crew from the native spawn only.

#### B1-18 — `PurgeAll` ignores the `immediateDelete` contract (soft purge hard-deletes)
- **Severity**: Medium · **Category**: Teardown / API contract
- **Where**: `BackupFeature.cs:171-178` (`PurgeAll()` always calls `PurgeAllInternal(immediateDelete: true)`), `CadenceMenu.cs:295-302`.
- **Description**: `CadenceMenu.PurgeAllFeatures(bool immediateDelete)` reflects a `PurgeAll(bool)` overload, but `BackupFeature.PurgeAll` (like every feature) has zero parameters, so the dispatch always takes the `Invoke(null, null)` branch and `immediateDelete` is discarded. BackupFeature then always hard-deletes (`Cruiser.Delete()/ofc.Delete()`), even for "soft" purge callers (`Main.cs:169`, `CadenceMenu.cs:734`).
- **Why it matters**: Soft purge still deletes persistent backup peds/vehicles abruptly (amplifies B1-1 crash exposure) and contradicts the intended `immediateDelete` semantics.
- **Recommendation**: Give `BackupFeature.PurgeAll` a `bool immediateDelete = true` parameter (or add a distinct overload) and honor it; fix the reflection dispatch or replace it with direct calls.

#### B1-19 — Menu/keybind processing gated by enable flags (possible stuck menu)
- **Severity**: Medium · **Category**: Logic / edge case
- **Where**: `BackupFeature.cs:325-350` — early `continue` at 331-334 skips both `_menuPool.ProcessMenus()` and `ProcessKeybinds`.
- **Description**: If `Settings.EnableBackup`/`EnablePlugin` flips false while `_backupMenu.Visible` (e.g., the Cadence menu toggle sets `Settings.EnableBackup` at `CadenceMenu.cs:666`), the backup loop stops processing the menu before it is closed. (The toggle also calls `ToggleFeature`/`Stop`, which likely hides it — so this is an edge case, not confirmed reachable.)
- **Why it matters**: Potential unclosable menu overlay if the flag changes without a `Stop()`.
- **Recommendation**: Process/close the menu and handle the dismiss keybind regardless of enable flags, before the early `continue`.

### LOW

#### B1-20 — Thread-unsafe `Random` and non-atomic callsign counter
- `BackupFeature.cs:53` (`_rand`, unlocked, unlike `Settings._rand` which uses `lock`) and `2357-2363` (`_callsignCounter++`). Concurrent dispatch fibers can corrupt `Random` state / duplicate callsigns. Use `lock`/`Interlocked`.

#### B1-21 — Hardcoded magic values throughout
- Distance/time literals: `120.0f` (759, 814), `150.0f` (1121), `300.0f` (1527), `350.0f` (1608), `2500` debounce (235, 249), `1200` dismiss hold (416), `650`/`1200` pacing (970), `600` (1101), `40/60` (1984), `15/20` scans (1713/1769), `0.1f` (2614). Promote to named constants/settings.

#### B1-22 — Inconsistent dispatch pacing
- `DispatchUnitsSequence` guards the final sleep (`968-971`), but `DispatchCalloutUnitsSequence` always `GameFiber.Sleep(600)` after the last unit (`1101`). Align them.

#### B1-23 — `SoundPlayer` not disposed; dev-only audio path ships
- `BackupFeature.cs:2313-2314` creates `SoundPlayer` without `Dispose()`; `2306` includes a `src/Audio` fallback path that only exists in a dev tree. Use `using` and remove the dev path.

#### B1-24 — Swallowed exceptions (standards deviation)
- Empty `catch { }` at `150`, `163` (`Stop`), `1437` (`GetCalloutFromHandle`), `1708`/`1719` (occupant/arrest scans), plus `Dispose` (`3797`, `3809`). Standards discourage swallowing; log at Debug at minimum.

#### B1-25 — Stale comment and null hygiene
- `1563` comment says "0m minimum for Panic/Group" but no such logic exists. `453` can push `null` into `toRemove`; `1665` dereferences `u.Cruiser` without `u != null` (other sites guard). Add guards.

#### B1-26 — Non-volatile cross-fiber flags
- `_isRunning` and the debounce timers (`56-60`) are plain fields read/written across fibers without `volatile`/lock. Benign today; mark `volatile` or document single-writer.

#### B1-27 — Player-relative spawn geometry for callout/waypoint dispatch
- `1169` uses `TryFindStaggeredBackupSpawnPoint(playerPos, playerH, …)` even for distant waypoint/callout destinations (spawns behind the player, then long drive), and `GetRoadsideCurbPosition` falls back to `playerPos` (`1872`). Combined with `World.GetNextPositionOnStreet` placements this can put arriving units in travel lanes — check against standards §Placement/Lane (6.5 m) intent.

#### B1-28 — Waypoint takes priority over active callout (design note)
- `1369-1385`: if a map waypoint is set while a callout is running, dispatch targets the waypoint, not the callout scene. May be intentional; flag for confirmation (`TriggerCalloutResponse`/Callout menu label says "Responding").

---

## Positive checks (no findings)
- Banned weapon hash `0xA2719248` absent from `src/`; `WeaponUnarmed = 0xA2719263` correct (per standards §Weapon Hash).
- In-region natives use uppercase canonical names (`IS_WAYPOINT_ACTIVE`, `GET_FIRST_BLIP_INFO_ID`, `DOES_BLIP_EXIST`, `GET_BLIP_INFO_ID_COORD`, `SET_VEHICLE_HANDBRAKE`), no raw hex natives, no void-with-generic-return.
- Snapshot-under-lock pattern used correctly in `DismissAll`, `OnLspdfrCalloutFinished`, panic/group candidate lists, `UpdateActiveUnits`.
- `BlipService.SafeDeleteBlip(ref …)` used consistently (pass-by-ref) in this region's blip handling.

## Open Questions
1. Is there an authoritative LSPDFR audio-name list used to validate `ASSISTANCE_REQUIRED` and the `AMBULANCE/FIRETRUCK …_01` variants? Are any of these known to throw on LSPDFR's own thread?
2. Who sets `Settings.EnableBackup` besides `CadenceMenu.cs:666`, and does `ToggleFeature` always call `Stop()` (closing the menu) — i.e., is B1-19 reachable?
3. Intended relationship between `Settings.AllowFallback` (integration) and `Settings.AllowFallbackSpawning` (backup)? Should B1-13 auto-downgrade or hard-fail?
4. Is `PursuitFeature` expected to enroll backup officers (is `_enrolledPursuitOfficers` ever populated in Part 2)? This determines B1-1's real-world frequency.
5. Does `UpdateActiveUnits` removal path (B1-14) have per-unit guards added in Part 2's `UpdateEnRoute`/`UpdateOnScene`?
6. Is a `MaxBackupUnits` global cap intended (dead `activePolice`/`grpActivePolice` suggest yes)?
7. Are `World.GetEntities` default entity-count limits sufficient, or is the overload with an explicit max needed (B1-15)?
