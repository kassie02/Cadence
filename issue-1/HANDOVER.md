# Cadence — Code Audit Handover

**For:** Developer
**From:** Read-only audit, 2026-10-16 (branch `dev`)
**Files in this folder:** `HANDOVER.md` (this TL;DR) + `code-audit-findings.md` (full detail: file paths, line numbers, suggested fixes)

---

## What this is

A read-only review of the entire Cadence codebase. **No code was changed.** All issues below are **logic / lifecycle / concurrency** bugs — not broken native calls.

**Good news:** the native validator passes clean (`python scripts/validate_natives.py` → 0 violations across 201 native call sites). Nothing here blocks a build.

**Total: ~14 findings** — 3 High, 1 Medium-High, ~5 Medium, rest Low/docs.

---

## 🔴 Fix first — can strand vehicles or freeze the game

1. **A1 — Assist permanently leaks a police cruiser** (`src/Features/AssistFeature.cs` ~L430). It commandeers a cruiser (halts it, sets handbrake, lights on) and claims it, but if no live officers are found it just `return`s — never releases the claim. Cruiser stays parked/handbraked forever and is locked out of the system.
2. **A3 — Assist holds a lock while disposing** (`AssistFeature.cs` ~L231). Calls `Update()`/`Dispose()` (native/LSPDFR calls) *while holding* `_syncLock`. Deadlock/re-entrancy risk.
3. **F4 — Backup `Stop()` disposes under the lock** (`src/Features/BackupFeature.cs` ~L174). Same class of bug. Fix pattern: copy out under lock, dispose outside.

## 🟠 Should fix — real leaks and races

4. **A4 — Assist leaks sessions** (`GetActiveCount`, ~L171). Removes dead sessions without disposing → blips, claims, cruisers pile up.
5. **F1 — Backup silently drops a bad unit** (`DispatchSingleUnit` ~L1040) but leaves its reserved cruiser/claims hanging.
6. **F9 — Blip dictionary race** (`BackupFeature.cs`). Written under a lock (L4105) but cleared *without* one (`ClearFootBlips` L4214, called from `Dispose`).
7. **F5 — Callout despawn ignores the user's setting** — hardcoded `600.0f`/`180000` (L3317, L3327) instead of `Settings.DespawnDistance`.
8. **F2/F3 — Panic/reengage paths enumerate under the lock** (L863–870, L919–926, L1688–1734).

## 🟡 Lower priority — polish / consistency

9. **F8 — Pursuit blip behaves inconsistently:** `AttachPursuitBlip` flashes red/blue, `TransitionToPursuit` doesn't — same state, two looks (`src/Services/BlipService.cs` L197 vs L513).
10. **F10/F11 — Ignored `TryClaim` results + a narrow claim leak** (`BackupFeature.cs` L2511–2520, L2021).
11. **F6/F7 — Missing `Exists()`/alive guards** before a couple of native/task calls (currently saved by try/catch).

## 📄 Docs / config drift

12. **D1 — README preset table is wrong.** Code + unit tests agree with each other; README disagrees on tiers 3, 4, 6. Decision needed: fix docs or fix code.
13. **D2 — Debug logging stays ON when `Cadence.ini` is missing.** `Logger` defaults `true`, `Settings` defaults `false`, and the missing-INI early return (`src/Core/Settings.cs` L463) never syncs them.
14. **D3 — Non-volatile cross-fiber flag + spin loop** (`src/Services/UltimateBackupService.cs` L21/L26) — should be `volatile` and time-bounded.

---

## Judgment calls (confirm intent)

- **A2** — Assist's relinquish code is effectively dead because Patrol claims its own cruisers. Likely intentional.
- **Metro preset sets `ChaosMode = true`** (`Settings.cs` L279) — tests assert it, so looks deliberate.
- **A5** — a couple of `TryClaim` return values are ignored (low impact).

## Suggested fix order

1. Claim/lifecycle leaks — **A1, A4, F1, F11, F10**
2. Lock-across-Dispose/yield — **A3, F4, F2/F3, F9**
3. Config/behavior mismatches — **F5, F8, D2, D1, D3**
4. Defensive guards — **F6, F7, A2, A5**

## After fixing, verify

- `python scripts/validate_natives.py` → still 0 violations
- `dotnet build` (`TreatWarningsAsErrors=true`, .NET 4.8 / x64) → clean
- Run `tests/Cadence.Tests` (if presets change, update `SettingsTests` in the same commit)
- In-game smoke test: threat-assist with no officers (**A1**), backup `Stop()` mid-dispatch (**F4**), both pursuit entry paths (**F8**), and load with no `Cadence.ini` (**D2**)
