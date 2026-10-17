# Cadence Code Audit — Findings & Remediation Plan

- **Scope:** `C:\Users\User\Pictures\111\Cadence` (read-only audit; no source changes made)
- **Date:** 2026-10-16
- **Branch:** `dev`
- **Reference standard:** `cadence-standards` skill invariants (zero-lock-across-yield, native rules, entity/blip lifecycle, claim lifecycle, pursuit integration)
- **Validator:** `python scripts/validate_natives.py` → **PASSED** (0 violations, 24 files, 201 native call sites). The issues below are **logic / lifecycle / concurrency** defects, not native-name violations.

> Legend: **S** = severity (High / Medium / Low / Info). Each item lists location, symptom, and suggested fix. Line numbers refer to the audited revision.

---

## Priority Summary

| ID | Severity | File | One-line summary |
|----|----------|------|------------------|
| A1 | High | `AssistFeature.cs` | Cruiser claim leaked when no officers found |
| A3 | High | `AssistFeature.cs` | Disposes/yields while holding `_syncLock` |
| A4 | Medium-High | `AssistFeature.cs` | `GetActiveCount` drops sessions without `Dispose` (leak) |
| F4 | High | `BackupFeature.cs` | `Stop()` disposes under `_syncLock` |
| F1 | Medium | `BackupFeature.cs` | Invalid unit silently dropped; claim/reservation stranded |
| F9 | Medium | `BackupFeature.cs` | `_officerFootBlips` unsynchronized in `ClearFootBlips` |
| F5 | Medium | `BackupFeature.cs` | Callout despawn ignores `Settings.DespawnDistance` |
| F2/F3 | Medium | `BackupFeature.cs` | Enumeration under `_syncLock` on panic/reengage paths |
| F8 | Low-Med | `BlipService.cs` / `BackupFeature.cs` | Pursuit blip flash mode inconsistent |
| F10/F11 | Low | `BackupFeature.cs` | Ignored `TryClaim` result / narrow claim leak |
| F6/F7 | Low | `BackupFeature.cs` | Missing `Exists()`/alive guards before native/task calls |
| A2/A5 | Low/Info | `AssistFeature.cs` | Unreachable relinquish path; ignored `TryClaim` |
| D1 | Low | `Settings.cs` / `docs/README.md` | Preset values drift between code and docs |
| D2 | Low-Med | `Logger.cs` / `Settings.cs` | Debug logging stays `true` when INI missing |
| D3 | Low | `UltimateBackupService.cs` | Non-volatile cross-fiber flag + spin loop |

---

## 1. AssistFeature.cs

### A1 — Cruiser claim leaked when no officers are found (HIGH)
**File:** `src/Features/AssistFeature.cs`, `MobilizeCruiserDefense` (lines ~384–473)

The cruiser is claimed at line **396** (`UnitRegistry.TryClaim(cruiser, UnitRegistry.OwnerAssist)`), then the vehicle is halted and the handbrake set (lines 406–407), silent siren enabled (line 410). If no live cop officers are found, the method early-returns:

```csharp
if (officers.Count == 0) return;   // line 430
```

There is **no** `UnitRegistry.Release(cruiser)` on this path. Result: the cruiser is left halted, handbraked, with silent lights on, and permanently claimed in `UnitRegistry` (never released until the feature is purged). This is a permanent claim leak + stranded vehicle.

**Fix:** Before the early return (and in the `catch` at line 469), release the claim and undo the halt/handbrake/lights, or move the claim until after the officer check succeeds.

### A2 — Relinquish path effectively unreachable (LOW / INFO)
**File:** `src/Features/AssistFeature.cs`, `ScanForNearbyThreats` (lines ~258–267) and `MobilizeCruiserDefense` (lines 389–394)

`ScanForNearbyThreats` skips any cruiser that is Cadence-managed (`IsBackupCruiser`, `IsPursuitCruiser`, etc.) or already claimed (`UnitRegistry.IsClaimed`). Since `PatrolFeature` claims all of its cruisers, the `RelinquishCruiser` calls in `MobilizeCruiserDefense` (lines 389–394) can essentially never fire — Assist only ever acts on unclaimed ambient cruisers.

**Disposition:** Likely intended behavior (Assist must not commandeer another feature's unit). Flag as INFO only; if cross-feature commandeering *is* desired, the scan filter must be relaxed.

### A3 — Lock held across `Update()` / `Dispose()` (HIGH)
**File:** `src/Features/AssistFeature.cs`

`UpdateActiveEngagements` calls `session.Update()` while holding `_syncLock` (line **231**), and `session.Dispose(...)` is also called inside the lock at line **226**. `Dispose` performs LSPDFR `Functions.*` calls and native calls (yield points / re-entrancy), violating the zero-lock-across-yield invariant. `PurgeAllInternal` (lines 475–491) correctly copies-out-then-disposes outside the lock, which is the pattern to follow.

**Fix:** Collect sessions to update/dispose under the lock, then release the lock and perform `Update()`/`Dispose()` outside it. Note: `Update()` on live sessions must still be serialized against `ScanForNearbyThreats` — a dedicated per-session work list is cleaner than a global lock.

### A4 — `GetActiveCount` leaks without disposing (MEDIUM-HIGH)
**File:** `src/Features/AssistFeature.cs`, `GetActiveCount` (lines 167–174)

```csharp
_activeSessions.RemoveAll(s => s == null || !s.IsValid() || s.State == CombatDefenseSession.CombatState.Completed);
```

Sessions are removed without calling `Dispose`. Invalid/completed sessions hold blips, `UnitRegistry` claims, and cruisers. `GetActiveCount` is called from the UI thread (`CadenceMenu`), so leaked sessions accumulate until manual purge.

**Fix:** Mirror `BackupFeature.GetActiveCount`: snapshot the stale sessions, remove them under the lock, then `Dispose` outside the lock.

### A5 — Ignored `TryClaim` result for officers (LOW)
**File:** `src/Features/AssistFeature.cs`, line **434**

`UnitRegistry.TryClaim(officer, UnitRegistry.OwnerAssist);` return value is ignored. If the officer is already claimed by another owner, Assist still mutates its tasks/weapons. Low impact, but should be checked (or logged).

---

## 2. BackupFeature.cs

### F4 — `Stop()` disposes under `_syncLock` (HIGH)
**File:** `src/Features/BackupFeature.cs`, `Stop` (lines ~174–263)

The teardown path disposes units while holding `_syncLock`, so native/LSPDFR calls run under the lock (lock-across-Dispose/yield). Same class of defect as A3.

**Fix:** Snapshot the collections under the lock, clear them, then dispose outside the lock.

### F1 — Invalid unit silently dropped; claim/reservation stranded (MEDIUM)
**File:** `src/Features/BackupFeature.cs`, `DispatchSingleUnit` (lines 1040–1055)

```csharp
BackupUnitSession unit = ResolveOrSpawnUnit(type, playerPos, isCode3);
if (unit != null && unit.IsValid()) { ... add to _activeUnits ... remove from _reservedCruisers ... }
```

When `unit` is `null` or invalid, nothing is added to `_activeUnits`, but `ResolveOrSpawnUnit` may already have reserved a cruiser / claimed units. There is **no** cleanup path for the rejected unit → stranded reservation.

**Fix:** On the invalid path, release any reserved cruiser and claims associated with the returned unit, or make `ResolveOrSpawnUnit` atomic (only reserve on success).

### F9 — `_officerFootBlips` race: locked on write, unlocked on clear (MEDIUM)
**File:** `src/Features/BackupFeature.cs`

`UpdateBlipStates` accesses `_officerFootBlips` under `lock (_officerFootBlips)` (lines 4105–4155), but `ClearFootBlips` (lines 4214–4221) iterates and clears the dictionary **without** the lock, and is invoked from `Dispose` (line 4238) and line 4085.

**Fix:** Wrap the `ClearFootBlips` body in `lock (_officerFootBlips)`.

### F5 — Callout despawn timeout ignores `Settings.DespawnDistance` (MEDIUM)
**File:** `src/Features/BackupFeature.cs`, lines **3317** and **3327**

Hardcoded `600.0f` / `180000 ms` constants are used instead of `Settings.DespawnDistance`, so user-configured despawn distance is not honored for callout units.

**Fix:** Derive the timeout from `Settings.DespawnDistance` (consistent with the rest of the feature).

### F2 / F3 — Enumeration while holding `_syncLock` (MEDIUM)
**File:** `src/Features/BackupFeature.cs`

- F2: panic path enumerates under `_syncLock` (lines ~863–870, ~919–926).
- F3: `TryReengageDismissingUnit` enumerates under `_syncLock` (lines ~1688–1734).

Enumeration itself is not a yield, but any native/`Exists()` calls inside the loop are, and this widens the lock-across-yield surface.

**Fix:** Snapshot to a local list under the lock, then iterate/native-call outside it.

### F8 — Inconsistent pursuit blip flash mode (LOW-MEDIUM)
**Files:** `src/Services/BlipService.cs` and `src/Features/BackupFeature.cs`

- `BlipService.AttachPursuitBlip` (line **197**) uses `alternateFlash: true`.
- `BlipService.TransitionToPursuit` (line **513**) uses `alternateFlash: false` for the same "PURSUIT" state.
- `BackupFeature` uses both paths (lines ~3220/3616 vs ~4188).

So depending on how a unit enters pursuit, the marker either alternates red/blue or does not — contradicting the documented "alternating red/blue pursuit blip" behavior.

**Fix:** Pick one flash policy for the pursuit state and use it in both methods.

### F10 — Ignored `TryClaim` result (LOW)
**File:** `src/Features/BackupFeature.cs`, lines **2511–2520**

`UnitRegistry.TryClaim` return value is ignored for cruiser/driver/passenger, so a failed claim is not detected. Check and log/handle.

### F11 — Narrow claim leak in ambient candidate selection (LOW)
**File:** `src/Features/BackupFeature.cs`, `FindCandidateAmbientVehicle` claim at line **2021** vs re-check at **1766**

A candidate ambient vehicle is claimed at line 2021; if the `candidate.Exists()` check at 1766 later fails, the claim is not released (narrow race).

**Fix:** Release the claim on the failure path.

### F6 / F7 — Missing `Exists()` / alive guards (LOW)
**File:** `src/Features/BackupFeature.cs`

- F6 (lines ~4009–4023): departure fiber dereferences `Cruiser.Driver` before a `Cruiser.Exists()` guard (currently caught by the surrounding `catch` — LOW).
- F7 (lines ~3261, ~3270): `Driver` is passed to `NativeTasks.TaskLongRangeEmergencyDrive` without an `Exists()`/alive guard; the watchdog at ~3274 does guard, so the native call may throw first.

**Fix:** Guard `Exists()`/`IsAlive` before the native/task call (fail fast, avoid relying on `catch`).

---

## 3. Settings / Logger / Docs drift

### D1 — Preset values drift between code and README (LOW)
**Files:** `src/Core/Settings.cs` (`ApplyPreset`, lines 228–321), `docs/README.md` (lines 164–170), `tests/Cadence.Tests/SettingsTests.cs`

Code and unit tests agree; the README table disagrees for several tiers:

| Tier | README (docs/README.md) | Code + tests (Settings.cs / SettingsTests.cs) |
|------|-------------------------|-----------------------------------------------|
| 3 | `6 / 3 / 3 / 3 / 5` | `6 / 3 / 2 / 3 / 5` (stationary 2, not 3) |
| 4 | `5 / 3 / 4 / 2 / 4` | `5 / 3 / 5 / 2 / 6` |
| 6 | `12 / 5 / 5 / 6 / 8` | `12 / 6 / 4 / 6 / 8` |

Also: README names tier 3 "Heavy Urban Patrol (High Density)" while code sets `Preset = "Metro"`. Tier 4 README says "State Police Ratio 0.8" which matches code `HighwayPatrolRatio = 80`. Tier 5 README says "Chaos Metropolis" but code preset name is `"Chaos"`.

Additionally, `Metro` sets `ChaosMode = true` (line 279) and `Chaos` sets `ChaosMode = true` (line 251); confirm this is intended for both (tests assert it, so it is at least deliberate in tests).

**Fix:** Reconcile README table/names with code (or vice-versa) so docs match shipped behavior. Decide whether `Metro` should really enable `ChaosMode`.

### D2 — Debug logging defaults disagree; missing INI keeps debug on (LOW-MEDIUM)
**Files:** `src/Core/Logger.cs` line 39, `src/Core/Settings.cs` lines 21 / 514

- `Logger.EnableDebugLogging` defaults to **`true`** (Logger.cs line 39).
- `Settings.EnableDebugLogging` defaults to **`false`** (Settings.cs line 21).
- `Settings.Load` syncs `Logger.EnableDebugLogging` only on the full-parse path (line 514). On the early return when the INI is missing (lines 463–468), it only calls `ValidateDistances()` and warns — it never syncs the logger.

Result: when `Cadence.ini` is absent, debug logging stays **enabled** (verbose), contradicting the intended default.

**Fix:** Default `Logger.EnableDebugLogging` to `false`, and/or sync `Logger.EnableDebugLogging = EnableDebugLogging;` on the missing-INI early-return path.

### D3 — Non-volatile cross-fiber flag + spin loop (LOW)
**File:** `src/Services/UltimateBackupService.cs` (line 21, spin loop line 26)

`_isUbRequestInProgress` is a non-`volatile` bool read across fibers, with a spin loop at line 26. On a multi-threaded runtime this is a visibility/hang hazard.

**Fix:** Mark the field `volatile` (or use `Interlocked`/`SpinWait`) and bound the spin with a timeout/backoff.

---

## Suggested Remediation Order

1. **Claim/lifecycle leaks (correctness, can strand vehicles):** A1, A4, F1, F11, F10.
2. **Lock-across-yield / Dispose (deadlock & re-entrancy):** A3, F4, F2/F3, F9.
3. **Config/behavior mismatches:** F5, F8, D2, D1, D3.
4. **Defensive guards:** F6, F7, A2 (decide intent), A5.

## Verification Steps After Fixes

- `python scripts/validate_natives.py` (must stay at 0 violations).
- `dotnet build` with `TreatWarningsAsErrors=true` (must stay clean, x64/.NET 4.8).
- Run `tests/Cadence.Tests` (`SettingsTests` already pin preset values — update them together with code if you change presets).
- In-game smoke test: threat-assist with no live officers (A1 path), backup `Stop()` during active units (F4), pursuit entered via both dispatch and transition paths (F8), and plugin load with no `Cadence.ini` present (D2).
