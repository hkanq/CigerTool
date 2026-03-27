from __future__ import annotations

from textual.app import App
from textual.binding import Binding
from textual.containers import Horizontal, Vertical
from textual.widgets import Button, Footer, Header, Static

from .commands import CommandRunner
from .config import AppSettings
from .i18n import t
from .logger import setup_logging
from .models import CloneMode, Disk
from .services.boot_service import BootRepairService
from .services.clone_service import CloneService
from .services.disk_service import DiskService
from .services.execution_service import PlanExecutor
from .services.log_service import LogService
from .services.resize_service import ResizeService
from .services.smart_service import SmartService
from .ui.base import PageBase, ServiceHub
from .ui.clone_page import ClonePage
from .ui.info_pages import HealthPage, LogsPage
from .ui.maintenance_pages import BootPage, ResizePage
from .ui.scan_page import DiskScanPage


class CigerToolApp(App):
    CSS_PATH = "app.tcss"
    TITLE = "CigerTool"
    BINDINGS = [
        Binding("1", "goto_scan", "1 Diskler"),
        Binding("2", "goto_full", "2 Tam Klon"),
        Binding("3", "goto_smart", "3 Akilli"),
        Binding("4", "goto_windows", "4 Windows"),
        Binding("5", "goto_resize", "5 Kucult"),
        Binding("6", "goto_boot", "6 Boot"),
        Binding("7", "goto_health", "7 SMART"),
        Binding("8", "goto_logs", "8 Loglar"),
        Binding("r", "refresh_data", "R Yenile"),
        Binding("f2", "toggle_theme", "F2 Tema"),
        Binding("f3", "toggle_language", "F3 Dil"),
        Binding("q", "quit", "Q Cikis"),
    ]

    def __init__(self, settings: AppSettings | None = None) -> None:
        super().__init__()
        self.settings = settings or AppSettings()
        self.language = self.settings.language
        self.logger, log_path = setup_logging()
        runner = CommandRunner(self.logger)
        self.services = ServiceHub(
            disk_service=DiskService(runner, self.logger),
            clone_service=CloneService(self.logger),
            resize_service=ResizeService(self.logger),
            smart_service=SmartService(runner, self.logger),
            boot_service=BootRepairService(self.logger),
            log_service=LogService(log_path),
            executor=PlanExecutor(runner, self.logger),
        )
        self.disks: list[Disk] = []
        self.current_page = "scan"

    def compose(self):
        yield Header(show_clock=True)
        with Horizontal(id="app-body"):
            with Vertical(id="sidebar"):
                yield Static(t("app_title", self.language), id="brand")
                yield Static(t("app_subtitle", self.language), id="brand-subtitle")
                yield Button(t("menu_scan", self.language), id="nav-scan", classes="nav-button")
                yield Button(t("menu_full", self.language), id="nav-full", classes="nav-button")
                yield Button(t("menu_smart", self.language), id="nav-smart", classes="nav-button")
                yield Button(t("menu_windows", self.language), id="nav-windows", classes="nav-button")
                yield Button(t("menu_resize", self.language), id="nav-resize", classes="nav-button")
                yield Button(t("menu_boot", self.language), id="nav-boot", classes="nav-button")
                yield Button(t("menu_health", self.language), id="nav-health", classes="nav-button")
                yield Button(t("menu_logs", self.language), id="nav-logs", classes="nav-button")
                yield Button(t("menu_exit", self.language), id="nav-exit", classes="nav-button exit")
                yield Static("F2: Tema\nF3: Dil\nR: Yenile\nQ: Cikis", id="sidebar-help")
            with Vertical(id="pages"):
                yield DiskScanPage(self.services, language=self.language, id="page-scan")
                yield ClonePage(self.services, CloneMode.FULL, "full_help", language=self.language, id="page-full")
                yield ClonePage(self.services, CloneMode.SMART, "smart_help", language=self.language, id="page-smart")
                yield ClonePage(self.services, CloneMode.WINDOWS, "windows_help", language=self.language, id="page-windows")
                yield ResizePage(self.services, language=self.language, id="page-resize")
                yield BootPage(self.services, language=self.language, id="page-boot")
                yield HealthPage(self.services, language=self.language, id="page-health")
                yield LogsPage(self.services, language=self.language, id="page-logs")
        yield Footer()

    def on_mount(self) -> None:
        self.add_class("theme-dark")
        self.show_page("scan")
        self.refresh_data()

    def on_button_pressed(self, event: Button.Pressed) -> None:
        if not event.button.id or not event.button.id.startswith("nav-"):
            return
        target = event.button.id.replace("nav-", "")
        if target == "exit":
            self.exit()
            return
        self.show_page(target)

    def show_page(self, page_name: str) -> None:
        self.current_page = page_name
        for name in ["scan", "full", "smart", "windows", "resize", "boot", "health", "logs"]:
            page = self.query_one(f"#page-{name}", PageBase)
            page.display = name == page_name
            if name == page_name:
                page.on_page_shown()
        self._highlight_nav(page_name)

    def _highlight_nav(self, page_name: str) -> None:
        for name in ["scan", "full", "smart", "windows", "resize", "boot", "health", "logs", "exit"]:
            button = self.query_one(f"#nav-{name}", Button)
            button.variant = "primary" if name == page_name else "default"
            if name == "exit":
                button.variant = "default"

    def refresh_data(self) -> None:
        try:
            self.disks = self.services.disk_service.scan_disks()
        except Exception as exc:
            self.logger.exception("Disk tarama basarisiz oldu")
            self.notify(str(exc), severity="error")
            self.disks = []
        for page_id in ["scan", "full", "smart", "windows", "resize", "boot", "health", "logs"]:
            self.query_one(f"#page-{page_id}", PageBase).set_disks(self.disks)
        self.query_one("#page-logs", LogsPage).refresh_logs()

    def action_refresh_data(self) -> None:
        self.refresh_data()
        self.notify("Disk bilgileri yenilendi.")

    def action_toggle_theme(self) -> None:
        if self.has_class("theme-dark"):
            self.remove_class("theme-dark")
            self.add_class("theme-light")
        else:
            self.remove_class("theme-light")
            self.add_class("theme-dark")

    def action_toggle_language(self) -> None:
        self.language = "en" if self.language == "tr" else "tr"
        self._refresh_app_copy()

    def _refresh_app_copy(self) -> None:
        self.query_one("#brand", Static).update(t("app_title", self.language))
        self.query_one("#brand-subtitle", Static).update(t("app_subtitle", self.language))
        nav_keys = {
            "scan": "menu_scan",
            "full": "menu_full",
            "smart": "menu_smart",
            "windows": "menu_windows",
            "resize": "menu_resize",
            "boot": "menu_boot",
            "health": "menu_health",
            "logs": "menu_logs",
            "exit": "menu_exit",
        }
        for name, key in nav_keys.items():
            self.query_one(f"#nav-{name}", Button).label = t(key, self.language)
        for page_id in ["scan", "full", "smart", "windows", "resize", "boot", "health", "logs"]:
            self.query_one(f"#page-{page_id}", PageBase).set_language(self.language)

    def action_goto_scan(self) -> None:
        self.show_page("scan")

    def action_goto_full(self) -> None:
        self.show_page("full")

    def action_goto_smart(self) -> None:
        self.show_page("smart")

    def action_goto_windows(self) -> None:
        self.show_page("windows")

    def action_goto_resize(self) -> None:
        self.show_page("resize")

    def action_goto_boot(self) -> None:
        self.show_page("boot")

    def action_goto_health(self) -> None:
        self.show_page("health")

    def action_goto_logs(self) -> None:
        self.show_page("logs")
