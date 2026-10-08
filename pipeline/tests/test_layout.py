from rhm.layout import build_layout, launch_target, museum_doc, split_balanced, youtube_watch_url


def P(i, platform="quest", fidelity="Native", year=2024, winner=False, **kw):
    return {"id": f"p{i}", "year": year, "title": f"T{i:02d}", "platform": platform, "fidelity": fidelity,
            "winner": winner, "tagline": "", "synopsis": "", "requirement_tier": "quest2",
            "extra_hardware": [], "license_gate": "ok", "links": {}, **kw}


def test_split_balanced():
    assert [len(r) for r in split_balanced(list(range(12)))] == [4, 4, 4]
    assert [len(r) for r in split_balanced(list(range(5)))] == [5]
    assert [len(r) for r in split_balanced(list(range(6)))] == [3, 3]
    assert split_balanced([]) == []


def test_device_groups_capped_and_archive_uncapped():
    ps = [P(i) for i in range(12)] + [P(100 + i, "snap", "Watch") for i in range(9)] + [P(200, "pcvr", "Ported")]
    ex = build_layout(ps)["wings"][0]["exhibits"]
    dev = [e for e in ex if e["kind"] == "device"]
    assert all(len(e["project_ids"]) <= 5 for e in dev)
    assert [e["device"] for e in dev] == ["quest"] * 3 + ["pcvr"]
    assert ex[-1]["kind"] == "archive" and len(ex[-1]["project_ids"]) == 9
    assert sorted(pid for e in ex for pid in e["project_ids"]) == sorted(p["id"] for p in ps)


def test_simulated_hardware_projects_stay_in_device_rooms():
    ex = build_layout([P(1, "hololens", "Simulated")])["wings"][0]["exhibits"]
    assert ex[0]["device"] == "hololens"


def test_winners_first_and_one_wing_per_year():
    ps = [P(1), P(2, winner=True), P(3, year=2020)]
    wings = build_layout(ps)["wings"]
    assert [w["year"] for w in wings] == [2020, 2024]
    assert wings[1]["exhibits"][0]["project_ids"] == ["p2", "p1"]


def test_launch_targets_and_video_urls():
    assert youtube_watch_url("https://www.youtube.com/embed/_uO0AcD545Y?enablejsapi=1") == "https://www.youtube.com/watch?v=_uO0AcD545Y"
    assert youtube_watch_url("https://player.vimeo.com/video/123?x=1") == "https://vimeo.com/123"
    assert launch_target(P(1, "webxr", "Browser", links={"web": ["https://x.glitch.me"]})) == {"kind": "browser", "url": "https://x.glitch.me"}
    assert launch_target(P(1, "horizon-worlds", links={"horizon": ["https://horizon.meta.com/world/1"]}))["kind"] == "horizon"
    assert launch_target(P(1, video_url="https://youtu.be/abcdefg"))["url"].endswith("v=abcdefg")


def test_museum_doc_every_project_has_exhibit():
    doc = {"generated_at": "x", "projects": [P(1), P(2, "snap", "Watch")]}
    m = museum_doc(doc)
    assert {p["exhibit"] for p in m["projects"]} == {"2024-quest-1", "2024-archive"}


def test_bayesian_order_within_device_group():
    ps = [P(1, winner=True), P(2), P(3)]
    scores = {"p3": {"bayes_score": 4.2, "rating_count": 9, "avg_stars": 4.4}, "p2": {"bayes_score": 3.9}}
    ex = build_layout(ps, scores=scores)["wings"][0]["exhibits"]
    assert ex[0]["project_ids"] == ["p3", "p2", "p1"]
    assert build_layout(ps)["wings"][0]["exhibits"][0]["project_ids"] == ["p1", "p2", "p3"]   # unrated: winners first
    m = museum_doc({"generated_at": "x", "projects": ps}, scores=scores)
    assert next(p for p in m["projects"] if p["id"] == "p3")["rating_count"] == 9


def test_supabase_sync_requests():
    import json
    from rhm import supabase_sync as ss

    class Resp:
        def __init__(self, code, body=None): self.status_code, self._b, self.text = code, body, ""
        def json(self): return self._b

    class Sess:
        calls = []
        def post(self, url, data=None, headers=None, timeout=None):
            self.calls.append((url, json.loads(data), headers))
            if url.endswith("/rpc/project_scores"):
                return Resp(200, [{"project_id": "p1", "rating_count": 2, "avg_stars": 4.5, "bayes_score": 3.9}])
            return Resp(201)

    s = Sess()
    rows = [{"id": f"p{i}"} for i in range(450)]
    assert ss.push_projects("https://x.supabase.co/", "svc", rows, session=s) == 450
    assert len([c for c in s.calls if "projects" in c[0]]) == 3                  # batches of 200
    url, body, headers = s.calls[0]
    assert url == "https://x.supabase.co/rest/v1/projects?on_conflict=id"
    assert "merge-duplicates" in headers["Prefer"] and headers["Authorization"] == "Bearer svc"
    assert ss.fetch_scores("https://x.supabase.co", "anon", session=s)["p1"]["bayes_score"] == 3.9


def test_apps_from_port_status(tmp_path):
    import json
    from rhm.layout import load_apps
    st = tmp_path / "port_status.json"
    st.write_text(json.dumps({"ports": {
        "p1": {"state": "ready", "package_id": "world.realityhack.p2024.a", "apk_sha256": "ab", "apk_bytes": 123, "recipe": "native"},
        "p2": {"state": "built", "package_id": "world.realityhack.p2024.b", "apk_sha256": "cd"},
        "p3": {"state": "triage:crash", "package_id": "c", "apk_sha256": "ef"},
    }}))
    apps = load_apps(st, base_url="https://x/rel/")
    assert list(apps) == ["p1"] and apps["p1"]["apk_url"] == "https://x/rel/world.realityhack.p2024.a.apk"
    assert set(load_apps(st, base_url="https://x/", include_untested=True)) == {"p1", "p2"}
    m = museum_doc({"generated_at": "x", "projects": [P(1), P(2)]}, apps={"p1": apps["p1"]})
    by = {p["id"]: p for p in m["projects"]}
    assert by["p1"]["launch"]["kind"] == "app" and by["p1"]["app"]["sha256"] == "ab"
    assert by["p2"]["app"] is None and by["p2"]["launch"]["kind"] == "theater"


def test_display_text_strips_emoji_keeps_cjk():
    from rhm.layout import display_text
    assert display_text("Hello 👋🏽 world ✨ VR") == "Hello world VR"
    assert display_text("茶道 XR ☕️") == "茶道 XR"
    assert display_text("🇺🇸 Flag") == "Flag"
    assert display_text(None) == ""
