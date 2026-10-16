# Cadence Coding & Architecture Standards

## Core Principles
1. **Discretion First**: Cadence exists to enrich the ambient background of GTA V / LSPDFR, not to hijack or dominate the game world. Ambient units must feel like natural, background law enforcement presence.
2. **Coexistence with Ultimate Backup & StopThePed**:
   - Cadence does not replace Ultimate Backup; it seamlessly yields to or integrates with it.
   - When panic or backup is activated, Cadence only dispatches authentic, available ambient police units in verified police vehicles. All standard backup logic belongs to Ultimate Backup.
3. **No Workarounds / Strict Validation**:
   - Never use loose heuristics (e.g. proximity checks) to guess vehicle ownership. A police cruiser must be strictly verified via `cruiser.IsPoliceVehicle`.
   - Never warp officers into civilian vehicles.
   - Never task officers onto active travel lanes unless managing a roadblock or dynamic scene with appropriate flare/barrier offsets.

## Spawning Rules & Geometry
1. **Pacing & Rate Limiting**:
   - Features must never execute immediate, unthrottled spawn bursts on game startup.
   - Startup spawn attempts must be staggered across features (minimum 15-30s intervals).
   - Global ambient density caps must be strictly respected.
2. **Placement & Roadway Geometry**:
   - Dynamic road node placement must account for travel lane widths (minimum 3.5m - 4.5m) and verify that vehicles are positioned on valid shoulders, parking spaces, or off-road lots.
   - If an officer or vehicle is within 6.5 meters of an active travel lane centerline without a dedicated parking shoulder, pedestrian scenarios must be disabled and the unit must remain inside the vehicle (`InVehicleObservation`).
   - High-speed freeways must be excluded from dynamic roadside spawning. Freeway units may only spawn at curated off-ramp rest areas or weigh stations.
3. **Relational Distance Invariants**:
   - Configuration distances must strictly adhere to the invariant:
     $$\text{SpawnDistanceMin} < \text{SpawnDistanceMax} < \text{DespawnDistance}$$
   - Recommended defaults: `SpawnDistanceMin = 180.0f`, `SpawnDistanceMax = 350.0f`, `DespawnDistance = 450.0f`.
   - Settings validation must enforce this relationship at boot time. If misconfigured, clamp with minimum buffer margins (`SpawnDistanceMax >= SpawnDistanceMin + 50.0f`, `DespawnDistance >= SpawnDistanceMax + 50.0f`) to prevent instant spawn-despawn thrashing.

## Concurrency & Fiber Safety
1. **Zero-Lock-Across-Yields Rule**:
   - **CRITICAL**: Never hold a C# monitor lock (`lock (_syncLock)`) across a fiber yield or blocking operation:
     - `GameFiber.Yield()`
     - `GameFiber.Sleep(...)`
     - `model.LoadAndWait()`
     - Streaming or asset requests (`REQUEST_ANIM_DICT`, `REQUEST_COLLISION_AT_COORD`, etc.)
   - Holding a lock across yields causes deadlocks between feature fibers, UI threads, and tick loops.
   - **Pattern**: Take snapshot of collection under lock, release lock, then iterate or perform asynchronous operations:
     ```csharp
     TrafficStopSession[] snapshot;
     lock (_syncLock)
     {
         snapshot = _activeStops.ToArray();
     }
     foreach (var stop in snapshot)
     {
         stop.Update(); // Safe to yield or sleep inside update
     }
     ```

## Native Invocation Standards
1. **Canonical Uppercase Identifiers**:
   - All `NativeFunction.Natives.*` calls must use uppercase canonical GTA V native names (e.g. `GET_CLOSEST_VEHICLE_NODE_WITH_HEADING`, never PascalCase `GetClosestVehicleNodeWithHeading`).
   - Raw hex calls (e.g. `NativeFunction.Natives.x...`) are strictly prohibited in application logic.
2. **Exact Parameter Types & Return Signatures**:
   - Void natives must NEVER be called with generic return type arguments (e.g. `TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE<uint>` is invalid and throws at runtime; invoke as `TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(...)`).
   - Float arguments must be passed explicitly as `0f` or `0.0f`, never relying on implicit integer coercions in dynamic calls.
3. **Engine Clamp Adherence**:
   - Respect GTA V native engine value limits. For example, `SET_DRIVER_ABILITY(driver, ability)` clamps strictly to `[0.0f, 1.0f]`. Passing `100.0f` is discarded by the engine.
4. **Siren & Lightbar Protocol**:
   - Silent lightbar activation must strictly use:
     ```csharp
     NativeFunction.Natives.SET_VEHICLE_SIREN(vehicle, true);
     NativeFunction.Natives.SET_VEHICLE_HAS_MUTED_SIRENS(vehicle, true);
     ```
   - Never invoke `0x2497C4717C8B881E` (`SET_VEHICLE_ENGINE_ON`) in siren contexts.

## Weapon Hash Integrity
1. **Verified JOAAT Hashes**:
   - All weapon hashes must match alloc8or / GTA V Jenkins One-at-a-Time (`joaat`) calculations.
   - `WEAPON_UNARMED` is strictly `0xA2719263` (`joaat("weapon_unarmed")`).
   - The corrupted hash `0xA2719248` is permanently banned.
   - Holstering must always be verified via `SET_CURRENT_PED_WEAPON(officer, 0xA2719263, true)`.

## Blip Lifecycle & Resource Management
1. **Zero-Leak Blip Destructor & Native Flash Reset Protocol**:
   - Blips must always be deleted using `BlipService.SafeDeleteBlip(ref Blip blip)`. Pass-by-value is prohibited.
   - Protocol steps:
     1. Null-check blip handle reference and verify existence.
     2. Halt visual flashing via native `SET_BLIP_FLASHES(blip, false)` and `SET_BLIP_FLASHES_ALTERNATE(blip, false)`.
     3. **Crucial Engine Reset**: Reset native interval and duration timers via `SET_BLIP_FLASH_INTERVAL(blip, 0)` and `SET_BLIP_FLASH_TIMER(blip, 0)`. Failing to reset these leaves GTA V's native flash timer armed, causing the blip to continue flashing when re-colored.
     4. Halt managed fibers with `blip.StopFlashing()`.
     5. Remove blip using managed `blip.Delete()` wrapped in exception guard.
     6. Clear memory reference (`blip = null`) in a `finally` block to prevent dangling pointer reuse.
2. **State Downgrade Blip Recreation**:
   - When transitioning an entity from a flashing emergency state (Code 3) to a non-flashing state (Patrol on Dismissal), delete and cleanly recreate the blip via `BlipService.AttachPatrolBlip`. This guarantees GTA V allocates a fresh `CBlip` entry with zero lingering native flash state.
3. **Police Vehicle Sprite Invariant**:
   - All law enforcement cruisers must strictly use **Sprite 56** (`PoliceCar`).
   - Never use **Sprite 1** (destination/waypoint circle) or `alternateFlash: true` on police vehicles. Generic circles create visual chaos, overlap with LSPDFR's native officer markers, and cause cruisers to look like red suspect markers during alternate pulses.
4. **Suspect Blip Inviolability & Native Radar Awareness**:
   - Never duplicate, overwrite, or delete suspect blips managed by LSPDFR.
   - Always query native blip existence via `NativeFunction.Natives.GET_BLIP_FROM_ENTITY(entity)` and `DOES_BLIP_EXIST(handle)`. Managed RPH calls (`entity.GetAttachedBlip()`) cannot detect native LSPDFR blips.
   - Cadence suspect watchdogs must strictly serve as emergency fallbacks if no native blip exists, must be tracked by `PoolHandle`, and must never thrash or re-create valid suspect markers across ticks.

## Pursuit & Backup Integration Standards
1. **LSPDFR Pursuit Disenrollment on Dismissal**:
   - Whenever an ambient or backup unit is dismissed, all enrolled officers must be removed from active pursuit AI via `Functions.RemovePedFromPursuit(ofc)` before tasks are cleared (`ofc.Tasks.Clear()`). Clearing tasks while officers are still managed by LSPDFR pursuit AI causes unhandled `NullReferenceException` crashes inside LSPDFR (`TaskCopChasePedAdv.cs:1515`).
2. **Ghost Drafting Prevention**:
   - Ambient cruiser drafting routines (`PursuitFeature.FindCandidateAmbientCruiser`) must verify that candidate vehicles are not active or dismissing backup cruisers (`!BackupFeature.IsBackupCruiser(v)`).
3. **Strict Pursuit Capacity Enforcement**:
   - All pursuit entry points (`RequestPursuitInterceptor`, `RequestGroupPursuit`) must strictly respect `Settings.MaxPursuitUnits` via `PursuitFeature.IsPursuitAtMaxCapacity(activePursuit)` to prevent runaway dispatching surges.
4. **LHandle Value Equality & Pursuit Enrollment**:
   - Never use reference equality (`handle != activePursuit`) on `LHandle` instances returned by `Functions.GetActivePursuit()`. `LHandle` is a heap object instantiated fresh on every call and does not override `Equals` or overload `operator ==`. Track active pursuit continuity using `_enrolledPursuitHandle == null || !Functions.IsPursuitStillRunning(_enrolledPursuitHandle)`.
   - Never call `ofc.Tasks.Clear()` prior to `Functions.AddCopToPursuit()`. Clearing tasks terminates LSPDFR's native `TaskCopChasePedAdv` vehicle pursuit AI and causes officers to thrash between mounting and dismounting their cruiser every tick.
5. **Pursuit Scene Retention Protocol**:
   - All pursuit-participating features must hook `Events.OnPursuitEnded` (not just `Events.OnCalloutFinished`).
   - When a pursuit concludes, units must transition to scene standby (`HoldOnSceneAfterPursuit()`) with handbrake engaged, silent emergency lights active, and `BlockPermanentEvents = true` with idle scenario tasks (`WORLD_HUMAN_COP_IDLES`).
   - Units must hold the scene until explicit player dismissal (Enter key or menu). Never automatically disperse or clear the scene immediately on Code 4.
6. **Siren Deactivation & Clean Demobilization**:
   - To completely shut off sirens, both visual and audio states must be neutralized:
     `vehicle.IsSirenOn = false; vehicle.IsSirenSilent = false; SET_VEHICLE_SIREN(veh, false); SET_VEHICLE_HAS_MUTED_SIRENS(veh, false); SET_SIREN_WITH_NO_DRIVER(veh, false); veh.ShouldVehiclesYieldToThisVehicle = false;`
   - When transitioning an emergency driver to routine ambient cruising, always demobilize native chase AI flags (`SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG 8 = false`, `SET_DRIVER_RACING_MODIFIER = 0.0f`, `cruiser.ShouldVehiclesYieldToThisVehicle = false`) via `ConfigurePatrolDriver()`. Leaving chase flags active causes GTA V's native driving AI to blast siren bursts at civilian traffic with lights turned off.

## Lifecycle & Teardown Architecture
1. **Inverted Teardown Lifecycle**:
   - On plugin shutdown (`Main.Finally` or emergency unload), entity cleanup must precede fiber suspension:
     1. First: `CadenceMenu.PurgeAllFeatures(immediateDelete: true)` to clean up spawned peds, cruisers, and blips.
     2. Second: `StopAllFeatures()` to signal feature fibers and stop background loops.
     3. Third: Unhook event handlers and release services.
   - Reversing this order leaves orphaned "ghost" entities in the world because fibers terminate before entity disposal logic runs.

## Tasking & AI State
1. **Vehicle Assignment**:
   - Every ambient officer must be permanently tied to their assigned cruiser handle.
   - If an officer dismounts, their vehicle reference must be preserved.
   - If responding to an emergency or panic call, the officer must return to and enter their own assigned cruiser. If their cruiser is inaccessible or destroyed, abort the task cleanly—do NOT steal civilian traffic.
2. **Build Quality**:
   - The project must compile with zero errors and zero warnings (`TreatWarningsAsErrors=true`).
   - Target framework: .NET Framework 4.8 / C# latest.
   - Continuous verification via `python scripts/validate_natives.py` must pass with exit code 0 before every release build.
