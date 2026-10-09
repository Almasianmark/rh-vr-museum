"""Supabase sync for the ratings backend (build-order step 3).

    export SUPABASE_URL=https://<ref>.supabase.co
    export SUPABASE_SERVICE_ROLE_KEY=...       # push only; never ship this key in the client
    python -m rhm.supabase_sync push            # upsert data/projects.json -> public.projects
    python -m rhm.supabase_sync scores          # print the Bayesian leaderboard (anon key is enough)

The museum layout uses the scores to order paintings inside each device group:
    SUPABASE_URL=... SUPABASE_ANON_KEY=... python -m rhm.layout
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parents[2]
BATCH = 200


def _headers(key: str, **extra) -> dict:
    return {"apikey": key, "Authorization": f"Bearer {key}", "Content-Type": "application/json", **extra}


def project_rows(doc: dict) -> list[dict]:
    """Only what the database needs; everything else stays in museum.json."""
    exhibit_of = {}
    museum = ROOT / "data" / "museum.json"
    if museum.exists():
        exhibit_of = {p["id"]: p["exhibit"] for p in json.loads(museum.read_text(encoding="utf-8"))["projects"]}
    return [{"id": p["id"], "year": p["year"], "title": p["title"], "platform": p["platform"],
             "fidelity": p["fidelity"], "exhibit": exhibit_of.get(p["id"])} for p in doc["projects"]]


def push_projects(url: str, service_key: str, rows: list[dict], session=requests) -> int:
    endpoint = f"{url.rstrip('/')}/rest/v1/projects?on_conflict=id"
    for i in range(0, len(rows), BATCH):
        r = session.post(endpoint, data=json.dumps(rows[i:i + BATCH]),
                         headers=_headers(service_key, Prefer="resolution=merge-duplicates,return=minimal"), timeout=30)
        if r.status_code >= 300:
            raise RuntimeError(f"push failed ({r.status_code}): {r.text[:300]}")
    return len(rows)


def fetch_scores(url: str, key: str, prior_weight: float = 5, session=requests) -> dict[str, dict]:
    r = session.post(f"{url.rstrip('/')}/rest/v1/rpc/project_scores", data=json.dumps({"prior_weight": prior_weight}),
                     headers=_headers(key), timeout=30)
    if r.status_code >= 300:
        raise RuntimeError(f"project_scores failed ({r.status_code}): {r.text[:300]}")
    return {row["project_id"]: row for row in r.json()}


BACKEND_JSON = Path(__file__).resolve().parents[2] / "data" / "backend.json"


def backend() -> dict | None:
    """data/backend.json: {"url", "key"} of the public ratings backend (written by supabase/setup.py)."""
    if not BACKEND_JSON.exists():
        return None
    b = json.loads(BACKEND_JSON.read_text(encoding="utf-8"))
    return {"url": b["url"], "key": b["key"]} if b.get("url") and b.get("key") else None


def scores_from_env() -> dict[str, dict] | None:
    """Live scores from SUPABASE_URL/SUPABASE_ANON_KEY, else from data/backend.json; None when neither is set."""
    url, key = os.environ.get("SUPABASE_URL"), os.environ.get("SUPABASE_ANON_KEY")
    if not (url and key) and backend():
        url, key = backend()["url"], backend()["key"]
    if not (url and key):
        return None
    try:
        return fetch_scores(url, key)
    except Exception as e:   # layout must still build offline
        print(f"[supabase] scores unavailable, ordering without them: {e}", file=sys.stderr)
        return None


def main(argv: list[str]) -> None:
    cmd = argv[0] if argv else "push"
    url = os.environ.get("SUPABASE_URL")
    if not url:
        sys.exit("set SUPABASE_URL")
    if cmd == "push":
        key = os.environ.get("SUPABASE_SERVICE_ROLE_KEY") or sys.exit("set SUPABASE_SERVICE_ROLE_KEY")
        doc = json.loads((ROOT / "data" / "projects.json").read_text(encoding="utf-8"))
        n = push_projects(url, key, project_rows(doc))
        print(f"upserted {n} projects", file=sys.stderr)
    elif cmd == "scores":
        key = os.environ.get("SUPABASE_ANON_KEY") or os.environ.get("SUPABASE_SERVICE_ROLE_KEY") or sys.exit("set SUPABASE_ANON_KEY")
        rows = sorted(fetch_scores(url, key).values(), key=lambda r: -(r["bayes_score"] or 0))
        for r in rows[:25]:
            print(f"{r['bayes_score']:.3f}  {r['rating_count']:4d}×  avg {r['avg_stars'] or 0:.2f}  {r['project_id']}")
    else:
        sys.exit(f"unknown command {cmd}; use push | scores")


if __name__ == "__main__":
    main(sys.argv[1:])
