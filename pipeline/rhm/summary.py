"""Count tables for projects.json -> SUMMARY.md."""

from __future__ import annotations

from collections import Counter
from pathlib import Path

FIDELITY_ORDER = ["Native", "Ported", "Ported-reduced", "Simulated", "Browser", "Watch"]
TIER_ORDER = ["quest2", "quest3", "pcvr", "non-quest", "extra-hardware", "unknown"]
CONF_ORDER = ["high", "medium", "low", "guess", "none"]


def _table(projects, years, key, order=None) -> list[str]:
    counts = {y: Counter(p[key] for p in projects if p["year"] == y) for y in years}
    total = Counter(p[key] for p in projects)
    keys = order or [k for k, _ in total.most_common()]
    keys = [k for k in keys if total[k]] + [k for k in total if k not in keys]
    head = f"| {key} | " + " | ".join(str(y) for y in years) + " | **Total** | % |"
    sep = "|---|" + "---:|" * (len(years) + 2)
    rows = [head, sep]
    n = len(projects) or 1
    for k in keys:
        rows.append(f"| {k} | " + " | ".join(str(counts[y][k] or "·") for y in years)
                    + f" | **{total[k]}** | {100 * total[k] / n:.0f}% |")
    rows.append("| **all** | " + " | ".join(f"**{sum(counts[y].values())}**" for y in years)
                + f" | **{len(projects)}** | 100% |")
    return rows


def write_summary(doc: dict, path: Path) -> None:
    projects = doc["projects"]
    years = sorted({p["year"] for p in projects})
    L = [
        "# projects.json summary",
        "",
        f"Generated {doc['generated_at']} from **{doc['source']}** data. "
        f"{len(projects)} projects, {len(doc['excluded'])} excluded, {len(doc['orphan_repos'])} orphan repos.",
        "",
    ]
    if doc["source"] == "snapshot":
        L += [
            "> **Snapshot run:** Devpost was reachable only as gallery listings (title, tagline, winner), so tags, "
            "\"Try it out\" links, and descriptions are missing. GitHub signals come from code search, which indexes only "
            "some repos. Most classifications here come from taglines, so check the confidence table before relying on them. "
            "Run `python -m rhm.build` (live) to replace this file.",
            "",
        ]
    L += ["## Per year", "",
          "| year | projects | winners | repo found | Unity manifest seen |", "|---|---:|---:|---:|---:|"]
    for y in years:
        ps = [p for p in projects if p["year"] == y]
        L.append(f"| {y} | {len(ps)} | {sum(p['winner'] for p in ps)} | "
                 f"{sum(bool(p['repo'] and not p['repo']['empty']) for p in ps)} | "
                 f"{sum(bool(p['repo'] and p['repo']['manifest_paths']) for p in ps)} |")
    L += ["", "## Fidelity tier (what the museum can offer on Quest 2)", ""] + _table(projects, years, "fidelity", FIDELITY_ORDER)
    L += ["", "## Platform (original target)", ""] + _table(projects, years, "platform")
    L += ["", "## Requirement tier (what the original needs)", ""] + _table(projects, years, "requirement_tier", TIER_ORDER)
    L += ["", "## Classification confidence", "",
          "high = manifest/structural evidence · medium = Devpost tags · low = keyword in text · guess = generic VR/AR wording only", ""] \
        + _table(projects, years, "confidence", CONF_ORDER)
    L += ["", "## License gate", ""] + _table(projects, years, "license_gate")

    hw = Counter(h for p in projects for h in p["extra_hardware"])
    if hw:
        L += ["", "## Extra hardware mentioned", "", "| hardware | projects |", "|---|---:|"]
        L += [f"| {k} | {v} |" for k, v in hw.most_common()]

    winners = [p for p in projects if p["winner"]]
    if winners:
        L += ["", "## Winners", "", "| year | title | platform | fidelity | confidence |", "|---|---|---|---|---|"]
        L += [f"| {p['year']} | {p['title']} | {p['platform']} | {p['fidelity']} | {p['confidence']} |"
              for p in sorted(winners, key=lambda p: (p["year"], p["title"].lower()))]
    if doc["excluded"]:
        L += ["", "## Excluded", ""] + [f"- {e['year']} · {e['title']} — {e['reason']}" for e in doc["excluded"]]
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(L) + "\n", encoding="utf-8")
