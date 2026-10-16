# Cadence Audit — Group E: UI / Services / Data

Read-only audit of the Cadence GTA V / LSPDFR plugin. Baseline: `CODING_STANDARDS.md` (Blip Lifecycle & Resource Management; Native Invocation; Concurrency & Fiber Safety; Lifecycle & Teardown; No-Workarounds).

## Files covered
- `src/UI/CadenceMenu.cs` (853 lines; prompt estimated 774)
- `src/Services/BlipService.cs` (608 lines; prompt estimated 534)
- `src/Services/UltimateBackupService.cs` (576 lines; prompt estimated 514)
- `src/Services/EupService.cs` (139 lines; prompt estimated 127)
- `src/Data/ZoneSpawnData.cs` (454 lines; prompt estimated 405)

Supporting files read to ground findings: `CODING_STANDARDS.md`, `src/Core/Settings.cs`, `src/Core/Main.cs`, `src/Common/WorldUtils.cs`, `src/Common/InputHelper.cs`, `src/Cadence.csproj`, `src/Cadence.ini`, `scripts/validate_natives.py`, plus targeted reads of `PursuitFeature.cs` / `ResponseFeature.cs`.

Verification performed (read-only):
- `python scripts/validate_natives.py` → **PASS, 0 violations** (184 native call sites).
- Reflected `lib/UltimateBackup.dll` (v1.8.7.1): confirms methods `getLocalPatrolUnit`, `getStatePatrolUnit`, `geStatePatrolPed` (sic, real UB API name), `getLocalPatrolPed`, `getAmbulanceUnit`, `getFireTruckUnit`, `isUltimateBackupCop` all exist. So the "typo" `geStatePatrolPed` is NOT a bug — it matches the shipped DLL.
- Reflected `lib/RAGENativeUI.dll` (v1.9.3) and pulled upstream source: `UIMenuListItem.Index` and `UIMenuCheckboxItem.Checked` setters do **not** raise `UIMenu.OnListChange` / `OnCheckboxChange` (those fire only from user input). Programmatic sync in `SyncSettingsMenuValues()` / `SyncTogglesFromSettings()` is therefore re-entrancy-safe. Not reported as a bug.

---

## Critical

### E1 — Ultimate Backup is considered "available" from a file-existence check alone; with strict fallback this can permanently brick ambient police spawning
- **Severity:** Critical
- **Category:** UltimateBackup API usage / fallback strictness
- **File:line:** `src/Services/UltimateBackupService.cs:111-121` (`Detect` Check 3), `:44-52` (`IsAvailable`), `:149-151`/`:177-179` (spawn gate), `src/Core/Main.cs:116-120`
- **Description:** `Detect()` returns `true` merely because `plugins/LSPDFR/UltimateBackup.dll` exists on disk. It never confirms the DLL actually loaded or that the API version is compatible. `Settings.AllowFallback` defaults to `false` (Cadence.ini `Fallback=false`), so once `IsAvailable == true`, `SpawnNativePatrolUnit` and friends reject the native fallback (`UltimateBackupService.cs:430-434`, `:482-486`, `:530-534`). If the installed UltimateBackup is present but fails to load / is an incompatible version / its API throws at JIT time, every UB request returns `null` (caught + logged) and every native fallback is refused → all ambient police spawning silently returns `null` forever. The startup warning in `Main.cs:116` checks `!UltimateBackupService.IsAvailable`, which is `true`, so the user gets no warning.
- **Why it matters:** The plugin's primary function (ambient police presence) can be completely dead on a machine where UB is "installed" but unusable, with no user-visible cause. This is a hard functional regression, not a cosmetic one.
- **Recommendation:** Only set `_isAvailable` after a successful live probe (e.g., resolve the `UltimateBackup.API.Functions` type and `getLocalPatrolUnit` method, or successfully return a unit once). Treat file existence as "maybe" and, if `UseUltimateBackup && !AllowFallback` and the live probe fails, surface the same warning/pause path as `!IsAvailable`. Consider auto-degrading to fallback (or emitting a loud notification) after N consecutive UB failures.

---

## High

### E2 — Pursuit blip uses `alternateFlash: true` on police cruisers, directly violating the Police Vehicle Sprite Invariant
- **Severity:** High
- **Category:** Blip lifecycle correctness / deviation from CODING_STANDARDS
- **File:line:** `src/Services/BlipService.cs:195-198` (`AttachPursuitBlip`, esp. `alternateFlash: true`), `:366-369` (`TransitionToPursuit`), `:568-571` (alternate branch in `ApplyBlipState`); callers `src/Features/PursuitFeature.cs:1015,1129,1193`
- **Description:** `CODING_STANDARDS.md:87` states: *"Never use Sprite 1 (destination/waypoint circle) or `alternateFlash: true` on police vehicles. ... cause cruisers to look like red suspect markers during alternate pulses."* `AttachPursuitBlip` sets `alternateFlash: true` (PursuitFeature routes all drafted pursuit cruisers through it).
- **Why it matters:** Explicit standards violation. Alternate-flash police markers overlap/complement LSPDFR's native pursuit markers and pulse red, which the standard explicitly calls out as confused-with-suspect visual noise.
- **Recommendation:** Set `alternateFlash: false` for pursuit cruisers, or add a dedicated (non-police) marker type. If red/blue pulsing is desired, use `SET_BLIP_FLASHES` with a police-appropriate color and document an explicit exception in CODING_STANDARDS.

### E3 — Settings edited in the in-game menu are never persisted to `Cadence.ini`
- **Severity:** High
- **Category:** Settings persistence
- **File:line:** `src/Core/Settings.cs` (only `Load()` at `:275`; **no `Save()` exists anywhere**); menu mutations at `src/UI/CadenceMenu.cs:542-671` and reload-only `:738-744`
- **Description:** Every toggle, preset, limit, emergency-variety, highway-ratio, and fallback change is applied to the static `Settings` object in memory only. There is no serializer and no write-back to `Cadence.ini`. `"[Reload Cadence.ini]"` calls `Settings.Load()` (line 740), but there is nothing to reload from except the file the user hand-edited.
- **Why it matters:** A "premium in-game settings menu" whose changes vanish on restart (and are discarded the moment the player toggles duty or reloads). The menu is effectively read-only in practice. This is a headline feature gap for a production release.
- **Recommendation:** Implement `Settings.Save()` (round-trip all keys under their sections, with validation/clamping) and call it after menu changes (debounced) or on duty-off/plugin unload. Add a "Save" vs "Reload" distinction in the UI.

### E4 — Player personal-vehicle blip overwrites a native LSPDFR blip and never restores it
- **Severity:** High
- **Category:** Blip lifecycle / Suspect Blip Inviolability
- **File:line:** `src/Services/BlipService.cs:471-488` (native-handle mutation), fiber at `:393-427`
- **Description:** `UpdatePlayerVehicleBlip()` calls `GET_BLIP_FROM_ENTITY`; if a native blip exists on the player's last vehicle it mutates that handle in place (`SET_BLIP_SPRITE`, `SET_BLIP_COLOUR`, `SET_BLIP_SCALE`, `SET_BLIP_AS_SHORT_RANGE`, flash resets) rather than leaving it alone. It runs on a 300 ms fiber and never restores the original sprite/colour/scale when the plugin unloads or the player re-enters the vehicle. `CODING_STANDARDS.md:88-91` mandates that Cadence must not overwrite/delete blips managed by LSPDFR and must query native blips only to *detect* them.
- **Why it matters:** Cadence permanently hijacks whatever native blip LSPDFR owns on that vehicle. If the player's `LastVehicle` becomes a pursued/stolen vehicle, Cadence recolors LSPDFR's red suspect marker into a purple personal-cruiser marker while the pursuit is live — exactly the inviolability the standard forbids. On unload the marker is left in Cadence's style.
- **Recommendation:** Do not mutate native handles. If a native blip exists, skip entirely (own nothing); only attach/own a Cadence blip when no native marker exists. If a Cadence blip was created, delete it on Stop/unload and when native state changes.

### E5 — `AttachSuspectBlip` is a dead stub that always returns `null`, disabling the required suspect-blip emergency fallback
- **Severity:** High
- **Category:** Blip lifecycle / dead code / deviation from CODING_STANDARDS
- **File:line:** `src/Services/BlipService.cs:325-330`; caller `src/Features/PursuitFeature.cs:526,530`
- **Description:** Both overloads unconditionally `return null`. `CODING_STANDARDS.md:91` requires: *"Cadence suspect watchdogs must strictly serve as emergency fallbacks if no native blip exists."* `PursuitFeature.UpdateSuspectRadarMarkers` calls it precisely as that fallback after confirming `HasAnyActiveRadarMarker` is false; because it always returns null, it can never fall back, and `_trackedSuspectBlips` stays empty.
- **Why it matters:** When LSPDFR (or vanilla) has not placed a suspect marker, no Cadence fallback is created — fleeing suspects can be invisible on the radar in the exact situation the watchdog exists for. The dead stub also masks any future intent/bug.
- **Recommendation:** Implement the fallback (attach a sprite (e.g. vanilla `BlipSprite` suspect / police-appropriate), track by `PoolHandle`, dedupe via `HasAnyActiveRadarMarker`). If it is intentionally disabled forever, remove the method and the dead call sites so the standard and code agree.

### E6 — Several test-spawn menu items yield the watchdog fiber *inside* `ProcessMenus`, stalling the menu for up to ~4-8s (CONFIRMED active)
- **Severity:** High
- **Category:** UI thread / fiber safety, UI stall
- **File:line:** `src/UI/CadenceMenu.cs:830-851` (`MenuFiberLoop` → `Process` → `_menuPool.ProcessMenus():181`), event handlers `:673-726`; yielding callees: `UltimateBackupService.cs:28` (`AcquireUbGate` → `GameFiber.Yield`), `WorldUtils.cs:375-383` (`SafeLoad` → `GameFiber.Yield`, 4s timeout), plus `AcquireUbGate`'s 8s wait
- **Description:** `MenuFiberLoop` (the menu watchdog fiber) calls `Process()` → `_menuPool.ProcessMenus()`. `ProcessMenus` synchronously raises `_testingMenu.OnItemSelect` → `InvokeFeatureSpawn` (`:363-382`) → the feature's static spawn method **on the same watchdog fiber**. Traced call chains that **do yield**:
  - `SpawnTestUnit` → `PatrolFeature.CreatePatrolUnitAt` → either `UltimateBackupService.SpawnLocalPatrol`/`SpawnStatePatrol` (→ `AcquireUbGate()` at `:155`/`:183`, which `GameFiber.Yield()`s at `UltimateBackupService.cs:28`, up to 8 s) **or** `SpawnNativePatrolUnit` → `SafeLoad` (`WorldUtils.cs:377`, up to 4 s × 2 models).
  - `SpawnTestStop` → `TrafficStopFeature.CreateTrafficStopAt` → `SafeLoad` (civilian vehicle/ped models).
  - `SpawnTestRadarUnit`/`SpawnTestChillingUnit` → `StationaryFeature.CreateStationaryUnitAt` → `SafeLoad` (`StationaryFeature.cs:980`).
  - `TriggerTestRun` → `ResponseFeature.DispatchOrSpawnCruiser` → `SpawnAmbulanceUnit`/`SpawnFireUnit` (`SafeLoad`) or a UB unit (→ gate `Yield`).
  These run **inline**, so the watchdog fiber is suspended mid-`ProcessMenus()` for the duration of model streaming / gate wait. **Verified NOT affected:** `TriggerPanicButton`/`TriggerGroupBackup` (`BackupFeature.cs:242`/`:256` → `RequestBackup`, body wrapped in `GameFiber.StartNew` at `:724`) and `TriggerCalloutResponse` (`:260` → `RequestCalloutBackup`, wrapped at `:981`) hand off to a new fiber without yielding on the caller. `PurgeAllFeatures`/`StopAllFeatures`/`Settings.Load` are non-yielding.
- **Why it matters:** While suspended, `ProcessMenus` never returns, so the Cadence menu does not draw or accept input and telemetry stops; worst case (UB gate 8 s + two 4 s loads) the menu appears hard-frozen for ~8-16 s on a single spawn click. Suspending RAGENativeUI mid-iteration also violates the project's UI/fiber discipline and risks partially-updated menu state across frames. It is a stall/hang, not (observed) memory corruption, because it is the same fiber.
- **Recommendation:** Do not run spawn/trigger work on the menu fiber. Queue the request in the `OnItemSelect` handler and execute it on a dedicated spawn fiber (or next tick) outside `ProcessMenus`; then notify the user on completion. Mirror the already-correct pattern used by `RequestBackup`/`RequestCalloutBackup` (`GameFiber.StartNew`). Guard against double-dispatch while a spawn is in flight.

---

## Medium

### E7 — `HandleOfficerRemount` silently ignores its `stateLabel` / `stateColor` parameters
- **Severity:** Medium
- **Category:** API correctness / latent bug
- **File:line:** `src/Services/BlipService.cs:299-319` (params declared `:299`, never referenced in body; body always calls `AttachPatrolBlip`)
- **Description:** The method advertises an arbitrary remount state (`stateLabel = "PATROL"`, `stateColor = 38`) but always recreates a PATROL blip regardless of arguments. Current caller `StationaryFeature.cs:800` passes the defaults so no visible bug today, but any caller requesting e.g. PARKED/RADAR gets PATROL.
- **Why it matters:** A public contract that lies; future callers will silently get the wrong blip and flash state.
- **Recommendation:** Either honor the parameters (route through `ApplyBlipState`) or remove them from the signature so misuse is a compile error.

### E8 — `StopPlayerVehicleMonitoring` does not stop/join the fiber; restart can spawn duplicate monitoring fibers
- **Severity:** Medium
- **Category:** Resource leak / concurrency
- **File:line:** `src/Services/BlipService.cs:398-410` (`Start`/`Stop`), loop `:412-427`; static `_playerVehicleFiber` never aborted/null-checked-for-exit; callers `Main.cs:80,134,168`
- **Description:** `Start` guards only on the `_playerVehicleMonitoringActive` bool; `Stop` clears the bool but leaves `_playerVehicleFiber` running until its next 300 ms sleep and never aborts/joins it or nulls the field. A quick off-duty→on-duty cycle (or any re-entrant call) can start a second fiber while the first is still alive, yielding two loops racing on `_playerVehicleBlip`.
- **Why it matters:** Duplicate fibers → duplicate attach/delete of the same blip, extra work every 300 ms, and potential use-after-delete on the shared static blip.
- **Recommendation:** Track the fiber, `Abort`/signal-and-join on Stop, null it, and make Start/Stop idempotent under a lock. Have the loop exit-check a generation token.

### E9 — `SendSettings`/static `Random` used from multiple fibers is not thread-safe
- **Severity:** Medium
- **Category:** Concurrency / resource
- **File:line:** `src/Data/ZoneSpawnData.cs:10` (`static readonly Random _rand`) with uses `:339, :390, :416, :423, :428, :431, :446`; contrast `Settings.cs:59` (`lock (_rand)`) and `WorldUtils.cs:348` (`new Random()` per call)
- **Description:** `System.Random` is not thread-safe. `ZoneSpawnData._rand` is shared across all feature fibers (patrol/traffic/stationary/response/pursuit) with no lock, unlike `Settings._rand` which is explicitly locked.
- **Why it matters:** Concurrent `Next()` can corrupt internal state and has historically returned all-zero sequences, producing degenerate vehicle/heading selection and non-random spawn distribution.
- **Recommendation:** Lock around `_rand` (as in `Settings`) or use a thread-local/`RandomNumberGenerator`.

### E10 — 16 duplicate coordinate groups across ZoneSpawnData; standby pool double/triple-counts and can place two units on the exact same spot
- **Severity:** Medium
- **Category:** ZoneSpawnData validity / duplicate & overlapping spots
- **File:line:** duplicates e.g. `(1853.25, 3687.97, 33.8)` at `:137, :176, :307`; `(-448.24, 6012.31, 31.72)` at `:138, :200, :317`; `(546.4, 2669.8, 42.16)` at `:148, :190, :314`; `(1701.3, 6416.7, 32.76)` at `:152, :196, :316`; `(2555.22, 380.78, 108.62)` `:145, :209`; `(-3040.6, 583.9, 7.91)` `:146, :208`; `(1707.9, 4920.6, 42.06)` `:147, :194`; `(-3154.2, 1129.8, 20.86)` `:155, :207`; `(1836.3, 3672.8, 34.27)` `:178, :308`; `(1707.9, 3774.5, 34.5)` `:181, :311`; `(1960.5, 3740.2, 32.3)` `:182, :312`; `(1738.2, 3291.5, 41.1)` `:185, :309`; `(1845.8, 3266.4, 41.3)` `:187, :310`; `(1982.5, 3053.8, 47.2)` `:189, :313`; `(1198.8, 2673.5, 37.8)` `:192, :315`; `(105.8, 6622.1, 31.8)` `:202, :318`
- **Description:** Manual parse: 173 coordinate literals, 153 unique, **16 groups duplicated** (some 3×). `CollectStandbyCandidates` (`:348-370`) iterates `CuratedStandbyLocations` **and** `GetStationaryStandbyZones()` (which itself contains `LSUrbanPoliceStations`, `LSCommercialPlazas`, `BlaineCountyCommercialAndLots`), so a coordinate present in `CuratedStandbyLocations` *and* a zone array is counted twice/thrice in the weighted random pick.
- **Why it matters:** Non-uniform distribution (over-selected spots) and, because each spawn attempt rebuilds candidates independently, two units dispatched close in time can be assigned the identical coordinate (overlapping cruisers/officers). `IsAreaClear(9.0f)` only rejects if an entity is *already* present at evaluation time, so it does not prevent same-frame collisions.
- **Recommendation:** De-duplicate coordinates programmatically (hash by rounded X/Y/Z) when building candidate lists, and de-dup within `CuratedStandbyLocations`. Maintain reserved/claimed spots to avoid concurrent assignment.

### E11 — Freeway/highway coordinates are used as dynamic roadside pullover zones
- **Severity:** Medium (needs in-game verification of shoulder offsets)
- **Category:** ZoneSpawnData / roadway geometry vs CODING_STANDARDS
- **File:line:** `src/Data/ZoneSpawnData.cs:16-31` (`BlaineSandyGrapeseedSenoraFwy`), `:33-44` (`BlainePaletoGreatOceanHwy`), `:67-74` (`LSNEHighway`), returned by `GetRoadsidePulloverZones:242-257`; consumed by `FindBestPulloverSpot:372-412`
- **Description:** `CODING_STANDARDS.md:21` requires high-speed freeways to be excluded from *dynamic* roadside spawning, allowing freeway units only at curated off-ramp rest areas / weigh stations. Two of the pullover arrays are named for freeways (`Senora Fwy`, `Great Ocean Hwy`) and `LSNEHighway` is an interstate; the pullover system picks these coordinates directly with only a 9 m `IsAreaClear` check — no lane-centerline distance assertion (standards require the <6.5 m lane rule / shoulder verification).
- **Why it matters:** If any of these coordinates fall in travel lanes rather than shoulders/turnouts, ambient cruisers and dismounted officers can spawn in live freeway lanes — the exact hazard the standard prohibits.
- **Recommendation:** Verify each coordinate against `WorldUtils.IsOnActiveRoadOrLane`/shoulder checks at load time, or move freeway entries to curated off-ramp/weigh-station lots. Add a one-time validation pass that drops spots failing the lane-distance rule.

### E12 — Menu uses reflection-by-string to find and invoke feature types/methods, swallowing all failures
- **Severity:** Medium
- **Category:** No-workarounds rule / robustness / dead-code risk
- **File:line:** `src/UI/CadenceMenu.cs:270-311` (`PurgeAllFeatures`), `:313-343` (`StopAllFeatures`), `:345-361` (`GetFeatureCount`), `:363-382` (`InvokeFeatureSpawn`), `:384-400` (`ToggleFeature`); feature name arrays duplicated five times
- **Description:** Cadence's teardown and dashboard depend on `Type.GetType($"Cadence.Features.{name}")` + `GetMethod(...)` + `Invoke`. Reflection lookup failures are swallowed (`GetFeatureCount` `catch {}` → returns 0; others log). The standards' Lifecycle section requires `PurgeAllFeatures`/`StopAllFeatures` to reliably clean up; a renamed namespace/class or refactor silently disables teardown, risking ghost entities.
- **Why it matters:** This is a workaround for "isolate from direct feature dependencies" that trades compile-time safety for silent runtime failure, directly against the No-Workarounds principle. It also allocates reflection objects per call/per second (telemetry).
- **Recommendation:** Introduce a small internal feature registry interface (`IFeature { PurgeAll, Stop, GetActiveCount, TrySpawn }`) that features register/implement, or at minimum assert all lookups succeed once at Init and hard-fail/log loudly.

### E13 — Ultimate Backup peds are leaked when the returned vehicle is invalid; no `IsPoliceVehicle` verification
- **Severity:** Medium
- **Category:** Resource leak / strict-validation rule
- **File:line:** `src/Services/UltimateBackupService.cs:347-352` (early `return null` drops `result.Item2` unmanaged), `:354-356` (no `cruiser.IsPoliceVehicle` check for `isPolice:true`)
- **Description:** If `result != null` but `result.Item1` is null/nonexistent, `PrepareUltimateBackupUnit` returns null without dismissing/making-persistent-or-deleting the peds in `Item2`. For police spawns it also never verifies the vehicle is a police vehicle, contrary to `CODING_STANDARDS.md:9` ("strictly verified via `cruiser.IsPoliceVehicle`").
- **Why it matters:** Orphaned non-persistent peds/U B leftovers, and acceptance of non-police vehicles into police roles.
- **Recommendation:** In the invalid-vehicle branch, iterate `Item2` and clean up (`Delete()`/`Dismiss()` as appropriate). For `isPolice == true`, validate `cruiser.IsPoliceVehicle` (or model membership in the police pools) and reject/clean up otherwise.

### E14 — Broad `catch`/`catch(Exception)` swallow failures across these files
- **Severity:** Medium
- **Category:** Exception handling
- **File:line:** `BlipService.cs:61` (`catch {}`), `:75-78`, `:103`, `:359` (`CadenceMenu.GetFeatureCount`), `:255` (`CadenceMenu.Stop`), `UltimateBackupService.cs:337-340`, etc.
- **Description:** Numerous silent `catch {}` blocks (e.g. `SafeDeleteEntityBlip`, `SafeGetAttachedBlip`, `GetFeatureCount`) hide real errors; several file-level `catch (Exception)` blocks are appropriate, but the fully-silent ones make debugging failures (e.g. reflection, native handle issues) impossible and can mask a standards violation.
- **Why it matters:** Production diagnosability; a swallowed exception is indistinguishable from success, and the audit target values verified correctness.
- **Recommendation:** Log at `Debug`/`Warn` at minimum (most already do); for `GetFeatureCount` returning 0 on failure, surface a one-time warning so telemetry bugs are visible.

### E15 — `EupService.SET_PED_PROP_INDEX` invoked with 6 arguments; signature should be verified against the RPH build
- **Severity:** Medium (needs verification)
- **Category:** Native invocation correctness
- **File:line:** `src/Services/EupService.cs:96` — `SET_PED_PROP_INDEX(ped, propId, drawableId, textureId, true, 0)`
- **Description:** The canonical GTA V signature is `SET_PED_PROP_INDEX(Ped, componentId, drawableId, textureId, BOOL attach)` (5 args); some RPH builds expose a 6th `p5`. `validate_natives.py` only checks uppercase naming, not arity/type, so it cannot catch a mismatch.
- **Why it matters:** If the arity is wrong this throws at runtime (caught/logged in `ApplyProps`), silently dropping all prop-based EUP items while components still apply — a subtle, hard-to-notice uniform bug.
- **Recommendation:** Confirm against the shipped `RagePluginHook.dll`/SDK used by the csproj; if 5-arg, remove the trailing `0`. Add arity to the validator if feasible.

---

## Low

### E16 — `SafeDeleteBlip` performs the flash calls in the opposite order to the standard
- **Severity:** Low
- **Category:** Blip lifecycle conformance
- **File:line:** `src/Services/BlipService.cs:29-30` (ALTERNATE before FLASHES)
- **Description:** `CODING_STANDARDS.md:78` specifies `SET_BLIP_FLASHES` then `SET_BLIP_FLASHES_ALTERNATE`. The implementation swaps them. All required calls (including interval/timer reset, `StopFlashing`, `Delete`, null-in-`finally`) are present and correctly by-ref.
- **Why it matters:** Functionally equivalent, but a documented protocol deviation.
- **Recommendation:** Reorder to match the standard, or update the standard to match (the code is otherwise compliant).

### E17 — Ambulances/fire trucks receive the police-car sprite via `AttachEmergencyBlip` (no EMS/Fire blip type)
- **Severity:** Low/Medium
- **Category:** Blip correctness / visual semantics
- **File:line:** `src/Services/BlipService.cs:185-188` (sprite = `Settings.EmergencyBlipSprite`, default 56) used for `[EMS]`/`[FIRE]` at `src/Features/ResponseFeature.cs:726-729`
- **Description:** Response units of type Ambulance/Fire are given the emergency blip, which is hard-wired to sprite 56 (PoliceCar) and red flashing. There is no EMS/Fire sprite setting.
- **Why it matters:** A fire engine / ambulance shows a police-car icon — misleading and against the "authentic" intent of the blip palette.
- **Recommendation:** Add `EmergencyServiceType`-aware sprites/colours (or a dedicated `AttachEmsBlip`/`AttachFireBlip`), defaulting to appropriate GTA sprites.

### E18 — `GetDefaultStatePatrolVehicle` can return non–State-Patrol models
- **Severity:** Low
- **Category:** ZoneSpawnData / jurisdiction
- **File:line:** `src/Data/ZoneSpawnData.cs:233-236` + `:444-447` (`PoliceVehiclesHighway` contains `"police","police2","police3","sheriff"`)
- **Description:** "Highway/State Patrol" selection uses a pool that includes local LSPD and Sheriff models, so state patrol responses can spawn local cruisers/liveries.
- **Why it matters:** Undermines jurisdiction authenticity the config comments promise (80% SAHP etc.).
- **Recommendation:** Give State Patrol its own pool (`police2/police3/sheriff2` or an EUP-backed model list) distinct from the city/highway mix.

### E19 — Static `CadenceMenu.Stop()` aborts the watchdog fiber via `Thread.Abort`
- **Severity:** Low
- **Category:** Fiber/resource lifecycle
- **File:line:** `src/UI/CadenceMenu.cs:250-259`
- **Description:** `Stop()` is called only from `Main.Finally()` (`Main.cs:83`, RPH unload, game thread) and the off-duty branch of `OnDutyStateChanged` (`Main.cs:167`, LSPDFR event, game thread) — so `Abort` is always issued from the game/LSPDFR thread. It targets the watchdog fiber, which may be executing `ProcessMenus`/a native call at that instant. `Thread.Abort` is obsolete and can throw; it is swallowed (`catch {}`).
- **Why it matters:** Aborting a fiber mid-`ProcessMenus` risks leaving RAGENativeUI/native-UI state inconsistent (the pool is not disposed and menu `Visible` flags may be mid-mutation). Error is hidden. Low likelihood given the toggles are user-initiated, but nondeterministic.
- **Recommendation:** Signal the loop via `_isRunning`/a cancellation flag and let it exit cleanly between iterations (it already checks `while (_isRunning)`), joining with a bounded wait instead of `Abort`.

### E20 — `MenuPool` and menu objects are not disposed on Stop/reload
- **Severity:** Low
- **Category:** Resource leak
- **File:line:** `src/UI/CadenceMenu.cs:241-264` (`Stop`); `BuildMenus:406-535` recreates on re-init
- **Description:** `RAGENativeUI.MenuPool` is `IDisposable`; `Stop()` leaves `_menuPool` and all `UIMenu` instances referenced. Re-`Initialize()` (`:125-148`) constructs a fresh `MenuPool` and overwrites the fields, orphaning the previous menus/handlers.
- **Why it matters:** Minor leak across duty cycles / repeated init; event handlers on old menus die with the GC.
- **Recommendation:** Dispose the pool and null the menu references in `Stop()`.

### E21 — `PrepareUltimateBackupUnit`/native spawners can leak loaded models on post-load exceptions
- **Severity:** Low
- **Category:** Resource leak
- **File:line:** `src/Services/UltimateBackupService.cs:451-471`, `:503-519`, `:551-567` (models dismissed only on the success path)
- **Description:** If `new Vehicle(...)`/`new Ped(...)` or intervening code throws after `SafeLoad` succeeded, the loaded `Model`s are never `Dismiss()`ed (the `catch` returns null).
- **Why it matters:** Model streaming handles remain loaded until engine eviction; repeated failures accumulate.
- **Recommendation:** Wrap entity creation so loaded models are dismissed in a `finally`.

### E22 — `SpawnNativePatrolUnit` redundantly grounds/heads the vehicle; `IsAvailable` re-scans assemblies when UB is absent
- **Severity:** Low
- **Category:** Native/API usage / perf
- **File:line:** `UltimateBackupService.cs:453` (`cruiser.Heading = heading` duplicate of ctor), `:455-456` (double ground) ; `:44-52` (`IsAvailable` → `Detect`) and `:58-132` (full AppDomain + LSPDFR enumeration each call until success)
- **Description:** Minor redundancy; and when UB is not installed, every `IsAvailable` access (called per spawn) re-enumerates all AppDomain assemblies and calls `LspdFunctions.GetAllUserPlugins()`.
- **Why it matters:** Wasted work each spawn; heading/ground double-application is harmless but noisy.
- **Recommendation:** Cache a negative-detection result with a re-probe interval; remove redundant calls.

### E23 — Duplicate/ambiguous fallback settings (`AllowFallback` vs `AllowFallbackSpawning`)
- **Severity:** Low
- **Category:** Settings persistence / configuration clarity
- **File:line:** `src/Core/Settings.cs:101` (`AllowFallbackSpawning`, INI `[Backup]` `:235-236`) vs `:169` (`AllowFallback`, INI `[Integration]` `Fallback/AllowFallback` `:202-203`); menu toggle edits `AllowFallback` (`CadenceMenu.cs:616-623`); `AllowFallbackSpawning` used only in `BackupFeature.cs:1153,1594`
- **Description:** Two similarly named fallback flags drive different subsystems; only `AllowFallback` is exposed in the menu. Users editing the INI can easily toggle the wrong one.
- **Why it matters:** Configuration confusion; the documented "Allow Native Fallback" behaviour can differ from the commandeering fallback.
- **Recommendation:** Rename to distinct, self-describing keys (e.g. `UbFallbackToVanilla` vs `BackupFallbackSpawner`) and expose both, or unify.

### E24 — `GetAllZones()` is dead code
- **Severity:** Low
- **Category:** Dead code
- **File:line:** `src/Data/ZoneSpawnData.cs:269-274`
- **Description:** `GetAllZones()` has no references in `src` (confirmed by grep).
- **Why it matters:** Dead code drifts out of sync with the real query paths.
- **Recommendation:** Remove or use it.

### E25 — `AttachSuspectBlip(Entity, …)` / `(Ped, …)` overloads are dead/duplicated
- **Severity:** Low
- **Category:** Dead code (related to E5)
- **File:line:** `src/Services/BlipService.cs:325-330`
- **Description:** Two identical always-null overloads; `Entity` overload is redundant with the `Ped` overload (Ped : Entity).
- **Recommendation:** Consolidate/remove with E5.

### E26 — EupService applies magic component/drawable IDs with no existence validation and ignores `EnableEupOutfits` on its fallback helpers
- **Severity:** Low
- **Category:** EUP / magic values
- **File:line:** `src/Services/EupService.cs:120` (component 9, drawable 1), `:134` (component 8, drawable 1); `:48-58`, `:80-96` (no range validation on `compId`/`propId`/`drawableId`)
- **Description:** `ApplyBodyArmor`/`ApplyTrafficVest` bypass the `Settings.EnableEupOutfits` gate that `Apply` honors, and hardcode drawable indices that may not exist on every ped model. Out-of-range IDs are not validated before native calls.
- **Why it matters:** Silent no-ops or wrong clothing on some models; inconsistent with the master EUP toggle.
- **Recommendation:** Route through the same gate; validate/`drawableId >= 0`; make vest/armor drawables configurable.

### E27 — `Logger` toggles applied after preset parsing during `Settings.Load`
- **Severity:** Low
- **Category:** Settings persistence ordering
- **File:line:** `src/Core/Settings.cs:323-325` (assigned at end) vs `ApplyPreset` logging at `:265` and section logging before
- **Description:** `Logger.EnableFileLogging`/`EnableDebugLogging` are copied from settings only after the entire file is parsed, so early log lines during `Load()` (including `ApplyPreset`'s `Logger.Info`) are emitted under the previous/stale logging configuration.
- **Recommendation:** Apply logger toggles as soon as `[General]` keys are parsed.

### E28 — `Settings.Load` does not reset to defaults before parsing (stale keys survive reload)
- **Severity:** Low
- **Category:** Settings persistence
- **File:line:** `src/Core/Settings.cs:275-334`
- **Description:** `Load` mutates the existing static `Settings` in place. If a user removes a key from the INI and reloads via the menu, the old value persists instead of reverting to the default.
- **Recommendation:** Track which keys were present, or reset to defaults at the start of `Load`.

---

## Per-file summary

### `src/Services/BlipService.cs`
- `SafeDeleteBlip` is essentially standard-compliant (by-ref, existence check, flash/interval/timer resets, `StopFlashing`, `Delete`, null-in-`finally`); only the FLASHES/ALTERNATE order deviates (E16).
- Main issues: `AttachPursuitBlip` uses `alternateFlash: true` (E2); `AttachSuspectBlip` is a dead null stub that defeats the required fallback (E5); the player-vehicle monitor overwrites native LSPDFR blips (E4) and its fiber lifecycle leaks/races (E8).
- `HasAnyActiveRadarMarker` correctly uses `GET_BLIP_FROM_ENTITY` + `DOES_BLIP_EXIST` and checks peds-in-vehicle. `SafeGetAttachedBlip`/`SafeDeleteEntityBlip` are managed-only by design (correctly will not delete native blips).
- `HandleOfficerRemount` ignores promised state params (E7).

### `src/UI/CadenceMenu.cs`
- Correct fiber usage for `ProcessMenus`; teardown public API exists and `Main.Finally` calls it in the standards-mandated order (`PurgeAll → StopAll → Stop`; `Main.cs:80-83`).
- Weaknesses: reflection-by-string dispatch with swallowed failures (E12); test-spawn items invoke feature logic on the watchdog fiber and **yield inside `ProcessMenus`** (`SpawnTestUnit`/`SpawnTestStop`/`SpawnTestRadarUnit`/`SpawnTestChillingUnit`/`TriggerTestRun`) stalling the menu up to ~8-16s (E6, confirmed); no settings persistence (E3); `Thread.Abort` teardown (E19); pool not disposed (E20); broad per-frame `catch` (E14). Note: the three `BackupFeature` triggers correctly dispatch via `GameFiber.StartNew`.
- Verified NOT a bug: programmatic `Index`/`Checked` sync does not re-enter `OnListChange`/`OnCheckboxChange` (RAGENativeUI 1.9.3 setters are plain field writes).

### `src/Services/UltimateBackupService.cs`
- Correctly keeps paramedics/firemen from being converted to cops: `SpawnAmbulanceUnit`/`SpawnFireTruckUnit` call `PrepareUltimateBackupUnit(..., isPolice:false)` and the native ambulance/fire spawners never call `SetPedAsCop`. (Requirement met.)
- Fallback gating (`UseUltimateBackup && !AllowFallback` → reject native) is directionally correct, but `Detect()`'s file-only availability check makes the strict path fragile (E1). UB spawn methods themselves ignore `Settings.UseUltimateBackup` (callers gate).
- `AcquireUbGate` is a plain bool (not a real lock) with a timeout that proceeds anyway; low risk.
- Method names/arity against the shipped UB 1.8.7.1 DLL are correct (including the real `geStatePatrolPed`).
- `[HandleProcessCorruptedStateExceptions]`+`[SecurityCritical]`+`NoInlining` gives partial CSE protection; an AccessViolation in RPH native/UB code may still terminate the process — treat as a residual crash risk (see Open Questions).
- Ped leak on invalid vehicle; no `IsPoliceVehicle` verification (E13); model leak on failure (E21).

### `src/Services/EupService.cs`
- Small and readable; native names are canonical uppercase (validator passes). Parsing is robust (`TryParse`, negative drawable → `CLEAR_PED_PROP`).
- Concerns are minor: `SET_PED_PROP_INDEX` arity (E15), magic IDs / gate bypass / no range validation (E26).

### `src/Data/ZoneSpawnData.cs`
- Good curated data volume, but 16 duplicate coordinate groups and cross-list double counting (E10); freeway pullover arrays conflict with the freeway-exclusion standard pending shoulder verification (E11).
- Shared non-locked static `Random` (E9); dead `GetAllZones` (E24); State Patrol pool mixes local models (E18).
- `IsAreaClear(9.0f)` is invoked per candidate per spawn (potential per-spawn cost) and does not prevent concurrent assignment to the same spot.

---

## Open questions / needs verification

1. **Thread.Abort on fibers (E19): RESOLVED.** All `CadenceMenu.Stop()` callers are on the game/LSPDFR thread (`Main.cs:83` `Finally()`, `Main.cs:167` off-duty). No off-thread call path exists. Residual risk is only that `Abort` may land while the watchdog fiber is mid-`ProcessMenus`, which a cooperative `_isRunning` exit would remove.
2. **Menu callback yields (E6): RESOLVED — active bug.** Traced all eight `InvokeFeatureSpawn` targets. `SpawnTestUnit`, `SpawnTestStop`, `SpawnTestRadarUnit`, `SpawnTestChillingUnit`, and `TriggerTestRun` yield the menu (watchdog) fiber inside `ProcessMenus` via `AcquireUbGate` (`UltimateBackupService.cs:28`) and/or `SafeLoad` (`WorldUtils.cs:377`). `TriggerPanicButton`/`TriggerGroupBackup`/`TriggerCalloutResponse` do **not** (they wrap work in `GameFiber.StartNew`). Impact is a UI/menu stall (up to ~8-16s worst case), not observed corruption. Fix by dispatching spawns onto their own fiber as `RequestBackup` already does.
3. **`SET_PED_PROP_INDEX` arity** against the exact RPH SDK referenced by `src/Cadence.csproj` (`lib/RagePluginHook.dll`, v1.124.1393.17586). Reflection of that reference assembly failed (types have no RVA), so this must be confirmed in a build environment. (E15)
4. **AccessViolation / CSE handling:** Whether `[HandleProcessCorruptedStateExceptions]` actually catches anything in this RPH host, or whether an AV in UltimateBackup/native code will hard-terminate `GTA5.exe`. This is the residual "can crash the game" risk and should be tested with a deliberately failing UB. (E1/UB)
5. **Freeway pullover spots (E11):** Exact in-game lane geometry of each coordinate in `BlaineSandyGrapeseedSenoraFwy`/`BlainePaletoGreatOceanHwy`/`LSNEHighway` — are they shoulders/turnouts or travel lanes? Requires running the lane-distance check.
6. **Zone coordinate validity:** Several standby/pullover spots (e.g. `(-3040.6, 583.9)`, `(2555.22, 380.78)`, airport/apron entries) may resolve inside buildings, on rooftops, or on non-drivable geometry. `WorldUtils` has `IsRooftopOrElevatedStructure`/`IsAreaClear`, but neither is applied to the curated standby path (`CollectStandbyCandidates` only calls `IsAreaClear`). Recommend a batch validation pass.
7. **Concurrent spot assignment (E10):** Confirm whether two features can call `FindBestStandbyLocation`/`FindBestPulloverSpot` for the same coordinates within one frame before either spawns (no reservation mechanism exists).
8. **`Settings.AllowFallback` vs `AllowFallbackSpawning`:** Confirm intended semantics and which one the "Allow Native Fallback" menu item is meant to control. (E23)
9. **Player-vehicle native blip ownership (E4):** Confirm whether the native blip being recolored is always LSPDFR's player-vehicle marker, or can be a suspect/other official marker; determines severity.
