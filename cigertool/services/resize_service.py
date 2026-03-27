from __future__ import annotations

import logging

from ..models import CommandStep, Disk, OperationPlan, Partition, human_bytes


class ResizeService:
    def __init__(self, logger: logging.Logger) -> None:
        self.logger = logger

    def build_ntfs_resize_plan(
        self,
        disk: Disk,
        partition: Partition,
        target_bytes: int,
        *,
        dry_run: bool = True,
    ) -> OperationPlan:
        if (partition.fs_type or "").lower() != "ntfs":
            raise ValueError("Yalnizca NTFS bolumleri icin otomatik kucultme plani uretilebiliyor.")
        if partition.geometry is None or partition.number is None:
            raise ValueError("Bolum geometrisi eksik. Lutfen diskleri yeniden taratin.")

        used = partition.used_bytes or 0
        if target_bytes <= used + (2 * 1024**3):
            raise ValueError(
                f"Hedef boyut cok kucuk. Kullanilan veri {human_bytes(used)}; "
                "en az 2 GB guvenlik payi birakin."
            )
        if target_bytes >= partition.size_bytes:
            raise ValueError("Hedef boyut mevcut bolum boyutundan kucuk olmali.")

        start_byte = partition.geometry.start_sector * disk.logical_sector_size
        end_byte = start_byte + target_bytes - 1
        steps = [
            CommandStep("NTFS minimum boyutu kontrol ediliyor", ["ntfsresize", "--info", partition.path]),
            CommandStep(
                f"{partition.path} dosya sistemi kucultuluyor",
                ["ntfsresize", "--force", "--size", str(target_bytes), partition.path],
            ),
            CommandStep(
                f"{partition.path} bolum siniri guncelleniyor",
                ["parted", "-s", disk.path, "unit", "B", "resizepart", str(partition.number), str(end_byte)],
                destructive=True,
            ),
            CommandStep("Kernel bolum tablosunu yeniden okuyor", ["partprobe", disk.path]),
            CommandStep("Degisiklikler diske yaziliyor", ["sync"]),
        ]

        return OperationPlan(
            mode="resize",
            title="Disk Boyutlandirma ve Kucultme",
            summary=(
                f"{partition.path} bolumu {human_bytes(partition.size_bytes)} boyuttan "
                f"{human_bytes(target_bytes)} boyuta kucultulecek."
            ),
            source_disk=disk.path,
            target_disk=None,
            warnings=[
                f"Kaynak disk: {disk.summary}",
                f"Islem NTFS dosya sistemini ve bolum sinirini degistirir: {partition.path}",
                "Islem sirasinda guc kesintisi olmamali.",
            ],
            steps=steps,
            suggested_actions=[
                "Kucultme tamamlandiktan sonra hedef SSD icin akilli klon ekranindan yeni plan alin.",
            ],
            dry_run=dry_run,
        )
