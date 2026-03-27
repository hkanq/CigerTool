from __future__ import annotations

from collections.abc import Callable
import logging

from ..commands import CommandRunner
from ..models import ExecutionEvent, OperationPlan


class PlanExecutor:
    def __init__(self, runner: CommandRunner, logger: logging.Logger) -> None:
        self.runner = runner
        self.logger = logger

    def execute(
        self,
        plan: OperationPlan,
        on_event: Callable[[ExecutionEvent], None] | None = None,
    ) -> None:
        total = max(1, len(plan.steps))

        def emit(event: ExecutionEvent) -> None:
            if on_event:
                on_event(event)

        for index, step in enumerate(plan.steps, start=1):
            emit(
                ExecutionEvent(
                    step_index=index,
                    total_steps=total,
                    description=step.description,
                    message=step.description,
                    progress=(index - 1) / total,
                    kind="step_start",
                )
            )

            self.runner.stream(
                step.command,
                callback=lambda line, idx=index, desc=step.description: emit(
                    ExecutionEvent(
                        step_index=idx,
                        total_steps=total,
                        description=desc,
                        message=line,
                        progress=(idx - 1) / total,
                        kind="log",
                    )
                ),
                shell=step.shell,
                dry_run=plan.dry_run,
            )

            emit(
                ExecutionEvent(
                    step_index=index,
                    total_steps=total,
                    description=step.description,
                    message=f"Tamamlandi: {step.description}",
                    progress=index / total,
                    kind="step_done",
                )
            )

        emit(
            ExecutionEvent(
                step_index=total,
                total_steps=total,
                description=plan.title,
                message=f"Plan tamamlandi: {plan.title}",
                progress=1.0,
                kind="completed",
            )
        )
