# Reality Hack VR Museum — Project Brief

A Quest VR museum of every MIT Reality Hack project. Walk the halls, touch a painting (ripple effect, Mario 64-style — **no Nintendo assets, models, textures, sounds, or castle layout**), and jump into that project. Each painting has a short synopsis, a hardware/fidelity badge, and 1–5 star ratings.

**Minimum target hardware: Meta Quest 2.** Anything that passes on Quest 2 runs on newer headsets.

## Where project data lives
- **2020, 2022, 2023:** central GitHub orgs — `MIT-Reality-Hack-2020` (~42 repos), `Reality-Hack-2022` (~82, mostly `TEAM-XX`), `Reality-Hack-2023` (~114).
- **2024:** no GitHub org — central repos are on Codeberg: `codeberg.org/reality-hack-2024` (~100 repos).
- **2025, 2026:** teams host their own repos (GitHub/Codeberg, sometimes Horizon Worlds links), linked from realityhack.world team pages and Devpost.
- **Devpost galleries are the master index for every year:** title, pitch, "Built with" tags, "Try it out" repo links, demo video.

## Architecture
1. **Pipeline (Python)** — Devpost galleries → GitHub API (README, LICENSE, `Packages/manifest.json`, releases) → hardware classifier → 2–3 sentence synopsis → `projects.json`. Rate-limit politely.
2. **Museum client (Unity + Meta XR SDK / OpenXR, C#)** — loads `museum.json` (built from `projects.json`) at runtime, so adding a year needs no rebuild. One wing per year; exhibits grouped by the device each project was designed for, **max 5 paintings per exhibit**; every Watch-only (inaccessible) project of a year goes in one large archive exhibit.
3. **Ratings backend (Supabase)** — `ratings(project_id, user_id, stars)`; sort by Bayesian average.

## Hardware classifier signals (strongest first)
1. `manifest.json`:
   - `com.meta.xr.*` / OpenXR + Android → Quest
   - `com.microsoft.mixedreality.*` → HoloLens
   - `com.unity.xr.magicleap` → Magic Leap
   - `polyspatial` / `visionos` → Vision Pro
   - ARKit/ARCore only → phone AR
2. Non-Unity markers:
   - `.lsproj` → Snap Lens Studio
   - `.ino` files / hardware folders → custom electronics
   - Horizon Worlds link → Quest native
3. Devpost tags + README keywords (HaptX, bHaptics, Ultraleap, Varjo, Vive trackers, OpenBCI, Qualcomm boards).
4. Quest 3-only features (Depth API, scene mesh, color passthrough, Passthrough Camera API) → reduced on Quest 2.

Output per project: `platform`, `requirement_tier` (quest2 / quest3 / pcvr / non-quest / extra-hardware), `fidelity` (Native / Ported / Ported-reduced / Simulated / Browser / Watch), `license`, plus a `manual_override` field.

## Compatibility kit (Unity package injected into every port)
- Rig swap to OpenXR XR Origin; input remap (air-tap / ML controller / SteamVR actions → Quest controllers or pinch).
- **Phone AR:** add `com.unity.xr.meta-openxr`, which provides passthrough, planes from Space Setup, raycasts, and anchors. Screen taps become ray/pinch taps; screen UI moves to a wrist tablet.
- **HoloLens / Magic Leap:** passthrough plus a luminance-to-alpha pass (black = transparent on additive displays); spatial mesh → Space Setup planes.
- **PC VR:** mobile performance pass — URP mobile settings, ASTC textures, no post-processing, fixed foveation, 72 Hz.
- **Image/marker tracking:** user places the marker anchor by hand (no camera access on Quest 2).
- **Custom hardware:** serial/BLE/OSC → simulated wrist device panel or controller mapping; haptic gloves → controller rumble.
- **WebXR:** launch in Quest Browser. **Horizon Worlds:** deep link.
- **Native Swift/RealityKit, Snap Lenses:** Watch mode (demo-video theater).
- `MuseumReturn` script: hold left Menu + right B for 1.5 s (Quest has only one app menu button) → relaunch the museum with `returnTo=<projectId>` and spawn at that painting.
- Shared branded splash screen in every build to mask OS app-switch transitions.

## Install / switching model
- The museum is sideloaded (the Horizon Store doesn't allow apps that install other apps).
- **Zones:**
  - Wing lobby (approach zone) → background-download APKs and verify SHA-256.
  - Wing entrance → install.
- **Install modes:**
  - Standalone: `PackageInstaller` with `REQUEST_INSTALL_PACKAGES`, one prompt per app, batched at the entrance.
  - Companion: PC or Raspberry Pi running ADB over Wi-Fi; silent `pm install -g` grants runtime permissions.
  - Preload: install everything that fits during setup.
- **Storage manager:** budget slider; when full, uninstall least-recently-played first, weighted by rating.
- **Later:** "cartridge pack" APKs that merge top projects sharing a Unity version and render pipeline, with scenes loaded additively for instant switching.

## Port factory
- GameCI Docker images per Unity version → recipe script (rig swap, kit injection, Android/ARM64/IL2CPP/ASTC, return button, splash) → smoke test on a real Quest 2 (install, launch, 60 s logcat crash/ANR check, FPS, screenshot) → triage queue.
- **Licensing gate:**
  - MIT and other permissive repos: rebuild with NOTICE file and team credit.
  - No license: Watch-only unless the team grants permission.

## Build order
1. **Scraper + classifier → `projects.json`** with real counts per year, platform, and fidelity tier. ← start here
2. Greybox museum + portal ripple shader + video theater (every project is at least watchable).
3. Ratings backend. (Supabase schema + RLS tests in `supabase/`, star bars in the client.)
4. Compat kit + port factory, starting with the top ~20 projects. (`kit/`, `factory/`, `python -m rhm.factory`; wave 1 queued in `factory/QUEUE.md`.)
5. Install/zone system + companion ADB script. (`museum/…/Scripts/Apps/`, `Plugins/Android/RHInstaller.java`, `companion/rh_companion.py`.)

## Current status
Read `STATUS.md` before starting work: what has been verified on a real Quest 2, known issues, and the next task (frame rate). The project is on Unity 6000.2.7f2.

## Owner
Mark (GitHub: Almasianmark). Prefers structured, direct, numbers-first communication.
