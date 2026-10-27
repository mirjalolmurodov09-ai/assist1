from models import Question


def make_bank(n: int, prefix: str = "Savol") -> list[Question]:
    """n ta savol: to'g'ri javob o'rni aylanib turadi, matnlarda «+» belgisi yo'q."""
    out = []
    for i in range(n):
        opts = tuple(f"{prefix}{i}-javob{j}" for j in range(4))
        out.append(Question(text=f"{prefix} {i} matni?", options=opts, correct=i % 4, source_no=i + 1))
    return out
