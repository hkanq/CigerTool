from __future__ import annotations

import unittest

from cigertool.models import Disk, Partition, PartitionGeometry
from cigertool.services.clone_service import CloneService, PlanningError


class CloneServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.service = CloneService(logger=self._dummy_logger())

    def test_full_clone_blocks_small_target(self) -> None:
        source = self._source_disk(include_data_partition=False)
        target = self._target_disk()
        with self.assertRaises(PlanningError):
            self.service.build_full_clone_plan(source, target, dry_run=True)

    def test_smart_clone_shrinks_ntfs_when_target_is_smaller(self) -> None:
        source = self._source_disk(include_data_partition=False)
        target = self._target_disk()

        plan = self.service.build_smart_clone_plan(source, target, dry_run=True)

        descriptions = [step.description for step in plan.steps]
        self.assertTrue(any("NTFS dosya sistemi kucultuluyor" in item for item in descriptions))
        self.assertTrue(any("NTFS klonlama" in item for item in descriptions))
        self.assertIn("Akilli Klon", plan.title)

    def test_windows_migration_warns_when_data_partition_is_skipped(self) -> None:
        source = self._source_disk(include_data_partition=True)
        target = self._target_disk()

        plan = self.service.build_windows_migration_plan(source, target, dry_run=True)

        self.assertTrue(any("atlanabilir" in warning for warning in plan.warnings))
        self.assertEqual(plan.mode, "windows")

    def _source_disk(self, *, include_data_partition: bool) -> Disk:
        partitions = [
            Partition(
                path="/dev/sda1",
                name="sda1",
                size_bytes=104857600,
                number=1,
                fs_type="vfat",
                part_label="EFI System",
                used_bytes=52428800,
                geometry=PartitionGeometry(1, "/dev/sda1", 2048, 204800, type_code="c12a7328-f81f-11d2-ba4b-00a0c93ec93b"),
            ),
            Partition(
                path="/dev/sda2",
                name="sda2",
                size_bytes=16777216,
                number=2,
                part_label="MSR",
                part_type="e3c9e316-0b5c-4db8-817d-f92df00215ae",
                geometry=PartitionGeometry(2, "/dev/sda2", 206848, 32768, type_code="e3c9e316-0b5c-4db8-817d-f92df00215ae"),
            ),
            Partition(
                path="/dev/sda3",
                name="sda3",
                size_bytes=214748364800,
                number=3,
                fs_type="ntfs",
                label="Windows",
                uuid="AAAA-BBBB",
                used_bytes=75161927680,
                geometry=PartitionGeometry(3, "/dev/sda3", 239616, 419430400, type_code="ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
            ),
            Partition(
                path="/dev/sda4",
                name="sda4",
                size_bytes=734003200,
                number=4,
                fs_type="ntfs",
                label="Recovery",
                used_bytes=314572800,
                part_type="de94bba4-06d1-4d40-a16a-bfd50179d6ac",
                geometry=PartitionGeometry(4, "/dev/sda4", 419670016, 1433600, type_code="de94bba4-06d1-4d40-a16a-bfd50179d6ac"),
            ),
        ]
        if include_data_partition:
            partitions.append(
                Partition(
                    path="/dev/sda5",
                    name="sda5",
                    size_bytes=21474836480,
                    number=5,
                    fs_type="ntfs",
                    label="Data",
                    uuid="CCCC-DDDD",
                    used_bytes=10737418240,
                    geometry=PartitionGeometry(5, "/dev/sda5", 421103616, 41943040, type_code="ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"),
                )
            )
        return Disk(
            path="/dev/sda",
            name="sda",
            size_bytes=249108103168,
            model="ST250DM000",
            serial="SRC-1",
            transport="sata",
            rotation=True,
            table_type="gpt",
            partitions=partitions,
        )

    def _target_disk(self) -> Disk:
        return Disk(
            path="/dev/sdb",
            name="sdb",
            size_bytes=128849018880,
            model="Samsung SSD",
            serial="DST-1",
            transport="sata",
            rotation=False,
            table_type="gpt",
            partitions=[],
        )

    @staticmethod
    def _dummy_logger():
        class DummyLogger:
            def info(self, *args, **kwargs):
                return None

            def warning(self, *args, **kwargs):
                return None

        return DummyLogger()


if __name__ == "__main__":
    unittest.main()
