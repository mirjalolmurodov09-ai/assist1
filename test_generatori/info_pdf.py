"""O'quvchi ma'lumotlari varaqlari: har bir o'quvchi uchun alohida sahifa
(F.I.Sh., sinf, maktab, Test ID) — hammasi bitta PDF da."""
from __future__ import annotations

import os
from pathlib import Path
from typing import Callable, Optional, Sequence
from xml.sax.saxutils import escape

from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfgen import canvas
from reportlab.platypus import Paragraph

from models import GeneratedTest, Student
from pdf_builder import FONT, FONT_B, MARGIN, PAGE_H, PAGE_W, PdfMeta, register_fonts, sanitize

ProgressCb = Callable[[int, int], None]
LABEL_W = 110.0


def _para(text: str, size: float, bold: bool = False) -> Paragraph:
    st = ParagraphStyle("v", fontName=FONT_B if bold else FONT, fontSize=size, leading=size * 1.25,
                        splitLongWords=1)
    return Paragraph(escape(sanitize(text)).replace("\n", "<br/>"), st)


def _draw_page(c: canvas.Canvas, test: GeneratedTest, student: Optional[Student], meta: PdfMeta,
               total: int) -> None:
    x0, x1 = MARGIN, PAGE_W - MARGIN
    y = PAGE_H - MARGIN - 8
    c.setFont(FONT_B, 17)
    c.drawString(x0, y - 14, "O'QUVCHI MA'LUMOTI")
    c.setFont(FONT, 10)
    c.drawRightString(x1, y - 12, f"{test.number} / {total}")
    y -= 34
    c.setLineWidth(1.2)
    c.line(x0, y, x1, y)
    y -= 30

    st = student or Student()
    rows = [("F.I.Sh.:", st.name), ("Sinf:", st.klass), ("Maktab:", st.school)]
    for label, value in rows:
        c.setFont(FONT, 12)
        c.drawString(x0, y - 14, label)
        if value:
            p = _para(value, 16, bold=True)
            _, h = p.wrap(x1 - x0 - LABEL_W, 500)
            p.drawOn(c, x0 + LABEL_W, y - h + 1)
            y -= max(h, 22) + 10
        else:                                           # bo'sh — qo'lda yoziladi
            c.setLineWidth(0.7)
            c.line(x0 + LABEL_W, y - 16, x1, y - 16)
            y -= 38
        y -= 4

    y -= 16
    bw, bh = 300.0, 92.0
    bx = (PAGE_W - bw) / 2
    c.setLineWidth(2)
    c.roundRect(bx, y - bh, bw, bh, 8)
    c.setFont(FONT, 11)
    c.drawCentredString(PAGE_W / 2, y - 22, "TEST ID")
    c.setFont(FONT_B, 34)
    c.drawCentredString(PAGE_W / 2, y - 62, sanitize(test.test_id))
    c.setFont(FONT, 10)
    c.drawCentredString(PAGE_W / 2, y - 81, f"Tartib raqami: № {test.number}")
    y -= bh + 30

    c.setFont(FONT, 11)
    if meta.title:
        p = _para(f"Fan / test: {meta.title}", 11)
        _, h = p.wrap(x1 - x0, 200)
        p.drawOn(c, x0, y - h)
        y -= h + 6
    if meta.subtitle:
        p = _para(meta.subtitle, 11)
        _, h = p.wrap(x1 - x0, 200)
        p.drawOn(c, x0, y - h)
        y -= h + 6
    if meta.date:
        c.drawString(x0, y - 11, f"Sana: {sanitize(meta.date)}")


def build_info_pdf(
    tests: Sequence[GeneratedTest],
    students: Optional[Sequence[Student]],
    path: str | Path,
    meta: Optional[PdfMeta] = None,
    progress: Optional[ProgressCb] = None,
) -> int:
    """Har bir test uchun bitta sahifa. `students` bo'lmasa (yoki qisqa bo'lsa) maydonlar bo'sh chiziq bo'ladi."""
    register_fonts()
    meta = meta or PdfMeta()
    path = Path(path)
    tmp = path.with_name(path.name + ".part")
    try:
        c = canvas.Canvas(str(tmp), pagesize=A4, pageCompression=1)
        c.setTitle("O'quvchilar ma'lumotlari")
        c.setAuthor("Test generatori")
        for i, t in enumerate(tests, start=1):
            st = students[i - 1] if students and i - 1 < len(students) else None
            _draw_page(c, t, st, meta, len(tests))
            c.showPage()
            if progress:
                progress(i, len(tests))
        c.save()
        os.replace(tmp, path)
    except BaseException:
        try:
            tmp.unlink()
        except OSError:
            pass
        raise
    return len(tests)
