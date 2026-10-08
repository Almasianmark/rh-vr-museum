"""Build projects.json.

    python -m rhm.build                      # live: Devpost + GitHub/Codeberg APIs (needs network)
    python -m rhm.build --source snapshot    # offline: pipeline/snapshots/ (what the cloud session could reach)
    python -m rhm.build --years 2023 2024 --limit 5   # quick partial run
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

from .classify import Signals, classify
from .config import YEARS
from .devpost import DevpostProject, fetch_project, list_gallery
from .repos import Codeberg, GitHub, RepoInfo, github_host, inspect_repo, link_kind, parse_repo_url
from .text import names_match, norm, slugify, synopsis
from .layout import write_museum
from .summary import write_summary

ROOT = Path(__file__).resolve().parents[1]
SNAP = ROOT / "snapshots"
REPO_ROOT = ROOT.parent
JUNK = re.compile(r"^test project$|ignore this", re.I)


@dataclass
class Record:
    year: int
    dp: DevpostProject
    repo: RepoInfo | None = None
    repo_match: str | None = None          # "devpost-link" | "alias" | "name"
    extra_links: list[str] = field(default_factory=list)


# --- snapshot loader --------------------------------------------------------

def _tsv(path: Path) -> list[list[str]]:
    rows = []
    with path.open(encoding="utf-8") as f:
        for row in csv.reader(f, delimiter="\t", quoting=csv.QUOTE_NONE):
            if row and not (row[0].startswith("# ") or row[0] == "#"):
                rows.append(row + [""] * 6)
    return rows


def _snapshot_repos() -> dict[int, list[RepoInfo]]:
    by_year: dict[int, list[RepoInfo]] = defaultdict(list)
    index: dict[str, RepoInfo] = {}
    for year, host, full, lang, desc, topics, *_ in _tsv(SNAP / "github" / "repos.tsv"):
        base = "github.com" if host == "github" else "codeberg.org"
        ri = RepoInfo(host=host, full_name=full, url=f"https://{base}/{full}", language=lang or None,
                      description=desc, topics=[t for t in topics.split(",") if t],
                      empty=not lang and not desc)
        by_year[int(year)].append(ri)
        index[full.lower()] = ri
    for full, signal, path, *_ in _tsv(SNAP / "github" / "manifest_hits.tsv"):
        ri = index.get(full.lower())
        if not ri:
            continue
        if signal.startswith("com."):
            ri.packages.add(signal)
            ri.markers.add("unity")
        elif signal == "unity-manifest":
            ri.markers.add("unity")
        else:
            ri.markers.add(signal)
        ri.manifest_paths.append(path)
    return by_year


def collect_snapshot(years: list[int]) -> tuple[list[Record], list[RepoInfo]]:
    repos_by_year = _snapshot_repos()
    aliases = {(int(y), norm(t)): full.lower() for y, t, full, *_ in _tsv(SNAP / "github" / "aliases.tsv")}
    records, orphans = [], []
    for year in years:
        path = SNAP / "devpost" / f"{year}.tsv"
        if not path.exists():
            continue
        recs = [Record(year, DevpostProject(title=t, tagline=tag, winner=w.strip() == "1"))
                for t, tag, w, *_ in _tsv(path)]
        _attach_org_repos(recs, repos_by_year.get(year, []), aliases)
        orphans += [r for r in repos_by_year.get(year, []) if not getattr(r, "_used", False) and not r.empty]
        records += recs
    return records, orphans


def _attach_org_repos(recs: list[Record], pool: list[RepoInfo], aliases: dict) -> None:
    by_name = {r.full_name.lower(): r for r in pool}
    for rec in recs:
        if rec.repo:
            for r in pool:
                if r.full_name.lower() == rec.repo.full_name.lower():
                    r._used = True
            continue
        full = aliases.get((rec.year, norm(rec.dp.title)))
        if full and full in by_name:
            rec.repo, rec.repo_match = by_name[full], "alias"
            by_name[full]._used = True
    for rec in recs:
        if rec.repo:
            continue
        for r in pool:
            if not getattr(r, "_used", False) and names_match(rec.dp.title, r.full_name.split("/")[1]):
                rec.repo, rec.repo_match = r, "name"
                r._used = True
                break


# --- live loader -------------------------------------------------------------

def collect_live(years: list[int], limit: int | None = None) -> tuple[list[Record], list[RepoInfo]]:
    from .http import PoliteSession
    http = PoliteSession()
    gh = github_host(http)
    print(f"GitHub access via {type(gh).__name__}", file=sys.stderr)
    cb = Codeberg(http)
    aliases = {(int(y), norm(t)): full.lower() for y, t, full, *_ in _tsv(SNAP / "github" / "aliases.tsv")}
    records, orphans = [], []
    for year in years:
        cfg = YEARS[year]
        print(f"[{year}] gallery {cfg['devpost']}", file=sys.stderr)
        gallery = list_gallery(cfg["devpost"], http)
        if limit:
            gallery = gallery[:limit]
        recs = []
        for i, dp in enumerate(gallery, 1):
            fetch_project(dp, http)
            rec = Record(year, dp)
            repo_links = [l for l in dp.links if parse_repo_url(l)]
            rec.extra_links = [l for l in dp.links if l not in repo_links]
            if repo_links:
                rec.repo = inspect_repo(repo_links[0], http, gh)
                rec.repo_match = "devpost-link"
            recs.append(rec)
            print(f"  {i}/{len(gallery)} {dp.title[:50]} repo={'y' if rec.repo else '-'}", file=sys.stderr)

        if cfg.get("realityhack_api"):
            _attach_realityhack_api(recs, cfg["realityhack_api"], http, gh)

        # Central org: match projects that had no "Try it out" repo, and report the rest as orphans.
        pool_meta = []
        if cfg.get("github_org"):
            listed = gh.org_repos(cfg["github_org"])
            if listed:
                pool_meta = [("github.com", r["full_name"]) for r in listed if r.get("size", 0) > 0]
            else:  # git-only access can't list an org; use the listing captured in snapshots/
                pool_meta = [("github.com", r.full_name) for r in _snapshot_repos().get(year, [])
                             if r.host == "github" and not r.empty]
        if cfg.get("codeberg_org"):
            pool_meta += [("codeberg.org", r["full_name"]) for r in cb.org_repos(cfg["codeberg_org"]) if not r.get("empty")]
        pool = [RepoInfo(host=h.split(".")[0], full_name=f, url=f"https://{h}/{f}") for h, f in pool_meta]
        _attach_org_repos(recs, pool, aliases)
        for rec in recs:  # org matches were only stubs; inspect them now
            if rec.repo and rec.repo_match in ("alias", "name") and not rec.repo.packages and not rec.repo.markers:
                rec.repo = inspect_repo(rec.repo.url, http, gh)
        orphans += [r for r in pool if not getattr(r, "_used", False)]
        records += recs
    return records, orphans


def _attach_realityhack_api(recs: list[Record], url: str, http, gh) -> None:
    """realityhack.world lists each team's repo + Devpost link; use it where Devpost had no repo."""
    rows = http.get_json(url) or []
    by_slug, by_name = {}, {}
    for row in rows:
        sub = row.get("submission_location") or ""
        m = re.search(r"devpost\.com/software/([^/?#]+)", sub)
        if m and not m.group(1).isdigit():
            by_slug[m.group(1)] = row
        by_name[norm(row.get("name") or "")] = row
    for rec in recs:
        row = by_slug.get(rec.dp.slug or "") or by_name.get(norm(rec.dp.title))
        if not row:
            continue
        if row.get("description"):
            rec.dp.sections.setdefault("realityhack.world", row["description"])
        repo_url = row.get("repository_location") or ""
        if not rec.repo and parse_repo_url(repo_url):
            rec.repo = inspect_repo(repo_url, http, gh)
            rec.repo_match = "realityhack.world"


# --- assembly ----------------------------------------------------------------

def to_signals(rec: Record) -> Signals:
    dp, repo = rec.dp, rec.repo
    parts = [dp.title, dp.tagline, " ".join(dp.sections.values())]
    if repo:
        parts += [repo.description, " ".join(repo.topics), repo.readme[:8000]]
    return Signals(
        packages=set(repo.packages) if repo else set(),
        markers=set(repo.markers) if repo else set(),
        built_with=dp.built_with,
        text="\n".join(p for p in parts if p),
        links=dp.links,
        repo_language=repo.language if repo else None,
        license_spdx=repo.license_spdx if repo else None,
        has_repo=bool(repo and repo.exists and not repo.empty),
    )


def assemble(rec: Record, pid: str) -> dict:
    dp, repo = rec.dp, rec.repo
    c = classify(to_signals(rec))
    links = {}
    for l in dp.links:
        links.setdefault(link_kind(l), []).append(l)
    return {
        "id": pid,
        "year": rec.year,
        "title": dp.title,
        "tagline": dp.tagline,
        "synopsis": synopsis(dp.tagline, dp.sections),
        "devpost_url": dp.url,
        "winner": dp.winner,
        "prizes": dp.prizes,
        "built_with": dp.built_with,
        "team": dp.team,
        "video_url": dp.video_url,
        "thumbnail": dp.thumbnail,
        "repo": None if not repo else {
            "url": repo.url,
            "host": repo.host,
            "match": rec.repo_match,
            "exists": repo.exists,
            "empty": repo.empty,
            "language": repo.language,
            "unity_version": repo.unity_version,
            "manifest_paths": repo.manifest_paths,
            "apk_assets": repo.apk_assets,
        },
        "links": links,
        "platform": c.platform,
        "platforms_detected": c.platforms_detected,
        "requirement_tier": c.requirement_tier,
        "fidelity": c.fidelity,
        "quest3_features": c.quest3_features,
        "extra_hardware": c.extra_hardware,
        "license": c.license,
        "license_gate": c.license_gate,
        "confidence": c.confidence,
        "evidence": c.evidence,
        "manual_override": None,
    }


def apply_overrides(projects: list[dict], path: Path) -> int:
    if not path.exists():
        return 0
    overrides = json.loads(path.read_text(encoding="utf-8"))
    n = 0
    for p in projects:
        o = overrides.get(p["id"])
        if o:
            fields = {k: v for k, v in o.items() if k != "note"}
            p.update(fields)
            p["manual_override"] = o
            n += 1
    return n


def build(source: str, years: list[int], out: Path, limit: int | None = None) -> dict:
    records, orphans = collect_live(years, limit) if source == "live" else collect_snapshot(years)

    projects, excluded, ids, seen_titles = [], [], set(), {}
    for rec in records:
        key = (rec.year, norm(rec.dp.title))
        if JUNK.search(rec.dp.title):
            excluded.append({"year": rec.year, "title": rec.dp.title, "reason": "placeholder/superseded entry"})
            continue
        if key in seen_titles:
            excluded.append({"year": rec.year, "title": rec.dp.title,
                             "reason": f"duplicate title of {seen_titles[key]}"})
            continue
        base = f"{rec.year}-{rec.dp.slug or slugify(rec.dp.title)}"
        pid, n = base, 2
        while pid in ids:
            pid, n = f"{base}-{n}", n + 1
        ids.add(pid)
        seen_titles[key] = pid
        projects.append(assemble(rec, pid))

    n_over = apply_overrides(projects, ROOT / "overrides.json")
    doc = {
        "schema_version": 1,
        "generated_at": dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat(),
        "source": source,
        "years": {
            str(y): {
                "devpost": YEARS[y]["devpost"] + "/project-gallery",
                "github_org": YEARS[y].get("github_org"),
                "codeberg_org": YEARS[y].get("codeberg_org"),
                "project_count": sum(p["year"] == y for p in projects),
            } for y in years
        },
        "projects": projects,
        "excluded": excluded,
        "orphan_repos": [{"url": r.url, "language": r.language, "description": r.description} for r in orphans],
        "stats": {"manual_overrides": n_over},
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return doc


def main(argv=None) -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", choices=("live", "snapshot"), default="live")
    ap.add_argument("--years", type=int, nargs="*", default=sorted(YEARS))
    ap.add_argument("--limit", type=int, help="max projects per year (live only; for smoke tests)")
    ap.add_argument("--out", type=Path, default=REPO_ROOT / "data" / "projects.json")
    ap.add_argument("--summary", type=Path, default=REPO_ROOT / "data" / "SUMMARY.md")
    ap.add_argument("--museum", type=Path, default=REPO_ROOT / "data" / "museum.json")
    a = ap.parse_args(argv)
    doc = build(a.source, a.years, a.out, a.limit)
    museum = write_museum(doc, a.museum)
    write_summary(doc, a.summary, museum)
    print(f"wrote {a.out} ({len(doc['projects'])} projects) and {a.summary}", file=sys.stderr)


if __name__ == "__main__":
    main()
