# Companion (phase 5): silent installs over ADB Wi-Fi

A PC or Raspberry Pi on the same network as the Quest installs the museum's ported apps with `adb install -r -g`. That means no per-app prompt, and runtime permissions are granted up front. There's no server: the companion and the museum talk through two files in the museum's app folder on the headset.

| File (`/sdcard/Android/data/world.realityhack.museum/files/companion/`) | Written by | Contents |
|---|---|---|
| `requests.json` | museum, atomically (temp file + rename) | `{seq, install:[{package, apk_url, sha256, bytes, project_id}], uninstall:[package]}` |
| `status.json` | companion | `{seq_done, installed:[package], message, updated}` |

## Setup

1. On the Quest: Settings → Developer → **Wireless ADB** on. Note the IP address.
2. On the PC or Pi (Python 3.9+ and `adb` installed; on Raspberry Pi OS: `sudo apt install adb`):
   ```bash
   python3 companion/rh_companion.py connect 192.168.1.42
   python3 companion/rh_companion.py watch
   ```
3. In the museum lobby kiosk, select **Companion**. Walking into a wing's entrance now asks the companion to install that wing's ports silently.

**Preload during setup** (before visitors arrive) installs the best-rated ports that fit the budget:
```bash
python3 companion/rh_companion.py preload --museum data/museum.json --budget-gb 24
```
The kiosk's **Preload** button does the same through the request file.

## Safety

- **Checksums:** every APK is SHA-256 checked against `museum.json` before `adb install`. A mismatch is deleted and reported.
- **Uninstall limits:** it only uninstalls `world.realityhack.p*` packages (museum ports), never anything else on the headset.
- **Cache:** downloads go to `~/.cache/rh-companion` (override with `RH_COMPANION_CACHE`) and are reused across headsets.

## Tests

`python3 -m pytest companion/tests`: 5 tests against a fake headset (`adb` replaced by a stub). They cover:
- serving a request, including install, uninstall and the status report
- not re-serving the same `seq`
- a SHA mismatch never reaching the headset
- refusing to uninstall non-museum packages
- reusing the APK cache
- preload selection under a budget
