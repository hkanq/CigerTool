from __future__ import annotations

import json
import unittest

from cigertool.services.disk_service import DiskService


class DiskServiceParserTests(unittest.TestCase):
    def test_parse_lsblk_json(self) -> None:
        payload = {
            "blockdevices": [
                {
                    "name": "sda",
                    "path": "/dev/sda",
                    "type": "disk",
                    "size": 249059350016,
                    "model": "ST250DM000",
                    "serial": "ABC123",
                    "tran": "sata",
                    "rota": 1,
                    "pttype": "gpt",
                    "children": [
                        {
                            "name": "sda1",
                            "path": "/dev/sda1",
                            "type": "part",
                            "size": 104857600,
                            "fstype": "vfat",
                            "partlabel": "EFI",
                            "fsused": 52428800,
                            "parttype": "c12a7328-f81f-11d2-ba4b-00a0c93ec93b",
                        },
                        {
                            "name": "sda2",
                            "path": "/dev/sda2",
                            "type": "part",
                            "size": 214748364800,
                            "fstype": "ntfs",
                            "label": "Windows",
                            "fsused": 75161927680,
                            "uuid": "AAAA-BBBB",
                        },
                    ],
                }
            ]
        }

        disks = DiskService._parse_lsblk(json.dumps(payload))
        self.assertEqual(len(disks), 1)
        disk = disks[0]
        self.assertEqual(disk.path, "/dev/sda")
        self.assertEqual(disk.storage_kind, "HDD")
        self.assertEqual(disk.connection_type, "SATA")
        self.assertEqual(len(disk.partitions), 2)
        self.assertEqual(disk.used_bytes, 75214356480)
        self.assertTrue(disk.partitions[0].is_efi())


if __name__ == "__main__":
    unittest.main()
