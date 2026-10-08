"""GitHub + Codeberg (Gitea) repo inspection -> classifier Signals."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import time
from dataclasses import dataclass, field
from urllib.parse import quote, urlparse

from .http import CACHE_DIR, PoliteSession

REPO_URL = re.compile(r"https?://(?:www\.)?(github\.com|codeberg\.org)/([\w.-]+)/([\w.-]+)", re.I)
NOT_REPOS = {"orgs", "sponsors", "topics", "features", "settings", "marketplace", "explore"}

WEBXR_JS = re.compile(r"aframe|a-frame|three(?:\.js|/)|\"three\"|webxr|8thwall|xr8|babylonjs|@react-three/xr|needle-tools", re.I)


def parse_repo_url(url: str) -> tuple[str, str, str] | None:
    """-> (host, owner, name) for GitHub/Codeberg repo URLs, else None."""
    m = REPO_URL.match(url.strip())
    if not m:
        return None
    host, owner, name = m.group(1).lower(), m.group(2), m.group(3)
    name = re.sub(r"\.git$", "", name)
    if owner.lower() in NOT_REPOS:
        return None
    return host, owner, name


@dataclass
class RepoInfo:
    host: str
    full_name: str
    url: str
    exists: bool = True
    empty: bool = False
    language: str | None = None
    description: str = ""
    topics: list[str] = field(default_factory=list)
    license_spdx: str | None = None      # "" = repo has no license file
    unity_version: str | None = None
    packages: set[str] = field(default_factory=set)
    markers: set[str] = field(default_factory=set)
    manifest_paths: list[str] = field(default_factory=list)
    readme: str = ""
    apk_assets: list[str] = field(default_factory=list)


class _Host:
    def __init__(self, session: PoliteSession):
        self.http = session

    # interface
    def meta(self, o, r): ...
    def tree(self, o, r, ref): ...
    def raw(self, o, r, path, ref): ...
    def readme(self, o, r, ref): ...
    def releases(self, o, r): ...
    def org_repos(self, org): ...


class GitHub(_Host):
    API = "https://api.github.com"

    def __init__(self, session, token: str | None = None):
        super().__init__(session)
        self.h = {"Accept": "application/vnd.github+json"}
        token = token or os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
        if token:
            self.h["Authorization"] = f"Bearer {token}"

    def meta(self, o, r):
        return self.http.get_json(f"{self.API}/repos/{o}/{r}", self.h)

    def tree(self, o, r, ref):
        j = self.http.get_json(f"{self.API}/repos/{o}/{r}/git/trees/{quote(ref)}?recursive=1", self.h)
        return [t["path"] for t in (j or {}).get("tree", []) if t.get("type") == "blob"]

    def raw(self, o, r, path, ref):
        status, body = self.http.get(f"{self.API}/repos/{o}/{r}/contents/{quote(path)}?ref={quote(ref)}",
                                     {**self.h, "Accept": "application/vnd.github.raw"})
        return body if status == 200 else None

    def readme(self, o, r, ref):
        status, body = self.http.get(f"{self.API}/repos/{o}/{r}/readme", {**self.h, "Accept": "application/vnd.github.raw"})
        return body if status == 200 else ""

    def releases(self, o, r):
        return self.http.get_json(f"{self.API}/repos/{o}/{r}/releases?per_page=30", self.h) or []

    def org_repos(self, org):
        out, page = [], 1
        while True:
            j = self.http.get_json(f"{self.API}/orgs/{org}/repos?per_page=100&page={page}&type=public", self.h)
            if not j:
                break
            out += j
            page += 1
        return out


class Codeberg(_Host):
    """Codeberg: Gitea API for metadata/org listing; files via git (the tree API pages at 1000 entries)."""
    API = "https://codeberg.org/api/v1"

    def __init__(self, session):
        super().__init__(session)
        self.git = GitClone(session, base="https://codeberg.org")

    def meta(self, o, r):
        return self.http.get_json(f"{self.API}/repos/{o}/{r}")

    def tree(self, o, r, ref):
        return self.git.tree(o, r, ref)

    def raw(self, o, r, path, ref):
        return self.git.raw(o, r, path, ref)

    def readme(self, o, r, ref):
        return self.git.readme(o, r, ref)

    def releases(self, o, r):
        return self.http.get_json(f"{self.API}/repos/{o}/{r}/releases?limit=30") or []

    def org_repos(self, org):
        out, page = [], 1
        while True:
            j = self.http.get_json(f"{self.API}/orgs/{org}/repos?limit=50&page={page}")
            if not j:
                break
            out += j
            page += 1
        return out


def _decode(b: bytes) -> str:
    """Repo files are mostly UTF-8, but Windows tools leave UTF-16 READMEs/LICENSEs behind."""
    if b.startswith((b"\xff\xfe", b"\xfe\xff")):
        return b.decode("utf-16", errors="replace")
    return b.decode("utf-8", errors="replace").lstrip("\ufeff")


class GitClone(_Host):
    """GitHub via plain git: blob-less shallow clone, then fetch only the files we read.

    Used when api.github.com isn't usable (no token / rate limit / sandbox policy).
    No description/topics/releases this way; license and language are inferred from files.
    """

    def __init__(self, session, base="https://github.com", min_interval=0.5):
        super().__init__(session)
        self.base = base
        self.min_interval = min_interval
        self._last = 0.0
        self.root = CACHE_DIR / "git" / base.split("//", 1)[1]

    def _git(self, *args, cwd=None, check=True) -> subprocess.CompletedProcess:
        env = {**os.environ, "GIT_TERMINAL_PROMPT": "0", "GIT_ASKPASS": "true"}
        res = subprocess.run(["git", "-c", "credential.helper=", *args], cwd=cwd, env=env,
                             capture_output=True, check=check, timeout=600)
        res.stdout, res.stderr = _decode(res.stdout), _decode(res.stderr)
        return res

    def _dir(self, o, r):
        d = self.root / o / r
        if not (d / ".git").exists() and not (d / "MISSING").exists():
            dt = time.monotonic() - self._last
            if dt < self.min_interval:
                time.sleep(self.min_interval - dt)
            self._last = time.monotonic()
            shutil.rmtree(d, ignore_errors=True)
            d.parent.mkdir(parents=True, exist_ok=True)
            res = self._git("clone", "--filter=blob:none", "--no-checkout", "--depth", "1", "--quiet",
                            f"{self.base}/{o}/{r}.git", str(d), check=False)
            if res.returncode != 0:
                shutil.rmtree(d, ignore_errors=True)
                d.mkdir(parents=True, exist_ok=True)
                (d / "MISSING").write_text(res.stderr[-500:])
        return None if (d / "MISSING").exists() else d

    def meta(self, o, r):
        d = self._dir(o, r)
        if not d:
            return None
        head = self._git("rev-parse", "--abbrev-ref", "HEAD", cwd=d, check=False).stdout.strip() or "HEAD"
        has_commit = self._git("rev-parse", "--verify", "-q", "HEAD", cwd=d, check=False).returncode == 0
        return {"default_branch": "HEAD", "branch_name": head, "empty": not has_commit}

    def tree(self, o, r, ref):
        d = self._dir(o, r)
        if not d:
            return []
        res = self._git("-c", "core.quotepath=off", "ls-tree", "-r", "--name-only", "HEAD", cwd=d, check=False)
        return [l for l in res.stdout.splitlines() if l]

    def raw(self, o, r, path, ref):
        d = self._dir(o, r)
        if not d:
            return None
        res = self._git("cat-file", "-p", f"HEAD:{path}", cwd=d, check=False)
        return res.stdout if res.returncode == 0 else None

    def readme(self, o, r, ref):
        for p in self.tree(o, r, ref):
            if "/" not in p and p.lower().startswith("readme"):
                return self.raw(o, r, p, ref) or ""
        return ""

    def releases(self, o, r):
        return []

    def org_repos(self, org):
        return []


def github_host(session: PoliteSession) -> _Host:
    """REST API if it answers for a public repo outside this session's scope, else git."""
    mode = os.environ.get("RHM_GITHUB", "auto")
    if mode == "git":
        return GitClone(session)
    gh = GitHub(session)
    if mode == "api":
        return gh
    try:
        status, _ = session.get(f"{gh.API}/repos/octocat/Hello-World", gh.h)
    except Exception:
        status = None
    return gh if status == 200 else GitClone(session)


LICENSE_SIGS = [
    ("AGPL-3.0", r"GNU AFFERO GENERAL PUBLIC LICENSE"),
    ("LGPL-3.0", r"GNU LESSER GENERAL PUBLIC LICENSE\s+Version 3"),
    ("LGPL-2.1", r"GNU LESSER GENERAL PUBLIC LICENSE\s+Version 2\.1"),
    ("GPL-3.0", r"GNU GENERAL PUBLIC LICENSE\s+Version 3"),
    ("GPL-2.0", r"GNU GENERAL PUBLIC LICENSE\s+Version 2"),
    ("Apache-2.0", r"Apache License,?\s+Version 2\.0"),
    ("MPL-2.0", r"Mozilla Public License,?\s+v(?:ersion)?\.? ?2\.0"),
    ("Unlicense", r"This is free and unencumbered software"),
    ("CC0-1.0", r"CC0 1\.0|Creative Commons Zero"),
    ("CC-BY-SA-4.0", r"Attribution-ShareAlike 4\.0"),
    ("CC-BY-4.0", r"Attribution 4\.0 International"),
    ("BSD-3-Clause", r"Redistribution and use in source and binary forms[\s\S]*Neither the name"),
    ("BSD-2-Clause", r"Redistribution and use in source and binary forms"),
    ("MIT", r"Permission is hereby granted, free of charge"),
    ("ISC", r"Permission to use, copy, modify, and/or distribute this software for any purpose"),
]

LANG_EXT = {".cs": "C#", ".swift": "Swift", ".js": "JavaScript", ".ts": "TypeScript", ".tsx": "TypeScript",
            ".jsx": "JavaScript", ".py": "Python", ".cpp": "C++", ".h": "C++", ".java": "Java", ".kt": "Kotlin",
            ".gd": "GDScript", ".html": "HTML"}


def detect_license(text: str) -> str:
    for spdx, pat in LICENSE_SIGS:
        if re.search(pat, text, re.I):
            return spdx
    return "NOASSERTION"


def infer_language(paths: list[str]) -> str | None:
    counts: dict[str, int] = {}
    for p in paths:
        ext = os.path.splitext(p)[1].lower()
        if ext in LANG_EXT:
            counts[LANG_EXT[ext]] = counts.get(LANG_EXT[ext], 0) + 1
    return max(counts, key=counts.get) if counts else None


def _skip_path(p: str) -> bool:
    low = p.lower()
    return any(s in low for s in ("/library/", "library/packagecache", "/temp/", "/builds/", "/build/", "node_modules/")) \
        or low.startswith("library/")


def _android_in_xr_settings(text: str) -> bool:
    # XRGeneralSettingsPerBuildTarget.asset stores BuildTargetGroup keys as little-endian hex; Android = 7.
    m = re.search(r"Keys:\s*([0-9a-f]+)", text)
    if not m:
        return False
    keys = m.group(1)
    return any(keys[i:i + 8] == "07000000" for i in range(0, len(keys), 8))


def inspect_repo(url: str, session: PoliteSession, gh: GitHub | None = None) -> RepoInfo | None:
    parsed = parse_repo_url(url)
    if not parsed:
        return None
    host_name, o, r = parsed
    host: _Host = (gh or github_host(session)) if host_name == "github.com" else Codeberg(session)
    info = RepoInfo(host="github" if host_name == "github.com" else "codeberg",
                    full_name=f"{o}/{r}", url=f"https://{host_name}/{o}/{r}")

    meta = host.meta(o, r)
    if not meta:
        info.exists = False
        return info
    info.language = meta.get("language") or None
    info.description = meta.get("description") or ""
    info.topics = meta.get("topics") or []
    info.empty = bool(meta.get("empty"))   # Gitea flag; GitHub's "size" lags, so GitHub emptiness comes from the tree
    lic = meta.get("license")
    if isinstance(lic, dict):
        info.license_spdx = lic.get("spdx_id") or ""
    elif meta.get("licenses") is not None:            # Gitea >= 1.22
        info.license_spdx = (meta["licenses"] or [""])[0]
    else:
        info.license_spdx = "" if info.host == "github" else None
    ref = meta.get("default_branch") or "main"
    if info.empty:
        return info

    paths = host.tree(o, r, ref)
    if not paths:
        info.empty = True
        return info
    keep = [p for p in paths if not _skip_path(p)]
    low = [p.lower() for p in keep]
    if not info.language:
        info.language = infer_language(keep)
    if not info.license_spdx:   # None (host didn't say) or "" (API says none) -> look for a file
        lic = next((p for p in keep if "/" not in p and re.match(r"(license|licence|copying)", p, re.I)), None)
        if lic:
            info.license_spdx = detect_license(host.raw(o, r, lic, ref) or "")
        elif info.license_spdx is None:
            info.license_spdx = ""

    # Unity manifests (may be in subfolders, sometimes several per repo)
    for p in keep:
        if p.endswith("Packages/manifest.json"):
            body = host.raw(o, r, p, ref)
            try:
                deps = json.loads(body or "{}").get("dependencies", {})
            except json.JSONDecodeError:
                continue
            info.packages |= set(deps)
            info.manifest_paths.append(p)
            info.markers.add("unity")
            if "nreal" in p.lower():
                info.markers.add("nreal")
    for p in keep:
        if p.endswith("ProjectSettings/ProjectVersion.txt"):
            body = host.raw(o, r, p, ref) or ""
            m = re.search(r"m_EditorVersion:\s*(\S+)", body)
            if m:
                info.unity_version = m.group(1)
                break
    for p in keep:
        if p.endswith("XRGeneralSettingsPerBuildTarget.asset"):
            if _android_in_xr_settings(host.raw(o, r, p, ref) or ""):
                info.markers.add("android-xr-loader")
            break
    if any(l.endswith(("oculusprojectconfig.asset", "oculussettings.asset")) for l in low):
        info.markers.add("android-xr-loader")

    # Non-Unity markers
    if any(l.endswith(".lsproj") or l.endswith(".esproj") for l in low):
        info.markers.add(".lsproj")
    if any(l.endswith(".ino") for l in low):
        info.markers.add(".ino")
    if any(l.endswith(".uproject") for l in low):
        info.markers.add("unreal")
    if any(l.endswith((".xcodeproj/project.pbxproj", "package.swift")) for l in low) and not info.packages:
        info.markers.add("xcode")
    if sum(l.endswith(".swift") for l in low) >= 3 and not info.packages:
        info.markers.add("swift")
    if any("steamvr" in l for l in low):
        info.markers.add("steamvr")
    if any("/nreal" in l or l.startswith("nreal") for l in low):
        info.markers.add("nreal")
    web_files = [p for p in keep if p.lower().endswith(("package.json", "index.html"))][:4]
    for p in web_files:
        if WEBXR_JS.search(host.raw(o, r, p, ref) or ""):
            info.markers.add("webxr-js")
            break

    info.readme = host.readme(o, r, ref)[:20000]
    for rel in host.releases(o, r):
        for a in rel.get("assets", []):
            if a.get("name", "").lower().endswith(".apk"):
                info.apk_assets.append(a.get("browser_download_url") or a["name"])
    if info.apk_assets:
        info.markers.add("apk-release")
    return info


def link_kind(url: str) -> str:
    host = urlparse(url).netloc.lower()
    if parse_repo_url(url):
        return "repo"
    if "horizon.meta.com" in host or "horizon.meta" in url:
        return "horizon"
    if any(h in host for h in ("youtube.com", "youtu.be", "vimeo.com")):
        return "video"
    if "lens.snapchat.com" in host or "snapchat.com" in host:
        return "snap-lens"
    if any(h in host for h in ("8thwall.app", "glitch.me", "vercel.app", "netlify.app", "github.io", "itch.io")):
        return "web"
    if "sidequestvr.com" in host or "meta.com/experiences" in url or "oculus.com/experiences" in url:
        return "store"
    if "drive.google.com" in host or "dropbox.com" in host:
        return "download"
    return "other"
