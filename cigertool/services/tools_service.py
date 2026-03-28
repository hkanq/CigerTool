from __future__ import annotations

from pathlib import Path

from ..models import ToolEntry
from .system_service import SystemEnvironmentService


class ToolsCatalogService:
    def __init__(self, system_service: SystemEnvironmentService) -> None:
        self.system_service = system_service

    def list_tools(self) -> list[ToolEntry]:
        preloaded = self._preloaded_tools()
        items = self._core_tools() + preloaded + self._user_tools(preloaded)
        return sorted(items, key=lambda item: (item.layer, item.category, item.name.lower()))

    @staticmethod
    def _core_tools() -> list[ToolEntry]:
        return [
            ToolEntry("Clone Wizard", "CORE", "Disk klonlama ve tasima sihirbazi.", bundled=True, layer="CORE"),
            ToolEntry("SMART Viewer", "CORE", "Disk saglik ve sicaklik ozeti.", bundled=True, layer="CORE"),
            ToolEntry("Dosya Yoneticisi", "CORE", "USB ve disk icerigini gezmek icin yerlesik yonetici.", bundled=True, layer="CORE"),
            ToolEntry("Boot Repair", "CORE", "EFI ve MBR onarim plani.", bundled=True, layer="CORE"),
            ToolEntry("Terminal", "CORE", "Ileri seviye komut satiri araclari icin shell erisimi.", launch_path="cmd.exe", bundled=True, layer="CORE"),
        ]

    def _preloaded_tools(self) -> list[ToolEntry]:
        expected = [
            ("Google Chrome Portable", "Browser", "Portable tarayici.", "chrome-portable", "ChromePortable.exe"),
            ("CPU-Z Portable", "Diagnostics", "Donanim ozeti ve CPU/RAM bilgisi.", "cpu-z", "cpuz_x64.exe"),
            ("Disk Benchmark Tool", "Benchmark", "Disk hiz testi araci.", "disk-benchmark", "benchmark.exe"),
            ("System Info Tool", "Diagnostics", "Sistem ozeti ve bilesen bilgisi.", "system-info", "systeminfo.exe"),
            ("Network Tools", "Network", "Ag teshis ve baglanti araclari.", "network-tools", "nettools.exe"),
            ("Partition Tool", "Storage", "Bolumleme ve disk duzenleme yardimcisi.", "partition-tool", "partition-tool.exe"),
        ]
        entries: list[ToolEntry] = []
        for name, category, description, folder, filename in expected:
            launch_path, source_root = self._resolve_tool(folder, filename)
            entries.append(
                ToolEntry(
                    name=name,
                    category=category,
                    description=description if launch_path else description + " Bulunamazsa /tools klasorune eklenebilir.",
                    launch_path=launch_path,
                    bundled=bool(launch_path),
                    layer="PRELOADED",
                    source_root=source_root,
                )
            )
        return entries

    def _user_tools(self, preloaded: list[ToolEntry]) -> list[ToolEntry]:
        entries: list[ToolEntry] = []
        seen: set[str] = set()
        reserved = {item.launch_path.lower() for item in preloaded if item.launch_path}
        for root in self.system_service.tool_roots():
            for path in root.rglob("*.exe"):
                key = str(path).lower()
                if key in seen or key in reserved:
                    continue
                seen.add(key)
                category = self._categorize_name(path.name)
                entries.append(
                    ToolEntry(
                        name=path.stem,
                        category=category,
                        description=f"Kullanici araci: {path.name}",
                        launch_path=str(path),
                        bundled=False,
                        layer="USER",
                        source_root=str(root),
                    )
                )
        return entries

    def _resolve_tool(self, folder: str, filename: str) -> tuple[str | None, str | None]:
        for root in self.system_service.tool_roots():
            candidate = root / folder / filename
            if candidate.exists():
                return str(candidate), str(root)
        return None, None

    @staticmethod
    def _categorize_name(name: str) -> str:
        lowered = name.lower()
        if any(token in lowered for token in ("cpu", "info", "hwinfo", "speccy")):
            return "Diagnostics"
        if any(token in lowered for token in ("bench", "crystal", "mark")):
            return "Benchmark"
        if any(token in lowered for token in ("disk", "part", "gpt", "mbr")):
            return "Storage"
        if any(token in lowered for token in ("net", "wifi", "ip", "rdp", "putty")):
            return "Network"
        if any(token in lowered for token in ("chrome", "firefox", "browser")):
            return "Browser"
        return "User Tool"
