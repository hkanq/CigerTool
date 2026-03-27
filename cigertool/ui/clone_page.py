from __future__ import annotations

from threading import Thread

from textual.containers import Horizontal, Vertical
from textual.widgets import Button, Checkbox, ProgressBar, RichLog, Select, Static

from ..models import CloneMode, Disk, ExecutionEvent, OperationPlan
from ..services.clone_service import PlanningError
from .base import ConfirmScreen, PageBase, disk_option_label


class ClonePage(PageBase):
    def __init__(self, services, mode: CloneMode, help_key: str, language: str = "tr", **kwargs) -> None:
        super().__init__(services, language=language, **kwargs)
        self.mode = mode
        self.help_key = help_key
        self.disks: list[Disk] = []
        self.current_plan: OperationPlan | None = None
        self.operation_running = False

    @property
    def prefix(self) -> str:
        return f"{self.mode.value}-page"

    def compose(self):
        with Horizontal(classes="page-layout"):
            with Vertical(classes="form-panel"):
                yield Static("", id=f"{self.prefix}-help", classes="help")
                yield Static(self.tr("source_disk"), classes="field-label")
                yield Select([], prompt="Kaynak diski secin", id=f"{self.prefix}-source")
                yield Static(self.tr("target_disk"), classes="field-label")
                yield Select([], prompt="Hedef diski secin", id=f"{self.prefix}-target")
                yield Checkbox(self.tr("dry_run"), value=True, id=f"{self.prefix}-dryrun")
                with Horizontal(classes="button-row"):
                    yield Button(self.tr("analyze"), id=f"{self.prefix}-analyze", variant="primary")
                    yield Button(self.tr("run"), id=f"{self.prefix}-run", variant="success")
            with Vertical(classes="output-panel"):
                yield Static(self.tr("execution_required"), id=f"{self.prefix}-summary", classes="summary")
                yield Static("", id=f"{self.prefix}-warnings", classes="warnings")
                yield ProgressBar(total=100, id=f"{self.prefix}-progress")
                yield RichLog(id=f"{self.prefix}-log", wrap=True, highlight=True, markup=False)

    def on_mount(self) -> None:
        self.query_one(f"#{self.prefix}-run", Button).disabled = True
        self.refresh_copy()

    def refresh_copy(self) -> None:
        self.query_one(f"#{self.prefix}-help", Static).update(self.tr(self.help_key))
        self.query_one(f"#{self.prefix}-analyze", Button).label = self.tr("analyze")
        self.query_one(f"#{self.prefix}-run", Button).label = self.tr("run")
        self.query_one(f"#{self.prefix}-dryrun", Checkbox).label = self.tr("dry_run")
        if not self.current_plan:
            self.query_one(f"#{self.prefix}-summary", Static).update(self.tr("execution_required"))

    def set_disks(self, disks: list[Disk]) -> None:
        self.disks = disks
        options = [(disk_option_label(disk), disk.path) for disk in disks]
        source_select = self.query_one(f"#{self.prefix}-source", Select)
        target_select = self.query_one(f"#{self.prefix}-target", Select)
        source_select.set_options(options)
        target_select.set_options(options)
        if options:
            source_select.value = options[0][1]
            target_select.value = options[-1][1]

    def on_button_pressed(self, event: Button.Pressed) -> None:
        if event.button.id == f"{self.prefix}-analyze":
            self._analyze_plan()
        elif event.button.id == f"{self.prefix}-run":
            self._confirm_and_run()

    def _analyze_plan(self) -> None:
        try:
            source, target = self._selected_disks()
            dry_run = self.query_one(f"#{self.prefix}-dryrun", Checkbox).value
            if self.mode is CloneMode.FULL:
                plan = self.services.clone_service.build_full_clone_plan(source, target, dry_run=dry_run)
            elif self.mode is CloneMode.SMART:
                plan = self.services.clone_service.build_smart_clone_plan(source, target, dry_run=dry_run)
            else:
                plan = self.services.clone_service.build_windows_migration_plan(source, target, dry_run=dry_run)
        except (ValueError, PlanningError) as exc:
            self.current_plan = None
            self.query_one(f"#{self.prefix}-run", Button).disabled = True
            self.query_one(f"#{self.prefix}-summary", Static).update(str(exc))
            self.query_one(f"#{self.prefix}-warnings", Static).update("")
            self.app.notify(str(exc), severity="error")
            return

        self.current_plan = plan
        self.query_one(f"#{self.prefix}-run", Button).disabled = False
        self.query_one(f"#{self.prefix}-summary", Static).update(plan.summary)
        self.query_one(f"#{self.prefix}-warnings", Static).update(self._render_plan_details(plan))
        self.app.notify(self.tr("execution_ready"))

    def _selected_disks(self) -> tuple[Disk, Disk]:
        source_value = self.query_one(f"#{self.prefix}-source", Select).value
        target_value = self.query_one(f"#{self.prefix}-target", Select).value
        source = self.services.disk_service.find_disk(self.disks, str(source_value))
        target = self.services.disk_service.find_disk(self.disks, str(target_value))
        if source is None or target is None:
            raise ValueError("Kaynak ve hedef disk secilmeli.")
        return source, target

    def _render_plan_details(self, plan: OperationPlan) -> str:
        warnings = "\n".join(f"- {warning}" for warning in plan.warnings)
        steps = "\n".join(f"{index}. {step.description}" for index, step in enumerate(plan.steps, start=1))
        return f"Uyarilar:\n{warnings}\n\nAdimlar:\n{steps}"

    def _confirm_and_run(self) -> None:
        if not self.current_plan:
            self.app.notify(self.tr("execution_required"), severity="warning")
            return
        if self.operation_running:
            self.app.notify("Bir islem zaten calisiyor.", severity="warning")
            return

        body = f"{self.current_plan.summary}\n\n" + "\n".join(
            f"- {warning}" for warning in self.current_plan.warnings
        )
        self.app.push_screen(ConfirmScreen(self.tr("confirm"), body), self._after_confirmation)

    def _after_confirmation(self, approved: bool) -> None:
        if not approved or not self.current_plan:
            return
        self.operation_running = True
        self.query_one(f"#{self.prefix}-run", Button).disabled = True
        self.query_one(f"#{self.prefix}-progress", ProgressBar).update(progress=0)
        log = self.query_one(f"#{self.prefix}-log", RichLog)
        log.clear()
        log.write(f"Baslatiliyor: {self.current_plan.title}")

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
        self.query_one(f"#{self.prefix}-progress", ProgressBar).update(progress=int(event.progress * 100))
        if event.message:
            self.query_one(f"#{self.prefix}-log", RichLog).write(event.message)

    def _finish_execution(self, success: bool, error_message: str | None) -> None:
        self.operation_running = False
        self.query_one(f"#{self.prefix}-run", Button).disabled = False
        if success:
            self.app.notify(self.tr("completed"))
        else:
            self.query_one(f"#{self.prefix}-log", RichLog).write(error_message or "Islem basarisiz oldu.")
            self.app.notify(error_message or self.tr("failed"), severity="error")
