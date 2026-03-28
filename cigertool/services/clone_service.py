from __future__ import annotations

import logging

from ..models import CloneAnalysis, CloneMode, Disk, OperationPlan, Partition, PlanRisk, PlanStep, PartitionRole, human_bytes


class ClonePlanningError(RuntimeError):
    pass


class CloneService:
    def __init__(self, logger: logging.Logger) -> None:
        self.logger = logger

    def analyze(self, source: Disk, target: Disk) -> CloneAnalysis:
        if source.number == target.number:
            raise ClonePlanningError("Kaynak ve hedef disk ayni olamaz.")

        system_partition = self._largest_windows_partition(source)
        efi_partition = next((part for part in source.partitions if part.role is PartitionRole.EFI), None)
        recovery_partition = next((part for part in source.partitions if part.role is PartitionRole.RECOVERY), None)
        data_partitions = [part for part in source.partitions if self._is_data_partition(part, system_partition)]
        source_used = sum(part.used_bytes or 0 for part in source.partitions if part.role is not PartitionRole.MSR)
        target_usable = target.size_bytes
        fits_raw = target.size_bytes >= source.size_bytes
        required_system = self._estimate_required_bytes(
            system_partition=system_partition,
            efi_partition=efi_partition,
            recovery_partition=recovery_partition,
            data_partitions=[],
        )
        required_smart = self._estimate_required_bytes(
            system_partition=system_partition,
            efi_partition=efi_partition,
            recovery_partition=recovery_partition,
            data_partitions=data_partitions,
        )
        fits_system = required_system <= target_usable if system_partition else False
        fits_smart = required_smart <= target_usable if system_partition else False
        warnings: list[str] = [
            f"Kaynak: {source.summary}",
            f"Hedef: {target.summary}",
            f"Tahmini Smart Clone gereksinimi: {human_bytes(required_smart)}",
            f"Tahmini System Clone gereksinimi: {human_bytes(required_system)}",
        ]
        blocked: list[str] = []
        recommended: CloneMode | None = None

        if fits_raw:
            recommended = CloneMode.RAW
        elif fits_smart:
            recommended = CloneMode.SMART
            warnings.append("Hedef disk daha kucuk ama kullanilan veri sigiyor. Smart Clone onerildi.")
        elif fits_system:
            recommended = CloneMode.SYSTEM
            warnings.append("Tum veri sigmiyor; yalnizca Windows tasimak icin System Clone onerildi.")
        else:
            blocked.append(
                "Kaynak yerlesimi hedef diske sigmiyor. Hedef kapasite "
                f"{human_bytes(target_usable)}, Smart Clone ihtiyaci {human_bytes(required_smart)}."
            )

        if system_partition is None:
            blocked.append("Windows sistem bolumu tespit edilemedi.")
        return CloneAnalysis(
            source_disk=source,
            target_disk=target,
            source_used_bytes=source_used,
            target_usable_bytes=target_usable,
            fits_raw=fits_raw,
            fits_smart=fits_smart,
            fits_system=fits_system,
            recommended_mode=recommended if not blocked else None,
            warnings=warnings,
            blocked_reasons=blocked,
            system_partition=system_partition,
            efi_partition=efi_partition,
            recovery_partition=recovery_partition,
            data_partitions=data_partitions,
            required_system_bytes=required_system,
            required_smart_bytes=required_smart,
        )

    def build_plan(self, analysis: CloneAnalysis, mode: CloneMode) -> OperationPlan:
        if analysis.blocked_reasons:
            raise ClonePlanningError("\n".join(analysis.blocked_reasons))
        if mode is CloneMode.RAW and not analysis.fits_raw:
            raise ClonePlanningError("RAW clone yalnizca esit veya daha buyuk hedef diskte calisir.")
        if mode in {CloneMode.SMART, CloneMode.SYSTEM} and not analysis.fits_smart:
            if mode is CloneMode.SYSTEM and analysis.fits_system:
                pass
            else:
                raise ClonePlanningError("Kullanilan veri hedef diske sigmiyor.")
        if mode is CloneMode.SYSTEM and not analysis.fits_system:
            raise ClonePlanningError("Windows sistem bolumleri bile hedef diske sigmiyor.")

        if mode is CloneMode.RAW:
            return self._raw_plan(analysis)
        if mode is CloneMode.SMART:
            return self._smart_plan(analysis, include_data=True)
        return self._smart_plan(analysis, include_data=False)

    def _raw_plan(self, analysis: CloneAnalysis) -> OperationPlan:
        source = analysis.source_disk
        target = analysis.target_disk
        command = [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            "build\\scripts\\invoke_raw_clone.ps1",
            "-SourceDisk",
            str(source.number),
            "-TargetDisk",
            str(target.number),
        ]
        return OperationPlan(
            mode=CloneMode.RAW,
            title="RAW Clone",
            summary="Sektor bazli birebir klon. Hedef diskteki her sey silinir.",
            warnings=analysis.warnings + ["Bu mod kucuk hedef diskte calismaz."],
            steps=[PlanStep("Ham disk kopyasi", command, destructive=True)],
            risk=PlanRisk.HIGH,
            analysis=analysis,
        )

    def _smart_plan(self, analysis: CloneAnalysis, *, include_data: bool) -> OperationPlan:
        source = analysis.source_disk
        target = analysis.target_disk
        mode = CloneMode.SMART if include_data else CloneMode.SYSTEM
        wizard = [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            "build\\scripts\\invoke_smart_clone.ps1",
            "-SourceDisk",
            str(source.number),
            "-TargetDisk",
            str(target.number),
            "-CloneMode",
            mode.value,
        ]
        summary = (
            "Sadece kullanilan veri tasinir, hedef layout otomatik ayarlanir ve boot onarimi hazirlanir."
            if include_data
            else "Windows icin gerekli bolumler tasinir, EFI/boot ayarlari otomatik yenilenir."
        )
        warnings = list(analysis.warnings)
        warnings.append("Hedef diskteki tum veri silinir.")
        if include_data:
            warnings.append("NTFS sistem verisi hedefe sigacak sekilde yeniden yerlestirilir.")
            warnings.append(f"Planlanan minimum hedef alan: {human_bytes(analysis.required_smart_bytes)}")
        else:
            warnings.append("Buyuk veri bolumleri atlanabilir; sistem ve boot odakli tasima yapilir.")
            warnings.append(f"Planlanan minimum hedef alan: {human_bytes(analysis.required_system_bytes)}")
        return OperationPlan(
            mode=mode,
            title="Smart Clone" if include_data else "System Clone",
            summary=summary,
            warnings=warnings,
            steps=[
                PlanStep("Disk yerlesimini hazirla", wizard, destructive=True),
            ],
            risk=PlanRisk.MEDIUM,
            analysis=analysis,
        )

    @staticmethod
    def _largest_windows_partition(disk: Disk) -> Partition | None:
        candidates = [part for part in disk.partitions if part.can_host_windows]
        if not candidates:
            return None
        prioritized = sorted(
            candidates,
            key=lambda item: (
                not item.is_boot,
                not item.is_system,
                (item.drive_letter or "").upper() != "C",
                "WINDOWS" not in (item.label or "").upper(),
                -item.size_bytes,
            ),
        )
        return prioritized[0]

    @staticmethod
    def _is_data_partition(partition: Partition, system_partition: Partition | None) -> bool:
        if system_partition and partition.partition_number == system_partition.partition_number:
            return False
        return partition.role in {PartitionRole.WINDOWS, PartitionRole.BASIC, PartitionRole.UNKNOWN} and bool(partition.drive_letter)

    def _estimate_required_bytes(
        self,
        *,
        system_partition: Partition | None,
        efi_partition: Partition | None,
        recovery_partition: Partition | None,
        data_partitions: list[Partition],
    ) -> int:
        if system_partition is None:
            return 0

        total = 0
        total += max(efi_partition.size_bytes if efi_partition else 0, 260 * 1024**2)
        total += 16 * 1024**2
        total += self._estimate_partition_target_size(system_partition, minimum_bytes=45 * 1024**3)
        for partition in data_partitions:
            total += self._estimate_partition_target_size(partition, minimum_bytes=4 * 1024**3)
        if recovery_partition is not None:
            total += max(recovery_partition.size_bytes, 900 * 1024**2)
        return total

    @staticmethod
    def _estimate_partition_target_size(partition: Partition, *, minimum_bytes: int) -> int:
        used = partition.used_bytes or partition.size_bytes
        proposed = int(used * 1.15)
        return min(partition.size_bytes, max(proposed, minimum_bytes))
