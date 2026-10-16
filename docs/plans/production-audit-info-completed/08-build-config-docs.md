# Cadence — Audit F: Build System, Configuration, Packaging, Deployment, Docs, Repo Hygiene

**Auditor scope:** Build/config/packaging/deployment/docs/repo-hygiene (NOT C# game logic).
**Workspace:** `C:\Users\User\Pictures\test-noedit\Cadence`
**Mode:** Read-only audit. No workspace files modified. Only `docs/cadence_workspace_handoff_2026-10-12.md` etc. inspected.
**Git branch:** `dev` (remote `origin/dev`, `origin/main` exist). Working tree has ~20 modified files uncommitted.
**Date of audit context:** 2026-10-15 (per CHANGELOG latest).

---

## Files covered (read in full)

- `CODING_STANDARDS.md` (Build Quality §, native standards, blip protocol)
- `src/Cadence.csproj`
- `src/Cadence.sln`
- `src/Cadence.ini` (config template)
- `src/Properties/AssemblyInfo.cs`
- `src/Core/Settings.cs` (read to compare ini→code key mapping; settings are build/config surface)
- `dist/build.bat`
- `scripts/promote_to_main.bat`, `scripts/push_dev.bat`
- `scripts/validate_natives.py` (+ executed; exit code 0 confirmed)
- `.gitignore`
- `README.md`, `docs/README.md`
- `docs/CHANGELOG.md`
- `future-features.md`, `docs/future-features.md`
- `docs/cadence_workspace_handoff_2026-10-12.md`
- `dist/Cadence_v1.1.0.zip` (archive table-of-contents inspected)
- `git ls-files`, `git status`, `git log`, `git branch`, `git check-ignore`
- File-size inventory, `grep` for machine paths/secrets, `glob` for missing infra files

---

## Directory tree (excluding `bin/`, `obj/`, `.git/`)

```
Cadence/
├── .gitignore
├── CODING_STANDARDS.md
├── README.md                       # v1.1.0 (tracked; also modified in worktree)
├── future-features.md              # DUPLICATE of docs/future-features.md (499 lines, identical)
├── dist/
│   ├── build.bat
│   └── Cadence_v1.1.0.zip          # untracked (gitignored via dist/*.zip), 311 KB
├── docs/
│   ├── README.md                   # near-duplicate of root README (diverged)
│   ├── CHANGELOG.md                # latest entry [1.1.9]
│   ├── future-features.md          # DUPLICATE of root future-features.md
│   └── cadence_workspace_handoff_2026-10-12.md   # stale scratch, personal absolute paths
├── scripts/
│   ├── promote_to_main.bat
│   ├── push_dev.bat
│   └── validate_natives.py
└── src/
    ├── Cadence.csproj
    ├── Cadence.sln
    ├── Cadence.ini
    ├── Audio/
    │   └── PANIC_BUTTON.wav        # 274,776 bytes — TRACKED (binary)
    ├── Common/{InputHelper,NativeTasks,WorldUtils}.cs
    ├── Core/{Logger,Main,Settings}.cs
    ├── Data/ZoneSpawnData.cs
    ├── Features/{Assist,Backup,Patrol,Pursuit,Response,Stationary,TrafficStop}Feature.cs
    ├── lib/
    │   ├── LSPD First Response.dll  # 5,755,904 bytes — TRACKED (3rd-party binary)
    │   ├── UltimateBackup.dll       # 1,750,016 bytes — TRACKED (3rd-party binary)
    │   ├── RAGENativeUI.dll         #   291,328 bytes — TRACKED (3rd-party binary)
    │   └── RagePluginHook.dll       #   219,648 bytes — TRACKED (3rd-party binary)
    ├── Properties/AssemblyInfo.cs
    ├── Services/{BlipService,EupService,UltimateBackupService}.cs
    └── UI/CadenceMenu.cs

MISSING (searched, none found): LICENSE, CONTRIBUTING.md, .editorconfig,
.gitattributes, .github/ (no CI), tests project, packages.config, Directory.Build.props,
*.ruleset, global.json, nuget.config
```

Repo size note: `src/lib/*.dll` total ≈ **8.0 MB** of committed third-party binaries; `PANIC_BUTTON.wav` another 275 KB.

---

## Severity-ranked findings

### CRITICAL

---

**F1 — No LICENSE file; third-party copyrighted DLLs committed to git**
- **Severity:** Critical
- **Category:** Repo hygiene / Legal
- **Files:** repository root (no `LICENSE`); `src/lib/RagePluginHook.dll`, `src/lib/LSPD First Response.dll`, `src/lib/RAGENativeUI.dll`, `src/lib/UltimateBackup.dll` (all tracked per `git ls-files`)
- **Description:** The project redistributes/commits four third-party, closed-source binaries without any license file, attribution, or acquisition instructions. `README.md §9` only says "Built on top of the RagePluginHook API and LSPDFR SDK". There is no license governing Cadence's own source either.
- **Why it matters:** Publishes copyrighted redistributables (RPH/LSPDFR/UB/RAGENativeUI) without permission or attribution, and gives downstream users no license terms. Blocks any "production-quality" public release; can trigger DMCA/removal and clear legal exposure.
- **Recommendation:** Add a `LICENSE` (choose one). Remove third-party binaries from version control (add `src/lib/*.dll` to `.gitignore`, `git rm --cached`) and document how to obtain/point at them (a `lib/README.md` with download links + expected versions). Confirm redistribution rights for each DLL; if not granted, never bundle/commit them. Add attribution/NOTICE.

---

**F2 — Version numbers drift across every artifact; no single source of truth**
- **Severity:** Critical
- **Category:** Configuration / Release consistency
- **Files:**
  - `src/Properties/AssemblyInfo.cs:16-17` → `1.1.0.0`
  - `dist/build.bat:82,152,157,160,171` → `Cadence_v1.1.0.zip`
  - `README.md:1` → `# Cadence (v1.1.0)`
  - `docs/README.md:1` → `v1.1.0`
  - `docs/CHANGELOG.md:7` → latest entry `[1.1.9] - 2026-10-15`
  - `docs/cadence_workspace_handoff_2026-10-12.md:1` → `v1.1.1`; `:64` zip = `Cadence_v1.1.0.zip`
- **Description:** The changelog has advanced to 1.1.9 while the assembly, packaging script, and both READMEs still say 1.1.0, and the handoff doc says 1.1.1. The version string is hardcoded in **five** separate places in `build.bat` alone.
- **Why it matters:** Users cannot tell what they actually have; bug reports become untriageable; the "release" zip name does not reflect the shipped code. Directly contradicts the "zero-bug production release" goal.
- **Recommendation:** Single-source the version in `Directory.Build.props` (or AssemblyInfo only) and feed `build.bat`/packaging from it (e.g., `/p:Version=` and read the built assembly via PowerShell). Bump AssemblyInfo + CHANGELOG + README together in one commit; add a CI check that they match.

---

**F3 — Build "succeeds" (exit 0) even when `Cadence.dll` is not produced/packaged**
- **Severity:** Critical
- **Category:** Packaging
- **Files:** `dist/build.bat:89-99`, `:151-162`
- **Description:** If `BUILT_DLL` cannot be found (path change, clean checkout, build output moved), the script prints `[WARN] Could not find compiled Cadence.dll to package.` and continues to create and report `Cadence_v1.1.0.zip` successfully (`[BUILD SUCCESS]`). The archive is then shipped **without the actual plugin binary** while all checks report pass.
- **Why it matters:** Produces a silently broken release artifact that users will install; the failure is invisible until runtime.
- **Recommendation:** Treat a missing DLL as a hard error: `echo [ERROR] ... & exit /b 1`. After packaging, assert the archive contains `plugins/LSPDFR/Cadence.dll` (e.g., `tar -tf ... | findstr /i Cadence.dll`) and fail otherwise.

---

**F4 — The mandated native-validation gate is never executed by any automated process and is easily fooled**
- **Severity:** Critical
- **Category:** Build process / Validation blind spots
- **Files:** `CODING_STANDARDS.md:117` (requires `python scripts/validate_natives.py` exit 0 before every release); `scripts/validate_natives.py`; `dist/build.bat` (no invocation)
- **Description:** `CODING_STANDARDS.md` mandates the validator pass with exit 0 before release, but `build.bat` never calls it, there is no CI, and `promote_to_main.bat` does not call it. I executed it: it returns exit 0 while scanning only 20 files / 184 call sites. Its checks are largely hardcoded to single symbols and contain documented checks that are **not implemented** (see F27).
- **Why it matters:** The one continuous-integration-style safeguard the project claims is (a) manual, (b) unenforced, and (c) has large blind spots. Any real native regression can pass with exit 0.
- **Recommendation:** Wire `validate_natives.py` into `build.bat` immediately after msbuild (fail the build on non-zero), and into CI. Expand the ruleset (see F27). Add an explicit "validation required for release" checklist in `promote_to_main.bat`.

---

### HIGH

---

**F5 — README documents keybind INI keys under the wrong section (`[Backup]` instead of `[Keybinds]`)**
- **Severity:** High
- **Category:** Documentation correctness / Config
- **Files:** `README.md:159-162`, `docs/README.md:156-159` vs `src/Core/Settings.cs:358-368` and `src/Cadence.ini:32-62`
- **Description:** The Keybindings Reference Table lists `PanicButtonKey`, `GroupBackupKey`, `BackupMenuKey`, and `DismissBackupKey` as `[Backup]` settings. The parser only recognizes these keys under `case "keybinds"` (`Settings.cs:358`), and the actual `[Backup]` section comment (`Cadence.ini:217`) explicitly says keybinds live in `[Keybinds]`. `docs/README.md` repeats the same error.
- **Why it matters:** A user who edits the keybind under `[Backup]` as documented will have their change **silently ignored**, producing "the mod is broken / keybind won't change" reports.
- **Recommendation:** Fix both READMEs to show `[Keybinds]` (or, defensively, make the parser also accept the keys under `[Backup]`). Regenerate docs from a single source.

---

**F6 — No CI at all**
- **Severity:** High
- **Category:** Missing engineering infrastructure
- **Files:** `.github/` (absent), no `.yml`/`.yaml` anywhere
- **Description:** No automated build, no validator run, no artifact publishing, no tag-on-release.
- **Why it matters:** Every quality gate is manual and human-dependent; the release process cannot be trusted or reproduced.
- **Recommendation:** Add a GitHub Actions workflow: restore toolchain → `msbuild /p:Configuration=Release /p:Platform=x64 /p:TreatWarningsAsErrors=true` → `python scripts/validate_natives.py` → upload `Cadence_vX.Y.Z.zip` artifact. Fail on non-zero from any step.

---

**F7 — No tests of any kind**
- **Severity:** High
- **Category:** Missing engineering infrastructure
- **Files:** repository (no test project; only 20 source files)
- **Description:** No unit tests, no settings-parser tests, no packaging smoke test. The only automated verification is the static validator.
- **Why it matters:** Pure C# logic (Settings parsing/clamping, preset application, key parsing, distance-invariant enforcement) is testable without the game engine but is completely unprotected. Regressions in config handling ship silently (see F5/F14/F15).
- **Recommendation:** Add a `tests/Cadence.Tests` project targeting .NET Framework 4.8 covering `Settings.ParseEntry`/`ParseKey`/`ParseRange`/`ApplyPreset`, then a `Settings.Load(tempIni)` + assert matrix. Add a packaging test that builds and asserts the zip contents.

---

**F8 — Third-party binaries tracked; `.gitignore` does not exclude `lib/*.dll` or `*.wav`; `push_dev.bat` uses `git add -A`**
- **Severity:** High
- **Category:** Repo hygiene
- **Files:** `.gitignore:1-20`; `scripts/push_dev.bat:64` (`git add -A`)
- **Description:** `RagePluginHook.dll`, `LSPD First Response.dll`, `RAGENativeUI.dll`, `UltimateBackup.dll` (≈8 MB) and `PANIC_BUTTON.wav` are tracked. `.gitignore` has no rule for `src/lib/` or `*.dll`/`*.wav`, and `push_dev.bat` stages everything with `git add -A`. Combined with F1 this both bloats the repo and risks committing copyrighted/bin files in future.
- **Why it matters:** Large binary history is permanent and unremovable without history rewrite; licensing risk compounds each push.
- **Recommendation:** Add `src/lib/` (or `*.dll`) and large audio to `.gitignore`, `git rm --cached` them, and replace `git add -A` with explicit path staging or at least ensure ignores are correct before use. Consider Git LFS only if assets must be versioned.

---

**F9 — Documentation is duplicated AND internally contradictory**
- **Severity:** High
- **Category:** Repo hygiene / Documentation correctness
- **Files:** `README.md` vs `docs/README.md`; `future-features.md` vs `docs/future-features.md`
- **Description:** `future-features.md` is a byte-identical 499-line duplicate of `docs/future-features.md`. The two READMEs have diverged: root says patrol speeds `12–21 m/s` (`README.md:25`) while docs says `16–20 m/s` (`docs/README.md:25`); root Feature 7 and docs Feature 7 describe different state machines; docs `README.md:67` still claims pursuit driver "ability 100.0" and "aggressiveness 0.6", which contradicts `CODING_STANDARDS.md` clamp rules and the CHANGELOG 1.1.2/1.1.7 entries.
- **Why it matters:** Two "sources of truth" that disagree guarantee stale/misleading user docs. Any downstream citation is unreliable.
- **Recommendation:** Keep exactly one canonical README (repo root) and one future-features doc; delete the duplicates and fix intra-doc links (`docs/README.md` cross-links to `../future-features.md`). Add a doc-consistency check for version/speed/color claims if feasible.

---

**F10 — No `.editorconfig` / Roslyn analyzer configuration**
- **Severity:** High
- **Category:** Missing engineering infrastructure
- **Files:** repository (absent)
- **Description:** The only style enforcement is `TreatWarningsAsErrors=true` in `Release|x64` (`Cadence.csproj:37`). There is no `.editorconfig`, no `.ruleset`, no `Directory.Build.props`, no analyzer package (e.g., `Microsoft.CodeAnalysis.NetAnalyzers` / `Roslynator`).
- **Why it matters:** "Zero warnings" only enforces the compiler's own warning set at level 4; correctness/performance/style rules (CA rules, IDE rules) are not enforced, and there's no shared editor behavior across machines. Contributes directly to regression risk in a large single-file feature (BackupFeature.cs is 182 KB).
- **Recommendation:** Add `.editorconfig` + a `Directory.Build.props` enabling `EnableNETAnalyzers`, `AnalysisLevel`, `EnforceCodeStyleInBuild`, and a curated ruleset; fail build on configured violations.

---

**F11 — `FrameworkPathOverride` hardcodes an absolute runtime path and bypasses .NET 4.8 reference assemblies**
- **Severity:** High
- **Category:** Build reproducibility
- **Files:** `src/Cadence.csproj:17`
- **Description:** `<FrameworkPathOverride>C:\Windows\Microsoft.NET\Framework64\v4.0.30319</FrameworkPathOverride>` is set unconditionally. This forces the compiler to resolve `mscorlib`/framework from the **.NET 4.0 runtime** folder rather than the .NET Framework 4.8 targeting/reference assemblies, and it is a machine-absolute path.
- **Why it matters:** Produces a build that is not exercised against the 4.8 reference assemblies, can hide/false-positive API availability, and makes builds machine-dependent. It is a common but fragile workaround for missing developer packs.
- **Recommendation:** Remove it and install the .NET Framework 4.8 Targeting Pack (or Developer Pack) on build agents so `<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>` resolves correctly. If a workaround is truly required, gate it behind an optional condition and document it.

---

**F12 — Shipping defaults enable verbose Debug logging and the Chaos preset; both contradict the stated "production" posture**
- **Severity:** High
- **Category:** Configuration / Documentation correctness
- **Files:** `src/Cadence.ini:20` (`Debug = true`), `:29-30` (`Preset = Chaos`, `ChaosMode = true`); `src/Core/Settings.cs:17-18` (defaults true/Chaos); `README.md:212` (says set `Debug=false` for silent gameplay), `README.md:181` (menu default "Realistic (Default)")
- **Description:** The distributed ini turns on full telemetry by default and selects the highest-density Chaos preset, yet README tells users to run with Debug off and describes Realistic as the default. `Settings.cs` defaults also equal Chaos.
- **Why it matters:** A "golden master" release should not ship a debug-density/debug-logging default; it will perform worse and generate noise/log files, and it directly conflicts with the documentation.
- **Recommendation:** Decide the intended production default (likely `Debug=false`, `Preset=Realistic`), set it consistently in `Cadence.ini`, `Settings.cs`, menus, and docs. Add a release-build sanity check.

---

**F13 — README menu defaults and preset description contradict the ini/code defaults**
- **Severity:** High
- **Category:** Documentation correctness
- **Files:** `README.md:181-185` (Realistic default; sliders "default: 4 / 2 / 2 / 2"), `docs/README.md:178-182` (same) vs `src/Cadence.ini:88-97` and `src/Core/Settings.cs:104-111` (MaxPatrol 8, Stops 4, Stationary 4, Emergency 4, Pursuit 6)
- **Description:** Documented menu defaults are off by ~2x from the actual Chaos defaults in the shipped ini and code. The README even names the menu preset default "Realistic (Default)" while the code ships Chaos.
- **Why it matters:** Documentation is provably wrong about the shipped product; users cannot reconcile the menu with the docs.
- **Recommendation:** Reconcile and single-source the defaults; document them from the code/ini rather than hand-writing tables.

---

### MEDIUM

---

**F14 — Duplicate/aliased ini keys: last-wins silently**
- **Severity:** Medium
- **Category:** Configuration footgun
- **Files:** `src/Cadence.ini:9-10` (`EnablePlugin` + `PluginActive`), `:29-30` (`Preset` + `ChaosMode`), `:202-203` (`Fallback` + `AllowFallback`); `src/Core/Settings.cs:344,347-355,465`
- **Description:** Three logical settings are each represented by two keys mapping to the same field. Because `Settings.Load` parses line-by-line, the later line wins with no warning. `Preset` and `ChaosMode` both trigger preset application.
- **Why it matters:** A user setting `EnablePlugin = false` but leaving `PluginActive = true` (or editing one of the ambiguous pairs) can get an unexpected result with no diagnostic.
- **Recommendation:** Keep one canonical key per setting; make the aliases deprecated-but-warned, or remove them from the template. If both must exist, detect disagreement and log a warning naming the winning value.

---

**F15 — Invalid ini values silently fall back to defaults; no user-visible validation**
- **Severity:** Medium
- **Category:** Configuration validation
- **Files:** `src/Core/Settings.cs:561-579` (`ParseBool`/`ParseInt`/`ParseFloat` return default on failure), `:317-321` (silent clamps), `:330-333` (only logs on exception)
- **Description:** Typo'd or out-of-range values are silently coerced (e.g., `MaxPatrolUnits = banana` → default; `DespawnDistance = 10` → clamped to 150 with no message). There is no consolidated "config report" surfaced to the user.
- **Why it matters:** Users believe their config took effect when it did not — the top cause of "mod isn't working" reports.
- **Recommendation:** Track unknown keys and rejected values during parse; log a single warning listing them and (optionally) show an in-game notification. Add a `Settings.Validate()` report. Cover with unit tests (F7).

---

**F16 — Log retention/rotation is not user-configurable despite being a documented feature**
- **Severity:** Medium
- **Category:** Configuration completeness
- **Files:** `src/Cadence.ini` (only `EnableFileLogging`, `Debug`), `README.md:211` (claims 10-session rolling retention), CHANGELOG/git log mention "10MB mid-session rotation"
- **Description:** The docs advertise session archiving and retention/rotation limits, but no ini key exposes retention count, archive enablement, or max log size. Users cannot cap disk usage.
- **Why it matters:** On long sessions the log directory grows; users have no supported knob. Docs promise configurability that doesn't exist.
- **Recommendation:** Add a `[Logging]` section (`MaxArchiveSessions`, `MaxLogSizeMB`, `ArchiveLogs`) read by `Settings.cs`/`Logger`, or correct the docs. Document defaults.

---

**F17 — Release version hardcoded in five places in `build.bat` (bump hazard)**
- **Severity:** Medium
- **Category:** Packaging
- **Files:** `dist/build.bat:82,152,157,160,171` (plus `:116`, `:171` README/echo text)
- **Description:** The zip filename and banner text embed `1.1.0` literally. Any release requires editing many lines, guaranteeing future drift (see F2).
- **Why it matters:** Human error will mismatch archive name vs assembly version.
- **Recommendation:** Derive the version once (parameter, `Directory.Build.props`, or read `Cadence.dll` FileVersion via PowerShell) and reuse the variable.

---

**F18 — Promotion/deploy scripts have no build gate, no dirty-tree check, and stage everything**
- **Severity:** Medium
- **Category:** Deployment safety
- **Files:** `scripts/promote_to_main.bat:19-42`, `scripts/push_dev.bat:64-74`
- **Description:** `promote_to_main.bat` merges `dev`→`main` and pushes with only a "did you test in GTA?" prompt; it never builds, never runs the validator, never tags a release, and does not check for a dirty working tree before `git checkout main` (uncommitted changes can block or carry over). `push_dev.bat` stages with `git add -A` and pushes without any secret/artifact scan. `push_dev.bat:54` relies on deprecated `wmic os get localdatetime`.
- **Why it matters:** A release can be merged/pushed to `main` with a broken build; new binaries/artifacts can be swept into commits.
- **Recommendation:** In `promote_to_main.bat`: verify clean tree, run `dist\build.bat --nopause` + `validate_natives.py`, require success, create an annotated tag (`vX.Y.Z`), then push. Replace `wmic` with `PowerShell Get-Date`. Avoid `git add -A`.

---

**F19 — Machine-specific deployment paths committed; the referenced deploy tool/script is absent from the repo**
- **Severity:** Medium
- **Category:** Deployment / Repo hygiene / Privacy
- **Files:** `docs/cadence_workspace_handoff_2026-10-12.md:5-7,65`
- **Description:** The committed handoff doc contains a personal absolute workspace path, a personal reference-codebase path, the Steam deployment target `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V`, and references `sync_and_stage.bat C` "via ExtractorTool", neither of which exists in the repo. There is no scripted install/deploy path.
- **Why it matters:** Leaks developer machine layout into a public repo; documents a deployment method no one else can use or reproduce.
- **Recommendation:** Remove/scrub the handoff scratch file (or move it out of `docs/` into an untracked notes area). Provide a real, portable deploy script (parameterized GTA path, validation, backup of user ini) if deployment automation is desired.

---

**F20 — Archive `README.txt` omits requirements/dependencies; Ultimate Backup is effectively required but not stated as such**
- **Severity:** Medium
- **Category:** Packaging / Documentation
- **Files:** `dist/build.bat:114-149` (generated README.txt), `src/Cadence.ini:202-203` (`Fallback=false`), `README.md:218-222` (requirements list omits Ultimate Backup)
- **Description:** The packaged README.txt lists only installation/controls and never states RagePluginHook, LSPDFR, RAGENativeUI, or Ultimate Backup requirements. With `Fallback=false` by default, if Ultimate Backup is missing or not ready Cadence spawns **nothing**; README requirements list does not call out UB even as strongly recommended. RAGENativeUI is also required for the menus but only listed in the repo README, not the archive.
- **Why it matters:** Users install the zip, lack UB, and conclude the mod is broken; support burden.
- **Recommendation:** Include a clear Requirements/Dependencies section in the generated README.txt (RPH, LSPDFR, RAGENativeUI, recommended Ultimate Backup + what happens without it). Add an in-game startup warning (already partially present) and align docs.

---

**F21 — Wildcard `<Compile Include="**\*.cs" />` silently compiles anything under `src/`**
- **Severity:** Medium
- **Category:** Build reproducibility
- **Files:** `src/Cadence.csproj:64-67`
- **Description:** The project compiles all `**/*.cs` except `obj/**;bin/**`. Any scratch, generated, or stray `.cs` file dropped anywhere under `src/` silently enters the build.
- **Why it matters:** Non-reproducible builds (local stray files), harder code review, and the "Ponytail delete-friendly" guarantee is only implicit. Also, `validate_natives.py` `rglob("*.cs")` over `src` will scan `src/bin`/`src/obj` generated files after a build.
- **Recommendation:** Use explicit `<Compile>` items (or at least a narrow glob), and/or add a `Directory.Build.props` `<Compile Remove>` for tooling. Make `validate_natives.py` skip `bin`/`obj`.

---

**F22 — No dependency version pinning/NuGet; lib DLL versions are unknown and untracked**
- **Severity:** Medium
- **Category:** Build reproducibility
- **Files:** `src/Cadence.csproj:47-62`, `src/lib/*.dll`
- **Description:** References are `HintPath` to committed DLLs (`Private=False`), with `<ResolveNuGetPackages>false</ResolveNuGetPackages>` and no `packages.config`. Nothing records which versions of RPH/LSPDFR/RAGENativeUI/UltimateBackup the project targets.
- **Why it matters:** Cannot reproduce or reason about API compatibility; upgrading a referenced DLL is invisible in history beyond a binary blob change.
- **Recommendation:** Document required versions in a `lib/README` (and README Requirements); if any of these exist on NuGet feed/local feed, prefer package references for pinning. At minimum record file versions/hashes.

---

**F23 — CHANGELOG contains internal contradictions and references non-existent scripts**
- **Severity:** Medium
- **Category:** Documentation correctness
- **Files:** `docs/CHANGELOG.md:398` (cites `scripts\build.bat`, which does not exist), `:262` (blip `Color 66` vs current palette `Color 38`), `:67`/`:137`/`:168` (driver ability `100.0f`), `:123` (claims "Synchronized ... to 1.1.0.0" while later entries go to 1.1.9)
- **Description:** The changelog describes assets/settings that no longer match the code or the standards, and points to a build script that isn't in the repo.
- **Why it matters:** A changelog is a release contract; contradictions erode trust and mislead bug triage.
- **Recommendation:** Reconcile CHANGELOG entries with the final code/standards; remove references to missing files; add the version/date for the current release at the top once frozen.

---

### LOW

---

**F24 — `.gitignore` gaps**
- **Severity:** Low
- **Category:** Repo hygiene
- **Files:** `.gitignore:1-20`
- **Description:** Ignores `bin/`, `obj/`, `*.user`, `.vs/`, `*.log`, `dist/*.zip`, `__pycache__/`. Missing: `*.pdb`, `*.dll` (see F8), `*.wav` (audio assets reconsidered), `dist/_staging/` (transient), `*.bak`, `.idea/`, and `TestResults/`.
- **Why it matters:** Minor risk of committing build outputs or editor cruft; the staging dir is only transiently present.
- **Recommendation:** Add the missing patterns; decide explicitly whether audio is source or artifact.

---

**F25 — Stale personal handoff document committed under `docs/`**
- **Severity:** Low
- **Category:** Repo hygiene
- **Files:** `docs/cadence_workspace_handoff_2026-10-12.md`
- **Description:** Internal scratch handoff with personal paths (see F19), still in the docs tree and likely rendered as user-facing documentation.
- **Recommendation:** Remove or relocate to an untracked notes folder / wiki.

---

**F26 — `validate_natives.py` blind spots and unimplemented claimed checks**
- **Severity:** Low→Medium
- **Category:** Validation capability
- **Files:** `scripts/validate_natives.py`
- **Description:** Despite a docstring claiming rules [A]–[H], effective coverage is narrow:
  - Rule **B** docstring says "Required: 0xA2719263" but the code only flags `0xA2719248`; it never verifies the correct hash is used.
  - Rule **D** only flags the single literal `GetClosestVehicleNodeWithHeading`; all other PascalCase native names are not checked (only `NativeFunction.Natives.<name>` casing via rule F).
  - Rule **F** only inspects `NativeFunction.Natives.<name>`; raw-hex calls made via other APIs (`CallByHash`, `Function.Call`, x-prefixed without the `Natives.` accessor) are invisible.
  - Rule **E** only matches a numeric literal second argument to `SET_DRIVER_ABILITY`; values passed via variables are missed.
  - Rule **H** flags any non-line-leading-comment occurrence of `REMOVE_BLIP`, including trailing comments/strings (false positives) and misses mixed-case.
  - The docstring lists architectural invariants (zero-lock-across-yields, blip lifecycle, etc.) that are **not** implemented.
  - `rglob("*.cs")` will also scan generated `src/bin`/`src/obj` files after a build (false results).
- **Why it matters:** It can return 0 while real native/architecture violations exist (false sense of security), which is exactly the release gate the standards rely on.
- **Recommendation:** Implement the full native-name validation against the canonical native database (the repo already references the `gta5-dev` MCP / alloc8or DB in CHANGELOG 1.1.4), verify required hashes, scan all native-invocation styles, implement a lock-across-yield heuristic, and exclude `bin`/`obj`.

---

**F27 — README claims "C# 10" while project uses `LangVersion latest`**
- **Severity:** Low
- **Category:** Documentation correctness / Build reproducibility
- **Files:** `src/Cadence.csproj:15` (`<LangVersion>latest</LangVersion>`), `README.md:4`, `docs/README.md:4` ("C# 10")
- **Description:** `latest` binds to whatever toolchain builds it (C# 12/13 on current VS2022), not C# 10.
- **Why it matters:** Compiler-version-dependent behavior; docs are inaccurate.
- **Recommendation:** Pin `<LangVersion>10.0</LangVersion>` (or bump the docs); prefer a pinned version for reproducible builds.

---

**F28 — `TreatWarningsAsErrors` only in `Release|x64`; both `/p:TreatWarningsAsErrors=true` and `/warnaserror` passed**
- **Severity:** Low
- **Category:** Build config
- **Files:** `src/Cadence.csproj:37` (Release only), `dist/build.bat:69`
- **Description:** Debug builds do not enforce zero warnings (acceptable), and the build command redundantly passes both the property and `/warnaserror` (which can also promote MSBuild-level warnings).
- **Why it matters:** Minor; contributors on Debug get warnings that CI/Release treats as errors, causing surprises.
- **Recommendation:** Enforce zero warnings in a shared props file applied to all configs, or document the Debug/Release difference.

---

**F29 — No CONTRIBUTING / CODEOWNERS / SECURITY / issue+PR templates**
- **Severity:** Low
- **Category:** Missing engineering infrastructure
- **Files:** repository (absent)
- **Description:** No contributor guidance, review ownership, or security policy.
- **Recommendation:** Add lightweight `CONTRIBUTING.md`, issue/PR templates, and a `SECURITY.md` (bug/exploit reporting) appropriate to a game-plugin community project.

---

**F30 — Large volume of uncommitted work on `dev`; release integrity unclear**
- **Severity:** Low
- **Category:** Repo hygiene / Release integrity
- **Files:** `git status` (≈20 modified files incl. `docs/CHANGELOG.md`, `src/*`, `README.md`)
- **Description:** The working tree is substantially ahead of the last commit; the CHANGELOG's 1.1.9 entries may not be committed. The committed README/AssemblyInfo are still 1.1.0.
- **Why it matters:** Cannot guarantee the shipped zip corresponds to any commit; bisecting is unreliable.
- **Recommendation:** Commit/tag the frozen release; treat tags as the release source of truth.

---

## Missing infrastructure / strategic recommendations

1. **CI (highest priority).** Add `.github/workflows/build.yml` that: checks out, installs .NET 4.8 targeting pack, runs `msbuild` Release/x64 with warnings-as-errors, runs `python scripts/validate_natives.py`, then packages and uploads the zip artifact. Fail the workflow on any non-zero step. This alone closes F4/F6.
2. **Automated tests.** Add a .NET Framework 4.8 test project for `Settings` (parsing, clamping, presets, key parsing, duplicate-key precedence), plus a packaging test asserting the zip contains `plugins/LSPDFR/Cadence.dll`, `Cadence.ini`, and `Audio/PANIC_BUTTON.wav`. This closes F7 and hardens F3/F14/F15.
3. **Single source of truth for version.** `Directory.Build.props` with `<Version>`; feed `build.bat` from it; add a CI assertion that AssemblyVersion == CHANGELOG top entry == zip name. Closes F2/F17.
4. **Static analysis baseline.** Add `.editorconfig` + `Directory.Build.props` enabling `EnableNETAnalyzers`, `EnforceCodeStyleInBuild`, `AnalysisLevel`, and a curated ruleset; enforce in CI. Closes F10.
5. **Harden packaging.** Make missing-DLL a hard failure; verify archive contents; include a requirements block in the generated README.txt; derive filename from version. Closes F3/F20.
6. **Dependency governance.** Remove committed third-party DLLs from VCS, document exact versions/hashes, and add a `LICENSE`/`NOTICE`. Closes F1/F8/F22.
7. **Release/deploy safety.** Gate `promote_to_main.bat` on a green build + validator, require a clean tree, create a signed/annotated tag, and provide a portable deploy script parameterized by GTA path (with user-ini backup). Closes F18/F19.
8. **Docs hygiene.** Reduce to one README and one future-features doc, auto-/check-sync version and default values, and remove the personal handoff file. Closes F9/F13/F25.
9. **Config diagnostics.** Emit a startup config report listing unknown keys and rejected/clamped values; expose log retention settings or correct the docs. Closes F15/F16.
10. **Validation depth.** Rebuild `validate_natives.py` to actually check the full native DB and the coding-standards invariants (not single hardcoded symbols), and exclude `bin`/`obj`. Closes F26.

---

## Open questions / needs verification

1. **Does the shipped `dist/Cadence_v1.1.0.zip`'s `Cadence.dll` correspond to current `src/`?** The archive contains a `Cadence.dll` compiled at some point; its version/commit cannot be confirmed. (No build was run during this read-only audit.) Needs a controlled build+hash comparison.
2. **What are the exact required versions of RagePluginHook / LSPDFR / RAGENativeUI / UltimateBackup?** The committed DLLs carry no recorded version metadata in the repo. Verify file versions and whether they match the README's claims (RPH v1.08+, LSPDFR 0.4.9+, RAGENativeUI 1.9.2+).
3. **Is Ultimate Backup intended to be a hard dependency?** With `Fallback=false` default and no UB bundled, Cadence may spawn nothing on a UB-less install. Confirm the intended UX and document it (F20).
4. **Where does `sync_and_stage.bat` / ExtractorTool deployment live?** Referenced in the handoff but absent. Is there an out-of-repo deployment pipeline that should be brought in-repo?
5. **Is `Debug = true` and `Preset = Chaos` the intended shipping default?** Confirm intended production defaults (F12/F13).
6. **Are the third-party DLLs legally redistributable/bundleable in a public repo?** Confirm before any public release (F1).
7. **Which branch is the release branch?** `dev` is current with substantial uncommitted work; `main` exists. Confirm the release/branching policy and whether `main` must always be green (F18/F30).
8. **`ResolveNuGetPackages=false` + committed DLLs:** is there an internal NuGet/feed or is the committed-binary approach deliberate? Determines the fix path for F22.
9. **Does the validator's claimed `gta5-dev` MCP native database integration exist in this workspace?** CHANGELOG 1.1.4 claims full DB validation; the committed script does not use a DB. Clarify whether validation was performed out-of-band.
