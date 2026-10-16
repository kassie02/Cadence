# Cadence External Dependencies

This directory contains reference assemblies required to compile the Cadence LSPDFR plugin.
These assemblies are linked at build time with `Private=False` (`Copy Local = false`), meaning they are not bundled into compiled releases and must be provided by the user's GTA V / LSPDFR installation.

## Reference Assemblies

| File | File Version | Product Version | Size (Bytes) | SHA-256 Checksum |
|---|---|---|---|---|
| `RagePluginHook.dll` | `1.124.1393.17586` | `1.124.1393.17586` | 219,648 | `5d439745604a5fedbf8fa401520d4f296c1b6800d77e2923ba3bf9772182c7e0` |
| `LSPD First Response.dll` | `0.4.9` | `0.4.9` | 5,755,904 | `d5adc28c97784297c10d031208ffc341a86e9f210e2153f094384e3c444780fa` |
| `RAGENativeUI.dll` | `1.9.3.0` | `1.9.3` | 291,328 | `d2607481b206e7907c9c1f2cabf15797654aacaaf1746ea202740dcdd5eb8bbb` |
| `UltimateBackup.dll` | `1.8.7.1` | `1.8.7.1` | 1,750,016 | `af6b9c01b8718fe7dedb00f0707f1a74e501db48f05c545c9369e6614b53fa58` |

---

## Dependency Details and Acquisition

### 1. RagePluginHook (`RagePluginHook.dll`)
- **Role:** Core runtime API providing game engine hooks, memory access, native invocation (`Rage.Native.NativeFunction`), fiber multitasking (`Rage.GameFiber`), entity wrappers (`Ped`, `Vehicle`, `Blip`), and spatial math (`Vector3`).
- **Target Version:** v1.08+ / RPH SDK build `1.124.1393.17586` or compatible.
- **Provider / Source:** Included with standard LSPDFR distributions or directly from [RagePluginHook](https://ragepluginhook.net/).

### 2. LSPD First Response (`LSPD First Response.dll`)
- **Role:** Official LSPDFR mod SDK (`LSPD_First_Response.Mod.API.Functions`, `Events`) providing pursuit management, duty tracking (`IsPlayerOnDuty`), dispatch audio (`PlayScannerAudioUsingPosition`), and callout lifecycle hooks.
- **Target Version:** LSPDFR 0.4.9 (Build 8382 or later).
- **Provider / Source:** [LCPDFR.com](https://www.lcpdfr.com/lspdfr/).

### 3. RAGENativeUI (`RAGENativeUI.dll`)
- **Role:** GTA V native-style UI menu framework for rendering the Cadence Dashboard, Quick Settings menu, and interactive sliders.
- **Target Version:** v1.9.3 (Alexguirre).
- **Provider / Source:** [GitHub - alexguirre/RAGENativeUI](https://github.com/alexguirre/RAGENativeUI/releases).

### 4. Ultimate Backup (`UltimateBackup.dll`)
- **Role:** Integration reference for BejoIjo's Ultimate Backup mod (`UltimateBackup.API.Functions`). Cadence optionally queries UB to coordinate ambient unit dispatch with player backup requests.
- **Target Version:** v1.8.7.1 (BejoIjo).
- **Provider / Source:** [BejoIjo Plugins](https://www.bejoijo.com/post/ultimate-backup).

---

## Build Reference Notice

These binaries are proprietary third-party software and are referenced solely for development and compile-time API binding. They are excluded from distribution packaging (`dist/build.bat` stages only `plugins/LSPDFR/Cadence.dll`, `Cadence.ini`, and audio assets).
