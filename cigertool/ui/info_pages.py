from __future__ import annotations

from textual.containers import Horizontal
from textual.widgets import Button, DataTable, RichLog, Static

from ..models import Disk
from .base import PageBase


class HealthPage(PageBase):
    def __init__(self, services, language: str = "tr", **kwargs) -> None:
        super().__init__(services, language=language, **kwargs)
        self.disks: list[Disk] = []

    def compose(self):
        yield Static("", id="health-help", classes="help")
        with Horizontal(classes="button-row"):
            yield Button("SMART Tara", id="health-refresh", variant="primary")
        yield DataTable(id="health-table")

    def on_mount(self) -> None:
        table = self.query_one("#health-table", DataTable)
        table.add_columns("Disk", "Durum", "Sicaklik", "Saat", "Notlar")
        self.refresh_copy()

    def refresh_copy(self) -> None:
        self.query_one("#health-help", Static).update(self.tr("health_help"))

    def set_disks(self, disks: list[Disk]) -> None:
        self.disks = disks

    def on_page_shown(self) -> None:
        self.refresh_health()

    def on_button_pressed(self, event: Button.Pressed) -> None:
        if event.button.id == "health-refresh":
            self.refresh_health()

    def refresh_health(self) -> None:
        table = self.query_one("#health-table", DataTable)
        table.clear()
        for disk in self.disks:
            try:
                health = self.services.smart_service.read_health(disk.path)
            except Exception as exc:
                table.add_row(disk.path, "Okunamadi", "-", "-", str(exc))
                continue
            table.add_row(
                disk.path,
                health.overall_status,
                f"{health.temperature_c:.0f} C" if health.temperature_c is not None else "-",
                str(health.power_on_hours) if health.power_on_hours is not None else "-",
                ", ".join(health.notes) or "-",
            )


class LogsPage(PageBase):
    def compose(self):
        yield Static("", id="logs-help", classes="help")
        yield RichLog(id="logs-view", wrap=True, highlight=False, markup=False)

    def on_mount(self) -> None:
        self.refresh_copy()

    def refresh_copy(self) -> None:
        self.query_one("#logs-help", Static).update(self.tr("log_help"))

    def on_page_shown(self) -> None:
        self.refresh_logs()

    def refresh_logs(self) -> None:
        log = self.query_one("#logs-view", RichLog)
        log.clear()
        for line in self.services.log_service.tail().splitlines():
            log.write(line)
