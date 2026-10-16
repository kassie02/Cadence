# Cadence Audit — Strand D: TrafficStop / Stationary / Response

Auditor pass against `CODING_STANDARDS.md` (correctness baseline) and the three assigned feature files.
Read-only audit. No source/config/doc files were modified.

## Files covered
- `src/Features/TrafficStopFeature.cs` (1304 lines)
- `src/Features/StationaryFeature.cs` (1100 lines)
- `src/Features/ResponseFeature.cs` (1079 lines)

Cross-read for verification (not audited in full): `CODING_STANDARDS.md`, `src/Services/BlipService.cs`,
`src/Services/UltimateBackupService.cs`, `src/Common/NativeTasks.cs`, `src/Common/WorldUtils.cs`,
`src/Core/Settings.cs`, `src/Core/Main.cs`, `src/Features/PatrolFeature.cs`,
`src/Features/BackupFeature.cs`, `src/Features/PursuitFeature.cs`, `src/UI/CadenceMenu.cs`.

---

## Critical

### D1 — Cross-feature relinquish/ownership race disposes units adopted by another feature (TrafficStop)
- **Severity:** Critical
- **Category:** Cross-feature ownership / use-after-cleanup / concurrency
- **File:line:** `TrafficStopFeature.cs:304-326` (snapshot loop), `196-215` (`RelinquishCruiser`), `1187-1241` (`Dispose`)
- **Description:** `UpdateActiveStops` takes a snapshot of `_activeStops` under lock and then iterates outside the lock. `RelinquishCruiser` (called from `ResponseFeature`, `PursuitFeature`, `BackupFeature`) removes the stop from `_activeStops` and calls `stop.Relinquish()`, which sets `IsCompleted = true`. The owning fiber's snapshot still references that stop, so on its next iteration `!stop.IsValid()` is true (because `IsValid()` returns false when `IsCompleted`), and it calls `stop.Dispose(immediateDelete:false)` (line 322) — dismissing/clearing the exact cruiser and officer that the other feature just took ownership of. It also calls `NativeTasks.SetSilentSiren(Cruiser,false)` (line 1230), killing the adopted unit's emergency lights.
- **Why it matters:** Dual ownership → the adopted response/pursuit unit is silently dismissed or has its siren/lightbar and tasks cleared one tick after adoption. Classic ghost/orphan unit and state corruption.
- **Recommendation:** Add a `Relinquished`/`Disposed` guard flag distinct from `IsCompleted`; the snapshot loop must skip/dispose only instances that are still owned (e.g. re-check membership under `_syncLock` before disposing: `if (!_activeStops.Contains(stop)) continue;`), and `Relinquish` must leave a tombstone the owner loop honors.

### D2 — Same snapshot/ownership race in StationaryFeature
- **Severity:** Critical
- **Category:** Cross-feature ownership / use-after-cleanup / concurrency
- **File:line:** `StationaryFeature.cs:351-425` (snapshot), `243-263` (`RelinquishCruiser`), `1012-1051` (`Relinquish`), `1053-1097` (`Dispose`)
- **Description:** Identical pattern to D1. `UpdateActiveUnits` snapshots `_activeUnits`, `RelinquishCruiser` removes the unit and sets `IsCompleted`, and the snapshot loop then hits `!unit.IsValid()` and calls `unit.Dispose(immediateDelete:false)` (line 371 / 400), dismissing the cruiser/officer/handbrake state another feature adopted.
- **Why it matters:** Adopted stationary cruisers are dismissed at next tick; yields ghost units and defeats EmergencyRun/Pursuit commandeering.
- **Recommendation:** Same membership re-check under lock before disposal; separate `Disposed` from `IsCompleted`.

### D3 — Same snapshot/ownership race in ResponseFeature
- **Severity:** Critical
- **Category:** Cross-feature ownership / concurrency
- **File:line:** `ResponseFeature.cs:332-397` (watchdog snapshot), `158-178` (`RelinquishCruiser`), `995-1024` (`Relinquish`), `1026-1076` (`Dispose`)
- **Description:** `RunResponseWatchdog` snapshots `_activeUnits`; `RelinquishCruiser` removes and sets `State = Finished`; the watchdog's snapshot loop then sees `IsFinished` (line 379) and calls `unit.Dispose(immediateDelete:false)`, dismissing the vehicle/peds another feature adopted.
- **Why it matters:** Same dual-ownership corruption as D1/D2.
- **Recommendation:** Re-verify membership under `_syncLock` before disposing; add an explicit `_disposed` flag; do not treat `Finished` as permission to destroy after an external relinquish.

### D4 — Non-cooperative `GameFiber.Abort()` can interrupt UB/native/model-load mid-flight
- **Severity:** Critical
- **Category:** Concurrency & teardown / native safety
- **File:line:** `TrafficStopFeature.cs:64-71`, `StationaryFeature.cs:64-71`, `ResponseFeature.cs:84-91`
- **Description:** `Stop()` calls `_xxxFiber.Abort()` while the fiber may be blocked inside `UltimateBackupService.AcquireUbGate()` (`GameFiber.Yield` loop), a UB API call, `model.SafeLoad`, or a native task. Thread aborts raised inside third-party UB internals or native marshalling can corrupt engine/UB state, and the UB `finally { ReleaseUbGate(); }` runs during unwind while UB itself may be left half-initialised.
- **Why it matters:** Potential hard crash / corrupted UB global state on plugin unload or feature toggle; violates the "no workarounds" and teardown-order expectations.
- **Recommendation:** Use cooperative cancellation: set `_isRunning=false`, then `GameFiber.Wait`/join a short timeout, and only abort as a last resort. Ensure the fiber checks `_isRunning` after every potentially long yield.

---

## High

### D5 — Traffic-stop dismount ignores active-lane safety rule (6.5 m centerline rule)
- **Severity:** High
- **Category:** Roadway geometry / CODING_STANDARDS violation (§Spawning Rules 2)
- **File:line:** `TrafficStopFeature.cs:815-848` (`InitialExiting`, `ApproachPassengerWindow`), `880-936` (pat-down/K9 dismounts); spawn path `422-462`
- **Description:** Unlike `StationaryFeature` (which computes `WorldUtils.IsOnActiveRoadOrLane(spawnPos, 5.5f)` at `StationaryFeature.cs:602` and forces in-vehicle observation), `TrafficStopFeature` never checks lane proximity. Curated `FindBestPulloverSpot` results and the dynamic offroad path are validated only with `IsAreaClear`/`IsSubterraneanOrVoid`/`IsRooftopOrElevatedStructure`. Officers and the civilian driver are then dismounted onto whatever surface was chosen.
- **Why it matters:** Direct violation of CODING_STANDARDS §Placement & Roadway Geometry ("within 6.5 m of an active travel lane centerline … pedestrian scenarios must be disabled and the unit must remain inside the vehicle"). Produces officers standing in live traffic.
- **Recommendation:** Compute active-lane distance in `CreateTrafficStopAt`; if within 6.5 m and no dedicated shoulder, keep officer in vehicle (skip dismount states) or reject the spot. Mirror the Stationary logic.

### D6 — `Tasks.Clear()` without `Functions.RemovePedFromPursuit` (LSPDFR crash path)
- **Severity:** High
- **Category:** Pursuit integration / exception safety (CODING_STANDARDS §Pursuit 1)
- **File:line:** `TrafficStopFeature.cs:1222,1286` (`Dispose`/`Relinquish`); `StationaryFeature.cs:941,1044,1070` (`Mobilize`/`Relinquish`/`Dispose`); `ResponseFeature.cs:1012,1017,1054` (`Relinquish`/`Dispose` clears/among dismiss)
- **Description:** All dismiss/relinquish paths clear ped tasks (`Officer.Tasks.Clear()`, `Driver.Tasks.Clear()`, `CivDriver.Tasks.Clear()`) without first calling `Functions.RemovePedFromPursuit(ofc)`. If any such ped is enrolled in an LSPDFR pursuit, clearing tasks while pursuit AI still owns them triggers the documented LSPDFR `NullReferenceException` (`TaskCopChasePedAdv.cs:1515`).
- **Why it matters:** Can crash the game/plugin during normal dismissal or on unload when a unit was drafted into a pursuit.
- **Recommendation:** Before any `Tasks.Clear()`/`Dismiss()`/`Delete()` on a Cadence officer, call `Functions.RemovePedFromPursuit(ped)` inside a guarded try/catch (standard pattern). Add to a shared teardown helper.

### D7 — `SetupResponseUnit` leaks persistent entities on failure
- **Severity:** High
- **Category:** Resource leak / exception handling
- **File:line:** `ResponseFeature.cs:699-741`
- **Description:** When the method throws after a cruiser/driver/passenger were spawned or commandeered (e.g. `MakePersistent`, blip attach, or `ConfigureEmergencyDriver` throws), the catch logs and returns `null` but never disposes `cruiser`/`driver`/`passenger`. They remain `MakePersistent()`-ed in the world with no owner tracking.
- **Why it matters:** Permanent ghost cruiser + officer that never despawns and is invisible to every watchdog (the unit was never added to `_activeUnits`).
- **Recommendation:** Wrap entity cleanup in the catch (mirror `TrafficStopFeature.CreateTrafficStopAt:680-692`), deleting blip, ped, then vehicle. Consider `try/finally` with a success flag.

### D8 — Response autonomous-run timer only advances on success → dispatch churn
- **Severity:** High
- **Category:** Logic / pacing (CODING_STANDARDS §Pacing)
- **File:line:** `ResponseFeature.cs:440-462` (only `_lastAutonomousRunTime = Game.GameTime;` at line 457 inside the success lock)
- **Description:** If `TryFindRoadDestination` fails or `DispatchOrSpawnCruiser` returns null/invalid, `_lastAutonomousRunTime` is never updated. The 1.2 s loop then re-evaluates a fresh random `cooldown` each tick and keeps retrying continuously, hammering `World.GetNextPositionOnStreet`/native node probes.
- **Why it matters:** Contradicts the pacing mandate and causes measurable perf churn when e.g. UB is unavailable with Fallback=false. TrafficStop/Stationary set the timestamp *before* attempting (correct); Response does not.
- **Recommendation:** Set `_lastAutonomousRunTime = now` immediately after passing the cooldown check, regardless of spawn outcome.

### D9 — All emergency service types disabled still spawns Police
- **Severity:** High
- **Category:** Settings enforcement / logic
- **File:line:** `ResponseFeature.cs:475-497`
- **Description:** `serviceType` defaults to `Police`. `candidateServices` is only populated for allowed services, and `serviceType` is only reassigned `if (candidateServices.Count > 0)`. If the user disables `AllowPoliceRuns`, `AllowFireRuns`, and `AllowAmbulanceRuns`, autonomous runs still dispatch Police. Even with only variety off, police runs ignore `AllowPoliceRuns`.
- **Why it matters:** User configuration is silently ignored; violates strict settings-honoring expectations.
- **Recommendation:** If `candidateServices.Count == 0`, abort the autonomous run (return) rather than defaulting to Police; separately gate police dispatch on `Settings.AllowPoliceRuns`.

---

## Medium

### D10 — Stationary scenario child fibers are untracked and outlive teardown
- **Severity:** Medium
- **Category:** Resource leak / teardown ordering / use-after-cleanup
- **File:line:** `StationaryFeature.cs:836-849, 856-874, 881-895, 902-923`
- **Description:** Each dismounting scenario (1-4) launches an anonymous `GameFiber.StartNew(...)` that sleeps 2 s + 2.5 s and then tasks the officer. These fibers are never stored, never named, and never aborted. On `Stop()`/`PurgeAll` the main stationary fiber is aborted and entities are deleted, but the child fibers keep running and can call `TaskGoTo`/`TASK_START_SCENARIO_IN_PLACE` on a just-deleted ped (guarded by `Exists()` but with a TOCTOU window).
- **Why it matters:** Fiber + entity leak across toggles/unload; small race window on deleted handles.
- **Recommendation:** Track child fibers per unit and abort/join them in `Dispose`, or fold the delay into the unit state machine (`IdleScenario` sub-timers) using the existing fiber.

### D11 — Station-keeping end states have no absolute timeout → immortal units
- **Severity:** Medium
- **Category:** Logic / resource leak
- **File:line:** `TrafficStopFeature.cs:1022-1063` (`ReleasingVehicles`), `StationaryFeature.cs:785-819` (`EnteringVehicle`/`CruisingAway`)
- **Description:** Completion is gated on the unit physically driving away (`distToPlayer >= 120/180` and elapsed windows). If the officer/civilian is boxed in, blocked, or the task fails, the state never completes and the unit is never despawned. The only escape is the outer distance/`IsValid` watchdog — which for TrafficStop requires >450 m *and* contacts LSPDFR busy checks.
- **Why it matters:** Accumulating stuck units, each pinning a persistent cruiser, peds, blip and prop resources.
- **Recommendation:** Add an absolute lifetime cap per station-keeping state (e.g. force `IsCompleted`/dispose after N seconds regardless of distance).

### D12 — Highway/freeway exclusion missing from dynamic spawns
- **Severity:** Medium
- **Category:** Roadway geometry / CODING_STANDARDS violation (§Spawning Rules 2)
- **File:line:** `TrafficStopFeature.cs:438-459` (dynamic path), `StationaryFeature.cs:501-522` (dynamic path)
- **Description:** Dynamic off-road search rejects `IsOnActiveRoadOrLane` but never checks `WorldUtils.IsHighway`. CODING_STANDARDS require high-speed freeways to be excluded from dynamic roadside spawning (curated off-ramp/weigh-station only).
- **Why it matters:** Ambient dismounted units can spawn on freeway shoulders, violating the standard and producing unsafe/immersion-breaking scenes.
- **Recommendation:** Add `!WorldUtils.IsHighway(pos)` to dynamic candidate validation (both features).

### D13 — Curated standby/pullover candidates skip rooftop/elevated validation in Stationary
- **Severity:** Medium
- **Category:** Roadway geometry / validation parity
- **File:line:** `StationaryFeature.cs:480-499` (`TryFindStationaryCoordinate` Priority 1/2)
- **Description:** Priority 1 (`FindBestStandbyLocation`) and Priority 2 (`FindBestPulloverSpot`) only reject `IsSubterraneanOrVoid`/`IsAreaClear`. `TrafficStopFeature` additionally rejects `IsRooftopOrElevatedStructure` for curated spots (`:430`). Stationary can therefore spawn on parking-deck/roof geometry.
- **Why it matters:** Inconsistent safety validation → elevated orphan units; also `CreateStationaryUnitAt` does not re-check rooftop.
- **Recommendation:** Add `IsRooftopOrElevatedStructure` checks to Stationary curated candidates and to `CreateStationaryUnitAt`.

### D14 — Hardcoded magic thresholds that should be settings
- **Severity:** Medium
- **Category:** Configurability / CODING_STANDARDS ("no workarounds", strict config)
- **File:line:** `TrafficStopFeature.cs:98,104,109,331,399,406` (maxSearch, 35 m spacing, 450 s/45 s grace, 80 m occupancy, 10 m clear); `StationaryFeature.cs:103,109,379,455,458` (maxSearch, 70 m occupancy, 45 s grace, 8 m clear); `ResponseFeature.cs:368` (`DespawnDistance + 100`), `441` (30-95 s pacing), `931` (18 m/35 m arrive radii), `934` (100 s en-route timeout), `962` (18 s on-scene)
- **Description:** Many tuning values are literals scattered through the files with no INI backing, while `Settings` already exposes related distances/caps.
- **Why it matters:** Users cannot tune behavior; values can drift between features; downtime tuning requires recompiles.
- **Recommendation:** Promote occupancy radius, grace period, station-keeping caps, en-route timeout and on-scene dwell to `Settings` with clamps.

### D15 — `Settings.AllowFallbackSpawning` is dead config; the real gate is `AllowFallback`
- **Severity:** Medium
- **Category:** Settings/config bug
- **File:line:** `Settings.cs:101` (declare), `390` (parse `allowfallbackspawning`), vs `169` (declare `AllowFallback`), `465` (parse `fallback`/`allowfallback`)
- **Description:** `[Backup] AllowFallbackSpawning` is parsed but never read anywhere; every feature gates on `Settings.AllowFallback` from `[Integration]`. A user setting `AllowFallbackSpawning=true` in `[Backup]` has no effect and may wrongly believe fallback is enabled.
- **Why it matters:** Silent misconfiguration of the exact `UseUltimateBackup`/`AllowFallback` contract this audit must verify.
- **Recommendation:** Remove the orphan or alias it to `AllowFallback` (and document the single authoritative key).

### D16 — Concurrent dispatches can commandeer the same cruiser (no reservation)
- **Severity:** Medium
- **Category:** Concurrency / dual ownership
- **File:line:** `ResponseFeature.cs:469-516` (+ `TriggerTestRun:99-134`, `OnLspdfrCalloutNotAccepted:204-253`, `RunAutonomousSpawningCycle:406-463`); `FindIdleAmbientPoliceVehicle:743-790`
- **Description:** `FindIdleAmbientPoliceVehicle` selects a candidate but does not reserve it, and `DispatchOrSpawnCruiser` can run concurrently on the response fiber, a callout-takeover fiber, and the menu/UI thread. Two callers can select the same vehicle; the second `RelinquishCruiser` returns false but `SetupResponseUnit` still runs on the same cruiser/driver, producing two `ResponseUnitInstance`s over one vehicle and two blips.
- **Why it matters:** Duplicate ownership/blips, task thrash, doubled siren/blip churn; leak of the first instance's blip.
- **Recommendation:** Reserve candidates in a locked `HashSet<Vehicle>` before relinquish/setup, releasing on failure/dispose; or serialize dispatch through a single fiber.

### D17 — Broad/swallowed exception handling hides real failures
- **Severity:** Medium
- **Category:** Exception handling
- **File:line:** e.g. `TrafficStopFeature.cs:71,343,547,607,1130,1167,1180`; `StationaryFeature.cs:71,391,590,966,992,1005`; `ResponseFeature.cs:79,425,776,780,787` and numerous `catch { }`
- **Description:** Many empty `catch { }` blocks silently discard failures (tasking, grounding, focus-facing, LSPDFR queries). Combined with `catch (Exception)` in `Dispose`, genuine errors are invisible.
- **Why it matters:** Failures become untraceable; masks the very state drift that produces stuck units.
- **Recommendation:** At minimum log at Debug with context; reserve empty catches for genuinely best-effort cleanup with a brief comment.

### D18 — `WorldUtils.IsAreaClear` returns `true` on exception
- **Severity:** Medium
- **Category:** Native invocation / safety (fail-open)
- **File:line:** `WorldUtils.cs:257-260` (used by all three features)
- **Description:** On any exception the clearance check fails *open*, treating an unknown area as clear. Used as the final gate before spawning.
- **Why it matters:** Can allow stacking/orphan spawns in exactly the situations where native queries failed.
- **Recommendation:** Fail closed (`return false`) on exception, or retry once.

---

## Low

### D19 — Callsign generation is not thread-safe and recycles duplicates
- **Severity:** Low
- **Category:** Concurrency / logic
- **File:line:** `TrafficStopFeature.cs:695-701`, `StationaryFeature.cs:666-672`, `ResponseFeature.cs:851-857`
- **Description:** `_callsignCounter++` is non-atomic across feature fibers/menu threads and wraps (`>99 → reset`), producing duplicate `DIV-NN` callsigns and occasional collisions with the same-number rebase.
- **Recommendation:** `Interlocked.Increment` with modulo, or a shared callsign allocator.

### D20 — Incomplete siren/lightbar native protocol in Stationary Dispose
- **Severity:** Low
- **Category:** Native invocation standards (§Siren & Lightbar Protocol)
- **File:line:** `StationaryFeature.cs:1078-1084`
- **Description:** Dispose performs `IsSirenOn=false; IsSirenSilent=true; SET_VEHICLE_SIREN(false)` but omits `SET_VEHICLE_HAS_MUTED_SIRENS`. `TrafficStopFeature.Dispose` uses `NativeTasks.SetSilentSiren` (correct). Inconsistent protocol.
- **Recommendation:** Route all siren shutdown through `NativeTasks.ShutOffSirenAndLights`/`SetSilentEmergencyLights`.

### D21 — Full form re-issue of silent siren every 500 ms
- **Severity:** Low
- **Category:** Native invocation / churn
- **File:line:** `TrafficStopFeature.cs:803-809`
- **Description:** `Update` calls `SetSilentSiren(Cruiser,true)` every 500 ms, which sets `IsSirenOn=true`, `IsSirenSilent=true`, `SET_VEHICLE_SIREN(true)` and `SET_VEHICLE_HAS_MUTED_SIRENS(true)`. Repeatedly toggling siren-on can cause chirps/audio artifacts on some vehicles.
- **Recommendation:** Only reinforce when the native siren is actually off; keep the managed siren flag steady.

### D22 — `TASK_FOLLOW_NAV_MESH_TO_COORD` fallback uses integer literals
- **Severity:** Low
- **Category:** Native invocation standards (§Exact Parameter Types)
- **File:line:** `TrafficStopFeature.cs:1132`, `StationaryFeature.cs:970`
- **Description:** Fallback passes `-1` and `0` where the native expects `float`; works via coercion but violates the explicit-float rule.
- **Recommendation:** Use `-1f`/`0f`.

### D23 — Stationary relinquish matches only the cruiser reference, not the officer's current vehicle
- **Severity:** Low
- **Category:** Cross-feature ownership parity
- **File:line:** `StationaryFeature.cs:252` vs `TrafficStopFeature.cs:205-206`
- **Description:** `TrafficStopFeature.RelinquishCruiser` also matches `stop.Officer.CurrentVehicle == cruiser`; Stationary only matches `unit.Cruiser == cruiser`. If the instance's cruiser reference and the in-world pairing diverge, relinquish silently fails.
- **Recommendation:** Mirror the TrafficStop dual-match (cruiser ref OR officer's current vehicle).

### D24 — Reflection-based callout position extraction is fragile
- **Severity:** Low
- **Category:** Integration / workaround
- **File:line:** `ResponseFeature.cs:255-289`
- **Description:** `ExtractCalloutPosition` reflects `LHandle.Object` then `CalloutPosition`/`Position`. A future LSPDFR internal rename yields `Vector3.Zero` silently (takeover simply never fires).
- **Recommendation:** Prefer a supported API if available; otherwise log once at Warn when the property is not found.

### D25 — `new Random()` per call in `ShouldSpawnHighwayUnit`
- **Severity:** Low
- **Category:** Logic / quality
- **File:line:** `WorldUtils.cs:348` (invoked from all three features)
- **Description:** Allocates a time-seeded `Random` on each call; rapid successive calls can return correlated values, biasing highway/local split.
- **Recommendation:** Share a single `Random` with a lock, or use the feature's existing `_rand`.

### D26 — `_state` in Stationary unit is not volatile while read by spawned fibers
- **Severity:** Low
- **Category:** Concurrency
- **File:line:** `StationaryFeature.cs:839,844,859,865,884,890,905,911,917`
- **Description:** Child scenario fibers read `_state` while the owning fiber writes it (`Mobilize`, etc.) without a memory barrier. Practically benign under cooperative scheduling, but formally a data race.
- **Recommendation:** If child fibers remain, mark `_state` `volatile` or move the logic into the single owner fiber (preferred; see D10).

### D27 — `TrafficStopFeature.Update` releases vehicles even when a K9 is present, with no scene guard
- **Severity:** Low
- **Category:** Logic / edge case
- **File:line:** `TrafficStopFeature.cs:1007-1011`
- **Description:** K9 is warped into seat 1/2/0 (falls back to `0`, the driver seat) if no free seat — could displace/overlap the driver. Fallback seat `0` is unsafe.
- **Recommendation:** If no free seat, dismiss the K9 rather than warping into the driver seat.

### D28 — Stationary Occupancy spacing (70 m) vs TrafficStop (80 m) inconsistent
- **Severity:** Low
- **Category:** Logic consistency
- **File:line:** `StationaryFeature.cs:455`, `TrafficStopFeature.cs:403`
- **Description:** Two features place ambient scenes that may overlap (Stationary pullover zones are also used by TrafficStop) with different spacing thresholds; a Stationary unit and a TrafficStop can land near each other.
- **Recommendation:** Centralize a cross-feature "scene spacing" constant and consult other features' active cruisers when choosing spots.

---

## Per-file summary

### TrafficStopFeature
Well-structured state machine (citation / pat-down / K9) with correct blip lifecycle via `BlipService`. Main defects: (1) critical dual-ownership race (D1), (2) missing active-lane safety gate that Stationary has (D5), (3) `Tasks.Clear()` without `RemovePedFromPursuit` (D6), (4) no absolute station-keeping timeout (D11), (5) freeway exclusion missing on dynamic spawns (D12), (6) magic thresholds (D14). Native names are canonical uppercase; no banned hashes; float literals mostly correct (D22).

### StationaryFeature
Correct use of handbrake, curb/passenger-side offsets, dismount blip transitions, and `Relinquish` handbrake release. Main defects: same critical dual-ownership race (D2); untracked scenario child fibers (D10); curated spots missing rooftop check (D13); freeway exclusion missing (D12); no station-keeping timeout (D11); incomplete siren shutdown natives (D20); relinquish matches only cruiser ref (D23); non-volatile `_state` race (D26). Child-fiber model is the biggest structural risk.

### ResponseFeature
Correctly enforces `UseUltimateBackup`/`AllowFallback` at every police/ambulance/fire spawn site (both direct guards and the native spawner guards). Main defects: dual-ownership race (D3); `SetupResponseUnit` entity leak on failure (D7); autonomous pacing timer not advanced on failure (D8); all-services-disabled still dispatches police (D9); same-cruiser concurrency (D16); routine lacking a single serialized dispatcher. `[Backup] AllowFallbackSpawning` dead setting verified against the three features (D15). Autonomie lifecycle (EnRoute/Arrived/Finished) has an en-route timeout but no stuck-on-scene mitigation beyond the 18 s dwell.

---

## Open questions / needs verification

1. **D1-D3 reachability window:** Confirm by inspection whether any relinquish path can realistically fire between a feature's snapshot and its iteration in a single tick (cross-fiber). `GameFiber` scheduling makes this possible but the practical frequency is unknown — worth a targeted stress test with Backup/Pursuit + Response + TrafficStop active.
2. **D5 curated-spot lane distances:** Do the curated `ZoneSpawnData` pullover/standby coordinates actually sit >6.5 m from lane centerlines? If so, D5 is latent rather than active. Needs inspection of `ZoneSpawnData.cs` data (not read in full).
3. **D6 pursuit enrollment:** Confirm whether Cadence ambient officers can be enrolled in an LSPDFR pursuit independently of Cadence's own `AddCopToPursuit`, which would make the missing `RemovePedFromPursuit` a live crash path.
4. **D4 abort safety:** Confirm whether `UltimateBackup.API.Functions.get*Unit` can be mid-execution when `Abort()` fires, and whether UB holds internal locks that would be left held.
5. **`Events.OnCalloutNotAccepted` semantics:** Verify the delegate signature `(LHandle)` and that `LHandle.Object` is the accepted callout instance for reflection in the installed LSPDFR version.
6. **`SET_VEHICLE_HAS_MUTED_SIRENS` necessity in D20/D21:** Confirm whether omitted muted-siren arming on dismiss causes audible residual siren on some police models.
7. **`AllowFallbackSpawning` intent:** Determine whether it was meant to be the public key (and `AllowFallback` an internal alias) or genuinely orphaned; affects D15 fix direction.
