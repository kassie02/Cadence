# Cadence Workspace Handoff (v1.1.1)

**Date**: 2026-10-12  
**Target Environment**: GTA V / LSPDFR / RagePluginHook (x64)  
**Workspace**: `C:\Users\User\Documents\Games\GTA 5 Plugin Workspace\01_MAIN_DEVELOPMENT\Cadence`  
**Reference Codebase**: `C:\Users\User\Documents\Games\GTA 5 Plugin Workspace\03_IGNORE_PROJECTS\05_PAUSED_PROJECTS\LivingCity Development\100% no UB - BEST\best-before-ub\LivingCity_Unified` *(Read-only, untouched)*  
**Deployment Target**: `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V`

---

## 1. Executive Summary of Resolved Issues

### Issue 1: Game Crash on Panic Button Trigger (`0xe0434352` CLR Exception)
- **Root Cause A**: Call to non-existent scanner audio string `"UNIT_RESPONDING_DISPATCH"` in `BackupFeature.cs:482`, causing an unhandled scanner hook exception in LSPDFR audio subroutines.
- **Root Cause B**: Synchronous playback `SoundPlayer.PlaySync()` blocking the game fiber and crashing during rapid fiber cancellation/disposal.
- **Root Cause C**: Dual ownership of commandeered vehicles. Cruisers commandeered from `StationaryFeature`, `PatrolFeature`, or `ResponseFeature` were never unlinked from their parent loops. Both features ran concurrent AI tasks, blip updates, and state changes. Specifically, `StationaryFeature` held `SET_VEHICLE_HANDBRAKE = true`, causing physics lockups when `BackupFeature` applied emergency driving velocity.
- **Root Cause D**: Commandeering vehicles within <40m of player immediately triggered broken arrival transitions and premature vehicle egress.
- **Root Cause E**: In `TaskFollowPlayer`, the navmesh fallback calculation erroneously added raw local coordinate offsets directly to world X/Y instead of computing `player.GetOffsetPosition(offset)`.
- **Fix Applied**:
  - Removed `"UNIT_RESPONDING_DISPATCH"` scanner call.
  - Replaced `sp.PlaySync()` with non-blocking `sp.Play()`.
  - Implemented decoupled cross-feature cruiser relinquishing (`RelinquishCruiser`) in `PatrolFeature`, `StationaryFeature`, and `ResponseFeature`. Commandeered units now cleanly release handbrakes, delete prior radar blips, clear props, and unhook prior feature tracking.
  - Enforced a 40m minimum distance in `FindCandidateAmbientVehicle`.
  - Guarded `Driver.Tasks.PerformDrivingManeuver` in `TransitionToSceneArrival` to verify the driver is seated inside the vehicle.
  - Corrected navmesh coordinate math in `TaskFollowPlayer`.

### Issue 2: Ambulance / EMS Models Spawning Despite `Fallback = false`
- **Root Cause**: `UltimateBackupService` lacked API bindings for `UBFunctions.getAmbulanceUnit` and `getFireTruckUnit`. Both `BackupFeature.cs` and `ResponseFeature.cs` hardcoded `new Model("ambulance")` and completely ignored `Settings.UseUltimateBackup` and `Settings.AllowFallback`.
- **Fix Applied**:
  - Added `SpawnAmbulanceUnit` and `SpawnFireTruckUnit` in `UltimateBackupService.cs`, hooking directly into Ultimate Backup's API.
  - Added `isPolice = false` flag in `PrepareUltimateBackupUnit`, ensuring paramedic and firefighter peds are not converted into cops.
  - Enforced strict fallback policy across `BackupFeature.cs` and `ResponseFeature.cs`: if `Settings.UseUltimateBackup = true` and `Settings.AllowFallback = false`, vanilla emergency models (`ambulance`, `firetruk`, `police`) will strictly **NEVER** spawn if Ultimate Backup fails or does not return a unit.
  - Enhanced ambient candidate vehicle detection in `FindCandidateAmbientVehicle` to match addon vehicles by keyword (`ambu`, `medic`, `ems`, `fire`, `engine`, `truk`).

### Issue 3: Standing Ambient Cruisers In Odd Locations (Middle of Traffic Lanes)
- **Root Cause**: `ZoneSpawnData.cs` only contained rural highway pullover spots. On lookup failure, `StationaryFeature` queried `WorldUtils.GetSafeStreetPosition`, dropping cruisers directly at road node centers (in the middle of active traffic lanes) with 0 shoulder offset.
- **Fix Applied**:
  - Imported 23 hand-curated urban, police station, and highway standby locations from `LivingCity_Unified` into `ZoneSpawnData.CuratedStandbyLocations`.
  - Overhauled `StationaryFeature.TryFindStationaryCoordinate`:
    1. Priority 1: Check curated standby locations (`FindBestStandbyLocation`).
    2. Priority 2: Check curated pullover turnouts (`FindBestPulloverSpot`).
    3. Priority 3: Dynamic road node generation with a 3.6m right-hand curb/shoulder offset (`nodePos + rightDir * 3.6f`) and ground collision verification.
  - Refined `isOnActiveLane` threshold in `CreateStationaryUnitAt` to `< 2.5f`, preventing roadside/shoulder units from being falsely identified as blocking active traffic lanes and allowing full variety of standing officer scenarios (leaning, coffee, phone, lidar).

---

## 2. Modified Files & Verification Matrix

| File | Changes Made | Verification Status |
| :--- | :--- | :--- |
| [`src/Services/UltimateBackupService.cs`](file:///C:/Users/User/Documents/Games/GTA%205%20Plugin%20Workspace/01_MAIN_DEVELOPMENT/Cadence/src/Services/UltimateBackupService.cs) | Added `SpawnAmbulanceUnit`, `SpawnFireTruckUnit`, non-cop flag for paramedics/firemen, and native fallbacks with strict `AllowFallback` checks. | Verified (0 warnings, 0 errors) |
| [`src/Data/ZoneSpawnData.cs`](file:///C:/Users/User/Documents/Games/GTA%205%20Plugin%20Workspace/01_MAIN_DEVELOPMENT/Cadence/src/Data/ZoneSpawnData.cs) | Added 23 curated standby spots from LivingCity and `FindBestStandbyLocation`. | Verified (0 warnings, 0 errors) |
| [`src/Features/StationaryFeature.cs`](file:///C:/Users/User/Documents/Games/GTA%205%20Plugin%20Workspace/01_MAIN_DEVELOPMENT/Cadence/src/Features/StationaryFeature.cs) | Added `RelinquishCruiser`/`MobilizeCruiser`, 3-tier coordinate picker with 3.6m curb offset, and refined `isOnActiveLane` threshold. | Verified (0 warnings, 0 errors) |
| [`src/Features/PatrolFeature.cs`](file:///C:/Users/User/Documents/Games/GTA%205%20Plugin%20Workspace/01_MAIN_DEVELOPMENT/Cadence/src/Features/PatrolFeature.cs) | Added `RelinquishCruiser` and blip/task cleanup upon relinquishing. | Verified (0 warnings, 0 errors) |
| [`src/Features/ResponseFeature.cs`](file:///C:/Users/User/Documents/Games/GTA%205%20Plugin%20Workspace/01_MAIN_DEVELOPMENT/Cadence/src/Features/ResponseFeature.cs) | Added `RelinquishCruiser`, updated ambulance/fire spawners to query UB, and strictly enforced `AllowFallback = false`. | Verified (0 warnings, 0 errors) |
| [`src/Features/BackupFeature.cs`](file:///C:/Users/User/Documents/Games/GTA%205%20Plugin%20Workspace/01_MAIN_DEVELOPMENT/Cadence/src/Features/BackupFeature.cs) | Fixed Panic Button crash (scanner audio & non-blocking sound), added `ReleaseFromOtherFeatures`, handbrake release, UB ambulance/fire integration, and fixed navmesh offset math. | Verified (0 warnings, 0 errors) |
| [`docs/CHANGELOG.md`](file:///C:/Users/User/Documents/Games/GTA%205%20Plugin%20Workspace/01_MAIN_DEVELOPMENT/Cadence/docs/CHANGELOG.md) | Documented v1.1.1 patch notes. | Verified |

---

## 3. Build & Deployment Status

- **Compilation**: Clean Release build (x64) with `/p:TreatWarningsAsErrors=true /warnaserror` (0 warnings, 0 errors).
- **Packaging**: Created `dist\Cadence_v1.1.0.zip`.
- **Auto-Deploy**: Executed `sync_and_stage.bat C` via ExtractorTool, successfully deploying all staged files directly to `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V`.
