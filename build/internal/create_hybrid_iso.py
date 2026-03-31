from __future__ import annotations

import argparse
import os
import re
from pathlib import Path

import pycdlib


def sanitize_component(name: str, used: set[str], is_dir: bool) -> str:
    stem, ext = os.path.splitext(name)
    stem = re.sub(r"[^A-Z0-9_]", "_", stem.upper()) or "ITEM"
    ext = re.sub(r"[^A-Z0-9_]", "", ext[1:].upper())
    if not is_dir:
        ext = ext[:3]

    for counter in range(1, 1000):
        suffix = "" if counter == 1 else str(counter)
        max_base = max(1, 8 - len(suffix))
        base = stem[:max_base] + suffix
        candidate = base if is_dir or not ext else f"{base}.{ext}"
        if candidate not in used:
            used.add(candidate)
            return candidate

    raise RuntimeError(f"ISO alias could not be generated for {name}")


def build_iso(iso_root: Path, output_path: Path, volume_name: str) -> None:
    iso_root = iso_root.resolve()
    output_path = output_path.resolve()
    path_map: dict[Path, tuple[str, str, str]] = {iso_root: ("", "", "")}
    alias_registry: dict[Path, set[str]] = {}
    relative_iso_paths: dict[str, str] = {}

    iso = pycdlib.PyCdlib()
    iso.new(
        interchange_level=3,
        joliet=3,
        rock_ridge="1.09",
        udf="2.60",
        vol_ident=volume_name,
    )

    for current_root, dirnames, filenames in os.walk(iso_root):
        current_path = Path(current_root)
        parent_iso, parent_joliet, parent_udf = path_map[current_path]
        used = alias_registry.setdefault(current_path, set())

        dirnames.sort()
        filenames.sort()

        for dirname in dirnames:
            child_path = current_path / dirname
            alias = sanitize_component(dirname, used, is_dir=True)
            iso_dir_path = f"{parent_iso}/{alias}" if parent_iso else f"/{alias}"
            joliet_dir_path = f"{parent_joliet}/{dirname}" if parent_joliet else f"/{dirname}"
            udf_dir_path = f"{parent_udf}/{dirname}" if parent_udf else f"/{dirname}"
            iso.add_directory(
                iso_path=iso_dir_path,
                rr_name=dirname,
                joliet_path=joliet_dir_path,
                udf_path=udf_dir_path,
            )
            path_map[child_path] = (iso_dir_path, joliet_dir_path, udf_dir_path)
            relative_iso_paths[str(child_path.relative_to(iso_root)).replace("\\", "/").lower()] = iso_dir_path

        for filename in filenames:
            source_path = current_path / filename
            alias = sanitize_component(filename, used, is_dir=False)
            iso_file_path = f"{parent_iso}/{alias};1" if parent_iso else f"/{alias};1"
            joliet_file_path = f"{parent_joliet}/{filename}" if parent_joliet else f"/{filename}"
            udf_file_path = f"{parent_udf}/{filename}" if parent_udf else f"/{filename}"
            iso.add_file(
                str(source_path),
                iso_path=iso_file_path,
                rr_name=filename,
                joliet_path=joliet_file_path,
                udf_path=udf_file_path,
            )
            relative_iso_paths[str(source_path.relative_to(iso_root)).replace("\\", "/").lower()] = iso_file_path

    bios_boot_path = relative_iso_paths["boot/etfsboot.com"]
    uefi_boot_path = relative_iso_paths["efi/microsoft/boot/efisys.bin"]
    iso.add_eltorito(
        bios_boot_path,
        bootcatfile="/BOOT.CAT;1",
        platform_id=0,
        media_name="noemul",
        boot_load_size=4,
    )
    iso.add_eltorito(
        uefi_boot_path,
        bootcatfile="/EFI.CAT;1",
        platform_id=0xEF,
        efi=True,
        media_name="noemul",
    )

    iso.write(str(output_path))
    iso.close()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--volume-name", default="CIGERTOOL")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    build_iso(Path(args.source), Path(args.output), args.volume_name)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
