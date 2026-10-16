#!/usr/bin/env python3
"""
validate_natives.py - Cadence Native & Architectural Integrity Validator

Automated static analysis script for GTA V / RPH native invocations and
architectural invariants in the Cadence plugin codebase.

Checks performed:
  [A] Banned / Corrupted Hashes:
      - 0x2497C4717C8B881E in siren context or raw hex call (SET_VEHICLE_ENGINE_ON).
      - xF4924635A19EB37D / xD8050E0EB60CF274 raw hex calls in siren muting.
  [B] Weapon Hash Integrity:
      - Banned: 0xA2719248 (corrupted unarmed hash). Permanent zero-tolerance ban.
      - Required: 0xA2719263 (verified JOAAT hash for WEAPON_UNARMED).
  [C] Void Native Return Type Mismatch:
      - Banned generic return type parameters on void natives:
        e.g., TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE<...>.
  [D] Canonical Native Casing:
      - Banned PascalCase / non-canonical names (e.g., GetClosestVehicleNodeWithHeading).
      - Required: Canonical uppercase (e.g., GET_CLOSEST_VEHICLE_NODE_WITH_HEADING).
  [E] Engine Value Clamps:
      - SET_DRIVER_ABILITY must not exceed 1.0f (banned: 100, 100.0f; range: [0.0, 1.0]).
      - SET_DRIVER_AGGRESSIVENESS range: [0.0, 1.0].
  [F] Canonical Uppercase for NativeFunction.Natives:
      - All NativeFunction.Natives.* calls must use uppercase letters, digits, and underscores.
      - Raw hex calls (x...) are prohibited unless explicitly whitelisted.
      - NativeFunction.CallByName string arguments must use canonical uppercase.
  [G] SafeDeleteBlip Reference Semantics:
      - All call-sites to SafeDeleteBlip must pass arguments with the 'ref' keyword.
  [H] Banned Pointer Native REMOVE_BLIP:
      - REMOVE_BLIP takes a Blip* pointer in GTA V engine; RPH invocation causes AccessViolationException. Must use RPH Blip.Delete().
  [I] Police Blip Alternate Flash Banned:
      - alternateFlash: true is banned on police vehicle blips (produces red suspect flash).
  [J] LHandle Reference Equality Banned:
      - Direct equality comparison (== or !=) on LHandle instances is banned.
  [K] Directory Filtering:
      - Excludes bin/ and obj/ directories from scanning.

Exit Code:
  0 - All validation checks passed cleanly.
  1 - One or more integrity violations detected.
"""

import sys
import os
import re
from pathlib import Path
from typing import List, Tuple, Dict

class NativeValidator:
    def __init__(self, src_dir: Path):
        self.src_dir = src_dir
        self.violations: List[Dict[str, str]] = []
        self.scanned_files_count = 0
        self.scanned_native_calls = 0
        self.found_verified_unarmed_hash = False

    def add_violation(self, file_path: Path, line_no: int, rule_id: str, message: str, line_content: str):
        rel_path = file_path.relative_to(self.src_dir.parent) if self.src_dir.parent in file_path.parents else file_path
        self.violations.append({
            "file": str(rel_path),
            "line": line_no,
            "rule": rule_id,
            "message": message,
            "snippet": line_content.strip()
        })

    def scan_file(self, file_path: Path):
        self.scanned_files_count += 1
        with open(file_path, "r", encoding="utf-8", errors="replace") as f:
            lines = f.readlines()

        for idx, line in enumerate(lines, start=1):
            stripped = line.strip()
            # Skip single-line comments that are pure documentation (unless checking banned comments/hashes)
            is_comment = stripped.startswith("//") or stripped.startswith("/*") or stripped.startswith("*")

            # [A] Banned / Corrupted Hashes in siren context or raw hex calls
            if "0x2497C4717C8B881E" in line or "x2497C4717C8B881E" in line:
                if "siren" in line.lower() or "NativeFunction.Natives.x2497C4717C8B881E" in line:
                    self.add_violation(
                        file_path, idx, "RULE-A-CORRUPTED-SIREN-HASH",
                        "Hash 0x2497C4717C8B881E is SET_VEHICLE_ENGINE_ON, banned in siren/muting context.",
                        line
                    )

            if "xF4924635A19EB37D" in line or "xD8050E0EB60CF274" in line:
                self.add_violation(
                    file_path, idx, "RULE-A-RAW-HEX-SIREN-CALL",
                    "Raw hex calls xF4924635A19EB37D / xD8050E0EB60CF274 are banned. Use canonical SET_VEHICLE_SIREN and SET_VEHICLE_HAS_MUTED_SIRENS.",
                    line
                )

            # [B] Weapon Hash Integrity: Permanent ban on corrupted hash 0xA2719248
            if re.search(r'0xA2719248\b', line, re.IGNORECASE):
                self.add_violation(
                    file_path, idx, "RULE-B-BANNED-WEAPON-HASH",
                    "Corrupted WEAPON_UNARMED hash 0xA2719248 detected. Permanently banned. Must be 0xA2719263.",
                    line
                )

            # [B] Verify required Joaat hash 0xA2719263 for unarmed weapon definitions
            if "WeaponUnarmed" in line or "WEAPON_UNARMED" in line:
                if "0xA2719263" in line or "0xa2719263" in line:
                    self.found_verified_unarmed_hash = True
                elif "=" in line and not is_comment:
                    # An assignment to WeaponUnarmed/WEAPON_UNARMED without the verified hash
                    if not re.search(r'0xA2719263\b', line, re.IGNORECASE):
                        self.add_violation(
                            file_path, idx, "RULE-B-INVALID-UNARMED-HASH",
                            "Unarmed weapon hash definition does not use the verified JOAAT hash 0xA2719263.",
                            line
                        )

            # [C] Void Native with Generic Type Parameter
            if re.search(r'TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE\s*<', line):
                self.add_violation(
                    file_path, idx, "RULE-C-VOID-NATIVE-GENERIC",
                    "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE returns void and cannot take generic type argument <T>.",
                    line
                )

            # [D] Non-Canonical Casing / PascalCase calls
            if "GetClosestVehicleNodeWithHeading" in line:
                self.add_violation(
                    file_path, idx, "RULE-D-NON-CANONICAL-CASING",
                    "Non-canonical PascalCase 'GetClosestVehicleNodeWithHeading' detected. Use canonical 'GET_CLOSEST_VEHICLE_NODE_WITH_HEADING'.",
                    line
                )

            # [E] Engine Value Clamps: SET_DRIVER_ABILITY in [0.0, 1.0]
            ability_match = re.search(r'SET_DRIVER_ABILITY\s*\(\s*[^,]+,\s*([+-]?[0-9]+(?:\.[0-9]+)?f?)\s*\)', line)
            if ability_match:
                val_str = ability_match.group(1).rstrip('fF')
                try:
                    val = float(val_str)
                    if val < 0.0 or val > 1.0:
                        self.add_violation(
                            file_path, idx, "RULE-E-DRIVER-ABILITY-BOUNDS",
                            f"SET_DRIVER_ABILITY parameter {val_str} is out of engine bounds [0.0, 1.0].",
                            line
                        )
                except ValueError:
                    pass

            # [E] Engine Value Clamps: SET_DRIVER_AGGRESSIVENESS in [0.0, 1.0]
            aggr_match = re.search(r'SET_DRIVER_AGGRESSIVENESS\s*\(\s*[^,]+,\s*([+-]?[0-9]+(?:\.[0-9]+)?f?)\s*\)', line)
            if aggr_match:
                val_str = aggr_match.group(1).rstrip('fF')
                try:
                    val = float(val_str)
                    if val < 0.0 or val > 1.0:
                        self.add_violation(
                            file_path, idx, "RULE-E-DRIVER-AGGRESSIVENESS-BOUNDS",
                            f"SET_DRIVER_AGGRESSIVENESS parameter {val_str} is out of engine bounds [0.0, 1.0].",
                            line
                        )
                except ValueError:
                    pass

            # [F] Canonical Uppercase for NativeFunction.Natives
            if not is_comment:
                native_calls = re.findall(r'NativeFunction\.Natives\.([A-Za-z0-9_]+)', line)
                for call_name in native_calls:
                    self.scanned_native_calls += 1
                    # Check for raw hex calls (e.g., x2497C4717C8B881E)
                    if re.match(r'^x[0-9A-Fa-f]{8,16}$', call_name):
                        self.add_violation(
                            file_path, idx, "RULE-F-RAW-HEX-NATIVE",
                            f"Raw hex native invocation '{call_name}' is not permitted. Use canonical named native.",
                            line
                        )
                    # Check for uppercase convention
                    elif not call_name.isupper():
                        self.add_violation(
                            file_path, idx, "RULE-F-NATIVE-NOT-UPPERCASE",
                            f"Native invocation '{call_name}' does not use canonical uppercase naming.",
                            line
                        )

                # Also verify NativeFunction.CallByName string arguments
                call_by_name_matches = re.finditer(r'NativeFunction\.CallByName(?:<[^>]+>)?\s*\(\s*"([^"]+)"', line)
                for cbn in call_by_name_matches:
                    cbn_name = cbn.group(1)
                    self.scanned_native_calls += 1
                    if not cbn_name.isupper():
                        self.add_violation(
                            file_path, idx, "RULE-F-CALLBYNAME-NOT-UPPERCASE",
                            f"NativeFunction.CallByName invocation with non-canonical casing: \"{cbn_name}\". Must be uppercase.",
                            line
                        )

            # [G] SafeDeleteBlip Pass-by-Ref Semantics
            if "SafeDeleteBlip" in line and not is_comment:
                # Exclude method definition itself: void SafeDeleteBlip(ref Blip blip)
                if not re.search(r'(?:public|private|internal|protected|static)\s+void\s+SafeDeleteBlip', line):
                    # Check invocations of SafeDeleteBlip(...)
                    blip_call_matches = re.finditer(r'(?:BlipService\.)?SafeDeleteBlip\s*\(\s*([^)]*)\)', line)
                    for match in blip_call_matches:
                        arg_expr = match.group(1).strip()
                        if not arg_expr.startswith("ref "):
                            self.add_violation(
                                file_path, idx, "RULE-G-SAFEDELETEBLIP-REF",
                                f"SafeDeleteBlip invocation '{match.group(0)}' does not pass argument by 'ref'.",
                                line
                            )

            # [H] Banned Pointer Native REMOVE_BLIP
            if "REMOVE_BLIP" in line and not is_comment:
                self.add_violation(
                    file_path, idx, "RULE-H-BANNED-POINTER-NATIVE",
                    "REMOVE_BLIP is strictly banned. In GTA V engine it takes a Blip* pointer; invoking via RPH throws AccessViolationException. Use RPH Blip.Delete() instead.",
                    line
                )

            # [I] Police Blip Alternate Flash Banned (CODING_STANDARDS.md Section 7.3)
            if ("AttachPursuitBlip" in line or "TransitionToPursuit" in line or "AttachPatrolBlip" in line) and "alternateFlash: true" in line and not is_comment:
                self.add_violation(
                    file_path, idx, "RULE-I-BANNED-ALTERNATE-FLASH",
                    "alternateFlash: true is strictly banned on police vehicle blips per CODING_STANDARDS.md Section 7.3 (produces red suspect flash).",
                    line
                )

            # [J] LHandle Reference Equality Banned (CODING_STANDARDS.md Section 8.4)
            if re.search(r'(!=|==)\s*(?:activePursuit|_enrolledPursuitHandle)', line) and not is_comment:
                self.add_violation(
                    file_path, idx, "RULE-J-LHANDLE-REFERENCE-EQUALITY",
                    "Direct equality comparison on LHandle instances is banned. LHandle does not override Equals/operator==; use Functions.IsPursuitStillRunning(handle).",
                    line
                )

    def run(self) -> int:
        print("=" * 80)
        print("CADENCE NATIVE & ARCHITECTURAL INTEGRITY VALIDATOR")
        print(f"Target Directory: {self.src_dir}")
        print("=" * 80)

        # Exclude bin/ and obj/ directories [F4, F26, Rule K]
        all_cs_files = sorted(list(self.src_dir.rglob("*.cs")))
        cs_files = [f for f in all_cs_files if not any(p.lower() in ("bin", "obj") for p in f.parts)]

        if not cs_files:
            print(f"[ERROR] No .cs source files found in {self.src_dir}")
            return 1

        for cs_file in cs_files:
            self.scan_file(cs_file)

        # Global Invariant Verification: Required Joaat hash 0xA2719263 for unarmed weapon
        if not self.found_verified_unarmed_hash:
            self.violations.append({
                "file": "src/",
                "line": 0,
                "rule": "RULE-B-MISSING-UNARMED-HASH",
                "message": "Verified JOAAT hash 0xA2719263 for WEAPON_UNARMED was not found anywhere in the codebase.",
                "snippet": "const uint WeaponUnarmed = 0xA2719263;"
            })

        print(f"Scanned {self.scanned_files_count} C# source files (excluded bin/ and obj/).")
        print(f"Scanned {self.scanned_native_calls} native call sites.")
        print("-" * 80)

        if not self.violations:
            print("[SUCCESS] All native invocations and architectural checks PASSED cleanly (0 violations).")
            print("=" * 80)
            return 0

        print(f"[FAILURE] Detected {len(self.violations)} architectural / native violations:\n")
        for v in self.violations:
            print(f"  [{v['rule']}] {v['file']}:{v['line']}")
            print(f"    Message: {v['message']}")
            print(f"    Code:    {v['snippet']}")
            print()

        print("=" * 80)
        print(f"Validation FAILED with {len(self.violations)} violation(s).")
        return 1

def main():
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent

    src_dir = repo_root / "src"
    if len(sys.argv) > 1:
        src_dir = Path(sys.argv[1]).resolve()

    if not src_dir.exists() or not src_dir.is_dir():
        print(f"[ERROR] Source directory does not exist: {src_dir}")
        sys.exit(1)

    validator = NativeValidator(src_dir)
    sys.exit(validator.run())

if __name__ == "__main__":
    main()
