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
