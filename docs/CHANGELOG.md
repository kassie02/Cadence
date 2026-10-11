# Cadence - Changelog

All notable changes to the Cadence project are documented in this file.

---

## [1.0.1] - 2026-10-10

### Enhanced Logging & In-Game Debugging System
- **Dedicated Logs Directory (`plugins/LSPDFR/Cadence/Logs/`)**:
  - Automatically writes clean, structured session logs to `Cadence.log`.
  - Automatic session archiving: moves previous session logs to `Cadence_Archive_yyyyMMdd_HHmmss.log`.
  - Automatic retention management: keeps the last 10 session logs and purges older archives.
  - Automatically migrates and cleans up legacy loose log files.
- **High-Performance Asynchronous Non-Blocking Worker**:
  - Bounded thread-safe queue (`ConcurrentQueue<string>`) with background worker thread running at `BelowNormal` priority.
  - Guarantees 0ms disk I/O latency on RagePluginHook game fibers, preventing micro-stutters or frame drops.
  - Built-in memory watchdog discarding excess debug entries if queue exceeds threshold under edge conditions.
- **Configurable `Debug = true` Setting**:
  - Added `Debug = true` (defaulted to `true` for testing/calibration) in `Cadence.ini` and `Settings.cs`.
  - Supports `Debug`, `EnableDebugLogging`, and `DebugLogging` keys.
- **Granular Debug Telemetry Across All Subsystems**:
  - Full configuration dump upon plugin boot.
  - Ultimate Backup multi-stage detection logs (AppDomain, LSPDFR API, file check).
  - Detailed spawner telemetry across Patrol, Traffic Stops, Stationary Radar, Code 3 Runs, Pursuit Drafting, and Threat Defense.
  - Full entity metadata logged (cruiser model, handles, officer models, civilian models, scenario types).
  - Watchdog despawn reasons and active count limits logged.
- **Maintained 100% Ponytail Decoupling**:
  - All 6 feature strands remain 100% independent and isolated with 0 cross-dependencies.

---

## [1.0.0] - 2026-10-10

### Initial Production Release

#### 🌟 Architecture: The "Ponytail" Delete-Friendly Model
- **Strict Single-File Modular Isolation**: Every single gameplay feature is completely self-contained within its own `.cs` source file located under `src/Features/`.
- **Zero Cross-Feature Dependencies**: No feature imports, references, or queries another feature.
- **Delete-Friendly Modularity**: Any user or developer can permanently remove any feature by deleting its single source file and removing its one-line registration in `Main.cs`. The solution will continue to compile cleanly with 0 warnings and 0 errors.
- **Thin "Scrunchie" Entry Point**: `Main.cs` acts strictly as an organizational orchestrator for plugin lifecycle events (`OnDutyStateChanged`, `Finally`) without housing monolithic gameplay logic.

---

#### 🚔 Ambient World Presence
- **Feature 1: Ambient Cruising Patrol (`PatrolFeature.cs`)**
  - Organic cruising AI utilizing native driving flags (`786603`) and natural speeds (16–20 m/s).
  - Safe road node locator generating cruisers 180m–350m from the player to prevent pop-in.
  - Integration with Ultimate Backup and native EUP fallback for jurisdiction-accurate vehicles and uniforms.
  - Continuous despawn watchdog cleaning up units beyond 450m or off-screen.
  - Navy Blue radar blip (`Sprite 56`, `Color 38`).
- **Feature 2: 1803 Roadside Traffic Stops (`TrafficStopFeature.cs`)**
  - Native RPH port of the popular 1803 ambient traffic stop system (no ScriptHookVDotNet dependency).
  - Curated shoulder pullover database in `ZoneSpawnData.cs` covering over 40 highway and rural locations.
  - Muted siren protocol keeping emergency lightbars active while eliminating repetitive audio chirping.
  - 3 authentic pullover scenarios:
    1. *Routine Citation*: Officer approaches with clipboard/notepad (`prop_notepad_01`) and pen.
    2. *Driver Pat-Down*: Officer conducts hands-on search against cruiser hood.
    3. *K9 Search*: Officer and German Shepherd (`a_c_shepherd`) conduct 4-point perimeter chassis vehicle sniff.
  - Smooth release phase with proper traffic merging and clean entity dismissal.
  - Yellow radar blip (`Sprite 56`, `Color 4`).
- **Feature 3: Stationary Units & Radar Speed Traps (`StationaryFeature.cs`)**
  - Deploys parked cruisers into turnouts, highway medians, and parking lots (120m–300m away).
  - 5 immersive idle activities: In-car radar watch, hood lean (`WORLD_HUMAN_COP_IDLES`), coffee cup prop (`ng_proc_coffee_01a`), phone call (`WORLD_HUMAN_STAND_MOBILE`), and lidar binoculars (`WORLD_HUMAN_BINOCULARS`).
  - Tactical remount: officers safely holster/drop props, enter their cruiser, and merge into traffic upon timeout or despawn.
  - Mint Green radar blip (`Sprite 56`, `Color 2`).

---

#### 🚨 Tactical & Dynamic AI
- **Feature 4A: Background 911 Emergency Runs (`ResponseFeature.cs`)**
  - Periodically dispatches ambient cruisers Code 2 or Code 3 to distant road nodes (400m–800m away) with emergency lights, sirens, and rapid navigation.
  - Flashing Red radar blip (`Sprite 56`, `Color 1`, 600ms pulse).
- **Feature 4B: Rejected Callout Takeover (`ResponseFeature.cs`)**
  - Subscribes to LSPDFR `Events.OnCalloutNotAccepted`.
  - Dispatches nearest ambient Cadence unit Code 3 to handle declined/timed-out callouts.
  - Operates completely silently without intrusive screen banners or radio chimes.
- **Feature 5: Ambient Pursuit Drafting (`PursuitFeature.cs`)**
  - Proactively queries `Functions.GetActivePursuit()`.
  - Seamlessly drafts ambient cruisers into active pursuits up to `MaxPursuitUnits`, enrolling officers via `Functions.AddCopToPursuit`.
  - Graceful Code 4 stand-down releases drafted units back to ambient cruising once the pursuit concludes.
  - Flashing Orange radar blip (`Sprite 56`, `Color 17`, 400ms pulse).
- **Feature 6: Nearby Threat Reaction / Combat Defense (`AssistFeature.cs`)**
  - Monitors 60m–90m radius for active armed suspects firing weapons or endangering officers.
  - Cruisers halt, engage handbrakes, activate emergency lightbars, and dismount.
  - Officers draw sidearms (`WEAPON_COMBATPISTOL` / `WEAPON_PISTOL`), take tactical cover, and neutralize hostiles.
  - Flashing Dark Orange radar blip (`Sprite 56`, `Color 49`, 300ms pulse).
  - Automatically holsters, re-mounts, and resumes patrol once threats are eliminated.

---

#### 🛡️ Stability & Infrastructure
- **Universal Radar Blip Engine (`BlipService.cs`)**:
  - Implements an atomic 4-step zero-leak destructor protocol preventing blip ghosting or minimap residue.
  - Supports dynamic blip switching (Patrol -> Emergency -> Pursuit -> Stand-down).
- **In-Game Dashboard Menu (`CadenceMenu.cs`)**:
  - Powered by RAGENativeUI.
  - Hotkey configurable in `Cadence.ini` (default `F8`, with `0` / `None` / `Disabled` disabling options).
  - Live telemetry dashboard tracking active entities across all features.
  - Real-time checkboxes to enable/disable features on the fly.
  - Instant spawn testing buttons for all unit types.
  - Emergency reset via `[Purge All Cadence Units]`.
  - `[Reload Cadence.ini]` configuration reloading without game restart.
- **World & Safety Utilities (`WorldUtils.cs`, `NativeTasks.cs`)**:
  - Subterranean and Z-coordinate validation preventing ground clipping or fall-throughs.
  - Thread-safe animation and model streaming handlers.
- **Build & CI Pipeline**:
  - Classic MSBuild targeting .NET Framework 4.8 / C# 10 / x64.
  - Automated build scripts (`dist\build.bat`, `scripts\build.bat`, `scripts\push_dev.bat`, `scripts\promote_to_main.bat`).
  - Zero warnings, zero errors under `/p:TreatWarningsAsErrors=true`.
