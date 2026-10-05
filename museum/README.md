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

## Ratings (phase 3)

- **Star bar:** every plaque has a row of 5 stars. Push a star with a fingertip, or click it on desktop, to rate 1–5.
  - Gold means your own rating; silver means the museum average. The label reads `avg · count`.
  - The vote shows immediately and syncs within about 1 s.
  - Offline votes are queued in `PlayerPrefs` and retried.
- **Top-rated board:** on the lobby's south wall (behind spawn), showing the top 10 by Bayesian average.
- **Setup:** set `supabaseUrl` and `supabaseAnonKey` on **Museum Bootstrap**. If they're empty, ratings are off and the bars show the snapshot numbers from `museum.json`.
- Backend setup is in `supabase/README.md`.

## Installs & zones (phase 5)

Ports that passed the factory smoke test show up in `museum.json` as `app` (package, APK URL, SHA-256, bytes). For those paintings:

| Where you are | What happens |
|---|---|
| **Approach zone:** spine within 16 m of a wing's opening, or anywhere inside the wing | That wing's APKs download in the background, one at a time. They're **SHA-256 verified** on a worker thread, and a mismatch is discarded. |
| **Entrance zone:** first 4 m of the wing corridor | Everything downloaded installs in a batch. **Standalone:** Android shows one prompt per app; cancelling one skips the rest of the batch. **Companion:** the request goes to the PC or Pi, which installs silently. |
| **Push through an installed painting** | The app launches. The kit's return gesture (left Menu + right B, 1.5 s) brings you back to this painting. |
| **Push through a port that isn't installed yet** | You land in the theater, which shows the install state and has an **Install & play** button. |

- **Badges:** each painting with a port shows its state above the frame (playable port / downloading 42% / ready / installed / error). A sign at each wing entrance summarizes the wing.
- **Lobby kiosk:** next to spawn. Sets the install mode (Standalone / Companion), the storage budget (±2 GB, default 8 GB) and **Preload** (best-rated ports that fit).
- **Storage manager:**
  - Before installing, it uninstalls the least-recently-played ports first, weighted by rating: `hours since played × (6 − rating) / 3`.
  - It never evicts the batch being installed, or anything played in the last 15 minutes.
  - Installed size is estimated at 1.6× the APK size, and each APK is deleted once installed.
  - A download needs free disk for the APK, its installed size, and a 1 GB reserve.
  - The policy is pure C#: `dotnet test museum/Tests` (9 tests).
- **Android:**
  - `Assets/Plugins/Android/RHInstaller.java` installs through PackageInstaller sessions, with a dynamically registered status receiver that starts the confirmation prompt and reports back through `UnitySendMessage`.
  - `Editor/AndroidManifestPatch.cs` adds `REQUEST_INSTALL_PACKAGES`, `REQUEST_DELETE_PACKAGES` and `QUERY_ALL_PACKAGES` to the generated manifest. The museum is sideloaded, never Store-listed.
  - The first standalone install opens **Install unknown apps** for the museum. The visitor flips it once.
- **Editor / desktop:** installs are simulated (they finish after 1 s), so the zone flow can be walked without a headset.

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
| `Scripts/RatingsClient.cs` | Supabase over REST: anonymous sign-in + refresh, `project_scores`, own ratings, `rate_project`, offline queue |
| `Scripts/StarBar.cs` | Touchable 5-star bar (mesh stars, no font glyphs) + lobby top-rated board |
| `Scripts/Apps/AppManager.cs` | Zones, download queue + SHA-256, install batches, evictions, launch, companion file bridge |
| `Scripts/Apps/StoragePlanner.cs` | Pure eviction / preload / free-space policy (unit-tested with dotnet) |
| `Scripts/Apps/AndroidApps.cs` | JNI wrapper over `RHInstaller.java`; simulated in the editor |
| `Scripts/Apps/AppUi.cs` | Painting badges, wing entrance signs, lobby kiosk, theater Install button |
| `../Plugins/Android/RHInstaller.java` | PackageInstaller install/uninstall, launch, free space |
| `Editor/AndroidManifestPatch.cs` | Install permissions in the generated Android manifest |
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
