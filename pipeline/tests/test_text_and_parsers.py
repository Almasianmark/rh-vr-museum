from rhm.devpost import DevpostProject, parse_gallery, parse_project
from rhm.repos import _android_in_xr_settings, link_kind, parse_repo_url
from rhm.text import names_match, slugify, synopsis

GALLERY = """
<div class="gallery-item">
  <a class="block-wrapper-link fade link-to-software" href="https://devpost.com/software/failtopia">
    <div class="software-entry"><figure><img src="t.png"><figcaption>
      <div class="software-entry-name entry-body"><h5>Failtopia</h5>
      <p class="small tagline">Welcome to FAILTOPIA where failures are celebrated.</p></div>
    </figcaption></figure></div>
  </a>
  <aside class="entry-badge"><img class="winner" src="w.png"></aside>
</div>
<div class="gallery-item">
  <a class="link-to-software" href="https://devpost.com/software/vriot"><h5>VRIoT</h5>
  <p class="small tagline">A New Dimension of Home Automation</p></a>
</div>
"""

PROJECT = """
<div id="app-details-left">
  <h2>Inspiration</h2><p>We fail a lot.</p>
  <h2>What it does</h2><p>Failtopia is an immersive multiplayer forest. Players record failure stories. They grow mushrooms.</p>
  <div id="built-with"><h2>Built With</h2><ul><li><span class="cp-tag">unity</span></li><li><span class="cp-tag">oculus-quest</span></li></ul></div>
</div>
<nav class="app-links"><ul data-role="software-urls">
  <li><a href="https://github.com/Reality-Hack-2023/Failtopia">GitHub Repo</a></li>
  <li><a href="https://youtu.be/xyz">Video</a></li>
</ul></nav>
<div id="gallery"><iframe class="video-embed" src="https://www.youtube.com/embed/xyz"></iframe></div>
<div id="submissions"><div class="software-list-content"><p><a href="#">MIT Reality Hack 2023</a></p>
<ul class="no-bullet"><li><span class="winner label">Winner</span> Best of Community</li></ul></div></div>
<section id="app-team"><ul>
<li class="software-team-member"><div class="bubble"><p>Design</p></div><a class="user-profile-link" href="/a"><img alt="Ada L"></a><a class="user-profile-link" href="/a">Ada L</a></li>
<li class="software-team-member"><a class="user-profile-link" href="/b"><img alt="Bo K"></a></li>
<li class="software-team-member"><a class="user-profile-link" href="/a">Ada L</a></li>
</ul></section>
"""


def test_parse_gallery():
    ps = parse_gallery(GALLERY)
    assert [p.title for p in ps] == ["Failtopia", "VRIoT"]
    assert ps[0].winner and not ps[1].winner
    assert ps[0].slug == "failtopia"
    assert ps[1].tagline == "A New Dimension of Home Automation"


def test_parse_project():
    p = parse_project(PROJECT, DevpostProject(title="Failtopia", url="https://devpost.com/software/failtopia"))
    assert p.built_with == ["unity", "oculus-quest"]
    assert p.links[0] == "https://github.com/Reality-Hack-2023/Failtopia"
    assert p.video_url.endswith("/embed/xyz")
    assert "immersive multiplayer forest" in p.sections["What it does"]
    assert "unity" not in p.sections["What it does"]
    assert p.prizes == ["Best of Community"] and p.winner
    assert p.team == ["Ada L", "Bo K"]
    assert "Try it out" not in p.sections


def test_synopsis_two_to_three_sentences():
    s = synopsis("Welcome to FAILTOPIA where failures are celebrated.",
                 {"What it does": "Failtopia is an immersive multiplayer forest. Players record failure stories. They grow mushrooms."})
    assert s.count(".") == 3
    assert s.startswith("Welcome to FAILTOPIA")
    assert synopsis("Tagline without period") == "Tagline without period."


def test_names_match():
    assert names_match("Spell Bound", "SpellBound")
    assert names_match("One Billion Lost: Australia's Wildfires VR Experience", "OneBillion")
    assert names_match("Team 71 - Bounce Ball", "Bounce")
    assert not names_match("Life", "LifeLinesAR")
    assert not names_match("Anything", "TEAM-30")


def test_slugify():
    assert slugify("Ide Træ") == "ide-trae"
    assert slugify("#³") == "3"


def test_repo_urls():
    assert parse_repo_url("https://github.com/Reality-Hack-2023/Failtopia.git") == ("github.com", "Reality-Hack-2023", "Failtopia")
    assert parse_repo_url("https://codeberg.org/reality-hack-2024/PlantAR") == ("codeberg.org", "reality-hack-2024", "PlantAR")
    assert parse_repo_url("https://github.com/orgs/foo") is None
    assert link_kind("https://horizon.meta.com/world/10/") == "horizon"
    assert link_kind("https://youtu.be/x") == "video"


def test_android_xr_settings_keys():
    assert _android_in_xr_settings("  Keys: 0100000007000000\n")
    assert not _android_in_xr_settings("  Keys: 01000000\n")


def test_detect_license_and_language():
    from rhm.repos import detect_license, infer_language
    assert detect_license("MIT License\n\nPermission is hereby granted, free of charge, to any person") == "MIT"
    assert detect_license("Apache License\n  Version 2.0, January 2004") == "Apache-2.0"
    assert detect_license("GNU GENERAL PUBLIC LICENSE\n  Version 3, 29 June 2007") == "GPL-3.0"
    assert detect_license("All rights reserved.") == "NOASSERTION"
    assert infer_language(["a.cs", "b.cs", "index.html"]) == "C#"
    assert infer_language(["README.md"]) is None
