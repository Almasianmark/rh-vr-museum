# Port factory (phase 4)

The factory takes a Reality Hack Unity project, rebuilds it as a Quest 2 APK with the museum kit injected, smoke-tests it on a real headset, and reports the result.

```
data/projects.json ─► queue ─► prepare (clone + inject) ─► GameCI build ─► smoke (Quest 2, adb) ─► triage
                      data/port_queue.json   kit/…museum-kit   build-report.json   smoke.json   data/port_status.json
```

## Wave 1: 20 projects (`QUEUE.md`)

- **Who qualifies:** the repo exists and is a Unity project with a known version, the license allows rebuilding (MIT etc.; 170 projects qualify), fidelity isn't Watch, and it isn't in `blocked.json`.
- **Ranking:**
  1. Bayesian rating (no ratings exist yet)
  2. prize winner
  3. Quest 2 before Quest 3-only (Depth API etc.), since Quest 2 is the minimum target
  4. ease: `native` < `openxr` < `passthrough` < custom hardware
  5. newer Unity
- **Quotas:** 3 `openxr` and 3 `passthrough` slots are reserved so wave 1 exercises every recipe.
- **Result:** 14 Quest-native winners, 3 OpenXR swaps (2 Android XR, 1 PC VR) and 3 passthrough ports (Vision Pro, AR glasses, phone AR). Unity versions range from 2021.3 to 6000.3.
- **Per project, the queue resolves:**
  - the exact **GameCI image**, checked against Docker Hub
  - the editor **changeset**, so Unity Hub's CLI can install that exact version
  - **XR package versions** from the Unity registry: the highest release compatible with that project's Unity
  - the **minimum versions** those packages need
  - an Android **package ID** of the form `world.realityhack.p<year>.<title>`

Regenerate with `cd pipeline && python -m rhm.factory queue [--size N]`. With `SUPABASE_URL` and `SUPABASE_ANON_KEY` set, ratings drive the ranking.

### Git LFS: many repos lost their assets (`blocked.json`)

Many hackathon repos keep models, textures and audio in Git LFS. A port built from LFS pointer files compiles but ships broken assets. So `prepare` now runs `git lfs pull` and fails if anything an Android build reads is still a pointer. It ignores PDFs, zips and desktop-only plugins.

`python -m rhm.factory lfscheck` fetches every LFS object of the wave's candidates in rank order. Projects whose host answers "Not Found" go into `blocked.json`; projects that pass are cached in `lfs_ok.json`. It repeats until the wave is full.

**Found 2026-10-09:** 11 of the 2024 Codeberg repos are missing most of their LFS objects (for example Beesper 56/58, GEOQUEST 719/727). The 2024 mirror was imported without them. Codeberg serves LFS fine for the repos that have it (HeaVR, Legacy, snAIder).

**Not yet verified:** GitHub-hosted repos. The cloud sandbox's git proxy refuses LFS for repos outside the session, so those checks are inconclusive there and nothing was blocked on that basis. Run `lfscheck` from a normal machine, or let `prepare` catch them.

## First ports: BattleFish, CAREGIVR, Memory Tree

All three are prize winners and Quest 2 natives with no LFS. Each was prepared and inspected in the cloud.

| Port | Unity | Entry scene | Notes |
|---|---|---|---|
| BattleFish | 6000.0.33f1 | `FinalScenes/FinalScene` | Meta SDK 83 + OpenXR loader |
| CAREGIVR | 2022.3.19f1 | `IntroScene` → 4 more | Oculus XR loader; Movement SDK from a git URL. The team's own APK is on itch.io for comparison. |
| Memory Tree | 6000.3.2f1 | `SampleScene` | The other 3 scenes are empty 8 KB stubs |

**Skipped:**
- **OnBook (#3):** needs two colocated headsets, Photon and Quest 3 color passthrough, so a solo Quest 2 smoke test can't judge it.
- **A "Fire" Training App:** LFS assets are gone, and it's Quest 3 Depth API.

**Build them on Windows (no Docker; uses the Unity licence in Unity Hub):**

```powershell
powershell -ExecutionPolicy Bypass -File factory\build_local.ps1          # the 3 above
powershell -ExecutionPolicy Bypass -File factory\build_local.ps1 -Ids 2024-heavr -SkipSmoke
```

The script does the following:
1. **Prepare:** clones the repo, pulls LFS, injects the kit.
2. **Editor:** installs the exact editor with Android SDK/NDK/JDK through Unity Hub's CLI if it's missing (`-AllowNewerPatch` uses an installed editor of the same minor version instead).
3. **Build:** runs PortRecipe in batch mode.
4. **Smoke test:** on the USB-connected Quest.
5. **Triage:** writes `data/port_status.json` and `TRIAGE.md`.

It was dry-run on Linux pwsh 7.4 with a stub editor to check the argument passing; it has not run on Windows yet.

## Recipes

| Recipe | Platforms | What the factory does automatically | Left for triage |
|---|---|---|---|
| `native` | quest, horizon-worlds | Keeps the project's own Quest loader (Oculus XR / Meta SDK / OpenXR). Adds XR Management if missing. Applies Android settings. | — |
| `openxr` | pcvr, android-xr, unreal | Strips PC-only XR plugins (OpenVR, WMR, Vive). Adds OpenXR and enables Meta Quest Support plus Oculus Touch. | Rig check, mobile perf pass |
| `passthrough` | phone-ar, hololens, magic-leap, ar-glasses, vision-pro | Same as `openxr`, plus strips ARKit / Magic Leap / visionOS / PolySpatial. Adds AR Foundation + Meta OpenXR (session, camera, plane, raycast features). Kit `PassthroughCompat` adds an ARSession + ARCameraManager and clears to transparent. | Rig swap, tap → trigger/pinch, marker anchors |
| + custom hardware | any `Simulated` | — | Simulated wrist panel / controller mapping |

Every port also gets:
- **Kit:** `kit/com.realityhack.museum-kit`, embedded in `Packages/`.
- **Credits:** `Assets/RHKitGenerated/Resources/rhkit.json`, used by the splash and the return gesture.
- **`NOTICE.txt`:** team credit, source repo, changes made, and the original license text.
- **Version fixes:** Unity honors a project's direct version pins, so the injector raises any pin below what the added packages need (for example, AR Foundation 6.0.3 → 6.6.2 for Meta OpenXR 2.6.1).
- **Untouched:** the project's own scenes and code.

## Kit (`kit/com.realityhack.museum-kit`)

- **`KitBootstrap`:** a `RuntimeInitializeOnLoadMethod` hook, so no scene edits are needed. It spawns the kit when `rhkit.json` exists.
- **`KitOverlay`:** a branded splash with credits at launch, masking the OS app switch. It uses a `TextMesh` with a built-in font and a CG shader, so it works in Unity 2019–6000, in the built-in pipeline or URP, with or without TextMeshPro.
- **`MuseumReturn`:** hold **left Menu + right B for 1.5 s** to relaunch the museum with `returnTo=<projectId>`. A Quest has only one app menu button; the right "menu" is the system button. It tries `getLaunchIntentForPackage` first and falls back to an explicit component name, because Android 11+ package visibility can hide the museum. The museum handles both cold and warm starts (`OnApplicationFocus`) and spawns you in front of the painting.
- **`PortRecipe` (editor, batch mode):**
  - sets Android, IL2CPP, ARM64, ASTC, minSdk 29 and Linear
  - assigns an Android XR loader through XR Management, keeping an existing one
  - enables the OpenXR features
  - builds the project's scene list (or every scene if the list is empty)
  - writes `build-report.json`
- **Language level:** C# 7.3 throughout, so it compiles in Unity 2019 projects.

## Run it

```bash
# 1. Prepare one project (clone at HEAD, inject). Prints the build command.
cd pipeline && python -m rhm.factory prepare 2026-battlefish --work ../factory/work

# 2. Build in GameCI. Needs Docker + a Unity license:
#    UNITY_LICENSE=<.ulf contents>  or  UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD
bash ../factory/docker_build.sh <image> <project> <out> <package_id> <title> <recipe>   # (the printed command)

# 3. Smoke test on a Quest 2 in developer mode, connected over USB:
#    install, launch, 60 s logcat, crash/ANR check, VrApi FPS, screenshot
python -m rhm.factory smoke 2026-battlefish --apk ../factory/work/_out/2026-battlefish/app.apk

# 4. Triage: merge reports -> data/port_status.json + factory/TRIAGE.md
python -m rhm.factory triage
```

**CI:** copy `factory/port-factory.yml` to `.github/workflows/`. My session token couldn't push workflow files, so this is the one manual step. Then run Actions → port-factory → Run workflow. It needs the `UNITY_LICENSE` repo secret, or the serial/email/password trio. Each queued project gets its own job (4 in parallel), and the APK, build report and Unity log are uploaded as artifacts. Smoke tests stay local because they need a physical Quest 2.

**Smoke verdicts:**

| Verdict | Meaning |
|---|---|
| `pass` | Alive at 60 s, no crash or ANR, median FPS ≥ 65 (90% of 72), loading period excluded |
| `perf` | Runs, but below that FPS |
| `crash` | `FATAL EXCEPTION` / `Fatal signal` / process gone |
| `anr` | Android reported "app not responding" |
| `no-fps-data` | Couldn't read FPS from logcat |
| `install-failed` | The APK wouldn't install |

`ready` ports (with SHA-256 and package ID) are what phase 5's install zones will consume.

## Verified vs not

- ✅ **Python logic:** pytest covers ranking and quotas, the version resolver, injection (strip, add, upgrade, keep existing loader), logcat verdicts and triage.
- ✅ **Real queue:** generated from live registry and Docker Hub data.
- ✅ **Real injection:** run on two actual wave-1 repos (sparse checkouts). BattleFish was left untouched apart from the kit. Paw Pals had visionOS stripped, OpenXR + Meta OpenXR added, and AR Foundation and core-utils raised.
- ✅ **Kit runtime:** compiles against UnityEngine reference assemblies at C# 7.3.
- ✅ **XR API names:** every reflection target in `PortRecipe` was checked against XR Management 4.4.0, OpenXR 1.13.2 and Meta OpenXR 1.0.1 package source.
- ✅ **Prepared for real (2026-10-09):** BattleFish, CAREGIVR and Memory Tree were cloned, LFS-checked, injected and inspected (scene lists, loaders). The whole wave was LFS-surveyed.
- ❌ **No Unity build has run yet.** That needs a Unity licence, which the sandbox doesn't have. Use `build_local.ps1` on the PC.
- ❌ **No smoke test has run yet.** That needs a Quest 2.
- **Not built:** the luminance-to-alpha pass for additive-display ports. It needs a per-pipeline full-screen render pass, so it's left as a triage step.
