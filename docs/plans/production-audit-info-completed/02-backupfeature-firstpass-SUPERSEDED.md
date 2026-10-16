# BackupFeature.cs Audit Findings (Read-Only)

File Covered: src/Features/BackupFeature.cs (3814 lines)
Baseline: CODING_STANDARDS.md

## Critical
### C1: Lock held across non-trivial calls (TryReengageDismissingUnit)
- File: 488-541
- Severity: Critical
- Description: Holds _syncLock while calling bestCandidate.Reengage(...).
- Why: Blocks other fibers; snapshot+release required.
- Recommendation: Snapshot candidates under lock, release, then call Reengage.

### C2: GameFiber.Abort() on shutdown
- File: 160
- Severity: Critical
- Description: Stop() aborts fiber after cleanup.
- Why: Unsafe; prefer cooperative shutdown.
- Recommendation: _isRunning=false and let fiber exit naturally.

### C3: Dual-ownership reservation race
- File: 1128-1150, 1569-1591, 1664-1666
- Severity: Critical
- Description: Selection/reservation window allows double-commandeering.
- Why: Two sessions can claim same cruiser/driver.
- Recommendation: Finder reserves; re-validate after reserving before Commandeer.

### C4: Dismissal disenrollment from LSPDFR pursuit before clearing tasks
- File: 2640-2660
- Severity: Critical
- Description: Remove officers from pursuit before task clear.
- Why: Prevents LSPDFR NRE per standards.
- Recommendation: Keep as implemented.

## High
### H1: IsBackupCruiser/IsBackupOfficer exclude Completed?
- File: 214-230
- Severity: High
- Recommendation: Exclude State==Completed; clarify Dismissing semantics.

### H2: SoundPlayer not disposed
- File: 2296-2326
- Severity: High
- Recommendation: Dispose SoundPlayer (or PlaySync with using).

### H3: Shared Random and unsynchronized callsign counter
- File: 53, 2357-2363
- Severity: High
- Recommendation: Lock or use Interlocked for counter; synchronize Random access.

### H4: Fine-grained locking in FindCandidateAmbientVehicle
- File: 1664-1666
- Severity: High
- Recommendation: Build reserved/active handle sets under lock once.

### H5: Siren protocol verification needed
- File: 2226-2236, 2241-2260, 2524-2534, 2781-2784
- Severity: High
- Recommendation: Verify against CODING_STANDARDS (SET_VEHICLE_SIREN + SET_VEHICLE_HAS_MUTED_SIRENS). Ensure SetSilentEmergencyLights usage correct.

## Medium
### M1: LINQ allocations in hot paths
- File: 184-212, 214-230, 755-761, 809-816, 1490-1541
- Severity: Medium
- Recommendation: Prefer snapshots/indexed loops.

### M2: Blip flash timer reset
- File: BlipService usage (2262-2271, 2663-2679)
- Severity: Medium
- Recommendation: Ensure SafeDeleteBlip does SET_BLIP_FLASH_INTERVAL/TIMER 0 per protocol.

### M3: Reservation re-validation
- File: 1128-1150, 1569-1591
- Severity: Medium
- Recommendation: After reserving, re-check validity (not busy, still driveable, correct agency).

## Low
### L1: Exception swallowing in teardown
- File: 146-151, 156-168, 128-131
- Severity: Low
- Recommendation: Log at Debug/Trace where appropriate.

## Open Questions
1. Does BlipService.SafeDeleteBlip implement full flash-timer reset (SET_BLIP_FLASH_INTERVAL 0, SET_BLIP_FLASH_TIMER 0)?
2. Is cooperative shutdown acceptable instead of Abort?
3. Are scanner audio strings valid for target LSPDFR?
4. Can reservation be hardened to fully prevent dual ownership?
5. Should IsBackupCruiser exclude Dismissing for drafting?
