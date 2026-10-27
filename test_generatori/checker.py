"""O'quvchi javoblarini o'qish, tekshirish va baholash."""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from typing import Optional

from models import LETTERS


class AnswerParseError(ValueError):
    """Javoblar qatori noto'g'ri formatda."""


# ---------------------------------------------------------------- Test ID
_CYR_TO_LAT = str.maketrans({
    "А": "A", "В": "B", "С": "C", "Е": "E", "Н": "H", "К": "K", "М": "M", "Р": "P", "Т": "T", "Х": "X",
    "а": "A", "в": "B", "с": "C", "е": "E", "н": "H", "к": "K", "м": "M", "р": "P", "т": "T", "х": "X",
})


def normalize_test_id(raw: str) -> str:
    """"t-4f7k29", " T 4F7K29 ", "4F7K29" -> "T-4F7K29"."""
    s = (raw or "").translate(_CYR_TO_LAT).upper()
    s = re.sub(r"[\s\-–—_.]+", "", s)
    if s.startswith("T") and len(s) > 1:
        s = s[1:]
    return "T-" + s if s else ""


# ---------------------------------------------------------------- Javoblar
_CYR_ANS = str.maketrans({"А": "A", "В": "B", "С": "C", "Д": "D", "Б": "B", "Г": "D",
                          "а": "A", "в": "B", "с": "C", "д": "D", "б": "B", "г": "D"})
_BLANKS = set("-_*.?xX")
_NUMBERED = re.compile(r"(\d+)\s*[\)\.:=\-]*\s*([A-D])?[\s,;]*")


def parse_answers(text: str, q_count: int) -> tuple[list[Optional[str]], list[str]]:
    """Javoblar qatorini `q_count` uzunlikdagi ro'yxatga aylantiradi (None = javobsiz).

    Qabul qilinadi: "ABCDABCD", "A B C D", "1A 2B 3C", "1-A, 2-B", "1) a 2) b", "AB-D".
    Qaytadi: (javoblar, ogohlantirishlar). Format noto'g'ri bo'lsa AnswerParseError.
    """
    warnings: list[str] = []
    s = (text or "").translate(_CYR_ANS).upper()
    s = s.replace(" ", " ").strip()
    if not s:
        raise AnswerParseError("Javoblar kiritilmagan.")
    answers: list[Optional[str]] = [None] * q_count

    if re.search(r"\d", s):
        pos = 0
        seen: dict[int, str] = {}
        while pos < len(s):
            if s[pos].isspace() or s[pos] in ",;":
                pos += 1
                continue
            m = _NUMBERED.match(s, pos)
            if not m or m.end() == pos:
                raise AnswerParseError(f"Javoblar qatorida tushunarsiz joy: «{s[pos:pos + 8]}». "
                                       "Namuna: 1A 2B 3C yoki ABCD...")
            num = int(m.group(1))
            letter = m.group(2)
            pos = m.end()
            if num < 1 or num > q_count:
                raise AnswerParseError(f"Savol raqami {num} testda yo'q (1 dan {q_count} gacha).")
            if num in seen and seen[num] != (letter or ""):
                warnings.append(f"{num}-savol ikki marta ko'rsatilgan; oxirgisi olindi.")
            seen[num] = letter or ""
            answers[num - 1] = letter
        return answers, warnings

    letters: list[Optional[str]] = []
    for ch in s:
        if ch.isspace() or ch in ",;":
            continue
        if ch in LETTERS:
            letters.append(ch)
        elif ch in _BLANKS:
            letters.append(None)
        else:
            raise AnswerParseError(f"Noto'g'ri belgi: «{ch}». Faqat A, B, C, D (va javobsiz uchun «-») ruxsat etiladi.")
    if len(letters) > q_count:
        raise AnswerParseError(
            f"Javoblar ortiqcha: {len(letters)} ta kiritildi, testda esa {q_count} ta savol bor.")
    if len(letters) < q_count:
        warnings.append(
            f"Faqat {len(letters)} ta javob kiritildi, testda {q_count} ta savol bor — "
            f"qolgan {q_count - len(letters)} tasi javobsiz hisoblanadi.")
    for i, a in enumerate(letters):
        answers[i] = a
    return answers, warnings


# ---------------------------------------------------------------- Baholash
@dataclass
class GradeScale:
    """Foiz -> baho shkalasi: min5 dan 5, min4 dan 4, min3 dan 3, qolgani 2."""

    min5: float = 86
    min4: float = 71
    min3: float = 56

    def validate(self) -> None:
        if not (0 < self.min3 < self.min4 < self.min5 <= 100):
            raise ValueError("Shkala noto'g'ri: 0 < «3» < «4» < «5» ≤ 100 bo'lishi kerak.")

    def grade(self, percent: float) -> int:
        if percent >= self.min5:
            return 5
        if percent >= self.min4:
            return 4
        if percent >= self.min3:
            return 3
        return 2

    def to_json(self) -> str:
        return json.dumps({"min5": self.min5, "min4": self.min4, "min3": self.min3})

    @classmethod
    def from_json(cls, raw: str | None) -> "GradeScale":
        if not raw:
            return cls()
        try:
            d = json.loads(raw)
            gs = cls(float(d["min5"]), float(d["min4"]), float(d["min3"]))
            gs.validate()
            return gs
        except (ValueError, KeyError, TypeError):
            return cls()


@dataclass
class Mistake:
    q_no: int
    given: Optional[str]
    correct: str
    text: str = ""


@dataclass
class CheckResult:
    test_id: str
    total: int
    correct: int
    wrong: int
    blank: int
    percent: float
    grade: int
    answers: list[Optional[str]]
    mistakes: list[Mistake] = field(default_factory=list)


def check_answers(test_id: str, correct_letters: list[str], texts: list[str],
                  answers: list[Optional[str]], scale: GradeScale) -> CheckResult:
    """Javoblarni to'g'ri javoblar bilan solishtirib natija hisoblaydi."""
    total = len(correct_letters)
    ok = wrong = blank = 0
    mistakes: list[Mistake] = []
    for i, right in enumerate(correct_letters):
        given = answers[i] if i < len(answers) else None
        if given is None:
            blank += 1
            mistakes.append(Mistake(i + 1, None, right, texts[i] if i < len(texts) else ""))
        elif given == right:
            ok += 1
        else:
            wrong += 1
            mistakes.append(Mistake(i + 1, given, right, texts[i] if i < len(texts) else ""))
    percent = (ok / total * 100.0) if total else 0.0
    return CheckResult(test_id=test_id, total=total, correct=ok, wrong=wrong, blank=blank,
                       percent=percent, grade=scale.grade(percent), answers=list(answers), mistakes=mistakes)
