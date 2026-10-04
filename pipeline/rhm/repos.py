"""GitHub + Codeberg (Gitea) repo inspection -> classifier Signals."""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from urllib.parse import quote, urlparse

from .http import PoliteSession

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
    API = "https://codeberg.org/api/v1"

    def meta(self, o, r):
        return self.http.get_json(f"{self.API}/repos/{o}/{r}")

    def tree(self, o, r, ref):
        out, page = [], 1
        while True:
            j = self.http.get_json(f"{self.API}/repos/{o}/{r}/git/trees/{quote(ref)}?recursive=true&per_page=10000&page={page}")
            if not j:
                break
            out += [t["path"] for t in j.get("tree", []) if t.get("type") == "blob"]
            if not j.get("truncated"):
                break
            page += 1
        return out

    def raw(self, o, r, path, ref):
        status, body = self.http.get(f"{self.API}/repos/{o}/{r}/raw/{quote(path)}?ref={quote(ref)}")
        return body if status == 200 else None

    def readme(self, o, r, ref):
        for name in ("README.md", "readme.md", "README", "README.txt"):
            body = self.raw(o, r, name, ref)
            if body:
                return body
        return ""

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
    host: _Host = (gh or GitHub(session)) if host_name == "github.com" else Codeberg(session)
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
