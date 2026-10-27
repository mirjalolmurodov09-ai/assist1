"""Barcha o'quvchilar testlarini bitta chop etishga tayyor A4 PDF ga yozish.

Har bir test yangi sahifadan boshlanadi. Har bir betda TEST ID ko'rinadi, pastda
«Test ID, bet X / Y» kolontituli bor. Hujjat test-ma-test oqim bilan yoziladi
(xotira 1000 ta o'quvchida ham o'smaydi). To'g'ri javob («+») PDF ga umuman yozilmaydi.
"""
from __future__ import annotations

import math
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional
from xml.sax.saxutils import escape

from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.pdfmetrics import stringWidth
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas
from reportlab.platypus import Paragraph

from models import LETTERS, GeneratedTest
from paths import resource_path

FONT = "TGSans"
FONT_B = "TGSans-Bold"

PAGE_W, PAGE_H = A4
MARGIN = 18 * mm
CONTENT_W = PAGE_W - 2 * MARGIN
FOOTER_Y = 10 * mm
BOTTOM = MARGIN + 2 * mm
Q_INDENT = 24.0
FS = 11.0
LEADING = 14.5
GAP = 7.0                 # savollar orasidagi bo'shliq
BUBBLE_ROW_H = 17.5
BUBBLE_COL_W = 118.0
BUBBLE_MAX_PER_CHUNK = 100

ProgressCb = Callable[[int, int], None]

_fonts_ready = False
_glyphs: set[int] = set()


@dataclass
class PdfMeta:
    """Test sarlavhasi ma'lumotlari."""

    title: str = "TEST"
    subtitle: str = ""     # maktab, sinf va h.k.
    date: str = ""


def register_fonts() -> None:
    """DejaVu Sans shriftlarini bir marta ro'yxatdan o'tkazadi."""
    global _fonts_ready
    if _fonts_ready:
        return
    reg = resource_path("fonts/DejaVuSans.ttf")
    bold = resource_path("fonts/DejaVuSans-Bold.ttf")
    for f in (reg, bold):
        if not f.is_file():
            raise FileNotFoundError(f"Shrift fayli topilmadi: {f}. fonts/ papkasini tekshiring.")
    pdfmetrics.registerFont(TTFont(FONT, str(reg)))
    pdfmetrics.registerFont(TTFont(FONT_B, str(bold)))
    pdfmetrics.registerFontFamily(FONT, normal=FONT, bold=FONT_B, italic=FONT, boldItalic=FONT_B)
    _glyphs.update(pdfmetrics.getFont(FONT).face.charToGlyph.keys())
    _fonts_ready = True


def sanitize(text: str) -> str:
    """Shriftda yo'q belgilarni «?» ga almashtiradi (qora kvadrat chiqmasligi uchun)."""
    out = []
    for ch in text:
        if ch in "\n\t":
            out.append(ch)
        elif ord(ch) < 32 or ord(ch) in (0x7F, 0x200B, 0x200C, 0x200D, 0xFEFF, 0x00AD):
            continue
        elif ch == " ":
            out.append(" ")
        elif ord(ch) in _glyphs:
            out.append(ch)
        else:
            out.append("?")
    return "".join(out)


def _markup(text: str) -> str:
    """Oddiy matnni Paragraph uchun xavfsiz belgilashga aylantiradi."""
    return escape(sanitize(text)).replace("\t", " ").replace("\n", "<br/>")


def _style(name: str, **kw) -> ParagraphStyle:
    base = dict(fontName=FONT, fontSize=FS, leading=LEADING, alignment=TA_LEFT,
                splitLongWords=1, spaceBefore=0, spaceAfter=0)
    base.update(kw)
    return ParagraphStyle(name, **base)


class _Row:
    """Sahifaga qo'yiladigan bir qator: (paragraf, x) juftliklari."""

    def __init__(self, cells: list[tuple[Paragraph, float]], height: float) -> None:
        self.cells = cells
        self.height = height


# --------------------------------------------------------------------------
# Maket (layout)
# --------------------------------------------------------------------------
class _TestLayout:
    """Bitta testni sahifalarga joylaydi (chizishdan oldin sahifalar sonini bilish uchun)."""

    def __init__(self, test: GeneratedTest, meta: PdfMeta) -> None:
        self.test = test
        self.meta = meta
        self.pages: list[list[Callable[[canvas.Canvas], None]]] = []
        self.y = 0.0
        self.top_offsets: list[float] = []
        self._styles()
        self._first_header_h = self._measure_first_header()
        self._new_page(first=True)
        self._layout_questions()
        self._layout_answer_sheet()

    # ---- uslublar
    def _styles(self) -> None:
        self.s_q = _style("q", leftIndent=Q_INDENT, bulletIndent=0, bulletFontName=FONT_B,
                          bulletFontSize=FS)
        self.s_opt_list = _style("ol", leftIndent=Q_INDENT + 16, bulletIndent=Q_INDENT,
                                 bulletFontName=FONT_B, bulletFontSize=FS)
        self.s_plain = _style("pl")

    # ---- sarlavha
    def _title_para(self, width: float) -> Paragraph:
        st = _style("title", fontName=FONT_B, fontSize=15, leading=19)
        return Paragraph(_markup(self.meta.title or "TEST"), st)

    def _measure_first_header(self) -> float:
        w = CONTENT_W - 135
        h = 0.0
        tp = self._title_para(w)
        _, th = tp.wrap(w, 10_000)
        h += th
        if self.meta.subtitle:
            sp = Paragraph(_markup(self.meta.subtitle), _style("sub", fontSize=10.5, leading=13.5))
            _, sh = sp.wrap(w, 10_000)
            h += sh + 2
        h = max(h, 52)               # ID qutisi balandligi
        h += 8 + 18 + 8 + 12         # F.I.Sh. qatori + chiziq + bo'shliq
        return h

    # ---- sahifa boshqaruvi
    def _new_page(self, first: bool = False) -> None:
        self.pages.append([])
        hdr = self._first_header_h if first else 30.0
        self.y = PAGE_H - MARGIN - hdr

    def _room(self) -> float:
        return self.y - BOTTOM

    def _page_capacity(self) -> float:
        return PAGE_H - MARGIN - 30.0 - BOTTOM

    def _place_rows(self, rows: list[_Row], gap_before: float) -> None:
        """Qatorlar blokini joylaydi: sig'masa yangi betdan boshlaydi (bo'lak-bo'lak bo'lish — oxirgi chora)."""
        total = sum(r.height for r in rows)
        fresh = not self.pages[-1]
        gb = 0.0 if fresh else gap_before
        if total + gb > self._room() and total <= self._page_capacity():
            self._new_page()
            gb = 0.0
        if total <= self._page_capacity():
            self.y -= gb
            for r in rows:
                self._add_row(r)
            return
        # juda baland blok: qator-qator
        self.y -= gb
        for r in rows:
            if r.height > self._room():
                self._new_page()
            self._add_row(r)

    def _add_row(self, row: _Row) -> None:
        y_top = self.y
        ops = self.pages[-1]
        for para, x in row.cells:
            ops.append(lambda c, p=para, x=x, yt=y_top: p.drawOn(c, x, yt - p.height))
        self.y -= row.height

    # ---- savollar
    def _layout_questions(self) -> None:
        for n, item in enumerate(self.test.items, start=1):
            rows: list[_Row] = []
            qp = Paragraph(_markup(item.text), self.s_q, bulletText=f"{n}.")
            _, qh = qp.wrap(CONTENT_W - 0, 10_000)
            rows.append(_Row([(qp, MARGIN)], qh + 2))
            rows.extend(self._option_rows(item.options))
            self._place_rows(rows, GAP)

    def _option_rows(self, options: tuple[str, ...]) -> list[_Row]:
        x0 = MARGIN + Q_INDENT
        avail = CONTENT_W - Q_INDENT
        texts = [sanitize(o) for o in options]
        multiline = any("\n" in t for t in texts)
        widths = [stringWidth(f"{LETTERS[i]}) ", FONT_B, FS) + stringWidth(t, FONT, FS) + 2
                  for i, t in enumerate(texts)]
        s_cell = _style("cell")

        def cell(i: int, w: float) -> Paragraph:
            p = Paragraph(f"<b>{LETTERS[i]})</b> {_markup(options[i])}", s_cell)
            p.wrap(w, 10_000)
            return p

        if not multiline:
            pad = 14.0
            if sum(widths) + pad * 3 <= avail:                 # bitta qatorda
                cells, x = [], x0
                for i, w in enumerate(widths):
                    cells.append((cell(i, w + 6), x))
                    x += w + pad
                return [_Row(cells, LEADING + 1)]
            half = (avail - pad) / 2
            if max(widths) <= half:                            # 2x2 jadval
                rows = []
                for r in (0, 2):
                    rows.append(_Row([(cell(r, half), x0), (cell(r + 1, half), x0 + half + pad)], LEADING + 1))
                return rows
        rows = []                                              # ro'yxat (hammasi pastma-past)
        for i in range(len(options)):
            p = Paragraph(_markup(options[i]), self.s_opt_list, bulletText=f"{LETTERS[i]})")
            _, h = p.wrap(CONTENT_W, 10_000)
            rows.append(_Row([(p, MARGIN)], h + 1))
        return rows

    # ---- javoblar varaqasi
    def _layout_answer_sheet(self) -> None:
        n = len(self.test.items)
        title = Paragraph(
            "<b>JAVOBLAR VARAQASI</b> &nbsp;&nbsp;<font size=9>(to'g'ri variant doirasini to'liq to'ldiring)</font>",
            self.s_plain)
        for start in range(0, n, BUBBLE_MAX_PER_CHUNK):
            count = min(BUBBLE_MAX_PER_CHUNK, n - start)
            cols = min(4, math.ceil(count / 10))
            rows_n = math.ceil(count / cols)
            height = 22 + rows_n * BUBBLE_ROW_H + 4
            if height > self._room():
                self._new_page()
            elif start == 0:
                self.y -= 12
            y_top = self.y
            title_para = title
            title_para.wrap(CONTENT_W, 100)
            ops = self.pages[-1]
            ops.append(lambda c, p=title_para, yt=y_top: p.drawOn(c, MARGIN, yt - 14))

            def draw(c: canvas.Canvas, yt: float = y_top, start: int = start, count: int = count,
                     rows_n: int = rows_n) -> None:
                c.setLineWidth(0.7)
                for k in range(count):
                    col, row = divmod(k, rows_n)
                    x = MARGIN + col * BUBBLE_COL_W
                    cy = yt - 22 - row * BUBBLE_ROW_H - BUBBLE_ROW_H / 2 - 1
                    c.setFont(FONT_B, 9)
                    c.drawRightString(x + 20, cy - 3, f"{start + k + 1}")
                    for j, L in enumerate(LETTERS):
                        cx = x + 33 + j * 24
                        c.circle(cx, cy, 6.3, stroke=1, fill=0)
                        c.setFont(FONT, 7.5)
                        c.drawCentredString(cx, cy - 2.6, L)

            ops.append(draw)
            self.y -= height

    # ---- chizish
    def draw(self, c: canvas.Canvas) -> None:
        total = len(self.pages)
        for pi, ops in enumerate(self.pages):
            if pi == 0:
                self._draw_first_header(c)
            else:
                self._draw_cont_header(c)
            for op in ops:
                op(c)
            self._draw_footer(c, pi + 1, total)
            c.showPage()

    def _draw_id_box(self, c: canvas.Canvas, top: float, big: bool) -> None:
        tid = sanitize(self.test.test_id)
        if big:
            w, h = 125.0, 52.0
            x = PAGE_W - MARGIN - w
            c.setLineWidth(1.4)
            c.roundRect(x, top - h, w, h, 5)
            c.setFont(FONT, 8)
            c.drawCentredString(x + w / 2, top - 12, "TEST ID")
            c.setFont(FONT_B, 18)
            c.drawCentredString(x + w / 2, top - 33, tid)
            c.setFont(FONT, 8)
            c.drawCentredString(x + w / 2, top - 46, f"№ {self.test.number}")
        else:
            c.setFont(FONT_B, 12)
            c.drawRightString(PAGE_W - MARGIN, top - 11, f"TEST ID: {tid}")

    def _draw_first_header(self, c: canvas.Canvas) -> None:
        top = PAGE_H - MARGIN
        self._draw_id_box(c, top, big=True)
        w = CONTENT_W - 135
        y = top
        tp = self._title_para(w)
        _, th = tp.wrap(w, 10_000)
        tp.drawOn(c, MARGIN, y - th)
        y -= th
        if self.meta.subtitle:
            sp = Paragraph(_markup(self.meta.subtitle), _style("sub", fontSize=10.5, leading=13.5))
            _, sh = sp.wrap(w, 10_000)
            sp.drawOn(c, MARGIN, y - 2 - sh)
            y -= sh + 2
        y = min(y, top - 52)
        y -= 8 + 14
        c.setFont(FONT, 11)
        c.drawString(MARGIN, y, "F.I.Sh.:")
        c.setLineWidth(0.6)
        c.line(MARGIN + 46, y - 2, MARGIN + 270, y - 2)
        c.drawString(MARGIN + 284, y, "Sinf:")
        c.line(MARGIN + 314, y - 2, MARGIN + 370, y - 2)
        if self.meta.date:
            c.drawRightString(PAGE_W - MARGIN, y, f"Sana: {sanitize(self.meta.date)}")
        y -= 10
        c.setLineWidth(1.0)
        c.line(MARGIN, y, PAGE_W - MARGIN, y)

    def _draw_cont_header(self, c: canvas.Canvas) -> None:
        top = PAGE_H - MARGIN
        self._draw_id_box(c, top, big=False)
        c.setFont(FONT_B, 10)
        t = sanitize(self.meta.title or "TEST")
        while t and stringWidth(t, FONT_B, 10) > CONTENT_W - 170:
            t = t[:-2]
        c.drawString(MARGIN, top - 11, t)
        c.setLineWidth(0.8)
        c.line(MARGIN, top - 20, PAGE_W - MARGIN, top - 20)

    def _draw_footer(self, c: canvas.Canvas, page: int, total: int) -> None:
        c.setLineWidth(0.5)
        c.line(MARGIN, FOOTER_Y + 11, PAGE_W - MARGIN, FOOTER_Y + 11)
        c.setFont(FONT, 8.5)
        c.drawCentredString(PAGE_W / 2, FOOTER_Y, f"{sanitize(self.test.test_id)}, bet {page} / {total}")


# --------------------------------------------------------------------------
# Ochiq funksiya
# --------------------------------------------------------------------------
def build_pdf(
    tests: list[GeneratedTest],
    path: str | Path,
    meta: Optional[PdfMeta] = None,
    progress: Optional[ProgressCb] = None,
) -> int:
    """Barcha testlarni bitta PDF ga yozadi. Sahifalar umumiy sonini qaytaradi.

    Fayl avval `.part` nomi bilan yoziladi, tugagach asl nomga o'tkaziladi —
    xato bo'lsa yarim fayl qolmaydi.
    """
    register_fonts()
    meta = meta or PdfMeta()
    path = Path(path)
    tmp = path.with_name(path.name + ".part")
    pages_total = 0
    try:
        c = canvas.Canvas(str(tmp), pagesize=A4, pageCompression=1)
        c.setTitle(sanitize(meta.title or "Test"))
        c.setAuthor("Test generatori")
        for i, t in enumerate(tests, start=1):
            layout = _TestLayout(t, meta)
            layout.draw(c)
            pages_total += len(layout.pages)
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
    return pages_total
