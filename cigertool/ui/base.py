from __future__ import annotations

from dataclasses import dataclass

from textual.containers import Horizontal, Vertical, VerticalScroll
from textual.screen import ModalScreen
from textual.widgets import Button, Static

from ..i18n import t
from ..models import Disk, human_bytes
from ..services.boot_service import BootRepairService
from ..services.clone_service import CloneService
from ..services.disk_service import DiskService
from ..services.execution_service import PlanExecutor
from ..services.log_service import LogService
from ..services.resize_service import ResizeService
from ..services.smart_service import SmartService


@dataclass(slots=True)
class ServiceHub:
    disk_service: DiskService
    clone_service: CloneService
    resize_service: ResizeService
    smart_service: SmartService
    boot_service: BootRepairService
    log_service: LogService
    executor: PlanExecutor


def disk_option_label(disk: Disk) -> str:
    used = human_bytes(disk.used_bytes)
    model = " ".join(part for part in [disk.vendor, disk.model] if part).strip() or "Model bilinmiyor"
    serial = disk.serial or "Seri yok"
    return f"{disk.path} | {disk.human_size} | {disk.connection_type} | {model} | {serial} | Kullanilan {used}"


class ConfirmScreen(ModalScreen[bool]):
    def __init__(self, title: str, body: str) -> None:
        super().__init__()
        self.title = title
        self.body = body

    def compose(self):
        with Vertical(id="confirm-dialog"):
            yield Static(self.title, classes="dialog-title")
            yield Static(self.body, id="confirm-body")
            with Horizontal(classes="dialog-actions"):
                yield Button("Iptal", id="confirm-cancel", variant="default")
                yield Button("Onayla ve Baslat", id="confirm-approve", variant="error")

    def on_button_pressed(self, event: Button.Pressed) -> None:
        self.dismiss(event.button.id == "confirm-approve")


class PageBase(VerticalScroll):
    def __init__(self, services: ServiceHub, language: str = "tr", **kwargs) -> None:
        super().__init__(**kwargs)
        self.services = services
        self.language = language

    def set_language(self, language: str) -> None:
        self.language = language
        self.refresh_copy()

    def refresh_copy(self) -> None:
        pass

    def set_disks(self, disks: list[Disk]) -> None:
        pass

    def on_page_shown(self) -> None:
        pass

    def tr(self, key: str, **kwargs) -> str:
        return t(key, self.language, **kwargs)
