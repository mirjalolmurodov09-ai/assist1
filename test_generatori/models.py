"""Dasturda ishlatiladigan umumiy ma'lumot turlari."""
from __future__ import annotations

from dataclasses import dataclass, field

LETTERS = "ABCD"


@dataclass(frozen=True)
class Question:
    """Bazadagi bitta savol: matn, 4 ta variant va to'g'ri variant indeksi (0..3)."""

    text: str
    options: tuple[str, str, str, str]
    correct: int
    source_no: int  # fayldagi tartib raqami (1 dan)


@dataclass
class Issue:
    """Faylni o'qishda topilgan xato yoki ogohlantirish."""

    level: str  # "xato" yoki "ogohlantirish"
    where: str
    message: str

    def __str__(self) -> str:
        return f"[{self.level.upper()}] {self.where}: {self.message}"


@dataclass
class ParseResult:
    """Fayl o'qish natijasi: yaroqli savollar va topilgan muammolar."""

    questions: list[Question] = field(default_factory=list)
    issues: list[Issue] = field(default_factory=list)
    total_found: int = 0  # fayldan topilgan savol bloklari soni

    @property
    def errors(self) -> list[Issue]:
        return [i for i in self.issues if i.level == "xato"]

    @property
    def warnings(self) -> list[Issue]:
        return [i for i in self.issues if i.level != "xato"]


@dataclass(frozen=True)
class TestItem:
    """Aralashtirilgandan keyingi, testdagi bitta savol."""

    src_no: int                       # bazadagi savolning tartib raqami
    text: str
    options: tuple[str, ...]          # aralashtirilgan variantlar
    correct: int                      # to'g'ri variantning yangi indeksi
    option_order: tuple[int, ...]     # yangi o'rin -> asl variant indeksi

    @property
    def correct_letter(self) -> str:
        return LETTERS[self.correct]


@dataclass
class GeneratedTest:
    """Bitta o'quvchi uchun tuzilgan variant."""

    number: int       # tartib raqami (1, 2, 3 ...)
    test_id: str      # masalan T-4F7K29
    items: list[TestItem]

    def correct_letters(self) -> list[str]:
        return [it.correct_letter for it in self.items]


@dataclass(frozen=True)
class Student:
    """O'quvchi ma'lumoti (ro'yxat faylidan)."""

    name: str = ""
    klass: str = ""
    school: str = ""
