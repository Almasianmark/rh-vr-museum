# Museum client (phase 2: greybox)

A Unity 6 project that builds the whole museum at runtime from `data/museum.json`. It has no hand-built scene, so adding a year or re-grouping exhibits needs no rebuild.

## Open and run (about 5 minutes)

1. Unity Hub → **Add project from disk** → `rh-vr-museum/museum`, using **Unity 6000.0 LTS** or newer. The first import resolves `Packages/manifest.json` (URP 17, OpenXR 1.18, Input System 1.20).
2. Menu **RH Museum → Setup Project**. It does the following:
   - creates and assigns a Quest-tuned URP asset (no HDR, no shadows, 4× MSAA)
   - creates the shader materials in `Assets/RHMuseum/Resources/`
   - imports TMP Essentials
   - copies `../data/museum.json` → `Assets/StreamingAssets/`
   - creates `Assets/RHMuseum/Scenes/Museum.unity`
   - sets Android/Quest player settings (IL2CPP, ARM64, ASTC, Linear, minSdk 32)
3. **Desktop test:** press Play. WASD moves, right-drag looks, left-click touches or pushes a painting, Esc goes back.
4. **Quest:**
   - Project Settings → XR Plug-in Management → **Android** tab → tick **OpenXR**.
   - Under OpenXR: enable **Meta Quest Support** and add **Oculus Touch Controller Profile**.
   - Switch the platform to Android, then Build And Run with the headset connected over USB.

After the pipeline reruns, use **RH Museum → Refresh museum.json**. Installed builds also fetch `remoteMuseumUrl` (the `MuseumBootstrap` field, default: raw `data/museum.json` on `main`) and cache it. If that URL isn't reachable (for example, a private repo), they use the last cached copy, then the bundled one.

## Controls (Quest)

| Input | Action |
|---|---|
| Left stick | Move (head-relative) |
| Right stick | Snap turn 30° |
| Fingertip on a painting | Ripple; drag for a trail |
| Push 7 cm into the painting | Jump in (swirl + fade) |
| B / Y | Back: theater → painting you came from; museum → lobby |

## Layout

The layout comes from `pipeline/rhm/layout.py` and is drawn by `MuseumBuilder.cs`.

```
   west (−X)                      east (+X)
   2026 wing ◄──┐            ┌──► 2025 wing
   2024 wing ◄──┤   spine    ├──► 2023 wing
   2022 wing ◄──┤     ║      ├──► 2020 wing
                    [Lobby]  ← spawn, facing the spine (+Z)
```

- **Wings:** one per year, in pairs off the spine.
- **Corridors:** each wing's corridor has **device rooms** on both sides, one per (year × device) group, with **at most 5 paintings** each: back wall, then two per side wall.
- **Archives:** the wing ends in the year's **archive hall**. It holds every project a Quest can't run (fidelity *Watch*), uncapped, hung in two tiers on all walls.
- **Lobby:** a frame-color legend and a wing directory.
- **Theater:** a separate room (out of sight at z = −400) that you teleport into.

Current data: 6 wings, 101 exhibits (95 device rooms + 6 archives), 444 paintings.

## What touching does today

| `launch.kind` | Result |
|---|---|
| `browser` (WebXR) | Opens the project's URL in Quest Browser; you're still at the painting when you return |
| `horizon` | Opens the Horizon Worlds link |
| `theater` (everything else, for now) | Teleports you to the video theater. The project fills the big screen with its full plaque. **Play** opens the YouTube/Vimeo demo in Quest Browser; direct `.mp4`/`.webm` files play on the screen. |

Native/ported projects go to the theater until build-order step 5 (APK install and launch) exists. `LaunchArgs.ReturnTo()` already reads the `returnTo` intent extra (or `-returnTo=<id>` on desktop) and spawns you in front of that painting, so the `MuseumReturn` kit script only has to relaunch with the extra.

## Code map (`Assets/RHMuseum/`)

| File | Role |
|---|---|
| `Scripts/MuseumData.cs` | `museum.json` DTOs + loader (remote → cache → StreamingAssets) |
| `Scripts/MuseumBuilder.cs` | Lobby / spine / wings / rooms / archive geometry and painting slots |
| `Scripts/PaintingView.cs` | Canvas mesh (48×30 grid), frame in the fidelity color, plaque, ripple and enter state, thumbnail crop |
| `Scripts/TouchTarget.cs` | Fingertip contact/push detection on flat rectangles; desktop click |
| `Scripts/PlayerRig.cs` | Head/hands via `UnityEngine.XR.InputDevices` (no XRI), locomotion, snap turn, desktop fallback |
| `Scripts/MuseumBootstrap.cs` | Entry point: build, spawn/return, jump-in and back transitions, wing and thumbnail streaming |
| `Scripts/TheaterAndFx.cs` | Video theater, touch buttons, screen fader |
| `Shaders/PortalRipple.shader` | Original ripple + swirl portal effect (damped circular waves, normal displacement, refraction); URP, single-pass-instanced stereo |
| `Shaders/Greybox.shader` | Unlit fake-lit greybox with a 1 m grid (no realtime lights) |
| `Editor/MuseumSetup.cs` | The setup menu above |

## Quest 2 performance choices

- Unlit shaders only; no realtime lights or shadows.
- Wings outside 20 m are deactivated.
- Thumbnails load within 14 m (2 concurrent downloads) and unload beyond 30 m. Mipmaps are built on load, and the CPU copy is dropped.
- Target is 72 Hz.

## Status: what was and wasn't verified

- ✅ The runtime C# compiles against Unity's engine reference assemblies (`UnityEngine.Modules` 2021.3 from NuGet). The TextMeshPro and Input System calls were checked only against small hand-written stubs.
- ✅ The URP API calls in `MuseumSetup.cs` were checked against Unity's `Graphics` repo (6000.0 branch).
- ❌ **Not yet opened in the Unity editor or run on a headset.** Shaders haven't been compiled by Unity, and nothing has been profiled on a Quest 2. Expect a first round of fixes when you open it.
- `.meta` files aren't committed; Unity generates them on first import. Commit them after your first open so GUIDs stay stable.
