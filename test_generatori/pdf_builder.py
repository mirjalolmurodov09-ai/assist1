"""Barcha o'quvchilar testlarini bitta chop etishga tayyor A4 PDF ga yozish.

Har bir test yangi sahifadan boshlanadi. Birinchi betda katta TEST ID qutisi, keyingi betlarda
joy olmaydigan mayda ID (chetda) va pastda «Test ID, bet X / Y» kolontituli bor. Hujjat test-ma-test oqim bilan yoziladi
(xotira 1000 ta o'quvchida ham o'smaydi). To'g'ri javob («+») PDF ga umuman yozilmaydi.
"""
from __future__ import annotations

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
# Juft sahifa uchun masshtablar: avval siqib (kamroq bet), bo'lmasa kengaytirib (ko'proq bet) ko'riladi
SHRINK_STEPS = (0.95, 0.9, 0.85, 0.8)
EXPAND_STEPS = (1.05, 1.1, 1.15, 1.2, 1.25)

ProgressCb = Callable[[int, int], None]

_fonts_ready = False
_glyphs: set[int] = set()


@dataclass
class PdfMeta:
    """Test sarlavhasi ma'lumotlari."""

    title: str = "TEST"
    subtitle: str = ""     # maktab, sinf va h.k. (test sarlavhasi ostida)
    date: str = ""         # faqat o'quvchi ma'lumotlari varag'ida ishlatiladi


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

    def __init__(self, test: GeneratedTest, meta: PdfMeta, scale: float = 1.0, pad_blank: bool = False) -> None:
        self.test = test
        self.meta = meta
        self.scale = scale
        self.fs = FS * scale                    # shrift o'lchami
        self.leading = LEADING * scale
        self.gap = GAP * scale
        self.q_indent = Q_INDENT * max(scale, 0.85)
        self.pages: list[list[Callable[[canvas.Canvas], None]]] = []
        self.y = 0.0
        self.top_offsets: list[float] = []
        self._styles()
        self._first_header_h = self._measure_first_header()
        self._new_page(first=True)
        self._layout_questions()
        if pad_blank and len(self.pages) % 2 == 1:
            self._add_blank_page()

    def _add_blank_page(self) -> None:
        """Juft sahifa uchun oxiriga bo'sh bet qo'shadi (ikki tomonlama chopda keyingi test aralashmasin)."""
        self.pages.append([lambda c: (c.setFont(FONT, 9),
                                      c.drawCentredString(PAGE_W / 2, PAGE_H / 2,
                                                          "Ikki tomonlama chop etish uchun bo'sh qoldirilgan bet"))])

    # ---- uslublar
    def _styles(self) -> None:
        kw = dict(fontSize=self.fs, leading=self.leading)
        self.s_q = _style("q", leftIndent=self.q_indent, bulletIndent=0, bulletFontName=FONT_B,
                          bulletFontSize=self.fs, **kw)
        self.s_opt_list = _style("ol", leftIndent=self.q_indent + 16 * self.scale, bulletIndent=self.q_indent,
                                 bulletFontName=FONT_B, bulletFontSize=self.fs, **kw)

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
        return max(h, 52) + 14       # ID qutisi balandligi + chiziq va bo'shliq

    # ---- sahifa boshqaruvi
    def _new_page(self, first: bool = False) -> None:
        self.pages.append([])
        hdr = self._first_header_h if first else 0.0   # keyingi betlarda sarlavha yo'q
        self.y = PAGE_H - MARGIN - hdr

    def _room(self) -> float:
        return self.y - BOTTOM

    def _page_capacity(self) -> float:
        return PAGE_H - MARGIN - BOTTOM

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
            self._place_rows(rows, self.gap)

    def _option_rows(self, options: tuple[str, ...]) -> list[_Row]:
        x0 = MARGIN + self.q_indent
        avail = CONTENT_W - self.q_indent
        fs, lead = self.fs, self.leading
        texts = [sanitize(o) for o in options]
        multiline = any("\n" in t for t in texts)
        widths = [stringWidth(f"{LETTERS[i]}) ", FONT_B, fs) + stringWidth(t, FONT, fs) + 2
                  for i, t in enumerate(texts)]
        s_cell = _style("cell", fontSize=fs, leading=lead)

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
                return [_Row(cells, lead + 1)]
            half = (avail - pad) / 2
            if max(widths) <= half:                            # 2x2 jadval
                rows = []
                for r in (0, 2):
                    rows.append(_Row([(cell(r, half), x0), (cell(r + 1, half), x0 + half + pad)], lead + 1))
                return rows
        rows = []                                              # ro'yxat (hammasi pastma-past)
        for i in range(len(options)):
            p = Paragraph(_markup(options[i]), self.s_opt_list, bulletText=f"{LETTERS[i]})")
            _, h = p.wrap(CONTENT_W, 10_000)
            rows.append(_Row([(p, MARGIN)], h + 1))
        return rows

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
        rule_y = top - (self._first_header_h - 14) - 6
        c.setLineWidth(1.0)
        c.line(MARGIN, rule_y, PAGE_W - MARGIN, rule_y)

    def _draw_cont_header(self, c: canvas.Canvas) -> None:
        """Keyingi betlar: ID yuqori chetdagi bo'sh joyda mayda yoziladi (kontentdan joy olmaydi)."""
        c.setFont(FONT, 8)
        c.drawRightString(PAGE_W - MARGIN, PAGE_H - MARGIN + 9, sanitize(self.test.test_id))

    def _draw_footer(self, c: canvas.Canvas, page: int, total: int) -> None:
        c.setLineWidth(0.5)
        c.line(MARGIN, FOOTER_Y + 11, PAGE_W - MARGIN, FOOTER_Y + 11)
        c.setFont(FONT, 8.5)
        c.drawCentredString(PAGE_W / 2, FOOTER_Y, f"{sanitize(self.test.test_id)}, bet {page} / {total}")


def _layout_for(test: GeneratedTest, meta: PdfMeta, even: bool) -> _TestLayout:
    """Testni joylaydi. `even=True` bo'lsa sahifalar soni juft bo'lishga harakat qilinadi:
    1) shriftni biroz kichraytirib bitta betga kamaytirish (siqish),
    2) bo'lmasa kattalashtirib bitta betga ko'paytirish (yoyish),
    3) bo'lmasa oxiriga bo'sh bet qo'shish."""
    base = _TestLayout(test, meta)
    n = len(base.pages)
    if not even or n % 2 == 0:
        return base
    if n > 1:
        for sc in SHRINK_STEPS:
            cand = _TestLayout(test, meta, scale=sc)
            p = len(cand.pages)
            if p % 2 == 0 and p < n:
                return cand
    best: Optional[_TestLayout] = None
    for sc in EXPAND_STEPS:
        cand = _TestLayout(test, meta, scale=sc)
        p = len(cand.pages)
        if p == n + 1:
            best = cand                      # eng kattasini saqlaymiz
        elif p > n + 1:
            break
    return best or _TestLayout(test, meta, pad_blank=True)


# --------------------------------------------------------------------------
# Ochiq funksiya
# --------------------------------------------------------------------------
def build_pdf(
    tests: list[GeneratedTest],
    path: str | Path,
    meta: Optional[PdfMeta] = None,
    progress: Optional[ProgressCb] = None,
    even_pages: bool = False,
) -> int:
    """Barcha testlarni bitta PDF ga yozadi. Sahifalar umumiy sonini qaytaradi.

    `even_pages=True` — har bir o'quvchining testi juft sahifa bo'ladi (ikki tomonlama chop uchun).

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
            layout = _layout_for(t, meta, even_pages)
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
