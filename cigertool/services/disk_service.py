from __future__ import annotations

from dataclasses import replace
import json
import logging
import re

from ..commands import CommandRunner
from ..models import Disk, DiskLayout, Partition, PartitionGeometry, human_bytes, parse_partition_number


def _value(entry: dict, *keys: str):
    for key in keys:
        if key in entry:
            return entry[key]
    return None


def _to_int(value) -> int | None:
    if value in (None, "", "-"):
        return None
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, (int, float)):
        return int(value)
    text = str(value).strip()
    try:
        return int(text)
    except ValueError:
        return None


class DiskService:
    LSBLK_COLUMNS = (
        "NAME,KNAME,PATH,SIZE,TYPE,MODEL,SERIAL,TRAN,VENDOR,ROTA,HOTPLUG,RM,"
        "LOG-SEC,PHY-SEC,PTTYPE,PTUUID,FSTYPE,MOUNTPOINT,FSUSED,FSAVAIL,LABEL,"
        "PARTLABEL,UUID,PARTTYPE,PARTTYPENAME,PKNAME"
    )

    def __init__(self, runner: CommandRunner, logger: logging.Logger) -> None:
        self.runner = runner
        self.logger = logger

    def scan_disks(self) -> list[Disk]:
        result = self.runner.run(
            ["lsblk", "--json", "-b", "-o", self.LSBLK_COLUMNS],
            dry_run=False,
        )
        disks = self._parse_lsblk(result.stdout)
        for disk in disks:
            try:
                layout = self.load_layout(disk)
                self._apply_layout(disk, layout)
            except Exception as exc:  # pragma: no cover - environment dependent
                self.logger.warning("sfdisk bilgisi okunamadi (%s): %s", disk.path, exc)
        return disks

    def load_layout(self, disk: Disk) -> DiskLayout:
        result = self.runner.run(["sfdisk", "--json", disk.path], dry_run=False)
        payload = json.loads(result.stdout)
        partition_table = payload.get("partitiontable", {})
        partitions: list[PartitionGeometry] = []
        for entry in partition_table.get("partitions", []):
            node = entry.get("node")
            if not node:
                continue
            number = parse_partition_number(node)
            if number is None:
                continue
            partitions.append(
                PartitionGeometry(
                    number=number,
                    node=node,
                    start_sector=int(entry.get("start", 0)),
                    size_sectors=int(entry.get("size", 0)),
                    type_code=entry.get("type"),
                    uuid=entry.get("uuid"),
                    name=entry.get("name"),
                    attrs=entry.get("attrs"),
                    bootable=bool(entry.get("bootable", False)),
                )
            )

        first_lba = partition_table.get("firstlba")
        return DiskLayout(
            device=partition_table.get("device", disk.path),
            label=partition_table.get("label"),
            unit=partition_table.get("unit", "sectors"),
            sector_size=disk.logical_sector_size,
            first_lba=int(first_lba) if first_lba is not None else None,
            partitions=partitions,
        )

    @classmethod
    def _parse_lsblk(cls, payload: str) -> list[Disk]:
        raw = json.loads(payload)
        disks: list[Disk] = []
        for entry in raw.get("blockdevices", []):
            if entry.get("type") != "disk":
                continue
            disk = Disk(
                path=_value(entry, "path") or f"/dev/{entry.get('name')}",
                name=_value(entry, "name") or "",
                size_bytes=_to_int(_value(entry, "size")) or 0,
                model=(_value(entry, "model") or "").strip() or None,
                serial=(_value(entry, "serial") or "").strip() or None,
                transport=(_value(entry, "tran") or "").strip() or None,
                vendor=(_value(entry, "vendor") or "").strip() or None,
                logical_sector_size=_to_int(_value(entry, "log-sec", "log_sec")) or 512,
                physical_sector_size=_to_int(_value(entry, "phy-sec", "phy_sec")) or 512,
                rotation=bool(_to_int(_value(entry, "rota"))) if _value(entry, "rota") is not None else None,
                removable=bool(_to_int(_value(entry, "rm")) or False),
                hotplug=bool(_to_int(_value(entry, "hotplug")) or False),
                table_type=_value(entry, "pttype"),
                table_uuid=_value(entry, "ptuuid"),
                partitions=cls._collect_partitions(entry),
            )
            disks.append(disk)
        return disks

    @classmethod
    def _collect_partitions(cls, node: dict) -> list[Partition]:
        partitions: list[Partition] = []
        for child in node.get("children", []) or []:
            child_type = child.get("type")
            if child_type == "part":
                name = child.get("name") or child.get("path") or "bolum"
                partitions.append(
                    Partition(
                        path=_value(child, "path") or f"/dev/{name}",
                        name=name,
                        size_bytes=_to_int(_value(child, "size")) or 0,
                        number=parse_partition_number(_value(child, "path") or name),
                        fs_type=(_value(child, "fstype") or "").strip() or None,
                        label=(_value(child, "label") or "").strip() or None,
                        mountpoint=_value(child, "mountpoint"),
                        used_bytes=_to_int(_value(child, "fsused")),
                        part_label=(_value(child, "partlabel") or "").strip() or None,
                        uuid=(_value(child, "uuid") or "").strip() or None,
                        part_type=(_value(child, "parttype") or "").strip() or None,
                        part_type_name=(_value(child, "parttypename") or "").strip() or None,
                        pkname=_value(child, "pkname"),
                    )
                )
            partitions.extend(cls._collect_partitions(child))
        return partitions

    @staticmethod
    def _apply_layout(disk: Disk, layout: DiskLayout) -> None:
        geometry_by_path = {entry.node: entry for entry in layout.partitions}
        updated_partitions = []
        for partition in disk.partitions:
            geometry = geometry_by_path.get(partition.path)
            updated_partitions.append(replace(partition, geometry=geometry))
        disk.partitions = updated_partitions

    @staticmethod
    def build_overview_message(disks: list[Disk]) -> str:
        if len(disks) < 2:
            return "En az iki disk takildiginda kaynak ve hedef icin otomatik oneriler burada gorunur."

        ordered = sorted(disks, key=lambda item: item.size_bytes, reverse=True)
        source = ordered[0]
        targets = [disk for disk in ordered[1:] if disk.path != source.path]
        if not targets:
            return "Hedef disk secmek icin ikinci bir disk takin."
        target = sorted(targets, key=lambda item: item.size_bytes)[0]

        used = source.used_bytes or 0
        if target.size_bytes >= source.size_bytes:
            return (
                f"Kaynak olarak {source.path} ({source.human_size}), hedef olarak {target.path} ({target.human_size}) "
                "gorunuyor. Tam klon ve akilli klon birlikte calisabilir."
            )
        if used and used < target.size_bytes:
            return (
                f"{source.path} diskindeki kullanilan veri {human_bytes(used)}. "
                f"{target.path} diski {target.human_size}; bu nedenle akilli klon en mantikli secim."
            )
        return (
            f"{target.path} diski {source.path} icin kucuk gorunuyor. "
            "Kullanilan veri sigmiyorsa islem baslatilmaz."
        )

    @staticmethod
    def find_disk(disks: list[Disk], path: str) -> Disk | None:
        return next((disk for disk in disks if disk.path == path), None)

    @staticmethod
    def find_partition(disk: Disk, path: str) -> Partition | None:
        return next((partition for partition in disk.partitions if partition.path == path), None)
