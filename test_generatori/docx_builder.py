"""Barcha o'quvchilar testlarini bitta tahrirlanadigan Word (.docx) faylga yozish.

Har bir test alohida bo'lim (section): sahifalar raqami har bir testda 1 dan boshlanadi, pastda
«Test ID, bet X / Y», keyingi betlarda yuqorida mayda ID. `even_pages=True` bo'lsa har bir test
«toq betdan boshlanadigan» bo'limga aylanadi — ikki tomonlama chop etganda keyingi o'quvchi testi
hech qachon oldingisining orqasiga tushmaydi (Word o'zi bo'sh bet qo'shadi).
To'g'ri javob («+») faylga yozilmaydi.
"""
from __future__ import annotations

import os
import re
from copy import deepcopy
from pathlib import Path
from typing import Callable, Optional

from docx import Document
from docx.enum.section import WD_SECTION
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.section import Section
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Mm, Pt
from reportlab.pdfbase.pdfmetrics import stringWidth

from models import LETTERS, GeneratedTest
from pdf_builder import FONT, PdfMeta, register_fonts

FONT_NAME = "Arial"
BODY_PT = 11
INDENT_PT = 24.0
MARGIN_MM = 18
CONTENT_W_PT = (210 - 2 * MARGIN_MM) / 25.4 * 72
ID_BOX_W_PT = 130.0

ProgressCb = Callable[[int, int], None]
_BAD_XML = re.compile("[\x00-\x08\x0b\x0c\x0e-\x1f￾￿]")
_SECT_AFTER_PGNUM = ("cols", "formProt", "vAlign", "noEndnote", "titlePg", "textDirection", "bidi",
                     "rtlGutter", "docGrid", "printerSettings")


def _clean(text: str) -> str:
    """XML da ruxsat etilmagan belgilarni olib tashlaydi."""
    return _BAD_XML.sub("", text.replace(" ", " "))


_RPR_CACHE: dict[tuple[float, bool], object] = {}


def _rpr(size: float, bold: bool):
    """Faqat odatdan farq qiladigan (qalin / boshqa o'lcham) sozlamali w:rPr. Shrift hujjat uslubida beriladi,
    shuning uchun oddiy matn run lari bo'sh qoladi (hujjat ancha yengil bo'ladi)."""
    if size == BODY_PT and not bold:
        return None
    key = (size, bold)
    proto = _RPR_CACHE.get(key)
    if proto is None:
        proto = OxmlElement("w:rPr")
        if bold:
            proto.append(OxmlElement("w:b"))
            proto.append(OxmlElement("w:bCs"))
        if size != BODY_PT:
            for tag in ("w:sz", "w:szCs"):
                el = OxmlElement(tag)
                el.set(qn("w:val"), str(int(round(size * 2))))
                proto.append(el)
        _RPR_CACHE[key] = proto
    return deepcopy(proto)


def _mk_run(size: float, bold: bool = False):
    r = OxmlElement("w:r")
    rpr = _rpr(size, bold)
    if rpr is not None:
        r.append(rpr)
    return r


def _set_document_font(doc) -> None:
    """Butun hujjat uchun Arial 11 pt (tema shriftlari o'rniga) — docDefaults va Normal uslubida."""
    styles = doc.styles.element
    for rf in styles.iter(qn("w:rFonts")):
        for attr in list(rf.attrib):
            del rf.attrib[attr]
        for attr in ("w:ascii", "w:hAnsi", "w:cs", "w:eastAsia"):
            rf.set(qn(attr), FONT_NAME)
    normal = doc.styles["Normal"]
    normal.font.name = FONT_NAME
    normal.font.size = Pt(BODY_PT)
    rpr = normal.element.get_or_add_rPr()
    rf = rpr.find(qn("w:rFonts"))
    for attr in ("w:ascii", "w:hAnsi", "w:cs", "w:eastAsia"):
        rf.set(qn(attr), FONT_NAME)


def _text_el(text: str):
    t = OxmlElement("w:t")
    t.set(qn("xml:space"), "preserve")
    t.text = text
    return t


def _add_text(p, text: str, size: float = BODY_PT, bold: bool = False) -> None:
    """Paragraf elementiga matn qo'shadi; "\\n" qator uzilishiga, "\\t" tabga aylanadi."""
    for i, line in enumerate(_clean(text).replace("\r", "").split("\n")):
        r = _mk_run(size, bold)
        if i:
            r.append(OxmlElement("w:br"))
        for j, part in enumerate(line.split("\t")):
            if j:
                r.append(OxmlElement("w:tab"))
            if part:
                r.append(_text_el(part))
        p.append(r)


def _add_field(p, instr: str, size: float) -> None:
    """PAGE kabi maydon qo'shadi."""
    def fld(kind: str):
        r = _mk_run(size)
        c = OxmlElement("w:fldChar")
        c.set(qn("w:fldCharType"), kind)
        r.append(c)
        p.append(r)

    fld("begin")
    r = _mk_run(size)
    it = OxmlElement("w:instrText")
    it.set(qn("xml:space"), "preserve")
    it.text = f" {instr} "
    r.append(it)
    p.append(r)
    fld("separate")
    r = _mk_run(size)
    r.append(_text_el("1"))
    p.append(r)
    fld("end")


def _twips(pt: float) -> str:
    return str(int(round(pt * 20)))


def _set_ppr(p, left: float = 0, first: float = 0, before: float = 0, after: float = 0,
             keep_next: bool = False, tabs: tuple[float, ...] = (), align: Optional[str] = None,
             rule: bool = False, tiny: bool = False) -> None:
    """w:pPr ni sxema tartibida (keepNext, keepLines, pBdr, tabs, spacing, ind, jc, rPr) yangidan quradi."""
    old = p.find(qn("w:pPr"))
    sect = None
    if old is not None:
        sect = old.find(qn("w:sectPr"))         # bo'lim uzilishi shu paragraf ichida bo'lishi mumkin — saqlab qolamiz
        p.remove(old)
    ppr = OxmlElement("w:pPr")
    if keep_next:
        ppr.append(OxmlElement("w:keepNext"))
    ppr.append(OxmlElement("w:keepLines"))
    if rule:
        bdr = OxmlElement("w:pBdr")
        b = OxmlElement("w:bottom")
        for k, v in (("val", "single"), ("sz", "8"), ("space", "1"), ("color", "000000")):
            b.set(qn(f"w:{k}"), v)
        bdr.append(b)
        ppr.append(bdr)
    if tabs:
        tb = OxmlElement("w:tabs")
        for pos in tabs:
            t = OxmlElement("w:tab")
            t.set(qn("w:val"), "left")
            t.set(qn("w:pos"), _twips(pos))
            tb.append(t)
        ppr.append(tb)
    sp = OxmlElement("w:spacing")
    sp.set(qn("w:before"), _twips(before))
    sp.set(qn("w:after"), _twips(after))
    sp.set(qn("w:line"), "20" if tiny else "240")
    sp.set(qn("w:lineRule"), "exact" if tiny else "auto")
    ppr.append(sp)
    ind = OxmlElement("w:ind")
    ind.set(qn("w:left"), _twips(left))
    if first < 0:
        ind.set(qn("w:hanging"), _twips(-first))
    elif first > 0:
        ind.set(qn("w:firstLine"), _twips(first))
    ppr.append(ind)
    if align:
        jc = OxmlElement("w:jc")
        jc.set(qn("w:val"), align)
        ppr.append(jc)
    if tiny:
        mark = OxmlElement("w:rPr")
        sz = OxmlElement("w:sz")
        sz.set(qn("w:val"), "2")
        mark.append(sz)
        ppr.append(mark)
    if sect is not None:
        ppr.append(sect)
    p.insert(0, ppr)


def _new_par(doc, **fmt):
    """Hujjat oxiriga (sectPr dan oldin) yangi paragraf qo'shadi — O(1), python-docx kabi butun hujjatni qidirmaydi."""
    p = OxmlElement("w:p")
    _set_ppr(p, **fmt)
    sentinel = doc.element.body[-1]
    assert sentinel.tag == qn("w:sectPr")
    sentinel.addprevious(p)
    return p


def _set_pg_num_start(section) -> None:
    """Sahifa raqamini bo'limda 1 dan boshlaydi (sectPr elementlari tartibini saqlagan holda)."""
    sect = section._sectPr
    for old in sect.findall(qn("w:pgNumType")):
        sect.remove(old)
    el = OxmlElement("w:pgNumType")
    el.set(qn("w:start"), "1")
    for child in sect:
        if child.tag.rsplit("}", 1)[-1] in _SECT_AFTER_PGNUM:
            child.addprevious(el)
            return
    sect.append(el)


def _cell_borders(cell, size: int = 12) -> None:
    tcpr = cell._tc.get_or_add_tcPr()
    borders = OxmlElement("w:tcBorders")
    for side in ("top", "left", "bottom", "right"):
        b = OxmlElement(f"w:{side}")
        b.set(qn("w:val"), "single")
        b.set(qn("w:sz"), str(size))
        b.set(qn("w:color"), "000000")
        borders.append(b)
    tcpr.append(borders)


def _clear(p) -> None:
    """Mavjud paragrafdagi barcha run larni olib tashlaydi."""
    for r in list(p.findall(qn("w:r"))):
        p.remove(r)


def _setup_section(section, test: GeneratedTest, first_section: bool, even_pages: bool) -> None:
    section.page_width, section.page_height = Mm(210), Mm(297)
    section.left_margin = section.right_margin = Mm(MARGIN_MM)
    section.top_margin = section.bottom_margin = Mm(MARGIN_MM)
    section.header_distance = Mm(8)
    section.footer_distance = Mm(8)
    section.different_first_page_header_footer = True
    if not even_pages:        # raqamni qayta boshlash toq-bet qoidasini buzadi, shuning uchun faqat oddiy rejimda
        _set_pg_num_start(section)
    tid = _clean(test.test_id)

    # Birinchi betda kolontitul yo'q (u yerda katta ID qutisi bor); keyingi betlarda mayda ID va pastki yozuv.
    if not first_section:
        section.header.is_linked_to_previous = False
        section.footer.is_linked_to_previous = False
    p = section.header.paragraphs[0]._p
    _clear(p)
    _set_ppr(p, align="right")
    _add_text(p, tid, 8)
    p = section.footer.paragraphs[0]._p
    _clear(p)
    _set_ppr(p, align="center")
    if even_pages:            # sahifa raqami uzluksiz bo'lgani uchun faqat ID yoziladi
        _add_text(p, tid, 8.5)
    else:
        _add_text(p, f"{tid}, bet ", 8.5)
        _add_field(p, "PAGE", 8.5)


def _write_head(doc, test: GeneratedTest, meta: PdfMeta) -> None:
    """Birinchi bet boshi: sarlavha (chapda) va TEST ID qutisi (o'ngda)."""
    table = doc._body.add_table(1, 2, Pt(CONTENT_W_PT))
    table.alignment = WD_TABLE_ALIGNMENT.CENTER
    table.autofit = False
    left_w = CONTENT_W_PT - ID_BOX_W_PT - 6
    lc, rc = table.rows[0].cells
    lc.width, rc.width = Pt(left_w), Pt(ID_BOX_W_PT)
    p = lc.paragraphs[0]._p
    _set_ppr(p)
    _add_text(p, meta.title or "TEST", 15, bold=True)
    if meta.subtitle:
        p2 = lc.add_paragraph()._p
        _set_ppr(p2, before=2)
        _add_text(p2, meta.subtitle, 10.5)
    _cell_borders(rc)
    texts = (("TEST ID", 8, False), (test.test_id, 18, True), (f"№ {test.number}", 8, False))
    for i, (t, size, bold) in enumerate(texts):
        pp = rc.paragraphs[0]._p if i == 0 else rc.add_paragraph()._p
        _set_ppr(pp, before=2 if i == 0 else 0, after=2 if i == 2 else 0, align="center")
        _add_text(pp, t, size, bold)
    _new_par(doc, after=4, rule=True)


def _option_layout(options: tuple[str, ...]) -> str:
    """'row' (bitta qatorda), 'grid' (2x2) yoki 'list' (pastma-past)."""
    texts = [_clean(o) for o in options]
    if any("\n" in t for t in texts):
        return "list"
    avail = CONTENT_W_PT - INDENT_PT
    widest = max(stringWidth(f"{LETTERS[i]}) ", "TGSans-Bold", BODY_PT) + stringWidth(t, FONT, BODY_PT)
                 for i, t in enumerate(texts)) + 10
    if widest <= avail / 4:
        return "row"
    if widest <= avail / 2:
        return "grid"
    return "list"


def _option_par(doc, labelled: list[tuple[str, str]], tabs: list[float], keep_next: bool,
                left: float = INDENT_PT, first: float = 0) -> None:
    p = _new_par(doc, left=left, first=first, keep_next=keep_next, tabs=tuple(tabs))
    last = len(labelled) - 1
    for i, (label, text) in enumerate(labelled):
        r = _mk_run(BODY_PT, bold=True)
        r.append(_text_el(label + " "))
        p.append(r)
        _add_text(p, text + ("\t" if i < last else ""))      # keyingi variantga o'tish tabi shu run ichida


def _write_questions(doc, test: GeneratedTest) -> None:
    avail = CONTENT_W_PT - INDENT_PT
    for n, item in enumerate(test.items, start=1):
        q = _new_par(doc, left=INDENT_PT, first=-INDENT_PT, before=6, keep_next=True, tabs=(INDENT_PT,))
        r = _mk_run(BODY_PT, bold=True)
        r.append(_text_el(f"{n}."))
        r.append(OxmlElement("w:tab"))
        q.append(r)
        _add_text(q, item.text)

        opts = [(f"{LETTERS[i]})", o) for i, o in enumerate(item.options)]
        mode = _option_layout(item.options)
        if mode == "row":
            col = avail / 4
            _option_par(doc, opts, [INDENT_PT + col * k for k in (1, 2, 3)], keep_next=False)
        elif mode == "grid":
            tab = [INDENT_PT + avail / 2]
            _option_par(doc, opts[:2], tab, keep_next=True)
            _option_par(doc, opts[2:], tab, keep_next=False)
        else:
            for i, o in enumerate(opts):
                _option_par(doc, [o], [INDENT_PT + 16], keep_next=i < len(opts) - 1,
                            left=INDENT_PT + 16, first=-16)


def _shrink_break_paragraph(doc) -> None:
    """Bo'lim uzilishi bo'lgan bo'sh paragraf joy olmasligi uchun 1 pt qilinadi."""
    _set_ppr(doc.element.body[-2], tiny=True)      # sentinel sectPr dan oldingi paragraf


def build_docx(
    tests: list[GeneratedTest],
    path: str | Path,
    meta: Optional[PdfMeta] = None,
    progress: Optional[ProgressCb] = None,
    even_pages: bool = False,
) -> None:
    """Barcha testlarni bitta .docx ga yozadi (har bir test yangi sahifadan)."""
    register_fonts()                         # so'z kengligini o'lchash uchun
    meta = meta or PdfMeta()
    path = Path(path)
    tmp = path.with_name(path.name + ".part")
    try:
        doc = Document()
        _set_document_font(doc)
        for i, t in enumerate(tests):
            body = doc.element.body
            if i == 0:
                section = Section(body.get_or_add_sectPr(), doc.part)
            else:                     # doc.add_section() har safar butun hujjatni qidiradi (sekin), shuning uchun to'g'ridan-to'g'ri
                section = Section(body.add_section_break(), doc.part)
                section.start_type = WD_SECTION.ODD_PAGE if even_pages else WD_SECTION.NEW_PAGE
                _shrink_break_paragraph(doc)
            _setup_section(section, t, first_section=(i == 0), even_pages=even_pages)
            _write_head(doc, t, meta)
            _write_questions(doc, t)
            if progress:
                progress(i + 1, len(tests))
        doc.core_properties.title = _clean(meta.title or "Test")
        doc.core_properties.author = "Test generatori"
        doc.save(str(tmp))
        os.replace(tmp, path)
    except PermissionError as e:
        _cleanup(tmp)
        raise PermissionError(f"«{path.name}» fayliga yozib bo'lmadi. Faylni yoping va qayta urinib ko'ring.") from e
    except BaseException:
        _cleanup(tmp)
        raise


def _cleanup(tmp: Path) -> None:
    try:
        tmp.unlink()
    except OSError:
        pass
