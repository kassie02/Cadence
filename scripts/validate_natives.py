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
      - Banned: 0xA2719248 (corrupted unarmed hash).
      - Required: 0xA2719263 (verified JOAAT hash for WEAPON_UNARMED).
  [C] Void Native Return Type Mismatch:
      - Banned generic return type parameters on void natives:
        e.g., TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE<...>.
  [D] Canonical Native Casing:
      - Banned PascalCase / non-canonical names (e.g., GetClosestVehicleNodeWithHeading).
      - Required: Canonical uppercase (e.g., GET_CLOSEST_VEHICLE_NODE_WITH_HEADING).
  [E] Engine Value Clamps:
      - SET_DRIVER_ABILITY must not exceed 1.0f (banned: 100, 100.0f).
  [F] Canonical Uppercase for NativeFunction.Natives:
      - All NativeFunction.Natives.* calls must use uppercase letters, digits, and underscores.
      - Raw hex calls (x...) are prohibited unless explicitly whitelisted.
  [G] SafeDeleteBlip Reference Semantics:
      - All call-sites to SafeDeleteBlip must pass arguments with the 'ref' keyword.
  [H] Banned Pointer Native REMOVE_BLIP:
      - REMOVE_BLIP takes a Blip* pointer in GTA V engine; RPH invocation causes AccessViolationException. Must use RPH Blip.Delete().

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

            # [B] Weapon Hash Integrity
            if re.search(r'0xA2719248\b', line, re.IGNORECASE):
                self.add_violation(
                    file_path, idx, "RULE-B-INVALID-WEAPON-HASH",
                    "Corrupted WEAPON_UNARMED hash 0xA2719248 detected. Must be 0xA2719263.",
                    line
                )

            # [C] Void Native with Generic Type Parameter
            if re.search(r'TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE\s*<', line):
                self.add_violation(
                    file_path, idx, "RULE-C-VOID-NATIVE-GENERIC",
                    "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE returns void and cannot take generic type argument <T>.",
                    line
                )

            # [D] Non-Canonical Casing
            if "GetClosestVehicleNodeWithHeading" in line:
                self.add_violation(
                    file_path, idx, "RULE-D-NON-CANONICAL-CASING",
                    "Non-canonical PascalCase 'GetClosestVehicleNodeWithHeading' detected. Use canonical 'GET_CLOSEST_VEHICLE_NODE_WITH_HEADING'.",
                    line
                )

            # [E] Out-of-bounds Driver Ability
            # SET_DRIVER_ABILITY ceiling in GTA V engine is 1.0f. Values > 1.0f are discarded by engine.
            ability_match = re.search(r'SET_DRIVER_ABILITY\s*\(\s*[^,]+,\s*([0-9]+(?:\.[0-9]+)?f?)\s*\)', line)
            if ability_match:
                val_str = ability_match.group(1).rstrip('fF')
                try:
                    val = float(val_str)
                    if val > 1.0:
                        self.add_violation(
                            file_path, idx, "RULE-E-DRIVER-ABILITY-BOUNDS",
                            f"SET_DRIVER_ABILITY parameter {val_str} exceeds GTA V engine ceiling of 1.0f.",
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

    def run(self) -> int:
        print("=" * 80)
        print("CADENCE NATIVE & ARCHITECTURAL INTEGRITY VALIDATOR")
        print(f"Target Directory: {self.src_dir}")
        print("=" * 80)

        cs_files = sorted(list(self.src_dir.rglob("*.cs")))
        if not cs_files:
            print(f"[ERROR] No .cs source files found in {self.src_dir}")
            return 1

        for cs_file in cs_files:
            self.scan_file(cs_file)

        print(f"Scanned {self.scanned_files_count} C# source files.")
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
    # Resolve src directory relative to script or current working directory
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
