# Cadence - Changelog

All notable changes to the Cadence project are documented in this file.

---

## [1.2.0] - 2026-10-16

### Production Readiness & Tier 0 Architectural Hardening
- **Shared Ownership Registry (`UnitRegistry.cs`)**:
  - Centralized atomic entity claim and release mechanism preventing cross-feature dual ownership conflicts (§3.1).
  - Eradicated multi-feature race conditions where `PatrolFeature`, `TrafficStopFeature`, `PursuitFeature`, and `BackupFeature` could simultaneously commandeer the same vehicle or ped.
  - Implemented lock-synchronized claim validation (`TryClaim`, `Release`, `IsClaimed`, `IsClaimedBy`, `IsClaimedByOthers`, `ReleaseAll`) tracking entity handles across all active plugins.
  - Exposed decoupled `uint` internal handle management to allow standalone test execution outside the GTA 5 engine without loading unmanaged native libraries.
- **Pursuit Disenrollment Teardown Safety (`TeardownHelper.cs`)**:
  - Eradicated critical crash vector during LSPDFR pursuit teardown (§3.2).
  - Safely disenrolls active Cadence units from LSPDFR pursuits via `Functions.RemoveCopFromPursuit` before entity cleanup, preventing null reference exceptions and native engine faults when pursuits conclude or features are disposed.
- **Cooperative Fiber Cancellation & Teardown Step Isolation**:
  - Hardened lifecycle management (§3.3) by decoupling background fibers and ensuring cooperative cancellation tokens and safe abort intervals are observed.
  - Each feature's teardown sequence executes in isolated `try`/`catch` blocks, guaranteeing that a failure in one feature cannot interrupt the orderly teardown of subsequent subsystems.
- **Settings Persistence, Invariants & NaN Rejection (`Settings.cs`)**:
  - Implemented robust `Settings.Save()` configuration persistence with comment-preserving INI serialization (§3.6).
  - Enforced distance relationship invariant `DespawnDistance >= SpawnDistanceMax + 50.0m` across all presets and custom configurations (`ValidateDistances`).
  - Added rigorous NaN and Infinity sanitization, automatically restoring safe fallback defaults upon invalid float values.
  - Updated production default values (`Debug = false`, `Preset = Realistic`, `MaxPatrolUnits = 4`, `MaxTrafficStops = 2`, `MaxStationaryUnits = 2`, `MaxEmergencyRuns = 2`, `MaxPursuitUnits = 4`).
- **Automated Test Suite (`tests/Cadence.Tests`)**:
  - Standalone MSBuild test suite targeting .NET Framework 4.8 / x64 with zero external mock dependencies.
  - Validates `Settings` parsing, baseline preservation, save roundtrips, distance invariants, and NaN protection (`SettingsTests.cs`).
  - Validates `UnitRegistry` atomic claims, multi-threading contention with 20 parallel threads, owner checks, and release idempotency (`UnitRegistryTests.cs`).
  - Validates release package asset availability and zip table of contents integrity (`PackagingTests.cs`).
- **Native Validator & Packaging Pipeline Hardening (`scripts/validate_natives.py`, `dist/build.bat`)**:
  - Upgraded native validator to exclude build artifacts (`bin/`, `obj/`), enforce canonical uppercase `NativeFunction.Natives.*` and `CallByName` syntax, ban blacklisted `0xA2719248`, verify mandatory unarmed weapon hash `0xA2719263`, and check driving ability/aggressiveness float clamps `[0.0, 1.0]`.
  - Hardened `dist/build.bat` with single-sourced `v1.2.0` versioning, hard-fail missing DLL checks, integrated validator and test suite gates, and archive integrity assertion.

### Forensic Architectural Fixes: Cruiser Thrashing, Scene Retention, Siren Neutralization & Concurrency Hardening
- **Cruiser In/Out Thrashing Eradication During Pursuits (`BackupFeature.cs`)**:
  - **LHandle Heap Object Reference Inequality Fix**: Uncovered root cause of infinite loop (`Cadence.log` lines 945–980) where responding backup officers joined pursuits, stopped, stepped out, re-entered, and repeated every second. `Functions.GetActivePursuit()` instantiates a new heap `LHandle` reference on each invocation, causing `_enrolledPursuitHandle != activePursuit` to evaluate `true` on every 1000ms tick, repeatedly clearing `_enrolledPursuitOfficers`.
  - **Eliminated `ofc.Tasks.Clear()` on Pursuit Enrollment**: In `TransitionToSceneArrival()` and `UpdateOnScene()`, eliminated destructive `ofc.Tasks.Clear()` calls prior to `Functions.AddCopToPursuit()`. Clearing tasks aborted LSPDFR's native `TaskCopChasePedAdv` vehicle pursuit AI.
  - **Stable Pursuit Tracking Invariant**: Replaced handle comparison with `_enrolledPursuitHandle == null || !Functions.IsPursuitStillRunning(_enrolledPursuitHandle)`, ensuring officer enrollment state persists stably throughout the duration of a chase.
  - **Pursuit Guard on Re-engagement**: In `Reengage()`, gated officer dismount egress fibers behind `!isPursuitActive`. Vehicle-mounted responders in active vehicle chases remain in their cruisers to engage fleeing subjects.
- **Scene Retention & Standby Holding After Pursuit Conclusion (`BackupFeature.cs`, `PursuitFeature.cs`)**:
  - **Event-Driven Pursuit Lifecycle Hook**: Subscribed `Events.OnPursuitEnded += OnLspdfrPursuitEnded` across both `BackupFeature` and `PursuitFeature` (with symmetric unhooks on shutdown), eradicating reliance on 750ms polling loops and intercepting LSPDFR native teardown instantaneously.
  - **`HoldOnSceneAfterPursuit` Implementation**: Implemented tactical post-pursuit scene holding on `BackupUnitSession`: activates silent emergency strobe lightbars (`IsSirenSilent = true`), locks cruisers with handbrakes, orders defensive staggered dismount, assigns `WORLD_HUMAN_COP_IDLES` scenario animations, and enforces `BlockPermanentEvents = true` until explicit player dismissal (`Settings.DismissBackupKey`).
  - **En Route Timeout Protection**: In `BackupFeature.cs`, prevented 75s en route despawn timeouts while a vehicular pursuit is actively running (`!isPursuitRunning`), preserving units during long-distance or high-speed freeway chases.
  - **Distance Clamping Parity**: In `PursuitFeature.PruneAndWatchSessions`, replaced the hardcoded 350m cutoff with `Settings.DespawnDistance` (450m), preventing trailing units from being prematurely pruned after high-speed chases.
- **Siren Wailing & Emergency Driving Flag Neutralization (`NativeTasks.cs`, `ResponseFeature.cs`, `PatrolFeature.cs`)**:
  - **Audio Engine Desynchronization Eradication**: Re-architected `ShutOffSirenAndLights` in `NativeTasks.cs`. Completely eliminated the inverted `SET_VEHICLE_HAS_MUTED_SIRENS(vehicle, true)` call during siren deactivation that corrupted GTA V's internal `CAEVehicleAudioEntity`. Fully neutralizes audio and yield flags (`vehicle.IsSirenOn = false`, `vehicle.IsSirenSilent = false`, `SET_VEHICLE_SIREN(false)`, `SET_VEHICLE_HAS_MUTED_SIRENS(false)`, `SET_SIREN_WITH_NO_DRIVER(false)`, `ShouldVehiclesYieldToThisVehicle = false`).
  - **Patrol Driver Demobilization**: In `ConfigurePatrolDriver`, cleared `SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG 8`, zeroed `SET_DRIVER_RACING_MODIFIER 0.0f`, disabled vehicle yield, and invoked `ShutOffSirenAndLights(cruiser)`. Ambient cruisers will no longer blast warning horns or siren bursts at civilian traffic.
  - **Response Disposal Sanitization**: In `ResponseFeature.Dispose(immediateDelete: false)`, properly invoked `ConfigurePatrolDriver` and `ShutOffSirenAndLights` prior to handing cruisers over to ambient traffic.
  - **Ultimate Backup Spawn Sanitization & Watchdog Enforcement**: In `PatrolFeature.cs`, sanitized newly spawned Ultimate Backup cruisers with `ShutOffSirenAndLights` before cruising AI starts, and added `NativeTasks.EnforceSirenLightbarPolicy(unit.Cruiser)` to the background watchdog.
- **Coding Standards, Concurrency & AI Attribute Restorations (`BlipService.cs`, `TrafficStopFeature.cs`, `PatrolFeature.cs`, `StationaryFeature.cs`, `PursuitFeature.cs`, `AssistFeature.cs`)**:
  - **Section 7.3 Compliance**: Changed `alternateFlash: true` to `alternateFlash: false` on police pursuit blips in `BlipService.cs` (lines 197 & 368), removing confusing red/blue alternating flashes that made friendly cruisers appear as suspect markers.
  - **Thread-Safe Logging Reads**: Across `TrafficStopFeature.cs`, `PatrolFeature.cs`, `StationaryFeature.cs`, and `PursuitFeature.cs`, captured collection counts (`_active*.Count`) safely inside `lock (_syncLock)` before logging.
  - **Combat Attribute Leak Fix**: In `AssistFeature.FinishStandDown()`, restored `SET_PED_COMBAT_ATTRIBUTES(officer, 1, true)` (`CanUseVehicles = true`) and cleared attribute 5 (`AlwaysFight = false`), ensuring assisted officers can operate vehicles in future encounters.

---

## [1.1.9] - 2026-10-15

### Foot Blip Deletion on Remount, Blip Pool Exhaustion & Crash Eradication
- **Blip Pool Exhaustion Crash Resolution (`BackupFeature.cs`, `BlipService.cs`)**:
  - **Root Cause of Crash**: Identified from `RagePluginHook.log` and `Cadence.log` where LSPDFR crashed with `System.Exception: Could not create blip` inside `TrafficStop.cs:230`. Cadence had flooded the native GTA V blip pool with over 1,200 blips in seconds (`AttachOfficerFootBlip error: Could not create blip`).
  - **Root Cause of Badge Blip Not Disappearing on Remount**: In `BackupUnitSession.UpdateBlipStates()`, foot blips were stored in an unmapped `List<Blip> FootBlips` and queried via `SafeGetAttachedBlip(ofc)`. Because `ofc.GetAttachedBlip()` frequently returned null in RPH, `UpdateBlipStates` attached a new blip for each dismounted officer *every tick*. When officers remounted, `FootBlips.RemoveAll(...)` was called, which only dropped the C# references from the list without ever calling `blip.Delete()`, leaving all native badge blips permanently stuck on the radar.
  - **Fix**: Replaced `List<Blip>` with a strict `Dictionary<PoolHandle, Blip> _officerFootBlips`. Each officer ped is guaranteed to have at most one attached foot blip. When an officer remounts their cruiser, their badge blip is immediately looked up by handle and deleted via the 4-step `BlipService.SafeDeleteBlip(ref fb)` protocol.
  - **`CreateOrUpdateBlip` Guard**: Updated `BlipService.CreateOrUpdateBlip` to query `IS_BLIP_FLASHING` before destroying and recreating solid blips, eliminating churn and preserving engine blip slots.

---

## [1.1.8] - 2026-10-15

### Unaccepted Callout Response Prevention, Panic Button Responders Vanishing Fix & Zero-Lock Concurrency Hardening
- **Unaccepted/Pending Callout Response Prevention (`BackupFeature.cs`, `ResponseFeature.cs`)**:
  - Resolved issue where LSPDFR callouts offered over the radio (pending acceptance with the `Y` key) caused the Lean Backup menu (`X + PageDown`) to display "Callout Response" as active, erroneously dispatching backup units to unaccepted calls.
  - Integrated `CalloutAcceptanceState.Running` validation via `Functions.GetCalloutAcceptanceState(currentCallout)`. Pending callouts (`CalloutAcceptanceState.Pending`) are now strictly ignored by waypoint/callout resolvers, backup menu defaults, and autonomous response logic until explicitly accepted by the player.
- **Panic Button & Group Backup Responders Vanishing Fix (`BackupFeature.cs`, `ResponseFeature.cs`, `AssistFeature.cs`)**:
  - **Fatal Re-engagement Defect Elimination**: In `BackupUnitSession`, `CanReengage()` was erroneously returning `true` for `State == UnitState.ArrivedOnScene`. During multi-unit dispatches (`DispatchUnitsSequence`), once the first responder arrived within 35m and dismounted, subsequent loop iterations selected the already-arrived unit via `TryReengageDismissingUnit` instead of spawning the remaining backup units. Restricting `CanReengage()` strictly to `State == UnitState.Dismissing` ensures all requested units (4–7 for panic, 2–3 for group) spawn and deploy properly.
  - **Panic & Group Quota Clamping Resolution**: Fixed quota calculation in `RequestBackup(BackupType.PanicButton)` and `GroupBackup` which clamped non-pursuit requests to `Settings.MaxPursuitUnits - totalEngaged`, throttling emergency panic requests to 1 single car. Non-pursuit requests now reliably dispatch the full configured quota (`PanicButtonUnits` and `GroupBackupUnits`).
  - **Cross-Feature Cruiser Commandeering Guard**: Added `BackupFeature.IsBackupCruiser` checks to `ResponseFeature.FindIdleAmbientPoliceVehicle` and `AssistFeature.ScanForNearbyThreats`. Responding and on-scene backup cruisers can no longer be hijacked or have their routes canceled by ambient threat scans or emergency runs.
  - **Radar Blip Self-Healing & Foot Blip Optimization**: In `UpdateBlipStates()`, proactively re-attaches `RadarBlip` if dropped or invalidated by external engine operations (`RadarBlip == null || !RadarBlip.Exists()`), preventing blips from vanishing off the radar. Gated foot blip cleanup behind `FootBlips.Count > 0` to eliminate redundant native calls on seated officers.
  - **Sticky Flash Protocol Reset**: Added zero-interval and zero-timer resets (`SET_BLIP_FLASH_INTERVAL(blip, 0)`, `SET_BLIP_FLASH_TIMER(blip, 0)`) in `BlipService.ApplyBlipState` for solid blips.
- **Zero-Lock Concurrency Hardening Across Yields (`BackupFeature.cs`, `ResponseFeature.cs`, `PatrolFeature.cs`, `StationaryFeature.cs`)**:
  - Refactored `UpdateActiveUnits`, `DismissAll`, `PurgeAllInternal`, and `RunResponseWatchdog` to snapshot collections (`_activeUnits.ToArray()`) under lock and execute state machine ticks, entity tasks, and disposal routines outside the lock.
  - Cleanly disposed invalid/completed units during list cleanups in `GetActiveCount` and `GetActivePoliceResponderCount`.

---

## [1.1.7] - 2026-10-15

### Forward Emergency Response Driving, Sticky Blip Flash Eradication & Player Cruiser Radar Customization
- **Emergency Driving Direction Fix (`NativeTasks.cs`)**:
  - Restored `LongRangeEmergencyDrivingFlag` to `787004L` (0xC023C), completely eradicating bit 1024 (`DriveInReverse`).
  - Emergency and backup units responding to Panic Button calls (`LControl + Z`) and 911 dispatch now navigate forward toward the player and waypoints with full obstacle avoidance (`SwerveAroundAllVehicles`, `SteerAroundStationaryVehicles`, `SteerAroundPeds`, `SteerAroundObjects`, `DriveIntoOncomingTraffic`, `UseShortCutLinks`, `ChangeLanesAroundObstructions`).
- **Sticky Radar Blip Flashing Fix on Remount & Dismissal (`BlipService.cs`, `BackupFeature.cs`, `PatrolFeature.cs`, `PursuitFeature.cs`)**:
  - Identified and resolved the GTA V engine quirk where `SET_BLIP_FLASHES(blip, false)` fails to terminate flash animation loops on an existing blip handle.
  - When officers remount their cruiser (or re-enter upon dismissal), Cadence now cleanly deletes the old blip reference via the 4-step destructor protocol (`SafeDeleteBlip`) and recreates a fresh, pristine `[PATROL]` blip (`Sprite 56`, `Color 38` Navy Blue, solid).
  - Dismissed cruisers returning to routine patrol now display a calm, steady blue icon rather than flashing continuously.
  - Enhanced `CreateOrUpdateBlip` in `BlipService.cs` so any transition to a solid state (`flashIntervalMs == 0`) automatically destroys any existing blip that was previously flashing, guaranteeing zero sticky flash artifacts across all features.
- **Personal Vehicle Radar Icon Customization (`BlipService.cs`, `Settings.cs`, `Main.cs`, `Cadence.ini`)**:
  - Added dedicated player vehicle blip monitoring fiber (`UpdatePlayerVehicleBlip`).
  - Intercepts LSPDFR's default oversized White Circle marker on the player's personal cruiser when dismounted on foot, transforming it into a compact, sleek Purple Police Car blip:
    - **Sprite**: `56` (`PoliceCar`) instead of the generic circle (`Sprite 1`).
    - **Color**: `27` (Purple) to distinguish the player's personal car from AI units (Navy Blue, Orange, Red, Mint Green).
    - **Scale**: `0.38f` (compact, sleek, matching Cadence's minimalist radar styling and eliminating the giant vanilla circle).
    - **Auto-Cleanup**: Automatically destroys or yields the icon when the player re-enters the cruiser or changes vehicles.
    - **Configurable**: Configured via `Cadence.ini` under `[Radar]` (`EnablePlayerVehicleBlip`, `PlayerVehicleBlipColor`, `PlayerVehicleBlipScale`, `PlayerVehicleBlipSprite`).

---

## [1.1.6] - 2026-10-15

### Radar Blip Overhaul, Obstacle Avoidance AI, Pursuit Resolution & Smart Backup Re-engagement
- **Cruiser Obstacle Avoidance Driving AI (`NativeTasks.cs`)**:
  - Upgraded ambient and emergency driving AI flags:
    - Set `CruisingFlags` and `NormalDrivingFlag` to `786603` (`StopForVehicles`, `StopForPeds`, `SteerAroundStationaryVehicles`, `SteerAroundPeds`, `SteerAroundObjects`, `StopAtTrafficLights`, `UseShortCutLinks`, `ChangeLanesAroundObstructions`). Cruisers now steer smoothly around pedestrians, stopped civilians, and parked vehicles instead of freezing or blocking roads.
    - Set `LongRangeEmergencyDrivingFlag` to `788284L` (adds `GoOffRoadWhenAvoiding` [256] and `DriveInReverse` [1024]), allowing emergency cruisers to reverse if boxed in and navigate off-road around major obstructions.
- **Traffic Stop Drafting Bug & Range Calibration (`PursuitFeature.cs`)**:
  - Excluded cruisers actively engaged in traffic stops (`TrafficStopFeature.IsActiveCruiser(v)`) and emergency runs (`ResponseFeature.IsActiveCruiser(v)`) from ambient pursuit candidate selection. Cruisers conducting roadside stops will never be erroneously drafted or blare sirens while parked behind civilian vehicles.
  - Reduced ambient pursuit drafting radius from 380m down to 220m to keep pursuit backup locally relevant and prevent pulling distant units across town.
- **Pursuit Conclusion Scene Resolution & Siren Muting (`PursuitFeature.cs`, `BackupFeature.cs`)**:
  - When an active pursuit terminates or suspects surrender/kneel:
    - Automatically mutes audible sirens across all participating and nearby police cruisers within 60m (`cruiser.IsSirenSilent = true`), keeping flashing emergency lightbars illuminated.
    - Commands all pursuit officers (drivers and passengers) to dismount their cruisers, arm themselves with combat sidearms (`WeaponCombatPistol`), and establish tactical scene perimeters rather than idling inside vehicles with sirens blaring.
- **Dismissal Help Text Notification Polish (`PursuitFeature.cs`)**:
  - Eliminated the repetitive 750ms notification loop in `ProcessStandbyDismissal`. Displays a single clean 4-second notification when the player first approaches the standby scene.
- **Universal Radar Blip Palette & Dismount/Remount Synchronization (`BlipService.cs`, `PursuitFeature.cs`, `BackupFeature.cs`, `PatrolFeature.cs`, `ResponseFeature.cs`)**:
  - **Pursuit Units**: Solid Orange (`Color 17`), `Sprite 56` (`PoliceCar`), `{Callsign} [PURSUIT]`.
  - **Callout Responders**: Flashing Purple (`Color 27`, 600ms), `Sprite 56` (`PoliceCar`), `{Callsign} [CALLOUT]`.
  - **Dismounted Officer Synchronization**: When all officers exit a cruiser, the cruiser blip turns White (`Color 0`, `[PARKED]`) and each dismounted officer receives a Badge blip (`Sprite 58`, `Color 38`, `{Callsign} [ON FOOT]`). When remounted, badge blips are destroyed and the cruiser blip restores to its active operational role.
- **Smart Unit Re-engagement & Duplicate Wave Elimination (`BackupFeature.cs`)**:
  - Corrected `TryReengageDismissingUnit` proximity check to measure distance against both player position and target callout destination (`Math.Min(distToTarget, distToPlayer) <= 300f`), allowing dismissed or departing units near the player to be immediately commandeered for new calls.
  - For Panic Button and Group Backup requests, Cadence now checks for and re-engages on-scene or dismissing units within 120m first, only dispatching remaining units needed to fulfill the quota, preventing duplicate waves of 6 cars from piling onto an already-active scene.
  - Idle ambient police vehicles can be drafted regardless of Ultimate Backup tagging.

---

## [1.1.5] - 2026-10-15

### Callout Response Mode, Waypoint Navigation & Tactical Scene Holding
- **Callout Response Mode in Lean Backup Menu (`BackupFeature.cs`)**:
  - Integrated dedicated `"Callout Response"` section in the `X + PageDown` Lean Backup Menu with **dynamic contextual visibility**:
    - When no callout or waypoint is active, `"Callout Response"` is cleanly hidden from the `Response Mode` selector list.
    - When an LSPDFR callout is active or a map waypoint is placed, `"Callout Response"` automatically surfaces as the **first option** (default index 0) with a live contextual subtitle (e.g. `Responding: Assault in Progress` or `Responding: Map Waypoint`).
  - Dynamically displays response actions: Local Police (Code 3 / Code 2), State Patrol (Code 3 / Code 2), Multi-Unit Tactical Group (Code 3), EMS Ambulance (Code 3 / Code 2), Fire Rescue (Code 3 / Code 2), and Dismiss All.
- **Ambient-First Waypoint & Active Callout Resolution (`BackupFeature.cs`)**:
  - Implemented `TryGetCalloutOrWaypointPosition`:
    - Priority 1: Reads player's active GPS waypoint via GTA V natives `HUD::IS_WAYPOINT_ACTIVE`, `HUD::GET_FIRST_BLIP_INFO_ID(8)`, and `HUD::GET_BLIP_INFO_ID_COORD`, snapped to a valid road curb node via `GetRoadsideCurbPosition`.
    - Priority 2: Safely inspects active LSPDFR callout via `Functions.IsCalloutRunning()`, `Functions.GetCurrentCallout()`, and non-inlined reflection on `LHandle.Object` to obtain `Callout.CalloutPosition`.
  - Maintains 1:1 ambient-first commandeering: searches within 350m for existing ambient patrol, stationary, or traffic stop cruisers, rerouting them directly to the target callout position.
  - Graceful fallback spawning via Ultimate Backup (`SpawnLocalPatrol`, `SpawnStatePatrol`, `SpawnAmbulanceUnit`, `SpawnFireTruckUnit`) or native police vehicles if no ambient candidate is available.
- **En Route Dynamics & Despawn Guard (`BackupFeature.cs`)**:
  - Recalculates course dynamically en route if the player moves their GPS waypoint.
  - Extended en route despawn timeout to 180 seconds and 600 meters for callout units, preventing premature despawn while responding across the map.
  - Smooth deceleration and arrival detection upon reaching within 22 meters of the target curb node or coming to a stop near scene.
- **On-Scene Perimeter Holding & Tactical Transition (`BackupFeature.cs`)**:
  - While player is >35m away, responders hold a defensive perimeter within 12m of their cruiser using `WORLD_HUMAN_COP_IDLES` instead of running across the map to buddy-follow.
  - Responders center threat scans around their cruiser (40m radius) and engage hostile peds immediately.
  - Responders automatically transition to close buddy-following support as soon as the player arrives on scene (within 35m).
  - Automatically hooks `Events.OnCalloutFinished` to cleanly dismiss callout units when the callout concludes.
- **Dashboard Menu Integration (`CadenceMenu.cs`)**:
  - Added `Trigger Callout Response` to the "Testing & Spawns" submenu, wired via reflection (`InvokeFeatureSpawn`) to preserve the Ponytail delete-friendly architecture.

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
  - Automated build scripts (`dist\build.bat`, `scripts\push_dev.bat`, `scripts\promote_to_main.bat`).
  - Zero warnings, zero errors under `/p:TreatWarningsAsErrors=true`.
