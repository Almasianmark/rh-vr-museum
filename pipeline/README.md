# Pipeline: Devpost + GitHub/Codeberg → `projects.json`

Build step 1 from `CLAUDE.md`: scrape every Reality Hack project, classify its hardware, and write `data/projects.json` plus `data/SUMMARY.md` (counts per year, platform, and fidelity tier).

## Run

```bash
cd pipeline
python -m venv .venv && . .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
export GITHUB_TOKEN=ghp_...                         # optional; without API access GitHub repos are read via git

python -m rhm.build                                 # live: all years (~450 projects, ~1 h first run; cached after)
python -m rhm.build --years 2024 --limit 5          # quick smoke test
python -m rhm.build --source snapshot               # offline rebuild from pipeline/snapshots/
python -m pytest -q
```

HTTP responses are cached in `pipeline/.cache/` (set `RHM_CACHE` to move it). Delete it to force a fresh scrape. Requests are rate-limited per host (Devpost 1.5 s, GitHub 0.25 s) and retry with backoff on 429/5xx and on GitHub rate-limit resets.

## How it works

| Stage | Module | Notes |
|---|---|---|
| Gallery → project pages | `rhm/devpost.py` | title, tagline, winner, Built-with tags, Try-it-out links, video, description sections, prizes/tracks, team |
| Repo inspection | `rhm/repos.py` | Reads metadata, license, the full file list, every `Packages/manifest.json`, `ProjectVersion.txt`, XR loader settings, `.lsproj`/`.ino`/`.uproject`/Swift/WebXR markers, README, and `.apk` release assets. **GitHub:** uses the REST API when it answers; otherwise (`RHM_GITHUB=git`, or auto-detected) it does a blob-less shallow `git clone` and fetches only the files it reads, inferring license and language from files. That fallback gets no description, topics, or releases. **Codeberg:** Gitea API for metadata and the org list, git for files (the tree API caps pages at 1,000 entries). |
| 2026 repo links | `rhm/build.py` | `api.realityhack.world/projects/` (public, current event only) gives each team's repo and Devpost link; used when the Devpost page has none |
| Repo matching | `rhm/build.py` | 1) Devpost "Try it out" link · 2) `snapshots/github/aliases.tsv` · 3) fuzzy name match against the year's central org. Unmatched org repos are listed as `orphan_repos`. |
| Classifier | `rhm/classify.py` | manifest → structural markers → Devpost tags → free text. First tier with a hit decides the platform; extra hardware and Quest 3 features accumulate across all tiers. Every decision is recorded in `evidence`. |
| Synopsis | `rhm/text.py` | Extractive: tagline + "What it does" (falls back to intro/Inspiration), 2–3 sentences, ≤420 chars |
| Overrides | `overrides.json` | `{"2023-failtopia": {"platform": "quest", "fidelity": "Native", "note": "checked APK"}}`. Applied last; the applied object is copied to `manual_override`. |

### Output fields (per project)

`id`, `year`, `title`, `tagline`, `synopsis`, `devpost_url`, `winner`, `prizes`, `built_with`, `team`, `video_url`, `thumbnail`, `repo{url,host,match,language,unity_version,manifest_paths,apk_assets}`, `links{repo,video,horizon,web,…}`, **`platform`**, `platforms_detected`, **`requirement_tier`**, **`fidelity`**, `quest3_features`, `extra_hardware`, **`license`**, `license_gate`, `confidence`, `evidence`, **`manual_override`**.

- `requirement_tier` = what the **original** needs: `quest2 · quest3 · pcvr · non-quest · extra-hardware · unknown`
- `fidelity` = the best the museum can offer **on a Quest 2**: `Native · Ported · Ported-reduced · Simulated · Browser · Watch`. This is technical potential. Rebuilding also needs `license_gate == "ok"` (or team permission). `needs-permission` means the repo has no license file, so the project is Watch-only for now.
- `confidence`: `high` (manifest/structure) · `medium` (Devpost tags) · `low` (keyword in text) · `guess` (generic "VR"/"AR" wording) · `none`

## Sources per year

| Year | Devpost | Central repos |
|---|---|---|
| 2020 | mit-reality-hack-2020 | github.com/MIT-Reality-Hack-2020 (40) |
| 2022 | mit-reality-hack-2022 | github.com/Reality-Hack-2022 (82, mostly `TEAM-NN`) |
| 2023 | mit-reality-hack-2023 | github.com/Reality-Hack-2023 (114, many empty `REPO-NNN`) |
| 2024 | mit-reality-hack-2024 | **codeberg.org/reality-hack-2024** (~100). No GitHub org exists. |
| 2025 | mit-reality-hack-2025 | team-hosted; Devpost Try-it-out links only |
| 2026 | reality-hack-2026 | team-hosted (GitHub/Codeberg); Devpost Try-it-out links only |

**Challenge tracks:** Devpost project pages show only prizes won, and the realityhack.world `eventtracks` endpoint requires a login. So `prizes` is the only grouping data so far. Rooms-by-track needs a track list from the organizers.

## About the committed `data/projects.json`

Built live (`python -m rhm.build`): Devpost pages, plus repos read via git (GitHub) and git + Gitea API (Codeberg). The GitHub REST API wasn't available in the build sandbox, so GitHub repos have no description/topics and no release APK check. `pipeline/snapshots/` holds the earlier offline capture and still feeds the GitHub org repo lists.
