#!/usr/bin/env python3
"""Finish setting up the ratings backend after the three dashboard steps in supabase/README.md.

    python supabase/setup.py --url https://<ref>.supabase.co --key <publishable or anon key> --service-key <secret key>

1. checks the migration is applied (project_scores exists)
2. loads all projects from data/projects.json (service key; used here only, never stored)
3. writes data/backend.json with the URL and the public key
4. rebuilds data/museum.json, which now carries a "ratings" block: installed museums switch ratings on
   the next time they fetch museum.json from main (no rebuild)
5. runs supabase/smoke.py: anonymous sign-in, rate, RLS, clear

Then commit and push data/backend.json and data/museum.json.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "pipeline"))


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", required=True, help="https://<project-ref>.supabase.co")
    ap.add_argument("--key", required=True, help="publishable (sb_publishable_...) or legacy anon key: ships in the app")
    ap.add_argument("--service-key", required=True, help="secret (sb_secret_...) or legacy service_role key: stays here")
    a = ap.parse_args(argv)
    url = a.url.rstrip("/")
    if a.key.startswith("sb_secret_") or a.key == a.service_key:
        print("--key must be the public key; the secret key would let anyone write the database")
        return 2

    import requests
    from rhm import supabase_sync
    from rhm.layout import write_museum

    # 1. migration
    r = requests.post(f"{url}/rest/v1/rpc/project_scores", data="{}", timeout=30,
                      headers={"apikey": a.key, "Authorization": f"Bearer {a.key}", "Content-Type": "application/json"})
    if r.status_code == 404 or "Could not find the function" in r.text:
        print("1 migration: NOT APPLIED. Paste supabase/migrations/20261004000000_ratings.sql into the SQL Editor and run it.")
        return 1
    if r.status_code >= 300:
        print(f"1 migration: unexpected {r.status_code}: {r.text[:200]}")
        return 1
    print("1 migration: ok")

    # 2. projects
    doc = json.loads((ROOT / "data" / "projects.json").read_text(encoding="utf-8"))
    n = supabase_sync.push_projects(url, a.service_key, supabase_sync.project_rows(doc))
    print(f"2 projects: {n} upserted")

    # 3. backend.json
    supabase_sync.BACKEND_JSON.write_text(json.dumps({
        "_note": "Public ratings backend for the museum client (embedded in museum.json). The key is the anon/"
                 "publishable key: public by design, RLS protects the data. Written by supabase/setup.py.",
        "url": url, "key": a.key}, indent=2) + "\n", encoding="utf-8")
    print(f"3 wrote {supabase_sync.BACKEND_JSON.relative_to(ROOT)}")

    # 4. museum.json with the ratings block (and live scores for ordering)
    m = write_museum(doc, ROOT / "data" / "museum.json")
    print(f"4 data/museum.json rebuilt: ratings -> {m.get('ratings', {}).get('url')}")

    # 5. live check
    print("5 smoke test:")
    code = subprocess.call([sys.executable, str(ROOT / "supabase" / "smoke.py"), "--url", url, "--key", a.key])
    if code == 0:
        print("\nDone. Commit and push data/backend.json and data/museum.json; headsets pick it up on next launch.")
    return code


if __name__ == "__main__":
    sys.exit(main())
