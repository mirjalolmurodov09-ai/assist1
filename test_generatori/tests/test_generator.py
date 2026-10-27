import re
from collections import Counter

import pytest

from generator import ID_ALPHABET, generate_tests, is_position_dependent, validate_params
from helpers import make_bank
from models import Question


def test_basic_counts_and_no_repeats_within_test():
    bank = make_bank(50)
    res = generate_tests(bank, 25, 20, seed=1)
    assert len(res.tests) == 25
    for t in res.tests:
        assert len(t.items) == 20
        assert len({it.src_no for it in t.items}) == 20


def test_correct_answer_follows_text_after_shuffle():
    bank = make_bank(30)
    by_no = {q.source_no: q for q in bank}
    res = generate_tests(bank, 40, 15, seed=7)
    for t in res.tests:
        for it in t.items:
            q = by_no[it.src_no]
            assert it.text == q.text
            assert it.options[it.correct] == q.options[q.correct]
            assert sorted(it.options) == sorted(q.options)
            assert [q.options[p] for p in it.option_order] == list(it.options)


def test_options_actually_shuffled():
    bank = make_bank(40)
    res = generate_tests(bank, 10, 40, seed=3)
    moved = sum(1 for t in res.tests for it in t.items if it.option_order != (0, 1, 2, 3))
    assert moved > 0.5 * 10 * 40
    letters = Counter(l for t in res.tests for l in t.correct_letters())
    assert set(letters) == set("ABCD") and min(letters.values()) > 50


def test_no_shuffle_option_keeps_order():
    res = generate_tests(make_bank(10), 3, 10, seed=1, shuffle_options=False)
    assert all(it.option_order == (0, 1, 2, 3) for t in res.tests for it in t.items)


def test_ids_unique_and_well_formed():
    res = generate_tests(make_bank(20), 1000, 5, seed=11)
    ids = [t.test_id for t in res.tests]
    assert len(set(ids)) == 1000
    pat = re.compile(rf"^T-[{ID_ALPHABET}]{{6}}$")
    assert all(pat.match(i) for i in ids)
    assert not set("O0I1") & set("".join(i[2:] for i in ids))
    assert [t.number for t in res.tests] == list(range(1, 1001))


def test_existing_ids_avoided():
    first = generate_tests(make_bank(10), 5, 5, seed=5)
    taken = {t.test_id for t in first.tests}
    again = generate_tests(make_bank(10), 5, 5, seed=5, existing_ids=taken)
    assert not taken & {t.test_id for t in again.tests}


def test_same_seed_same_result_different_seed_differs():
    bank = make_bank(40)
    a = generate_tests(bank, 8, 12, seed=99)
    b = generate_tests(bank, 8, 12, seed=99)
    c = generate_tests(bank, 8, 12, seed=100)
    assert [(t.test_id, t.items) for t in a.tests] == [(t.test_id, t.items) for t in b.tests]
    assert [t.test_id for t in a.tests] != [t.test_id for t in c.tests]


def test_random_seed_reported_and_reproducible():
    bank = make_bank(40)
    a = generate_tests(bank, 4, 10)
    b = generate_tests(bank, 4, 10, seed=a.seed)
    assert [t.test_id for t in a.tests] == [t.test_id for t in b.tests]


def test_usage_balanced():
    bank = make_bank(50)
    res = generate_tests(bank, 25, 20, seed=2)   # 500 ta joy / 50 savol = har biri 10 marta
    use = Counter(it.src_no for t in res.tests for it in t.items)
    assert len(use) == 50 and max(use.values()) - min(use.values()) <= 1


def test_no_two_identical_variants_even_when_full_bank():
    bank = make_bank(6)
    res = generate_tests(bank, 200, 6, seed=4)       # barcha savollar, faqat tartib/variant farqlanadi
    sigs = {tuple((it.src_no, it.option_order) for it in t.items) for t in res.tests}
    assert len(sigs) == 200 and not res.warnings


def test_warning_when_impossible_to_be_unique():
    res = generate_tests(make_bank(1), 60, 1, seed=1)   # faqat 24 ta turli variant mumkin
    assert res.warnings and len(res.tests) == 60


def test_validation_messages():
    with pytest.raises(ValueError, match="30"):
        validate_params(30, 5, 31)
    with pytest.raises(ValueError):
        validate_params(30, 0, 5)
    with pytest.raises(ValueError):
        validate_params(30, 1001, 5)
    with pytest.raises(ValueError):
        validate_params(30, 5, 0)
    with pytest.raises(ValueError):
        validate_params(0, 5, 1)


def test_position_dependent_options_not_shuffled():
    q = Question("Qaysi?", ("1", "2", "3", "A va B"), 3, 1)
    assert is_position_dependent(q)
    res = generate_tests([q], 20, 1, seed=1)
    assert all(it.option_order == (0, 1, 2, 3) and it.correct == 3 for t in res.tests for it in t.items)
    assert res.warnings
