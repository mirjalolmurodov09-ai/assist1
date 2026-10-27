"""GUI duti: ekran (DISPLAY yoki xvfb) bo'lmasa o'tkazib yuboriladi."""
import time

import pytest

tk = pytest.importorskip("tkinter")


@pytest.fixture
def app(tmp_path, monkeypatch):
    try:
        root = tk.Tk()
        root.destroy()
    except tk.TclError:
        pytest.skip("ekran yo'q")
    from tkinter import messagebox
    box = []
    for n in ("showinfo", "showwarning", "showerror"):
        monkeypatch.setattr(messagebox, n, lambda *a, _n=n, **k: box.append((_n, a[-1] if a else "")))
    monkeypatch.setattr(messagebox, "askyesno", lambda *a, **k: True)
    monkeypatch.setattr(messagebox, "askokcancel", lambda *a, **k: True)
    import gui
    from storage import Storage
    a = gui.App(Storage(tmp_path / "t.db"))
    a.box = box
    yield a
    a.destroy()


def pump(app, cond, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        app.update()
        if cond():
            return True
        time.sleep(0.02)
    return False


def test_generate_and_check_flow(app, tmp_path):
    from pathlib import Path
    app.v_src.set(str(Path(__file__).resolve().parent.parent / "examples" / "sample.xlsx"))
    app.v_n.set("4")
    app.v_q.set("10")
    app.v_out.set(str(tmp_path / "out"))
    app.generate()
    assert pump(app, lambda: app._last_out is not None)
    assert app._last_out.pdf_path.exists()
    sess = app._cur_session()
    assert sess is not None and sess.n_students == 4

    tid = app.storage.all_test_ids().pop()
    app.v_tid.set(tid.lower())
    assert app.find_test()
    keys = app._stored.correct_letters
    app.v_text.set("".join(keys))
    res = app.check()
    assert res.correct == 10 and res.grade == 5 and "[saqlandi]" in app.lbl_res.cget("text")
    assert len(app.tv_res.get_children()) == 1

    app.v_tid.set("T-QQQQQQ")
    assert not app.find_test() and app.box[-1][0] == "showerror"
    app.v_text.set("ABZ")
    app.v_tid.set(tid)
    assert app.check() is None


def test_input_validation_shows_messages(app):
    import gui
    for bad in ("", "abc", "-5", "0", "2000", "3.5"):
        with pytest.raises(ValueError):
            gui.parse_int(bad, "O'quvchilar soni", 1, 1000)
    app.v_src.set("")
    app.generate()
    assert app.box and app.box[-1][0] == "showwarning"
    app.v_src.set("/yoq/fayl.docx")
    app.generate()
    assert pump(app, lambda: not app._busy)
    assert app.box[-1][0] == "showerror" and "topilmadi" in app.box[-1][1]
