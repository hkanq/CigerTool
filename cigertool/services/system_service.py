from __future__ import annotations

import os
from pathlib import Path

from ..config import (
    APP_DIR_NAME,
    ISOS_LINUX_ROOT,
    ISOS_TOOLS_ROOT,
    ISOS_WINDOWS_ROOT,
    LEGACY_ISO_LIBRARY_ROOT,
    TOOLS_ROOT,
    resolve_adk_root,
)


class SystemEnvironmentService:
    @staticmethod
    def is_winpe() -> bool:
        return os.environ.get("SYSTEMDRIVE", "").upper() == "X:"

    @staticmethod
    def adk_installed() -> bool:
        return resolve_adk_root() is not None

    @staticmethod
    def removable_roots() -> list[Path]:
        roots = []
        for letter in "DEFGHIJKLMNOPQRSTUVWXYZ":
            root = Path(f"{letter}:\\")
            if root.exists():
                roots.append(root)
        return roots

    @classmethod
    def tool_roots(cls) -> list[Path]:
        roots: list[Path] = []
        for candidate in [
            TOOLS_ROOT,
            Path(f"X:\\{APP_DIR_NAME}\\tools"),
            Path(f"X:\\tools"),
            *(root / "tools" for root in cls.removable_roots()),
        ]:
            if candidate.exists():
                roots.append(candidate)
        unique: list[Path] = []
        seen: set[str] = set()
        for item in roots:
            rendered = str(item).lower()
            if rendered in seen:
                continue
            seen.add(rendered)
            unique.append(item)
        return unique

    @classmethod
    def iso_roots(cls) -> list[Path]:
        roots: list[Path] = []
        for candidate in [
            ISOS_WINDOWS_ROOT,
            ISOS_LINUX_ROOT,
            ISOS_TOOLS_ROOT,
            LEGACY_ISO_LIBRARY_ROOT,
            *(root / "isos" / "windows" for root in cls.removable_roots()),
            *(root / "isos" / "linux" for root in cls.removable_roots()),
            *(root / "isos" / "tools" for root in cls.removable_roots()),
            *(root / "iso-library" for root in cls.removable_roots()),
        ]:
            if candidate.exists():
                roots.append(candidate)
        unique: list[Path] = []
        seen: set[str] = set()
        for item in roots:
            rendered = str(item).lower()
            if rendered in seen:
                continue
            seen.add(rendered)
            unique.append(item)
        return unique
