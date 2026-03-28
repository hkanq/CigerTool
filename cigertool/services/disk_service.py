from __future__ import annotations

from collections import defaultdict
import json
import logging

from ..commands import CommandRunner
from ..models import Disk, DiskBus, Partition


class DiskService:
    def __init__(self, runner: CommandRunner, logger: logging.Logger) -> None:
        self.runner = runner
        self.logger = logger

    def scan_disks(self) -> list[Disk]:
        script = r"""
$disks = Get-Disk | Select-Object Number, FriendlyName, SerialNumber, BusType, Size, PartitionStyle, HealthStatus, OperationalStatus
$partitions = Get-Partition | Select-Object DiskNumber, PartitionNumber, DriveLetter, Size, Offset, GptType, MbrType, IsBoot, IsSystem, Guid
$volumes = Get-Volume | Select-Object DriveLetter, FileSystem, FileSystemLabel, SizeRemaining, Size
[pscustomobject]@{
  disks = $disks
  partitions = $partitions
  volumes = $volumes
} | ConvertTo-Json -Depth 5
"""
        payload = self.runner.powershell_json(script)
        return self._parse(payload)

    def _parse(self, payload: object) -> list[Disk]:
        if payload is None:
            return []

        disks_raw = payload.get("disks", []) if isinstance(payload, dict) else []
        partitions_raw = payload.get("partitions", []) if isinstance(payload, dict) else []
        volumes_raw = payload.get("volumes", []) if isinstance(payload, dict) else []

        if isinstance(disks_raw, dict):
            disks_raw = [disks_raw]
        if isinstance(partitions_raw, dict):
            partitions_raw = [partitions_raw]
        if isinstance(volumes_raw, dict):
            volumes_raw = [volumes_raw]

        volumes_by_letter = {str(volume.get("DriveLetter") or "").upper(): volume for volume in volumes_raw}
        partitions_by_disk: dict[int, list[Partition]] = defaultdict(list)

        for part in partitions_raw:
            letter = str(part.get("DriveLetter") or "").upper()
            volume = volumes_by_letter.get(letter, {})
            size_total = int(volume.get("Size") or part.get("Size") or 0)
            size_remaining = int(volume.get("SizeRemaining") or 0)
            partitions_by_disk[int(part.get("DiskNumber"))].append(
                Partition(
                    disk_number=int(part.get("DiskNumber")),
                    partition_number=int(part.get("PartitionNumber")),
                    size_bytes=int(part.get("Size") or 0),
                    offset_bytes=int(part.get("Offset") or 0),
                    fs_type=volume.get("FileSystem"),
                    label=volume.get("FileSystemLabel"),
                    drive_letter=letter or None,
                    gpt_type=part.get("GptType"),
                    mbr_type=part.get("MbrType"),
                    used_bytes=max(0, size_total - size_remaining) if size_total else None,
                    free_bytes=size_remaining if size_total else None,
                    is_boot=bool(part.get("IsBoot")),
                    is_system=bool(part.get("IsSystem")),
                    guid=part.get("Guid"),
                )
            )

        disks: list[Disk] = []
        for item in disks_raw:
            number = int(item.get("Number"))
            disks.append(
                Disk(
                    number=number,
                    path=fr"\\.\PhysicalDrive{number}",
                    friendly_name=str(item.get("FriendlyName") or f"Disk {number}"),
                    serial=str(item.get("SerialNumber") or "").strip() or None,
                    bus_type=self._parse_bus_type(item.get("BusType")),
                    size_bytes=int(item.get("Size") or 0),
                    partition_style=item.get("PartitionStyle"),
                    health_status=item.get("HealthStatus"),
                    operational_status=item.get("OperationalStatus"),
                    partitions=sorted(
                        partitions_by_disk.get(number, []),
                        key=lambda part: part.partition_number,
                    ),
                )
            )
        return disks

    @staticmethod
    def _parse_bus_type(value: object) -> DiskBus:
        text = str(value or "").upper()
        if text == "USB":
            return DiskBus.USB
        if text == "NVME":
            return DiskBus.NVME
        if text == "SATA":
            return DiskBus.SATA
        if text == "RAID" or text == "VHD":
            return DiskBus.VIRTUAL
        return DiskBus.UNKNOWN

    @staticmethod
    def find_disk(disks: list[Disk], number: int) -> Disk | None:
        return next((disk for disk in disks if disk.number == number), None)

