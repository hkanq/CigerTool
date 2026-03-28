from __future__ import annotations

import sys

from PySide6.QtWidgets import QApplication

from .app_context import create_context
from .ui.main_window import MainWindow


def main() -> None:
    app = QApplication(sys.argv)
    context = create_context()
    window = MainWindow(context)
    window.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
