import json

import pytest

from rhm import factory as f


def P(i, platform="quest", fidelity="Native", winner=False, unity="2022.3.10f1", lic="ok", **kw):
    return {"id": f"2024-proj-{i}", "year": 2024, "title": f"Proj {i}", "platform": platform, "fidelity": fidelity,
            "winner": winner, "license_gate": lic, "license": "MIT", "team": ["A", "B"], "extra_hardware": [],
            "repo": {"url": f"https://github.com/x/p{i}", "exists": True, "empty": False, "unity_version": unity,
                     "manifest_paths": ["Packages/manifest.json"]}, **kw}


def test_candidates_need_repo_unity_and_license():
    assert f.is_candidate(P(1))
    assert not f.is_candidate(P(1, lic="needs-permission"))
    assert not f.is_candidate(P(1, fidelity="Watch"))
    p = P(1); p["repo"]["manifest_paths"] = []
    assert not f.is_candidate(p)


def test_rank_and_quotas():
    ps = [P(i) for i in range(30)] + [P(100, "pcvr", "Ported"), P(101, "hololens", "Ported"), P(102, winner=True)]
    wave = f.select_wave(ps, size=10, quotas={"openxr": 1, "passthrough": 1})
    ids = [p["id"] for p in wave]
    assert len(wave) == 10 and ids[0] == "2024-proj-102"                         # winner first
    assert "2024-proj-100" in ids and "2024-proj-101" in ids                    # quotas honored
    scored = f.select_wave(ps, size=3, scores={"2024-proj-7": {"bayes_score": 4.8}}, quotas={})
    assert scored[0]["id"] == "2024-proj-7"                                     # ratings beat everything


def test_recipes_and_package_ids():
    assert f.recipe_for(P(1)) == "native"
    assert f.recipe_for(P(1, "pcvr")) == "openxr"
    assert f.recipe_for(P(1, "magic-leap")) == "passthrough"
    assert f.package_id({"id": "2024-x", "year": 2024, "title": "A \"Fire\" Training App"}) == "world.realityhack.p2024.a_fire_training_app"
    assert f.package_id({"id": "2020-x", "year": 2020, "title": "#³"}) == "world.realityhack.p2020.p_3"
    assert f.package_id({"id": "2026-x", "year": 2026, "title": "OurLife : A Second Brain"}) == "world.realityhack.p2026.ourlife"


def test_project_path_prefers_shallow_non_backup():
    assert f.project_path(["old/Packages/manifest.json", "Game/Packages/manifest.json"]) == "Game"
    assert f.project_path(["Packages/manifest.json", "sub/Packages/manifest.json"]) == "."


class FakeResolver(f.Resolver):
    def __init__(self, registry):
        self._pkgs = {k: {"versions": v} for k, v in registry.items()}

    def image(self, unity):
        return f"unityci/editor:ubuntu-{unity}-android-3"


REG = {
    "com.unity.xr.openxr": {"1.13.0": {"unity": "2021.3"}, "1.17.1": {"unity": "2022.3"}, "1.18.0": {"unity": "6000.0"},
                            "1.19.0-pre.1": {"unity": "2022.3"}},
    "com.unity.xr.management": {"4.5.2": {"unity": "2019.4"}, "4.7.0": {"unity": "2022.3"}},
    "com.unity.xr.oculus": {"1.9.1": {"unity": "2019.4"}, "4.2.0": {"unity": "2021.3"}},
    "com.unity.xr.arfoundation": {"5.2.2": {"unity": "2021.2"}},
    "com.unity.xr.meta-openxr": {"1.0.4": {"unity": "2022.3", "dependencies": {"com.unity.xr.arfoundation": "5.1.5",
                                                                                "com.unity.xr.core-utils": "2.2.3",
                                                                                "com.unity.ugui": "1.0.0"}}},
}


def test_resolver_picks_highest_compatible_release():
    r = FakeResolver(REG)
    assert r.package("com.unity.xr.openxr", "2022.3.18f1") == "1.17.1"            # skips 6000-only and pre-release
    assert r.package("com.unity.xr.openxr", "6000.0.34f1") == "1.18.0"
    assert f.xr_packages("native", "2019.4.1f1", r) == {"com.unity.xr.management": "4.5.2", "com.unity.xr.oculus": "1.9.1"}
    add = f.xr_packages("passthrough", "2022.3.18f1", r)
    assert add["com.unity.xr.meta-openxr"] == "1.0.4"
    need = f.min_versions(add, r)
    assert need["com.unity.xr.core-utils"] == "2.2.3" and "com.unity.ugui" not in need


def _project(tmp_path, deps):
    proj = tmp_path / "proj"
    (proj / "Packages").mkdir(parents=True)
    (proj / "Assets").mkdir()
    (proj / "Packages" / "manifest.json").write_text(json.dumps({"dependencies": deps}))
    (proj / "LICENSE").write_text("MIT License\nPermission is hereby granted, free of charge")
    return proj


def _entry(recipe, add, need=None):
    return {"id": "2024-proj-1", "title": "Proj", "year": 2024, "repo_url": "https://github.com/x/p", "recipe": recipe,
            "license": "MIT", "team": ["Ada"], "add_packages": add, "min_versions": need or add}


def test_inject_passthrough_strips_adds_upgrades(tmp_path):
    proj = _project(tmp_path, {"com.unity.xr.magicleap": "6.0.0", "com.unity.xr.arfoundation": "5.0.0",
                               "com.unity.textmeshpro": "3.0.6"})
    ch = f.inject(proj, _entry("passthrough", {"com.unity.xr.openxr": "1.17.1", "com.unity.xr.meta-openxr": "1.0.4"},
                               {"com.unity.xr.openxr": "1.17.1", "com.unity.xr.meta-openxr": "1.0.4", "com.unity.xr.arfoundation": "5.1.5"}))
    deps = json.loads((proj / "Packages" / "manifest.json").read_text())["dependencies"]
    assert "com.unity.xr.magicleap" not in deps and ch["removed"] == ["com.unity.xr.magicleap"]
    assert deps["com.unity.xr.openxr"] == "1.17.1" and deps["com.unity.xr.arfoundation"] == "5.1.5"
    assert ch["upgraded"] == {"com.unity.xr.arfoundation": "5.0.0 -> 5.1.5"}
    assert (proj / "Packages" / "com.realityhack.museum-kit" / "package.json").exists()
    cfg = json.loads((proj / "Assets/RHKitGenerated/Resources/rhkit.json").read_text())
    assert cfg["passthrough"] and cfg["museumPackage"] == "world.realityhack.museum" and cfg["team"] == "Ada"
    notice = (proj / "Assets/RHKitGenerated/NOTICE.txt").read_text()
    assert "Proj (2024) by Ada" in notice and "Permission is hereby granted" in notice


def test_inject_native_keeps_existing_quest_loader(tmp_path):
    proj = _project(tmp_path, {"com.meta.xr.sdk.all": "83.0.1", "com.unity.xr.openxr": "1.10.0", "com.unity.xr.windowsmr": "5.0.0"})
    ch = f.inject(proj, _entry("native", {"com.unity.xr.management": "4.7.0", "com.unity.xr.openxr": "1.17.1"}))
    deps = json.loads((proj / "Packages" / "manifest.json").read_text())["dependencies"]
    assert deps["com.unity.xr.openxr"] == "1.10.0"            # not bumped, not duplicated
    assert "com.unity.xr.windowsmr" in deps                   # native recipe doesn't strip
    assert ch["added"] == {"com.unity.xr.management": "4.7.0"}


LOG_OK = "\n".join(f"10-04 12:00:{i:02d} I VrApi   : FPS={72 if i > 5 else 30}/72,Prd=45ms" for i in range(40))


@pytest.mark.parametrize("log,alive,verdict", [
    (LOG_OK, True, "pass"),
    (LOG_OK.replace("FPS=72", "FPS=48"), True, "perf"),
    (LOG_OK + "\nE AndroidRuntime: FATAL EXCEPTION: main", True, "crash"),
    (LOG_OK, False, "crash"),
    (LOG_OK + "\nE ActivityManager: ANR in world.realityhack.p2024.x", True, "anr"),
    ("nothing useful", True, "no-fps-data"),
])
def test_analyze_logcat(log, alive, verdict):
    r = f.analyze_logcat(log, alive)
    assert r["verdict"] == verdict
    if verdict == "pass":
        assert r["fps_median"] == 72                          # loading-time 30s are skipped


def test_triage_states(tmp_path, monkeypatch):
    q = tmp_path / "queue.json"
    q.write_text(json.dumps({"entries": [_entry("native", {}) | {"rank": 1, "package_id": "a", "manual_steps": []},
                                         _entry("native", {}) | {"id": "2024-b", "rank": 2, "package_id": "b", "manual_steps": []}]}))
    reports = tmp_path / "factory"
    (reports / "reports" / "2024-proj-1").mkdir(parents=True)
    (reports / "reports" / "2024-proj-1" / "smoke.json").write_text(json.dumps({"verdict": "pass", "apk_sha256": "ab", "fps_median": 72}))
    (reports / "reports" / "2024-b").mkdir(parents=True)
    (reports / "reports" / "2024-b" / "build-report.json").write_text(json.dumps({"exitCode": 3, "result": "Failed"}))
    monkeypatch.setattr(f, "QUEUE_JSON", q)
    monkeypatch.setattr(f, "STATUS_JSON", tmp_path / "status.json")
    monkeypatch.setattr(f, "FACTORY", reports)
    st = f.triage(None)
    assert st["2024-proj-1"]["state"] == "ready" and st["2024-proj-1"]["apk_sha256"] == "ab"
    assert st["2024-b"]["state"] == "triage:build-Failed"


def test_quest3_only_ranks_after_quest2_and_blocked_are_skipped(monkeypatch):
    ps = [P(1, requirement_tier="quest3", unity="6000.0.1f1"), P(2, requirement_tier="quest2"), P(3)]
    monkeypatch.setattr(f, "blocked", lambda: {"2024-proj-3": "LFS missing"})
    ids = [p["id"] for p in f.select_wave(ps, size=5, quotas={})]
    assert ids == ["2024-proj-2", "2024-proj-1"]          # newer Unity no longer lifts a Quest 3-only port


def _git_repo(tmp_path, files):
    import subprocess
    d = tmp_path / "r"
    d.mkdir()
    subprocess.run(["git", "init", "-q", str(d)], check=True)
    for name, data in files.items():
        p = d / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(data)
    return d


def test_lfs_missing_ignores_files_android_builds_skip(tmp_path, monkeypatch):
    listing = ("1111111111 - Assets/Models/tree.fbx\n"
               "2222222222 * Assets/Audio/ok.wav\n"
               "3333333333 - Assets/Simple FX Kit/Documentation - FX.pdf\n"
               "4444444444 - Packages/tflite/Plugins/Linux/arm64/lib.so\n"
               "5555555555 * Assets/UI/Button - Circular BG.png\n")
    monkeypatch.setattr(f.subprocess, "run", lambda *a, **k: type("R", (), {"stdout": listing, "returncode": 0})())
    assert f.lfs_missing(tmp_path) == ["Assets/Models/tree.fbx"]


def test_uses_lfs(tmp_path):
    assert not f.uses_lfs(_git_repo(tmp_path, {"a.txt": "x"}))
    (tmp_path / "r" / ".gitattributes").write_text("*.fbx filter=lfs diff=lfs merge=lfs -text\n")
    assert f.uses_lfs(tmp_path / "r")
