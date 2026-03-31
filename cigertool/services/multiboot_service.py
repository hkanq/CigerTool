from __future__ import annotations

import json
import logging
import os
from pathlib import Path

from ..logger import get_logger
from ..models import BootStrategy, IsoCategory, IsoEntry, IsoProfile, IsoSupportStatus


class MultibootService:
    _SECTION_ORDER = {
        "windows": 0,
        "linux": 1,
        "tools": 2,
        "legacy": 3,
        "other": 4,
    }

    def __init__(self, logger: logging.Logger | None = None) -> None:
        self.logger = logger or get_logger()

    def scan_isos(self, roots: list[Path]) -> list[IsoEntry]:
        found: list[IsoEntry] = []
        seen: set[str] = set()
        for root in roots:
            if not root.exists():
                continue
            for current_root, _, files in os.walk(root, onerror=lambda _: None):
                for filename in files:
                    if not filename.lower().endswith(".iso"):
                        continue
                    path = Path(current_root) / filename
                    key = self._dedupe_key(path)
                    if key in seen:
                        continue
                    seen.add(key)
                    found.append(self.profile_iso(path, root))
        return sorted(found, key=self._sort_key)

    def profile_iso(self, path: Path, root: Path | None = None) -> IsoEntry:
        try:
            size = path.stat().st_size
        except OSError:
            size = 0

        source_root, library_root, library_section, relative_path = self._classify_library(path, root)
        category = self._detect_category(path, library_section)
        profile = self._detect_profile(path, category)
        strategy = self._detect_strategy(profile)
        companion_config = self._find_companion_grub(path)
        notes: list[str] = []
        kernel_path: str | None = None
        initrd_path: str | None = None
        efi_boot_path: str | None = None
        support_status = IsoSupportStatus.UNTESTED
        failure_reason: str | None = None

        notes.append(f"ISO Library bolumu: {self._section_label(library_section)}")
        if library_root and library_root != source_root:
            notes.append(f"Kutuphane koku: {library_root}")
        elif source_root:
            notes.append(f"Kaynak kok: {source_root}")

        if relative_path:
            notes.append(f"Goreli yol: {relative_path}")

        if companion_config:
            strategy = BootStrategy.CUSTOM_CONFIG
            notes.append("Bu ISO icin ozel GRUB config bulundu.")

        if profile is IsoProfile.WINDOWS:
            efi_boot_path = "/efi/boot/bootx64.efi"
            notes.append("Windows ISO algilandi. WIMBOOT tercih edilir.")
        elif profile is IsoProfile.UBUNTU_DEBIAN:
            kernel_path = "/casper/vmlinuz"
            initrd_path = "/casper/initrd"
            notes.append("Ubuntu/Debian turevi olarak profillendi.")
        elif profile is IsoProfile.ARCH:
            kernel_path = "/arch/boot/x86_64/vmlinuz-linux"
            initrd_path = "/arch/boot/x86_64/initramfs-linux.img"
            notes.append("Arch tabanli ISO profili secildi.")
        elif profile is IsoProfile.TOOLS:
            efi_boot_path = "/efi/boot/bootx64.efi"
            notes.append("Arac ISO'su icin EFI chainload denenir.")
        else:
            notes.append("Genel fallback stratejisi kullanilir.")

        sidecar = self._load_sidecar(path)
        if sidecar:
            category = self._safe_enum(IsoCategory, sidecar.get("category"), category)
            profile = self._safe_enum(IsoProfile, sidecar.get("profile"), profile)
            strategy = self._safe_enum(BootStrategy, sidecar.get("boot_strategy"), strategy)
            kernel_path = sidecar.get("kernel_path", kernel_path)
            initrd_path = sidecar.get("initrd_path", initrd_path)
            efi_boot_path = sidecar.get("efi_boot_path", efi_boot_path)
            support_status = self._safe_enum(IsoSupportStatus, sidecar.get("support_status"), support_status)
            failure_reason = sidecar.get("failure_reason", failure_reason)
            custom_note = sidecar.get("note")
            notes.append("Yan dosya ile ISO profili gecersiz kilindi ve yeniden tanimlandi.")
            if custom_note:
                notes.append(str(custom_note))

        assessed_status, assessed_reason, assessed_note = self._assess_support(
            category=category,
            profile=profile,
            strategy=strategy,
            companion_config=companion_config,
            kernel_path=kernel_path,
            initrd_path=initrd_path,
            efi_boot_path=efi_boot_path,
        )
        if support_status is IsoSupportStatus.UNTESTED and failure_reason is None:
            support_status = assessed_status
            failure_reason = assessed_reason
        elif support_status is IsoSupportStatus.UNSUPPORTED and failure_reason is None:
            failure_reason = assessed_reason or "incompatible ISO type"
        elif support_status is IsoSupportStatus.SUPPORTED:
            failure_reason = None

        if assessed_note and assessed_note not in notes:
            notes.append(assessed_note)
        if failure_reason:
            notes.append(f"Boot hatasi durumunda gosterilecek neden: {failure_reason}")

        self._log_entry(path, support_status, failure_reason, strategy)

        return IsoEntry(
            name=path.name,
            path=str(path),
            size_bytes=size,
            source_root=source_root,
            library_root=library_root,
            library_section=library_section,
            relative_path=relative_path,
            category=category,
            profile=profile,
            boot_strategy=strategy,
            support_status=support_status,
            kernel_path=kernel_path,
            initrd_path=initrd_path,
            efi_boot_path=efi_boot_path,
            companion_config=str(companion_config) if companion_config else None,
            failure_reason=failure_reason,
            notes=notes,
        )

    @staticmethod
    def _dedupe_key(path: Path) -> str:
        try:
            return str(path.resolve()).lower()
        except OSError:
            return str(path).lower()

    @classmethod
    def _sort_key(cls, item: IsoEntry) -> tuple[int, int, str, str]:
        support_order = {
            IsoSupportStatus.SUPPORTED: 0,
            IsoSupportStatus.UNTESTED: 1,
            IsoSupportStatus.UNSUPPORTED: 2,
        }
        return (
            cls._SECTION_ORDER.get(item.library_section, 99),
            support_order.get(item.support_status, 99),
            item.relative_path or item.name.lower(),
            item.name.lower(),
        )

    @staticmethod
    def _path_relative_to(path: Path, root: Path) -> str | None:
        try:
            relative = path.resolve().relative_to(root.resolve())
        except (OSError, ValueError):
            return None
        return str(relative).replace("\\", "/")

    @classmethod
    def _classify_library(cls, path: Path, root: Path | None) -> tuple[str | None, str | None, str, str | None]:
        source_root = str(root.resolve()) if root and root.exists() else (str(root) if root else None)
        library_root = source_root
        library_section = "other"
        relative_path = cls._path_relative_to(path, root) if root else None

        if root:
            root_parts = [part.lower() for part in root.parts]
            relative_parts = [part.lower() for part in Path(relative_path).parts] if relative_path else []

            if "isos" in root_parts:
                library_root = str(root.resolve())
                try:
                    idx = len(root_parts) - 1 - root_parts[::-1].index("isos")
                except ValueError:
                    idx = -1
                if idx >= 0 and idx + 1 < len(root_parts) and root_parts[idx + 1] in {"windows", "linux", "tools"}:
                    library_section = root_parts[idx + 1]
                elif relative_parts and relative_parts[0] in {"windows", "linux", "tools"}:
                    library_section = relative_parts[0]
                else:
                    library_section = "other"
            elif root.name.lower() == "iso-library":
                library_root = str(root.resolve())
                if relative_parts and relative_parts[0] in {"windows", "linux", "tools"}:
                    library_section = relative_parts[0]
                else:
                    library_section = "legacy"

        return source_root, library_root, library_section, relative_path

    @classmethod
    def _section_label(cls, section: str) -> str:
        labels = {
            "windows": "Windows",
            "linux": "Linux",
            "tools": "Araclar",
            "legacy": "Legacy",
            "other": "Diger",
        }
        return labels.get(section, section.title())

    @staticmethod
    def _detect_category(path: Path, library_section: str) -> IsoCategory:
        if library_section == "windows":
            return IsoCategory.WINDOWS
        if library_section == "linux":
            return IsoCategory.LINUX
        if library_section == "tools":
            return IsoCategory.TOOLS
        filename = path.name.lower()
        if any(token in filename for token in ("windows", "win10", "win11", "server")):
            return IsoCategory.WINDOWS
        if "linux" in filename:
            return IsoCategory.LINUX
        if any(token in filename for token in ("tool", "rescue", "diagnostic", "utility")):
            return IsoCategory.TOOLS
        return IsoCategory.OTHER

    @staticmethod
    def _detect_profile(path: Path, category: IsoCategory) -> IsoProfile:
        rendered = path.name.lower()
        if category is IsoCategory.WINDOWS or any(token in rendered for token in ("windows", "win10", "win11", "server", "winpe")):
            return IsoProfile.WINDOWS
        if any(token in rendered for token in ("ubuntu", "debian", "mint", "pop-os", "kubuntu", "xubuntu", "zorin")):
            return IsoProfile.UBUNTU_DEBIAN
        if any(token in rendered for token in ("arch", "manjaro", "endeavour", "garuda")):
            return IsoProfile.ARCH
        if category is IsoCategory.TOOLS:
            return IsoProfile.TOOLS
        return IsoProfile.OTHER

    @staticmethod
    def _detect_strategy(profile: IsoProfile) -> BootStrategy:
        if profile is IsoProfile.WINDOWS:
            return BootStrategy.WIMBOOT
        if profile in {IsoProfile.UBUNTU_DEBIAN, IsoProfile.ARCH}:
            return BootStrategy.LINUX_LOOPBACK
        if profile is IsoProfile.TOOLS:
            return BootStrategy.EFI_CHAINLOAD
        return BootStrategy.FALLBACK

    @staticmethod
    def _find_companion_grub(path: Path) -> Path | None:
        candidates = [
            path.with_suffix(".grub.cfg"),
            path.with_name(path.stem + ".cfg"),
        ]
        for candidate in candidates:
            if candidate.exists():
                return candidate
        return None

    @staticmethod
    def _load_sidecar(path: Path) -> dict | None:
        sidecar = path.with_suffix(".cigertool.json")
        if not sidecar.exists():
            return None
        try:
            return json.loads(sidecar.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return None

    @staticmethod
    def _safe_enum(enum_cls, raw_value: object, default):
        if raw_value in (None, ""):
            return default
        try:
            return enum_cls(raw_value)
        except ValueError:
            return default

    @staticmethod
    def _assess_support(
        *,
        category: IsoCategory,
        profile: IsoProfile,
        strategy: BootStrategy,
        companion_config: Path | None,
        kernel_path: str | None,
        initrd_path: str | None,
        efi_boot_path: str | None,
    ) -> tuple[IsoSupportStatus, str | None, str | None]:
        if companion_config or strategy is BootStrategy.CUSTOM_CONFIG:
            return IsoSupportStatus.SUPPORTED, None, "Durum: ozel config sayesinde dogrudan destekleniyor."

        if profile is IsoProfile.WINDOWS:
            return (
                IsoSupportStatus.UNTESTED,
                None,
                "Durum: standart Windows ISO olarak WIMBOOT / EFI fallback ile denenecek.",
            )

        if profile in {IsoProfile.UBUNTU_DEBIAN, IsoProfile.ARCH}:
            if not kernel_path:
                return IsoSupportStatus.UNSUPPORTED, "unsupported kernel", "Durum: kernel yolu belirlenemedi."
            if not initrd_path:
                return IsoSupportStatus.UNSUPPORTED, "missing boot files", "Durum: initrd dosyasi tanimlanmadi."
            return (
                IsoSupportStatus.UNTESTED,
                None,
                "Durum: kernel ve initrd biliniyor, loopback ile test edilmeden hazirlandi.",
            )

        if strategy is BootStrategy.EFI_CHAINLOAD or profile is IsoProfile.TOOLS:
            if not efi_boot_path:
                return IsoSupportStatus.UNSUPPORTED, "missing boot files", "Durum: EFI boot dosyasi tanimli degil."
            return (
                IsoSupportStatus.UNTESTED,
                None,
                "Durum: EFI chainload denenebilir, fakat ISO icerigi build sirasinda dogrulanmadi.",
            )

        if category is IsoCategory.LINUX:
            return IsoSupportStatus.UNSUPPORTED, "unsupported kernel", "Durum: Linux ISO profili desteklenmiyor."

        return IsoSupportStatus.UNSUPPORTED, "incompatible ISO type", "Durum: otomatik boot profili eslesmedi."

    def _log_entry(
        self,
        path: Path,
        support_status: IsoSupportStatus,
        failure_reason: str | None,
        strategy: BootStrategy,
    ) -> None:
        if support_status is IsoSupportStatus.UNSUPPORTED:
            self.logger.warning(
                "ISO desteklenmiyor: %s | neden=%s | strateji=%s | yol=%s",
                path.name,
                failure_reason or "belirtilmedi",
                strategy.value,
                path,
            )
            return
        self.logger.info(
            "ISO profillendi: %s | durum=%s | strateji=%s | yol=%s",
            path.name,
            support_status.value,
            strategy.value,
            path,
        )
