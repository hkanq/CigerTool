from __future__ import annotations

import unittest

from cigertool.models import CloneMode, Disk, DiskBus, Partition, WINDOWS_BASIC_GUID, WINDOWS_EFI_GUID
from cigertool.services.clone_service import ClonePlanningError, CloneService


class DummyLogger:
    def info(self, *args, **kwargs):
        return None

    def warning(self, *args, **kwargs):
        return None


class CloneServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.service = CloneService(DummyLogger())

    def test_recommends_smart_clone_for_smaller_disk_when_data_fits(self) -> None:
        source = Disk(
            number=0,
            path="\\\\.\\PhysicalDrive0",
            friendly_name="HDD",
            serial="SRC",
            bus_type=DiskBus.SATA,
            size_bytes=256 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[
                Partition(0, 1, 260 * 1024**2, fs_type="FAT32", gpt_type=WINDOWS_EFI_GUID, used_bytes=120 * 1024**2),
                Partition(0, 2, 180 * 1024**3, fs_type="NTFS", drive_letter="C", gpt_type=WINDOWS_BASIC_GUID, used_bytes=70 * 1024**3),
            ],
        )
        target = Disk(
            number=1,
            path="\\\\.\\PhysicalDrive1",
            friendly_name="SSD",
            serial="DST",
            bus_type=DiskBus.SATA,
            size_bytes=120 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[],
        )

        analysis = self.service.analyze(source, target)

        self.assertFalse(analysis.fits_raw)
        self.assertTrue(analysis.fits_smart)
        self.assertEqual(analysis.recommended_mode, CloneMode.SMART)

    def test_build_raw_plan_blocks_smaller_target(self) -> None:
        source = Disk(
            number=0,
            path="\\\\.\\PhysicalDrive0",
            friendly_name="HDD",
            serial="SRC",
            bus_type=DiskBus.SATA,
            size_bytes=256 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[Partition(0, 1, 128 * 1024**3, fs_type="NTFS", drive_letter="C", gpt_type=WINDOWS_BASIC_GUID, used_bytes=70 * 1024**3)],
        )
        target = Disk(
            number=1,
            path="\\\\.\\PhysicalDrive1",
            friendly_name="SSD",
            serial="DST",
            bus_type=DiskBus.SATA,
            size_bytes=100 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[],
        )

        analysis = self.service.analyze(source, target)
        with self.assertRaises(ClonePlanningError):
            self.service.build_plan(analysis, CloneMode.RAW)

    def test_recommends_system_clone_when_only_windows_fits(self) -> None:
        source = Disk(
            number=0,
            path="\\\\.\\PhysicalDrive0",
            friendly_name="HDD",
            serial="SRC",
            bus_type=DiskBus.SATA,
            size_bytes=256 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[
                Partition(0, 1, 260 * 1024**2, fs_type="FAT32", gpt_type=WINDOWS_EFI_GUID, used_bytes=120 * 1024**2),
                Partition(0, 2, 80 * 1024**3, fs_type="NTFS", drive_letter="C", gpt_type=WINDOWS_BASIC_GUID, used_bytes=40 * 1024**3),
                Partition(0, 3, 100 * 1024**3, fs_type="NTFS", drive_letter="D", gpt_type=WINDOWS_BASIC_GUID, used_bytes=70 * 1024**3),
            ],
        )
        target = Disk(
            number=1,
            path="\\\\.\\PhysicalDrive1",
            friendly_name="SSD",
            serial="DST",
            bus_type=DiskBus.SATA,
            size_bytes=60 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[],
        )

        analysis = self.service.analyze(source, target)

        self.assertFalse(analysis.fits_raw)
        self.assertFalse(analysis.fits_smart)
        self.assertTrue(analysis.fits_system)
        self.assertEqual(analysis.recommended_mode, CloneMode.SYSTEM)
        plan = self.service.build_plan(analysis, CloneMode.SYSTEM)
        self.assertEqual(plan.mode, CloneMode.SYSTEM)

    def test_supports_usb_to_ssd_smart_clone(self) -> None:
        source = Disk(
            number=2,
            path="\\\\.\\PhysicalDrive2",
            friendly_name="USB HDD",
            serial="USB-SRC",
            bus_type=DiskBus.USB,
            size_bytes=512 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[
                Partition(2, 1, 260 * 1024**2, fs_type="FAT32", gpt_type=WINDOWS_EFI_GUID, used_bytes=90 * 1024**2),
                Partition(2, 2, 220 * 1024**3, fs_type="NTFS", drive_letter="C", gpt_type=WINDOWS_BASIC_GUID, used_bytes=95 * 1024**3, is_boot=True, is_system=True),
            ],
        )
        target = Disk(
            number=1,
            path="\\\\.\\PhysicalDrive1",
            friendly_name="Internal SSD",
            serial="SSD-DST",
            bus_type=DiskBus.SATA,
            size_bytes=120 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[],
        )

        analysis = self.service.analyze(source, target)
        plan = self.service.build_plan(analysis, CloneMode.SMART)

        self.assertEqual(analysis.recommended_mode, CloneMode.SMART)
        self.assertEqual(plan.mode, CloneMode.SMART)

    def test_rejects_same_disk_selection(self) -> None:
        disk = Disk(
            number=0,
            path="\\\\.\\PhysicalDrive0",
            friendly_name="SSD",
            serial="SAME",
            bus_type=DiskBus.SATA,
            size_bytes=120 * 1024**3,
            partition_style="GPT",
            health_status="Healthy",
            operational_status="Online",
            partitions=[
                Partition(0, 1, 100 * 1024**3, fs_type="NTFS", drive_letter="C", gpt_type=WINDOWS_BASIC_GUID, used_bytes=40 * 1024**3),
            ],
        )

        with self.assertRaises(ClonePlanningError):
            self.service.analyze(disk, disk)


if __name__ == "__main__":
    unittest.main()
