"""Tkinter interfeysi (o'zbekcha): test tuzish, tekshirish, sozlamalar."""
from __future__ import annotations

import logging
import os
import queue
import subprocess
import sys
import threading
import tkinter as tk
from datetime import datetime
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from typing import Callable, Optional

from checker import (AnswerParseError, CheckResult, GradeScale, check_answers,
                     normalize_test_id, parse_answers)
from excel_io import write_results
from generator import MAX_STUDENTS
from models import LETTERS, ParseResult, Student
from parser import ParseError, parse_file
from paths import default_output_dir, logs_dir, unique_path
from service import SessionOutput, create_session
from storage import SessionRow, Storage, StoredTest
from students import parse_students

log = logging.getLogger("testgen")
APP_TITLE = "Test generatori"


def open_path(p: str | Path) -> None:
    """Faylni yoki papkani tizim dasturi bilan ochadi."""
    try:
        p = str(p)
        if sys.platform.startswith("win"):
            os.startfile(p)  # type: ignore[attr-defined]
        elif sys.platform == "darwin":
            subprocess.Popen(["open", p])
        else:
            subprocess.Popen(["xdg-open", p])
    except Exception as e:  # noqa: BLE001
        messagebox.showerror(APP_TITLE, f"Ochib bo'lmadi: {p}\n{e}")


def parse_int(raw: str, name: str, lo: int, hi: int) -> int:
    """Butun sonni tekshirib o'qiydi; xato bo'lsa ValueError (o'zbekcha xabar)."""
    s = (raw or "").strip()
    if not s:
        raise ValueError(f"«{name}» kiritilmagan.")
    if not s.isdigit():
        raise ValueError(f"«{name}» butun musbat son bo'lishi kerak (kiritilgan: «{s}»).")
    v = int(s)
    if not lo <= v <= hi:
        raise ValueError(f"«{name}» {lo} dan {hi} gacha bo'lishi kerak.")
    return v


class IssuesDialog(tk.Toplevel):
    """Fayl o'qishda topilgan xato va ogohlantirishlar ro'yxati."""

    def __init__(self, parent: tk.Misc, res: ParseResult, ask: bool) -> None:
        super().__init__(parent)
        self.title("Fayl tekshiruvi natijasi")
        self.geometry("860x460")
        self.transient(parent)  # type: ignore[arg-type]
        self.proceed = False
        n_err, n_warn = len(res.errors), len(res.warnings)
        head = (f"Topilgan savol bloklari: {res.total_found}. Yaroqli: {len(res.questions)}. "
                f"Xatolar: {n_err}. Ogohlantirishlar: {n_warn}.")
        ttk.Label(self, text=head, wraplength=820).pack(anchor="w", padx=10, pady=(10, 4))
        frm = ttk.Frame(self)
        frm.pack(fill="both", expand=True, padx=10, pady=4)
        cols = ("lvl", "where", "msg")
        tv = ttk.Treeview(frm, columns=cols, show="headings")
        for c, t, w in (("lvl", "Turi", 100), ("where", "Joyi", 280), ("msg", "Izoh", 440)):
            tv.heading(c, text=t)
            tv.column(c, width=w, anchor="w")
        sb = ttk.Scrollbar(frm, orient="vertical", command=tv.yview)
        tv.configure(yscrollcommand=sb.set)
        tv.pack(side="left", fill="both", expand=True)
        sb.pack(side="right", fill="y")
        tv.tag_configure("xato", foreground="#b00020")
        for i in res.issues:
            tv.insert("", "end", values=(i.level, i.where, i.message), tags=(i.level,))
        bar = ttk.Frame(self)
        bar.pack(fill="x", padx=10, pady=10)
        if ask:
            ttk.Label(bar, text="Xatoli savollar o'tkazib yuboriladi. Davom etasizmi?").pack(side="left")
            ttk.Button(bar, text="To'xtatish", command=self.destroy).pack(side="right", padx=4)
            ttk.Button(bar, text="Davom etish", command=self._go).pack(side="right", padx=4)
        else:
            ttk.Button(bar, text="Yopish", command=self.destroy).pack(side="right")
        try:
            self.wait_visibility()
            self.grab_set()
        except tk.TclError:
            pass  # ba'zi oyna boshqaruvchilarida grab ishlamaydi — dialog baribir ishlaydi
        self.protocol("WM_DELETE_WINDOW", self.destroy)

    def _go(self) -> None:
        self.proceed = True
        self.destroy()


class App(tk.Tk):
    """Asosiy oyna."""

    def __init__(self, storage: Optional[Storage] = None) -> None:
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("980x720")
        self.minsize(860, 620)
        self.storage = storage or Storage()
        self._q: "queue.Queue[Callable[[], None]]" = queue.Queue()
        self._busy = False
        self._last_out: Optional[SessionOutput] = None
        self._sessions: list[SessionRow] = []
        self._stored: Optional[StoredTest] = None
        self._grid_vars: list[tk.StringVar] = []
        self._parsed: Optional[ParseResult] = None

        nb = ttk.Notebook(self)
        nb.pack(fill="both", expand=True, padx=8, pady=8)
        self.tab_gen = ttk.Frame(nb)
        self.tab_chk = ttk.Frame(nb)
        self.tab_set = ttk.Frame(nb)
        nb.add(self.tab_gen, text="  Test tuzish  ")
        nb.add(self.tab_chk, text="  Tekshirish  ")
        nb.add(self.tab_set, text="  Sozlamalar  ")
        self._build_gen()
        self._build_chk()
        self._build_set()
        self.refresh_sessions()
        self.after(80, self._poll)

    # ------------------------------------------------------------------ umumiy
    def report_callback_exception(self, exc, val, tb) -> None:  # noqa: D401
        """Kutilmagan xatoni logga yozadi va foydalanuvchiga ko'rsatadi."""
        log.error("Kutilmagan xato", exc_info=(exc, val, tb))
        messagebox.showerror(APP_TITLE, f"Kutilmagan xatolik: {val}\n\nBatafsil: {logs_dir() / 'app.log'}")

    def _poll(self) -> None:
        try:
            while True:
                self._q.get_nowait()()
        except queue.Empty:
            pass
        self.after(80, self._poll)

    def _bg(self, fn: Callable[[], object], ok: Callable[[object], None],
            err: Callable[[BaseException], None]) -> None:
        """`fn` ni alohida oqimda bajaradi, natijani asosiy oqimda `ok`/`err` ga beradi."""
        def run() -> None:
            try:
                res = fn()
            except BaseException as e:  # noqa: BLE001
                log.exception("Orqa fon ishida xato")
                self._q.put(lambda e=e: err(e))
            else:
                self._q.put(lambda r=res: ok(r))
        threading.Thread(target=run, daemon=True).start()

    def _set_busy(self, busy: bool) -> None:
        self._busy = busy
        st = "disabled" if busy else "normal"
        for b in (self.btn_gen, self.btn_scan, self.btn_browse):
            b.configure(state=st)
        if not busy:
            self.pb.configure(value=0)

    def _log(self, text: str) -> None:
        self.log_box.configure(state="normal")
        self.log_box.insert("end", f"{datetime.now():%H:%M:%S}  {text}\n")
        self.log_box.see("end")
        self.log_box.configure(state="disabled")

    @staticmethod
    def _friendly(e: BaseException) -> str:
        if isinstance(e, PermissionError):
            return f"{e}\nFayl ochiq turgan bo'lishi mumkin — uni yoping va qayta urinib ko'ring."
        if isinstance(e, OSError) and not str(e):
            return "Fayl bilan ishlashda xatolik."
        return str(e) or e.__class__.__name__

    # ------------------------------------------------------------------ 1-tab
    def _build_gen(self) -> None:
        f = self.tab_gen
        f.columnconfigure(1, weight=1)
        self.v_src = tk.StringVar()
        self.v_n = tk.StringVar(value="25")
        self.v_q = tk.StringVar(value="20")
        self.v_title = tk.StringVar(value="Test sinovi")
        self.v_sub = tk.StringVar()
        self.v_date = tk.StringVar(value=datetime.now().strftime("%d.%m.%Y"))
        self.v_seed = tk.StringVar()
        self.v_shuffle = tk.BooleanVar(value=True)
        self.v_out = tk.StringVar(value=str(default_output_dir()))
        self.v_stu = tk.StringVar()

        pad = {"padx": 6, "pady": 4}
        r = 0
        ttk.Label(f, text="Savollar fayli (.docx / .xlsx):").grid(row=r, column=0, sticky="w", **pad)
        ttk.Entry(f, textvariable=self.v_src).grid(row=r, column=1, sticky="ew", **pad)
        bf = ttk.Frame(f)
        bf.grid(row=r, column=2, **pad)
        self.btn_browse = ttk.Button(bf, text="Tanlash…", command=self.choose_src)
        self.btn_browse.pack(side="left", padx=2)
        self.btn_scan = ttk.Button(bf, text="Faylni tekshirish", command=self.scan_file)
        self.btn_scan.pack(side="left", padx=2)
        r += 1
        self.lbl_bank = ttk.Label(f, text="Fayl hali tanlanmagan.", foreground="#555")
        self.lbl_bank.grid(row=r, column=1, columnspan=2, sticky="w", padx=6)
        r += 1
        ttk.Separator(f).grid(row=r, column=0, columnspan=3, sticky="ew", pady=6)
        r += 1
        ttk.Label(f, text="O'quvchilar ro'yxati (ixtiyoriy):").grid(row=r, column=0, sticky="w", **pad)
        ttk.Entry(f, textvariable=self.v_stu).grid(row=r, column=1, sticky="ew", **pad)
        sbf = ttk.Frame(f)
        sbf.grid(row=r, column=2, **pad)
        ttk.Button(sbf, text="Tanlash…", command=self.choose_students).pack(side="left", padx=2)
        ttk.Button(sbf, text="Tozalash", command=lambda: self.v_stu.set("")).pack(side="left", padx=2)
        r += 1
        ttk.Label(f, text="Ro'yxat: .xlsx/.csv/.txt/.docx, ustunlar: F.I.Sh. | Sinf | Maktab. Ro'yxat berilsa, "
                          "o'quvchilar soni undan olinadi.", foreground="#555").grid(
            row=r, column=1, columnspan=2, sticky="w", padx=6)
        r += 1
        ttk.Label(f, text="O'quvchilar soni:").grid(row=r, column=0, sticky="w", **pad)
        num = ttk.Frame(f)
        num.grid(row=r, column=1, sticky="w", **pad)
        ttk.Spinbox(num, from_=1, to=MAX_STUDENTS, width=8, textvariable=self.v_n).pack(side="left")
        ttk.Label(num, text=f"   (1–{MAX_STUDENTS})      Har bir testdagi savollar soni:").pack(side="left")
        ttk.Spinbox(num, from_=1, to=500, width=8, textvariable=self.v_q).pack(side="left", padx=6)
        r += 1
        ttk.Label(f, text="Test sarlavhasi (fan):").grid(row=r, column=0, sticky="w", **pad)
        ttk.Entry(f, textvariable=self.v_title).grid(row=r, column=1, columnspan=2, sticky="ew", **pad)
        r += 1
        ttk.Label(f, text="Maktab / sinf (ixtiyoriy):").grid(row=r, column=0, sticky="w", **pad)
        ttk.Entry(f, textvariable=self.v_sub).grid(row=r, column=1, columnspan=2, sticky="ew", **pad)
        r += 1
        ttk.Label(f, text="Sana:").grid(row=r, column=0, sticky="w", **pad)
        sd = ttk.Frame(f)
        sd.grid(row=r, column=1, sticky="w", **pad)
        ttk.Entry(sd, textvariable=self.v_date, width=14).pack(side="left")
        ttk.Label(sd, text="  (ma'lumot varag'ida)    Seed (ixtiyoriy):").pack(side="left")
        ttk.Entry(sd, textvariable=self.v_seed, width=12).pack(side="left", padx=6)
        ttk.Label(sd, text="(qayta tiklash uchun)", foreground="#555").pack(side="left")
        r += 1
        ttk.Checkbutton(f, text="Har bir savolning variantlarini ham aralashtirish",
                        variable=self.v_shuffle).grid(row=r, column=1, sticky="w", **pad)
        r += 1
        ttk.Label(f, text="Natija papkasi:").grid(row=r, column=0, sticky="w", **pad)
        ttk.Entry(f, textvariable=self.v_out).grid(row=r, column=1, sticky="ew", **pad)
        ttk.Button(f, text="Tanlash…", command=self.choose_out).grid(row=r, column=2, **pad)
        r += 1
        self.btn_gen = ttk.Button(f, text="TEST TUZISH  (testlar PDF + o'quvchi varaqlari + javoblar kaliti)", command=self.generate)
        self.btn_gen.grid(row=r, column=0, columnspan=3, sticky="ew", padx=6, pady=(10, 4), ipady=6)
        r += 1
        self.pb = ttk.Progressbar(f, mode="determinate", maximum=100)
        self.pb.grid(row=r, column=0, columnspan=3, sticky="ew", padx=6, pady=2)
        r += 1
        self.lbl_status = ttk.Label(f, text="Tayyor.")
        self.lbl_status.grid(row=r, column=0, columnspan=3, sticky="w", padx=6)
        r += 1
        self.log_box = tk.Text(f, height=4, state="disabled", wrap="word", background="#fafafa")
        self.log_box.grid(row=r, column=0, columnspan=3, sticky="ew", padx=6, pady=(4, 2))
        r += 1
        f.rowconfigure(r, weight=1)
        sf = ttk.LabelFrame(f, text="Tuzilgan testlar (vaqt bo'yicha tanlang)")
        sf.grid(row=r, column=0, columnspan=3, sticky="nsew", padx=6, pady=(4, 6))
        sf.columnconfigure(0, weight=1)
        sf.rowconfigure(0, weight=1)
        cols = ("name", "date", "n", "q", "seed", "chk")
        self.tv_sess = ttk.Treeview(sf, columns=cols, show="headings", selectmode="browse", height=5)
        for c, t, w in (("name", "Nomi", 300), ("date", "Tuzilgan vaqt", 140), ("n", "Testlar", 70),
                        ("q", "Savollar", 70), ("seed", "Seed", 100), ("chk", "Tekshirilgan", 90)):
            self.tv_sess.heading(c, text=t)
            self.tv_sess.column(c, width=w, anchor="w" if c == "name" else "center")
        ssb = ttk.Scrollbar(sf, orient="vertical", command=self.tv_sess.yview)
        self.tv_sess.configure(yscrollcommand=ssb.set)
        self.tv_sess.grid(row=0, column=0, sticky="nsew", padx=(6, 0), pady=6)
        ssb.grid(row=0, column=1, sticky="ns", pady=6)
        bf2 = ttk.Frame(sf)
        bf2.grid(row=1, column=0, columnspan=2, sticky="w", padx=6, pady=(0, 6))
        for text, what in (("Testlar PDF", "pdf"), ("O'quvchi varaqlari PDF", "info"),
                           ("Javoblar kaliti", "key"), ("Papkani ochish", "dir")):
            ttk.Button(bf2, text=text, command=lambda w=what: self._sess_open(w)).pack(side="left", padx=3)
        ttk.Button(bf2, text="O'chirish", command=self._sess_delete).pack(side="left", padx=22)

    def choose_students(self) -> None:
        p = filedialog.askopenfilename(
            title="O'quvchilar ro'yxatini tanlang",
            filetypes=[("Ro'yxat", "*.xlsx *.csv *.txt *.docx"), ("Barcha fayllar", "*.*")])
        if p:
            self.v_stu.set(p)

    def choose_src(self) -> None:
        p = filedialog.askopenfilename(
            title="Savollar faylini tanlang",
            filetypes=[("Word / Excel", "*.docx *.xlsx *.xlsm"), ("Word", "*.docx"), ("Excel", "*.xlsx *.xlsm")])
        if p:
            self.v_src.set(p)
            self.scan_file()

    def choose_out(self) -> None:
        p = filedialog.askdirectory(title="Natija papkasini tanlang", initialdir=self.v_out.get() or None)
        if p:
            self.v_out.set(p)

    def _stage(self, name: str, done: int, total: int) -> None:
        """Orqa oqimdan chaqiriladi: progressni asosiy oqimga uzatadi."""
        def upd() -> None:
            self.lbl_status.configure(text=f"{name}… {done}/{total}" if total > 1 else f"{name}…")
            self.pb.configure(value=(done / total * 100) if total else 0)
        self._q.put(upd)

    # ---- faylni tekshirish
    def scan_file(self) -> None:
        if self._busy:
            return
        src = self.v_src.get().strip().strip('"')
        if not src:
            messagebox.showwarning(APP_TITLE, "Avval savollar faylini tanlang.")
            return
        self._set_busy(True)
        self.lbl_status.configure(text="Fayl o'qilmoqda…")

        def ok(res: object) -> None:
            self._set_busy(False)
            assert isinstance(res, ParseResult)
            self._parsed = res
            self.lbl_status.configure(text="Tayyor.")
            self.lbl_bank.configure(
                text=f"Yaroqli savollar: {len(res.questions)}  |  xatolar: {len(res.errors)}  |  ogohlantirishlar: {len(res.warnings)}",
                foreground="#b00020" if res.errors else "#1b6e1b")
            self._log(f"Fayl o'qildi: {len(res.questions)} ta yaroqli savol, {len(res.errors)} ta xato.")
            if res.issues:
                IssuesDialog(self, res, ask=False)

        def err(e: BaseException) -> None:
            self._set_busy(False)
            self.lbl_status.configure(text="Xatolik.")
            self.lbl_bank.configure(text="Fayl o'qilmadi.", foreground="#b00020")
            messagebox.showerror(APP_TITLE, self._friendly(e))

        self._bg(lambda: parse_file(src), ok, err)

    # ---- test tuzish
    def generate(self) -> None:
        if self._busy:
            return
        try:
            src = self.v_src.get().strip().strip('"')
            if not src:
                raise ValueError("Avval savollar faylini tanlang.")
            stu_path = self.v_stu.get().strip().strip('"')
            n = (parse_int(self.v_n.get(), "O'quvchilar soni", 1, MAX_STUDENTS) if not stu_path
                 else 1)  # ro'yxat berilsa, son undan olinadi
            q = parse_int(self.v_q.get(), "Savollar soni", 1, 100000)
            seed_raw = self.v_seed.get().strip()
            seed = parse_int(seed_raw, "Seed", 0, 2**31 - 1) if seed_raw else None
            out = self.v_out.get().strip()
            if not out:
                raise ValueError("Natija papkasini tanlang.")
        except ValueError as e:
            messagebox.showwarning(APP_TITLE, str(e))
            return

        # Tk o'zgaruvchilari faqat asosiy oqimda o'qiladi
        title, subtitle = self.v_title.get().strip(), self.v_sub.get().strip()
        date, shuffle = self.v_date.get().strip(), bool(self.v_shuffle.get())
        self._set_busy(True)
        self.lbl_status.configure(text="Fayl o'qilmoqda…")
        self.pb.configure(value=0)

        def parsed(both: object) -> None:
            assert isinstance(both, tuple)
            res, students = both
            assert isinstance(res, ParseResult)
            self._parsed = res
            if res.issues:
                dlg = IssuesDialog(self, res, ask=bool(res.errors))
                self.wait_window(dlg)
                if res.errors and not dlg.proceed:
                    self._set_busy(False)
                    self.lbl_status.configure(text="To'xtatildi.")
                    return
            bank = len(res.questions)
            if bank == 0:
                self._set_busy(False)
                messagebox.showerror(APP_TITLE, "Faylda yaroqli savol qolmadi.")
                return
            if q > bank:
                self._set_busy(False)
                self.lbl_status.configure(text="Xatolik.")
                messagebox.showerror(
                    APP_TITLE, f"Savollar soni ({q}) bazadagi yaroqli savollar sonidan ({bank}) katta.\n"
                               f"Eng ko'pi bilan {bank} ta savol kiritish mumkin.")
                return
            n_eff = len(students) if students else n
            self._log(f"Baza: {bank} ta savol. {n_eff} ta o'quvchi, har birida {q} ta savol.")
            self._bg(lambda: create_session(
                res.questions, n_students=n, q_per_test=q, out_dir=out, storage=self.storage,
                title=title, subtitle=subtitle, date=date, seed=seed, shuffle_options=shuffle,
                students=students,
                source_file=src, stage=self._stage), done, fail)

        def done(res: object) -> None:
            assert isinstance(res, SessionOutput)
            self._set_busy(False)
            self._last_out = res
            self.lbl_status.configure(text=f"Tayyor! {res.pages} sahifa. Seed: {res.seed}")
            self.pb.configure(value=100)
            self._log(f"PDF: {res.pdf_path}")
            self._log(f"O'quvchi varaqlari: {res.info_pdf_path}")
            self._log(f"Javoblar kaliti: {res.key_path}")
            self._log(f"Seed: {res.seed}  (xuddi shu testlarni qayta tiklash uchun saqlang)")
            for w in res.warnings:
                self._log("Ogohlantirish: " + w)
            self.refresh_sessions()
            self.tv_sess.selection_set(str(res.session_id))
            self.tv_sess.see(str(res.session_id))
            msg = (f"Testlar tayyor.\n\nTestlar PDF: {res.pdf_path}\nO'quvchi varaqlari: {res.info_pdf_path}\n"
                   f"Kalit: {res.key_path}\n\nSahifalar: {res.pages}")
            if res.warnings:
                msg += "\n\nOgohlantirish:\n" + "\n".join(res.warnings)
            messagebox.showinfo(APP_TITLE, msg)

        def fail(e: BaseException) -> None:
            self._set_busy(False)
            self.lbl_status.configure(text="Xatolik.")
            self._log("Xatolik: " + self._friendly(e))
            messagebox.showerror(APP_TITLE, self._friendly(e))

        def load() -> tuple:
            return parse_file(src), (parse_students(stu_path) if stu_path else None)

        self._bg(load, parsed, fail)

    # ------------------------------------------------------------------ 2-tab
    def _build_chk(self) -> None:
        f = self.tab_chk
        f.columnconfigure(1, weight=1)
        pad = {"padx": 6, "pady": 4}
        self.v_sess = tk.StringVar()
        self.v_tid = tk.StringVar()
        self.v_student = tk.StringVar()
        self.v_text = tk.StringVar()
        self.v_mode = tk.StringVar(value="text")

        ttk.Label(f, text="Sessiya:").grid(row=0, column=0, sticky="w", **pad)
        self.cb_sess = ttk.Combobox(f, textvariable=self.v_sess, state="readonly")
        self.cb_sess.grid(row=0, column=1, sticky="ew", **pad)
        self.cb_sess.bind("<<ComboboxSelected>>", lambda _e: self._on_session_change())
        ttk.Button(f, text="Yangilash", command=self.refresh_sessions).grid(row=0, column=2, **pad)

        ttk.Label(f, text="Test ID:").grid(row=1, column=0, sticky="w", **pad)
        ef = ttk.Frame(f)
        ef.grid(row=1, column=1, sticky="w", **pad)
        self.e_tid = ttk.Entry(ef, textvariable=self.v_tid, width=16, font=("TkDefaultFont", 12, "bold"))
        self.e_tid.pack(side="left")
        self.e_tid.bind("<Return>", lambda _e: self.find_test())
        ttk.Button(ef, text="Topish", command=self.find_test).pack(side="left", padx=6)
        self.lbl_found = ttk.Label(ef, text="")
        self.lbl_found.pack(side="left", padx=6)

        ttk.Label(f, text="F.I.Sh. (ixtiyoriy):").grid(row=2, column=0, sticky="w", **pad)
        ttk.Entry(f, textvariable=self.v_student).grid(row=2, column=1, sticky="ew", **pad)

        mf = ttk.Frame(f)
        mf.grid(row=3, column=0, columnspan=3, sticky="w", **pad)
        ttk.Label(mf, text="Javoblarni kiritish usuli:").pack(side="left")
        ttk.Radiobutton(mf, text="Matn (ABCD… / 1A 2B…)", variable=self.v_mode, value="text",
                        command=self._switch_mode).pack(side="left", padx=8)
        ttk.Radiobutton(mf, text="Tugmalar", variable=self.v_mode, value="grid",
                        command=self._switch_mode).pack(side="left", padx=8)

        self.ans_host = ttk.Frame(f)
        self.ans_host.grid(row=4, column=0, columnspan=3, sticky="ew", padx=6)
        self.ans_host.columnconfigure(0, weight=1)
        # matn usuli
        self.fr_text = ttk.Frame(self.ans_host)
        self.e_ans = ttk.Entry(self.fr_text, textvariable=self.v_text, font=("TkFixedFont", 12))
        self.e_ans.pack(fill="x")
        self.e_ans.bind("<Return>", lambda _e: self.check())
        self.v_text.trace_add("write", lambda *_: self._update_counter())
        self.lbl_cnt = ttk.Label(self.fr_text, text="Namuna: ABCDABCDAB   yoki   1A 2B 3C   (javobsiz: -)",
                                 foreground="#555")
        self.lbl_cnt.pack(anchor="w")
        # tugmalar usuli
        self.fr_grid = ttk.Frame(self.ans_host)
        self.cv = tk.Canvas(self.fr_grid, height=150, highlightthickness=0)
        gsb = ttk.Scrollbar(self.fr_grid, orient="vertical", command=self.cv.yview)
        self.cv.configure(yscrollcommand=gsb.set)
        self.cv.pack(side="left", fill="both", expand=True)
        gsb.pack(side="right", fill="y")
        self.grid_inner = ttk.Frame(self.cv)
        self.cv.create_window((0, 0), window=self.grid_inner, anchor="nw")
        self.grid_inner.bind("<Configure>", lambda _e: self.cv.configure(scrollregion=self.cv.bbox("all")))
        self.cv.bind("<Enter>", lambda _e: self._bind_wheel(True))
        self.cv.bind("<Leave>", lambda _e: self._bind_wheel(False))
        self.fr_text.grid(row=0, column=0, sticky="ew")

        bf = ttk.Frame(f)
        bf.grid(row=5, column=0, columnspan=3, sticky="w", padx=6, pady=6)
        ttk.Button(bf, text="Tekshirish va saqlash", command=self.check).pack(side="left", padx=3, ipady=3)
        ttk.Button(bf, text="Tozalash", command=self.clear_inputs).pack(side="left", padx=3, ipady=3)
        self.lbl_res = ttk.Label(f, text="", font=("TkDefaultFont", 12, "bold"), wraplength=900)
        self.lbl_res.grid(row=6, column=0, columnspan=3, sticky="w", padx=6)

        f.rowconfigure(7, weight=1)
        f.rowconfigure(9, weight=1)
        cols = ("q", "given", "right", "text")
        self.tv_mis = ttk.Treeview(f, columns=cols, show="headings", height=5)
        for c, t, w, a in (("q", "Savol №", 70, "center"), ("given", "O'quvchi javobi", 110, "center"),
                           ("right", "To'g'ri javob", 100, "center"), ("text", "Savol matni", 560, "w")):
            self.tv_mis.heading(c, text=t)
            self.tv_mis.column(c, width=w, anchor=a)  # type: ignore[arg-type]
        self.tv_mis.grid(row=7, column=0, columnspan=3, sticky="nsew", padx=6, pady=2)

        hf = ttk.Frame(f)
        hf.grid(row=8, column=0, columnspan=3, sticky="ew", padx=6, pady=(8, 0))
        ttk.Label(hf, text="Shu sessiyada tekshirilgan natijalar:", font=("TkDefaultFont", 10, "bold")).pack(side="left")
        ttk.Button(hf, text="Natijalarni Excelga eksport (natijalar.xlsx)", command=self.export_results).pack(side="right")
        rc = ("tid", "no", "name", "ok", "pct", "grade")
        self.tv_res = ttk.Treeview(f, columns=rc, show="headings", height=6)
        for c, t, w in (("tid", "Test ID", 110), ("no", "№", 50), ("name", "F.I.Sh.", 300),
                        ("ok", "To'g'ri", 90), ("pct", "Foiz", 80), ("grade", "Baho", 60)):
            self.tv_res.heading(c, text=t)
            self.tv_res.column(c, width=w, anchor="w" if c == "name" else "center")
        self.tv_res.grid(row=9, column=0, columnspan=3, sticky="nsew", padx=6, pady=(2, 6))

    def _bind_wheel(self, on: bool) -> None:
        if on:
            self.cv.bind_all("<MouseWheel>", lambda e: self.cv.yview_scroll(int(-e.delta / 120) or -1 * (e.delta > 0) or 1, "units"))
            self.cv.bind_all("<Button-4>", lambda _e: self.cv.yview_scroll(-1, "units"))
            self.cv.bind_all("<Button-5>", lambda _e: self.cv.yview_scroll(1, "units"))
        else:
            for ev in ("<MouseWheel>", "<Button-4>", "<Button-5>"):
                self.cv.unbind_all(ev)

    def _cur_session(self) -> Optional[SessionRow]:
        i = self.cb_sess.current()
        return self._sessions[i] if 0 <= i < len(self._sessions) else None

    def refresh_sessions(self) -> None:
        prev = self._cur_session()
        self._sessions = self.storage.list_sessions()
        self.cb_sess.configure(values=[s.label for s in self._sessions])
        idx = 0
        if prev:
            idx = next((i for i, s in enumerate(self._sessions) if s.id == prev.id), 0)
        if self._sessions:
            self.cb_sess.current(idx)
        else:
            self.v_sess.set("")
        self._on_session_change()
        self._fill_sessions_tab()

    def _on_session_change(self) -> None:
        self._stored = None
        self.lbl_found.configure(text="")
        self._rebuild_grid(0)
        self._fill_results()

    def _fill_results(self) -> None:
        self.tv_res.delete(*self.tv_res.get_children())
        s = self._cur_session()
        if not s:
            return
        for r in self.storage.list_results(s.id):
            self.tv_res.insert("", "end", values=(
                r["test_id"], r.get("number") or "", r["student"], f"{r['correct']}/{r['total']}",
                f"{r['percent']:.1f}%", r["grade"]))

    def find_test(self) -> bool:
        """Test ID bo'yicha testni topadi."""
        s = self._cur_session()
        if not s:
            messagebox.showwarning(APP_TITLE, "Avval sessiyani tanlang (sessiya yo'q bo'lsa, oldin test tuzing).")
            return False
        tid = normalize_test_id(self.v_tid.get())
        if tid in ("", "T-"):
            messagebox.showwarning(APP_TITLE, "Test ID ni kiriting.")
            return False
        st = self.storage.find_test(s.id, tid)
        if not st:
            self._stored = None
            self.lbl_found.configure(text="Bunday ID topilmadi", foreground="#b00020")
            messagebox.showerror(APP_TITLE, f"Bunday ID topilmadi: {tid}\nTanlangan sessiya: {s.name}")
            return False
        self.v_tid.set(tid)
        self._stored = st
        who = ", ".join(x for x in (st.student, st.klass, st.school) if x)
        self.lbl_found.configure(
            text=f"Topildi: № {st.number}, {len(st.texts)} ta savol" + (f"  —  {who}" if who else ""),
            foreground="#1b6e1b")
        if st.student and not self.v_student.get().strip():
            self.v_student.set(st.student)
        self._rebuild_grid(len(st.texts))
        self._update_counter()
        return True

    def _rebuild_grid(self, n: int) -> None:
        for w in self.grid_inner.winfo_children():
            w.destroy()
        self._grid_vars = []
        cols = 3 if n > 30 else 2 if n > 12 else 1
        per = max(1, -(-n // cols))
        for i in range(n):
            var = tk.StringVar(value="")
            self._grid_vars.append(var)
            col, row = divmod(i, per)
            cell = ttk.Frame(self.grid_inner)
            cell.grid(row=row, column=col, padx=12, sticky="w")
            ttk.Label(cell, text=f"{i + 1}.", width=4, anchor="e").pack(side="left")
            for L in LETTERS:
                ttk.Radiobutton(cell, text=L, value=L, variable=var).pack(side="left", padx=2)
            ttk.Radiobutton(cell, text="–", value="", variable=var).pack(side="left", padx=2)

    def _switch_mode(self) -> None:
        mode = self.v_mode.get()
        n = len(self._grid_vars)
        if mode == "grid":
            if n == 0:
                messagebox.showinfo(APP_TITLE, "Avval Test ID ni kiritib «Topish» tugmasini bosing.")
                self.v_mode.set("text")
                return
            txt = self.v_text.get().strip()
            if txt:
                try:
                    ans, _ = parse_answers(txt, n)
                    for v, a in zip(self._grid_vars, ans):
                        v.set(a or "")
                except AnswerParseError as e:
                    messagebox.showwarning(APP_TITLE, f"Matndagi javoblarni o'qib bo'lmadi:\n{e}")
            self.fr_text.grid_remove()
            self.fr_grid.grid(row=0, column=0, sticky="ew")
        else:
            if n:
                self.v_text.set("".join(v.get() or "-" for v in self._grid_vars) if any(v.get() for v in self._grid_vars) else "")
            self.fr_grid.grid_remove()
            self.fr_text.grid(row=0, column=0, sticky="ew")

    def _update_counter(self) -> None:
        n = len(self._stored.texts) if self._stored else 0
        txt = self.v_text.get().strip()
        if not txt:
            self.lbl_cnt.configure(text="Namuna: ABCDABCDAB   yoki   1A 2B 3C   (javobsiz: -)", foreground="#555")
            return
        try:
            ans, warns = parse_answers(txt, n if n else 10_000)
            given = sum(1 for a in ans if a)
            txt2 = f"Kiritilgan javoblar: {given}" + (f" / {n}" if n else "")
            self.lbl_cnt.configure(text=txt2, foreground="#b36b00" if warns else "#1b6e1b")
        except AnswerParseError as e:
            self.lbl_cnt.configure(text=str(e), foreground="#b00020")

    def clear_inputs(self) -> None:
        self.v_tid.set("")
        self.v_student.set("")
        self.v_text.set("")
        for v in self._grid_vars:
            v.set("")
        self._stored = None
        self.lbl_found.configure(text="")
        self.lbl_res.configure(text="")
        self.tv_mis.delete(*self.tv_mis.get_children())
        self._rebuild_grid(0)
        if self.v_mode.get() == "grid":
            self.v_mode.set("text")
            self.fr_grid.grid_remove()
            self.fr_text.grid(row=0, column=0, sticky="ew")
        self.e_tid.focus_set()

    def _collect_answers(self, n: int) -> tuple[list[Optional[str]], list[str]]:
        if self.v_mode.get() == "grid":
            return [v.get() or None for v in self._grid_vars], []
        return parse_answers(self.v_text.get(), n)

    def check(self) -> Optional[CheckResult]:
        """Javoblarni tekshiradi, natijani ko'rsatadi va saqlaydi."""
        s = self._cur_session()
        if not s:
            messagebox.showwarning(APP_TITLE, "Avval sessiyani tanlang.")
            return None
        tid = normalize_test_id(self.v_tid.get())
        if not self._stored or self._stored.test_id != tid or self._stored.session_id != s.id:
            if not self.find_test():
                return None
        st = self._stored
        assert st is not None
        n = len(st.texts)
        try:
            answers, warns = self._collect_answers(n)
        except AnswerParseError as e:
            messagebox.showerror(APP_TITLE, str(e))
            return None
        if warns and not messagebox.askokcancel(APP_TITLE, "\n".join(warns) + "\n\nDavom etasizmi?"):
            return None
        res = check_answers(st.test_id, st.correct_letters, st.texts, answers, self.storage.get_grade_scale())
        self._show_result(res)

        old = self.storage.get_result(s.id, st.test_id)
        saved = True
        if old is not None:
            saved = messagebox.askyesno(
                APP_TITLE, f"Bu test avval tekshirilgan ({old['correct']}/{old['total']}, baho {old['grade']}).\n"
                           "Natijani yangilaysizmi?")
        if saved:
            try:
                self.storage.save_result(s.id, res, self.v_student.get().strip())
            except Exception as e:  # noqa: BLE001
                log.exception("Natijani saqlashda xato")
                messagebox.showerror(APP_TITLE, f"Natijani saqlab bo'lmadi: {e}")
                return res
            self.lbl_res.configure(text=self.lbl_res.cget("text") + "   [saqlandi]")
            self.refresh_sessions_keep()
        else:
            self.lbl_res.configure(text=self.lbl_res.cget("text") + "   [avvalgi natija saqlanib qoldi]")
        return res

    def refresh_sessions_keep(self) -> None:
        """Sessiyalar ro'yxatini yangilaydi, tanlovni va kiritilgan ma'lumotni saqlab qoladi."""
        sel = self._cur_session()
        tid, stored = self.v_tid.get(), self._stored
        self._sessions = self.storage.list_sessions()
        self.cb_sess.configure(values=[x.label for x in self._sessions])
        if sel:
            idx = next((i for i, x in enumerate(self._sessions) if x.id == sel.id), 0)
            self.cb_sess.current(idx)
        self.v_tid.set(tid)
        self._stored = stored
        self._fill_results()
        self._fill_sessions_tab()

    def _show_result(self, r: CheckResult) -> None:
        color = {5: "#1b6e1b", 4: "#2e7d32", 3: "#b36b00", 2: "#b00020"}[r.grade]
        self.lbl_res.configure(
            foreground=color,
            text=f"{r.test_id}:  to'g'ri {r.correct}, noto'g'ri {r.wrong}, javobsiz {r.blank}  —  "
                 f"{r.percent:.1f}%  —  BAHO: {r.grade}")
        self.tv_mis.delete(*self.tv_mis.get_children())
        for m in r.mistakes:
            self.tv_mis.insert("", "end", values=(m.q_no, m.given or "—", m.correct,
                                                  " ".join(m.text.split())[:140]))

    def export_results(self) -> None:
        s = self._cur_session()
        if not s:
            return
        rows = self.storage.list_results(s.id)
        if not rows:
            messagebox.showinfo(APP_TITLE, "Bu sessiyada hali tekshirilgan natija yo'q.")
            return
        folder = filedialog.askdirectory(title="Natijalar papkasini tanlang",
                                         initialdir=str(Path(s.pdf_path).parent) if s.pdf_path else None)
        if not folder:
            return
        path = unique_path(Path(folder), f"natijalar_{datetime.now():%Y%m%d_%H%M%S}", ".xlsx")
        try:
            write_results(rows, path)
        except Exception as e:  # noqa: BLE001
            log.exception("Eksportda xato")
            messagebox.showerror(APP_TITLE, self._friendly(e))
            return
        if messagebox.askyesno(APP_TITLE, f"Natijalar saqlandi:\n{path}\n\nFaylni ochamizmi?"):
            open_path(path)

    # ------------------------------------------------------------------ 3-tab
    def _build_set(self) -> None:
        f = self.tab_set
        f.columnconfigure(0, weight=1)
        box = ttk.LabelFrame(f, text="Baholash shkalasi (foiz bo'yicha)")
        box.grid(row=0, column=0, sticky="ew", padx=8, pady=8)
        sc = self.storage.get_grade_scale()
        self.v5, self.v4, self.v3 = (tk.StringVar(value=f"{x:g}") for x in (sc.min5, sc.min4, sc.min3))
        for i, (lab, var) in enumerate((("«5» baho: kamida", self.v5), ("«4» baho: kamida", self.v4),
                                        ("«3» baho: kamida", self.v3))):
            ttk.Label(box, text=lab).grid(row=0, column=i * 3, padx=(10, 2), pady=8)
            ttk.Entry(box, textvariable=var, width=6).grid(row=0, column=i * 3 + 1)
            ttk.Label(box, text="%").grid(row=0, column=i * 3 + 2)
        ttk.Label(box, text="Qolgani — «2».").grid(row=0, column=9, padx=10)
        ttk.Button(box, text="Saqlash", command=self.save_scale).grid(row=0, column=10, padx=8)

        ttk.Label(f, text=f"Baza: {self.storage.path}\nLog: {logs_dir() / 'app.log'}", foreground="#555").grid(
            row=1, column=0, sticky="w", padx=8, pady=6)

    def _fill_sessions_tab(self) -> None:
        self.tv_sess.delete(*self.tv_sess.get_children())
        for s in self._sessions:
            self.tv_sess.insert("", "end", iid=str(s.id), values=(
                s.name, s.created_at, s.n_students, s.q_per_test, s.seed if s.seed is not None else "", s.checked))

    def _selected_session(self) -> Optional[SessionRow]:
        sel = self.tv_sess.selection()
        if not sel:
            messagebox.showinfo(APP_TITLE, "Ro'yxatdan sessiyani tanlang.")
            return None
        return next((s for s in self._sessions if str(s.id) == sel[0]), None)

    def _sess_open(self, what: str) -> None:
        """Ro'yxatdan tanlangan sessiyaning faylini ochadi."""
        s = self._selected_session()
        if not s:
            return
        p = {"pdf": s.pdf_path, "info": s.info_pdf_path, "key": s.key_path,
             "dir": str(Path(s.pdf_path).parent) if s.pdf_path else ""}[what]
        if not p or not Path(p).exists():
            messagebox.showwarning(APP_TITLE, "Fayl topilmadi (ko'chirilgan yoki o'chirilgan bo'lishi mumkin).")
            return
        open_path(p)

    def _sess_delete(self) -> None:
        """Tanlangan sessiyani (testlar, natijalar va yaratilgan fayllarni) o'chiradi."""
        s = self._selected_session()
        if not s:
            return
        files = [Path(p) for p in (s.pdf_path, s.info_pdf_path, s.key_path) if p and Path(p).is_file()]
        names = "\n".join(f"  • {f.name}" for f in files) or "  (fayllar topilmadi)"
        if not messagebox.askyesno(
                APP_TITLE,
                f"«{s.name}» o'chiriladi:\n\n• bazadan: {s.n_students} ta test va {s.checked} ta tekshirilgan natija\n"
                f"• kompyuterdan fayllar:\n{names}\n\nBu amalni qaytarib bo'lmaydi. Davom etasizmi?"):
            return
        failed = []
        for f in files:
            try:
                f.unlink()
            except OSError as e:
                failed.append(f"{f.name}: {e}")
        self.storage.delete_session(s.id)
        self._log(f"Sessiya o'chirildi: {s.name}")
        self.refresh_sessions()
        if failed:
            messagebox.showwarning(APP_TITLE, "Sessiya o'chirildi, lekin ba'zi fayllarni o'chirib bo'lmadi "
                                              "(ochiq turgan bo'lishi mumkin):\n" + "\n".join(failed))

    def save_scale(self) -> None:
        try:
            vals = [float(v.get().replace(",", ".")) for v in (self.v5, self.v4, self.v3)]
            sc = GradeScale(*vals)
            sc.validate()
            self.storage.set_grade_scale(sc)
        except ValueError as e:
            messagebox.showerror(APP_TITLE, str(e) if "Shkala" in str(e) else "Foizlar son bo'lishi kerak (masalan 86).")
            return
        messagebox.showinfo(APP_TITLE, "Baholash shkalasi saqlandi.")


def run() -> None:
    """Dasturni ishga tushiradi."""
    app = App()
    app.mainloop()
