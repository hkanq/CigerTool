from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import json
import logging
import subprocess
from collections.abc import Callable


@dataclass(slots=True)
class CommandResult:
    returncode: int
    stdout: str
    stderr: str


class CommandError(RuntimeError):
    pass


class CommandRunner:
    def __init__(self, logger: logging.Logger, dry_run: bool = False) -> None:
        self.logger = logger
        self.dry_run = dry_run

    def run(
        self,
        command: list[str] | str,
        *,
        shell: bool = False,
        check: bool = True,
        cwd: str | Path | None = None,
        dry_run: bool | None = None,
    ) -> CommandResult:
        active_dry_run = self.dry_run if dry_run is None else dry_run
        rendered = command if isinstance(command, str) else " ".join(command)
        self.logger.info("Komut: %s", rendered)
        if active_dry_run:
            return CommandResult(0, "", f"[DRY-RUN] {rendered}")

        completed = subprocess.run(
            command,
            shell=shell,
            cwd=str(cwd) if cwd else None,
            text=True,
            capture_output=True,
            check=False,
        )
        result = CommandResult(completed.returncode, completed.stdout or "", completed.stderr or "")
        if check and completed.returncode != 0:
            raise CommandError(result.stderr or result.stdout or rendered)
        return result

    def powershell_json(self, script: str) -> object:
        wrapper = [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            script,
        ]
        result = self.run(wrapper, dry_run=False)
        output = result.stdout.strip() or "null"
        return json.loads(output)

    def run_streaming(
        self,
        command: list[str] | str,
        *,
        shell: bool = False,
        check: bool = True,
        cwd: str | Path | None = None,
        dry_run: bool | None = None,
        on_line: Callable[[str], None] | None = None,
    ) -> CommandResult:
        active_dry_run = self.dry_run if dry_run is None else dry_run
        rendered = command if isinstance(command, str) else " ".join(command)
        self.logger.info("Komut: %s", rendered)
        if active_dry_run:
            text = f"[DRY-RUN] {rendered}"
            if on_line:
                on_line(text)
            return CommandResult(0, text, "")

        process = subprocess.Popen(
            command,
            shell=shell,
            cwd=str(cwd) if cwd else None,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            bufsize=1,
        )
        output_lines: list[str] = []
        assert process.stdout is not None
        for line in process.stdout:
            cleaned = line.rstrip()
            output_lines.append(cleaned)
            if on_line and cleaned:
                on_line(cleaned)

        process.wait()
        stdout = "\n".join(output_lines)
        result = CommandResult(process.returncode, stdout, "")
        if check and process.returncode != 0:
            raise CommandError(result.stdout or rendered)
        return result
