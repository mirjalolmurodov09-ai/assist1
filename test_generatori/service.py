"""Test tuzish jarayonining to'liq zanjiri: generatsiya -> PDF -> kalit -> baza."""
from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional

from excel_io import write_answer_key
from generator import GenerationResult, generate_tests, validate_params
from models import Question, Student
from paths import unique_path
from info_pdf import build_info_pdf
from pdf_builder import PdfMeta, build_pdf
from storage import Storage

log = logging.getLogger("testgen")


@dataclass
class SessionOutput:
    session_id: int
    pdf_path: Path
    key_path: Path
    info_pdf_path: Path
    seed: int
    pages: int
    warnings: list[str] = field(default_factory=list)


def create_session(
    questions: list[Question],
    *,
    n_students: int,
    q_per_test: int,
    out_dir: str | Path,
    storage: Storage,
    title: str = "",
    subtitle: str = "",
    date: str = "",
    seed: Optional[int] = None,
    shuffle_options: bool = True,
    students: Optional[list[Student]] = None,
    source_file: str = "",
    stage: Optional[Callable[[str, int, int], None]] = None,
) -> SessionOutput:
    """Testlarni tuzadi, PDF va javoblar kalitini yozadi, hammasini bazaga saqlaydi.

    `students` berilsa, o'quvchilar soni shu ro'yxat uzunligiga teng bo'ladi (n_students e'tiborga olinmaydi)
    va i-test i-o'quvchiga biriktiriladi. `stage(nom, bajarildi, jami)` — jarayon haqida xabar berish uchun.
    """
    if students:
        n_students = len(students)
    def report(name: str, done: int, total: int) -> None:
        if stage:
            stage(name, done, total)

    validate_params(len(questions), n_students, q_per_test)
    out = Path(out_dir)
    try:
        out.mkdir(parents=True, exist_ok=True)
    except OSError as e:
        raise OSError(f"Chiqish papkasini yaratib bo'lmadi: {out} ({e})") from e

    existing = storage.all_test_ids() if seed is None else ()
    report("Testlar tuzilmoqda", 0, n_students)
    gen: GenerationResult = generate_tests(
        questions, n_students, q_per_test, seed=seed, shuffle_options=shuffle_options,
        existing_ids=existing, progress=lambda d, t: report("Testlar tuzilmoqda", d, t))

    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    pdf_path = unique_path(out, f"testlar_{stamp}", ".pdf")
    key_path = unique_path(out, f"javoblar_kaliti_{stamp}", ".xlsx")
    info_path = unique_path(out, f"oquvchilar_{stamp}", ".pdf")
    meta = PdfMeta(title=title or "TEST", subtitle=subtitle, date=date or datetime.now().strftime("%d.%m.%Y"))

    report("PDF yozilmoqda", 0, n_students)
    try:
        pages = build_pdf(gen.tests, pdf_path, meta, progress=lambda d, t: report("PDF yozilmoqda", d, t))
        report("O'quvchi varaqlari yozilmoqda", 0, n_students)
        build_info_pdf(gen.tests, students, info_path, meta,
                       progress=lambda d, t: report("O'quvchi varaqlari yozilmoqda", d, t))
        report("Javoblar kaliti yozilmoqda", 0, 1)
        write_answer_key(gen.tests, key_path, students)
        report("Baza yangilanmoqda", 0, 1)
        name = f"{title or 'Test'} — {datetime.now().strftime('%Y-%m-%d %H:%M')}"
        sid = storage.create_session(
            name=name, title=title, n_students=n_students, q_per_test=q_per_test, seed=gen.seed,
            source_file=source_file, pdf_path=str(pdf_path), key_path=str(key_path), tests=gen.tests,
            info_pdf_path=str(info_path), students=students)
    except BaseException:
        # yarim qolgan fayllarni tozalaymiz
        for p in (pdf_path, key_path, info_path):
            try:
                p.unlink()
            except OSError:
                pass
        raise
    report("Tayyor", 1, 1)
    log.info("Sessiya %s yaratildi: %d test, PDF=%s", sid, n_students, pdf_path)
    return SessionOutput(sid, pdf_path, key_path, info_path, gen.seed, pages, gen.warnings)
