from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
import math
import re


GIB = 1024**3
MIB = 1024**2
WINDOWS_ESP_GUID = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b"
WINDOWS_MSR_GUID = "e3c9e316-0b5c-4db8-817d-f92df00215ae"
WINDOWS_RECOVERY_GUID = "de94bba4-06d1-4d40-a16a-bfd50179d6ac"


class CloneMode(str, Enum):
    FULL = "full"
    SMART = "smart"
    WINDOWS = "windows"


def human_bytes(value: int | None) -> str:
    if value is None:
        return "Bilinmiyor"
    if value < 1024:
        return f"{value} B"

    units = ["KB", "MB", "GB", "TB", "PB"]
    size = float(value)
    for unit in units:
        size /= 1024.0
        if size < 1024 or unit == units[-1]:
            return f"{size:.1f} {unit}"
    return f"{value} B"


def align_up(value: int, alignment: int) -> int:
    if alignment <= 0:
        return value
    return int(math.ceil(value / alignment) * alignment)


def parse_partition_number(device_path: str) -> int | None:
    match = re.search(r"(\d+)$", device_path)
    if not match:
        return None
    return int(match.group(1))


@dataclass(slots=True)
class PartitionGeometry:
    number: int
    node: str
    start_sector: int
    size_sectors: int
    type_code: str | None = None
    uuid: str | None = None
    name: str | None = None
    attrs: str | None = None
    bootable: bool = False


@dataclass(slots=True)
class DiskLayout:
    device: str
    label: str | None
    unit: str
    sector_size: int
    first_lba: int | None = None
    partitions: list[PartitionGeometry] = field(default_factory=list)


@dataclass(slots=True)
class Partition:
    path: str
    name: str
    size_bytes: int
    number: int | None = None
    fs_type: str | None = None
    label: str | None = None
    mountpoint: str | None = None
    used_bytes: int | None = None
    part_label: str | None = None
    uuid: str | None = None
    part_type: str | None = None
    part_type_name: str | None = None
    pkname: str | None = None
    geometry: PartitionGeometry | None = None

    @property
    def display_name(self) -> str:
        label = self.part_label or self.label or self.name
        return f"{self.path} ({label})"

    @property
    def human_size(self) -> str:
        return human_bytes(self.size_bytes)

    @property
    def is_mounted(self) -> bool:
        return bool(self.mountpoint)

    def _matches(self, *needles: str) -> bool:
        haystacks = [
            (self.fs_type or "").lower(),
            (self.label or "").lower(),
            (self.part_label or "").lower(),
            (self.part_type or "").lower(),
            (self.part_type_name or "").lower(),
            self.path.lower(),
        ]
        return any(needle.lower() in hay for needle in needles for hay in haystacks)

    def is_efi(self) -> bool:
        return self._matches("efi", WINDOWS_ESP_GUID) or (
            (self.fs_type or "").lower() in {"vfat", "fat16", "fat32"}
            and self.size_bytes <= 2 * GIB
        )

    def is_microsoft_reserved(self) -> bool:
        return self._matches("microsoft reserved", WINDOWS_MSR_GUID)

    def is_recovery(self) -> bool:
        return self._matches("recovery", "winre", WINDOWS_RECOVERY_GUID)

    def is_system_reserved(self) -> bool:
        return (self.fs_type or "").lower() == "ntfs" and self.size_bytes <= 2 * GIB and not self.is_recovery()

    def is_windows_os_candidate(self) -> bool:
        return (self.fs_type or "").lower() == "ntfs" and not self.is_recovery() and self.size_bytes >= 20 * GIB

    def can_resize_non_destructively(self) -> bool:
        return (self.fs_type or "").lower() == "ntfs" and not self.is_recovery() and not self.is_efi()

    def requires_raw_copy(self) -> bool:
        return not self.fs_type and not self.is_microsoft_reserved()


@dataclass(slots=True)
class DiskHealth:
    overall_status: str = "Bilinmiyor"
    temperature_c: float | None = None
    power_on_hours: int | None = None
    notes: tuple[str, ...] = ()


@dataclass(slots=True)
class Disk:
    path: str
    name: str
    size_bytes: int
    model: str | None = None
    serial: str | None = None
    transport: str | None = None
    vendor: str | None = None
    logical_sector_size: int = 512
    physical_sector_size: int = 512
    rotation: bool | None = None
    removable: bool = False
    hotplug: bool = False
    table_type: str | None = None
    table_uuid: str | None = None
    partitions: list[Partition] = field(default_factory=list)
    health: DiskHealth | None = None

    @property
    def used_bytes(self) -> int | None:
        values = [part.used_bytes for part in self.partitions if part.used_bytes is not None]
        if not values:
            return None
        return sum(values)

    @property
    def human_size(self) -> str:
        return human_bytes(self.size_bytes)

    @property
    def connection_type(self) -> str:
        if self.transport:
            return self.transport.upper()
        if self.hotplug or self.removable:
            return "USB"
        return "Bilinmiyor"

    @property
    def storage_kind(self) -> str:
        if self.transport == "nvme":
            return "NVMe SSD"
        if self.rotation is True:
            return "HDD"
        if self.rotation is False:
            return "SSD"
        return "Disk"

    @property
    def summary(self) -> str:
        used = human_bytes(self.used_bytes)
        model = " ".join(part for part in [self.vendor, self.model] if part).strip() or "Model bilinmiyor"
        return f"{self.path} | {self.human_size} | {model} | Kullanilan: {used}"


@dataclass(slots=True)
class CommandStep:
    description: str
    command: list[str] | str
    destructive: bool = False
    shell: bool = False


@dataclass(slots=True)
class OperationPlan:
    mode: str
    title: str
    summary: str
    source_disk: str | None
    target_disk: str | None
    warnings: list[str] = field(default_factory=list)
    steps: list[CommandStep] = field(default_factory=list)
    suggested_actions: list[str] = field(default_factory=list)
    dry_run: bool = True


@dataclass(slots=True)
class ExecutionEvent:
    step_index: int
    total_steps: int
    description: str
    message: str
    progress: float
    kind: str = "log"
