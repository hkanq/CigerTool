from __future__ import annotations

import logging

from ..commands import CommandRunner


class SmartService:
    def __init__(self, runner: CommandRunner, logger: logging.Logger) -> None:
        self.runner = runner
        self.logger = logger

    def snapshot(self) -> list[dict]:
        script = r"""
$items = Get-PhysicalDisk | Select-Object FriendlyName, HealthStatus, OperationalStatus, MediaType, Size
$counters = @()
try {
  $counters = Get-PhysicalDisk | Get-StorageReliabilityCounter | Select-Object DeviceId, Temperature, PowerOnHours, ReadErrorsTotal, WriteErrorsTotal
} catch {
  $counters = @()
}
[pscustomobject]@{
  disks = $items
  counters = $counters
} | ConvertTo-Json -Depth 5
"""
        data = self.runner.powershell_json(script)
        return [data] if isinstance(data, dict) else []

