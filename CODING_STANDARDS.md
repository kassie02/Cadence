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

## Spawning Rules
1. **Pacing & Rate Limiting**:
   - Features must never execute immediate, unthrottled spawn bursts on game startup.
   - Startup spawn attempts must be staggered across features (minimum 15-30s intervals).
   - Global ambient density caps must be strictly respected.
2. **Placement & Roadway Geometry**:
   - Dynamic road node placement must account for travel lane widths (minimum 3.5m - 4.5m) and verify that vehicles are positioned on valid shoulders, parking spaces, or off-road lots.
   - If an officer or vehicle is within 6.5 meters of an active travel lane centerline without a dedicated parking shoulder, pedestrian scenarios must be disabled and the unit must remain inside the vehicle (`InVehicleObservation`).
   - High-speed freeways must be excluded from dynamic roadside spawning. Freeway units may only spawn at curated off-ramp rest areas or weigh stations.

## Tasking & AI State
1. **Vehicle Assignment**:
   - Every ambient officer must be permanently tied to their assigned cruiser handle.
   - If an officer dismounts, their vehicle reference must be preserved.
   - If responding to an emergency or panic call, the officer must return to and enter their own assigned cruiser. If their cruiser is inaccessible or destroyed, abort the task cleanly—do NOT steal civilian traffic.
2. **Build Quality**:
   - The project must compile with zero errors and zero warnings (`TreatWarningsAsErrors=true`).
   - Target framework: .NET Framework 4.8 / C# latest.
