# Cadence - Changelog

All notable changes to the Cadence project are documented in this file.

---

## [1.1.4] - 2026-10-14

### Code Audit, Native Verification & Vehicle Physics Deadlock Resolution
- **Vehicle Physics & Engine Stall Deadlock Fix (`AssistFeature.cs`, `ResponseFeature.cs`)**:
  - Resolved `BRING_VEHICLE_TO_HALT` parameter defect where `duration`/`killEngine` parameter was passed as `1`, causing GTA V's physics engine to permanently stall vehicle engines upon arrival or threat reaction.
  - Set halt duration to `0` and implemented canonical native `STOP_BRINGING_VEHICLE_TO_HALT(Cruiser)` across `FinishStandDown`, `Relinquish`, and `Dispose` routines before issuing cruising tasks, ensuring cruisers smoothly resume patrol and merge into traffic without freezing in place.
- **Blip Destructor Protocol & Standard Alignment (`CODING_STANDARDS.md`)**:
  - Corrected Step 4 in `CODING_STANDARDS.md` to remove references to the banned native `HUD::REMOVE_BLIP` (which accepts an internal engine `Blip*` pointer, causing `AccessViolationException` in RPH).
  - Aligned documentation with the battle-tested 4-step `BlipService.SafeDeleteBlip(ref Blip blip)` protocol: null check, visual flash cessation (`SET_BLIP_FLASHES` & `StopFlashing()`), managed deletion (`blip.Delete()`), and reference nullification (`blip = null`) in a `finally` block.
- **Assembly Version Alignment (`AssemblyInfo.cs`)**:
  - Synchronized `AssemblyVersion` and `AssemblyFileVersion` to `1.1.0.0` matching `build.bat` release packaging and plugin documentation.
- **Native Verification & Static Analysis**:
  - Confirmed 100% compliance across all 121 native call sites in 20 C# source files against alloc8or's GTA V native database via `gta5-dev` MCP server.
  - Zero RDR2 natives, zero raw hex hacks, and zero-warning MSBuild compilation under `/p:TreatWarningsAsErrors=true`.

---

## [1.1.3] - 2026-10-13

### Cadence Overhaul Phase 2 - Pursuit AI, Standby Scene Holding, Backup Navigation & UB Parity Menu
- **Vehicle Model Correction (`ZoneSpawnData.cs`)**:
  - Replaced invalid vehicle model hash name `"schafter"` with valid GTA V model `"schafter2"` in `ZoneSpawnData.CivilianVehicles`, eliminating invalid model hash lookup failures and spawn errors during ambient traffic generation.
- **Pursuit Driving AI & U-Turn Navigation Overhaul (`NativeTasks.cs`, `PursuitFeature.cs`)**:
  - Upgraded emergency pursuit driver AI in `ConfigureEmergencyDriver`:
    - Increased driver aggressiveness from `0.0f` to `0.6f` and driving ability to `100.0f`.
    - Enabled native chase behavior flag 8 (`SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG(driver, 8, true)`), allowing pursuit units to aggressively overtake traffic, initiate assertive pursuit maneuvers, and execute U-turns to keep up with fleeing suspects.
  - Purged conflicting ambient cruising tasks (`driver.Tasks.Clear()` and `passenger.Tasks.Clear()`) prior to calling `Functions.AddCopToPursuit()`, ensuring ambient officers drafted into pursuits immediately adopt pursuit AI without residual traffic-following behavior.
  - Harmonized default stopping radius in `TaskLongRangeEmergencyDrive` to `12.0f`.
- **Pursuit Conclusion Scene Hold & Standby Dismissal (`PursuitFeature.cs`)**:
  - Introduced `OnSceneStandby` state in `PursuitUnitSession` when an active pursuit concludes:
    - Cruisers switch to silent warning lights (sirens muted, flashing emergency lightbars remain illuminated).
    - Seated officers hold position (`VehicleManeuver.Wait`), and dismounted officers maintain perimeter security without interfering with LSPDFR's native arrest and suspect-in-car routines.
  - Implemented `ProcessStandbyDismissal`: Prompts the player (`Hold ~b~[ENTER]~s~ to dismiss pursuit scene officers`), tracking key hold duration for >= 1100ms (`_dismissHoldThresholdMs = 1100`) to prevent accidental dismissals.
  - Added graceful dismissal transition (`DismissFromScene`): Officers return to cruisers, wait for driver entry (up to 6s), deactivate lightbars, transition blips back to routine Navy Blue, cruise away smoothly at 16 m/s, and ambiently release after 15 seconds.
  - Implemented safety watchdog in `PruneAndWatchSessions`: Automatically dismisses standby units if the player moves > 250m away from the scene or after a 5-minute (300,000ms) scene inactivity timeout to prevent entity leaks.
  - Added public dispatch helpers `RequestPursuitInterceptor(isHighway)` and `RequestGroupPursuit(count)` for direct backup menu integration.
- **Panic Button & Emergency Backup Response AI (`BackupFeature.cs`)**:
  - Resolved ped freezing / stuttering: Throttled `UpdateBuddyFollowing` to an asynchronous 1300ms cadence (`_lastBuddyFollowUpdate`), preventing 60 Hz navmesh spam (`Tasks.FollowNavigationMeshToPosition`) from continuously resetting officer pathfinding. Officers within 3.5m stop moving and stand naturally.
  - Fixed cruiser circling loops: Implemented `GetRoadsideCurbPosition` using `WorldUtils.GetSafeStreetPosition` and `World.GetNextPositionOnStreet` to navigate responders to roadside/curb coordinates instead of raw player positions on sidewalks or off-road. Harmonized stopping radius to 12.0m and arrival check to 14.0m (target) / 16.0m (player) with 25m/35m deceleration arrival fallbacks.
- **Redesigned Backup Menu - Ultimate Backup `Class74.cs` Parity (`BackupFeature.cs`)**:
  - Rebuilt backup menu architecture to match Ultimate Backup's `Class74.cs` layout:
    - Top `UIMenuListItem` for **Response Mode** (`Code 3 [Emergency]`, `Code 2 [Urgent]`, and `Pursuit Backup`).
    - Dynamic menu item population based on the selected mode:
      - Normal Modes (Code 2 / Code 3): Local Police Backup, State Patrol / Highway, EMS Ambulance, Fire Rescue Service, Group Backup, Emergency Panic Button, and Dismiss All Backup Units.
      - Pursuit Backup Mode: Local Pursuit Backup, Highway Pursuit Backup, Pursuit Interceptor, Group Pursuit Backup (2 Units), and Dismiss All Pursuit Units.
    - Integrated with `PursuitFeature.RequestPursuitInterceptor` and `PursuitFeature.RequestGroupPursuit` for instant tactical reinforcement during active chases.
    - Dismiss All dispatches clean release commands to both active backup sessions and pursuit standby units.

---

## [1.1.2] - 2026-10-12

### Ambient AI Driving & Organic Backup Drafting Stabilization
- **Ambient Patrol Driving Physics Overhaul (`NativeTasks.cs`, `PatrolFeature.cs`)**:
  - Replaced erratic cruising flags `786603` (which erroneously contained bit 3 `SwerveAroundAllCars`, causing routine ambient cops to constantly swerve across double-yellow lines into oncoming traffic) with clean standard flags `447` (`0x1BF` / `VehicleDrivingFlags.Normal`). Cops now stay smoothly in their lanes and obey traffic rules.
  - Driver ability clamped to native range `1.0f` (previously passed `100.0f`, exceeding the `0.0f - 1.0f` GTA V native scale and destabilizing native driving physics).
  - Set `driver.BlockPermanentEvents = false` for routine patrol drivers, restoring GTA V's native ambient traffic brain for proper lane queuing, turning, and red-light yielding.
  - Passenger peds set to `BlockPermanentEvents = true` with `TASK_PAUSE`, eliminating steering/task collisions between dual-officer crews.
  - Tuned patrol speeds to natural traffic flow: 12–15 m/s (~27–33 mph) on surface city streets, 18–21 m/s (~40–47 mph) on highways and freeways.
  - Fixed `EnsurePatrolDriving` watchdog: checks task completion (`TaskStatus.None` or `Interrupted`) every 15s instead of resetting drivers waiting at red lights every 8s.
- **Organic Ambient Commandeering & Anti-Face Spawning (`BackupFeature.cs`)**:
  - Removed legacy Backspace test spawner (`ManageFastSpawnHints` / `FastSpawn`): previously, pressing Backspace (the standard LSPDFR/RAGENativeUI menu back/decline key) warped all en-route cruisers 30m directly in front of the player.
  - Removed artificial minimum distance exclusions: ambient cruisers and stationary officers sitting right next to the player (0m–350m) are now instantly drafted for Panic Button (`LControlKey + Z`) and Group Backup (`LControlKey + C`).
  - Added seamless `isAlreadyOnScene` handling: when a unit sitting <= 35m from the player is drafted, the plugin bypasses the 71 mph long-range driving task and forward impulse; the cruiser halts smoothly (`VehicleManeuver.Wait`), mutes siren, keeps flashing lightbars active, and transitions officers directly to tactical scene support.
  - Fallback backup spawner math overhauled: replaced inverted trigonometry with `player.GetOffsetPosition(new Vector3(sideOffset, -behindDist, 0f))`, placing backup 120m–200m behind player out of direct line of sight with an 80m minimum standoff distance.
  - Commandeering queries Cadence's own active features (`PatrolFeature`, `StationaryFeature`, `TrafficStopFeature`, `ResponseFeature`) and all nearby emergency vehicles/law enforcement peds, seamlessly supporting custom addon cruisers.
  - Staggered egress and tactical sidearm auto-draw (`WeaponCombatPistol`) for panic button responders.
- **Cross-Feature Conflict Resolution & Occupant Departure Protection**:
  - **Traffic Stop Ped Departure Protection (`TrafficStopFeature.cs`)**: Added `IsInVehicle` validation and `WarpIntoVehicle` fallback during `ReleasingVehicles` departure to guarantee civilian peds and officers are safely inside their vehicles before vehicles accelerate and merge into traffic. Also fixed `Relinquish` to warp civilian peds back into their cars if drafted while standing outside during a pat-down/sobriety check, preventing them from being left stranded on foot in traffic.
  - **Stationary Standby Remount Protection (`StationaryFeature.cs`)**: Added `IsInVehicle` validation and warp fallback before cruiser acceleration in `EnteringVehicle`, preventing rare edge cases where bumped officers got left behind while cruisers rolled away.
  - **Long-Range Response Driving & Addon Model Support (`ResponseFeature.cs`)**: Upgraded short-range `DriveToPosition` to native `TaskLongRangeEmergencyDrive`, allowing units responding to rejected callouts (>250m) to reach their scenes across the map without task cancellation. Added cross-feature relinquishing (`RelinquishCruiser`) when drafting ambient cruisers for emergency runs.
  - **Pursuit Drafting Cross-Feature Handshake (`PursuitFeature.cs`)**: Drafting an ambient cruiser into an active pursuit now cleanly calls `RelinquishCruiser` across `PatrolFeature`, `StationaryFeature`, `TrafficStopFeature`, and `ResponseFeature`. This prevents background cruising watchdogs from resetting pursuit driving AI or despawning cruisers chasing suspects beyond 450m. Expanded candidate detection to include custom addon law enforcement models.

---

## [1.1.1] - 2026-10-12

### Critical Bug Fixes & Tactical Alignment (Panic Button, Ultimate Backup EMS & Standby Positioning)
- **Panic Button Crash Resolution (`BackupFeature.cs`)**:
  - Eliminated game-crashing CLR exception (`0xe0434352`) triggered when pressing Panic Button (`LControlKey + Z` or via Lean Backup Menu).
  - Removed call to non-existent scanner audio `"UNIT_RESPONDING_DISPATCH"`.
  - Upgraded panic audio playback from synchronous `SoundPlayer.PlaySync()` to non-blocking `SoundPlayer.Play()`, preventing fiber lockups and disposal conflicts.
  - Implemented decoupled cross-feature cruiser relinquishing (`RelinquishCruiser`) across `PatrolFeature`, `StationaryFeature`, and `ResponseFeature`. Commandeered units automatically drop radar blips, release handbrakes, clean up scenario props, and unhook previous feature loops.
  - Enforced a minimum 40m distance threshold in `FindCandidateAmbientVehicle` to prevent commandeering cruisers directly adjacent to the player, which previously caused instant broken arrival transitions and sudden vehicle egress.
  - Guarded driving maneuvers in `TransitionToSceneArrival` to verify officer is seated in the vehicle before issuing vehicle halt tasks.
  - Corrected navmesh coordinate calculation in `TaskFollowPlayer` fallback to use `player.GetOffsetPosition(offset)`.
- **Strict Ultimate Backup EMS & Fire Parity (`AllowFallback = false` Enforcement)**:
  - Added dedicated UB API bindings `SpawnAmbulanceUnit` and `SpawnFireTruckUnit` calling `UBFunctions.getAmbulanceUnit` and `getFireTruckUnit`.
  - Preserved Paramedic/Firefighter AI behavior by passing `isPolice = false` in `PrepareUltimateBackupUnit`, ensuring EMS/fire units are not converted to police officers.
  - Updated both `BackupFeature.cs` and `ResponseFeature.cs` to query `UltimateBackupService` first for ambulance and fire units.
  - Enforced strict `Fallback = false` policy: if `Settings.UseUltimateBackup = true` and `Settings.AllowFallback = false`, vanilla emergency models (`ambulance`, `firetruk`, `police`) will strictly NEVER spawn if Ultimate Backup fails or does not return a unit.
  - Expanded candidate vehicle matching in `FindCandidateAmbientVehicle` to support custom addon ambulance and fire vehicle names using substring matching (`ambu`, `medic`, `ems`, `fire`, `engine`, `truk`).
- **Curated Standby Locations & Anti-Lane Obstruction (LivingCity Parity)**:
  - Imported 23 hand-curated urban, police station, and highway standby locations from LivingCity into `ZoneSpawnData.CuratedStandbyLocations`.
  - Overhauled `StationaryFeature.TryFindStationaryCoordinate`:
    1. Priority 1: Check curated standby locations (`FindBestStandbyLocation`).
    2. Priority 2: Check curated pullover turnouts (`FindBestPulloverSpot`).
    3. Priority 3: Dynamic road node generation with a 3.6m right-hand curb/shoulder offset (`nodePos + rightDir * 3.6f`) and ground collision verification.
  - Refined `isOnActiveLane` threshold in `CreateStationaryUnitAt` to `< 2.5f`, preventing roadside/shoulder units from being falsely identified as blocking active traffic lanes and allowing full variety of standing officer scenarios (leaning, coffee, phone, lidar).
- **Ponytail Architecture Compliance**:
  - Maintained zero direct dependencies between feature files; cross-feature coordination executes safely via reflection.
  - 100% clean compilation: 0 warnings, 0 errors under `/p:TreatWarningsAsErrors=true /warnaserror`.

---

## [1.1.0] - 2026-10-12

### Keybindings Update & Master Activation Controls
- **Ergonomic Default Keybindings**:
  - **Lean Backup Menu**: Configured default to `X + PageDown` (`BackupMenuKey = PageDown`, `BackupMenuModifierKey = X`).
  - **Panic Button**: Configured default to `LControlKey + Z` (`PanicButtonKey = Z`, `PanicButtonModifierKey = LControlKey`), triggering 1:1 Ultimate Backup distress tones and tactical response.
  - **Group Backup**: Configured default to `LControlKey + C` (`GroupBackupKey = C`, `GroupBackupModifierKey = LControlKey`), coordinating multi-unit emergency reinforcement.
  - **Cadence Dashboard**: Configured default to `Q + PageDown` (`MenuKey = PageDown`, `MenuModifierKey = Q`).
  - **Dismiss Backup**: Configured default to `Enter` (`DismissBackupKey = Enter`, hold ~1.2s to prevent accidental dismissal during gameplay).
- **Dynamic Keybind Disabling**:
  - Added robust parser support for disabling any keybind: setting any key or modifier to `None`, `0`, `Disabled`, or `false` (case-insensitive) in `Cadence.ini` turns off key registration and completely bypasses input evaluation.
- **Top-Level Master Activation Toggle**:
  - Added master activation toggle `EnablePlugin = true` and alias `PluginActive = true` under `[General]` in `Cadence.ini`.
  - Setting `EnablePlugin = false` cleanly bypasses all feature bootstrapping on duty transition and notifies the player, allowing instant full plugin deactivation without removing files.

### Ambient Backup & Response Systems (Feature 7)
- **Ambient-First Commandeering & Tactical Backup (`BackupFeature.cs`)**:
  - Implemented 100% self-contained Feature 7 adhering strictly to Cadence's "Ponytail" Delete-Friendly Model (zero direct dependencies on other feature strands).
  - Ambient Commandeer Radius (320m): Dynamically commandeers existing cruising (`PatrolFeature`) and stationary/radar (`StationaryFeature`) units before falling back to spawning, creating hyper-realistic organic responses.
  - Seamless Fallback Spawner: If no ambient units are within 320m, automatically spawns jurisdiction-appropriate units (via Ultimate Backup XMLs or native police fallback) behind line-of-sight 180m–300m away.
- **Panic Button System (`PanicButtonKey = Z`, Modifier = LControlKey)**:
  - 1:1 parity with Ultimate Backup: plays high-priority dispatch audio alert (`PANIC_BUTTON.wav`), triggers animated officer radio shoulder-tap animation (`random@arrests`), and displays dispatch notification banners.
  - Multi-unit emergency Code 3 mobilization: instantly mobilizes configured number of units (`PanicButtonUnits = 2`) racing to the officer's exact location with sirens, lights, and aggressive navigation.
- **Group Backup System (`GroupBackupKey = C`, Modifier = LControlKey)**:
  - Dispatches or commandeers configured number of backup units (`GroupBackupUnits = 2`) Code 3 or Code 2 to reinforce the player.
  - Radio animation and scanner confirmation dispatch audio.
- **Lean Backup Menu (`BackupMenuKey = PageDown`, Modifier = X)**:
  - Sleek, lightweight RAGENativeUI quick-response menu.
  - Immediate tactical dispatch options:
    - *Local Police Backup (Code 2 / Code 3)*
    - *State Patrol / Highway Backup (Code 2 / Code 3)*
    - *Emergency Medical Services (EMS)*
    - *Fire Rescue Service*
    - *Group Backup*
    - *Emergency Panic Button*
    - *Dismiss All Responding Units*
- **Responding Unit Multi-Stage State Machine**:
  - *En Route*: Units navigate Code 2 or Code 3 to player location with dynamic path updates.
  - *On Scene & Arrival Protocol*: Siren mutes automatically upon reaching on-scene proximity (25m) while keeping lightbars active. Drivers and passengers perform staggered tactical egress, draw appropriate sidearms, and secure the perimeter.
  - *Threat Engagement*: Responders actively detect, suppress, and neutralize hostiles within range.
  - *Follow / Buddy Support*: Responders follow player with smart spacing (4m–10m) and tactical awareness.
  - *Interior Watchdog*: Detects when player enters an interior, keeping units outside in protective perimeter guard or staging postures.
  - *Clean Dismissal (`DismissBackupKey = Enter`, Modifier = None)*: Responders re-enter cruisers, cancel emergency lights, holster weapons, and smoothly merge back into ambient traffic.
- **Radar & Minimap Blips**:
  - Bright Yellow flashing radar blip (`Sprite 56`, `Color 66`, 350ms pulse) during En Route phase.
  - Solid Yellow radar blip (`Sprite 56`, `Color 66`) upon arrival On Scene.
  - Full atomic blip cleanup upon dismissal or despawn.

### In-Game Customization Menu, Density Tuning, Strict UB Guard & Service Variety
- **In-Game "Settings & Customization" Submenu (`Q + PageDown`)**:
  - Integrated directly into the main Cadence menu without altering existing aesthetic styling.
  - **Density Presets**: On-the-fly toggling between `Realistic (Default)` (standard authentic density) and `Chaos (Living City)` (high-density traffic stops, faster emergency dispatches, and active patrol presence).
  - **On-the-Fly Limit Sliders**:
    - Cruising Patrol Units: 1 to 16
    - Roadside Traffic Stops: 0 to 8
    - Stationary Radar Traps: 0 to 8
    - Emergency Runs: 0 to 8
  - **Emergency Run Agency Variety**: Toggle permitted agencies for background runs (`Police, Fire & EMS`, `Police & EMS`, `Police & Fire`, `Police Only`).
  - **Freeway vs City Balance**: Granular slider adjusting the ratio between Highway Patrol / State Troopers and Local Police (`Balanced 50/50`, `Heavy Highway 75/25`, `Heavy Local 25/75`, `Only Local`, `Only Highway`).
  - **Dynamic Fallback Toggle**: Checkbox to toggle vanilla vehicle fallback on/off in real-time.
- **Strict Ultimate Backup (UB) Guard & Zero Vanilla Fallback**:
  - Added `Fallback = false` (default) across `Cadence.ini` and `Settings.cs`. When false, Cadence strictly forbids spawning default vanilla GTA police cars (`police`, `s_m_y_cop_01`).
  - Implemented 5–8 second startup grace period giving Ultimate Backup XML configuration loaders ample time to finish initializing before ambient spawns begin.
  - Added in-game warning notification and startup log if UB is missing and fallback is disabled.
- **Emergency Services Variety (Ambulance & Fire Engines)**:
  - Added paramedic crew and ambulance vehicle (`ambulance` + `s_m_m_paramedic_01`) for Code 3 medical response calls.
  - Added firefighter crew and fire engine (`firetruk` + `s_m_y_fireman_01`) for emergency fire/rescue runs.
  - Emergency vehicles mute sirens upon arriving on-scene while maintaining flashing emergency lightbars.
- **Purge All Menu Button & Siren Overhaul**:
  - Fixed spawner loop pacing cooldown bypass (`&& currentCount > 0` bug) that previously caused replacement cruisers to spawn 250ms after purging.
  - Added active 35–45 second post-purge grace period preventing immediate unit recreation.
  - Sweep 350m radius around the player during purge to mute all emergency vehicle sirens, completely eliminating GTA V audio engine ghost loops.
- **Jurisdiction & Freeway Balancing**:
  - Integrated native zone detection (`GET_NAME_OF_ZONE`) to identify Blaine County locations (Sandy Shores, Grapeseed, Paleto Bay, Alamo Sea, etc.) and strictly prevent city police (LAPD/LSPD) from spawning in rural areas.
  - Integrated freeway road name detection (`IsHighway`) to weight State Highway Patrol vehicles on freeways according to `HighwayPatrolRatio`.

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
