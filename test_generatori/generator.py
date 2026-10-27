"""Har bir o'quvchi uchun alohida test variantini tuzish."""
from __future__ import annotations

import random
import re
import secrets
from dataclasses import dataclass, field
from typing import Callable, Collection, Optional

from models import GeneratedTest, Question, TestItem

MAX_STUDENTS = 1000
ID_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"  # chalkash I, O, 0, 1 yo'q
ID_LEN = 6
MAX_ATTEMPTS = 200

# "Yuqoridagilarning hammasi", "A va B" kabi variantlar tartibga bog'liq — ularni aralashtirmaymiz.
_POSITION_DEPENDENT = re.compile(
    r"(hamma|barcha|hammasi|barchasi|hech\s*biri|yuqoridagi|yuqorida|"
    r"all\s+of|none\s+of|both|"
    r"все|ни\s+один|"
    r"\b[A-DАВСД]\s*(?:va|и|ва|and|,|&)\s*[A-DАВСД]\b|"
    r"\b[A-DАВСД]\s*(?:dan|дан)\s*[A-DАВСД]\b)",
    re.IGNORECASE,
)

ProgressCb = Callable[[int, int], None]


@dataclass
class GenerationResult:
    tests: list[GeneratedTest]
    seed: int
    warnings: list[str] = field(default_factory=list)
    shuffled_options: bool = True
    fixed_option_questions: int = 0  # variantlari aralashtirilmagan savollar soni


def is_position_dependent(q: Question) -> bool:
    """Variantlar tartibiga tayanadigan savolmi? (masalan «A va B to'g'ri»)."""
    return any(_POSITION_DEPENDENT.search(o) for o in q.options)


def make_test_id(rng: random.Random) -> str:
    """Tasodifiy ID: T- + 6 belgi (masalan T-4F7K29)."""
    return "T-" + "".join(rng.choice(ID_ALPHABET) for _ in range(ID_LEN))


def new_seed() -> int:
    """Kriptografik jihatdan tasodifiy yangi seed."""
    return secrets.randbelow(2**31 - 1) + 1


def validate_params(bank_size: int, n_students: int, q_per_test: int) -> None:
    """Kiritilgan sonlarni tekshiradi; xato bo'lsa ValueError (o'zbekcha xabar)."""
    if bank_size < 1:
        raise ValueError("Bazada birorta ham yaroqli savol yo'q.")
    if not 1 <= n_students <= MAX_STUDENTS:
        raise ValueError(f"O'quvchilar soni 1 dan {MAX_STUDENTS} gacha bo'lishi kerak.")
    if q_per_test < 1:
        raise ValueError("Savollar soni kamida 1 bo'lishi kerak.")
    if q_per_test > bank_size:
        raise ValueError(
            f"Savollar soni ({q_per_test}) bazadagi savollar sonidan ({bank_size}) katta. "
            f"Eng ko'pi bilan {bank_size} ta savol kiritish mumkin.")


def generate_tests(
    questions: list[Question],
    n_students: int,
    q_per_test: int,
    *,
    seed: Optional[int] = None,
    shuffle_options: bool = True,
    existing_ids: Collection[str] = (),
    progress: Optional[ProgressCb] = None,
) -> GenerationResult:
    """`n_students` ta variant tuzadi; har birida `q_per_test` ta takrorlanmas savol.

    * Savollar ishlatilish soni bo'yicha tekis taqsimlanadi.
    * Savollar tartibi va (ixtiyoriy) variantlar tartibi aralashtiriladi.
    * Ikki o'quvchiga aynan bir xil variant tushmasligi uchun harakat qilinadi.
    * Bir xil `seed` aynan bir xil natijani beradi.
    """
    validate_params(len(questions), n_students, q_per_test)
    used_seed = seed if seed is not None else new_seed()
    rng = random.Random(used_seed)
    m = len(questions)
    usage = [0] * m
    fixed = [is_position_dependent(q) for q in questions]
    used_ids: set[str] = set(existing_ids)
    seen_sigs: set[tuple] = set()
    tests: list[GeneratedTest] = []
    dup_count = 0

    for k in range(1, n_students + 1):
        chosen: list[int] = []
        items: list[TestItem] = []
        for attempt in range(MAX_ATTEMPTS):
            order = list(range(m))
            rng.shuffle(order)
            order.sort(key=lambda i: usage[i])      # barqaror saralash: teng ishlatilganlar tasodifiy
            chosen = order[:q_per_test]
            rng.shuffle(chosen)
            items = []
            for qi in chosen:
                q = questions[qi]
                perm = list(range(4))
                if shuffle_options and not fixed[qi]:
                    rng.shuffle(perm)
                items.append(TestItem(
                    src_no=q.source_no,
                    text=q.text,
                    options=tuple(q.options[p] for p in perm),
                    correct=perm.index(q.correct),
                    option_order=tuple(perm),
                ))
            sig = tuple((qi, it.option_order) for qi, it in zip(chosen, items))
            if sig not in seen_sigs:
                seen_sigs.add(sig)
                break
        else:
            dup_count += 1
        for qi in chosen:
            usage[qi] += 1
        while True:
            tid = make_test_id(rng)
            if tid not in used_ids:
                used_ids.add(tid)
                break
        tests.append(GeneratedTest(number=k, test_id=tid, items=items))
        if progress:
            progress(k, n_students)

    warnings: list[str] = []
    if dup_count:
        warnings.append(
            f"Bazadagi savollar yetarli emas: {dup_count} ta variant boshqasi bilan aynan bir xil bo'lib qoldi. "
            "Ko'proq savol qo'shing yoki savollar sonini kamaytiring.")
    if shuffle_options and any(fixed):
        warnings.append(
            f"{sum(fixed)} ta savolning variantlari («hammasi», «A va B» kabi) tartibga bog'liq bo'lgani uchun aralashtirilmadi.")
    return GenerationResult(tests=tests, seed=used_seed, warnings=warnings,
                            shuffled_options=shuffle_options, fixed_option_questions=sum(fixed))
