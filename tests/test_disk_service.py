from __future__ import annotations

import unittest

from cigertool.models import DiskBus
from cigertool.services.disk_service import DiskService


class DiskServiceParseTests(unittest.TestCase):
    def test_parse_payload(self) -> None:
        payload = {
            "disks": [
                {
                    "Number": 0,
                    "FriendlyName": "Samsung SSD",
                    "SerialNumber": "XYZ",
                    "BusType": "USB",
                    "Size": 128849018880,
                    "PartitionStyle": "GPT",
                    "HealthStatus": "Healthy",
                    "OperationalStatus": "Online",
                }
            ],
            "partitions": [
                {
                    "DiskNumber": 0,
                    "PartitionNumber": 1,
                    "DriveLetter": "C",
                    "Size": 107374182400,
                    "Offset": 1048576,
                    "GptType": "{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}",
                    "MbrType": None,
                    "IsBoot": True,
                    "IsSystem": True,
                    "Guid": "guid",
                }
            ],
            "volumes": [
                {
                    "DriveLetter": "C",
                    "FileSystem": "NTFS",
                    "FileSystemLabel": "Windows",
                    "SizeRemaining": 21474836480,
                    "Size": 107374182400,
                }
            ],
        }

        service = DiskService.__new__(DiskService)
        disks = service._parse(payload)
        self.assertEqual(len(disks), 1)
        self.assertEqual(disks[0].bus_type, DiskBus.USB)
        self.assertEqual(disks[0].partitions[0].drive_letter, "C")
        self.assertEqual(disks[0].used_bytes, 85899345920)


if __name__ == "__main__":
    unittest.main()

