# Pipeline: Devpost + GitHub/Codeberg → `projects.json`

Build step 1 from `CLAUDE.md`: scrape every Reality Hack project, classify its hardware, and write `data/projects.json` plus `data/SUMMARY.md` (counts per year, platform, and fidelity tier).

## Run

```bash
cd pipeline
python -m venv .venv && . .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
export GITHUB_TOKEN=ghp_...                         # 5000 req/h instead of 60; any classic or fine-grained read token

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
| Repo inspection | `rhm/repos.py` | GitHub REST + Codeberg (Gitea) API: metadata, license, recursive tree, every `Packages/manifest.json`, `ProjectVersion.txt`, XR loader settings, `.lsproj`/`.ino`/`.uproject`/Swift/WebXR markers, README, `.apk` release assets |
| Repo matching | `rhm/build.py` | 1) Devpost "Try it out" link · 2) `snapshots/github/aliases.tsv` · 3) fuzzy name match against the year's central org. Unmatched org repos are listed as `orphan_repos`. |
| Classifier | `rhm/classify.py` | manifest → structural markers → Devpost tags → free text. First tier with a hit decides the platform; extra hardware and Quest 3 features accumulate across all tiers. Every decision is recorded in `evidence`. |
| Synopsis | `rhm/text.py` | Extractive: tagline + "What it does" (falls back to intro/Inspiration), 2–3 sentences, ≤420 chars |
| Overrides | `overrides.json` | `{"2023-failtopia": {"platform": "quest", "fidelity": "Native", "note": "checked APK"}}`. Applied last; the applied object is copied to `manual_override`. |

### Output fields (per project)

`id`, `year`, `title`, `tagline`, `synopsis`, `devpost_url`, `winner`, `tracks`, `built_with`, `team`, `video_url`, `thumbnail`, `repo{url,host,match,language,unity_version,manifest_paths,apk_assets}`, `links{repo,video,horizon,web,…}`, **`platform`**, `platforms_detected`, **`requirement_tier`**, **`fidelity`**, `quest3_features`, `extra_hardware`, **`license`**, `license_gate`, `confidence`, `evidence`, **`manual_override`**.

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

Not yet scraped: realityhack.world team pages, which list the "Github" field for 2025/2026 teams that left it off Devpost. Add a loader when Devpost coverage proves thin.

## About the committed `data/projects.json`

It was built with `--source snapshot` in a sandbox where devpost.com, codeberg.org, and raw GitHub access were blocked. Gallery listings came through a web-fetch proxy (title, tagline, and winner only). GitHub org listings and manifest signals came through GitHub search, and code search indexes only some repos. Most rows are classified from the tagline alone, so treat `confidence: guess/none` rows as placeholders. Run the live build on a normal connection to replace it.
