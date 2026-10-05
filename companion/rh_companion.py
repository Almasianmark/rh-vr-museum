#!/usr/bin/env python3
"""Reality Hack Museum companion: silent installs over ADB Wi-Fi (CLAUDE.md phase 5, "Companion" mode).

Runs on a PC or Raspberry Pi on the same network as the Quest. No server: it talks to the museum
through two files in the museum's app folder on the headset:

    companion/requests.json   written by the museum: {seq, install:[{package, apk_url, sha256, bytes, project_id}], uninstall:[...]}
    companion/status.json     written by this script: {seq_done, installed:[...], message, updated}

Usage:
    python3 rh_companion.py connect 192.168.1.42           # once per session (Quest: Settings > Developer > Wireless ADB)
    python3 rh_companion.py watch                          # serve the museum's install/uninstall requests
    python3 rh_companion.py preload --museum ../data/museum.json --budget-gb 24   # setup: best-rated ports that fit
    python3 rh_companion.py status

Installs use `adb install -r -g`: no per-app prompt, and runtime permissions are granted up front.
Every APK is SHA-256 verified before it touches the headset. Downloads are cached in ~/.cache/rh-companion.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

MUSEUM_PACKAGE = "world.realityhack.museum"
REMOTE_DIR = f"/sdcard/Android/data/{MUSEUM_PACKAGE}/files/companion"
CACHE = Path(os.environ.get("RH_COMPANION_CACHE", Path.home() / ".cache" / "rh-companion"))
INSTALL_FACTOR = 1.6   # same estimate as the museum's StoragePlanner


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def default_fetch(url: str, dest: Path) -> None:
    tmp = dest.with_suffix(".part")
    with urllib.request.urlopen(url, timeout=60) as r, open(tmp, "wb") as f:
        shutil.copyfileobj(r, f, 1 << 20)
    tmp.replace(dest)


class Companion:
    def __init__(self, serial: str | None = None, remote_dir: str = REMOTE_DIR, adb=None, fetch=default_fetch,
                 cache: Path = CACHE, log=print):
        self.serial = serial
        self.remote_dir = remote_dir
        self._adb = adb or self._run_adb
        self.fetch = fetch
        self.cache = Path(cache)
        self.log = log
        self.cache.mkdir(parents=True, exist_ok=True)

    # ------------------------------------------------------------ adb

    def _run_adb(self, *args: str) -> tuple[int, str]:
        cmd = ["adb"] + (["-s", self.serial] if self.serial else []) + list(args)
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
        return p.returncode, (p.stdout + p.stderr).strip()

    def adb(self, *args: str) -> tuple[int, str]:
        return self._adb(*args)

    def installed_packages(self) -> set[str]:
        code, out = self.adb("shell", "pm", "list", "packages")
        return {line.split(":", 1)[1].strip() for line in out.splitlines() if line.startswith("package:")} if code == 0 else set()

    def read_requests(self) -> dict | None:
        code, out = self.adb("shell", "cat", f"{self.remote_dir}/requests.json")
        if code != 0 or not out.strip().startswith("{"):
            return None
        try:
            return json.loads(out)
        except json.JSONDecodeError:
            return None   # caught mid-write; try again next poll

    def write_status(self, seq_done: int, message: str) -> None:
        installed = sorted(p for p in self.installed_packages() if p.startswith("world.realityhack.p"))
        body = {"seq_done": seq_done, "installed": installed, "message": message, "updated": int(time.time())}
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
            json.dump(body, f)
            local = f.name
        try:
            self.adb("shell", "mkdir", "-p", self.remote_dir)
            self.adb("push", local, f"{self.remote_dir}/status.json")
        finally:
            os.unlink(local)

    # ------------------------------------------------------------ install / uninstall

    def ensure_apk(self, app: dict) -> Path:
        dest = self.cache / f"{app['package']}-{app['sha256'][:12]}.apk"
        if not dest.exists() or sha256_file(dest) != app["sha256"].lower():
            self.log(f"  download {app['package']} ({app.get('bytes', 0) / 1e6:.0f} MB)")
            self.fetch(app["apk_url"], dest)
        got = sha256_file(dest)
        if got != app["sha256"].lower():
            dest.unlink(missing_ok=True)
            raise ValueError(f"SHA-256 mismatch for {app['package']}: got {got[:12]}…, expected {app['sha256'][:12]}…")
        return dest

    def install(self, app: dict) -> bool:
        try:
            apk = self.ensure_apk(app)
        except Exception as e:  # noqa: BLE001  (report and continue with the next app)
            self.log(f"  ✗ {app['package']}: {e}")
            return False
        code, out = self.adb("install", "-r", "-g", str(apk))
        ok = code == 0 and "Success" in out
        self.log(f"  {'✓' if ok else '✗'} install {app['package']}{'' if ok else ': ' + out[-200:]}")
        return ok

    def uninstall(self, package: str) -> bool:
        if not package.startswith("world.realityhack.p"):
            self.log(f"  refusing to uninstall non-museum package {package}")
            return False
        code, out = self.adb("uninstall", package)
        ok = code == 0 and "Success" in out
        self.log(f"  {'✓' if ok else '✗'} uninstall {package}")
        return ok

    def serve_once(self, last_seq: int) -> int:
        """Handle one new request (if any). Returns the seq handled (or last_seq)."""
        req = self.read_requests()
        if not req or int(req.get("seq", 0)) <= last_seq:
            return last_seq
        seq = int(req["seq"])
        self.log(f"request #{seq}: install {len(req.get('install', []))}, uninstall {len(req.get('uninstall', []))}")
        have = self.installed_packages()
        removed = sum(self.uninstall(p) for p in req.get("uninstall", []) if p in have)
        todo = [a for a in req.get("install", []) if a.get("package")]
        ok = sum(self.install(a) for a in todo)
        msg = f"installed {ok}/{len(todo)}" + (f", removed {removed}" if removed else "")
        self.write_status(seq, msg)
        return seq

    def watch(self, interval: float = 2.0) -> None:
        last = -1
        self.write_status(0, "companion connected")
        self.log(f"watching {self.remote_dir}/requests.json (Ctrl+C to stop)")
        while True:
            try:
                last = self.serve_once(last)
            except subprocess.TimeoutExpired:
                self.log("adb timed out; is the headset asleep?")
            time.sleep(interval)

    # ------------------------------------------------------------ preload

    @staticmethod
    def preload_selection(museum: dict, budget_bytes: int, installed: set[str]) -> list[dict]:
        """Best-rated ports first (avg_stars, then winners), as many as fit (same rule as the museum)."""
        apps = [dict(p["app"], project_id=p["id"], _rating=p.get("avg_stars") or 3.0, _winner=p.get("winner", False))
                for p in museum.get("projects", []) if p.get("app") and p["app"].get("package")]
        used = sum(int(a.get("bytes", 0) * INSTALL_FACTOR) for a in apps if a["package"] in installed)
        room = budget_bytes - used
        picked = []
        for a in sorted(apps, key=lambda a: (-a["_rating"], not a["_winner"], a["package"])):
            if a["package"] in installed:
                continue
            need = int(a.get("bytes", 0) * INSTALL_FACTOR)
            if need <= room:
                picked.append(a)
                room -= need
        return picked

    def preload(self, museum: dict, budget_gb: float) -> int:
        picked = self.preload_selection(museum, int(budget_gb * (1 << 30)), self.installed_packages())
        self.log(f"preload: {len(picked)} app(s) fit in {budget_gb:g} GB")
        ok = sum(self.install(a) for a in picked)
        self.write_status(0, f"preloaded {ok}/{len(picked)}")
        return ok


def main(argv=None) -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--serial", help="adb serial (ip:port) when several devices are connected")
    ap.add_argument("--remote-dir", default=REMOTE_DIR)
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("connect"); c.add_argument("host"); c.add_argument("--port", default="5555")
    sub.add_parser("watch")
    p = sub.add_parser("preload"); p.add_argument("--museum", type=Path, default=Path(__file__).resolve().parents[1] / "data" / "museum.json")
    p.add_argument("--budget-gb", type=float, default=16)
    sub.add_parser("status")
    a = ap.parse_args(argv)

    if a.cmd == "connect":
        out = subprocess.run(["adb", "connect", f"{a.host}:{a.port}"], capture_output=True, text=True)
        print((out.stdout + out.stderr).strip())
        return
    comp = Companion(a.serial, a.remote_dir)
    if a.cmd == "watch":
        comp.watch()
    elif a.cmd == "preload":
        comp.preload(json.loads(a.museum.read_text(encoding="utf-8")), a.budget_gb)
    elif a.cmd == "status":
        pk = sorted(p for p in comp.installed_packages() if p.startswith("world.realityhack.p"))
        print(f"{len(pk)} museum port(s) installed" + ("".join(f"\n  {p}" for p in pk)))


if __name__ == "__main__":
    main()
