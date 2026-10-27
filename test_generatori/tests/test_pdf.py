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
    assert "Fan testi" in texts[0] and "Maktab 5" in texts[0]
    for tx in texts:                      # F.I.Sh./Sinf/Sana qatori va doirachali varaqa yo'q
        for banned in ("F.I.Sh", "Sinf", "Sana", "JAVOBLAR"):
            assert banned not in tx


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


def test_big_id_only_on_first_page_and_continuation_has_no_header(tmp_path):
    res = generate_tests(make_bank(120), 2, 90, seed=2)
    out = tmp_path / "h.pdf"
    build_pdf(res.tests, out, PdfMeta("Uzun test", "Maktab 7"))
    reader, texts = text_of(out)
    seen_first = set()
    for tx, page in zip(texts, reader.pages):
        tid = next(t.test_id for t in res.tests if t.test_id in tx)
        if tid not in seen_first:
            seen_first.add(tid)
            assert "TEST ID" in tx and "Uzun test" in tx
        else:
            assert "TEST ID" not in tx and "Uzun test" not in tx
            assert tx.count(tid) == 2          # chetdagi mayda ID + pastki kolontitul
            assert tx.lstrip().startswith(tid)  # sarlavha o'rniga faqat mayda ID, keyin savollar


# ---------------------------------------------------------------- Juft sahifa
def _pages_per_test(path, tests):
    """Har bir test necha betga tushganini hisoblaydi (footer «ID, bet X / Y» orqali)."""
    _, texts = text_of(path)
    out = {}
    for tx in texts:
        tid = next(t.test_id for t in tests if t.test_id in tx)
        m = re.search(r"bet (\d+) / (\d+)", tx)
        out.setdefault(tid, []).append((int(m.group(1)), int(m.group(2))))
    return out


@pytest.mark.parametrize("q", [1, 3, 8, 16, 17, 20, 34, 35, 40, 51, 68, 69, 90, 119])
def test_even_pages_for_all_sizes(tmp_path, q):
    bank = make_bank(120)
    res = generate_tests(bank, 3, q, seed=q)
    out = tmp_path / "e.pdf"
    pages = build_pdf(res.tests, out, PdfMeta("Juft"), even_pages=True)
    per = _pages_per_test(out, res.tests)
    assert pages % 2 == 0
    for tid, lst in per.items():
        total = lst[0][1]
        assert total % 2 == 0 and len(lst) == total
        assert [n for n, _ in lst] == list(range(1, total + 1))
    _, texts = text_of(out)
    flat = re.sub(r"\s+", " ", "\n".join(texts))
    for t in res.tests:                                  # hech qaysi savol/variant yo'qolmagan
        for it in t.items:
            assert it.text in flat
            for o in it.options:
                assert o in flat


def test_default_does_not_force_even(tmp_path):
    res = generate_tests(make_bank(60), 1, 40, seed=1)
    a = tmp_path / "a.pdf"
    b = tmp_path / "b.pdf"
    odd = build_pdf(res.tests, a)
    even = build_pdf(res.tests, b, even_pages=True)
    assert odd % 2 == 1 and even % 2 == 0


def test_short_single_page_gets_blank_back_page(tmp_path):
    res = generate_tests(make_bank(10), 2, 5, seed=1)
    out = tmp_path / "s.pdf"
    assert build_pdf(res.tests, out, even_pages=True) == 4
    _, texts = text_of(out)
    assert "bo'sh qoldirilgan" in texts[1] and "bo'sh qoldirilgan" in texts[3]


def test_shrink_and_expand_strategies():
    from pdf_builder import _TestLayout, _layout_for
    bank = make_bank(150)
    meta = PdfMeta("X")

    def lay(q):
        t = generate_tests(bank, 1, q, seed=1).tests[0]
        return len(_TestLayout(t, meta).pages), _layout_for(t, meta, True)

    n, l = lay(36)                      # 3 bet -> siqilib 2 bet
    assert n == 3 and len(l.pages) == 2 and l.scale < 1
    n, l = lay(45)                      # 3 bet -> kengaytirilib 4 bet
    assert n == 3 and len(l.pages) == 4 and l.scale > 1
    n, l = lay(72)                      # 5 bet -> siqilib 4 bet
    assert n == 5 and len(l.pages) == 4 and l.scale < 1
    n, l = lay(5)                       # 1 qisqa bet -> yoyib bo'lmaydi: bo'sh bet
    assert n == 1 and len(l.pages) == 2 and l.scale == 1
    n, l = lay(20)                      # allaqachon juft: o'zgarmaydi
    assert n == 2 and len(l.pages) == 2 and l.scale == 1


def test_even_mode_performance_reasonable(tmp_path):
    import time
    res = generate_tests(make_bank(100), 60, 20, seed=1)
    t0 = time.time()
    build_pdf(res.tests, tmp_path / "p.pdf", even_pages=True)
    assert time.time() - t0 < 60
