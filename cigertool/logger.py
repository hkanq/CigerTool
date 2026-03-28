from __future__ import annotations

from pathlib import Path
import logging

from .config import LOG_PATH


def get_logger() -> logging.Logger:
    logger = logging.getLogger("cigertool")
    if logger.handlers:
        return logger

    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    handler = logging.FileHandler(LOG_PATH, encoding="utf-8")
    formatter = logging.Formatter("%(asctime)s [%(levelname)s] %(message)s")
    handler.setFormatter(formatter)
    logger.setLevel(logging.INFO)
    logger.addHandler(handler)
    logger.propagate = False
    return logger


def tail_log(lines: int = 200) -> str:
    path = Path(LOG_PATH)
    if not path.exists():
        return ""
    content = path.read_text(encoding="utf-8", errors="replace").splitlines()
    return "\n".join(content[-lines:])

