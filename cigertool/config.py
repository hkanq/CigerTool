from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os
import tempfile


APP_NAME = "CigerTool by hkannq"
ISO_NAME = "CigerTool-by-hkannq.iso"
APP_DIR_NAME = "CigerTool"
COMPANY_NAME = "hkannq"
DEFAULT_THEME = "turkuaz"
DEFAULT_LANGUAGE = "tr"

APP_ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = APP_ROOT.parent
TOOLS_ROOT = PROJECT_ROOT / "tools"
ASSETS_ROOT = PROJECT_ROOT / "cigertool" / "assets"
WINPE_ROOT = PROJECT_ROOT / "winpe"
BUILD_ROOT = PROJECT_ROOT / "build"
ARTIFACT_ROOT = PROJECT_ROOT / "artifacts"
ISOS_ROOT = PROJECT_ROOT / "isos"
ISOS_WINDOWS_ROOT = ISOS_ROOT / "windows"
ISOS_LINUX_ROOT = ISOS_ROOT / "linux"
ISOS_TOOLS_ROOT = ISOS_ROOT / "tools"
ISO_LIBRARY_ROOT = ISOS_ROOT
LEGACY_ISO_LIBRARY_ROOT = PROJECT_ROOT / "iso-library"
LOG_PATH = Path(tempfile.gettempdir()) / "cigertool.log"

ADK_ROOT_CANDIDATES = [
    Path(r"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit"),
    Path(r"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit"),
]


@dataclass(slots=True)
class AppSettings:
    theme: str = DEFAULT_THEME
    language: str = DEFAULT_LANGUAGE
    dry_run: bool = True
    temp_dir: Path = Path(tempfile.gettempdir()) / "cigertool"


def resolve_adk_root() -> Path | None:
    env_value = os.environ.get("CIGERTOOL_ADK_ROOT")
    if env_value:
        candidate = Path(env_value)
        if candidate.exists():
            return candidate
    for candidate in ADK_ROOT_CANDIDATES:
        if candidate.exists():
            return candidate
    return None
