# Cadence Audit — Stream A: Core & Common

**Auditor scope:** `src/Core/Main.cs`, `src/Core/Settings.cs`, `src/Core/Logger.cs`,
`src/Common/InputHelper.cs`, `src/Common/NativeTasks.cs`, `src/Common/WorldUtils.cs`.

**Baseline:** `CODING_STANDARDS.md` (correctness contract). Key clauses exercised:
Zero-Lock-Across-Yields, canonical UPPERCASE natives / no void-with-generic-return,
explicit float literals, engine clamps, banned hash `0xA2719248`,
SpawnDistanceMin < SpawnDistanceMax < DespawnDistance,
Inverted Teardown Lifecycle (purge → stop → unhook), Siren protocol,
`TreatWarningsAsErrors=true`.

**Verdict preview:** No banned weapon hash, no raw-hex native calls, no lock held
across a yield, and orientationally the teardown order is correct. The highest risk
is **teardown fragility** (a single throw aborts remaining cleanup) and the
**Logger shutdown race**. Secondary risk is **config validation accepting NaN/Infinity**.

---

## Critical

### A1 — Teardown is a single sequential try-block; one failure aborts all remaining cleanup
- **Severity:** Critical
- **Category:** Cleanup / teardown ordering; exception handling
- **File:line:** `src/Core/Main.cs:79-86` (Finally) and `src/Core/Main.cs:167-172` (off-duty)
- **Description:** In `Finally()` the cleanup sequence is:
  `StopPlayerVehicleMonitoring()` → `PurgeAllFeatures(immediateDelete:true)` →
  `StopAllFeatures()` → `CadenceMenu.Stop()` → `Logger.Shutdown()`, all inside one
  `try`. The off-duty path (`OnDutyStateChanged(false)`) has the same shape
  (`Close` → `StopPlayerVehicleMonitoring` → `PurgeAllFeatures` → `StopAllFeatures`).
  If any early call throws, the remaining calls never run.
- **Why it matters:** A throw in purge/stop leaves feature fibers alive and
  spawned peds/cruisers/blips orphaned "ghost" entities in the world after the
  plugin is supposed to be unloaded — exactly the failure mode the standards'
  "Inverted Teardown Lifecycle" was written to prevent. If `Logger.Shutdown()` is
  skipped, queued log lines are lost and the worker thread/stream is never
  released. This can crash the game on the *next* unload/reload or leave the
  session in an inconsistent state.
- **Recommendation:** Wrap each teardown step in its own `try/catch` (log-and-
  continue) so one failure cannot abort the rest. Emit a distinct error per step.
  Consider a single `Teardown()` helper shared by `Finally` and off-duty, with a
  `_tornDown` guard.

### A2 — Logger worker not guaranteed stopped before `_fileWriter` is disposed (shutdown race)
- **Severity:** Critical
- **Category:** Concurrency / thread-safety; teardown crash
- **File:line:** `src/Core/Logger.cs:117-151`, `258-299`
- **Description:** `Shutdown()` sets `_isRunning=false`, signals, then
  `_workerThread.Join(1000)`. If the worker is blocked (slow disk flush) the join
  times out and code proceeds to `_fileWriter.Dispose(); _fileWriter = null;`
  while `WorkerLoop`/`DrainQueueToFile` may still be executing. `DrainQueueToFile`
  null-checks `_fileWriter` at `:260` and then dereferences it at `:267`/`:294`
  with no local snapshot, so a `Dispose`/null between check and use yields
  `NullReferenceException`/`ObjectDisposedException`. Those are caught in inner
  `try` blocks, but the worker can also be mid-`WriteLine` on a disposed stream.
  In addition, `_fileWriter` is written by `Shutdown` (under `_initLock`) but read
  by the worker with no lock, so the access is unsynchronized.
- **Why it matters:** Process-exit/DomainUnload runs `Shutdown` on a different
  thread than the worker; racing a finalizer/process teardown with a live writer
  is a classic crash-on-exit. At minimum it silently drops the final drain.
- **Recommendation:** Make `_fileWriter` access thread-safe (capture to a local,
  or guard with a dedicated object, or null it before disposing and have the
  worker re-check). Give the worker an explicit `ManualResetEventSlim` "stopped"
  signal and wait unbounded (or a longer bounded wait + `Interlocked` flag) before
  disposing. Prefer `while (!_isRunning) { ... }` with a clean join, then dispose
  inside the `_initLock` block only after confirming `!IsAlive`.

---

## High

### A3 — `ParseFloat` accepts `NaN`/`Infinity`; distance invariant clamps propagate NaN
- **Severity:** High
- **Category:** Settings/config validation; logic edge cases
- **File:line:** `src/Core/Settings.cs:574-579`, `317-318`, `409-411`
- **Description:** `ParseFloat` uses `float.TryParse(... NumberStyles.Float ...)`,
  which parses the literals `NaN`, `Infinity`, `-Infinity`. `ParseEntry` then
  applies `Math.Max(lo, Math.Min(hi, value))`, and `Math.Min`/`Math.Max` propagate
  `NaN`. The final invariant clamp
  `if (SpawnDistanceMin >= SpawnDistanceMax) ...` is also `false` for `NaN`, so a
  value like `SpawnDistanceMin = NaN` survives as a non-finite distance forever.
- **Why it matters:** A single typo in `Cadence.ini` (or a translated locale value)
  can set spawn/despawn distances to `NaN`. All later distance comparisons against
  `NaN` evaluate `false`, silently killing or mis-gating ambient spawns/despawns
  with no error surfaced — a production "feature silently dead" bug that is very
  hard to diagnose in the field.
- **Recommendation:** Reject non-finite input in `ParseFloat`
  (`if (!float.IsFinite(res)) return defaultVal;` / `float.IsNaN || float.IsInfinity`).
  Additionally validate at the end of `Load` that all three distances are finite
  and enforce the strict invariant, falling back to defaults on failure.

### A4 — `Logger.Write()` lazily initializes on the *calling* thread (game fiber) and does blocking disk I/O
- **Severity:** High
- **Category:** Concurrency / fiber safety; resource init
- **File:line:** `src/Core/Logger.cs:211-214`, `42-106`
- **Description:** If any code logs before `Logger.Initialize()` has run, `Write`
  calls `Initialize()` synchronously. `Initialize` performs
  `Directory.CreateDirectory`, `ArchivePreviousSession` (`File.Move`),
  `MigrateLegacyLogs`, `EnforceArchiveRetention` (`Directory.GetFiles` + deletes),
  and opens a `FileStream`, then spawns the worker thread. Because `Write` is
  reachable from feature fibers and the tick loop, this heavy synchronous work can
  run on a GameFiber mid-frame. It also mutates `AppDomain` events from whatever
  thread first logged.
- **Why it matters:** Synchronous file I/O on a game fiber can hitch the frame or,
  worse, if the caller already holds a state it needs, create surprising ordering.
  The standards prioritize keeping fiber work yield-friendly; init belongs on a
  deterministic startup path, not on first log.
- **Recommendation:** Ensure `Main.Initialize()` calls `Logger.Initialize()` before
  any feature can log (it does today), and make the lazy-init fallback only a
  no-op/console fallback rather than full disk init on arbitrary threads. Guard
  the one-time init with an `Interlocked`/`Lazy` and avoid `AppDomain` mutation
  from non-main threads.

### A5 — Log queue is unbounded for non-DEBUG messages
- **Severity:** High
- **Category:** Resource leak / memory
- **File:line:** `src/Core/Logger.cs:216-228`
- **Description:** The capacity check at `:217` only drops `DEBUG` lines; `INFO`,
  `WARN`, `ERROR` are always enqueued regardless of `_queue.Count`. If the worker
  thread stalls (disk contention, or `CheckMidSessionRotation` blocking on a slow
  `File.Move`), the queue grows without bound.
- **Why it matters:** Under sustained logging (Cadence is chatty) a stalled worker
  produces unbounded memory growth until the game runs out of memory/crashes.
- **Recommendation:** Apply a hard cap for all levels (drop oldest or drop with a
  counted warning), or use a bounded `BlockingCollection`. Ensure the queue is
  drained on rotation and that rotation cannot deadlock the drain.

### A6 — `IsAreaClear` counts the player and Cadence's own units, and fails open on exception
- **Severity:** High
- **Category:** Logic/edge cases; native invocation correctness
- **File:line:** `src/Common/WorldUtils.cs:217-261`
- **Description:** `World.GetEntities(pos, clearanceRadius, ConsiderAllVehicles/
  ConsiderAllPeds)` returns *all* nearby entities, including the local player's
  vehicle and every already-spawned Cadence unit. Any such entity inside the
  clearance radius makes the candidate position "not clear", so own units starve
  future spawns. The `catch { return true; }` at `:257-260` inverts the failure
  semantics: on any native/exception failure it reports the area as clear.
- **Why it matters:** Self-blocking can prevent the forced-ambient density from
  ever being reached (feature looks broken), while the fail-open catch can approve
  spawns in genuinely obstructed/invalid geometry (overlapping cruisers, clipping).
- **Recommendation:** Exclude the player and Cadence-owned entities from the
  consideration set (filter by handle against the owned pool), and make the catch
  fail *closed* (`return false`) or rethrow/log-and-fail-closed, since approving an
  unverified spawn is the more dangerous outcome.

### A7 — Swallowed exception handling across Main (and helpers) hides failures
- **Severity:** High
- **Category:** Exception handling
- **File:line:** `src/Core/Main.cs:62`, `src/Core/Main.cs:77`;
  `src/Common/NativeTasks.cs:83-87`, `124-128`, `258`;
  `src/Common/WorldUtils.cs:257-260`, `445-448`
- **Description:** `IsPlayerOnDuty()` ends with `catch { }` (state probe failure is
  indistinguishable from "off duty"), and `Finally()` wraps the event-unhook in a
  silent `catch { }`. `NativeTasks` inner catches silently fall back to managed
  RPH tasks, masking whether a native name resolution failed or a runtime native
  error occurred (the very "wrong native signature" class the standards flag).
  `WorldUtils` swallows into fail-open/no-op defaults.
- **Why it matters:** The standards prohibit loose/swallowed handling; silent
  catches convert real bugs (bad native call, access violation in a bad native
  signature) into intermittent misbehavior with no diagnostic trail — the hardest
  class of production bug.
- **Recommendation:** Replace silent catches with `Logger.Warn/Debug` including the
  exception type/message (the codebase already has the logger). Keep genuinely
  expected no-op cases minimal and documented.

---

## Medium

### A8 — Duplicate/confusable fallback flags (`AllowFallback` vs `AllowFallbackSpawning`)
- **Severity:** Medium
- **Category:** Settings semantics / logic
- **File:line:** `src/Core/Settings.cs:101` (`AllowFallbackSpawning`), `:169`
  (`AllowFallback`), `:390`, `:465`; used in `Main.cs:116` and pervasively in
  features (e.g. `BackupFeature.cs:1153` vs `:1215`)
- **Description:** Two near-identically named booleans exist:
  `AllowFallback` (UB fallback, default **false**) and `AllowFallbackSpawning`
  (default **true**). The ini documents `Fallback`/`AllowFallback` and separately
  `AllowFallbackSpawning`. Feature code uses both in different branches.
- **Why it matters:** Extremely easy to configure one and believe you configured the
  other; the two defaults disagree, producing surprising spawn behavior. This is a
  configuration-correctness hazard, not merely cosmetic.
- **Recommendation:** Consolidate to one flag or rename to unambiguously distinct
  concepts (`AllowUltimateBackupFallback` vs `AllowNativeFallbackSpawn`) and
  document the interaction.

### A9 — Distance invariants validated only inside `Load()` and only when the ini exists
- **Severity:** Medium
- **Category:** Settings/config validation
- **File:line:** `src/Core/Settings.cs:280-284` (early return), `:317-318`
- **Description:** The invariant clamp lives at the end of `Load()`'s `try`, after
  parsing. If `Cadence.ini` is missing, `Load()` returns at `:283` *before* the
  clamps (defaults currently satisfy the invariant, so latent). If an exception is
  thrown mid-parse at `:330`, the clamps are skipped. `ApplyPreset` and any runtime
  menu edit do not re-validate.
- **Why it matters:** Any future default/preset/menu change can violate
  `SpawnDistanceMin < SpawnDistanceMax < DespawnDistance`, causing spawn/despawn
  thrashing (the exact bug the standards call out) with no safety net.
- **Recommendation:** Factor invariant enforcement into a `ValidateDistances()`
  method and call it from `Load()`, `ApplyPreset()`, and after any settings dialog
  mutation. Also enforce the standards' minimum margins (>= +50 m).

### A10 — `ShouldSpawnHighwayUnit` constructs a new `Random` each call
- **Severity:** Medium
- **Category:** Thread-safety of static state; logic
- **File:line:** `src/Common/WorldUtils.cs:348`
- **Description:** `int roll = new Random().Next(100);` allocates a time-seeded
  generator on every invocation. Rapid successive calls within the same tick can
  share the same seed (same value), biasing distribution; it is also wasteful.
- **Why it matters:** Deterministic-looking rolls de-randomize jurisdiction mix and
  add GC churn in a hot path.
- **Recommendation:** Use a single static generator guarded by a lock (or
  `RandomNumberGenerator`), mirroring `Settings._rand`.

### A11 — Player-vehicle monitoring fiber is signalled but never joined/aborted
- **Severity:** Medium
- **Category:** Resource leak / teardown
- **File:line:** called from `src/Core/Main.cs:80`, `:168`; defined in
  `src/Services/BlipService.cs:398-427`
- **Description:** `StopPlayerVehicleMonitoring()` only clears the flag and deletes
  the blip; `_playerVehicleFiber` is neither aborted nor awaited and is not nulled.
  The loop can run for up to one `GameFiber.Sleep(300)` after teardown begins.
- **Why it matters:** During `Finally`, `PurgeAllFeatures` may delete the same
  entities the loop is inspecting; the sleeping fiber then touches stale state on
  its next iteration. Also the fiber reference leaks across duty cycles.
- **Recommendation:** Track and `Abort()`/await the fiber in stop, then null it.
  Re-check `_playerVehicleMonitoringActive` immediately after `Sleep`.

### A12 — No teardown/reset of service singletons (e.g. `UltimateBackupService._initialized`)
- **Severity:** Medium
- **Category:** Lifecycle / static state
- **File:line:** `src/Core/Main.cs:114` (init) with no counterpart in `Finally`;
  `src/Services/UltimateBackupService.cs:134-140` (`_initialized` never cleared)
- **Description:** `UltimateBackupService.Initialize()` is called on duty; there is
  no matching shutdown/reset on unload. `_initialized`/reflection caches persist.
- **Why it matters:** If RPH reloads the assembly in-session (or duty cycles
  re-enter), stale reflection/type caches and `_initialized` can desync from the
  actual UB availability, and cached delegates can point at an unloaded assembly.
- **Recommendation:** Add `UltimateBackupService.Shutdown()` that clears reflection
  caches/handles and resets `_initialized`, invoked from `Main.Finally`.

### A13 — Double session-summary logging
- **Severity:** Medium
- **Category:** Logging quality
- **File:line:** `src/Core/Main.cs:165` and `src/Core/Logger.cs:130`
- **Description:** Off-duty calls `SessionMetrics.LogSummary()`; `Logger.Shutdown()`
  calls it again. Both run on the same session.
- **Why it matters:** Duplicated/misleading telemetry; extra log noise.
- **Recommendation:** Emit the summary once (e.g., remove from `Main` or from
  `Shutdown`, or guard with a `_summaryLogged` flag).

### A14 — Every log line is mirrored to `Game.LogTrivial` (RagePluginHook.log bloat)
- **Severity:** Medium
- **Category:** Logging quality / performance
- **File:line:** `src/Core/Logger.cs:194-206`
- **Description:** All non-suppressed levels call `Game.LogTrivial`. With
  `EnableDebugLogging` defaulting to `true`, a chatty session can flood
  `RagePluginHook.log`, and RPH log formatting on the game thread is not free.
- **Why it matters:** Oversized RPH logs hurt field triage; per-call overhead adds
  to frame cost in a hot plugin.
- **Recommendation:** Keep the dedicated Cadence log as the verbose sink and mirror
  only `WARN`/`ERROR` (or a separate `EchoToRphLog` toggle) to `Game.LogTrivial`.

### A15 — Loaded anim dicts / models are leaked on timeout
- **Severity:** Medium
- **Category:** Resource leak
- **File:line:** `src/Common/NativeTasks.cs:319-330` (`RequestAnimDict`),
  `src/Common/WorldUtils.cs:367-391` (`SafeLoad`)
- **Description:** Both helpers issue `REQUEST_ANIM_DICT` / `model.Load()` and then
  loop with `GameFiber.Yield`. On timeout they `return false` without
  `REMOVE_ANIM_DICT` / `model.Unload()`, leaving the request pending in the
  streaming manager.
- **Why it matters:** Repeated timeouts accumulate streaming references, wasting
  memory and potentially leaving partially-streamed assets resident.
- **Recommendation:** Release the request on the timeout path before returning
  (best-effort `RemoveAnimDict` / `model.Unload()` with logging).

### A16 — Unsynchronized mutable static settings and `_isOnDuty`
- **Severity:** Medium
- **Category:** Thread-safety of static/global state
- **File:line:** `src/Core/Settings.cs` (all auto-properties), `src/Core/Main.cs:18`
  (`_isOnDuty`)
- **Description:** Settings are plain static get/set properties read from feature
  fibers/tick loop and written from the menu/UI thread and `Load()`. `_isOnDuty`
  is read/written across `Initialize`/`Finally`/event callback with no memory
  barrier or lock.
- **Why it matters:** Races can produce torn/stale reads mid-frame (e.g. a settings
  dialog toggling a feature while its fiber reads the flag), leading to
  inconsistent behavior; `_isOnDuty` double-entry/exit races could double-start or
  double-teardown features.
- **Recommendation:** Introduce a single settings snapshot/version, or make
  `_isOnDuty` an `int` with `Interlocked.CompareExchange` for transitions. At
  minimum document the threading contract.

---

## Low

### A17 — `GetSafeStreetPosition` `nthNode` parameter is dead
- **File:line:** `src/Common/WorldUtils.cs:151`
- **Description:** `nthNode` is accepted but never used; all callers pass `1`.
- **Recommendation:** Remove the parameter or implement node spacing to avoid
  spawning multiple units on the identical node.

### A18 — Inconsistent non-finite validation in `IsSubterraneanOrVoid`
- **File:line:** `src/Common/WorldUtils.cs:17-19`
- **Description:** Checks `NaN` for X/Y/Z but `IsInfinity` only for Z (also misses
  X/Y infinity); the `pos == Vector3.Zero` check is redundant with the explicit
  component check.
- **Recommendation:** Use `!float.IsFinite(pos.X/Y/Z)` once; drop redundancy.

### A19 — Duplicate driving-flag constants / magic numbers not settings
- **File:line:** `src/Common/NativeTasks.cs:17` vs `:46` (`CruisingFlags` and
  `NormalDrivingFlag` both 786603); thresholds throughout `WorldUtils`
  (e.g. `:431` 25 m, `:438` 2.2/-4.0 m, `:502` 3.0 m)
- **Description:** Overlapping constants and hardcoded geometry thresholds that the
  standards' spawning rules describe as tunable behavior.
- **Recommendation:** Deduplicate constants; promote geometry thresholds to named
  constants/settings so they can be tuned without rebuilds.

### A20 — `IsRooftopOrElevatedStructure` rejects legitimate underpasses
- **File:line:** `src/Common/WorldUtils.cs:438`
- **Description:** `deltaZ < -4.0f` flags candidates *below* the nearest road node as
  rooftop/elevated, which can reject valid below-grade/tunnel shoulder positions.
- **Recommendation:** Reconsider the lower bound or handle underpass geometry.

### A21 — `GetAccurateGroundZ` reports success with an unvalidated Z
- **File:line:** `src/Common/WorldUtils.cs:143`
- **Description:** When every probe fails it returns `!IsSubterraneanOrVoid(queryPos)`
  with `safeGroundZ` still equal to `queryPos.Z`, so callers may treat an unverified
  Z as ground.
- **Recommendation:** Return `false` when no ground was actually found, or clearly
  document the "passthrough" semantics.

### A22 — `ParseRange` tokenizer is fragile
- **File:line:** `src/Core/Settings.cs:588`
- **Description:** Splits on `'-', ',', ' ', '/', 't', 'o', 'T', 'O', '.'`, so values
  containing letters or decimals are chopped unexpectedly (e.g. `"1.5"` → `1`,`5`).
  Fine for the current `4-7` form but brittle.
- **Recommendation:** Use an explicit regex (`^\s*(\d+)\s*(?:[-/,]|to)\s*(\d+)\s*$`)
  or `int.TryParse` on a single delimiter.

### A23 — Archive retention ordering and stream handle cleanup
- **File:line:** `src/Core/Logger.cs:429-442` (sort by `CreationTimeUtc`), `:25`
  (`_wakeEvent` never disposed)
- **Description:** `File.Move` preserves creation timestamps, so sorting archives by
  `CreationTimeUtc` may not reflect true age/rotation order; `_wakeEvent` is an OS
  handle never disposed. Minor, but "log rotation/retention correctness" was called
  out.
- **Recommendation:** Sort by `LastWriteTimeUtc` (or embed a sortable timestamp,
  which the filename already has) and dispose `_wakeEvent` in `Shutdown`.

### A24 — `Main` log claims staggered pacing that `Main` itself does not implement
- **File:line:** `src/Core/Main.cs:126-133`
- **Description:** The log states "Patrol (T+15s), Stationary (T+45s), Traffic Stops
  (T+75s), Response (T+105s)" but the code calls every feature's `Start()`
  back-to-back. If the stagger is not implemented inside each feature, this violates
  the standards' "never execute immediate, unthrottled spawn bursts" rule.
- **Recommendation:** Verify feature `Start()` staggering (see Open Questions);
  if absent, implement the stagger centrally here.

### A25 — Duplicate comment numbering / minor cleanup in `Main`
- **File:line:** `src/Core/Main.cs:122`, `:125` (both "3.")
- **Description:** Cosmetic; harmless.

---

## Per-file summary

| File | Overall | Notable findings |
|---|---|---|
| `Core/Main.cs` | Risky teardown, silent catches | A1 (teardown fragility), A7 (swallowed catches), A8 (fallback flag confusion), A11 (fiber not joined), A13 (double summary), A24 (stagger claim), A25 |
| `Core/Settings.cs` | Robust parsing, weak numeric validation | A3 (NaN/Infinity), A8 (dual flags), A9 (clamp scope), A16 (unsynchronized statics), A22 (ParseRange) |
| `Core/Logger.cs` | Well-structured but shutdown/queue races | A2 (writer race), A4 (lazy init on fiber), A5 (unbounded queue), A13/A14 (log quality), A23 (retention/wake handle) |
| `Common/InputHelper.cs` | Clean | No functional defects found; native calls wrapped, uppercase, floats/signatures correct. (Only nit: `IsModifierDown(None)=true` is intentional per docs.) |
| `Common/NativeTasks.cs` | Natives correct; some silent fallbacks | A7 (swallowed catches), A15 (anim dict leak on timeout), A19 (duplicate constants) |
| `Common/WorldUtils.cs` | Useful geometry, several fail-open/leak issues | A6 (clearance/fail-open), A10 (Random per call), A15 (model leak), A17/A18/A20/A21 (geometry edge cases) |

---

## Open questions / needs verification

1. **Feature staggering (A24):** Do `PatrolFeature.Start()`, `StationaryFeature.Start()`,
   `TrafficStopFeature.Start()`, and `ResponseFeature.Start()` internally delay
   their first spawn by 15/45/75/105 s? If not, the standards' startup-pacing rule
   is violated in practice. (Feature files were outside this stream's scope.)
2. **Menu mutation of distances:** Does `CadenceMenu` re-run the
   `SpawnDistanceMin < SpawnDistanceMax < DespawnDistance` clamp after a user edits
   those values? If not, A9 is exploitable at runtime.
3. **`SecureEntityGround(freezeTemporarily:true)` callers:** Do all callers invoke
   `ReleaseEntityGround`? A missed release leaves the entity permanently frozen.
4. **Thread affinity:** Is `Functions.OnOnDutyStateChanged` always raised on the
   game/main thread? If it can arrive off-thread, A16 (`_isOnDuty`, Settings.Load)
   needs hardening.
5. **Event handler leak on failed init:** If `Main.Initialize()` throws after
   `Functions.OnOnDutyStateChanged += OnDutyStateChanged`, is the handler ever
   unhooked? (The catch at `Main.cs:41-44` logs only.)
6. **Banned hash sweep:** Confirmed `0xA2719248` absent in this stream; ensure the
   feature/service streams are equally clean.
7. **`Logger.Shutdown` from `ProcessExit`:** Verify no finalizer ordering issue
   when `ProcessExit` fires after `Finally()` already shut the logger down (second
   call returns early at `Logger.cs:112` before clearing `_initialized`).
