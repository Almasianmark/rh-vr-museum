"""Hardware classifier.

Signals are evaluated strongest-first (see CLAUDE.md):
  1. Unity Packages/manifest.json package IDs
  2. Non-Unity structural markers (.lsproj, .ino, Unreal, Xcode/Swift, web stack, Horizon link)
  3. Devpost "Built with" tags
  4. Free text (title, tagline, description, README, repo description/topics)

The first tier that identifies a platform decides it. Extra hardware and
Quest-3-only features accumulate across all tiers.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

# --- vocab -----------------------------------------------------------------

PLATFORMS = (
    "quest",           # Meta Quest native (Oculus / Meta XR / OpenXR-Android)
    "horizon-worlds",  # built inside Meta Horizon Worlds
    "webxr",           # browser: A-Frame / three.js / 8th Wall / WebXR
    "pcvr",            # SteamVR / OpenVR / WMR headsets / Varjo / Vive
    "hololens",
    "magic-leap",
    "ar-glasses",      # Nreal/Xreal, Snapdragon Spaces (Lenovo A3/ThinkReality), Raven, RayNeo
    "android-xr",      # Samsung Galaxy XR / Android XR
    "vision-pro",      # Unity PolySpatial / visionOS
    "phone-ar",        # ARKit / ARCore / AR Foundation-only / Lightship / Vuforia
    "apple-native",    # Swift / RealityKit (iOS or visionOS)
    "snap",            # Snap Lens Studio / Spectacles
    "social-filter",   # TikTok Effect House, Instagram/Meta Spark
    "holo-display",    # Looking Glass, Leia
    "unreal",          # Unreal project with no other platform evidence
    "hardware-only",   # physical device; no headset app found
    "unknown",
)

TIERS = ("quest2", "quest3", "pcvr", "non-quest", "extra-hardware", "unknown")
FIDELITIES = ("Native", "Ported", "Ported-reduced", "Simulated", "Browser", "Watch")

# Unity package prefix -> platform. Order matters only for display.
PACKAGE_PLATFORMS = [
    ("com.meta.xr", "quest"),
    ("com.oculus", "quest"),
    ("com.unity.xr.oculus", "quest"),
    ("com.unity.xr.meta-openxr", "quest"),
    ("com.microsoft.mixedreality", "hololens"),
    ("com.unity.xr.magicleap", "magic-leap"),
    ("com.magicleap", "magic-leap"),
    ("com.unity.polyspatial", "vision-pro"),
    ("com.unity.xr.visionos", "vision-pro"),
    ("com.qualcomm.snapdragon.spaces", "ar-glasses"),
    ("com.nreal", "ar-glasses"),
    ("com.xreal", "ar-glasses"),
    ("com.valvesoftware.unity.openvr", "pcvr"),
    ("com.unity.xr.openvr", "pcvr"),
    ("com.htc.upm.vive", "pcvr"),
    ("com.unity.xr.androidxr", "android-xr"),
    ("com.google.xr.extensions", "android-xr"),
]
PHONE_AR_PACKAGES = ("com.unity.xr.arkit", "com.unity.xr.arcore", "com.unity.xr.arfoundation",
                     "com.niantic.lightship", "com.ptc.vuforia")
AMBIGUOUS_PACKAGES = ("com.unity.xr.openxr", "com.unity.xr.windowsmr")

# Quest-3-only capabilities -> reduced on Quest 2.
QUEST3_FEATURES = [
    ("depth-api", r"\bdepth api\b|com\.meta\.xr\.depth|environmentdepth"),
    ("scene-mesh", r"\bscene mesh\b|\bmesh api\b"),
    ("color-passthrough", r"\bcolou?r passthrough\b"),
    ("passthrough-camera-api", r"\bpassthrough camera api\b|\bpca\b(?= api)"),
    ("built-for-quest-3", r"\b(?:meta |oculus )?quest 3s?\b"),
]

# (label, regex, essential). essential=False means Quest can stand in for it
# (e.g. Ultraleap -> Quest hand tracking), so it doesn't force Simulated.
EXTRA_HARDWARE = [
    ("arduino", r"\barduino\b|\.ino\b", True),
    ("esp32", r"\besp ?32\b", True),
    ("raspberry-pi", r"\braspberry ?pi\b", True),
    ("qualcomm-rb3", r"\brb3\b|\bqcs\d{4}\b", True),
    ("haptx", r"\bhaptx\b", True),
    ("bhaptics", r"\bb ?haptics\b", True),
    ("haptic-device", r"\bhaptic (?:glove|vest|device|headband|motor|jaw|rig)s?\b|\bforce feedback rig\b|\bpeltier\b|\btelehaptic\b", True),
    ("openbci", r"\bopenbci\b|\bgalea\b", True),
    ("eeg-bci", r"\beeg\b|\bbci\b|\bbrain ?waves?\b|\bbrain[- ]computer\b|\bmuse (?:2|headband|s)\b|\bneurosity\b|\bemotiv\b|\bneuos\b", True),
    ("biosensor", r"\bbiosens\w*|\bbiofeedback\b|\bheart ?rate\b|\bheartbeat\b|\bgsr\b|\beda sensor\b|\bpolar h10\b|\bwhoop\b", True),
    ("robotics", r"\brobot(?:ic)? arm\b|\bfighting robot\b|\banimatronic\b|\breal robots\b|\bmedical robot\b", True),
    ("kinect", r"\bkinect\b|\bazure kinect\b", True),
    ("vive-tracker", r"\bvive trackers?\b", True),
    ("scent-device", r"\bscent\b|\bolfactory\b", True),
    ("lidar-wearable", r"\blidar[- ]equipped\b|\blidar vest\b", True),
    ("imu-wearable", r"\bimus?\b", True),
    ("custom-electronics", r"\bcustom (?:electronics|hardware|pcb)\b|\bphysical (?:kit|board|device|controller)\b|\bhardware controller\b|\bbluetooth (?:jaw|device)\b|\bserial port\b", True),
    ("ultraleap", r"\bultraleap\b|\bleap ?motion\b", False),
    ("varjo", r"\bvarjo\b", False),
]

# Free-text platform keywords (tier 3/4). Patterns are matched on lowercased text.
TEXT_PLATFORMS = [
    ("horizon-worlds", r"\bhorizon ?worlds?\b|\bmeta horizons? world\b|\bhorizon\.meta\.com/world"),
    ("snap", r"\blens studio\b|\bsnap(?:chat)? lens(?:es)?\b|\bspectacles\b|\bsnap specs\b|\bsnapchat\b|\bsnap ar\b|\bsnap cloud\b"),
    ("social-filter", r"\beffect house\b|\btiktok\b|\bspark ar\b|\binstagram filter\b|\blens effect\b"),
    ("vision-pro", r"\bvision ?pro\b|\bvisionos\b|\bpolyspatial\b"),
    ("apple-native", r"\brealitykit\b|\bswiftui\b|\breality composer\b"),
    ("hololens", r"\bholo ?lens\b|\bmrtk\b"),
    ("magic-leap", r"\bmagic ?leap\b"),
    ("android-xr", r"\bandroid ?xr\b|\bgalaxy xr\b|\bproject moohan\b"),
    ("ar-glasses", r"\bn ?real\b|\bxreal\b|\bsnapdragon spaces\b|\bthinkreality\b|\blenovo a3\b|\braven (?:resonance |ar )?glasses\b|\braven resonance\b|\brayneo\b|\bsmart ?glasses\b|\bmeta wearables\b|\bray-?ban meta\b"),
    ("holo-display", r"\blooking glass\b|\bleia ?sr\b|\bholographic display\b"),
    ("quest", r"\boculus\b|\bmeta quest\b|\bquest (?:2|3s?|pro)\b|\bmeta xr\b|\bpresence platform\b|\bmeta sdk\b|\bmeta mixed reality toolkit\b|\binteraction sdk\b|\bmeta spatial sdk\b"),
    ("pcvr", r"\bsteam ?vr\b|\bopenvr\b|\bhtc vive\b|\bvive (?:pro|focus|xr elite)\b|\bvalve index\b|\bvarjo\b|\bpc ?vr\b|\bwindows mixed reality\b|\bsrworks\b"),
    ("webxr", r"\bwebxr\b|\bwebvr\b|\bweb ?ar\b|\ba-?frame\b|\bthree\.?js\b|\bbabylon\.?js\b|\b8th ?wall\b|\breact[- ]three\b|\bweb[- ]based ar\b|\bneedle engine\b"),
    ("phone-ar", r"\barkit\b|\barcore\b|\bar ?foundation\b|\blightship\b|\bniantic\b|\bvuforia\b|\bmobile ar\b|\biphone\b|\bipad\b|\bsmartphones?\b|\bandroid app\b|\bios app\b|\bmobile app\b|\bphone\b"),
]

TEXT_PATTERNS = dict(TEXT_PLATFORMS)

# Generic "it's a headset experience" words. Weak: only used if nothing else matched.
GENERIC_VR = r"\bvr\b|\bvirtual reality\b|\bimmersive\b"
GENERIC_MR = r"\bmixed reality\b|\bmr\b|\bpassthrough\b|\bxr\b"
GENERIC_AR = r"\baugmented reality\b|\bar\b"

MARKER_TRACKING = r"\bimage (?:target|tracking|marker)s?\b|\bmarker[- ]based\b|\bqr code\b|\bvuforia\b|\bimage anchor"

LICENSE_PERMISSIVE = {"mit", "apache-2.0", "bsd-2-clause", "bsd-3-clause", "isc", "unlicense",
                      "cc0-1.0", "0bsd", "zlib", "mpl-2.0", "bsl-1.0", "cc-by-4.0"}
LICENSE_COPYLEFT = {"gpl-2.0", "gpl-3.0", "lgpl-2.1", "lgpl-3.0", "agpl-3.0", "cc-by-sa-4.0"}


# --- data ------------------------------------------------------------------

@dataclass
class Signals:
    """Everything the classifier looks at. Populated by the live or snapshot loader."""
    packages: set[str] = field(default_factory=set)       # Unity package IDs from any manifest.json
    markers: set[str] = field(default_factory=set)        # ".lsproj", ".ino", "unreal", "xcode", "swift", "webxr-js", "nreal", "steamvr", "unity", "apk-release", "android-xr-loader"
    built_with: list[str] = field(default_factory=list)   # Devpost tags
    text: str = ""                                        # title + tagline + description + README + repo desc/topics
    links: list[str] = field(default_factory=list)        # all project links
    repo_language: str | None = None
    license_spdx: str | None = None                       # None = unknown; "NOASSERTION"/"" = no license file
    has_repo: bool = False


@dataclass
class Classification:
    platform: str
    platforms_detected: list[str]
    requirement_tier: str
    fidelity: str
    quest3_features: list[str]
    extra_hardware: list[str]
    license: str | None
    license_gate: str
    confidence: str
    evidence: list[str]

    def as_dict(self) -> dict:
        return dict(self.__dict__)


# --- helpers ---------------------------------------------------------------

def _rx(pattern: str, text: str) -> list[str]:
    return [m.group(0) for m in re.finditer(pattern, text, re.IGNORECASE)]


def _platforms_from_packages(packages: set[str]) -> tuple[list[str], list[str]]:
    found, ev = [], []
    for prefix, plat in PACKAGE_PLATFORMS:
        hits = sorted(p for p in packages if p.startswith(prefix))
        if hits:
            if plat not in found:
                found.append(plat)
            ev.append(f"manifest: {hits[0]} -> {plat}")
    xr_specific = bool(found)
    phone = sorted(p for p in packages if p.startswith(PHONE_AR_PACKAGES))
    if phone and not xr_specific:
        found.append("phone-ar")
        ev.append(f"manifest: {', '.join(phone)} only -> phone-ar")
    return found, ev


def _resolve_ambiguous(packages: set[str], text: str, found: list[str]) -> tuple[list[str], list[str]]:
    """windowsmr / bare openxr need context to resolve."""
    ev = []
    if any(p.startswith("com.unity.xr.windowsmr") for p in packages) and "hololens" not in found:
        if re.search(TEXT_PATTERNS["hololens"], text, re.I):  # hololens / mrtk
            found.append("hololens")
            ev.append("manifest: com.unity.xr.windowsmr + 'HoloLens' in text -> hololens")
        elif "pcvr" not in found and not found:
            found.append("pcvr")
            ev.append("manifest: com.unity.xr.windowsmr (no HoloLens mention) -> pcvr (WMR)")
    if "com.unity.xr.openxr" in packages and not found:
        if re.search(TEXT_PATTERNS["quest"], text, re.I):
            found.append("quest")
            ev.append("manifest: com.unity.xr.openxr + Quest mention -> quest")
        else:
            found.append("pcvr")
            ev.append("manifest: com.unity.xr.openxr only, no Android/Quest evidence -> pcvr (verify)")
    return found, ev


def _platforms_from_markers(s: Signals) -> tuple[list[str], list[str]]:
    m, found, ev = s.markers, [], []
    if any("horizon.meta.com" in l or "horizon.meta" in l for l in s.links):
        found.append("horizon-worlds"); ev.append("link: Horizon Worlds URL")
    if ".lsproj" in m:
        found.append("snap"); ev.append("repo: .lsproj (Lens Studio)")
    if "nreal" in m:
        found.append("ar-glasses"); ev.append("repo: Nreal project folder/SDK")
    if "steamvr" in m:
        found.append("pcvr"); ev.append("repo: SteamVR bindings")
    if "android-xr-loader" in m:
        found.append("quest"); ev.append("repo: XR loader configured for Android")
    if "apk-release" in m:
        found.append("quest"); ev.append("repo: .apk in releases")
    if "swift" in m or "xcode" in m:
        found.append("apple-native"); ev.append("repo: Swift/Xcode project")
    if "webxr-js" in m:
        found.append("webxr"); ev.append("repo: WebXR JS stack (A-Frame/three.js/8th Wall)")
    if "unreal" in m and not found:
        found.append("unreal"); ev.append("repo: Unreal project")
    if s.repo_language and s.repo_language.lower() == "swift" and "apple-native" not in found:
        found.append("apple-native"); ev.append("repo language: Swift")
    return found, ev


def _platforms_from_text(text: str, source: str) -> tuple[list[str], list[str]]:
    found, ev = [], []
    for plat, pattern in TEXT_PLATFORMS:
        hits = _rx(pattern, text)
        if hits:
            found.append(plat)
            ev.append(f"{source}: '{hits[0].strip()}' -> {plat}")
    return found, ev


def _pick(found: list[str], text: str) -> str:
    """Choose one primary platform from several candidates.

    Multi-target repos (e.g. GhostBustXR: Quest VR + phone AR) get the most
    Quest-friendly target, since that's what the museum will run.
    """
    if len(found) == 1:
        return found[0]
    # "phone" words are weak; drop phone-ar if a headset platform is also present.
    headset = [p for p in found if p != "phone-ar"]
    if headset:
        found = headset
    pref = ["horizon-worlds", "quest", "webxr", "pcvr", "android-xr", "hololens", "magic-leap",
            "ar-glasses", "vision-pro", "phone-ar", "snap", "social-filter", "apple-native",
            "holo-display", "unreal", "hardware-only", "unknown"]
    return min(found, key=lambda p: pref.index(p))


def license_gate(spdx: str | None) -> str:
    if spdx is None:
        return "unknown"
    s = spdx.lower()
    if s in LICENSE_PERMISSIVE:
        return "ok"
    if s in LICENSE_COPYLEFT:
        return "ok-copyleft"
    if s in ("", "none"):
        return "needs-permission"   # no license file: Watch-only unless the team grants permission
    return "review"


# --- main ------------------------------------------------------------------

def classify(s: Signals) -> Classification:
    text = s.text.lower()
    tags = " | ".join(t.lower() for t in s.built_with)
    evidence: list[str] = []
    confidence = "none"

    # Tier 1: manifest packages
    found, ev = _platforms_from_packages(s.packages)
    found, ev2 = _resolve_ambiguous(s.packages, text + " " + tags, found)
    ev += ev2
    if len(found) > 1:
        # Several XR plugins in one manifest (often template leftovers): let the
        # write-up break the tie if it names exactly one of them.
        named = [p for p in found if p in TEXT_PATTERNS and re.search(TEXT_PATTERNS[p], text, re.I)]
        if len(named) == 1:
            ev.append(f"text names {named[0]} -> preferred over {', '.join(p for p in found if p != named[0])}")
            found = named
    if found:
        confidence = "high"
    # Tier 2: structural markers
    if not found:
        found, ev = _platforms_from_markers(s)
        if found:
            confidence = "high"
    else:
        mfound, mev = _platforms_from_markers(s)
        # Horizon link / lsproj alongside Unity is still worth recording
        ev += [e for e in mev if e not in ev]
        found += [p for p in mfound if p not in found and p in ("horizon-worlds",)]
    evidence += ev
    # Tier 3: Devpost built-with tags
    if not found and tags:
        found, ev = _platforms_from_text(tags, "devpost tag")
        if found:
            confidence = "medium"
            evidence += ev
    # Tier 4: free text
    if not found:
        found, ev = _platforms_from_text(text, "text")
        if found:
            confidence = "low"
            evidence += ev

    # Extra hardware + Quest 3 features accumulate across all sources.
    hw_text = " ".join([text, tags, " ".join(sorted(s.markers))])
    extra_hw, essential_hw = [], False
    for label, pattern, essential in EXTRA_HARDWARE:
        hits = _rx(pattern, hw_text)
        if hits:
            extra_hw.append(label)
            essential_hw |= essential
            evidence.append(f"hardware: '{hits[0].strip()}' -> {label}")
    q3 = []
    for label, pattern in QUEST3_FEATURES:
        hits = _rx(pattern, text + " " + tags)
        if hits:
            q3.append(label)
            evidence.append(f"quest3: '{hits[0].strip()}' -> {label}")

    if not found and (s.repo_language or "").lower() in ("javascript", "typescript", "html"):
        found = ["webxr"]
        evidence.append(f"repo language {s.repo_language} with no other platform evidence -> assume webxr")
        confidence = "guess"

    if not found:
        if re.search(GENERIC_MR, text) or re.search(GENERIC_VR, text):
            # A "VR/MR experience" with no other hint is most often Quest.
            found = ["quest"]
            evidence.append("text: generic VR/MR wording only -> assume quest")
            confidence = "guess"
        elif re.search(GENERIC_AR, text):
            found = ["phone-ar"]
            evidence.append("text: generic AR wording only -> assume phone-ar")
            confidence = "guess"
        elif extra_hw and essential_hw:
            found = ["hardware-only"]
            evidence.append("no headset platform found; physical device project")
            confidence = "low"
        else:
            found = ["unknown"]

    platform = _pick(found, text + " " + tags)
    marker = bool(_rx(MARKER_TRACKING, text + " " + tags))

    # requirement_tier: what the original needs
    if essential_hw:
        tier = "extra-hardware"
    elif platform == "quest":
        tier = "quest3" if q3 else "quest2"
    elif platform in ("horizon-worlds", "webxr"):
        tier = "quest2"
    elif platform in ("pcvr", "unreal"):
        tier = "pcvr"
    elif platform == "unknown":
        tier = "unknown"
    else:
        tier = "non-quest"

    # fidelity: best the museum can offer on a Quest 2
    if platform in ("snap", "social-filter", "apple-native", "holo-display", "unreal", "unknown"):
        fidelity = "Watch"
    elif platform == "vision-pro":
        fidelity = "Ported" if any(p.startswith("com.unity.polyspatial") or p.startswith("com.unity.xr.visionos")
                                   for p in s.packages) else "Watch"
    elif essential_hw:
        fidelity = "Simulated"
    elif platform == "hardware-only":
        fidelity = "Simulated"
    elif platform in ("quest", "horizon-worlds"):
        fidelity = "Ported-reduced" if q3 else "Native"
    elif platform == "webxr":
        fidelity = "Browser"
    else:  # pcvr, hololens, magic-leap, ar-glasses, android-xr, phone-ar
        fidelity = "Ported-reduced" if (marker or q3) else "Ported"
    if marker and fidelity in ("Native", "Ported"):
        fidelity = "Ported-reduced"
        evidence.append("image/marker tracking -> hand-placed anchor on Quest 2")

    return Classification(
        platform=platform,
        platforms_detected=found,
        requirement_tier=tier,
        fidelity=fidelity,
        quest3_features=q3,
        extra_hardware=extra_hw,
        license=s.license_spdx,
        license_gate=license_gate(s.license_spdx),
        confidence=confidence,
        evidence=evidence,
    )
