# Status and handoff — 2026-10-08

Where the project stands after the first local Unity and Quest 2 sessions, and what to do next. Read this after `CLAUDE.md`.

## Summary

- All five build-order phases are written (commits `75b7514` … `cbc2d3c`).
- The museum client compiles, runs in the editor, builds for Android, and runs on a real Quest 2.
- **It did not hold frame rate on Quest 2**: 53 fps average against a 72 fps target. A perf pass with an in-headset A/B switch is now on `claude/busy-ride-2qcbjl` and needs one headset session to measure (see "Perf pass" below).
- Ratings, the port factory and the install/zone flow have not been exercised end to end.

## Where the code is

| Item | State |
|---|---|
| `main` | Phases 1–5 (PR [#1](https://github.com/Almasianmark/rh-vr-museum/pull/1), merged) |
| Branch | `claude/busy-ride-2qcbjl`: `main` + the perf pass, not merged yet |
| Local clone | `C:\Users\almas\rh-vr-museum` on Mark's Windows PC |


## What was verified locally

| Check | Result |
|---|---|
| Headless compile, Unity 6000.2.7f2, Input System enabled | Pass, no C# or shader errors |
| **RH Museum → Setup Project** | Pass; one benign TMP importer exception (see below) |
| Desktop Play test: lobby, wings, painting ripple and jump-in, theater, Back | Pass |
| Android build (IL2CPP, ARM64) and install on Quest 2 | Pass |
| Runs on Quest 2 with controllers | Pass, no crashes or Unity errors in 60 s of logcat |
| Frame rate on Quest 2 | **Fail** (details below) |

## Changes made outside the cloud session

1. **`PlayerRig.cs` compile fix.** With the Input System backend enabled, `using UnityEngine.InputSystem;` made `InputDevice` ambiguous with `UnityEngine.XR.InputDevice` (CS0104). The file now aliases only `Keyboard` and `Mouse`. The original compile check missed this because it ran with `ENABLE_INPUT_SYSTEM` undefined.
2. **Editor version.** The project is on **Unity 6000.2.7f2**, not 6000.0 LTS. URP moved from 17.0.4 to 17.2.0 to match. Check URP APIs against the 6000.2 branch from now on.
3. **Generated files committed:** `ProjectSettings/`, all `.meta` files, `Museum.unity`, URP assets, materials, TMP Essentials.
4. **Package list kept minimal.** Unity added its default packages (Ads, Analytics, In App Purchasing, Timeline, 2D) on first import; they were removed again. Do not re-add them.
5. **Quest XR config committed:** OpenXR on the Android tab, Meta Quest Support, Oculus Touch Controller Profile, single-pass instanced, active input handling = Both.

## Quest 2 performance capture

60 seconds of `VrApi` logcat while walking a wing and jumping into a painting.

| Measure | Result |
|---|---|
| Frame rate | 53 avg, 72 target, low of 22 |
| Seconds below target | 42 of 60 |
| Worst stretch | about 20 s locked at 36 fps |
| App GPU time per frame | 17–26 ms in slow stretches, 3–5 ms in fast ones (budget 13.9 ms) |
| GPU utilisation | 83–99% in slow stretches |
| CPU utilisation | 23–35% in slow stretches |
| Foveation | Off (`Fov=0`) |

**Reading:** GPU-bound and strongly view-dependent. Scripts, layout building and thumbnail streaming are not the cause. What was on screen during the slow stretches was not recorded, so the cause is not yet isolated.

**Facts that narrow it down:**
- `QuestURP.asset`: 4× MSAA, render scale 1.0, no HDR, opaque and depth textures off.
- The Foveated Rendering OpenXR feature is not enabled on Android.
- `Fade.shader` is the only transparent shader; its renderer is disabled at alpha 0.
- Every plaque, badge and sign is a separate TextMeshPro object (alpha-blended).
- Wings deactivate beyond 20 m, but nothing culls rooms inside the active wing.

**Suggested order of attack:**
1. Enable Foveated Rendering on Android and set a fixed foveation level at startup.
2. Compare 4× and 2× MSAA.
3. Review `PortalRipple.shader` and `Greybox.shader` fragment cost; the 48×30 canvas mesh with per-pixel waves is the main unknown.
4. Reduce text overdraw: cull or disable plaque text beyond reading distance.
5. Re-capture after each change. The test must be run by Mark on the headset; a cloud session cannot reach it.

## Perf pass (2026-10-08, cloud session, not yet measured)

Every fix can be toggled in the headset, so one session measures all of them. **Click the left thumbstick** (desktop: **P**) to cycle 8 modes; a yellow label shows the mode for 3 s. The app starts in `shipping`.

| # | Mode | What's on |
|---|---|---|
| 1 | `shipping` | Fixes A–E (the intended default) |
| 2 | `legacy` | None: should reproduce the 53 fps build (except the theater card and CJK font) |
| 3 | `foveation` | A only |
| 4 | `msaa2x` | B only |
| 5 | `ripple-idle` | C only |
| 6 | `text-cull` | D only |
| 7 | `room-cull` | E only |
| 8 | `shipping+gpu-boost` | A–E + F |

| Fix | Change | Files |
|---|---|---|
| A. Foveation | OpenXR **Foveated Rendering** feature enabled for Android (API: SRP Foveation, eye tracking off); `XRDisplaySubsystem.foveatedRenderingLevel = 1` (Quest: high) | `OpenXR Package Settings.asset`, `Perf/XRPlatform.cs` |
| B. MSAA 2× | URP asset's `msaaSampleCount` set at runtime (URP 17.2 pushes it to the XR display every frame); restored on exit so the asset on disk stays 4× | `Perf/PerfModes.cs` |
| C. Ripple idle | Untouched paintings draw a 4-vertex quad with a one-texture-fetch shader variant; touched/entered ones switch to the 1,500-vertex grid + wave variant (`RHM_RIPPLE_ON`) for 4.5 s | `PortalRipple.shader`, `PaintingView.cs` |
| D. Text | Painting text (plaque, badges, star label) hidden beyond 6 m (shown again under 6 m, hidden past 7.5 m); all labels use TMP's **Mobile** SDF shader | `Perf/MuseumCulling.cs`, `Greybox.cs`, `Resources/RHM_TextMobile.mat` |
| E. Room culling | A room's paintings draw only if their wall segment is inside the 2D wedge from the eye through the room's doorway. Walls always draw, so nothing shows a hole. Unit-tested (`museum/Tests/DoorwayTests.cs`) | `Perf/Doorway.cs`, `Perf/MuseumCulling.cs`, `MuseumBuilder.cs` |
| F. GPU boost | OpenXR **XR Performance Settings** feature enabled; GPU hint Boost vs Sustained High. Not in `shipping`: Boost can throttle when hot | `Perf/XRPlatform.cs` |

**Logcat lines** (tag `Unity`, already in the capture filter):
- `[RHPerf] start …`: device, graphics API, refresh rate, eye-texture size, MSAA. Foveation needs Vulkan.
- `[RHPerf] mode=… foveation=… msaa=… gpuHint=…` on every switch.
- `[RHPerf] stats mode=… fps=… min=… slow=n/frames gpu=…ms pos=x,z` every 2 s. GPU ms may read `n/a`; VrApi's `App=` has it either way.

Remove the switch once the numbers are in: keep the winning fixes as plain settings.

**Also in this pass:**
- **Theater:** the project's details and star bar are now on a lectern card about 2 m from the viewpoint. The screen has no plaque.
- **CJK:** on Android, the system Noto CJK font is added as a dynamic TMP fallback. Only one project (Chado XR) needs it.
- **Emoji:** `layout.py` strips emoji from titles, taglines, synopses and prizes. TMP SDF can't draw color emoji.
- **Compile check:** the cloud check now stubs the Input System `InputDevice` and `CommonUsages`, and it reproduces the CS0104 error when `using UnityEngine.InputSystem;` is added.
- **Not compile-checked:** the two `foveatedRendering*` lines. The reference DLLs predate them, so they were checked against the 6000.2 scripting docs instead.

## Known issues

| Issue | Severity | Notes |
|---|---|---|
| Frame rate below 72 on Quest 2 | High | Perf pass written; needs the A/B capture above |
| Theater plaque unreadable | Medium | Replaced by a lectern card; not yet seen in the headset |
| Wall plaque body text small | Low | Not yet judged in the headset |
| Missing glyphs | Low | Emoji stripped by the pipeline; CJK uses the system font on Android (untested). In the editor CJK still shows boxes. |
| TMP Essentials import in `MuseumSetup.ImportTmpEssentials` | Low | Logs "Import TMP Essentials manually" and throws `ArgumentNullException` inside TMP's importer, but the resources are imported. Now committed, so new clones skip this path. |

## Not yet tested

- **Supabase ratings: hosted project not created yet.** Everything else is ready and was verified against a real local Supabase stack (`supabase/smoke.py`: all checks pass with publishable and legacy anon keys). Create the project in the dashboard, then run `python supabase/setup.py ...`; see `supabase/README.md`. Headsets pick it up from `museum.json`, with no rebuild.
- **Port factory: no Unity build yet.** First ports are BattleFish, CAREGIVR and Memory Tree, prepared and inspected in the cloud. Build them on the PC with `factory\build_local.ps1` (see `factory/README.md`). 11 of the 2024 Codeberg repos lost their Git LFS assets and are in `factory/blocked.json`.
- Install/zone system and `companion/rh_companion.py`: no ported APK exists to install, so only the simulated editor path has run.
- `MuseumReturn` relaunch round trip.

## Local environment (Mark's PC)

- Windows 11, Unity Hub, Unity 6000.2.7f2 with Android Build Support (SDK, NDK, OpenJDK).
- Quest 2 in developer mode, USB debugging authorised for this PC.
- `adb` path: `C:\Program Files\Unity\Hub\Editor\6000.2.7f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe`
- Installed package: `world.realityhack.museum` (Library → Unknown Sources → Reality Hack Museum).

## What needs a local machine

A cloud session can write and compile-check code. These steps need Mark's PC and headset:

- Opening the project in the Unity editor and pressing Play.
- Android builds (no Unity licence or Android toolchain in the cloud container).
- Anything on the Quest 2: install, launch, logcat, frame-rate capture.

Hand these back as short, numbered instructions and ask for the Console output or a logcat capture in return.

## Performance capture command

Run on the PC with the museum running on the headset:

```powershell
$adb = "C:\Program Files\Unity\Hub\Editor\6000.2.7f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
& $adb logcat -c
& $adb logcat -v time -s VrApi Unity
```

Stop with Ctrl+C after 60 seconds (about 3 minutes for the 8-mode A/B run). To keep a copy, add `| Tee-Object perf.txt` to the second command. `FPS=a/b` is achieved/target, `App=` is GPU time per frame, and `GPU%` / `CPU%` are utilisation.
