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


def _make_students(path, n):
    from openpyxl import Workbook
    wb = Workbook()
    ws = wb.active
    ws.append(["F.I.Sh.", "Sinf", "Maktab"])
    for i in range(1, n + 1):
        ws.append([f"O'quvchi {i}", "9-A", "12-maktab"])
    wb.save(path)


def _generate(app, tmp_path, n_students_file=None, label="x"):
    from pathlib import Path
    app.v_src.set(str(Path(__file__).resolve().parent.parent / "examples" / "sample.xlsx"))
    app.v_n.set("2")
    app.v_q.set("5")
    app.v_stu.set(str(n_students_file) if n_students_file else "")
    app.v_out.set(str(tmp_path / "out"))
    app._last_out = None
    app.v_docx.set(True)
    app.generate()
    assert pump(app, lambda: app._last_out is not None)
    return app._last_out


def test_generate_with_student_list_and_autofill(app, tmp_path):
    lst = tmp_path / "students.xlsx"
    _make_students(lst, 6)
    out = _generate(app, tmp_path, lst)
    assert out.info_pdf_path.exists() and out.pdf_path.exists() and out.key_path.exists()
    assert app._cur_session().n_students == 6            # «O'quvchilar soni» emas, ro'yxatdan
    tid = sorted(app.storage.all_test_ids())[0]
    app.v_tid.set(tid)
    assert app.find_test()
    assert app.v_student.get().startswith("O'quvchi ") and "9-A" in app.lbl_found.cget("text")


def test_pick_older_session_open_and_delete(app, tmp_path, monkeypatch):
    import gui
    opened = []
    monkeypatch.setattr(gui, "open_path", lambda p: opened.append(str(p)))
    first = _generate(app, tmp_path)
    second = _generate(app, tmp_path)
    assert len(app.tv_sess.get_children()) == 2

    app.tv_sess.selection_set(str(first.session_id))      # eskisini tanlaymiz (oxirgisini emas)
    app._sess_open("pdf")
    app._sess_open("info")
    app._sess_open("key")
    app._sess_open("docx")
    assert opened == [str(first.pdf_path), str(first.info_pdf_path), str(first.key_path), str(first.docx_path)]

    app._sess_delete()                                    # askyesno -> True
    assert not any(p.exists() for p in (first.pdf_path, first.info_pdf_path, first.key_path, first.docx_path))
    assert second.pdf_path.exists() and second.info_pdf_path.exists() and second.docx_path.exists()
    assert [s.id for s in app.storage.list_sessions()] == [second.session_id]
    assert len(app.tv_sess.get_children()) == 1


def test_no_selection_gives_message(app):
    app._sess_open("pdf")
    app._sess_delete()
    assert [m[0] for m in app.box][-2:] == ["showinfo", "showinfo"]
