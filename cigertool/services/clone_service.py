from __future__ import annotations

from dataclasses import dataclass
import logging
import shlex

from ..config import SUPPORTED_PARTCLONE_TOOLS
from ..models import (
    GIB,
    MIB,
    CloneMode,
    CommandStep,
    Disk,
    OperationPlan,
    Partition,
    align_up,
    human_bytes,
)


class PlanningError(RuntimeError):
    """Raised when a disk operation plan would be unsafe or impossible."""


def make_partition_path(disk_path: str, number: int) -> str:
    if disk_path[-1].isdigit():
        return f"{disk_path}p{number}"
    return f"{disk_path}{number}"


@dataclass(slots=True)
class PlannedPartition:
    source: Partition
    target_number: int
    target_start_sector: int
    target_size_bytes: int
    target_size_sectors: int
    target_path: str
    strategy: str
    resize_source_to_bytes: int | None = None


class CloneService:
    def __init__(self, logger: logging.Logger) -> None:
        self.logger = logger

    def build_full_clone_plan(self, source: Disk, target: Disk, *, dry_run: bool = True) -> OperationPlan:
        self._validate_pair(source, target)
        if target.size_bytes < source.size_bytes:
            raise PlanningError(
                f"Hedef disk ({target.human_size}) kaynak diskten ({source.human_size}) kucuk. "
                "Tam klon guvenlik nedeniyle engellendi. Akilli klon oneriliyor."
            )

        warnings = self._common_warnings(source, target)
        warnings.append("Tam klon sektor bazli calisir. Hedef diskteki tum veri silinecektir.")
        steps = [
            CommandStep(
                "Hedef disk uzerindeki imzalar siliniyor",
                ["wipefs", "-af", target.path],
                destructive=True,
            ),
            CommandStep(
                "Hedef disk bolum tablosu sifirlaniyor",
                ["sgdisk", "--zap-all", target.path],
                destructive=True,
            ),
            CommandStep(
                "Disk birebir kopyalaniyor",
                [
                    "ddrescue",
                    "--force",
                    "--no-split",
                    source.path,
                    target.path,
                    f"/var/log/cigertool-{source.name}-to-{target.name}.log",
                ],
                destructive=True,
            ),
            CommandStep("Disk tablosu yenileniyor", ["partprobe", target.path]),
            CommandStep("Degisiklikler diske yaziliyor", ["sync"]),
        ]
        return OperationPlan(
            mode=CloneMode.FULL.value,
            title="Tam Disk Klonlama",
            summary=(
                f"{source.path} diski {target.path} diskine sektor bazli kopyalanacak. "
                f"Kaynak boyutu {source.human_size}, hedef boyutu {target.human_size}."
            ),
            source_disk=source.path,
            target_disk=target.path,
            warnings=warnings,
            steps=steps,
            suggested_actions=[
                "Klon bittikten sonra eski ve yeni diski ayni anda acmadan once BIOS boot sirasini kontrol edin.",
                "Gerekirse Boot Onarma ekranini calistirin.",
            ],
            dry_run=dry_run,
        )

    def build_smart_clone_plan(self, source: Disk, target: Disk, *, dry_run: bool = True) -> OperationPlan:
        return self._build_resize_aware_plan(CloneMode.SMART, source, target, dry_run=dry_run)

    def build_windows_migration_plan(self, source: Disk, target: Disk, *, dry_run: bool = True) -> OperationPlan:
        return self._build_resize_aware_plan(CloneMode.WINDOWS, source, target, dry_run=dry_run)

    def _build_resize_aware_plan(
        self,
        mode: CloneMode,
        source: Disk,
        target: Disk,
        *,
        dry_run: bool,
    ) -> OperationPlan:
        self._validate_pair(source, target)
        selected_partitions = self._select_partitions(source, mode)
        if not selected_partitions:
            raise PlanningError("Tasima icin uygun bolum bulunamadi.")

        missing_geometry = [part.path for part in selected_partitions if part.geometry is None]
        if missing_geometry:
            raise PlanningError(
                "Bolum geometrisi eksik. sfdisk bilgisi alinmamis gorunuyor: " + ", ".join(missing_geometry)
            )

        target_sector_size = target.logical_sector_size or 512
        planned = self._plan_partitions(selected_partitions, target.path, target_sector_size, mode)
        required_bytes = sum(item.target_size_bytes for item in planned) + (8 * MIB)
        if required_bytes > target.size_bytes:
            raise PlanningError(
                f"Secilen bolumler en az {human_bytes(required_bytes)} alana ihtiyac duyuyor, "
                f"ama hedef disk {target.human_size}. Islem guvenlik nedeniyle baslatilmayacak."
            )

        label = source.table_type or ("gpt" if any(part.is_efi() for part in source.partitions) else "dos")
        warnings = self._common_warnings(source, target)
        warnings.append(
            "Akilli klon gerekiyorsa kaynak NTFS dosya sistemini hedefe sigacak kadar kucultebilir."
        )
        if mode is CloneMode.WINDOWS:
            excluded = [part.path for part in source.partitions if part not in selected_partitions]
            if excluded:
                warnings.append(
                    "Windows tasima modunda veri bolumleri atlanabilir: " + ", ".join(excluded)
                )

        summary = (
            f"{source.path} -> {target.path}. "
            f"Planlanan veri yerlesimi: {human_bytes(required_bytes)} / {target.human_size}. "
        )
        if mode is CloneMode.SMART:
            summary += "Kullanilan veri bazli klon uygulanacak."
        else:
            summary += "Yalnizca Windows icin gereken sistem bolumleri tasinacak."

        return OperationPlan(
            mode=mode.value,
            title="Akilli Klon" if mode is CloneMode.SMART else "Windows / Sistem Tasima",
            summary=summary,
            source_disk=source.path,
            target_disk=target.path,
            warnings=warnings,
            steps=self._build_resize_aware_steps(source, target, planned, label),
            suggested_actions=[
                "Islemden sonra Boot Onarma ekranini acip yeni disk icin UEFI/Legacy kaydini tazeleyin.",
                "Ilk Windows acilisinda otomatik disk denetimi gorurseniz tamamlanmasina izin verin.",
            ],
            dry_run=dry_run,
        )

    def _validate_pair(self, source: Disk, target: Disk) -> None:
        if source.path == target.path:
            raise PlanningError("Kaynak ve hedef disk ayni olamaz.")
        if not source.partitions:
            raise PlanningError("Kaynak diskte kopyalanacak bolum bulunamadi.")
        if target.size_bytes <= 0:
            raise PlanningError("Hedef disk boyutu okunamadi.")

    def _common_warnings(self, source: Disk, target: Disk) -> list[str]:
        warnings = [
            f"Kaynak disk: {source.summary}",
            f"Hedef disk: {target.summary}",
            "Hedef diskteki mevcut tum veri silinecektir.",
        ]
        mounted = [part.path for part in source.partitions if part.is_mounted]
        if mounted:
            warnings.append(
                "Bazi kaynak bolumler mount edilmis gorunuyor. En guvenli sonuc icin Windows kapali ve bolumler bosta olmali: "
                + ", ".join(mounted)
            )
        return warnings

    def _select_partitions(self, source: Disk, mode: CloneMode) -> list[Partition]:
        ordered = sorted(source.partitions, key=lambda part: part.geometry.number if part.geometry else part.number or 0)
        if mode is CloneMode.SMART:
            return ordered

        efi = [part for part in ordered if part.is_efi()]
        msr = [part for part in ordered if part.is_microsoft_reserved()]
        recovery = [part for part in ordered if part.is_recovery()]
        ntfs = [part for part in ordered if (part.fs_type or "").lower() == "ntfs" and not part.is_recovery()]
        if not ntfs:
            raise PlanningError("Windows bolumu bulunamadi. NTFS sistem bolumu gerekli.")

        os_partition = max(ntfs, key=lambda part: part.size_bytes)
        system_reserved = [
            part for part in ntfs if part.is_system_reserved() and part.path != os_partition.path
        ]

        selected: list[Partition] = []
        for part in efi[:1] + msr[:1] + system_reserved[:1] + [os_partition] + recovery[:1]:
            if part not in selected:
                selected.append(part)
        return selected

    def _plan_partitions(
        self,
        partitions: list[Partition],
        target_disk_path: str,
        sector_size: int,
        mode: CloneMode,
    ) -> list[PlannedPartition]:
        alignment_sectors = max(2048, MIB // sector_size)
        current_sector = alignment_sectors
        planned: list[PlannedPartition] = []

        for number, partition in enumerate(partitions, start=1):
            target_size_bytes = self._suggest_partition_size(partition, mode)
            target_size_bytes = max(target_size_bytes, sector_size)
            target_size_sectors = align_up(target_size_bytes, sector_size) // sector_size
            target_size_bytes = target_size_sectors * sector_size
            start_sector = align_up(current_sector, alignment_sectors)
            target_path = make_partition_path(target_disk_path, number)
            strategy, resize_to = self._clone_strategy(partition, target_size_bytes)
            planned.append(
                PlannedPartition(
                    source=partition,
                    target_number=number,
                    target_start_sector=start_sector,
                    target_size_bytes=target_size_bytes,
                    target_size_sectors=target_size_sectors,
                    target_path=target_path,
                    strategy=strategy,
                    resize_source_to_bytes=resize_to,
                )
            )
            current_sector = start_sector + target_size_sectors

        return planned

    def _suggest_partition_size(self, partition: Partition, mode: CloneMode) -> int:
        original = partition.size_bytes
        if partition.is_microsoft_reserved():
            return original
        if partition.is_efi() or partition.is_recovery():
            return original
        if partition.requires_raw_copy():
            return original

        fs_type = (partition.fs_type or "").lower()
        if fs_type == "swap":
            return original
        if partition.can_resize_non_destructively():
            used = partition.used_bytes or int(original * 0.75)
            buffer = max(4 * GIB, int(used * 0.15))
            minimum_floor = 40 * GIB if partition.is_windows_os_candidate() else 1 * GIB
            planned = align_up(max(used + buffer, minimum_floor), MIB)
            if mode in {CloneMode.SMART, CloneMode.WINDOWS}:
                return min(original, planned)
        return original

    def _clone_strategy(self, partition: Partition, target_size_bytes: int) -> tuple[str, int | None]:
        if partition.is_microsoft_reserved():
            return "create-only", None

        fs_type = (partition.fs_type or "").lower()
        if fs_type == "swap":
            return "mkswap", None
        if partition.can_resize_non_destructively():
            resize_to = target_size_bytes if target_size_bytes < partition.size_bytes else None
            return "ntfsclone", resize_to
        if fs_type in SUPPORTED_PARTCLONE_TOOLS:
            return SUPPORTED_PARTCLONE_TOOLS[fs_type], None
        return "ddrescue", None

    def _build_resize_aware_steps(
        self,
        source: Disk,
        target: Disk,
        planned: list[PlannedPartition],
        label: str,
    ) -> list[CommandStep]:
        steps: list[CommandStep] = [
            CommandStep("Hedef disk imzalari temizleniyor", ["wipefs", "-af", target.path], destructive=True),
            CommandStep("Hedef disk bolum tablosu sifirlaniyor", ["sgdisk", "--zap-all", target.path], destructive=True),
            CommandStep(
                "Yeni bolum tablosu yaziliyor",
                self._sfdisk_shell_command(target.path, label, planned),
                destructive=True,
                shell=True,
            ),
            CommandStep("Kernel yeni bolum tablosunu okuyor", ["partprobe", target.path]),
            CommandStep("Aygit olusumu bekleniyor", ["udevadm", "settle"]),
        ]

        if label == "gpt":
            steps.append(CommandStep("Disk GUID degeri yenileniyor", ["sgdisk", "-G", target.path]))

        for item in planned:
            if item.resize_source_to_bytes:
                steps.append(
                    CommandStep(
                        f"{item.source.path} NTFS dosya sistemi kucultuluyor",
                        [
                            "ntfsresize",
                            "--force",
                            "--size",
                            str(item.resize_source_to_bytes),
                            item.source.path,
                        ],
                    )
                )

            clone_step = self._build_partition_copy_step(item)
            if clone_step:
                steps.append(clone_step)

        steps.append(CommandStep("Degisiklikler diske yaziliyor", ["sync"]))
        return steps

    def _build_partition_copy_step(self, item: PlannedPartition) -> CommandStep | None:
        if item.strategy == "create-only":
            return None
        if item.strategy == "mkswap":
            return CommandStep(
                f"{item.target_path} swap bolumu hazirlaniyor",
                ["mkswap", item.target_path],
            )
        if item.strategy == "ntfsclone":
            return CommandStep(
                f"{item.source.path} -> {item.target_path} NTFS klonlama",
                ["ntfsclone", "--overwrite", item.target_path, item.source.path],
                destructive=True,
            )
        if item.strategy.startswith("partclone."):
            return CommandStep(
                f"{item.source.path} -> {item.target_path} dosya sistemi klonlama",
                [item.strategy, "-b", "-s", item.source.path, "-O", item.target_path],
                destructive=True,
            )
        return CommandStep(
            f"{item.source.path} -> {item.target_path} ham kopya",
            [
                "ddrescue",
                "--force",
                item.source.path,
                item.target_path,
                f"/var/log/cigertool-{item.source.name}-to-{item.target_number}.log",
            ],
            destructive=True,
        )

    def _sfdisk_shell_command(self, target_disk_path: str, label: str, planned: list[PlannedPartition]) -> str:
        script = self._build_sfdisk_script(target_disk_path, label, planned)
        target = shlex.quote(target_disk_path)
        return (
            "bash -lc "
            + shlex.quote(f"cat <<'EOF' | sfdisk --wipe always {target}\n{script}\nEOF")
        )

    def _build_sfdisk_script(self, target_disk_path: str, label: str, planned: list[PlannedPartition]) -> str:
        lines = [
            f"label: {label}",
            "unit: sectors",
        ]

        for item in planned:
            geometry = item.source.geometry
            if geometry is None:
                continue
            attributes = [
                f"start={item.target_start_sector}",
                f"size={item.target_size_sectors}",
            ]
            if geometry.type_code:
                attributes.append(f"type={geometry.type_code}")
            if geometry.name:
                safe_name = geometry.name.replace('"', '\\"')
                attributes.append(f'name="{safe_name}"')
            if geometry.bootable and label == "dos":
                attributes.append("bootable")
            line = f"{make_partition_path(target_disk_path, item.target_number)} : " + ", ".join(attributes)
            lines.append(line)

        return "\n".join(lines)
