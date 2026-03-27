from __future__ import annotations

import json
import logging

from ..commands import CommandRunner
from ..models import DiskHealth


class SmartService:
    def __init__(self, runner: CommandRunner, logger: logging.Logger) -> None:
        self.runner = runner
        self.logger = logger

    def read_health(self, disk_path: str) -> DiskHealth:
        result = self.runner.run(["smartctl", "-a", "-j", disk_path], dry_run=False)
        payload = json.loads(result.stdout)
        return self._parse_health(payload)

    def _parse_health(self, payload: dict) -> DiskHealth:
        smart_status = payload.get("smart_status", {})
        if smart_status.get("passed") is True:
            overall = "Iyi"
        elif smart_status.get("passed") is False:
            overall = "Riskli"
        else:
            overall = "Bilinmiyor"

        temperature = None
        temp_block = payload.get("temperature", {})
        if "current" in temp_block:
            temperature = float(temp_block["current"])
        else:
            nvme_temp = payload.get("nvme_smart_health_information_log", {}).get("temperature")
            if isinstance(nvme_temp, (int, float)):
                temperature = float(nvme_temp - 273) if nvme_temp > 200 else float(nvme_temp)

        power_on_hours = None
        power_on = payload.get("power_on_time", {})
        if isinstance(power_on.get("hours"), (int, float)):
            power_on_hours = int(power_on["hours"])

        notes: list[str] = []
        table = payload.get("ata_smart_attributes", {}).get("table", [])
        important = {
            5: "Reallocated_Sector_Ct",
            197: "Current_Pending_Sector",
            198: "Offline_Uncorrectable",
        }
        for row in table:
            attr_id = row.get("id")
            raw_value = row.get("raw", {}).get("value")
            if attr_id in important and raw_value not in (None, 0):
                notes.append(f"{important[attr_id]}={raw_value}")

        nvme_log = payload.get("nvme_smart_health_information_log", {})
        media_errors = nvme_log.get("media_errors")
        if media_errors not in (None, 0):
            notes.append(f"NVMe media_errors={media_errors}")

        return DiskHealth(
            overall_status=overall,
            temperature_c=temperature,
            power_on_hours=power_on_hours,
            notes=tuple(notes),
        )
