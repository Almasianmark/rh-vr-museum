import pytest

from rhm.classify import Signals, classify, license_gate


def c(**kw):
    return classify(Signals(**kw))


@pytest.mark.parametrize("packages,platform,tier,fidelity", [
    ({"com.meta.xr.sdk.all"}, "quest", "quest2", "Native"),
    ({"com.unity.xr.oculus"}, "quest", "quest2", "Native"),
    ({"com.microsoft.mixedreality.toolkit.foundation"}, "hololens", "non-quest", "Ported"),
    ({"com.unity.xr.magicleap"}, "magic-leap", "non-quest", "Ported"),
    ({"com.unity.polyspatial"}, "vision-pro", "non-quest", "Ported"),
    ({"com.unity.xr.arkit", "com.unity.xr.arfoundation"}, "phone-ar", "non-quest", "Ported"),
    ({"com.valvesoftware.unity.openvr"}, "pcvr", "pcvr", "Ported"),
])
def test_manifest_signals(packages, platform, tier, fidelity):
    r = c(packages=packages)
    assert (r.platform, r.requirement_tier, r.fidelity, r.confidence) == (platform, tier, fidelity, "high")


def test_arfoundation_with_headset_plugin_is_not_phone_ar():
    assert c(packages={"com.unity.xr.arfoundation", "com.unity.xr.oculus"}).platform == "quest"


def test_openxr_alone_needs_context():
    assert c(packages={"com.unity.xr.openxr"}).platform == "pcvr"
    assert c(packages={"com.unity.xr.openxr"}, text="Built for Meta Quest 2").platform == "quest"


def test_windowsmr_hololens_vs_wmr():
    assert c(packages={"com.unity.xr.windowsmr"}, text="HoloLens 2 app").platform == "hololens"
    assert c(packages={"com.unity.xr.windowsmr"}, text="VR surgery viz").platform == "pcvr"


def test_text_breaks_manifest_tie():
    r = c(packages={"com.unity.xr.oculus", "com.unity.xr.magicleap"}, text="A Magic Leap tool")
    assert r.platform == "magic-leap"


def test_manifest_beats_text():
    r = c(packages={"com.unity.xr.oculus"}, built_with=["hololens"], text="hololens")
    assert r.platform == "quest" and r.confidence == "high"


def test_structural_markers():
    assert c(markers={".lsproj"}).platform == "snap"
    assert c(markers={".lsproj"}).fidelity == "Watch"
    assert c(markers={"webxr-js"}).fidelity == "Browser"
    assert c(markers={"unreal"}).fidelity == "Watch"
    assert c(links=["https://horizon.meta.com/world/123"]).platform == "horizon-worlds"
    assert c(repo_language="Swift").platform == "apple-native"


def test_devpost_tags_are_medium_confidence():
    r = c(built_with=["Unity", "Oculus Quest", "C#"])
    assert (r.platform, r.confidence) == ("quest", "medium")


def test_quest_word_alone_is_not_quest():
    # "Rehab Quest", "Hacker Quest" are titles, not hardware.
    assert c(text="Rehab Quest. Quest your way to recovery.").platform == "unknown"


def test_quest3_features_reduce():
    r = c(text="Mixed reality fire drill using the Quest 3 Depth API")
    assert r.requirement_tier == "quest3"
    assert r.fidelity == "Ported-reduced"
    assert "depth-api" in r.quest3_features


def test_extra_hardware_simulated():
    r = c(packages={"com.unity.xr.oculus"}, markers={".ino"})
    assert (r.requirement_tier, r.fidelity) == ("extra-hardware", "Simulated")
    assert "arduino" in r.extra_hardware


def test_ultraleap_is_not_essential():
    r = c(packages={"com.unity.xr.oculus"}, text="hand tracking with Ultraleap")
    assert r.fidelity == "Native" and "ultraleap" in r.extra_hardware


def test_marker_tracking_reduced():
    assert c(text="ARKit image tracking of posters").fidelity == "Ported-reduced"


def test_fallbacks():
    assert c(text="An immersive VR meditation").confidence == "guess"
    assert c(text="An AR scavenger hunt").platform == "phone-ar"
    assert c(text="A haptic glove made of cardboard").platform == "hardware-only"
    assert c(text="TBC").platform == "unknown"
    assert c(text="TBC").fidelity == "Watch"
    assert c(repo_language="JavaScript").platform == "webxr"


def test_license_gate():
    assert license_gate("MIT") == "ok"
    assert license_gate("GPL-3.0") == "ok-copyleft"
    assert license_gate("") == "needs-permission"
    assert license_gate("NOASSERTION") == "review"
    assert license_gate(None) == "unknown"


def test_bare_quest_devpost_tag():
    r = c(built_with=["blender", "oculus-quest", "unity"])
    assert (r.platform, r.confidence) == ("quest", "medium")
    assert c(built_with=["quest", "unity"]).platform == "quest"
