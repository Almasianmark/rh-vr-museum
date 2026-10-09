#!/usr/bin/env python3
"""Live check of a Supabase ratings backend, making the same HTTP calls as the museum's RatingsClient.cs.

    python supabase/smoke.py --url https://<ref>.supabase.co --key <anon or publishable key> [--project <id>]

Steps (each prints ok/FAIL):
  1. anonymous sign-up            POST /auth/v1/signup              (fails if anonymous sign-ins are off)
  2. token refresh                POST /auth/v1/token?grant_type=refresh_token
  3. scores as a visitor          POST /rest/v1/rpc/project_scores  (needs projects loaded: supabase_sync push)
  4. rate 4 stars, then 2         POST /rest/v1/rpc/rate_project    (second vote replaces the first)
  5. read own votes               GET  /rest/v1/ratings?select=project_id,stars
  6. RLS: anon key alone can't read votes or rate
  7. clear the test vote          POST /rest/v1/rpc/clear_rating    (leaves no trace in the scores)
Exit code 0 only if every step passed. Standard library only.
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request


class Client:
    def __init__(self, url: str, key: str):
        self.url, self.key, self.token = url.rstrip("/"), key, None

    def call(self, method: str, path: str, body=None, session: bool = True):
        # Same headers as RatingsClient.Request(): apikey always; Bearer = session token, else the key itself.
        bearer = self.token if session and self.token else self.key
        req = urllib.request.Request(self.url + path, method=method,
                                     data=None if body is None else json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json", "apikey": self.key,
                                              "Authorization": f"Bearer {bearer}"})
        try:
            with urllib.request.urlopen(req, timeout=15) as r:
                text = r.read().decode()
                return r.status, json.loads(text) if text.strip() else None
        except urllib.error.HTTPError as e:
            text = e.read().decode(errors="replace")
            try:
                return e.code, json.loads(text)
            except ValueError:
                return e.code, text


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", required=True)
    ap.add_argument("--key", required=True, help="anon (legacy JWT) or publishable key; never the service/secret key")
    ap.add_argument("--project", help="project id to vote on (default: first one returned by project_scores)")
    a = ap.parse_args(argv)
    if a.key.startswith("sb_secret_") or '"service_role"' in a.key:
        print("refusing: that is a server-side key; use the anon/publishable key the headset ships with")
        return 2

    c, failures = Client(a.url, a.key), 0

    def check(name: str, ok: bool, detail="") -> bool:
        nonlocal failures
        failures += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {name}" + (f"  ({detail})" if detail and not ok else ""))
        return ok

    code, s = c.call("POST", "/auth/v1/signup", {"data": {}}, session=False)
    if not check("1 anonymous sign-up", code < 300 and isinstance(s, dict) and s.get("access_token"),
                 f"{code} {s}; Authentication > Sign In / Providers > Allow anonymous sign-ins"):
        return 1
    c.token, refresh = s["access_token"], s["refresh_token"]

    code, s = c.call("POST", "/auth/v1/token?grant_type=refresh_token", {"refresh_token": refresh}, session=False)
    check("2 token refresh", code < 300 and isinstance(s, dict) and bool(s.get("access_token")), f"{code} {s}")
    if code < 300:
        c.token = s["access_token"]

    code, scores = c.call("POST", "/rest/v1/rpc/project_scores", {})
    have = code < 300 and isinstance(scores, list) and len(scores) > 0
    if not check("3 project_scores", have, f"{code} {scores if not isinstance(scores, list) else 'no projects; run supabase_sync push'}"):
        return 1
    pid = a.project or scores[0]["project_id"]
    before = next((x for x in scores if x["project_id"] == pid), {})

    code1, _ = c.call("POST", "/rest/v1/rpc/rate_project", {"p_project_id": pid, "p_stars": 4})
    code2, _ = c.call("POST", "/rest/v1/rpc/rate_project", {"p_project_id": pid, "p_stars": 2})
    check("4 rate_project (4 then 2)", code1 < 300 and code2 < 300, f"{code1}/{code2}")

    code, mine = c.call("GET", "/rest/v1/ratings?select=project_id,stars")
    check("5 own votes", code < 300 and mine == [{"project_id": pid, "stars": 2}], f"{code} {mine}")

    _, after = c.call("POST", "/rest/v1/rpc/project_scores", {})
    row = next((x for x in after if x["project_id"] == pid), {}) if isinstance(after, list) else {}
    check("  score counts the vote once", row.get("rating_count") == (before.get("rating_count") or 0) + 1, f"{before} -> {row}")

    anon = Client(a.url, a.key)
    code_r, rows = anon.call("GET", "/rest/v1/ratings?select=project_id,stars")
    code_w, _ = anon.call("POST", "/rest/v1/rpc/rate_project", {"p_project_id": pid, "p_stars": 5})
    check("6 RLS: key alone reads no votes and can't rate", (code_r >= 300 or rows == []) and code_w >= 300,
          f"read {code_r} {rows}, rate {code_w}")

    code, _ = c.call("POST", "/rest/v1/rpc/clear_rating", {"p_project_id": pid})
    _, final = c.call("POST", "/rest/v1/rpc/project_scores", {})
    row = next((x for x in final if x["project_id"] == pid), {}) if isinstance(final, list) else {}
    check("7 clear_rating leaves no trace", code < 300 and (row.get("rating_count") or 0) == (before.get("rating_count") or 0),
          f"{code} {before} -> {row}")

    print("ALL CHECKS PASSED" if failures == 0 else f"{failures} CHECK(S) FAILED")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
