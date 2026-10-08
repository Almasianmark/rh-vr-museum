# Status and handoff — 2026-10-08

Where the project stands after the first local Unity and Quest 2 sessions, and what to do next. Read this after `CLAUDE.md`.

## Summary

- All five build-order phases are written (commits `75b7514` … `cbc2d3c`).
- The museum client compiles, runs in the editor, builds for Android, and runs on a real Quest 2.
- **It does not hold frame rate on Quest 2**: 53 fps average against a 72 fps target. This is the next thing to fix.
- Ratings, the port factory and the install/zone flow have not been exercised end to end.

## Where the code is

| Item | State |
|---|---|
| Branch | `claude/busy-ride-2qcbjl` holds everything |
| `main` | Only `CLAUDE.md` until PR #1 is merged |
| PR | [#1](https://github.com/Almasianmark/rh-vr-museum/pull/1), open, no conflicts |
| Local clone | `C:\Users\almas\rh-vr-museum` on Mark's Windows PC |

A cloud session started from the web begins on the default branch. Until PR #1 is merged, pick `claude/busy-ride-2qcbjl` explicitly or the session sees an empty repo.

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

## Known issues

| Issue | Severity | Notes |
|---|---|---|
| Frame rate below 72 on Quest 2 | High | See above |
| Theater plaque unreadable | Medium | Same ~6 cm text as wall plaques, viewed from about 7 m. Needs the screen and plaque rebalanced in `VideoTheater.BuildRoom` / `PaintingView.Build`. |
| Wall plaque body text small | Low | Not yet judged in the headset |
| Missing glyphs | Low | CJK characters and emoji in project names render as boxes; LiberationSans SDF has no fallback font |
| `remote museum.json unavailable (404)` | Low | Clears when PR #1 is merged; the bundled copy is used meanwhile |
| TMP Essentials import in `MuseumSetup.ImportTmpEssentials` | Low | Logs "Import TMP Essentials manually" and throws `ArgumentNullException` inside TMP's importer, but the resources are imported. Now committed, so new clones skip this path. |

## Not yet tested

- Supabase ratings: no project created, `supabaseUrl` / `supabaseAnonKey` are empty, so star bars show snapshot numbers only.
- Port factory: wave 1 (20 projects) is queued in `factory/QUEUE.md`; no port has been built or smoke-tested.
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

Stop with Ctrl+C after 60 seconds. `FPS=a/b` is achieved/target, `App=` is GPU time per frame, and `GPU%` / `CPU%` are utilisation.
