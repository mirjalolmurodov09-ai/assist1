"""Test generatori — kirish nuqtasi.  Ishga tushirish:  python main.py"""
from __future__ import annotations

import logging
import sys
from logging.handlers import RotatingFileHandler

from paths import logs_dir


def setup_logging() -> None:
    """logs/app.log fayliga yozadigan log tizimini sozlaydi."""
    root = logging.getLogger("testgen")
    root.setLevel(logging.INFO)
    if root.handlers:
        return
    h = RotatingFileHandler(logs_dir() / "app.log", maxBytes=1_000_000, backupCount=3, encoding="utf-8")
    h.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
    root.addHandler(h)


def main() -> int:
    setup_logging()
    log = logging.getLogger("testgen")
    log.info("Dastur ishga tushdi")

    def hook(exc_type, exc, tb) -> None:  # tkinterdan tashqaridagi kutilmagan xatolar
        log.error("Kutilmagan xato", exc_info=(exc_type, exc, tb))
        sys.__excepthook__(exc_type, exc, tb)

    sys.excepthook = hook
    try:
        import tkinter  # noqa: F401
    except ImportError:
        print("Tkinter o'rnatilmagan. Linux: sudo apt install python3-tk", file=sys.stderr)
        return 1
    if sys.platform.startswith("win"):
        try:  # aniq (blur bo'lmagan) interfeys
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)  # type: ignore[attr-defined]
        except Exception:  # noqa: BLE001
            pass
    from gui import run
    run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
