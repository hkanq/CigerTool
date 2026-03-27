from __future__ import annotations

from logging.handlers import RotatingFileHandler
from pathlib import Path
import logging

from .config import resolve_log_path


def setup_logging(log_path: Path | None = None) -> tuple[logging.Logger, Path]:
    logger = logging.getLogger("cigertool")
    if logger.handlers:
        for handler in logger.handlers:
            if isinstance(handler, RotatingFileHandler):
                return logger, Path(handler.baseFilename)
        return logger, resolve_log_path()

    final_path = log_path or resolve_log_path()
    final_path.parent.mkdir(parents=True, exist_ok=True)

    handler = RotatingFileHandler(
        final_path,
        maxBytes=2_000_000,
        backupCount=3,
        encoding="utf-8",
    )
    formatter = logging.Formatter(
        "%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
    handler.setFormatter(formatter)

    logger.setLevel(logging.INFO)
    logger.addHandler(handler)
    logger.propagate = False
    logger.info("CigerTool log baslatildi. Dosya: %s", final_path)
    return logger, final_path
