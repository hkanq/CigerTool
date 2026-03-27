from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import logging
import shlex
import subprocess


@dataclass(slots=True)
class CommandResult:
    returncode: int
    stdout: str
    stderr: str
    command_text: str


class CommandError(RuntimeError):
    def __init__(self, command_text: str, returncode: int, stderr: str) -> None:
        super().__init__(f"Komut basarisiz oldu ({returncode}): {command_text}\n{stderr}".strip())
        self.command_text = command_text
        self.returncode = returncode
        self.stderr = stderr


def format_command(command: list[str] | str) -> str:
    if isinstance(command, str):
        return command
    return shlex.join(command)


class CommandRunner:
    def __init__(self, logger: logging.Logger, dry_run: bool = False) -> None:
        self.logger = logger
        self.dry_run = dry_run

    def run(
        self,
        command: list[str] | str,
        *,
        check: bool = True,
        capture_output: bool = True,
        shell: bool = False,
        cwd: str | Path | None = None,
        dry_run: bool | None = None,
    ) -> CommandResult:
        command_text = format_command(command)
        effective_dry_run = self.dry_run if dry_run is None else dry_run
        self.logger.info("Komut calisiyor: %s", command_text)

        if effective_dry_run:
            message = f"[DRY-RUN] {command_text}"
            self.logger.info(message)
            return CommandResult(0, "", message, command_text)

        completed = subprocess.run(
            command_text if shell else command,
            capture_output=capture_output,
            text=True,
            shell=shell,
            cwd=str(cwd) if cwd else None,
            check=False,
        )
        stdout = completed.stdout or ""
        stderr = completed.stderr or ""

        if stdout:
            self.logger.info(stdout.rstrip())
        if stderr:
            self.logger.warning(stderr.rstrip())

        result = CommandResult(completed.returncode, stdout, stderr, command_text)
        if check and completed.returncode != 0:
            raise CommandError(command_text, completed.returncode, stderr or stdout)
        return result

    def stream(
        self,
        command: list[str] | str,
        callback,
        *,
        check: bool = True,
        shell: bool = False,
        cwd: str | Path | None = None,
        dry_run: bool | None = None,
    ) -> CommandResult:
        command_text = format_command(command)
        effective_dry_run = self.dry_run if dry_run is None else dry_run
        self.logger.info("Akim komutu calisiyor: %s", command_text)

        if effective_dry_run:
            message = f"[DRY-RUN] {command_text}"
            callback(message)
            self.logger.info(message)
            return CommandResult(0, "", message, command_text)

        process = subprocess.Popen(
            command_text if shell else command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            shell=shell,
            cwd=str(cwd) if cwd else None,
            bufsize=1,
        )
        output_lines: list[str] = []
        assert process.stdout is not None
        for line in process.stdout:
            callback(line.rstrip())
            output_lines.append(line)
            self.logger.info(line.rstrip())

        process.wait()
        stdout = "".join(output_lines)
        result = CommandResult(process.returncode or 0, stdout, "", command_text)
        if check and process.returncode != 0:
            raise CommandError(command_text, process.returncode or 0, stdout)
        return result
