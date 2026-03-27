from __future__ import annotations

from textual.widgets import DataTable, Static

from ..models import Disk, human_bytes
from .base import PageBase


class DiskScanPage(PageBase):
    def compose(self):
        yield Static("", id="scan-help", classes="help")
        yield Static("", id="scan-recommendation", classes="recommendation")
        yield DataTable(id="disk-table")

    def on_mount(self) -> None:
        table = self.query_one("#disk-table", DataTable)
        table.add_columns("Disk", "Boyut", "Tur", "Baglanti", "Model", "Seri", "Kullanilan Alan")
        self.refresh_copy()

    def refresh_copy(self) -> None:
        self.query_one("#scan-help", Static).update(self.tr("scan_help"))

    def set_disks(self, disks: list[Disk]) -> None:
        table = self.query_one("#disk-table", DataTable)
        table.clear()
        if not disks:
            self.query_one("#scan-recommendation", Static).update(self.tr("no_disks"))
            return

        recommendation = self.services.disk_service.build_overview_message(disks)
        self.query_one("#scan-recommendation", Static).update(recommendation)
        for disk in disks:
            table.add_row(
                disk.path,
                disk.human_size,
                disk.storage_kind,
                disk.connection_type,
                " ".join(part for part in [disk.vendor, disk.model] if part).strip() or "Bilinmiyor",
                disk.serial or "-",
                human_bytes(disk.used_bytes),
            )
