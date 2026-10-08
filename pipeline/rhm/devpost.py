"""Devpost gallery + project page scraper."""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from bs4 import BeautifulSoup

from .http import PoliteSession


@dataclass
class DevpostProject:
    title: str
    tagline: str = ""
    url: str | None = None
    winner: bool = False
    built_with: list[str] = field(default_factory=list)
    links: list[str] = field(default_factory=list)        # "Try it out" links
    video_url: str | None = None
    sections: dict[str, str] = field(default_factory=dict)  # "What it does" -> text
    prizes: list[str] = field(default_factory=list)        # prizes won (Devpost doesn't expose tracks entered)
    team: list[str] = field(default_factory=list)
    thumbnail: str | None = None

    @property
    def slug(self) -> str | None:
        if self.url and "/software/" in self.url:
            return self.url.rstrip("/").rsplit("/", 1)[-1]
        return None


def _text(el) -> str:
    return re.sub(r"\s+", " ", el.get_text(" ", strip=True)).strip() if el else ""


def parse_gallery(html: str) -> list[DevpostProject]:
    soup = BeautifulSoup(html, "html.parser")
    out = []
    for item in soup.select("div.gallery-item"):
        a = item.select_one("a.link-to-software") or item.find("a", href=True)
        title = _text(item.select_one("h5")) or _text(item.select_one(".software-entry-name"))
        if not title:
            continue
        img = item.select_one("figure img")
        out.append(DevpostProject(
            title=title,
            tagline=_text(item.select_one("p.tagline")),
            url=a["href"] if a else None,
            winner=bool(item.select_one(".winner, aside.entry-badge img")),
            thumbnail=img.get("src") if img else None,
        ))
    return out


def list_gallery(base: str, session: PoliteSession, max_pages: int = 30) -> list[DevpostProject]:
    projects, seen = [], set()
    for page in range(1, max_pages + 1):
        status, html = session.get(f"{base}/project-gallery?page={page}")
        if status != 200:
            break
        batch = parse_gallery(html)
        new = [p for p in batch if (p.url or p.title) not in seen]
        if not new:
            break
        for p in new:
            seen.add(p.url or p.title)
        projects += new
    return projects


def parse_project(html: str, p: DevpostProject) -> DevpostProject:
    soup = BeautifulSoup(html, "html.parser")
    p.built_with = [_text(li) for li in soup.select("#built-with li")] or \
                   [_text(t) for t in soup.select("#built-with .cp-tag")]
    links = [a["href"] for a in soup.select('ul[data-role="software-urls"] a[href]')]
    if not links:
        links = [a["href"] for a in soup.select("nav.app-links a[href]")]
    p.links = list(dict.fromkeys(links))

    iframe = soup.select_one("#gallery iframe[src], .video-embed[src], iframe.video-embed")
    if iframe:
        p.video_url = iframe.get("src")

    details = soup.select_one("#app-details-left")
    if details:
        current, buf = "_intro", []
        for el in details.find_all(["h2", "h3", "p", "ul", "ol"], recursive=True):
            if el.name in ("h2", "h3"):
                if buf:
                    p.sections[current] = " ".join(buf).strip()
                current, buf = _text(el), []
            elif el.find_parent(id="built-with") is None:
                buf.append(_text(el))
        if buf:
            p.sections[current] = " ".join(buf).strip()
        for junk in ("Built With", "Try it out"):
            p.sections.pop(junk, None)
        p.sections = {k: v for k, v in p.sections.items() if v}

    prizes = []
    for li in soup.select("#submissions .software-list-content li"):
        if li.select_one(".winner"):
            name = re.sub(r"^\s*winner\s*", "", _text(li), flags=re.I)
            if name:
                prizes.append(name)
    p.prizes = list(dict.fromkeys(prizes))
    if prizes:
        p.winner = True
    team = []
    for li in soup.select("#app-team li.software-team-member"):
        a = next((a for a in li.select("a.user-profile-link") if _text(a)), None)
        img = li.select_one("img[alt]")
        name = _text(a) if a else (img["alt"] if img else _text(li.select_one(".columns span, .columns")))
        if name:
            team.append(name)
    p.team = list(dict.fromkeys(team))
    return p


def fetch_project(p: DevpostProject, session: PoliteSession) -> DevpostProject:
    if not p.url:
        return p
    status, html = session.get(p.url)
    return parse_project(html, p) if status == 200 else p
