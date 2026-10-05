import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from rh_companion import Companion  # noqa: E402

APK = b"fake apk bytes"
SHA = hashlib.sha256(APK).hexdigest()


class FakeQuest:
    """Just enough adb: a file system for the companion dir + a package list."""
    def __init__(self, requests=None, installed=()):
        self.files = {}
        self.packages = set(installed) | {"com.oculus.browser"}
        if requests is not None:
            self.files["/c/requests.json"] = json.dumps(requests)
        self.calls = []

    def __call__(self, *args):
        self.calls.append(args)
        if args[:3] == ("shell", "pm", "list"):
            return 0, "\n".join(f"package:{p}" for p in sorted(self.packages))
        if args[:2] == ("shell", "cat"):
            return (0, self.files[args[2]]) if args[2] in self.files else (1, "No such file")
        if args[:2] == ("shell", "mkdir"):
            return 0, ""
        if args[0] == "push":
            self.files[args[2]] = Path(args[1]).read_text()
            return 0, "1 file pushed"
        if args[0] == "install":
            assert args[1:3] == ("-r", "-g")                       # silent + grant runtime permissions
            assert hashlib.sha256(Path(args[3]).read_bytes()).hexdigest() == SHA
            self.packages.add(Path(args[3]).name.split("-")[0])
            return 0, "Performing Streamed Install\nSuccess"
        if args[0] == "uninstall":
            self.packages.discard(args[1])
            return 0, "Success"
        raise AssertionError(args)


def fetch_ok(url, dest):
    Path(dest).write_bytes(APK)


def app(pkg, sha=SHA, gb=1):
    return {"package": pkg, "apk_url": f"https://x/{pkg}.apk", "sha256": sha, "bytes": gb << 30, "project_id": pkg}


def comp(quest, tmp_path, fetch=fetch_ok):
    return Companion(adb=quest, remote_dir="/c", fetch=fetch, cache=tmp_path, log=lambda *_: None)


def test_serves_request_installs_uninstalls_and_reports(tmp_path):
    q = FakeQuest({"seq": 3, "install": [app("world.realityhack.p2024.a")], "uninstall": ["world.realityhack.p2020.old"]},
                  installed={"world.realityhack.p2020.old"})
    c = comp(q, tmp_path)
    assert c.serve_once(-1) == 3
    assert "world.realityhack.p2024.a" in q.packages and "world.realityhack.p2020.old" not in q.packages
    st = json.loads(q.files["/c/status.json"])
    assert st["seq_done"] == 3 and st["installed"] == ["world.realityhack.p2024.a"] and "installed 1/1" in st["message"]
    n = len(q.calls)
    assert c.serve_once(3) == 3 and not any(a[0] == "install" for a in q.calls[n:])   # same seq is not re-served


def test_sha_mismatch_never_reaches_headset(tmp_path):
    q = FakeQuest({"seq": 1, "install": [app("world.realityhack.p2024.bad", sha="0" * 64)], "uninstall": []})
    comp(q, tmp_path).serve_once(-1)
    assert not any(a[0] == "install" for a in q.calls)
    assert "installed 0/1" in json.loads(q.files["/c/status.json"])["message"]


def test_refuses_to_uninstall_foreign_packages(tmp_path):
    q = FakeQuest({"seq": 1, "install": [], "uninstall": ["com.oculus.browser"]})
    comp(q, tmp_path).serve_once(-1)
    assert "com.oculus.browser" in q.packages


def test_cached_apk_is_reused(tmp_path):
    calls = []
    def counting_fetch(url, dest):
        calls.append(url)
        fetch_ok(url, dest)
    q = FakeQuest({"seq": 1, "install": [app("world.realityhack.p2024.a")], "uninstall": []})
    c = comp(q, tmp_path, counting_fetch)
    c.serve_once(-1)
    q.packages.discard("world.realityhack.p2024.a")
    q.files["/c/requests.json"] = json.dumps({"seq": 2, "install": [app("world.realityhack.p2024.a")], "uninstall": []})
    c.serve_once(1)
    assert len(calls) == 1


def test_preload_best_rated_that_fit():
    museum = {"projects": [
        {"id": "a", "avg_stars": 4.5, "app": app("world.realityhack.p.a", gb=4)},
        {"id": "b", "avg_stars": 4.0, "app": app("world.realityhack.p.b", gb=2)},
        {"id": "c", "avg_stars": 2.0, "app": app("world.realityhack.p.c", gb=1)},
        {"id": "d", "avg_stars": 5.0, "app": None},
    ]}
    pick = Companion.preload_selection(museum, budget_bytes=11 << 30, installed=set())
    # footprints 6.4 + 3.2 + 1.6 GB = 11.2 > 11 -> c doesn't fit after a and b
    assert [a["project_id"] for a in pick] == ["a", "b"]
    assert [a["project_id"] for a in Companion.preload_selection(museum, 12 << 30, {"world.realityhack.p.a"})] == ["b", "c"]   # 6.4 used + 3.2 + 1.6 = 11.2 <= 12
