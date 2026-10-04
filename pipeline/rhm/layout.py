"""Museum layout: one wing per year, exhibits grouped by the device each project was designed for.

- Device exhibits hold at most MAX_PER_EXHIBIT paintings; bigger device groups are split
  into balanced rooms (12 -> 4/4/4, not 5/5/2).
- Projects that can't be experienced on a Quest (fidelity == Watch) all go into one
  uncapped "archive" exhibit per year, shown as demo videos.

Writes data/museum.json: the compact file the Unity client loads at runtime.
"""

from __future__ import annotations

import math
import re

MAX_PER_EXHIBIT = 5

DEVICE_LABELS = {
    "quest": "Meta Quest",
    "horizon-worlds": "Horizon Worlds",
    "webxr": "Web (WebXR)",
    "pcvr": "PC VR",
    "hololens": "HoloLens",
    "magic-leap": "Magic Leap",
    "ar-glasses": "AR Glasses",
    "android-xr": "Android XR",
    "vision-pro": "Apple Vision Pro",
    "phone-ar": "Phone AR",
    "apple-native": "iPhone / Apple",
    "snap": "Snap Spectacles & Lenses",
    "social-filter": "Social AR Filters",
    "holo-display": "Holographic Displays",
    "unreal": "Unreal (PC)",
    "hardware-only": "Custom Hardware",
    "unknown": "Unknown Device",
}
# Tie-break order when two device groups are the same size.
DEVICE_ORDER = list(DEVICE_LABELS)


def is_inaccessible(p: dict) -> bool:
    """Can't be experienced on a Quest at all; only the demo video is left."""
    return p["fidelity"] == "Watch"


def split_balanced(items: list, cap: int = MAX_PER_EXHIBIT) -> list[list]:
    if not items:
        return []
    rooms = math.ceil(len(items) / cap)
    base, extra = divmod(len(items), rooms)
    out, i = [], 0
    for r in range(rooms):
        n = base + (1 if r < extra else 0)
        out.append(items[i:i + n])
        i += n
    return out


def _order(ps: list[dict], scores: dict[str, dict] | None = None) -> list[dict]:
    """Highest Bayesian rating first (CLAUDE.md: sort by Bayesian average); ties -> prize winners -> title.
    Before anyone has rated, every project sits at the prior, so this is winners-first alphabetical."""
    scores = scores or {}
    return sorted(ps, key=lambda p: (-(scores.get(p["id"], {}).get("bayes_score") or 0),
                                     not p["winner"], p["title"].lower()))


def youtube_watch_url(embed: str | None) -> str | None:
    if not embed:
        return None
    m = re.search(r"(?:youtube(?:-nocookie)?\.com/(?:embed/|watch\?v=)|youtu\.be/)([\w-]{6,})", embed)
    if m:
        return f"https://www.youtube.com/watch?v={m.group(1)}"
    m = re.search(r"player\.vimeo\.com/video/(\d+)", embed)
    if m:
        return f"https://vimeo.com/{m.group(1)}"
    return embed


def launch_target(p: dict) -> dict:
    """What touching the painting does today (installs/APK launch come in build-order step 5)."""
    links = p.get("links") or {}
    if p["platform"] == "horizon-worlds" and links.get("horizon"):
        return {"kind": "horizon", "url": links["horizon"][0]}
    if p["fidelity"] == "Browser":
        web = (links.get("web") or links.get("other") or [None])[0]
        if web:
            return {"kind": "browser", "url": web}
    return {"kind": "theater", "url": youtube_watch_url(p.get("video_url")) or ""}


def build_layout(projects: list[dict], cap: int = MAX_PER_EXHIBIT, scores: dict[str, dict] | None = None) -> dict:
    wings = []
    for year in sorted({p["year"] for p in projects}):
        ps = [p for p in projects if p["year"] == year]
        groups: dict[str, list[dict]] = {}
        for p in ps:
            if not is_inaccessible(p):
                groups.setdefault(p["platform"], []).append(p)
        devices = sorted(groups, key=lambda d: (-len(groups[d]), DEVICE_ORDER.index(d) if d in DEVICE_ORDER else 99))
        exhibits = []
        for dev in devices:
            rooms = split_balanced(_order(groups[dev], scores), cap)
            label = DEVICE_LABELS.get(dev, dev)
            for i, room in enumerate(rooms, 1):
                exhibits.append({
                    "id": f"{year}-{dev}-{i}",
                    "kind": "device",
                    "device": dev,
                    "title": label if len(rooms) == 1 else f"{label} {i}",
                    "subtitle": f"Room {i} of {len(rooms)} · {len(groups[dev])} projects" if len(rooms) > 1
                                else f"{len(groups[dev])} project{'s' if len(groups[dev]) != 1 else ''}",
                    "project_ids": [p["id"] for p in room],
                })
        archive = _order([p for p in ps if is_inaccessible(p)], scores)
        if archive:
            exhibits.append({
                "id": f"{year}-archive",
                "kind": "archive",
                "device": None,
                "title": f"{year} Archive",
                "subtitle": f"{len(archive)} projects for devices a Quest can't run · demo videos",
                "project_ids": [p["id"] for p in archive],
            })
        wings.append({"year": year, "title": str(year), "project_count": len(ps), "exhibits": exhibits})
    return {"max_per_exhibit": cap, "wings": wings}


def museum_doc(doc: dict, cap: int = MAX_PER_EXHIBIT, scores: dict[str, dict] | None = None) -> dict:
    layout = build_layout(doc["projects"], cap, scores)
    scores = scores or {}
    exhibit_of = {pid: ex["id"] for w in layout["wings"] for ex in w["exhibits"] for pid in ex["project_ids"]}
    projects = []
    for p in doc["projects"]:
        repo = p.get("repo") or {}
        projects.append({
            "id": p["id"],
            "year": p["year"],
            "exhibit": exhibit_of[p["id"]],
            "title": p["title"],
            "tagline": p["tagline"],
            "synopsis": p["synopsis"],
            "platform": p["platform"],
            "device_label": DEVICE_LABELS.get(p["platform"], p["platform"]),
            "requirement_tier": p["requirement_tier"],
            "fidelity": p["fidelity"],
            "extra_hardware": p["extra_hardware"],
            "winner": p["winner"],
            "prizes": p.get("prizes", []),
            "thumbnail": p.get("thumbnail") or "",
            "video_url": youtube_watch_url(p.get("video_url")) or "",
            "devpost_url": p.get("devpost_url") or "",
            "repo_url": repo.get("url") or "",
            "license_gate": p["license_gate"],
            "launch": launch_target(p),
            # Snapshot for offline display; the client refreshes live from Supabase.
            "rating_count": int(scores.get(p["id"], {}).get("rating_count") or 0),
            "avg_stars": float(scores.get(p["id"], {}).get("avg_stars") or 0),
        })
    return {
        "schema_version": 1,
        "generated_at": doc["generated_at"],
        **layout,
        "projects": projects,
    }


def write_museum(doc: dict, path, cap: int = MAX_PER_EXHIBIT, scores: dict[str, dict] | None = None) -> dict:
    import json
    from pathlib import Path
    if scores is None:
        from .supabase_sync import scores_from_env
        scores = scores_from_env()
    m = museum_doc(doc, cap, scores)
    Path(path).write_text(json.dumps(m, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    return m


if __name__ == "__main__":
    # Re-layout without re-scraping:  python -m rhm.layout [projects.json] [museum.json]
    import json
    import sys
    from pathlib import Path
    root = Path(__file__).resolve().parents[2] / "data"
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "projects.json"
    dst = Path(sys.argv[2]) if len(sys.argv) > 2 else root / "museum.json"
    m = write_museum(json.loads(src.read_text(encoding="utf-8")), dst)
    n = sum(len(w["exhibits"]) for w in m["wings"])
    print(f"wrote {dst}: {len(m['wings'])} wings, {n} exhibits, {len(m['projects'])} paintings", file=sys.stderr)
