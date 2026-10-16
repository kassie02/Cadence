# Cadence — Production Readiness Audit

**Audience:** Cadence maintainers
**Date:** 2026-10-15
**Scope:** Entire repository at `C:\Users\User\Pictures\test-noedit\Cadence` (~15,300 lines of C# across 19 source files, plus build scripts, config, docs)
**Target:** GTA V / LSPDFR / RagePluginHook (x64), .NET Framework 4.8
**Mode:** Read-only audit. **No source, config, or build files were modified.** This document is the only artifact produced.
**Baseline contract:** [`CODING_STANDARDS.md`](../CODING_STANDARDS.md)

---

## 0. How this audit was produced

Six independent passes read every source file in full, cross-checked against `CODING_STANDARDS.md`, and recorded findings with `file:line` pointers. The largest and most crash-prone file (`BackupFeature.cs`, 3,814 lines) was re-audited in two overlapping halves after the first pass proved too shallow. The following facts were independently re-verified during synthesis:

| Claim | Result |
|---|---|
| Banned hash `0xA2719248` present anywhere | **No occurrences** (clean) |
| Raw-hex native calls (`Natives.x...`) | **No occurrences** (clean) |
| A `Settings.Save()` / persistence method exists | **None** — confirms settings are never written back |
| `BlipService.AttachSuspectBlip` implements a fallback | **False** — both overloads `=> null` (`BlipService.cs:325-330`) |
| Pursuit/emergency blips use `alternateFlash: true` | **True** (`BlipService.cs:197,368`) — violates the sprite invariant |
| `BackupFeature.cs` size | 3,814 lines |

Findings are tagged with the pass that raised them (`A`–`F`) for traceability to the raw audit notes. Everything below is a **change recommendation**, ranked by how close it stands to a game crash or a broken release.

---

## 1. Executive summary

**Verdict: not production-ready.** The plugin is feature-rich and, in the happy path, works. But there are multiple *systemic* defects that will produce crashes, ghost entities, or silently-dead features in real sessions, plus release-engineering gaps that make a "0 bugs" guarantee impossible to verify.

Three systemic problems dominate:

1. **Cross-feature ownership is not atomic.** Five features (Backup, Pursuit, Patrol, Stationary, Response, TrafficStop, Assist) each maintain their own list of "owned" cruisers/officers and hand them to each other through non-atomic `Find` → `Reserve` → `Adopt` sequences. Multiple concrete races were found where two features (or two fibers within one feature) end up tasking the same vehicle/officer. This is the root cause behind the project's own "dual ownership of commandeered vehicles" crash history.
2. **LSPDFR pursuit disenrollment is missing on teardown.** The standards mandate `Functions.RemovePedFromPursuit(ofc)` *before* `Tasks.Clear()` / delete. Only the interactive dismissal paths do this; purge, stop, and dispose paths across **every** feature delete/task peds while they may still be enrolled in LSPDFR pursuit AI → the documented `TaskCopChasePedAdv.cs:1515` NullReferenceException, which is exactly the `0xe0434352` crash the team has chased before.
3. **Teardown is fragile and non-cooperative.** Cleanup runs as one sequential try-block (one throw aborts the rest), many fibers are stopped with `GameFiber.Abort()`/`Thread.Abort` (which can interrupt Ultimate Backup/native/model-loading mid-flight), and numerous dispatch/child fibers are never tracked so they re-add entities *after* purge. Result: orphaned "ghost" persistent cruisers/peds/blips surviving unload.

On top of that, the release pipeline is not trustworthy: **the build returns exit 0 even when `Cadence.dll` was not produced**, the required `validate_natives.py` gate is never executed by any automated process (and only checks 184 hardcoded sites), the version string is inconsistent across five artifacts, and there are no tests and no CI.

### Severity counts (canonical, de-duplicated)

| Severity | Count | Meaning |
|---|---|---|
| Critical | ~14 | Crash, corruption, or a released build that cannot work |
| High | ~30 | Data/ownership corruption, standards violation with visible impact, silent feature failure |
| Medium | ~40 | Robustness, leaks, config correctness, performance |
| Low | ~45 | Hygiene, dead code, native style, docs |

---

## 2. Severity definitions

- **Critical** — causes a crash (game or plugin), corrupts engine/LSPDFR/Ultimate Backup state, leaks or hijacks entities persistently, or makes a shipped release non-functional.
- **High** — incorrect behavior with clear user impact, violation of an explicit `CODING_STANDARDS.md` rule, resource leak that accumulates, or a feature that silently stops working under valid input.
- **Medium** — latent bug, race that is hard to hit, configurability/validation gap, or performance churn.
- **Low** — code hygiene, dead code, documentation drift, non-canonical native style.

Each item gives the change to make, not just the problem.

---

## 3. Tier 0 — Release blockers (fix before any public release)

These are the items that, left unfixed, will produce crashes, ghost entities, or a broken artifact. Grouped by theme; the raw finding IDs from the audit passes are kept in brackets.

### 3.1 Cross-feature dual ownership of cruisers and officers [D1, D2, D3, B1-2, B1-3, B2-2, B1-10, C-H1, D16]

The single most important class of bug. Every feature that "commandeers"/"drafts"/"adopts" an ambient unit does it in three non-atomic steps: (a) scan a shared candidate pool, (b) mark it reserved/relinquish from the previous owner, (c) construct a new owner session. Between (a) and (c) another feature/fiber can pick the same unit.

Concrete defects:

- **Snapshot/relinquish race dismisses adopted units [D1/D2/D3].** `UpdateActiveStops` (`TrafficStopFeature.cs:304-326`), `UpdateActiveUnits` (`StationaryFeature.cs:351-425`), and `RunResponseWatchdog` (`ResponseFeature.cs:332-397`) iterate a snapshot taken under lock, then dispose anything whose `IsValid()`/`IsFinished` is false. When another feature relinquishes that same unit, the owner's *stale snapshot* still points at it and disposes the cruiser/officer the other feature just adopted — often one tick after adoption. It may also kill the adopted unit's siren (`TrafficStopFeature.cs:1230`).
  - **Change:** introduce an explicit `Disposed` flag distinct from `IsCompleted`/`IsFinished`. Before disposing a snapshot entry, re-check membership under `_syncLock` (`if (!_activeX.Contains(x)) continue;`). `Relinquish` must leave a tombstone the owner loop honors.
- **Backup reservation is check-then-act (TOCTOU) [B1-3/B2-2].** `FindCandidateAmbientVehicle` checks `_reservedCruisers` under one lock; the caller adds under a second lock (`BackupFeature.cs:1663-1666` → `1569-1571`/`1128-1131`). Every dispatch starts an untracked fiber, so two fibers can select and reserve the same vehicle (`HashSet.Add` return is ignored). Worse, the reservation is released in `finally` *before* the session is added to `_activeUnits` (`937-961`, `1074-1097`), reopening the window.
  - **Change:** one atomic `bool TryReserve(Vehicle)` that checks-and-adds under a single lock; keep the reservation until the session is registered in `_activeUnits` (move `Remove` after `Add`). Reserve inside the finder.
- **Backup can commandeer a dismissing/pursuit cruiser [B1-2, B1-10].** Candidate filter excludes only `State != Dismissing && State != Completed` (`BackupFeature.cs:1665`), so a cruiser still owned by a *dismissing* session (or a pursuit cruiser) can be re-commandeered into a second session. `FindCandidateAmbientVehicle` never calls `PursuitFeature.IsPursuitCruiser(v)` (Assist and Response do), and `ReleaseFromOtherFeatures` (`1814-1829`) omits `PursuitFeature.RelinquishCruiser`.
  - **Change:** exclude every session that references the vehicle (including `Dismissing`); add the `IsPursuitCruiser` screen and the `PursuitFeature.RelinquishCruiser` call.
- **Pursuit does not check Assist ownership [C-H1].** `PursuitFeature.FindCandidateAmbientCruiser` (`930-976`) checks Backup/TrafficStop/Response/Patrol/Stationary but not Assist. Assist checks the others symmetrically. Result: Assist can engage a cruiser while Pursuit drafts it.
  - **Change:** add `AssistFeature.IsActiveCruiser(cruiser)` to the pursuit filter (expose a public API on `AssistFeature`).
- **Response has no reservation at all [D16].** `FindIdleAmbientPoliceVehicle` (`ResponseFeature.cs:743-790`) selects without reserving, and `DispatchOrSpawnCruiser` can run on the response fiber, a callout-takeover fiber, and the UI thread concurrently → two `ResponseUnitInstance`s over one vehicle, two blips, one leaked.
  - **Change:** lock a `HashSet<Vehicle>` reservation before relinquish/setup; release on failure/dispose; or serialize all dispatch through one fiber.

**Strategic fix (recommended):** replace per-feature lists with a single **shared ownership registry** (`TryClaim(Entity, owner)` / `Release(Entity, owner)` / `IsClaimedByOthers(Entity, owner)`). This converts a whole class of race into one atomic operation and removes the need for each feature to know about the others. See §9.

### 3.2 LSPDFR pursuit disenrollment before task clear/delete [C-C1/C2, D6, B1-1, B2-10]

`CODING_STANDARDS.md` §Pursuit 1 requires `Functions.RemovePedFromPursuit(ofc)` **before** `ofc.Tasks.Clear()`; clearing while LSPDFR still owns the ped throws `NullReferenceException` inside LSPDFR (`TaskCopChasePedAdv.cs:1515`). Interactive dismiss paths comply, but teardown paths do not:

- `PursuitFeature.Stop()/PurgeAll()` and `PursuitUnitSession.Dispose` never disenroll (`PursuitFeature.cs:56-88`, `1034-1047`, `1447-1501`).
- `BackupFeature.PurgeAll/Stop/UpdateActiveUnits` bypass `Dismiss()` and call `Dispose()` directly, which deletes peds with no disenrollment (`BackupFeature.cs:137-178`, `450-494`, `3782-3811`).
- `TrafficStopFeature.Dispose/Relinquish` (`1222`, `1286`), `StationaryFeature` (`941`, `1044`, `1070`), `ResponseFeature` (`1012`, `1054`), `PatrolFeature` (`677-753`), `AssistFeature` (`592-636`), and `BackupFeature.EngageThreat/UpdateThreatEngagement` (`3463`, `3512`) all clear tasks/deletes without a guaranteed disenrollment.
- **Change:** create a single shared teardown helper, e.g. `SafeDismiss(Ped ofc)` that does `if (Functions.IsPedInPursuit(ofc)) Functions.RemovePedFromPursuit(ofc);` inside try/catch, then clears/deletes. Route **every** dismissal/dispose path in **every** feature through it. This is the highest-value single refactor for crash reduction.

### 3.3 Teardown fragility, non-cooperative abort, and untracked fibers [A1, A2, B1-8, C-L5, D4, E8, E19]

- **Teardown is one sequential try-block [A1].** `Main.Finally` (`Main.cs:79-86`) and the off-duty path (`167-172`) run purge → stop → menu-stop → logger-shutdown in one `try`. A throw in an early step aborts the rest, leaving fibers alive and entities orphaned, and can skip `Logger.Shutdown()` (losing logs / leaking the writer thread).
  - **Change:** wrap each step in its own try/catch (log-and-continue); add a `_tornDown` guard and one shared `Teardown()` helper.
- **Non-cooperative abort [B1-8, C-L5, D4, E19].** `GameFiber.Abort()` (`BackupFeature.cs:160`, `TrafficStopFeature.cs:64-71`, `StationaryFeature.cs:64-71`, `ResponseFeature.cs:84-91`, `PursuitFeature.cs:66`, `PatrolFeature.cs:62-75`, `AssistFeature.cs:67-80`) and `Thread.Abort` (`CadenceMenu.cs:250-259`) can fire while the fiber is inside `UltimateBackupService.AcquireUbGate()` (a `GameFiber.Yield` loop), a UB API call, a native task, or `Model.LoadAndWait`. Aborting there can corrupt engine/UB state and leave UB's `finally { ReleaseUbGate(); }` running during unwind.
  - **Change:** cooperative cancel (`_isRunning=false`), check the flag after every long yield, join with a bounded wait, and abort only as a last resort. `BackupFeature.Stop` also aborts while holding `_syncLock` — never abort under a lock.
- **Untracked dispatch/child fibers [B1-8, D10, E8, B1-18/B2-18].** In-flight dispatch fibers (`BackupFeature.cs:724, 981, 1028, 2298, 2556, 2704`), Stationary scenario child fibers (`StationaryFeature.cs:836-923`), panic sound fibers (`BackupFeature.cs:2311-2325`), and the player-vehicle monitoring fiber (`BlipService.cs:398-427`) are never stored/cancelled. After purge they can re-add or touch entities.
  - **Change:** track these fibers (or use a cancellation token) and stop them before purge; null them. For `BlipService`, abort/join and null the fiber on `Stop`, and make Start/Stop idempotent under a lock (a fast off→on duty cycle can currently spawn a second monitor fiber).

### 3.4 Blip lifecycle and suspect-blip inviolability [E2, E4, E5, B2-7, B2-15]

- **Pursuit blips use `alternateFlash: true` on police cruisers [E2, B2-12].** Directly contradicts `CODING_STANDARDS.md:87`. `AttachPursuitBlip` (`BlipService.cs:195-198`) and the pursuit branch in `ApplyBlipState` (`:368`) — reached by every drafted pursuit cruiser through `PursuitFeature`/`BackupFeature.TransitionToPursuit`.
  - **Change:** `alternateFlash: false` for police vehicles; if pulsing is wanted, use standard flashing with a police colour and document the exception.
- **Player-vehicle monitor overwrites a native LSPDFR blip [E4].** `UpdatePlayerVehicleBlip` (`BlipService.cs:471-488`) mutates a native handle in place (`SET_BLIP_SPRITE/COLOUR/SCALE`) and never restores it. If the player's `LastVehicle` becomes a pursued/stolen vehicle, Cadence recolours LSPDFR's suspect marker while a pursuit is live — the exact inviolability the standard forbids.
  - **Change:** never mutate native handles; if `GET_BLIP_FROM_ENTITY` returns a blip, skip (own nothing). Only attach/own a Cadence blip when no native blip exists, and delete it on Stop and when native state changes.
- **Suspect fallback is a dead stub [E5, E25].** `AttachSuspectBlip` returns `null` unconditionally (`BlipService.cs:325-330`), so the mandated "emergency fallback when no native blip exists" never fires; tracked suspect blips stay empty. `PursuitFeature.cs:526,530` call it precisely as that fallback.
  - **Change:** implement it (attach a police-appropriate sprite, track by `PoolHandle`, dedupe with `HasAnyActiveRadarMarker`), or delete the method and its call sites so code and standard agree.
- **Radar blips leak when toggled off [B2-7].** `BackupUnitSession.UpdateBlipStates` early-returns when `ShowRadarBlips` is false (`BackupFeature.cs:3642-3647`) without deleting existing `RadarBlip`/foot blips.
  - **Change:** on transition to disabled, delete radar + all foot blips immediately; don't early-return before cleanup.
- **Foot-blip dictionary is unsynchronized [B2-15].** `_officerFootBlips` is touched by the update fiber and by `Dispose`/`ClearFootBlips` during `PurgeAll` (`BackupFeature.cs:2379-2381, 3642-3770, 3800-3812`).
  - **Change:** guard with `_syncLock` (or a dedicated lock) and snapshot keys before enumerating.

### 3.5 Guaranteed spawn: UB detection and strict-fallback trap [E1, B1-13, B2-3, F20]

- **UB is declared "available" from a file-existence check alone [E1].** `UltimateBackupService.Detect()` (`UltimateBackupService.cs:111-121`) returns true if `UltimateBackup.dll` exists on disk, without confirming it loaded or is API-compatible. Because `AllowFallback` defaults to **false**, once `IsAvailable == true` the native fallback is refused everywhere. If UB is present but unusable, **all ambient police spawning silently returns null forever**, and `Main.cs:116` does not warn (it checks `!IsAvailable`).
  - **Change:** set `_isAvailable` only after a live probe (resolve `UltimateBackup.API.Functions` + a method, or successfully return one unit). Treat file existence as "maybe". If `UseUltimateBackup && !AllowFallback` and the probe fails, surface the same warning/pause path as "not installed", and/or auto-downgrade after N consecutive failures.
- **Fallback abort ignores `IsAvailable` [B1-13, B2-3].** Guards like `if (Settings.UseUltimateBackup && !Settings.AllowFallback) return null;` (`BackupFeature.cs:1215-1219, 1266-1270, 1324-1328`; `UltimateBackupService.cs:430-434, 482-486, 530-534`) abort even when UB is not actually available, and even when the user set `UseUltimateBackup=false` (intent: use native).
  - **Change:** condition on the *effective* availability: `UseUltimateBackup && UltimateBackupService.IsAvailable && !AllowFallback`. When `UseUltimateBackup=false`, permit native as the primary path.
- **Two conflicting fallback flags [A8, D15, E23, F14].** `AllowFallback` (default false, `[Integration]`) vs `AllowFallbackSpawning` (default true, `[Backup]`); the latter is parsed but **never read** — a user toggling it sees no effect. Several code paths reference both.
  - **Change:** collapse to one authoritative key (or rename so the two concepts are unmistakable), expose it in the menu, and document which subsystem each controls.
- **Archive docs omit dependencies [F20].** The packaged `README.txt` never states RPH/LSPDFR/RAGENativeUI requirements, and with `Fallback=false` a UB-less install spawns nothing.
  - **Change:** add a Requirements section to the generated README and align with an in-game startup warning.

### 3.6 Settings persistence and validation [E3, A3, A9, F15]

- **The in-game menu never saves [E3].** There is **no `Settings.Save()`** anywhere; every toggle/preset/limit change lives only in memory and is lost on restart or duty cycle. `"[Reload Cadence.ini]"` reloads the file, which the menu never wrote.
  - **Change:** implement `Settings.Save()` that round-trips all keys under their sections (with validation/clamping) and call it (debounced) after menu changes or on duty-off/unload. Distinguish Save vs Reload in the UI. This is a headline feature gap.
- **`ParseFloat` accepts `NaN`/`Infinity` [A3].** `Settings.cs:574-579` uses `NumberStyles.Float`, so `NaN` parses; `Math.Min/Max` propagate it, and the distance invariant check (``if (SpawnDistanceMin >= SpawnDistanceMax)` `) is `false` for `NaN` — a single typo sets a distance to `NaN` forever, silently killing spawn logic.
  - **Change:** reject non-finite input in `ParseFloat`; add a finite `ValidateDistances()`.
- **Distance invariant enforced only inside `Load()` and only when the ini exists [A9].** The clamp at `Settings.cs:317-318` is skipped if the ini is missing (early return `280-284`) or an exception is thrown mid-parse, and `ApplyPreset`/menu edits never re-validate.
  - **Change:** factor `ValidateDistances()` and call it from `Load()`, `ApplyPreset()`, and after any menu mutation; enforce the standards' +50 m margins.
- **Invalid values silently fall back; no config report [F15].** Out-of-range keys are clamped/dropped with no user-visible message.
  - **Change:** track unknown/rejected values and emit one consolidated warning (and log).

### 3.7 Build and release integrity [F1, F2, F3, F4, F11, F30]

- **Build exits 0 without the plugin DLL [F3].** `dist/build.bat:89-99, 151-162` warns if `Cadence.dll` is missing and still reports `[BUILD SUCCESS]`, shipping a zip without the binary.
  - **Change:** missing DLL = `exit /b 1`; assert the archive actually contains `plugins/LSPDFR/Cadence.dll`.
- **Mandated validator is never run and is easily fooled [F4, F26].** `CODING_STANDARDS.md:117` requires `python scripts/validate_natives.py` exit 0, but no build/CI/promote path calls it. It scans only 184 sites and several documented rules are unimplemented (e.g. it flags only the literal `0xA2719248`, not misuse of the correct hash; only one PascalCase native name; raw-hex via other APIs invisible; `rglob` also scans generated `bin`/`obj`).
  - **Change:** wire it into `build.bat` and CI (fail on non-zero), expand the ruleset against the canonical native DB, exclude `bin`/`obj`.
- **Version drift across five artifacts [F2, F17].** `AssemblyInfo.cs` = `1.1.0.0`; `build.bat` hardcodes `1.1.0` in 5 places; READMEs say 1.1.0; `CHANGELOG.md` top entry is `[1.1.9]`; handoff doc says 1.1.1.
  - **Change:** single-source the version in `Directory.Build.props`, feed `build.bat` from it, and add a CI assertion that assembly == changelog top == zip name.
- **`FrameworkPathOverride` hardcodes a machine path to the .NET 4.0 runtime [F11].** `Cadence.csproj:17`. It forces `mscorlib` resolution from `v4.0.30319` instead of the 4.8 reference assemblies and breaks reproducibility.
  - **Change:** remove it; install the .NET Framework 4.8 Targeting/Developer Pack on build agents.
- **Legal/repo: no LICENSE, third-party DLLs committed [F1, F8, F22].** ~8 MB of copyrighted binaries (`src/lib/*.dll`) are tracked with no license or acquisition instructions.
  - **Change:** add a `LICENSE`; `git rm --cached src/lib/*.dll` and gitignore them; add a `lib/README` with download links + expected versions; confirm redistribution rights before any public release.
- **Release integrity unclear [F30].** ~20 files are uncommitted on `dev`; the shipped zip's DLL cannot be tied to a commit.
  - **Change:** commit/tag the frozen release; treat tags as the release source of truth.

---

## 4. Tier 1 — High-priority correctness

Beyond the blockers above:

| ID | File:line | Issue | Change |
|---|---|---|---|
| A4 | `Logger.cs:211-214`, `42-106` | `Logger.Write` lazily runs heavy init (dir create, file moves, `Directory.GetFiles`/deletes, stream open, thread spawn) on whatever thread first logs — possibly a game fiber mid-frame. | Ensure `Logger.Initialize()` runs on the startup path before any feature; make the lazy fallback a no-op/console only. |
| A5 | `Logger.cs:216-228` | Log queue is unbounded for non-DEBUG levels; a stalled worker grows memory until OOM. | Hard-cap all levels (drop oldest + counted warning) or use a bounded collection. |
| A6 | `WorldUtils.cs:217-261` | `IsAreaClear` counts the player and Cadence's own units (self-blocking spawns) and **fails open** on exception (`catch { return true; }`). | Exclude owned handles/player; fail closed on exception. |
| A7 | `Main.cs:62,77`; `NativeTasks.cs:83-87,124-128,258`; `WorldUtils.cs:257-260,445-448` | Swallowed catches mask bad native calls/signature failures as silent misbehavior. | Log `Warn`/`Debug` with the exception. |
| A11/E8 | `BlipService.cs:398-427` (from `Main.cs:80`) | Player-vehicle fiber is signalled but never joined/aborted/nulled; can touch deleted entities during teardown; restart can spawn a second monitor. | Track, abort/join, null; exit-check flag after `Sleep`; make Start/Stop idempotent. |
| A12 | `Main.cs:114`; `UltimateBackupService.cs:134-140` | `UltimateBackupService` has no shutdown/reset; `_initialized` and reflection caches persist across reloads. | Add `Shutdown()` clearing caches/`_initialized`; call from `Main.Finally`. |
| A14 | `Logger.cs:194-206` | Every log line is mirrored to `Game.LogTrivial`; with debug default true this floods `RagePluginHook.log`. | Mirror only WARN/ERROR (or add an `EchoToRphLog` toggle). |
| A15 | `NativeTasks.cs:319-330`; `WorldUtils.cs:367-391` | Anim dicts/models leaked on load timeout (no `RemoveAnimDict`/`Unload`). | Release on the timeout path (best-effort + log). |
| B1-4 | `BackupFeature.cs:930-941,1067-1103,1576-1589,2285-2289` | Spawn→register gap leaks persistent cruisers/peds/blips when a session fails `IsValid()` or `ConfigureBackupSession` throws after `MakePersistent`. | Register inside the spawn step; dispose failures; wrap configure in try/finally. |
| B1-5/B1-6 | `BackupFeature.cs:1669-1709,1744` | EMS/Fire requests accept any `IsEmergencyVehicle` (incl. police) as ambulance/fire, while the prisoner heuristic rejects 2-person EMS/Fire crews. | Whitelist ambulance/fire models + crew; apply the prisoner heuristic only to police candidates. |
| B1-7/B2-4 | `BackupFeature.cs:750,805,850-912,1006-1014,1050` | 17 inline scanner-audio strings, some unverified (`"AMBULANCE ASSISTANCE_REQUIRED_01 ..."`), called *before* spawn in the same try — an unknown name throws and cancels the dispatch, or throws on LSPDFR's thread. | Centralize validated constants, wrap each audio call in a local try/catch, and dispatch independently of audio. |
| B1-9 | `BackupFeature.cs:741,797,745-747,801-803` | Dead `activePolice` counters; panic/group math forces ≥`PanicButtonUnitsMin`/≥1 even at capacity; no global active cap; 2.5 s debounce allows spam. | Enforce a real global cap using the active count; make capacity authoritative; cap totals. |
| B1-14 | `BackupFeature.cs:436-476` | No per-unit try/catch in `UpdateActiveUnits`; one bad unit aborts the tick and skips cleanup. | Isolate each unit update; remove on exception. |
| C-H3/H4 | `PatrolFeature.cs:677-753`; `AssistFeature.cs:592-636` | Dispose/relinquish clear tasks without disenrollment (see §3.2). | Route through the shared `SafeDismiss` helper. |
| D5 | `TrafficStopFeature.cs:815-848,880-936` | Traffic-stop dismount ignores the 6.5 m active-lane rule that Stationary enforces → officers stand in live traffic. | Compute lane distance at spawn; keep officer in vehicle (or reject spot) when within 6.5 m and no shoulder. |
| D7 | `ResponseFeature.cs:699-741` | `SetupResponseUnit` leaks persistent cruiser/peds when it throws after spawning. | Dispose entities in the catch (mirror `CreateTrafficStopAt`). |
| D8 | `ResponseFeature.cs:440-462` | Autonomous pacing timestamp advances only on success → 1.2 s retry churn when UB unavailable. | Set the timestamp right after passing the cooldown check. |
| D9 | `ResponseFeature.cs:475-497` | With all service types disabled, autonomous runs still dispatch Police. | Abort when `candidateServices` empty; gate police on `AllowPoliceRuns`. |
| E6 | `CadenceMenu.cs:830-851,673-726` | Test-spawn menu items run spawn work *on the menu watchdog fiber* and yield inside `ProcessMenus` via `AcquireUbGate` (8 s) / `SafeLoad` (4 s ×2) → menu hard-freezes up to ~8-16 s. | Dispatch spawns on their own fiber (as `RequestBackup` already does); guard double-dispatch. |
| E7 | `BlipService.cs:299-319` | `HandleOfficerRemount` ignores its `stateLabel`/`stateColor` and always makes a PATROL blip. | Honor the params (route through `ApplyBlipState`) or drop them. |
| E13 | `UltimateBackupService.cs:347-356` | UB peds leaked when the returned vehicle is invalid; no `IsPoliceVehicle` verification for police spawns. | Clean up `Item2` on the invalid branch; validate `IsPoliceVehicle`. |
| F5 | `README.md:159-162`; `docs/README.md:156-159` | Keybind keys documented under `[Backup]` but the parser reads `[Keybinds]` → documented edits silently ignored. | Fix docs (or accept both sections). |
| F6/F7/F10 | repo | No CI, no tests, no `.editorconfig`/analyzers. | See §9. |
| F8/F9/F12/F13 | repo | Duplicated/contradictory docs; shipping `Debug=true` + `Chaos` preset while docs claim `Realistic`; `Debug`/preset defaults mismatch code. | Reconcile to one canonical README + one future-features doc; pick production defaults consistently. |
| F19/F25 | `docs/cadence_workspace_handoff_2026-10-12.md` | Committed scratch doc leaks personal machine paths and references a deploy script absent from the repo. | Remove/relocate; provide a portable, parameterized deploy script. |

---

## 5. Tier 2 — Medium

Grouped; each is a genuine change to make but lower blast radius.

**Concurrency / shared state**
- `_syncLock` held across the entire `Reengage` call (`BackupFeature.cs:1488-1541`) — snapshot under lock, call outside [B1-11/B2-1].
- `BackupUnitSession.State` mutated from three fibers (LSPDFR callout event, dispatch fiber, update fiber) without sync (`BackupFeature.cs:270-293,457,764-769,1536-1540`) [B1-12].
- Static `Random` accessed from multiple fibers, unsynchronized: `BackupFeature.cs:53`, `PursuitFeature.cs:26`, `PatrolFeature.cs:23`, `AssistFeature.cs:30`, `ZoneSpawnData.cs:10`, and `new Random()` per call in `WorldUtils.cs:348` [A10, D25, E9]. Use a locked/thread-local generator.
- Non-atomic `_callsignCounter++` across fibers/menu thread wraps to duplicate callsigns (`TrafficStopFeature.cs:695-701`, `StationaryFeature.cs:666-672`, `ResponseFeature.cs:851-857`, `BackupFeature.cs:2357-2363`) [D19]. Use `Interlocked` + modulo.
- Non-volatile cross-fiber flags/`_state` (`BackupFeature.cs:56-60`; `StationaryFeature.cs:839-917`) [B1-26, D26].
- Mutable static `Settings` and `_isOnDuty` read/written across fibers/UI without barriers (`Settings.cs`, `Main.cs:18`) [A16]. Document the threading contract or version the snapshot.

**Geometry / spawning**
- Dynamic spawns never exclude freeways (`TrafficStopFeature.cs:438-459`, `StationaryFeature.cs:501-522`) [D12]; curated Stationary spots skip the rooftop/elevated check that TrafficStop applies (`StationaryFeature.cs:480-499`) [D13].
- `ZoneSpawnData` freeway pullover arrays need lane-distance verification: `BlaineSandyGrapeseedSenoraFwy`, `BlainePaletoGreatOceanHwy`, `LSNEHighway` (`ZoneSpawnData.cs:16-74`) [E11].
- 16 duplicate coordinate groups + cross-list double counting over-weight some standby spots and allow two units on the exact same coordinate (`ZoneSpawnData.cs`) [E10]. De-dup by rounded XYZ and reserve claimed spots.
- Curated standby path only calls `IsAreaClear`; add `IsRooftopOrElevatedStructure`/lane checks [E-open6].
- `IsRooftopOrElevatedStructure` may reject valid underpasses (`WorldUtils.cs:438`) [A20]; `GetAccurateGroundZ` reports success with an unvalidated Z (`WorldUtils.cs:143`) [A21].

**Lifecycle / leaks**
- Stationary scenario child fibers untracked (`StationaryFeature.cs:836-923`) [D10]; absolute station-keeping timeout missing → immortal units (`TrafficStopFeature.cs:1022-1063`, `StationaryFeature.cs:785-819`) [D11].
- Double session-summary logging (`Main.cs:165` + `Logger.cs:130`) [A13].
- `MenuPool`/menus not disposed on Stop (`CadenceMenu.cs:241-264`) [E20]; `_wakeEvent` never disposed (`Logger.cs:25`) [A23].
- Models/dicts leaked on post-load exceptions in UB spawners (`UltimateBackupService.cs:451-471,503-519,551-567`) [E21].
- `BackupFeature` reflection `GetCalloutFromHandle` aborts on first throwing property; returns null silently (`1415-1440`) [B1-16]; `ResponseFeature.ExtractCalloutPosition` reflection is fragile (`255-289`) [D24].
- UB partial success leaks the UB vehicle (`BackupFeature.cs:1195-1227,1246-1278`) [B1-17].

**Config / API contracts**
- `PurgeAll` ignores the `immediateDelete` contract (`BackupFeature.cs:171-178`; reflection dispatch in `CadenceMenu.cs:295-302`) [B1-18].
- Menu/test-spawn reflection-by-string dispatch with swallowed failures (`CadenceMenu.cs:270-400`) [E12]; replace with an `IFeature` registry.
- `Settings.Load` doesn't reset to defaults before parsing → removed keys persist stale values (`Settings.cs:275-334`) [E28]; logger toggles applied after preset parsing (`Settings.cs:323-325`) [E27].
- `SET_PED_PROP_INDEX` called with 6 args in `EupService.cs:96` — verify arity against the shipped RPH build [E15]; EUP magic IDs unvalidated and `ApplyBodyArmor`/`ApplyTrafficVest` bypass `EnableEupOutfits` [`E26`].
- `Settings.AllowFallbackSpawning` is dead (see §3.5).
- `ResponseFeature` watchdog lacks a serialized dispatcher (see §3.1) [D16].

**Robustness**
- Many empty `catch {}` blocks hide failures across `TrafficStopFeature`, `StationaryFeature`, `ResponseFeature`, `BlipService`, `CadenceMenu`, `UltimateBackupService` [D17, E14, B1-24].
- No per-unit exception isolation in Backup update loop (see Tier 1) [B1-14].

---

## 6. Tier 3 — Low / hygiene

- **Blip protocol order** — `SafeDeleteBlip` issues `SET_BLIP_FLASHES_ALTERNATE` before `SET_BLIP_FLASHES`, opposite to the documented order; all required calls are present (`BlipService.cs:29-30`) [E16].
- **EMS/Fire blip sprite** — ambulances/fire trucks get the police sprite (56) via `AttachEmergencyBlip` (`BlipService.cs:185-188`; `ResponseFeature.cs:726-729`) [E17]. Add EMS/Fire sprites.
- **Siren shutdown inconsistency** — `StationaryFeature.Dispose` omits `SET_VEHICLE_HAS_MUTED_SIRENS` (`1078-1084`) [D20]; `TrafficStopFeature.Update` re-issues the full silent-siren native every 500 ms (chirp risk) (`803-809`) [D21]. Route through `NativeTasks`.
- **Native float literals** — `-1`/`0` passed where floats expected in nav fallbacks (`TrafficStopFeature.cs:1132`, `StationaryFeature.cs:970`) [D22].
- **Dead code** — unused `WeaponPistol` (`AssistFeature.cs:25`), `WeaponCarbineRifle` (`BackupFeature.cs:30`), `GetAllZones` (`ZoneSpawnData.cs:269-274`) [C-L4, E24, B2-16]; duplicate `AttachSuspectBlip` overloads [E25].
- **`GetSafeStreetPosition` `nthNode` unused** (`WorldUtils.cs:151`) [A17]; duplicate driving-flag constants (`NativeTasks.cs:17,46`) [A19].
- **`ParseRange` tokenizer** chops `"1.5"` (`Settings.cs:588`) [A22].
- **Contributor docs** — no `CONTRIBUTING`/`SECURITY`/templates [F29]; `.gitignore` gaps [F24].
- **README claims C# 10 while `LangVersion=latest`** (`Cadence.csproj:15`) [F27].
- **Stale comments / double comment numbers** (`Main.cs:122,125`; `BackupFeature.cs:1563`) [A25, B1-25].
- **CHANGELOG contradictions** — references `scripts\build.bat` (absent), stale colours, stale ability values, and mixed versions [F23].
- **`IsSubterraneanOrVoid` non-finite check** only tests Z for infinity (`WorldUtils.cs:17-19`) [A18].

---

## 7. Recommended refactors that eliminate whole bug classes

These are structural changes that each resolve many of the findings above at once. They double as the "pit of success" for future work.

1. **Shared ownership registry.** A single `UnitRegistry` with atomic `TryClaim(Entity, owner)`, `Release(Entity, owner)`, `IsClaimedBy(Entity, owner)`, and `IsAvailable(Entity)`. Every feature consults it instead of cross-calling each other's `IsActiveCruiser`/`RelinquishCruiser`/`IsBackupCruiser`. Eliminates §3.1 entirely and makes "No Workarounds / strict validation" enforceable.
2. **`SafeDismiss(Ped)` teardown helper.** Enforces `IsPedInPursuit → RemovePedFromPursuit → Tasks.Clear/Delete` in one place. Eliminates §3.2 across all features.
3. **`IFeature` registry interface.** `{ void Start(); void Stop(); void PurgeAll(bool immediateDelete); int GetActiveCount(); }`. Replace the reflection-by-string dispatch in `CadenceMenu` (E12, B1-18). Teardown becomes compile-checked.
4. **Cancellation token / tracked fibers.** One helper that owns the feature fiber plus its children, supports cooperative cancel + bounded join, and never uses `Abort` under a lock. Eliminates §3.3.
5. **`SafePlayScannerAudio(...)` helper.** Validated names, local try/catch, never blocks dispatch. Eliminates B1-7/B2-4.
6. **Single settings snapshot + `Settings.Save()` + `Settings.Validate()`.** Eliminates E3, A3, A9, F15 and the A16 race.
7. **`NativeTasks`/`BlipService` shutdown helpers.** Every feature uses the same siren/blip shutdown path (D20, D21, E16, E17).

---

## 8. Environment improvements ("pit of success")

The audit's second goal is to make the *next* change safer than the last. Recommended, in priority order:

1. **CI (`.github/workflows/build.yml`).** restore toolchain → `msbuild Release|x64 /p:TreatWarningsAsErrors=true` → `python scripts/validate_natives.py` → package → upload artifact; fail on any non-zero step. Closes F4/F6.
2. **Wire the validator into `build.bat`** (fail on non-zero) and fix its blind spots (F4/F26): verify the *correct* hashes are used, scan all native-invocation styles, implement the lock-across-yield heuristic, and exclude `bin`/`obj`.
3. **Tests (`tests/Cadence.Tests`, net48).** Cover `Settings.ParseEntry/ParseKey/ParseRange/ApplyPreset`, a `Load(tempIni)` matrix, the distance invariant, and a packaging test asserting the zip contains `plugins/LSPDFR/Cadence.dll`, `Cadence.ini`, and `Audio/PANIC_BUTTON.wav`. Closes F7.
4. **`.editorconfig` + `Directory.Build.props` with `EnableNETAnalyzers`/`AnalysisLevel`/`EnforceCodeStyleInBuild`** and a curated ruleset; enforce in CI. Closes F10.
5. **Single-source version** in `Directory.Build.props`; drive `build.bat` from it; CI asserts assembly == changelog top == zip name. Closes F2/F17.
6. **Harden packaging**: missing DLL is a hard failure; assert archive contents; add a Requirements block to the generated README; derive the filename from version. Closes F3/F20.
7. **Dependency governance**: remove committed DLLs from VCS, add `LICENSE` + `NOTICE`, record exact versions/hashes in `lib/README`. Closes F1/F8/F22.
8. **Logging as a data source**: keep a `[Logging]` section (`MaxArchiveSessions`, `MaxLogSizeMB`, `ArchiveLogs`) so field logs are bounded and triageable; mirror only WARN/ERROR to `RagePluginHook.log`. Closes F16/A14.
9. **Portable deploy script** parameterized by GTA path (with user-ini backup) so deployment is reproducible and the personal handoff scratch doc can be removed. Closes F18/F19/F25.

---

## 9. Verified facts and dismissed suspicions

- **Banned hash `0xA2719248`**: absent from `src/`. **No raw-hex native calls.** Compliant.
- **`geStatePatrolPed` (sic)**: not a typo — it matches the shipped `UltimateBackup.dll` (v1.8.7.1) API. Dismissed.
- **RAGENativeUI programmatic `Index`/`Checked` setters**: do not raise `OnListChange`/`OnCheckboxChange` in v1.9.3, so `SyncSettingsMenuValues`/`SyncTogglesFromSettings` are re-entrancy-safe. Dismissed.
- **`IsModifierDown(None) == true`**: intentional per `InputHelper` docs. Dismissed.
- **Interactive dismissal ordering** (`PursuitFeature.DismissFromScene`/`BeginStandDown`, `BackupFeature.Dismiss`): correctly call `RemovePedFromPursuit` before clearing. The defect is only in teardown/purge/dispose paths and a few engage paths.
- **`BlipService.SafeDeleteBlip`**: by-ref, existence-checked, resets flash interval/timer, `StopFlashing`, deletes, nulls in `finally`. Only the FLASHES/ALTERNATE order deviates.

---

## 10. Open questions requiring runtime / in-game verification

These cannot be settled by reading alone and should be tested before declaring 0 bugs:

1. **Scanner-audio names.** Which of `"ASSISTANCE_REQUIRED IN_OR_ON_POSITION"`, `"AMBULANCE ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION"`, `"FIRETRUCK ASSISTANCE_REQUIRED_01 IN_OR_ON_POSITION"` are valid for the target LSPDFR build, and which throw on LSPDFR's audio thread?
2. **`SET_PED_PROP_INDEX` arity** against the exact `lib/RagePluginHook.dll` referenced by the csproj (reflection of the reference assembly has no RVA).
3. **AccessViolation handling.** Whether `[HandleProcessCorruptedStateExceptions]` catches anything in this RPH host, or an AV in UB/native code hard-terminates `GTA5.exe`. This is the residual "can crash the game" risk.
4. **Zone coordinate validity.** Do the freeway pullover and certain standby coordinates fall on shoulders/turnouts, or in live lanes/on rooftops/inside geometry? Needs the lane-distance check applied to all curated data.
5. **Cross-feature race reachability.** Confirm the D1/D2/D3 window with a stress test (Backup + Pursuit + Response + TrafficStop active simultaneously).
6. **Does the shipped `dist/Cadence_v1.1.0.zip` DLL match current `src/`?** Requires a controlled build + hash comparison.
7. **Intended production defaults**: is `Debug=true` + `Preset=Chaos` deliberate, or should a release ship `Debug=false` + `Realistic`?

---

## 11. Suggested remediation order (roadmap)

**Milestone 1 — Stop the crashes (highest value, lowest risk)**
1. Shared `SafeDismiss(Ped)` helper + route all teardown/dispose paths through it (§3.2).
2. Atomic `TryReserve` + keep reservation until session registered; exclude dismissing/pursuit cruisers (§3.1).
3. Snapshot-under-lock membership re-check before dispose in TrafficStop/Stationary/Response (§3.1).
4. Replace `Abort()` with cooperative cancel for all feature fibers; never abort under a lock (§3.3).
5. Harden scanner audio (validated constants + local try/catch + dispatch independent of audio) (§3.5/B1-7).

**Milestone 2 — Make it correct**
6. Blip lifecycle: pursuit `alternateFlash:false`, stop overwriting native blips, implement or delete `AttachSuspectBlip`, clean up on `ShowRadarBlips=false` (§3.4).
7. UB live probe + effective-availability fallback guard + collapse the duplicate fallback flag (§3.5).
8. `Settings.Save()` + `Settings.Validate()` + NaN rejection + distance invariant everywhere (§3.6).
9. Response/TrafficStop/Backup HIGH items in Tier 1 (D5, D7, D8, D9, B1-4, B1-5/6, B1-9, B1-14).

**Milestone 3 — Make it durable**
10. Shared ownership registry + `IFeature` registry + tracked-fiber helper (§7).
11. Teardown isolation (per-step try/catch) + Logger shutdown race + unbounded queue + lazy-init fix (§3.3, A2, A4, A5).
12. Per-unit exception isolation; remove dead code; thread-safe RNG/counters.

**Milestone 4 — Make it releaseable**
13. Build hard-fails without DLL; wire validator; version single-source; remove `FrameworkPathOverride` (§3.7).
14. Add CI + tests + `.editorconfig`/analyzers (§8).
15. Docs hygiene: one README, one future-features, reconcile defaults/version, remove handoff scratch, add LICENSE + lib/README (§3.7, F9, F12, F13, F24, F25).

---

## Appendix A — Findings index by source file

| File | Key finding IDs |
|---|---|
| `Core/Main.cs` | A1, A7, A11, A12, A13, A24 |
| `Core/Settings.cs` | A3, A8, A9, A16, A22, E27, E28 |
| `Core/Logger.cs` | A2, A4, A5, A13, A14, A23 |
| `Common/InputHelper.cs` | (clean) |
| `Common/NativeTasks.cs` | A7, A15, A19 |
| `Common/WorldUtils.cs` | A6, A10, A15, A17, A18, A20, A21, D18, D25, E9 |
| `Features/BackupFeature.cs` | B1-1..B1-28, B2-1..B2-21 |
| `Features/PursuitFeature.cs` | C-C1, C-C2, C-H1, C-H2, C-M1..M7 |
| `Features/PatrolFeature.cs` | C-H3, C-L1, C-M6 |
| `Features/AssistFeature.cs` | C-H4, C-M8 |
| `Features/TrafficStopFeature.cs` | D1, D5, D6, D11, D12, D14, D19..D22, D27 |
| `Features/StationaryFeature.cs` | D2, D10, D11, D13, D14, D19, D20, D23, D26, D28 |
| `Features/ResponseFeature.cs` | D3, D4, D6, D7, D8, D9, D14, D16, D24 |
| `UI/CadenceMenu.cs` | E3, E6, E12, E14, E19, E20 |
| `Services/BlipService.cs` | E2, E4, E5, E7, E8, E16, E17, E25 |
| `Services/UltimateBackupService.cs` | E1, E13, E21, E22 |
| `Services/EupService.cs` | E15, E26 |
| `Data/ZoneSpawnData.cs` | E9, E10, E11, E18, E24 |
| Build/config/docs | F1..F30 |

## Appendix B — Raw audit notes

The detailed per-finding notes (with full descriptions, rationale, and recommendations) are available on request. They were produced in a temporary audit directory during this read-only pass and are summarized in this document.

---

*No source, configuration, or build files were modified during this audit. This document is the only change produced.*
