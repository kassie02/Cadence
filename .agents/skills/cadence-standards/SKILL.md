---
name: cadence-standards
description: Enforces Cadence GTA V / LSPDFR coding standards, RPH concurrency invariants, zero-lock-across-yields rules, native invocation conventions, and entity/blip lifecycle management. Use whenever writing, editing, or reviewing code in Cadence.
---

# Cadence Coding & Architecture Standards

This skill provides the definitive engineering rules, concurrency patterns, and native API invariants for developing, refactoring, and reviewing features in the **Cadence** GTA V / LSPDFR ambient policing plugin.

---

## 1. Concurrency & Fiber Safety (Zero-Lock-Across-Yields)

**CRITICAL RULE**: Never hold a C# monitor lock (`lock (_syncLock)`) across a fiber yield or blocking operation:
- `GameFiber.Yield()`
- `GameFiber.Sleep(...)`
- `model.LoadAndWait()`
- Streaming or asset requests (`REQUEST_ANIM_DICT`, `REQUEST_COLLISION_AT_COORD`, etc.)

Holding locks across yields causes catastrophic deadlocks between feature fibers, UI threads, and main tick loops.

### Safe Snapshot Pattern
Always take a snapshot of collections under lock, exit the lock immediately, and then iterate or perform asynchronous fiber calls:

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

---

## 2. GTA V Native Invocation Rules

1. **Canonical Uppercase Identifiers**:
   - All `NativeFunction.Natives.*` calls must use uppercase canonical GTA V native names (e.g., `GET_CLOSEST_VEHICLE_NODE_WITH_HEADING`, never PascalCase `GetClosestVehicleNodeWithHeading`).
   - Raw hex calls (e.g. `NativeFunction.Natives.x...`) are strictly prohibited in application logic.

2. **Exact Parameter Types & Return Signatures**:
   - Void natives must NEVER be called with generic return type arguments (e.g., `TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE<uint>` is invalid and throws at runtime; invoke as `TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE(...)`).
   - Float arguments must be passed explicitly as `0f` or `0.0f`, never relying on implicit integer coercions in dynamic calls.

3. **Banned Pointer Natives (`REMOVE_BLIP`)**:
   - Never call `NativeFunction.Natives.REMOVE_BLIP(...)`. In the GTA V engine, `REMOVE_BLIP` takes a `Blip*` pointer, and invoking it through RPH's dynamic marshaler causes an unhandled `AccessViolationException`. Always use RPH's managed `blip.Delete()`.

4. **Engine Clamp Adherence**:
   - Respect GTA V native engine value limits:
     - `SET_DRIVER_ABILITY(driver, ability)` clamps strictly to `[0.0f, 1.0f]`.
     - `SET_DRIVER_AGGRESSIVENESS(driver, agg)` clamps to `[0.0f, 1.0f]`.
     - `SET_DRIVER_RACING_MODIFIER(driver, mod)` clamps to `[0.0f, 1.0f]`.

5. **Static Validation**:
   - Always run `python scripts/validate_natives.py` before committing. All 200+ call sites must pass with 0 violations.
   - Use the `gta5-dev` MCP server (`get_native_details`, `search_gta_native`) whenever verifying native signatures.

---

## 3. Siren & Emergency Lightbar Protocol

### Silent Lightbars (Traffic Stops & Stationary Scenarios)
Emergency lightbars flashing without siren audio must strictly use:
```csharp
NativeFunction.Natives.SET_VEHICLE_SIREN(vehicle, true);
NativeFunction.Natives.SET_VEHICLE_HAS_MUTED_SIRENS(vehicle, true);
```
*Never invoke `0x2497C4717C8B881E` (`SET_VEHICLE_ENGINE_ON`) in siren contexts.*

### Complete Siren Deactivation
To completely shut off sirens and prevent native driving AI from blasting audible siren bursts:
```csharp
vehicle.IsSirenOn = false;
vehicle.IsSirenSilent = false;
NativeFunction.Natives.SET_VEHICLE_SIREN(vehicle, false);
NativeFunction.Natives.SET_VEHICLE_HAS_MUTED_SIRENS(vehicle, false);
NativeFunction.Natives.SET_SIREN_WITH_NO_DRIVER(vehicle, false);
vehicle.ShouldVehiclesYieldToThisVehicle = false;

// Demobilize chase AI flags when returning to routine cruising
NativeFunction.Natives.SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG(driver, 8, false);
NativeFunction.Natives.SET_DRIVER_RACING_MODIFIER(driver, 0.0f);
```

---

## 4. Blip Lifecycle & Resource Management

1. **Zero-Leak Blip Destructor & Native Flash Reset**:
   - Blips must always be deleted using `BlipService.SafeDeleteBlip(ref Blip blip)`. Calling without the `ref` keyword is banned.
   - Proper reset sequence:
     1. Null-check reference and verify existence.
     2. Halt visual flashing via `SET_BLIP_FLASHES(blip, false)` and `SET_BLIP_FLASHES_ALTERNATE(blip, false)`.
     3. Reset native timers via `SET_BLIP_FLASH_INTERVAL(blip, 0)` and `SET_BLIP_FLASH_TIMER(blip, 0)` (critical to prevent lingering native flash state in the `CBlip` engine pool).
     4. Halt managed fibers with `blip.StopFlashing()`.
     5. Remove blip using `blip.Delete()`.
     6. Clear memory reference `blip = null` in a `finally` block.

2. **State Downgrade Blip Recreation**:
   - When transitioning an entity from flashing emergency state (Code 3) to non-flashing state (Patrol on Dismissal), delete and cleanly recreate the blip via `BlipService.AttachPatrolBlip` so GTA V allocates a fresh `CBlip` entry.

3. **Police Vehicle Sprite Invariant**:
   - Law enforcement cruisers must strictly use **Sprite 56** (`PoliceCar`).
   - Never use **Sprite 1** (destination/waypoint circle).
   - `alternateFlash: true` is strictly banned on police vehicle blips (pulses red, making cruisers look like suspects).

4. **Suspect Blip Inviolability**:
   - Never duplicate, overwrite, or delete suspect blips managed by LSPDFR.
   - Always query native blip existence via `GET_BLIP_FROM_ENTITY(entity)` and `DOES_BLIP_EXIST(handle)`.

---

## 5. Weapon Hash Integrity

- All weapon hashes must match alloc8or / GTA V Jenkins One-at-a-Time (`joaat`) calculations.
- `WEAPON_UNARMED` is strictly `0xA2719263` (`joaat("weapon_unarmed")`).
- The corrupted hash `0xA2719248` is permanently banned.
- Holstering must always be verified via `NativeFunction.Natives.SET_CURRENT_PED_WEAPON(officer, 0xA2719263, true)`.

---

## 6. Pursuit & Backup Integration Standards

1. **LSPDFR Pursuit Disenrollment on Dismissal**:
   - Whenever an ambient or backup unit is dismissed, remove all enrolled officers from active pursuit AI via `Functions.RemovePedFromPursuit(ofc)` before clearing tasks (`ofc.Tasks.Clear()`).
   - Calling `Tasks.Clear()` while officers are still managed by LSPDFR pursuit AI causes unhandled crashes inside LSPDFR (`TaskCopChasePedAdv.cs:1515`).

2. **Ghost Drafting Prevention**:
   - Ambient cruiser drafting routines (`PursuitFeature.FindCandidateAmbientCruiser`) must verify that candidate vehicles are not active or dismissing backup cruisers (`!BackupFeature.IsBackupCruiser(v)`).

3. **LHandle Value Equality & Pursuit Monitoring**:
   - Never use direct reference or value equality (`handle != activePursuit` or `==`) on `LHandle` instances returned by `Functions.GetActivePursuit()`. `LHandle` is instantiated fresh on every call and does not override equality operators.
   - Track active pursuit continuity using `_enrolledPursuitHandle == null || !Functions.IsPursuitStillRunning(_enrolledPursuitHandle)`.
   - Never call `ofc.Tasks.Clear()` prior to `Functions.AddCopToPursuit()`.

4. **Pursuit Scene Retention Protocol**:
   - When a pursuit ends, units must transition to scene standby (`HoldOnSceneAfterPursuit()`) with handbrake engaged, silent lights active, and `BlockPermanentEvents = true` with idle scenario tasks (`WORLD_HUMAN_COP_IDLES`).
   - Units hold the scene until explicit player dismissal (Enter key or menu). Never automatically disperse immediately on Code 4.

---

## 7. Spawning & Relational Distance Invariants

1. **Relational Distance Invariants**:
   - Configuration distances must strictly adhere to:
     `SpawnDistanceMin < SpawnDistanceMax < DespawnDistance`
   - Recommended defaults: `SpawnDistanceMin = 180.0f`, `SpawnDistanceMax = 350.0f`, `DespawnDistance = 450.0f`.
   - Settings validation must clamp buffer margins (`SpawnDistanceMax >= SpawnDistanceMin + 50.0f`, `DespawnDistance >= SpawnDistanceMax + 50.0f`) to prevent instant spawn-despawn thrashing.

2. **Water & River Spawning Prevention**:
   - All spawn coordinates must be checked against native water probes (`WorldUtils.IsPositionInWater` via `GET_WATER_HEIGHT` and `TEST_VERTICAL_PROBE_AGAINST_ALL_WATER`).

---

## 8. Build & Verification Standards

1. **Zero Warnings**: The project builds with `TreatWarningsAsErrors=true`.
2. **Native Verification**: Run `python scripts/validate_natives.py` before finalizing any changes. Must exit with code 0.
3. **Unit Tests**: Ensure all tests in `tests/Cadence.Tests` pass.
