# Cadence (v1.0.0)

> **A Discreet, Living Ambient Policing System for LSPDFR**  
> *Built for RagePluginHook (.NET Framework 4.8 / C# 10 / x64)*

---

## 1. Overview

**Cadence** is an ambient policing plugin for LSPDFR designed to bring the world of San Andreas to life with an organic, realistic law enforcement presence. Unlike traditional police mods that overwhelm players with synthetic radio screeching, jarring audio beeps, or intrusive on-screen notifications, Cadence operates quietly, reliably, and naturally in the background.

With Cadence, you will encounter fellow officers actively patrolling city avenues and country highways, conducting authentic roadside traffic stops, monitoring traffic from speed traps and turnouts, responding Code 2 or Code 3 to distant 911 calls, taking over rejected callouts without fanfare, assisting in active pursuits, and defending against nearby armed hostiles.

Cadence was built from the ground up using the **"Ponytail" Delete-Friendly Architecture**: every feature is 100% self-contained in its own module. The plugin delivers uncompromising stability, absolute zero entity/blip leakage, and seamless compatibility with the entire LSPDFR ecosystem.

---

## 2. Features

### Feature 1: Ambient Cruising Patrol
- **Autonomous Roadway Patrols**: Ambient cruisers patrol naturally across San Andreas streets and highways using authentic driving flags (`CruisingFlags = 786603`), respecting traffic signals and maintaining realistic cruising speeds (16–20 m/s).
- **Safe Road Node Generation**: New units spawn on validated road nodes 180m–350m from the player, preventing visual pop-in or mid-air/subterranean anomalies.
- **Dynamic Liveries & Uniforms**: Integrated with Ultimate Backup and native EUP uniform presets to ensure officers match local jurisdictions (LSPD, BCSO, SAHP, Park Rangers).
- **Radar Minimap Identity**: Color-coded Navy Blue blip (`Sprite 56`, `Color 38`).
- **Despawn Watchdog**: Safely dismisses cruisers when beyond 450m or off-screen, maintaining minimal memory footprint.

### Feature 2: 1803 Roadside Traffic Stops
- **Authentic RPH Port**: Direct, native RagePluginHook recreation of the roadside pullover system with 0 external dependencies (no ScriptHookVDotNet required).
- **Curated Shoulder Coordinates**: Uses over 40 hand-tuned roadside pullover zones from `ZoneSpawnData.cs` across all major highways and arterials for safe, natural vehicle positioning.
- **Silent Lightbar Protocol**: Officers keep emergency lightbars flashing while muting siren audio, avoiding repetitive siren screeching during stops.
- **Three Authentic Scenarios**:
  1. *Routine Citation*: Officer approaches the driver's window holding a clipboard and notepad (`prop_notepad_01`), taking notes and issuing citations.
  2. *Driver Pat-Down*: Officer orders the suspect to the rear of the vehicle, conducting a thorough hands-on frisk against the cruiser hood.
  3. *K9 Chassis Sniff*: Officer deploys a trained police K9 Shepherd (`a_c_shepherd`) to perform a 4-point walk-around sniff of the suspect's vehicle perimeter.
- **Smooth Re-Entry**: Upon completion, the violator and cruiser smoothly merge back into highway traffic before being naturally dismissed.
- **Radar Minimap Identity**: Color-coded Yellow blip (`Sprite 56`, `Color 4`).

### Feature 3: Stationary Units & Radar Speed Traps
- **Strategic Standby Locations**: Spawns parked cruisers in highway medians, turnouts, side streets, and commercial parking lots 120m–300m from the player.
- **Five Immersive Idle Behaviors**:
  1. *In-Car Radar Watch*: Officers seated inside their cruiser monitoring traffic.
  2. *Hood Lean*: Officer leaning casually against the cruiser fender (`WORLD_HUMAN_COP_IDLES`).
  3. *Coffee Break*: Officer standing beside the cruiser enjoying a coffee cup (`ng_proc_coffee_01a`).
  4. *Phone Call*: Officer conducting dispatch communication via smartphone (`WORLD_HUMAN_STAND_MOBILE`).
  5. *Lidar / Speed Observation*: Officer scanning highway lanes with binoculars/radar device (`WORLD_HUMAN_BINOCULARS`).
- **Tactical Remount & Rejoin**: When despawn timers trigger or the unit is reassigned, officers cleanly holster/drop props, enter their cruiser, and merge back into traffic.
- **Radar Minimap Identity**: Color-coded Mint Green blip (`Sprite 56`, `Color 2`).

### Feature 4A: Background 911 Emergency Runs
- Ambient cruisers periodically receive simulated emergency dispatches, activating emergency lights and sirens to navigate rapidly through traffic Code 2 or Code 3 to distant incident locations (400m–800m away) before standing down.
- **Radar Minimap Identity**: Color-coded Flashing Red blip (`Sprite 56`, `Color 1`, 600ms pulse).

### Feature 4B: Rejected Callout Takeover
- Hooks into LSPDFR's `Events.OnCalloutNotAccepted` event.
- When the player declines an LSPDFR callout (or lets the timer expire), Cadence silently identifies the incident coordinates and assigns the nearest ambient Cadence unit to take over the call.
- The assigned unit activates Code 3 and rushes to the scene.
- **Zero Audio & HUD Clutter**: No annoying dispatch audio repeats, no synthetic radio chimes, and no screen banners.

### Feature 5: Ambient Pursuit Drafting
- Continuously monitors active LSPDFR pursuits via `Functions.GetActivePursuit()`.
- If an active chase has fewer cruisers than `MaxPursuitUnits`, Cadence drafts nearby cruising officers into the chase, registering both driver and passenger into LSPDFR's pursuit engine (`Functions.AddCopToPursuit`).
- Drafted cruisers activate pursuit driving AI, sirens, and emergency maneuvers.
- **Code 4 Stand-Down**: When the pursuit terminates, drafted units are gracefully released, sirens are deactivated, and cruisers return to routine cruising patrol.
- **Radar Minimap Identity**: Color-coded Flashing Orange blip (`Sprite 56`, `Color 17`, 400ms pulse).

### Feature 6: Nearby Threat Reaction & Combat Defense
- Cruisers monitor a 60m–90m radius for active hostile gunfire and armed suspects attacking officers or civilians.
- When a threat is detected, the cruiser halts, applies the emergency handbrake, and illuminates flashing emergency lights.
- Officers dismount, unholster sidearms (`WEAPON_COMBATPISTOL` / `WEAPON_PISTOL`), take tactical cover behind cruiser doors/pillars, and engage the hostile suspect.
- Once the threat is neutralized, officers holster weapons, remount their cruiser, release the handbrake, and resume routine patrol.
- **Radar Minimap Identity**: Color-coded Flashing Dark Orange blip (`Sprite 56`, `Color 49`, 300ms pulse).
- *Note: In accordance with project design, no manual panic buttons or manual assist hotkeys are included.*

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

- **Default Keybind**: `F8` (configurable in `Cadence.ini`)
- **Keybind Disabling**: Set `MenuKey = 0`, `MenuKey = None`, or `MenuKey = Disabled` in `Cadence.ini` if you prefer to use external menu managers or want to avoid hotkey collisions.

### Menu Navigation & Features
1. **Live Telemetry & Dashboard**:
   - Real-time monitors displaying the current count of Active Patrol Cruisers, Traffic Stops, Speed Traps, Emergency Runs, Pursuit Units, and Combat Units, along with Total Tracked Entities.
2. **Real-Time Feature Toggles**:
   - Live checkboxes to enable or disable individual features on the fly without reloading the game:
     - Ambient Cruising Patrol
     - Roadside Traffic Stops
     - Stationary & Speed Traps
     - Emergency 911 Runs
     - Pursuit Drafting
     - Nearby Threat Defense
3. **Instant Testing & Spawner**:
   - Trigger instant spawns for testing or immediate immersion:
     - `[Spawn Patrol Cruiser]`
     - `[Spawn Roadside Pullover]`
     - `[Spawn Radar Speed Trap]`
     - `[Trigger Code 3 Emergency Run]`
4. **Purge All Cadence Units**:
   - Emergency reset button: safely clears every active Cadence vehicle, officer, canine, accessory prop, and radar blip in a single tick.
5. **Reload Cadence.ini**:
   - Re-reads `Cadence.ini` settings from disk and updates all running parameters and menu toggles immediately.

---

## 5. Installation Instructions

### Requirements
- Grand Theft Auto V (latest retail/Steam/Epic release)
- RagePluginHook (v1.08+ or latest packaged with LSPDFR)
- LSPD First Response (0.4.9+)
- RAGENativeUI (1.9.2+ - standard in modern LSPDFR setups)

### Standard Installation
1. Download the release archive (`Cadence_v1.0.0.zip`).
2. Open the archive and extract the `plugins` folder directly into your **Grand Theft Auto V root directory**:
   ```
   Grand Theft Auto V/
   └── plugins/
       └── LSPDFR/
           ├── Cadence.dll
           └── Cadence.ini
   ```
3. Launch GTA V via `RAGEPluginHook.exe`.
4. Go on duty with LSPDFR (`ForceDuty` in console or go on duty at any police station).
5. Cadence will automatically initialize and display a subtle notification in the bottom-left corner:
   > *Cadence loaded successfully. Press F8 for menu.*

---

## 6. Compatibility & Ecosystem

Cadence was engineered from the start to be a peaceful, unobtrusive neighbor in your mod directory:
- **Ultimate Backup (UB)**: Fully supported! Cadence dynamically probes for Ultimate Backup via AppDomain reflection, pulling local jurisdiction vehicle models and ped components when available. If UB is absent, it gracefully falls back to native LSPDFR spawners.
- **StopThePed (STP)**: 100% compatible. Cadence respects STP ped states and does not conflict with STP traffic stop or ped interrogation routines.
- **External Callout Packs**: Seamless coexistence. Ambient Cadence units will never steal an accepted player callout or interfere with callout objectives. Rejected callout takeovers only trigger on callouts you actively decline or ignore.
- **EUP (Emergency Uniforms Pack)**: Built-in EUP component applier assigns authentic hats, vests, and badges matching your local patrol jurisdiction.

---

## 7. License & Credits

- Developed for the LSPDFR community by the Cadence Development Team.
- Built on top of the RagePluginHook API and LSPDFR SDK.
