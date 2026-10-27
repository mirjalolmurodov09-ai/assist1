import re

import pytest
from pypdf import PdfReader

from generator import generate_tests
from helpers import make_bank
from models import Question
from pdf_builder import PdfMeta, build_pdf, sanitize, register_fonts


def text_of(path):
    r = PdfReader(str(path))
    return r, [p.extract_text() or "" for p in r.pages]


def test_pdf_created_with_ids_and_no_plus(tmp_path):
    bank = make_bank(50)
    res = generate_tests(bank, 12, 20, seed=1)
    out = tmp_path / "t.pdf"
    pages = build_pdf(res.tests, out, PdfMeta("Fan testi", "Maktab 5", "01.01.2026"))
    reader, texts = text_of(out)
    assert out.exists() and len(reader.pages) == pages >= 12
    assert not (tmp_path / "t.pdf.part").exists()
    full = "\n".join(texts)
    for t in res.tests:
        assert t.test_id in full
    assert "+" not in full
    assert "Fan testi" in texts[0] and "F.I.Sh." in texts[0]


def test_every_page_shows_id_and_page_numbers(tmp_path):
    bank = make_bank(120)
    res = generate_tests(bank, 3, 90, seed=2)            # uzun testlar: ko'p betli
    out = tmp_path / "long.pdf"
    build_pdf(res.tests, out, PdfMeta("Uzun"))
    reader, texts = text_of(out)
    ids = [t.test_id for t in res.tests]
    seen = {i: [] for i in ids}
    for tx in texts:
        found = [i for i in ids if i in tx]
        assert len(found) == 1, "har bir betda aynan bitta test ID bo'lishi kerak"
        seen[found[0]].append(tx)
    for i, pages in seen.items():
        assert len(pages) >= 2
        for n, tx in enumerate(pages, start=1):
            assert f"bet {n} / {len(pages)}" in tx


def test_each_test_starts_on_new_page(tmp_path):
    res = generate_tests(make_bank(10), 5, 3, seed=1)
    out = tmp_path / "s.pdf"
    build_pdf(res.tests, out)
    _, texts = text_of(out)
    assert len(texts) == 5
    for t, tx in zip(res.tests, texts):
        assert t.test_id in tx and "TEST ID" in tx


def test_all_questions_and_options_present(tmp_path):
    bank = make_bank(8)
    res = generate_tests(bank, 2, 8, seed=3)
    out = tmp_path / "c.pdf"
    build_pdf(res.tests, out)
    _, texts = text_of(out)
    flat = re.sub(r"\s+", " ", "\n".join(texts))
    for t in res.tests:
        for it in t.items:
            assert it.text in flat
            for o in it.options:
                assert o in flat


def test_uzbek_cyrillic_glyphs_and_special_chars(tmp_path):
    register_fonts()
    assert sanitize("Oʻzbekiston Gʻafur ўғқҳ") == "Oʻzbekiston Gʻafur ўғқҳ"
    assert sanitize("a​b") == "ab"
    assert sanitize("x\u4e2dy") == "x?y"           # shriftda yo'q belgi -> «?»
    q = Question("Ўзбекистон <b>poytaxti</b> & «Oʻzbek» ўғқҳ?",
                 ("Тошкент", "Samarqand & <i>x</i>", "Buxoro", "Xiva"), 0, 1)
    res = generate_tests([q], 1, 1, seed=1, shuffle_options=False)
    out = tmp_path / "u.pdf"
    build_pdf(res.tests, out)
    _, texts = text_of(out)
    t = texts[0]
    assert "Ўзбекистон" in t and "<b>poytaxti</b>" in t and "ўғқҳ" in t and "Тошкент" in t
    assert "Samarqand & <i>x</i>" in t


def test_very_long_texts_wrap_inside_page(tmp_path):
    long_q = "uzunsoʻz " * 200
    q = Question(long_q, ("a" * 300, "b " * 100, "c", "d"), 0, 1)
    res = generate_tests([q, *make_bank(3)], 2, 4, seed=1)
    out = tmp_path / "w.pdf"
    build_pdf(res.tests, out)
    reader, texts = text_of(out)
    assert len(reader.pages) >= 2
    for p in reader.pages:
        assert float(p.mediabox.width) == pytest.approx(595.27, abs=0.5)


def test_large_batch_is_fast(tmp_path):
    import time
    res = generate_tests(make_bank(100), 300, 20, seed=1)
    t0 = time.time()
    out = tmp_path / "big.pdf"
    build_pdf(res.tests, out)
    assert time.time() - t0 < 60
    assert len(PdfReader(str(out)).pages) >= 300


def test_failed_build_leaves_no_partial_file(tmp_path):
    res = generate_tests(make_bank(5), 2, 3, seed=1)
    out = tmp_path / "x.pdf"

    def boom(done, total):
        raise RuntimeError("to'xtatildi")

    with pytest.raises(RuntimeError):
        build_pdf(res.tests, out, progress=boom)
    assert not out.exists() and not (tmp_path / "x.pdf.part").exists()
