import pytest

from checker import (AnswerParseError, GradeScale, check_answers, normalize_test_id, parse_answers)


@pytest.mark.parametrize("raw,expected", [
    ("T-4F7K29", "T-4F7K29"), ("t-4f7k29", "T-4F7K29"), (" T 4F7K29 ", "T-4F7K29"),
    ("4f7k29", "T-4F7K29"), ("T4F7K29", "T-4F7K29"), ("Т-4F7К29", "T-4F7K29"), ("", ""),
])
def test_normalize_id(raw, expected):
    assert normalize_test_id(raw) == expected


def test_plain_letters():
    a, w = parse_answers("ABCDABCDAB", 10)
    assert a == list("ABCDABCDAB") and not w
    assert parse_answers("a b c d", 4)[0] == list("ABCD")
    assert parse_answers("А В С Д", 4)[0] == list("ABCD")           # kirill


def test_blank_markers():
    a, _ = parse_answers("A-C?", 4)
    assert a == ["A", None, "C", None]


def test_numbered_formats():
    assert parse_answers("1A 2B 3C", 3)[0] == ["A", "B", "C"]
    assert parse_answers("1-A, 2-B, 3-C", 3)[0] == ["A", "B", "C"]
    assert parse_answers("1)a 2)b 3)c", 3)[0] == ["A", "B", "C"]
    assert parse_answers("1A2B3C", 3)[0] == ["A", "B", "C"]
    assert parse_answers("3C 1A", 3)[0] == ["A", None, "C"]
    assert parse_answers("1A 2 3C", 3)[0] == ["A", None, "C"]
    a, _ = parse_answers("10D 1A", 10)
    assert a[9] == "D" and a[0] == "A"


def test_too_few_answers_warns_and_pads():
    a, w = parse_answers("ABC", 5)
    assert a == ["A", "B", "C", None, None] and w and "javobsiz" in w[0]


def test_too_many_answers_is_error():
    with pytest.raises(AnswerParseError, match="ortiqcha"):
        parse_answers("ABCDA", 4)
    with pytest.raises(AnswerParseError):
        parse_answers("1A 9B", 4)


@pytest.mark.parametrize("bad", ["", "   ", "ABZ", "A B E", "1A!", "АБВГД@"])
def test_bad_formats(bad):
    with pytest.raises(AnswerParseError):
        parse_answers(bad, 5)


def test_scoring_and_grades():
    right = list("ABCDABCDAB")
    texts = [f"s{i}" for i in range(10)]
    ans, _ = parse_answers("ABCDABCDAA", 10)
    r = check_answers("T-X", right, texts, ans, GradeScale())
    assert (r.correct, r.wrong, r.blank, r.total) == (9, 1, 0, 10)
    assert r.percent == 90 and r.grade == 5
    assert [(m.q_no, m.given, m.correct) for m in r.mistakes] == [(10, "A", "B")]

    ans, _ = parse_answers("ABCD", 10)                         # 4 to'g'ri, 6 javobsiz
    r = check_answers("T-X", right, texts, ans, GradeScale())
    assert (r.correct, r.wrong, r.blank, r.grade) == (4, 0, 6, 2)


def test_grade_boundaries_and_scale_validation():
    s = GradeScale()
    assert [s.grade(p) for p in (100, 86, 85.9, 71, 70.9, 56, 55.9, 0)] == [5, 5, 4, 4, 3, 3, 2, 2]
    custom = GradeScale(90, 75, 60)
    assert custom.grade(89) == 4 and custom.grade(90) == 5
    for bad in (GradeScale(50, 60, 70), GradeScale(101, 70, 50), GradeScale(80, 80, 50), GradeScale(80, 60, 0)):
        with pytest.raises(ValueError):
            bad.validate()
    assert GradeScale.from_json("buzuq{").min5 == 86
    assert GradeScale.from_json(GradeScale(90, 75, 60).to_json()).min4 == 75
