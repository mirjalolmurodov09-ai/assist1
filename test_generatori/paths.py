"""Fayl va papka yo'llari (oddiy ishga tushirishda ham, PyInstaller .exe da ham)."""
from __future__ import annotations

import os
import sys
from pathlib import Path


def is_frozen() -> bool:
    """PyInstaller bilan yig'ilgan .exe ichida ishlayaptimi?"""
    return bool(getattr(sys, "frozen", False))


def app_dir() -> Path:
    """Dastur joylashgan papka (yozish mumkin bo'lgan ma'lumotlar shu yerda)."""
    if is_frozen():
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def resource_path(rel: str) -> Path:
    """Dastur bilan birga keladigan resurs (masalan shrift) yo'li."""
    base = Path(getattr(sys, "_MEIPASS", app_dir()))
    return base / rel


def _writable_dir(preferred: Path, fallback_name: str) -> Path:
    """`preferred` ga yozish mumkin bo'lsa o'shani, bo'lmasa uy papkasidagi nusxani qaytaradi."""
    try:
        preferred.mkdir(parents=True, exist_ok=True)
        probe = preferred / ".write_test"
        probe.write_text("x", encoding="utf-8")
        probe.unlink()
        return preferred
    except OSError:
        alt = Path.home() / "TestGeneratori" / fallback_name
        alt.mkdir(parents=True, exist_ok=True)
        return alt


def data_dir() -> Path:
    """SQLite bazasi papkasi (TESTGEN_DATA_DIR muhit o'zgaruvchisi bilan almashtirish mumkin)."""
    env = os.environ.get("TESTGEN_DATA_DIR")
    if env:
        p = Path(env)
        p.mkdir(parents=True, exist_ok=True)
        return p
    return _writable_dir(app_dir() / "data", "data")


def logs_dir() -> Path:
    """Log fayllari papkasi."""
    env = os.environ.get("TESTGEN_LOGS_DIR")
    if env:
        p = Path(env)
        p.mkdir(parents=True, exist_ok=True)
        return p
    return _writable_dir(app_dir() / "logs", "logs")


def default_output_dir() -> Path:
    """Chiqish fayllari uchun standart papka."""
    docs = Path.home() / "Documents"
    base = docs if docs.is_dir() else Path.home()
    return base / "TestGeneratori"


def unique_path(folder: Path, stem: str, suffix: str) -> Path:
    """Mavjud faylni ustiga yozmaslik uchun noyob fayl yo'lini qaytaradi (_2, _3 ...)."""
    folder.mkdir(parents=True, exist_ok=True)
    cand = folder / f"{stem}{suffix}"
    n = 2
    while cand.exists():
        cand = folder / f"{stem}_{n}{suffix}"
        n += 1
    return cand
