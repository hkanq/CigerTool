from __future__ import annotations

import logging
import shlex

from ..config import MOUNT_ROOT
from ..models import CommandStep, Disk, OperationPlan, Partition


class BootRepairService:
    def __init__(self, logger: logging.Logger) -> None:
        self.logger = logger

    def build_plan(self, disk: Disk, *, dry_run: bool = True) -> OperationPlan:
        efi_partition = next((part for part in disk.partitions if part.is_efi()), None)
        if efi_partition and efi_partition.number is not None:
            return self._build_uefi_plan(disk, efi_partition, dry_run=dry_run)
        return self._build_legacy_plan(disk, dry_run=dry_run)

    def _build_uefi_plan(self, disk: Disk, efi_partition: Partition, *, dry_run: bool) -> OperationPlan:
        mount_dir = MOUNT_ROOT / "efi"
        bootmgfw = mount_dir / "EFI" / "Microsoft" / "Boot" / "bootmgfw.efi"
        fallback = mount_dir / "EFI" / "Boot" / "bootx64.efi"
        steps = [
            CommandStep("EFI mount klasoru hazirlaniyor", ["mkdir", "-p", str(mount_dir)]),
            CommandStep("EFI bolumu baglaniyor", ["mount", efi_partition.path, str(mount_dir)]),
            CommandStep(
                "EFI dizinleri hazirlaniyor",
                ["mkdir", "-p", str(bootmgfw.parent), str(fallback.parent)],
            ),
            CommandStep(
                "Windows EFI dosyalari kontrol ediliyor",
                self._bash(
                    f"test -f {shlex.quote(str(bootmgfw))} || "
                    f"(echo 'bootmgfw.efi bulunamadi. Windows EFI dosyalari eksik.' >&2; exit 1)"
                ),
                shell=True,
            ),
            CommandStep(
                "UEFI fallback yolu guncelleniyor",
                ["cp", "-f", str(bootmgfw), str(fallback)],
                destructive=True,
            ),
            CommandStep(
                "UEFI boot kaydi yaziliyor",
                [
                    "efibootmgr",
                    "-c",
                    "-d",
                    disk.path,
                    "-p",
                    str(efi_partition.number),
                    "-L",
                    "Windows Boot Manager (CigerTool)",
                    "-l",
                    "\\EFI\\Microsoft\\Boot\\bootmgfw.efi",
                ],
                destructive=True,
            ),
            CommandStep("EFI bolumu ayriliyor", ["umount", str(mount_dir)]),
        ]
        return OperationPlan(
            mode="boot",
            title="Boot Onarma (UEFI)",
            summary=f"{disk.path} diski icin UEFI kaydi tazelenip Windows Boot Manager yeniden yazilacak.",
            source_disk=disk.path,
            target_disk=None,
            warnings=[
                f"Disk: {disk.summary}",
                "EFI bolumunde Windows boot dosyalarinin mevcut olmasi gerekir.",
            ],
            steps=steps,
            suggested_actions=[
                "Islemden sonra BIOS/UEFI icinde yeni SSD'yi ilk boot sirasi yapin.",
            ],
            dry_run=dry_run,
        )

    def _build_legacy_plan(self, disk: Disk, *, dry_run: bool) -> OperationPlan:
        ntfs_partitions = [part for part in disk.partitions if (part.fs_type or "").lower() == "ntfs"]
        if not ntfs_partitions:
            raise ValueError("Legacy onarim icin NTFS Windows bolumu bulunamadi.")

        boot_partition = next((part for part in ntfs_partitions if part.is_system_reserved()), None)
        os_partition = max(ntfs_partitions, key=lambda part: part.size_bytes)
        chain_target = boot_partition or os_partition
        if not chain_target.uuid:
            raise ValueError("Chainload icin UUID okunamadi. Diskleri yeniden taratin.")

        mount_dir = MOUNT_ROOT / "legacy"
        grub_cfg = mount_dir / "boot" / "grub" / "grub.cfg"
        steps = [
            CommandStep("Mount klasoru hazirlaniyor", ["mkdir", "-p", str(mount_dir)]),
            CommandStep("Windows bolumu baglaniyor", ["mount", chain_target.path, str(mount_dir)]),
            CommandStep("GRUB klasoru hazirlaniyor", ["mkdir", "-p", str(grub_cfg.parent)]),
            CommandStep(
                "GRUB zincir yukleme ayari yaziliyor",
                self._bash(
                    "cat <<'EOF' > {path}\nset timeout=5\nset default=0\n\nmenuentry 'Windows' {{\n"
                    "  insmod ntfs\n"
                    "  search --no-floppy --fs-uuid --set=root {uuid}\n"
                    "  chainloader +1\n"
                    "}}\nEOF".format(path=shlex.quote(str(grub_cfg)), uuid=chain_target.uuid)
                ),
                shell=True,
                destructive=True,
            ),
            CommandStep(
                "GRUB MBR kurulumunu yaziyor",
                [
                    "grub-install",
                    "--target=i386-pc",
                    f"--boot-directory={mount_dir / 'boot'}",
                    disk.path,
                ],
                destructive=True,
            ),
            CommandStep("Windows bolumu ayriliyor", ["umount", str(mount_dir)]),
        ]
        return OperationPlan(
            mode="boot",
            title="Boot Onarma (Legacy)",
            summary=(
                f"{disk.path} diski icin MBR/Legacy boot kaydi GRUB zincir yukleme ile yeniden olusturulacak."
            ),
            source_disk=disk.path,
            target_disk=None,
            warnings=[
                f"Disk: {disk.summary}",
                "Bu mod GRUB ile Windows'a zincir yukleme yapar. Saf Windows boot sektoru yerine guvenli bir kurtarma yoludur.",
            ],
            steps=steps,
            suggested_actions=[
                "Eski disk de takiliysa BIOS icinde hedef diski ilk siraya alin.",
            ],
            dry_run=dry_run,
        )

    @staticmethod
    def _bash(script: str) -> str:
        return "bash -lc " + shlex.quote(script)
