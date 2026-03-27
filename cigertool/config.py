from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import tempfile


APP_NAME = "CigerTool"
DEFAULT_LANGUAGE = "tr"
DEFAULT_THEME = "ciger-dark"
MOUNT_ROOT = Path("/mnt/cigertool")

SUPPORTED_PARTCLONE_TOOLS = {
    "ext2": "partclone.extfs",
    "ext3": "partclone.extfs",
    "ext4": "partclone.extfs",
    "fat16": "partclone.fat16",
    "fat32": "partclone.fat32",
    "vfat": "partclone.fat32",
    "btrfs": "partclone.btrfs",
    "xfs": "partclone.xfs",
    "ntfs": "partclone.ntfs",
}

LOG_PATH_CANDIDATES = (
    Path("/var/log/cigertool.log"),
    Path.cwd() / "cigertool.log",
    Path(tempfile.gettempdir()) / "cigertool.log",
)


@dataclass(slots=True)
class AppSettings:
    language: str = DEFAULT_LANGUAGE
    theme: str = DEFAULT_THEME
    dry_run: bool = True


def resolve_log_path() -> Path:
    for candidate in LOG_PATH_CANDIDATES:
        try:
            candidate.parent.mkdir(parents=True, exist_ok=True)
            if candidate.exists():
                return candidate
            candidate.touch(exist_ok=True)
            return candidate
        except OSError:
            continue

    fallback = Path(tempfile.gettempdir()) / "cigertool.log"
    fallback.touch(exist_ok=True)
    return fallback
