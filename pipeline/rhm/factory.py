"""Port factory (build-order step 4).

    python -m rhm.factory queue [--size 20]      # rank candidates -> data/port_queue.json + factory/QUEUE.md
    python -m rhm.factory prepare <id> [--work DIR]   # clone + inject kit/manifest/config/NOTICE, print build cmd
    python -m rhm.factory smoke <id> --apk APK   # real Quest 2 over adb: install, launch, 60 s logcat, FPS, screenshot
    python -m rhm.factory triage                 # merge build/smoke reports -> data/port_status.json + factory/TRIAGE.md

Builds run in GameCI containers (factory/docker_build.sh locally; factory/port-factory.yml -> .github/workflows/ for CI).
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import shutil
import statistics
import subprocess
import sys
import time
from pathlib import Path

from .http import PoliteSession

ROOT = Path(__file__).resolve().parents[2]
KIT = ROOT / "kit" / "com.realityhack.museum-kit"
FACTORY = ROOT / "factory"
QUEUE_JSON = ROOT / "data" / "port_queue.json"
STATUS_JSON = ROOT / "data" / "port_status.json"
MUSEUM_PACKAGE = "world.realityhack.museum"

# ---------------------------------------------------------------- recipes

RECIPES = {
    # recipe: (platforms, ease rank, manual steps for the triage queue)
    "native": (("quest", "horizon-worlds"), 0, []),
    "openxr": (("pcvr", "android-xr", "unreal"), 1, [
        "Check the camera rig follows the headset (SteamVR/WMR rigs may need swapping for an XR Origin).",
        "Mobile perf pass if FPS < 65: URP mobile settings, no post-processing, fixed foveation."]),
    "passthrough": (("phone-ar", "hololens", "magic-leap", "ar-glasses", "vision-pro"), 2, [
        "Replace the AR/HMD rig with an XR Origin; map screen taps / air-taps to trigger or pinch.",
        "Check content reads well on passthrough (Quest 2 passthrough is grayscale).",
        "Image/marker tracking: add a hand-placed anchor (no camera access on Quest 2)."]),
}
SIM_STEPS = ["Custom hardware: wire the simulated wrist panel / controller mapping for the device's inputs."]

# XR plugins that can't run on Quest/Android and break or confuse Android builds.
STRIP_PACKAGES = re.compile(
    r"^(com\.unity\.xr\.windowsmr|com\.valvesoftware\.unity\.openvr|com\.unity\.xr\.openvr.*|com\.htc\.upm\.vive.*"
    r"|com\.unity\.xr\.magicleap|com\.magicleap\..*|com\.microsoft\.mixedreality\.openxr|com\.unity\.xr\.windowsmr\.metro"
    r"|com\.unity\.xr\.arkit.*|com\.unity\.xr\.visionos|com\.unity\.polyspatial.*)$")


def recipe_for(p: dict) -> str:
    for name, (platforms, _, _) in RECIPES.items():
        if p["platform"] in platforms:
            return name
    return "native"


def unity_minor(v: str) -> tuple[int, int]:
    m = re.match(r"(\d+)\.(\d+)", v or "")
    return (int(m.group(1)), int(m.group(2))) if m else (0, 0)


def version_key(v: str) -> tuple:
    """2022.3.13f1 -> (2022, 3, 13, 'f', 1); semver 1.13.2 -> (1, 13, 2)."""
    return tuple(int(x) if x.isdigit() else x for x in re.findall(r"\d+|[a-z]+", v))


def package_id(p: dict) -> str:
    from .text import ascii_fold
    slug = re.sub(r"[^a-z0-9]+", "_", ascii_fold(p["title"]).lower().split(":")[0]).strip("_") or "app"
    if not slug[0].isalpha():
        slug = "p_" + slug
    return f"world.realityhack.p{p['year']}.{slug[:40]}"


def is_candidate(p: dict) -> bool:
    r = p.get("repo") or {}
    return bool(r.get("exists") and not r.get("empty") and r.get("manifest_paths") and r.get("unity_version")
                and p["license_gate"] in ("ok", "ok-copyleft")
                and p["fidelity"] in ("Native", "Ported", "Ported-reduced", "Simulated"))


def project_path(manifest_paths: list[str]) -> str:
    """Dir holding the Unity project: the shallowest Packages/manifest.json outside backup/Library dirs."""
    good = [m for m in manifest_paths if not re.search(r"(^|/)(Library|Temp|Builds?|old|backup)/", m, re.I)] or manifest_paths
    best = min(good, key=lambda m: (m.count("/"), m))
    return best[: -len("Packages/manifest.json")].rstrip("/") or "."


def rank_key(p: dict, scores: dict[str, dict] | None = None) -> tuple:
    s = (scores or {}).get(p["id"], {})
    ease = RECIPES[recipe_for(p)][1] + (2 if p["fidelity"] == "Simulated" else 0)
    return (-(s.get("bayes_score") or 0), not p["winner"], ease, tuple(-x for x in unity_minor(p["repo"]["unity_version"])), p["title"].lower())


def select_wave(projects: list[dict], size: int = 20, scores: dict | None = None,
                quotas: dict[str, int] | None = None) -> list[dict]:
    """Top projects by score/winner/ease, with small per-recipe quotas so wave 1 exercises every recipe."""
    quotas = quotas if quotas is not None else {"openxr": 3, "passthrough": 3}
    cands = sorted((p for p in projects if is_candidate(p)), key=lambda p: rank_key(p, scores))
    picked, ids = [], set()
    for recipe, n in quotas.items():
        for p in [c for c in cands if recipe_for(c) == recipe][:n]:
            picked.append(p); ids.add(p["id"])
    for p in cands:
        if len(picked) >= size:
            break
        if p["id"] not in ids:
            picked.append(p); ids.add(p["id"])
    return sorted(picked[:size], key=lambda p: rank_key(p, scores))


# ---------------------------------------------------------------- version resolution (registry + Docker Hub)

class Resolver:
    REGISTRY = "https://packages.unity.com"
    HUB = "https://hub.docker.com/v2/repositories/unityci/editor/tags/"

    def __init__(self, http: PoliteSession | None = None):
        self.http = http or PoliteSession()
        self._pkgs: dict[str, dict] = {}

    def package(self, name: str, unity: str) -> str | None:
        """Highest released version of a UPM package whose minimum Unity <= the project's version."""
        if name not in self._pkgs:
            self._pkgs[name] = self.http.get_json(f"{self.REGISTRY}/{name}") or {"versions": {}}
        target = version_key(unity)
        ok = []
        for v, meta in self._pkgs[name]["versions"].items():
            if not re.fullmatch(r"\d+\.\d+\.\d+", v):
                continue
            need = meta.get("unity", "2018.1") + "." + (meta.get("unityRelease") or "0a0")
            if version_key(need) <= target:
                ok.append(v)
        return max(ok, key=version_key) if ok else None

    def dependencies(self, name: str, version: str) -> dict[str, str]:
        if name not in self._pkgs:
            self._pkgs[name] = self.http.get_json(f"{self.REGISTRY}/{name}") or {"versions": {}}
        return (self._pkgs[name]["versions"].get(version) or {}).get("dependencies") or {}

    def image(self, unity: str) -> str | None:
        """unityci/editor Android image for this exact version, else the newest patch of the same minor."""
        exact = self.http.get_json(f"{self.HUB}?name=ubuntu-{unity}-android-3&page_size=10") or {}
        if any(r["name"] == f"ubuntu-{unity}-android-3" for r in exact.get("results", [])):
            return f"unityci/editor:ubuntu-{unity}-android-3"
        major, minor = unity_minor(unity)
        found = self.http.get_json(f"{self.HUB}?name=ubuntu-{major}.{minor}.&page_size=100") or {}
        tags = [r["name"] for r in found.get("results", []) if re.fullmatch(rf"ubuntu-{major}\.{minor}\.\d+f\d+-android-3", r["name"])]
        if not tags:
            return None
        best = max(tags, key=lambda t: version_key(t.split("-")[1]))
        return f"unityci/editor:{best}"


def xr_packages(recipe: str, unity: str, res: Resolver) -> dict[str, str]:
    """Packages the factory adds so the recipe can enable a Quest loader."""
    out = {}
    if unity_minor(unity) >= (2020, 3):
        for name in ("com.unity.xr.management", "com.unity.xr.openxr"):
            v = res.package(name, unity)
            if v:
                out[name] = v
        if recipe == "passthrough":
            for name in ("com.unity.xr.arfoundation", "com.unity.xr.meta-openxr"):
                v = res.package(name, unity)
                if v:
                    out[name] = v
    else:
        # OpenXR needs 2020.3+; 2019 projects get the Oculus XR Plugin.
        for name in ("com.unity.xr.management", "com.unity.xr.oculus"):
            v = res.package(name, unity)
            if v:
                out[name] = v
    return out


def min_versions(add: dict[str, str], res: Resolver) -> dict[str, str]:
    """Lowest versions the added packages need. Unity honors a project's direct pin even when it is
    too old for a dependency, so the injector raises pins below these (e.g. meta-openxr 2.6.1 needs
    arfoundation >= 6.6.0)."""
    need = dict(add)
    for name, version in add.items():
        for dep, v in res.dependencies(name, version).items():
            if dep.startswith("com.unity.modules.") or dep == "com.unity.ugui":
                continue   # built into the editor; never pin these
            if re.fullmatch(r"\d+\.\d+\.\d+", v) and (dep not in need or version_key(v) > version_key(need[dep])):
                need[dep] = v
    return need


def queue_entry(p: dict, rank: int, res: Resolver | None) -> dict:
    recipe = recipe_for(p)
    unity = p["repo"]["unity_version"]
    steps = list(RECIPES[recipe][2]) + (SIM_STEPS if p["fidelity"] == "Simulated" else [])
    if p["fidelity"] == "Ported-reduced":
        steps.append("Quest-3-only feature in use: gate it or provide a Quest 2 fallback.")
    return {
        "rank": rank,
        "id": p["id"],
        "title": p["title"],
        "year": p["year"],
        "winner": p["winner"],
        "platform": p["platform"],
        "fidelity": p["fidelity"],
        "recipe": recipe,
        "repo_url": p["repo"]["url"],
        "project_path": project_path(p["repo"]["manifest_paths"]),
        "unity_version": unity,
        "image": res.image(unity) if res else None,
        "add_packages": (add := xr_packages(recipe, unity, res) if res else {}),
        "min_versions": min_versions(add, res) if res else {},
        "package_id": package_id(p),
        "license": p["license"],
        "team": p.get("team", []),
        "extra_hardware": p["extra_hardware"],
        "manual_steps": steps,
    }


def write_queue(size: int, offline: bool = False) -> list[dict]:
    doc = json.loads((ROOT / "data" / "projects.json").read_text(encoding="utf-8"))
    from .supabase_sync import scores_from_env
    wave = select_wave(doc["projects"], size, scores_from_env())
    res = None if offline else Resolver()
    entries = [queue_entry(p, i + 1, res) for i, p in enumerate(wave)]
    QUEUE_JSON.write_text(json.dumps({"generated_at": _now(), "wave": 1, "entries": entries}, indent=2) + "\n")
    FACTORY.mkdir(exist_ok=True)
    L = ["# Port queue: wave 1", "",
         f"{len(entries)} projects. Ranked by Bayesian rating (none yet) → prize winner → ease (native < OpenXR swap < passthrough "
         "< custom hardware) → newer Unity. Quotas reserve 3 OpenXR and 3 passthrough slots so every recipe gets exercised.", "",
         "| # | project | year | platform → recipe | Unity | image | package |", "|---:|---|---:|---|---|---|---|"]
    for e in entries:
        L.append(f"| {e['rank']} | {'**' if e['winner'] else ''}{e['title']}{'**' if e['winner'] else ''} | {e['year']} | "
                 f"{e['platform']} → `{e['recipe']}` | {e['unity_version']} | `{(e['image'] or 'none').split(':')[-1]}` | `{e['package_id']}` |")
    (FACTORY / "QUEUE.md").write_text("\n".join(L) + "\n")
    return entries


# ---------------------------------------------------------------- prepare (clone + inject)

def _git(*args, cwd=None):
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True)


def inject(project_dir: Path, entry: dict, embed_kit: bool = True) -> dict:
    """Edit a cloned Unity project in place. Returns a change log for the build report."""
    changes = {"added": {}, "removed": [], "upgraded": {}, "kit": None}
    manifest_path = project_dir / "Packages" / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    deps = manifest.setdefault("dependencies", {})

    if entry["recipe"] != "native":
        for name in list(deps):
            if STRIP_PACKAGES.match(name):
                deps.pop(name)
                changes["removed"].append(name)
    # A project that already ships a Quest loader keeps it; adding OpenXR next to Oculus XR / Meta SDK
    # gives two competing loaders.
    has_quest_loader = any(n.startswith(("com.unity.xr.oculus", "com.meta.xr.sdk", "com.oculus")) or n == "com.unity.xr.openxr"
                           for n in deps)
    for name, version in entry.get("add_packages", {}).items():
        if has_quest_loader and name in ("com.unity.xr.openxr", "com.unity.xr.oculus"):
            continue
        if name not in deps:
            deps[name] = version
            changes["added"][name] = version
    for name, need in entry.get("min_versions", {}).items():
        have = deps.get(name)
        if have and re.fullmatch(r"\d+\.\d+\.\d+", have) and version_key(have) < version_key(need) \
                and not (has_quest_loader and name in ("com.unity.xr.openxr", "com.unity.xr.oculus")):
            deps[name] = need
            changes["upgraded"][name] = f"{have} -> {need}"

    if embed_kit:
        # Embedded package: lives inside the project, so the container needs no extra mounts.
        dst = project_dir / "Packages" / KIT.name
        if dst.exists():
            shutil.rmtree(dst)
        shutil.copytree(KIT, dst)
        changes["kit"] = f"embedded Packages/{KIT.name}"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    gen = project_dir / "Assets" / "RHKitGenerated"
    (gen / "Resources").mkdir(parents=True, exist_ok=True)
    team = ", ".join(entry.get("team") or []) or "Reality Hack team"
    cfg = {
        "projectId": entry["id"], "title": entry["title"], "team": team, "year": entry["year"],
        "license": entry.get("license") or "unknown", "repoUrl": entry["repo_url"],
        "museumPackage": MUSEUM_PACKAGE, "passthrough": entry["recipe"] == "passthrough",
    }
    (gen / "Resources" / "rhkit.json").write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")
    lic = next((f for f in project_dir.iterdir() if re.match(r"(license|licence|copying)", f.name, re.I) and f.is_file()), None)
    notice = [f"{entry['title']} ({entry['year']}) by {team}",
              f"Original: {entry['repo_url']}",
              f"License: {entry.get('license')}",
              "Ported to Meta Quest for the Reality Hack VR Museum. Changes: Android/Quest build settings, XR loader,",
              f"museum kit ({KIT.name}). Packages added: {', '.join(changes['added']) or 'none'}; removed: {', '.join(changes['removed']) or 'none'}.",
              ""]
    if lic:
        notice += ["----- original license -----", lic.read_text(encoding="utf-8", errors="replace")]
    (gen / "NOTICE.txt").write_text("\n".join(notice), encoding="utf-8")
    return changes


def prepare(entry: dict, work: Path) -> Path:
    repo_dir = work / entry["id"]
    if not repo_dir.exists():
        _git("clone", "--depth", "1", entry["repo_url"], str(repo_dir))
    commit = _git("rev-parse", "HEAD", cwd=repo_dir).stdout.strip()
    project = (repo_dir / entry["project_path"]).resolve()
    changes = inject(project, entry)
    out = work / "_out" / entry["id"]
    out.mkdir(parents=True, exist_ok=True)
    (out / "prepare.json").write_text(json.dumps({"commit": commit, "project": str(project), **changes}, indent=2))
    return project


def build_command(entry: dict, project: Path, out: Path) -> str:
    return (f"factory/docker_build.sh '{entry['image']}' '{project.resolve()}' '{out.resolve()}' "
            f"'{entry['package_id']}' '{entry['title']}' '{entry['recipe']}'")


# ---------------------------------------------------------------- smoke test (real Quest 2 over adb)

CRASH = re.compile(r"FATAL EXCEPTION|Fatal signal \d+|E CRASH\s|backtrace:|Abort message", re.I)
ANR = re.compile(r"ANR in |am_anr", re.I)
FPS = re.compile(r"FPS=(\d+)/(\d+)")
UNITY_EXC = re.compile(r"Unity\s*:\s*\w*Exception", re.I)


def analyze_logcat(text: str, alive: bool, target_fps: int = 72) -> dict:
    fps = [int(a) for a, _ in FPS.findall(text)]
    median = statistics.median(fps[len(fps) // 4:]) if fps else None    # skip the loading quarter
    crash = bool(CRASH.search(text))
    anr = bool(ANR.search(text))
    if crash or not alive:
        verdict = "crash"
    elif anr:
        verdict = "anr"
    elif median is None:
        verdict = "no-fps-data"
    elif median < 0.9 * target_fps:
        verdict = "perf"
    else:
        verdict = "pass"
    return {"verdict": verdict, "alive_after_60s": alive, "crash": crash, "anr": anr,
            "fps_median": median, "fps_samples": len(fps), "unity_exceptions": len(UNITY_EXC.findall(text))}


def smoke(entry: dict, apk: Path, seconds: int = 60, adb: str = "adb") -> dict:
    out = FACTORY / "reports" / entry["id"]
    out.mkdir(parents=True, exist_ok=True)
    pkg = entry["package_id"]
    run = lambda *a, **k: subprocess.run([adb, *a], capture_output=True, text=True, **k)
    run("logcat", "-c")
    inst = run("install", "-r", "-g", str(apk))
    if inst.returncode != 0:
        res = {"verdict": "install-failed", "detail": (inst.stdout + inst.stderr)[-500:]}
    else:
        run("shell", "monkey", "-p", pkg, "-c", "android.intent.category.LAUNCHER", "1")
        time.sleep(seconds)
        alive = bool(run("shell", "pidof", pkg).stdout.strip())
        log = run("logcat", "-d").stdout
        (out / "logcat.txt").write_text(log)
        with open(out / "screenshot.png", "wb") as f:
            f.write(subprocess.run([adb, "exec-out", "screencap", "-p"], capture_output=True).stdout)
        res = analyze_logcat(log, alive)
        run("shell", "am", "force-stop", pkg)
    res.update({"id": entry["id"], "apk": str(apk), "apk_sha256": hashlib.sha256(apk.read_bytes()).hexdigest(),
                "apk_bytes": apk.stat().st_size, "tested_at": _now()})
    (out / "smoke.json").write_text(json.dumps(res, indent=2))
    return res


# ---------------------------------------------------------------- triage

def triage(work: Path | None = None) -> dict:
    queue = json.loads(QUEUE_JSON.read_text())["entries"]
    status = {}
    for e in queue:
        build = _load(work / "_out" / e["id"] / "build-report.json") if work else None
        build = build or _load(FACTORY / "reports" / e["id"] / "build-report.json")
        sm = _load(FACTORY / "reports" / e["id"] / "smoke.json")
        if sm:
            state = "ready" if sm["verdict"] == "pass" else f"triage:{sm['verdict']}"
        elif build:
            state = "built" if build.get("exitCode") == 0 else f"triage:build-{build.get('result') or build.get('error') or 'failed'}"
        else:
            state = "queued"
        status[e["id"]] = {"state": state, "package_id": e["package_id"], "recipe": e["recipe"],
                           "apk_sha256": (sm or {}).get("apk_sha256"), "fps_median": (sm or {}).get("fps_median"),
                           "manual_steps": e["manual_steps"]}
    STATUS_JSON.write_text(json.dumps({"generated_at": _now(), "ports": status}, indent=2) + "\n")
    L = ["# Port triage", "", "| # | project | recipe | state | FPS | manual steps |", "|---:|---|---|---|---:|---|"]
    for e in queue:
        s = status[e["id"]]
        L.append(f"| {e['rank']} | {e['title']} | `{e['recipe']}` | {s['state']} | {s['fps_median'] or ''} | "
                 f"{'<br>'.join(e['manual_steps']) or '—'} |")
    (FACTORY / "TRIAGE.md").write_text("\n".join(L) + "\n")
    return status


def _load(p: Path):
    return json.loads(p.read_text()) if p and p.exists() else None


def _now() -> str:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat()


def _entry(pid: str) -> dict:
    for e in json.loads(QUEUE_JSON.read_text())["entries"]:
        if e["id"] == pid:
            return e
    sys.exit(f"{pid} is not in {QUEUE_JSON}; run `python -m rhm.factory queue` first")


def main(argv=None) -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    q = sub.add_parser("queue"); q.add_argument("--size", type=int, default=20); q.add_argument("--offline", action="store_true")
    p = sub.add_parser("prepare"); p.add_argument("id"); p.add_argument("--work", type=Path, default=ROOT / "factory" / "work")
    s = sub.add_parser("smoke"); s.add_argument("id"); s.add_argument("--apk", type=Path, required=True); s.add_argument("--seconds", type=int, default=60)
    t = sub.add_parser("triage"); t.add_argument("--work", type=Path, default=ROOT / "factory" / "work")
    a = ap.parse_args(argv)
    if a.cmd == "queue":
        es = write_queue(a.size, a.offline)
        print(f"wave 1: {len(es)} projects -> {QUEUE_JSON} and factory/QUEUE.md", file=sys.stderr)
    elif a.cmd == "prepare":
        e = _entry(a.id)
        project = prepare(e, a.work)
        print(build_command(e, project, a.work / "_out" / e["id"]))
    elif a.cmd == "smoke":
        print(json.dumps(smoke(_entry(a.id), a.apk, a.seconds), indent=2))
    elif a.cmd == "triage":
        st = triage(a.work)
        print(f"{sum(v['state'] == 'ready' for v in st.values())}/{len(st)} ready -> {STATUS_JSON}", file=sys.stderr)


if __name__ == "__main__":
    main()
