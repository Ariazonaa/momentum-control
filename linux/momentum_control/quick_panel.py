"""Small popup at the tray icon, like the quick panel of the Windows app (left-click on the tray icon).

It shows the model, battery and status and offers the noise modes and, in ANC mode, the ANC strength. Every action
goes through the main window (``select_noise_mode``/``write``), so the same guards, queue and readback apply.
"""

from PySide6.QtCore import QPoint, Qt
from PySide6.QtGui import QCursor, QGuiApplication
from PySide6.QtWidgets import QButtonGroup, QFrame, QLabel, QPushButton, QRadioButton, QSlider, QVBoxLayout, QGridLayout

MODES = (('transparency', 'Full transparency'), ('anc', 'ANC'), ('adaptive', 'Adaptive'), ('off', 'Off'))


def product_title(snapshot) -> str:
    model = getattr(snapshot, 'model', '') or ''
    if model.startswith('M5AEBT'):
        return 'MOMENTUM 5'
    if model.startswith('M4AEBT') or model == 'MOMENTUM 4':
        return 'MOMENTUM 4'
    return 'Momentum Control'


class QuickPanel(QFrame):
    def __init__(self, window):
        super().__init__(window, Qt.WindowType.Popup | Qt.WindowType.FramelessWindowHint)
        from .gui import mode_available, noise_mode  # gui imports this module; resolve lazily
        self._mode_available = mode_available
        self._noise_mode = noise_mode
        self.window = window
        self.setFrameShape(QFrame.Shape.StyledPanel)
        self.setMinimumWidth(260)
        layout = QVBoxLayout(self)
        self.title = QLabel()
        self.title.setStyleSheet('font-weight: 600;')
        self.battery = QLabel()
        self.status = QLabel()
        self.status.setWordWrap(True)
        for label in (self.title, self.battery, self.status):
            label.setTextFormat(Qt.TextFormat.PlainText)
            layout.addWidget(label)
        grid = QGridLayout()
        self.group = QButtonGroup(self)
        self.group.setExclusive(True)
        self.mode_buttons = {}
        for index, (mode, text) in enumerate(MODES):
            button = QRadioButton(window.tr(text))
            button.clicked.connect(lambda checked=False, mode=mode: self.choose(mode))
            self.group.addButton(button)
            self.mode_buttons[mode] = button
            grid.addWidget(button, index // 2, index % 2)
        layout.addLayout(grid)
        self.strength_label = QLabel(window.tr('ANC strength'))
        layout.addWidget(self.strength_label)
        self.strength = QSlider(Qt.Orientation.Horizontal)
        self.strength.setRange(0, 100)
        self.strength.setPageStep(10)
        self.strength.sliderReleased.connect(self.commit_strength)
        self.strength.setAccessibleName(window.tr('ANC strength'))
        layout.addWidget(self.strength)
        self.open_button = QPushButton(window.tr('Open'))
        self.open_button.clicked.connect(self.open_app)
        layout.addWidget(self.open_button)

    def choose(self, mode):
        if not self.window.select_noise_mode(mode):
            self.refresh()

    def commit_strength(self):
        snapshot = self.window.snapshot
        if snapshot is None or 'anc_strength' not in snapshot.writable:
            return self.refresh()
        if not self.window.write('anc_strength', 100 - self.strength.value()):
            self.refresh()

    def open_app(self):
        self.hide()
        self.window.open_window()

    def refresh(self):
        window = self.window
        snapshot = window.snapshot
        self.title.setText(product_title(snapshot))
        if snapshot is not None and snapshot.battery is not None:
            self.battery.setText(f'{snapshot.battery} %')
        else:
            self.battery.setText(window.bluetooth_battery_text())
        self.status.setText(window.status.text())
        ready = snapshot is not None and not window.busy and not window.closing
        current = self._noise_mode(snapshot)
        self.group.setExclusive(False)
        for mode, button in self.mode_buttons.items():
            button.setEnabled(ready and self._mode_available(snapshot, mode))
            button.setChecked(mode == current)
        self.group.setExclusive(True)
        strength = (snapshot is not None and current == 'anc' and 'anc_strength' in snapshot.settings
                    and 'anc_strength' in snapshot.writable)
        self.strength_label.setVisible(strength)
        self.strength.setVisible(strength)
        self.strength.setEnabled(strength and ready)
        if strength and not self.strength.isSliderDown():
            self.strength.setValue(100 - int(snapshot.settings['anc_strength']))
        self.adjustSize()

    def toggle(self):
        if self.isVisible():
            self.hide()
            return
        self.refresh()
        self.adjustSize()
        point = QCursor.pos()
        screen = QGuiApplication.screenAt(point) or QGuiApplication.primaryScreen()
        if screen is not None:
            area = screen.availableGeometry()
            x = min(max(point.x() - self.width() // 2, area.left()), area.right() - self.width())
            y = point.y() - self.height() if point.y() + self.height() > area.bottom() else point.y()
            self.move(QPoint(x, max(y, area.top())))
        self.show()
        self.raise_()
