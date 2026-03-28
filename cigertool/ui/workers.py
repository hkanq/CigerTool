from __future__ import annotations

from typing import Callable

from PySide6.QtCore import QObject, Signal, QRunnable, QThreadPool


class TaskSignals(QObject):
    started = Signal()
    message = Signal(str)
    result = Signal(object)
    finished = Signal()
    failed = Signal(str)


class TaskRunnable(QRunnable):
    def __init__(self, fn: Callable[[Callable[[str], None]], None]) -> None:
        super().__init__()
        self.fn = fn
        self.signals = TaskSignals()

    @staticmethod
    def _safe_emit(signal, *args) -> None:
        try:
            signal.emit(*args)
        except RuntimeError:
            pass

    def run(self) -> None:
        self._safe_emit(self.signals.started)
        try:
            value = self.fn(lambda message: self._safe_emit(self.signals.message, message))
        except Exception as exc:  # pragma: no cover - GUI background failures
            self._safe_emit(self.signals.failed, str(exc))
        else:
            self._safe_emit(self.signals.result, value)
            self._safe_emit(self.signals.finished)


class TaskRunner:
    def __init__(self) -> None:
        self.pool = QThreadPool.globalInstance()

    def submit(self, fn: Callable[[Callable[[str], None]], None]) -> TaskSignals:
        runnable = TaskRunnable(fn)
        self.pool.start(runnable)
        return runnable.signals
