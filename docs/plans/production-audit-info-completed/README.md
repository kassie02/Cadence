# Cadence — Production Readiness Audit Info Pack

**Purpose:** Handoff bundle for the Cadence maintainer/dev. Contains the full production-readiness audit performed on 2026-10-15 plus the raw per-subsystem research notes it was compiled from.

**Scope audited:** The entire repository at `C:\Users\User\Pictures\test-noedit\Cadence` — ~15,300 lines of C# across 19 source files, plus build scripts, config, and docs.

**Mode:** Read-only. No source, configuration, or build files were modified during the audit.

**Baseline contract:** [`../CODING_STANDARDS.md`](../CODING_STANDARDS.md)

---

## Read this first

| Order | File | What it is |
|---|---|---|
| 1 | [`00-MASTER-production-readiness-audit.md`](00-MASTER-production-readiness-audit.md) | **THE MAIN DOCUMENT.** Executive summary, Tier 0 release blockers, all Critical/High/Medium/Low findings with `file:line` + recommended fixes, 7 structural refactors, CI/environment improvements, verified false-positives, open questions, and the 4-milestone remediation roadmap. |

Start and finish with the master document. Everything else is supporting detail.

## Raw research notes (context pointers)

| File | Coverage |
|---|---|
| [`01-core-common.md`](01-core-common.md) | `Core/Main.cs`, `Core/Settings.cs`, `Core/Logger.cs`, `Common/InputHelper.cs`, `Common/NativeTasks.cs`, `Common/WorldUtils.cs` |
| [`02-backupfeature-firstpass-SUPERSEDED.md`](02-backupfeature-firstpass-SUPERSEDED.md) | **SUPERSEDED — do not rely on this.** An early shallow pass on `BackupFeature.cs`; it under-covered the file. Use 03 + 04 instead. Kept only for completeness. |
| [`03-backupfeature-part1.md`](03-backupfeature-part1.md) | `Features/BackupFeature.cs` lines 1–1700 (full re-audit) |
| [`04-backupfeature-part2.md`](04-backupfeature-part2.md) | `Features/BackupFeature.cs` lines 1650–3814 (full re-audit) |
| [`05-pursuit-patrol-assist.md`](05-pursuit-patrol-assist.md) | `Features/PursuitFeature.cs`, `Features/PatrolFeature.cs`, `Features/AssistFeature.cs` |
| [`06-trafficstop-stationary-response.md`](06-trafficstop-stationary-response.md) | `Features/TrafficStopFeature.cs`, `Features/StationaryFeature.cs`, `Features/ResponseFeature.cs` |
| [`07-ui-services-data.md`](07-ui-services-data.md) | `UI/CadenceMenu.cs`, `Services/BlipService.cs`, `Services/UltimateBackupService.cs`, `Services/EupService.cs`, `Data/ZoneSpawnData.cs` |
| [`08-build-config-docs.md`](08-build-config-docs.md) | Build/config/packaging/deployment/docs/repo-hygiene: `Cadence.csproj`, `Cadence.sln`, `Cadence.ini`, `AssemblyInfo.cs`, `dist/build.bat`, `scripts/*`, `.gitignore`, READMEs, CHANGELOG, future-features |

## Method

Six independent passes read every source file in full and cross-checked against `CODING_STANDARDS.md`. `BackupFeature.cs` (3,814 lines, the largest and most crash-prone file) was re-audited in two overlapping halves after the first pass proved too shallow. The following facts were independently re-verified during synthesis:

- Banned hash `0xA2719248`: **no occurrences** (clean)
- Raw-hex native calls (`Natives.x...`): **no occurrences** (clean)
- A `Settings.Save()` / persistence method: **none exists**
- `BlipService.AttachSuspectBlip`: **both overloads `=> null`** (dead stub)
- Pursuit/emergency blips use `alternateFlash: true`: **confirmed**
- `BackupFeature.cs` size: **3,814 lines**

## Headline verdict

**Not production-ready.** Three systemic root causes dominate, and they explain the project's crash history:

1. **Cross-feature dual ownership** — Backup/Pursuit/Assist/Response/TrafficStop/Stationary hand cruisers and officers between each other through non-atomic `Find → Reserve → Adopt` sequences. Multiple concrete races found.
2. **Missing LSPDFR pursuit disenrollment on teardown** — only interactive dismissal paths call `Functions.RemovePedFromPursuit`; purge/stop/dispose paths across every feature delete/clear peds still enrolled in pursuit AI → the documented `TaskCopChasePedAdv.cs:1515` NRE (the `0xe0434352` crash).
3. **Fragile, non-cooperative teardown** — cleanup runs as one sequential try-block (one throw aborts the rest), fibers are stopped with `GameFiber.Abort()`/`Thread.Abort` mid native/model-load, and untracked dispatch/child fibers re-add entities after purge.

Plus release-engineering gaps that make a "0 bugs" guarantee unverifiable: the build exits 0 without producing `Cadence.dll`, the mandated `validate_natives.py` gate is never run, version strings drift across five artifacts, and there are no tests and no CI.

See `00-MASTER-production-readiness-audit.md` for the complete, itemized list and the remediation roadmap.

---

*Generated 2026-10-15 as a read-only audit. No source, configuration, or build files were modified.*
