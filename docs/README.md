# Cadence (v1.1.0)

> **A Discreet, Living Ambient Policing & Response System for LSPDFR**  
> *Built for RagePluginHook (.NET Framework 4.8 / C# 10 / x64)*

---

## 1. Overview

**Cadence** is an ambient policing plugin for LSPDFR designed to bring the world of San Andreas to life with an organic, realistic law enforcement presence. Unlike traditional police mods that overwhelm players with synthetic radio screeching, jarring audio beeps, or intrusive on-screen notifications, Cadence operates quietly, reliably, and naturally in the background.

With Cadence, you will encounter fellow officers actively patrolling city avenues and country highways, conducting authentic roadside traffic stops, monitoring traffic from speed traps and turnouts, responding Code 2 or Code 3 to distant 911 calls, taking over rejected callouts without fanfare, assisting in active pursuits, and defending against nearby armed hostiles.

In v1.1.0, Cadence introduces the **Ambient Backup & Response Systems**, delivering 1:1 Ultimate Backup compatibility (Panic Button, Group Backup, Lean Backup Menu) powered by an **Ambient-First Commandeering Architecture**: instead of magically spawning new cars out of thin air, Cadence commands nearby ambient cruisers already driving or parked on the beat to respond to your distress call!

Cadence was built from the ground up using the **"Ponytail" Delete-Friendly Architecture**: every feature is 100% self-contained in its own module. The plugin delivers uncompromising stability, absolute zero entity/blip leakage, and seamless compatibility with the entire LSPDFR ecosystem.

> **Roadmap & Architecture Blueprint**: For detailed technical designs on upcoming post-v1.1.0 features (Spikestrips, K9 units, Station Sally Ports, Air Support, Voice Integration, Coroner/Tow, and Escalation Matrix), see [`future-features.md`](future-features.md) (or root [`../future-features.md`](../future-features.md)). Active focus remains 100% on v1.1.0 production calibration and zero leakage.

---

## 2. Features

### Feature 1: Ambient Cruising Patrol (`PatrolFeature.cs`)
- **Autonomous Roadway Patrols**: Ambient cruisers patrol naturally across San Andreas streets and highways using authentic driving flags (`CruisingFlags = 786603`), respecting traffic signals and maintaining realistic cruising speeds (16–20 m/s).
- **Safe Road Node Generation**: New units spawn on validated road nodes 180m–350m from the player, preventing visual pop-in or mid-air/subterranean anomalies.
- **Dynamic Liveries & Uniforms**: Integrated with Ultimate Backup and native EUP uniform presets to ensure officers match local jurisdictions (LSPD, BCSO, SAHP, Park Rangers).
- **Radar Minimap Identity**: Color-coded Navy Blue blip (`Sprite 56`, `Color 38`).
- **Despawn Watchdog**: Safely dismisses cruisers when beyond 450m or off-screen, maintaining minimal memory footprint.

### Feature 2: 1803 Roadside Traffic Stops (`TrafficStopFeature.cs`)
- **Authentic RPH Port**: Direct, native RagePluginHook recreation of the roadside pullover system with 0 external dependencies (no ScriptHookVDotNet required).
- **Curated Shoulder Coordinates**: Uses over 40 hand-tuned roadside pullover zones from `ZoneSpawnData.cs` across all major highways and arterials for safe, natural vehicle positioning.
- **Silent Lightbar Protocol**: Officers keep emergency lightbars flashing while muting siren audio, avoiding repetitive siren screeching during stops.
- **Three Authentic Scenarios**:
  1. *Routine Citation*: Officer approaches the driver's window holding a clipboard and notepad (`prop_notepad_01`), taking notes and issuing citations.
  2. *Driver Pat-Down*: Officer orders the suspect to the rear of the vehicle, conducting a thorough hands-on frisk against the cruiser hood.
  3. *K9 Chassis Sniff*: Officer deploys a trained police K9 Shepherd (`a_c_shepherd`) to perform a 4-point walk-around sniff of the suspect's vehicle perimeter.
- **Smooth Re-Entry**: Upon completion, the violator and cruiser smoothly merge back into highway traffic before being naturally dismissed.
- **Radar Minimap Identity**: Color-coded Yellow blip (`Sprite 56`, `Color 4`).

### Feature 3: Stationary Units & Radar Speed Traps (`StationaryFeature.cs`)
- **Strategic Standby Locations**: Spawns parked cruisers in highway medians, turnouts, side streets, and commercial parking lots 120m–300m from the player.
- **Five Immersive Idle Behaviors**:
  1. *In-Car Radar Watch*: Officers seated inside their cruiser monitoring traffic.
  2. *Hood Lean*: Officer leaning casually against the cruiser fender (`WORLD_HUMAN_COP_IDLES`).
  3. *Coffee Break*: Officer standing beside the cruiser enjoying a coffee cup (`ng_proc_coffee_01a`).
  4. *Phone Call*: Officer conducting dispatch communication via smartphone (`WORLD_HUMAN_STAND_MOBILE`).
  5. *Lidar / Speed Observation*: Officer scanning highway lanes with binoculars/radar device (`WORLD_HUMAN_BINOCULARS`).
- **Tactical Remount & Rejoin**: When despawn timers trigger or the unit is mobilized for backup, officers cleanly holster/drop props, enter their cruiser, and merge back into traffic.
- **Radar Minimap Identity**: Color-coded Mint Green blip (`Sprite 56`, `Color 2`).

### Feature 4A: Background 911 Emergency Runs (`ResponseFeature.cs`)
- Ambient cruisers periodically receive simulated emergency dispatches, activating emergency lights and sirens to navigate rapidly through traffic Code 2 or Code 3 to distant incident locations (400m–800m away) before standing down.
- **Agency Variety**: Police cruisers, Los Santos / County Fire engines (`firetruk`), and Paramedic ambulances (`ambulance`).
- **Radar Minimap Identity**: Color-coded Flashing Red blip (`Sprite 56`, `Color 1`, 600ms pulse).

### Feature 4B: Rejected Callout Takeover (`ResponseFeature.cs`)
- Hooks into LSPDFR's `Events.OnCalloutNotAccepted` event.
- When the player declines an LSPDFR callout (or lets the timer expire), Cadence silently identifies the incident coordinates and assigns the nearest ambient Cadence unit to take over the call.
- The assigned unit activates Code 3 and rushes to the scene.
- **Zero Audio & HUD Clutter**: No annoying dispatch audio repeats, no synthetic radio chimes, and no screen banners.

### Feature 5: Ambient Pursuit Drafting (`PursuitFeature.cs`)
- Continuously monitors active LSPDFR pursuits via `Functions.GetActivePursuit()`.
- If an active chase has fewer cruisers than `MaxPursuitUnits`, Cadence drafts nearby cruising officers into the chase, clearing ambient tasks and registering both driver and passenger into LSPDFR's pursuit engine (`Functions.AddCopToPursuit`).
- Drafted cruisers activate high-performance pursuit driving AI (aggressiveness 0.6, ability 100.0, chase behavior flag 8 for U-turns and overtaking), sirens, and tactical maneuvers.
- **Scene Standby & Dismissal Protocol**: When the pursuit terminates, units transition to `OnSceneStandby`. Sirens mute while emergency lightbars stay active. Seated officers hold position (`VehicleManeuver.Wait`) and dismounted officers secure the perimeter without interrupting LSPDFR's suspect arrest routines.
- **Standby Dismissal (`Enter` held >= 1.1s)**: Holding the dismissal key prompts and releases scene officers back to their cruisers, turning off lights, cruising away, and safely rejoining ambient traffic. Automatic distance (>250m) and 5-minute timeout watchdogs prevent entity leaks.
- **Radar Minimap Identity**: Color-coded Flashing Orange blip (`Sprite 56`, `Color 17`, 400ms pulse).

### Feature 6: Nearby Threat Reaction & Combat Defense (`AssistFeature.cs`)
- Cruisers monitor a 60m–90m radius for active hostile gunfire and armed suspects attacking officers or civilians.
- When a threat is detected, the cruiser halts, applies the emergency handbrake, and illuminates flashing emergency lights.
- Officers dismount, unholster sidearms (`WEAPON_COMBATPISTOL` / `WEAPON_PISTOL`), take tactical cover behind cruiser doors/pillars, and engage the hostile suspect.
- Once the threat is neutralized, officers holster weapons, remount their cruiser, release the handbrake, and resume routine patrol.
- **Radar Minimap Identity**: Color-coded Flashing Dark Orange blip (`Sprite 56`, `Color 49`, 300ms pulse).

### Feature 7: Ambient Backup & Response Systems (`BackupFeature.cs`)
- **1:1 Ultimate Backup Audio & Notification Parity**:
  - **Panic Button (`LControlKey + Z`)**: Plays authentic `PANIC_BUTTON.wav` tone, scanner audio (`ASSISTANCE_REQUIRED IN_OR_ON_POSITION`), player radio animation, red dispatch banner, subtitle prompt, and multi-unit tactical Code 3 emergency response.
  - **Group Backup (`LControlKey + C`)**: Multi-unit tactical reinforcement request with synchronized dispatch and scanner audio.
  - **Redesigned Backup Menu (`X + PageDown`)**: Ultimate Backup `Class74.cs` parity featuring a top **Response Mode** selector:
    - *Code 3 [Emergency]*: Local Police, State Patrol / Highway, EMS Ambulance, Fire Department, Group Backup, Panic Button, and Dismiss All.
    - *Code 2 [Urgent]*: Urgent responses with sirens off and warning lights.
    - *Pursuit Backup*: Local Pursuit Backup, Highway Pursuit Backup, Pursuit Interceptor, Group Pursuit Backup (2 Units), and Dismiss All Pursuit Units.
- **Ambient-First Commandeering Architecture**:
  - Scans a 320m radius for existing ambient cruisers, stationary speed traps, ambulances, or fire engines.
  - Commandeers active road units immediately, clearing cruising tasks, setting emergency sirens, and routing them to roadside curb positions near the player.
  - Seamless fallback spawner fulfills requested unit quotas via Ultimate Backup or native fallback if ambient units are unavailable.
- **Responding Unit Lifecycle & State Machine**:
  - **En Route & Roadside Arrival**: High-speed emergency driving with road/curb node target snapping (`GetRoadsideCurbPosition`) and a 12m stopping radius, preventing cruiser circling loops.
  - **Scene Arrival (within 14m/16m)**: Mutes audible sirens immediately while keeping lightbars flashing, followed by staggered vehicle egress (driver exits first, passenger follows after 350ms).
  - **Threat Engagement**: Scans for active gunfire, suspects attacking player/police, or active pursuits. Officers unholster sidearms, take tactical cover, and neutralize hostiles.
  - **Follow / Buddy Support**: Throttled 1300ms navigation mesh updates eliminate 60 Hz ped stutter/freezing; officers stand naturally when within 3.5m.
  - **Clean Dismissal (`Enter` held ~1.1s or menu option)**: Deletes blips atomically, extinguishes sirens, remounts cruiser, and cruises away before seamlessly merging back into ambient traffic.

---

## 3. Universal Radar Blips & Zero-Leak Destructor

Cadence assigns distinct, color-coded minimap blips to all active units so you can instantly recognize ambient police activity at a glance.

### Radar Blip Color Matrix

| Unit State | Sprite | Color ID | Color Name | Flashing | Display Name Format |
| :--- | :---: | :---: | :--- | :---: | :--- |
| **Cruising Patrol** | 56 | 38 | Navy Blue | No | `{Callsign} [PATROL]` |
| **Roadside Traffic Stop** | 56 | 4 | Yellow | No | `{Callsign} [TRAFFIC STOP]` |
| **Stationary / Radar Trap** | 56 | 2 | Mint Green | No | `{Callsign} [STANDBY]` |
| **Emergency Run / Takeover** | 56 | 1 | Red | Yes (600ms) | `{Callsign} [CODE 3]` |
| **Pursuit Assist** | 56 | 17 | Orange | Yes (400ms) | `{Callsign} [PURSUIT]` |
| **Threat Reaction / Combat** | 56 | 49 | Dark Orange | Yes (300ms) | `{Callsign} [ENGAGING]` |
| **Dismounted Officer Foot** | 58 | 38 | Navy Blue | No | `{Callsign} [ON FOOT]` |

### Zero-Leak Destructor Protocol
To prevent the notorious "blip ghosting" and minimap clutter issues common in legacy mods, Cadence enforces an atomic 4-step teardown protocol whenever any blip is dismissed or transitioned:
```csharp
// 1. Native flag flash suppression
NativeFunction.Natives.SET_BLIP_FLASHES(blip, false);

// 2. RPH managed flash termination
blip.StopFlashing();

// 3. Engine entity deletion
blip.Delete();

// 4. Reference cleanup
blip = null;
```

---

## 4. In-Game Menu Guide

Cadence includes an in-game dashboard powered by RAGENativeUI.

- **Master Activation Toggle**: `EnablePlugin = true` / `PluginActive = true` in `[General]` (set to `false` to completely turn off and deactivate Cadence)
- **Cadence Dashboard**: `Q + PageDown` (`MenuKey = PageDown`, `MenuModifierKey = Q` in `Cadence.ini`)
- **Lean Backup Menu**: `X + PageDown` (`BackupMenuKey = PageDown`, `BackupMenuModifierKey = X` in `Cadence.ini`)
- **Panic Button**: `LControlKey + Z` (`PanicButtonKey = Z`, `PanicButtonModifierKey = LControlKey` in `Cadence.ini`)
- **Group Backup**: `LControlKey + C` (`GroupBackupKey = C`, `GroupBackupModifierKey = LControlKey` in `Cadence.ini`)
- **Dismiss Backup**: `Enter` (`DismissBackupKey = Enter` in `Cadence.ini`, hold ~1.2s to prevent accidental dismissals)
- **Dynamic Keybind Disabling**: Setting any key or modifier to `None`, `0`, `Disabled`, or `false` turns it off completely and bypasses input detection.

### Keybindings Reference Table

| Feature / Control | Default Keybind | INI Setting (`Cadence.ini`) | Configurable / Toggleable |
| :--- | :--- | :--- | :--- |
| **Master Activation Toggle** | `N/A` (Config Only) | `[General]` `EnablePlugin = true` / `PluginActive = true` | Yes (`false` turns off plugin) |
| **Cadence Dashboard** | `Q + PageDown` | `[Keybinds]` `MenuKey = PageDown`, `MenuModifierKey = Q` | Yes (`None` / `0` / `Disabled` / `false` to disable) |
| **Lean Backup Menu** | `X + PageDown` | `[Backup]` `BackupMenuKey = PageDown`, `BackupMenuModifierKey = X` | Yes (`None` / `0` / `Disabled` / `false` to disable) |
| **Panic Button (Code Red)** | `LControlKey + Z` | `[Backup]` `PanicButtonKey = Z`, `PanicButtonModifierKey = LControlKey` | Yes (`None` / `0` / `Disabled` / `false` to disable) |
| **Group Backup (Code 3)** | `LControlKey + C` | `[Backup]` `GroupBackupKey = C`, `GroupBackupModifierKey = LControlKey` | Yes (`None` / `0` / `Disabled` / `false` to disable) |
| **Dismiss Backup** | `Enter` (hold ~1.2s) | `[Backup]` `DismissBackupKey = Enter` | Yes (`None` / `0` / `Disabled` / `false` to disable) |
| **Fast-Spawn En Route Unit** | `Backspace` (tap) | Context-sensitive safety hint | Active while backup unit is en route |

> **Note on Dynamic Keybind Disabling**: Setting any key or modifier to `None`, `0`, `Disabled`, or `false` in `Cadence.ini` turns it off completely, disabling key registration and bypassing key detection.

### Menu Navigation & Features
1. **Live Telemetry & Dashboard**:
   - Real-time monitors displaying the current count of Active Patrol Cruisers, Traffic Stops, Speed Traps, Emergency Runs, Pursuit Units, Combat Units, and Backup Units, along with Total Tracked Entities.
2. **Real-Time Feature Toggles**:
   - Live checkboxes to enable or disable individual features on the fly without reloading the game:
     - Ambient Cruising Patrol
     - Roadside Traffic Stops
     - Stationary & Speed Traps
     - Emergency 911 Runs
     - Pursuit Drafting
     - Nearby Threat Defense
     - Ambient Backup System
3. **Settings & Customization Submenu**:
   - On-the-fly customization of density, limits, and service varieties:
     - **Preset Profile**: Toggle between `Realistic (Default)` (balanced, authentic density) and `Chaos (Living City)` (high density, faster pacing, living city feel).
     - **Max Patrol Cruisers**: Dynamic slider (1 to 16 cruisers; default: 4).
     - **Max Traffic Stops**: Dynamic slider (0 to 8 stops; default: 2).
     - **Max Speed Traps**: Dynamic slider (0 to 8 standby units; default: 2).
     - **Max Emergency Runs**: Dynamic slider (0 to 8 runs; default: 2).
     - **Emergency Run Variety**: Choose between `Police, Fire & EMS (Default)`, `Police & EMS`, `Police & Fire`, or `Police Only`.
     - **Highway vs City Balance**: Adjust State/Highway Patrol ratio vs Local Police (Balanced 50/50, Heavy Highway 75/25, Heavy Local 25/75, Only Local, Only Highway).
     - **Allow Native Fallback**: Checkbox toggle for vanilla GTA vehicle fallback.
4. **Instant Testing & Spawner**:
   - Trigger instant spawns for testing or immediate immersion:
     - `[Spawn Patrol Cruiser]`
     - `[Spawn Roadside Pullover]`
     - `[Spawn Radar Speed Trap]`
     - `[Trigger Code 3 Emergency Run]`
     - `[Trigger Panic Backup]`
     - `[Trigger Group Backup]`
5. **Purge All Cadence Units**:
   - Safely wipes all active Cadence vehicles, officers, canines, accessory props, and radar blips in a single tick.
   - Cleans up and silences all lingering emergency vehicle sirens within 350m to prevent GTA V audio engine ghost loops.
   - Enforces an active 35–45 second spawner grace period so replacement cruisers do not immediately respawn.
6. **Reload Cadence.ini**:
   - Re-reads `Cadence.ini` settings from disk and updates all running parameters, limits, and menu toggles immediately.

---

## 5. Dedicated In-Game Logging & Diagnostics

Cadence includes an enterprise-grade asynchronous logging subsystem:
- **Log Path**: `Grand Theft Auto V\plugins\LSPDFR\Cadence\Logs\Cadence.log`
- **Zero Disk Stutters**: Operates on a dedicated low-priority background worker thread (`ThreadPriority.BelowNormal`) with a thread-safe queue. Disk writes never block RagePluginHook game fibers (0ms fiber overhead).
- **Session Archiving**: Each new game session automatically archives the previous session log to `Cadence_Archive_yyyyMMdd_HHmmss.log`, maintaining a clean 10-session rolling retention.
- **Configurable Verbosity**: Set `Debug = true` in `Cadence.ini` for detailed telemetry (spawns, road nodes, UB queries, state transitions, despawns) or `Debug = false` for standard silent gameplay.

---

## 6. Installation Instructions

### Requirements
- Grand Theft Auto V (latest retail/Steam/Epic release)
- RagePluginHook (v1.08+ or latest packaged with LSPDFR)
- LSPD First Response (0.4.9+)
- RAGENativeUI (1.9.2+ - standard in modern LSPDFR setups)

### Standard Installation
1. Download the release archive (`Cadence_v1.1.0.zip`).
2. Open the archive and extract the `plugins` folder directly into your **Grand Theft Auto V root directory**:
   ```
   Grand Theft Auto V/
   └── plugins/
       └── LSPDFR/
           ├── Cadence.dll
           ├── Cadence.ini
           └── Cadence/
               ├── Audio/
               │   └── PANIC_BUTTON.wav
               └── Logs/
   ```
3. Launch GTA V via `RAGEPluginHook.exe`.
4. Go on duty with LSPDFR (`ForceDuty` in console or go on duty at any police station).
5. Cadence will automatically initialize and display a subtle notification in the bottom-left corner:
   > *Cadence loaded successfully. Press Q + PageDown for menu.*

---

## 7. Compatibility & Ecosystem

Cadence was engineered from the start to be a peaceful, unobtrusive neighbor in your mod directory:
- **Ultimate Backup (UB)**: 100% compatible. Cadence provides 1:1 Panic Button audio/notifications and seamlessly uses UB's XML fleet spawner.
  - **Strict Fleet Protection**: Fallback to vanilla GTA vehicles (`police`, `s_m_y_cop_01`) is **disabled by default** (`Fallback = false`). If UB is not ready or returns null, Cadence aborts the spawn cleanly to preserve your customized vehicle fleet.
  - **Startup Grace Period**: Cadence observes a 5–8 second grace period on boot to allow UB's XML configuration files to complete loading before initiating ambient spawns.
- **StopThePed (STP)**: 100% compatible. Cadence respects STP ped states and does not conflict with STP traffic stop or ped interrogation routines.
- **External Callout Packs**: Seamless coexistence. Ambient Cadence units will never steal an accepted player callout or interfere with callout objectives. Rejected callout takeovers only trigger on callouts you actively decline or ignore.
- **EUP (Emergency Uniforms Pack)**: Built-in EUP component applier assigns authentic hats, vests, and badges matching your local patrol jurisdiction.

---

## 8. Future Roadmap & Architecture Blueprints

Comprehensive architectural blueprints and multi-stage state machines for planned post-v1.1.0 modules are documented in:
- [`future-features.md`](future-features.md) (or root [`../future-features.md`](../future-features.md))

### Planned Future Features ("Ponytail" Delete-Friendly Model):
1. **Tactical Pursuit Spikestrips** (`SpikeFeature.cs`): Ambient intercept vector positioning and dynamic stinger deployment.
2. **Ambient K9 Unit Deployments** (`K9Feature.cs`): Dedicated handler & canine patrols with bite-and-hold apprehension.
3. **Station Sally Ports & Shift Rotations** (`StationFeature.cs`): Realistic station roll-outs and end-of-watch bay parking.
4. **Ambient Air Support Overwatch** (`AirSupportFeature.cs`): Tactical rotary air support with nocturnal searchlight tracking.
5. **VocalDispatch & Native Voice Integration**: Hands-free voice recognition and tactile radio integration.
6. **Coroner & Tow Ambient Services** (`ServiceFeature.cs`): Living physical service units replacing instant-teleport removals.
7. **Traffic Stop Escalation Matrix**: Seamless ambient backup transitions during hostile traffic stop shootouts/pursuits.

> *Note: Active development is 100% frozen on new features until v1.1.0 achieves complete golden master sign-off (zero entity/blip leakage, deterministic memory footprint, and 60fps stable performance).*

---

## 9. License & Credits

- Developed for the LSPDFR community by the Cadence Development Team.
- Built on top of the RagePluginHook API and LSPDFR SDK.
