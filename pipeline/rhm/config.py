"""Per-year sources. Devpost galleries are the master index for every year."""

YEARS = {
    2020: {
        "devpost": "https://mit-reality-hack-2020.devpost.com",
        "github_org": "MIT-Reality-Hack-2020",
    },
    2022: {
        "devpost": "https://mit-reality-hack-2022.devpost.com",
        "github_org": "Reality-Hack-2022",
    },
    2023: {
        "devpost": "https://mit-reality-hack-2023.devpost.com",
        "github_org": "Reality-Hack-2023",
    },
    # 2024: no GitHub org (org:Reality-Hack-2024 / org:MIT-Reality-Hack-2024 don't exist).
    # Central repos live on Codeberg instead: codeberg.org/reality-hack-2024 (~100 repos).
    2024: {
        "devpost": "https://mit-reality-hack-2024.devpost.com",
        "github_org": None,
        "codeberg_org": "reality-hack-2024",
    },
    2025: {
        "devpost": "https://mit-reality-hack-2025.devpost.com",
        "github_org": None,
    },
    2026: {
        "devpost": "https://reality-hack-2026.devpost.com",
        "github_org": None,
    },
}

USER_AGENT = "rh-vr-museum-pipeline/0.1 (+https://github.com/Almasianmark/rh-vr-museum)"

# Seconds between requests to the same host.
MIN_INTERVAL = {
    "devpost.com": 1.5,
    "api.github.com": 0.25,
    "default": 1.0,
}
