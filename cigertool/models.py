from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any


class CloneMode(str, Enum):
    RAW = "raw"
    SMART = "smart"
    SYSTEM = "system"


class DiskBus(str, Enum):
    SATA = "SATA"
    NVME = "NVMe"
    USB = "USB"
    VIRTUAL = "Virtual"
    UNKNOWN = "Unknown"


class PartitionRole(str, Enum):
    EFI = "EFI"
    MSR = "MSR"
    WINDOWS = "WINDOWS"
    RECOVERY = "RECOVERY"
    DATA = "DATA"
    BASIC = "BASIC"
    UNKNOWN = "UNKNOWN"


class PlanRisk(str, Enum):
    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"
    BLOCKED = "blocked"


class IsoCategory(str, Enum):
    WINDOWS = "windows"
    LINUX = "linux"
    TOOLS = "tools"
    OTHER = "other"


class IsoProfile(str, Enum):
    WINDOWS = "windows"
    UBUNTU_DEBIAN = "ubuntu-debian"
    ARCH = "arch"
    TOOLS = "tools"
    OTHER = "other"


class BootStrategy(str, Enum):
    WIMBOOT = "wimboot"
    LINUX_LOOPBACK = "linux-loopback"
    EFI_CHAINLOAD = "efi-chainload"
    CUSTOM_CONFIG = "custom-config"
    FALLBACK = "fallback"


class IsoSupportStatus(str, Enum):
    SUPPORTED = "supported"
    UNTESTED = "untested"
    UNSUPPORTED = "unsupported"


WINDOWS_EFI_GUID = "{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}"
WINDOWS_MSR_GUID = "{E3C9E316-0B5C-4DB8-817D-F92DF00215AE}"
WINDOWS_BASIC_GUID = "{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}"
WINDOWS_RECOVERY_GUID = "{DE94BBA4-06D1-4D40-A16A-BFD50179D6AC}"


def human_bytes(value: int | None) -> str:
    if value is None:
        return "Bilinmiyor"
    size = float(value)
    units = ["B", "KB", "MB", "GB", "TB"]
    for unit in units:
        if size < 1024 or unit == units[-1]:
            return f"{size:.1f} {unit}" if unit != "B" else f"{int(size)} B"
        size /= 1024.0
    return f"{value} B"


@dataclass(slots=True)
class Partition:
    disk_number: int
    partition_number: int
    size_bytes: int
    offset_bytes: int | None = None
    fs_type: str | None = None
    label: str | None = None
    drive_letter: str | None = None
    gpt_type: str | None = None
    mbr_type: str | None = None
    used_bytes: int | None = None
    free_bytes: int | None = None
    is_boot: bool = False
    is_system: bool = False
    guid: str | None = None

    @property
    def path(self) -> str:
        return f"Disk {self.disk_number} / Part {self.partition_number}"

    @property
    def role(self) -> PartitionRole:
        gpt = (self.gpt_type or "").upper()
        if gpt == WINDOWS_EFI_GUID:
            return PartitionRole.EFI
        if gpt == WINDOWS_MSR_GUID:
            return PartitionRole.MSR
        if gpt == WINDOWS_RECOVERY_GUID:
            return PartitionRole.RECOVERY
        if (self.fs_type or "").upper() == "NTFS" and self.size_bytes >= 20 * 1024**3:
            return PartitionRole.WINDOWS
        if gpt == WINDOWS_BASIC_GUID:
            return PartitionRole.BASIC
        return PartitionRole.UNKNOWN

    @property
    def can_host_windows(self) -> bool:
        return self.role in {PartitionRole.WINDOWS, PartitionRole.BASIC}


@dataclass(slots=True)
class Disk:
    number: int
    path: str
    friendly_name: str
    serial: str | None
    bus_type: DiskBus
    size_bytes: int
    partition_style: str | None
    health_status: str | None
    operational_status: str | None
    model: str | None = None
    vendor: str | None = None
    partitions: list[Partition] = field(default_factory=list)

    @property
    def used_bytes(self) -> int:
        return sum(part.used_bytes or 0 for part in self.partitions)

    @property
    def summary(self) -> str:
        return (
            f"Disk {self.number} | {self.friendly_name} | {human_bytes(self.size_bytes)} | "
            f"{self.bus_type.value} | Seri: {self.serial or 'Yok'}"
        )


@dataclass(slots=True)
class CloneAnalysis:
    source_disk: Disk
    target_disk: Disk
    source_used_bytes: int
    target_usable_bytes: int
    fits_raw: bool
    fits_smart: bool
    recommended_mode: CloneMode | None
    fits_system: bool = False
    warnings: list[str] = field(default_factory=list)
    blocked_reasons: list[str] = field(default_factory=list)
    system_partition: Partition | None = None
    efi_partition: Partition | None = None
    recovery_partition: Partition | None = None
    data_partitions: list[Partition] = field(default_factory=list)
    required_system_bytes: int = 0
    required_smart_bytes: int = 0


@dataclass(slots=True)
class PlanStep:
    title: str
    command: list[str] | str
    shell: bool = False
    destructive: bool = False
    notes: str | None = None


@dataclass(slots=True)
class OperationPlan:
    mode: CloneMode
    title: str
    summary: str
    warnings: list[str]
    steps: list[PlanStep]
    risk: PlanRisk
    analysis: CloneAnalysis


@dataclass(slots=True)
class ToolEntry:
    name: str
    category: str
    description: str
    launch_path: str | None = None
    download_url: str | None = None
    bundled: bool = False
    layer: str = "USER"
    source_root: str | None = None


@dataclass(slots=True)
class SystemSnapshot:
    disks: list[Disk]
    is_winpe: bool
    adk_installed: bool
    tools: list[ToolEntry]
    multiboot_isos: list["IsoEntry"]


@dataclass(slots=True)
class PathInfo:
    name: str
    path: str
    size_bytes: int
    extra: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class IsoEntry:
    name: str
    path: str
    size_bytes: int
    category: IsoCategory
    profile: IsoProfile
    boot_strategy: BootStrategy
    support_status: IsoSupportStatus
    kernel_path: str | None = None
    initrd_path: str | None = None
    efi_boot_path: str | None = None
    companion_config: str | None = None
    failure_reason: str | None = None
    notes: list[str] = field(default_factory=list)

    @property
    def status_label(self) -> str:
        if self.support_status is IsoSupportStatus.SUPPORTED:
            return "✔ çalışır"
        if self.support_status is IsoSupportStatus.UNTESTED:
            return "⚠ test edilmedi"
        return "❌ desteklenmiyor"
