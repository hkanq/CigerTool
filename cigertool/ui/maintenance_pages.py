from __future__ import annotations

from threading import Thread

from textual.containers import Horizontal, Vertical
from textual.widgets import Button, Checkbox, Input, ProgressBar, RichLog, Select, Static

from ..models import Disk, ExecutionEvent, OperationPlan, Partition, human_bytes
from .base import ConfirmScreen, PageBase, disk_option_label


class ResizePage(PageBase):
    def __init__(self, services, language: str = "tr", **kwargs) -> None:
        super().__init__(services, language=language, **kwargs)
        self.disks: list[Disk] = []
        self.current_plan: OperationPlan | None = None
        self.operation_running = False

    def compose(self):
        with Horizontal(classes="page-layout"):
            with Vertical(classes="form-panel"):
                yield Static("", id="resize-help", classes="help")
                yield Static(self.tr("source_disk"), classes="field-label")
                yield Select([], prompt="Disk secin", id="resize-disk")
                yield Static(self.tr("partition"), classes="field-label")
                yield Select([], prompt="NTFS bolumu secin", id="resize-partition")
                yield Static(self.tr("target_size"), classes="field-label")
                yield Input(placeholder="Ornek: 90", id="resize-target-gb")
                yield Checkbox(self.tr("dry_run"), value=True, id="resize-dryrun")
                with Horizontal(classes="button-row"):
                    yield Button(self.tr("analyze"), id="resize-analyze", variant="primary")
                    yield Button(self.tr("run"), id="resize-run", variant="success")
            with Vertical(classes="output-panel"):
                yield Static(self.tr("execution_required"), id="resize-summary", classes="summary")
                yield Static("", id="resize-warnings", classes="warnings")
                yield ProgressBar(total=100, id="resize-progress")
                yield RichLog(id="resize-log", wrap=True, highlight=True, markup=False)

    def on_mount(self) -> None:
        self.query_one("#resize-run", Button).disabled = True
        self.refresh_copy()

    def refresh_copy(self) -> None:
        self.query_one("#resize-help", Static).update(self.tr("resize_help"))
        self.query_one("#resize-dryrun", Checkbox).label = self.tr("dry_run")
        self.query_one("#resize-analyze", Button).label = self.tr("analyze")
        self.query_one("#resize-run", Button).label = self.tr("run")

    def set_disks(self, disks: list[Disk]) -> None:
        self.disks = disks
        disk_options = [(disk_option_label(disk), disk.path) for disk in disks]
        disk_select = self.query_one("#resize-disk", Select)
        disk_select.set_options(disk_options)
        if disk_options:
            disk_select.value = disk_options[0][1]
        self._update_partition_options()

    def on_select_changed(self, event: Select.Changed) -> None:
        if event.select.id == "resize-disk":
            self._update_partition_options()

    def on_button_pressed(self, event: Button.Pressed) -> None:
        if event.button.id == "resize-analyze":
            self._analyze_plan()
        elif event.button.id == "resize-run":
            self._confirm_and_run()

    def _update_partition_options(self) -> None:
        disk = self._selected_disk()
        options: list[tuple[str, str]] = []
        if disk:
            for partition in disk.partitions:
                if (partition.fs_type or "").lower() == "ntfs":
                    options.append(
                        (
                            f"{partition.path} | {partition.human_size} | Kullanilan {human_bytes(partition.used_bytes)}",
                            partition.path,
                        )
                    )
        select = self.query_one("#resize-partition", Select)
        select.set_options(options)
        if options:
            select.value = options[0][1]

    def _selected_disk(self) -> Disk | None:
        value = self.query_one("#resize-disk", Select).value
        return self.services.disk_service.find_disk(self.disks, str(value))

    def _selected_partition(self) -> tuple[Disk, Partition]:
        disk = self._selected_disk()
        if disk is None:
            raise ValueError("Disk secilmeli.")
        value = self.query_one("#resize-partition", Select).value
        partition = self.services.disk_service.find_partition(disk, str(value))
        if partition is None:
            raise ValueError("Bolum secilmeli.")
        return disk, partition

    def _analyze_plan(self) -> None:
        try:
            disk, partition = self._selected_partition()
            target_text = self.query_one("#resize-target-gb", Input).value.strip().replace(",", ".")
            target_bytes = int(float(target_text) * 1024**3)
            dry_run = self.query_one("#resize-dryrun", Checkbox).value
            plan = self.services.resize_service.build_ntfs_resize_plan(
                disk,
                partition,
                target_bytes,
                dry_run=dry_run,
            )
        except Exception as exc:
            self.current_plan = None
            self.query_one("#resize-run", Button).disabled = True
            self.query_one("#resize-summary", Static).update(str(exc))
            self.query_one("#resize-warnings", Static).update("")
            self.app.notify(str(exc), severity="error")
            return

        self.current_plan = plan
        self.query_one("#resize-run", Button).disabled = False
        self.query_one("#resize-summary", Static).update(plan.summary)
        self.query_one("#resize-warnings", Static).update("\n".join(f"- {warning}" for warning in plan.warnings))
        self.app.notify(self.tr("execution_ready"))

    def _confirm_and_run(self) -> None:
        if not self.current_plan:
            self.app.notify(self.tr("execution_required"), severity="warning")
            return
        if self.operation_running:
            self.app.notify("Bir islem zaten calisiyor.", severity="warning")
            return
        body = f"{self.current_plan.summary}\n\n" + "\n".join(f"- {item}" for item in self.current_plan.warnings)
        self.app.push_screen(ConfirmScreen(self.tr("confirm"), body), self._after_confirmation)

    def _after_confirmation(self, approved: bool) -> None:
        if not approved or not self.current_plan:
            return
        self.operation_running = True
        self.query_one("#resize-run", Button).disabled = True
        self.query_one("#resize-progress", ProgressBar).update(progress=0)
        self.query_one("#resize-log", RichLog).clear()

        def worker() -> None:
            try:
                self.services.executor.execute(
                    self.current_plan,
                    on_event=lambda event: self.app.call_from_thread(self._handle_event, event),
                )
            except Exception as exc:
                self.app.call_from_thread(self._finish_execution, False, str(exc))
            else:
                self.app.call_from_thread(self._finish_execution, True, None)

        Thread(target=worker, daemon=True).start()

    def _handle_event(self, event: ExecutionEvent) -> None:
        self.query_one("#resize-progress", ProgressBar).update(progress=int(event.progress * 100))
        if event.message:
            self.query_one("#resize-log", RichLog).write(event.message)

    def _finish_execution(self, success: bool, error_message: str | None) -> None:
        self.operation_running = False
        self.query_one("#resize-run", Button).disabled = False
        if success:
            self.app.notify(self.tr("completed"))
        else:
            self.query_one("#resize-log", RichLog).write(error_message or "Islem basarisiz oldu.")
            self.app.notify(error_message or self.tr("failed"), severity="error")


class BootPage(PageBase):
    def __init__(self, services, language: str = "tr", **kwargs) -> None:
        super().__init__(services, language=language, **kwargs)
        self.disks: list[Disk] = []
        self.current_plan: OperationPlan | None = None
        self.operation_running = False

    def compose(self):
        with Horizontal(classes="page-layout"):
            with Vertical(classes="form-panel"):
                yield Static("", id="boot-help", classes="help")
                yield Static(self.tr("target_disk"), classes="field-label")
                yield Select([], prompt="Windows hedef diskini secin", id="boot-disk")
                yield Checkbox(self.tr("dry_run"), value=True, id="boot-dryrun")
                with Horizontal(classes="button-row"):
                    yield Button(self.tr("analyze"), id="boot-analyze", variant="primary")
                    yield Button(self.tr("run"), id="boot-run", variant="success")
            with Vertical(classes="output-panel"):
                yield Static(self.tr("execution_required"), id="boot-summary", classes="summary")
                yield Static("", id="boot-warnings", classes="warnings")
                yield ProgressBar(total=100, id="boot-progress")
                yield RichLog(id="boot-log", wrap=True, highlight=True, markup=False)

    def on_mount(self) -> None:
        self.query_one("#boot-run", Button).disabled = True
        self.refresh_copy()

    def refresh_copy(self) -> None:
        self.query_one("#boot-help", Static).update(self.tr("boot_help"))
        self.query_one("#boot-dryrun", Checkbox).label = self.tr("dry_run")
        self.query_one("#boot-analyze", Button).label = self.tr("analyze")
        self.query_one("#boot-run", Button).label = self.tr("run")

    def set_disks(self, disks: list[Disk]) -> None:
        self.disks = disks
        options = [(disk_option_label(disk), disk.path) for disk in disks]
        select = self.query_one("#boot-disk", Select)
        select.set_options(options)
        if options:
            select.value = options[-1][1]

    def on_button_pressed(self, event: Button.Pressed) -> None:
        if event.button.id == "boot-analyze":
            self._analyze_plan()
        elif event.button.id == "boot-run":
            self._confirm_and_run()

    def _analyze_plan(self) -> None:
        try:
            value = self.query_one("#boot-disk", Select).value
            disk = self.services.disk_service.find_disk(self.disks, str(value))
            if disk is None:
                raise ValueError("Disk secilmeli.")
            dry_run = self.query_one("#boot-dryrun", Checkbox).value
            plan = self.services.boot_service.build_plan(disk, dry_run=dry_run)
        except Exception as exc:
            self.current_plan = None
            self.query_one("#boot-run", Button).disabled = True
            self.query_one("#boot-summary", Static).update(str(exc))
            self.query_one("#boot-warnings", Static).update("")
            self.app.notify(str(exc), severity="error")
            return

        self.current_plan = plan
        self.query_one("#boot-run", Button).disabled = False
        self.query_one("#boot-summary", Static).update(plan.summary)
        self.query_one("#boot-warnings", Static).update("\n".join(f"- {warning}" for warning in plan.warnings))
        self.app.notify(self.tr("execution_ready"))

    def _confirm_and_run(self) -> None:
        if not self.current_plan:
            self.app.notify(self.tr("execution_required"), severity="warning")
            return
        if self.operation_running:
            self.app.notify("Bir islem zaten calisiyor.", severity="warning")
            return
        body = f"{self.current_plan.summary}\n\n" + "\n".join(f"- {item}" for item in self.current_plan.warnings)
        self.app.push_screen(ConfirmScreen(self.tr("confirm"), body), self._after_confirmation)

    def _after_confirmation(self, approved: bool) -> None:
        if not approved or not self.current_plan:
            return
        self.operation_running = True
        self.query_one("#boot-run", Button).disabled = True
        self.query_one("#boot-progress", ProgressBar).update(progress=0)
        self.query_one("#boot-log", RichLog).clear()

        def worker() -> None:
            try:
                self.services.executor.execute(
                    self.current_plan,
                    on_event=lambda event: self.app.call_from_thread(self._handle_event, event),
                )
            except Exception as exc:
                self.app.call_from_thread(self._finish_execution, False, str(exc))
            else:
                self.app.call_from_thread(self._finish_execution, True, None)

        Thread(target=worker, daemon=True).start()

    def _handle_event(self, event: ExecutionEvent) -> None:
        self.query_one("#boot-progress", ProgressBar).update(progress=int(event.progress * 100))
        if event.message:
            self.query_one("#boot-log", RichLog).write(event.message)

    def _finish_execution(self, success: bool, error_message: str | None) -> None:
        self.operation_running = False
        self.query_one("#boot-run", Button).disabled = False
        if success:
            self.app.notify(self.tr("completed"))
        else:
            self.query_one("#boot-log", RichLog).write(error_message or "Islem basarisiz oldu.")
            self.app.notify(error_message or self.tr("failed"), severity="error")
