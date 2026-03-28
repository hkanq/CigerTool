STYLE_SHEET = """
QWidget {
    background-color: #0b1220;
    color: #e6f1ff;
    font-family: Segoe UI;
    font-size: 10pt;
}
QMainWindow, QFrame#Card {
    background-color: #0f172a;
}
QPushButton {
    background-color: #12314e;
    border: 1px solid #1d5f8f;
    border-radius: 10px;
    padding: 10px 14px;
}
QPushButton:hover {
    background-color: #14557f;
}
QPushButton[accent="true"] {
    background-color: #0ea5a7;
    color: #041018;
    font-weight: 700;
    border: none;
}
QPushButton[danger="true"] {
    background-color: #8b1e3f;
}
QLabel#Title {
    font-size: 22pt;
    font-weight: 700;
    color: #e6fbff;
}
QLabel#Subtitle {
    color: #9ec7dc;
    font-size: 10pt;
}
QListWidget, QTreeView, QTextEdit, QPlainTextEdit, QTableWidget, QComboBox, QLineEdit {
    background-color: #08111d;
    border: 1px solid #1c334d;
    border-radius: 10px;
    padding: 6px;
}
QGroupBox {
    border: 1px solid #1c334d;
    border-radius: 12px;
    margin-top: 12px;
    padding: 12px;
    font-weight: 600;
}
QGroupBox::title {
    subcontrol-origin: margin;
    left: 12px;
    padding: 0 6px;
}
QProgressBar {
    background-color: #08111d;
    border: 1px solid #1c334d;
    border-radius: 10px;
    text-align: center;
}
QProgressBar::chunk {
    background-color: #14b8a6;
    border-radius: 8px;
}
QListWidget::item:selected, QTreeView::item:selected, QTableWidget::item:selected {
    background-color: #164e63;
}
QHeaderView::section {
    background-color: #11263d;
    color: #d9f7ff;
    border: none;
    padding: 8px;
}
"""

