from __future__ import annotations

from collections import deque
from pathlib import Path


class LogService:
    def __init__(self, log_path: Path) -> None:
        self.log_path = log_path

    def tail(self, lines: int = 200) -> str:
        if not self.log_path.exists():
            return "Log dosyasi henuz olusmadi."

        buffer: deque[str] = deque(maxlen=lines)
        with self.log_path.open("r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                buffer.append(line.rstrip())
        return "\n".join(buffer)
