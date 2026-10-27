"""SQLite baza: sessiyalar, testlar, savollar va tekshirish natijalari."""
from __future__ import annotations

import json
import sqlite3
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterator, Optional, Sequence

from checker import CheckResult, GradeScale
from models import GeneratedTest, Student
from paths import data_dir

SCHEMA = """
CREATE TABLE IF NOT EXISTS sessions(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    created_at TEXT NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    n_students INTEGER NOT NULL,
    q_per_test INTEGER NOT NULL,
    seed INTEGER,
    source_file TEXT NOT NULL DEFAULT '',
    pdf_path TEXT NOT NULL DEFAULT '',
    key_path TEXT NOT NULL DEFAULT '',
    info_pdf_path TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS tests(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    test_id TEXT NOT NULL,
    number INTEGER NOT NULL,
    student TEXT NOT NULL DEFAULT '',
    klass TEXT NOT NULL DEFAULT '',
    school TEXT NOT NULL DEFAULT '',
    UNIQUE(session_id, test_id)
);
CREATE TABLE IF NOT EXISTS test_items(
    test_pk INTEGER NOT NULL REFERENCES tests(id) ON DELETE CASCADE,
    position INTEGER NOT NULL,
    src_no INTEGER NOT NULL,
    text TEXT NOT NULL,
    options_json TEXT NOT NULL,
    option_order_json TEXT NOT NULL,
    correct_letter TEXT NOT NULL,
    PRIMARY KEY(test_pk, position)
);
CREATE TABLE IF NOT EXISTS results(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    test_id TEXT NOT NULL,
    student TEXT NOT NULL DEFAULT '',
    correct INTEGER NOT NULL,
    wrong INTEGER NOT NULL,
    blank INTEGER NOT NULL,
    total INTEGER NOT NULL,
    percent REAL NOT NULL,
    grade INTEGER NOT NULL,
    answers TEXT NOT NULL,
    checked_at TEXT NOT NULL,
    UNIQUE(session_id, test_id)
);
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
"""


# Eski bazalarga keyin qo'shilgan ustunlar: (jadval, ustun)
_MIGRATIONS = [("sessions", "info_pdf_path"), ("tests", "student"), ("tests", "klass"), ("tests", "school")]


@dataclass
class SessionRow:
    id: int
    name: str
    created_at: str
    title: str
    n_students: int
    q_per_test: int
    seed: Optional[int]
    source_file: str
    pdf_path: str
    key_path: str
    info_pdf_path: str = ""
    checked: int = 0

    @property
    def label(self) -> str:
        return f"{self.name}  ({self.n_students} ta test, tekshirilgan: {self.checked})"


@dataclass
class StoredTest:
    session_id: int
    test_id: str
    number: int
    texts: list[str]
    options: list[list[str]]
    correct_letters: list[str]
    student: str = ""
    klass: str = ""
    school: str = ""


class Storage:
    """Bazaga ulanish. Har bir amal o'z ulanishini ochadi (thread xavfsiz)."""

    def __init__(self, path: str | Path | None = None) -> None:
        self.path = Path(path) if path else data_dir() / "tests.db"
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self._conn() as c:
            c.executescript(SCHEMA)
            for table, col in _MIGRATIONS:
                have = {r["name"] for r in c.execute(f"PRAGMA table_info({table})")}
                if col not in have:
                    c.execute(f"ALTER TABLE {table} ADD COLUMN {col} TEXT NOT NULL DEFAULT ''")

    @contextmanager
    def _conn(self) -> Iterator[sqlite3.Connection]:
        conn = sqlite3.connect(str(self.path), timeout=15)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys = ON")
        try:
            with conn:
                yield conn
        finally:
            conn.close()

    # ---- sessiyalar
    def create_session(self, *, name: str, title: str, n_students: int, q_per_test: int,
                       seed: int | None, source_file: str, pdf_path: str, key_path: str,
                       tests: list[GeneratedTest], info_pdf_path: str = "",
                       students: Sequence[Student] | None = None) -> int:
        """Sessiya va barcha testlarni bitta tranzaksiyada saqlaydi."""
        with self._conn() as c:
            cur = c.execute(
                "INSERT INTO sessions(name,created_at,title,n_students,q_per_test,seed,source_file,pdf_path,key_path,"
                "info_pdf_path) VALUES(?,?,?,?,?,?,?,?,?,?)",
                (name, datetime.now().strftime("%Y-%m-%d %H:%M:%S"), title, n_students, q_per_test,
                 seed, source_file, pdf_path, key_path, info_pdf_path))
            sid = int(cur.lastrowid)
            for k, t in enumerate(tests):
                st = students[k] if students and k < len(students) else Student()
                tc = c.execute("INSERT INTO tests(session_id,test_id,number,student,klass,school) VALUES(?,?,?,?,?,?)",
                               (sid, t.test_id, t.number, st.name, st.klass, st.school))
                tpk = int(tc.lastrowid)
                c.executemany(
                    "INSERT INTO test_items(test_pk,position,src_no,text,options_json,option_order_json,correct_letter)"
                    " VALUES(?,?,?,?,?,?,?)",
                    [(tpk, pos, it.src_no, it.text, json.dumps(list(it.options), ensure_ascii=False),
                      json.dumps(list(it.option_order)), it.correct_letter)
                     for pos, it in enumerate(t.items, start=1)])
            return sid

    def list_sessions(self) -> list[SessionRow]:
        with self._conn() as c:
            rows = c.execute(
                "SELECT s.*, (SELECT COUNT(*) FROM results r WHERE r.session_id=s.id) AS checked "
                "FROM sessions s ORDER BY s.id DESC").fetchall()
        return [SessionRow(r["id"], r["name"], r["created_at"], r["title"], r["n_students"],
                           r["q_per_test"], r["seed"], r["source_file"], r["pdf_path"], r["key_path"],
                           r["info_pdf_path"], r["checked"]) for r in rows]

    def delete_session(self, session_id: int) -> None:
        with self._conn() as c:
            c.execute("DELETE FROM sessions WHERE id=?", (session_id,))

    def all_test_ids(self) -> set[str]:
        with self._conn() as c:
            return {r[0] for r in c.execute("SELECT DISTINCT test_id FROM tests")}

    # ---- testlar
    def find_test(self, session_id: int, test_id: str) -> Optional[StoredTest]:
        with self._conn() as c:
            t = c.execute("SELECT id, number, student, klass, school FROM tests WHERE session_id=? AND test_id=?",
                          (session_id, test_id)).fetchone()
            if not t:
                return None
            items = c.execute(
                "SELECT text, options_json, correct_letter FROM test_items WHERE test_pk=? ORDER BY position",
                (t["id"],)).fetchall()
        return StoredTest(session_id, test_id, t["number"],
                          [i["text"] for i in items],
                          [json.loads(i["options_json"]) for i in items],
                          [i["correct_letter"] for i in items],
                          t["student"], t["klass"], t["school"])

    # ---- natijalar
    def get_result(self, session_id: int, test_id: str) -> Optional[sqlite3.Row]:
        with self._conn() as c:
            return c.execute("SELECT * FROM results WHERE session_id=? AND test_id=?",
                             (session_id, test_id)).fetchone()

    def save_result(self, session_id: int, res: CheckResult, student: str) -> None:
        """Natijani saqlaydi; shu ID uchun avvalgisi bo'lsa yangilaydi."""
        with self._conn() as c:
            c.execute(
                "INSERT INTO results(session_id,test_id,student,correct,wrong,blank,total,percent,grade,answers,checked_at)"
                " VALUES(?,?,?,?,?,?,?,?,?,?,?)"
                " ON CONFLICT(session_id,test_id) DO UPDATE SET student=excluded.student, correct=excluded.correct,"
                " wrong=excluded.wrong, blank=excluded.blank, total=excluded.total, percent=excluded.percent,"
                " grade=excluded.grade, answers=excluded.answers, checked_at=excluded.checked_at",
                (session_id, res.test_id, student, res.correct, res.wrong, res.blank, res.total, res.percent,
                 res.grade, "".join(a or "-" for a in res.answers), datetime.now().strftime("%Y-%m-%d %H:%M:%S")))

    def list_results(self, session_id: int) -> list[dict]:
        with self._conn() as c:
            rows = c.execute(
                "SELECT r.*, t.number, t.klass, t.school FROM results r LEFT JOIN tests t "
                "ON t.session_id=r.session_id AND t.test_id=r.test_id "
                "WHERE r.session_id=? ORDER BY t.number, r.test_id", (session_id,)).fetchall()
        return [dict(r) for r in rows]

    # ---- sozlamalar
    def get_grade_scale(self) -> GradeScale:
        with self._conn() as c:
            r = c.execute("SELECT value FROM settings WHERE key='grade_scale'").fetchone()
        return GradeScale.from_json(r["value"] if r else None)

    def set_grade_scale(self, scale: GradeScale) -> None:
        scale.validate()
        with self._conn() as c:
            c.execute("INSERT INTO settings(key,value) VALUES('grade_scale',?) "
                      "ON CONFLICT(key) DO UPDATE SET value=excluded.value", (scale.to_json(),))
