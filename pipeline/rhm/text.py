"""Text helpers: slugs, name matching, extractive synopsis."""

from __future__ import annotations

import re
import unicodedata

STOP = {"the", "team", "a", "an", "of", "and", "for", "mit", "xr", "vr", "ar", "mr", "app", "project"}


def ascii_fold(s: str) -> str:
    s = s.replace("æ", "ae").replace("Æ", "AE").replace("ø", "o").replace("³", "3")
    return unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode()


def slugify(s: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "-", ascii_fold(s).lower()).strip("-")
    return s or "untitled"


def norm(s: str) -> str:
    """Name key for fuzzy matching: lowercase alnum, no team/table prefixes."""
    s = ascii_fold(s).lower()
    s = re.sub(r"\b(team|table)\s*#?\s*\d+\b", " ", s)
    s = re.sub(r"\[.*?\]|\(.*?\)", " ", s)
    s = s.split(":")[0] if len(s.split(":")[0].strip()) >= 4 else s
    return re.sub(r"[^a-z0-9]", "", s)


def names_match(title: str, repo_name: str) -> bool:
    a, b = norm(title), norm(repo_name)
    if not a or not b or re.fullmatch(r"(team|repo)\d*", b):
        return False
    if a == b:
        return True
    short, long_ = sorted((a, b), key=len)
    return len(short) >= 6 and long_.startswith(short)


_SENT = re.compile(r"(?<=[.!?])\s+(?=[A-Z0-9\"“'])")


def sentences(text: str) -> list[str]:
    text = re.sub(r"\s+", " ", text or "").strip()
    return [s.strip() for s in _SENT.split(text) if len(s.strip()) > 2]


def synopsis(tagline: str, sections: dict[str, str] | None = None, max_chars: int = 420) -> str:
    """2-3 sentences: tagline, then 'What it does' (falls back to intro / Inspiration)."""
    sections = sections or {}
    body = ""
    for key in sections:
        if key.lower().startswith("what it does"):
            body = sections[key]
            break
    if not body:
        body = sections.get("_intro") or next((v for k, v in sections.items() if k.lower().startswith("inspiration")), "")

    out: list[str] = []
    seen = set()
    for s in sentences(tagline) + sentences(body):
        key = norm(s)[:60]
        if key in seen or len(s) < 12 and out:
            continue
        if out and len(" ".join(out)) + len(s) + 1 > max_chars:
            break
        out.append(s if s[-1] in ".!?" else s + ".")
        seen.add(key)
        if len(out) == 3:
            break
    text = " ".join(out)
    if len(text) > max_chars:
        text = text[: max_chars - 1].rsplit(" ", 1)[0] + "…"
    return text
