#!/usr/bin/env bash
# Build one prepared port inside a GameCI Unity image.
#   factory/docker_build.sh IMAGE PROJECT_DIR OUT_DIR PACKAGE_ID TITLE RECIPE
# License (one of):
#   UNITY_LICENSE + UNITY_EMAIL + UNITY_PASSWORD  Personal: .ulf contents; the serial is read from it
#   UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD   Plus/Pro seat
# A .ulf on its own does not work: it is bound to the machine that created it ("Machine bindings don't match").
set -euo pipefail
IMAGE=$1; PROJECT=$(cd "$2" && pwd); OUT=$(mkdir -p "$3" && cd "$3" && pwd)
export RH_PACKAGE=$4 RH_TITLE=$5 RH_RECIPE=$6
if [ -z "${UNITY_SERIAL:-}" ] && [ -n "${UNITY_LICENSE:-}" ]; then
  # Same derivation as GameCI's unity-builder: base64 DeveloperData, minus a 4-byte header.
  UNITY_SERIAL=$(printf "%s" "$UNITY_LICENSE" | tr -d '\r' | sed -n 's/.*<DeveloperData Value="\([^"]*\)".*/\1/p' | base64 -d | tail -c +5)
  if [ -n "${GITHUB_ACTIONS:-}" ] && [ -n "$UNITY_SERIAL" ]; then echo "::add-mask::$UNITY_SERIAL"; fi
  export UNITY_SERIAL
fi
if [ -z "${UNITY_SERIAL:-}" ] || [ -z "${UNITY_EMAIL:-}" ] || [ -z "${UNITY_PASSWORD:-}" ]; then
  echo "set UNITY_EMAIL and UNITY_PASSWORD plus UNITY_LICENSE (Personal .ulf) or UNITY_SERIAL" >&2; exit 64
fi
docker run --rm \
  -e UNITY_SERIAL -e UNITY_EMAIL -e UNITY_PASSWORD -e RH_PACKAGE -e RH_TITLE -e RH_RECIPE \
  -v "$PROJECT":/project -v "$OUT":/out "$IMAGE" bash -c '
# The activation log stays out of /out: that folder is uploaded as a public build artifact.
unity-editor -batchmode -nographics -quit -logFile /tmp/activate.log \
  -serial "$UNITY_SERIAL" -username "$UNITY_EMAIL" -password "$UNITY_PASSWORD" \
  || { echo "Unity licence activation failed:" >&2; grep -iE "licens|error" /tmp/activate.log | tail -30 >&2; exit 70; }
# Meta XR SDK does not compile on the Linux editor as shipped (run 38111085760, SDK 83.0.1 from packages.unity.com):
#  - OVRPlugin treats every editor but Windows/macOS as an unsupported platform, and some of those stubs are broken.
#    Counting the Linux editor as supported compiles the same code path the teams built with. Every platform
#    guard in those files needs it, not only the first: the native declarations sit behind a second one.
#  - The audio package still says PhysicMaterial, which Unity 6 only fixes through an interactive API-update prompt.
# Packages have to be downloaded before they can be patched, hence the extra resolve pass.
if grep -qs "com.meta.xr.sdk" /project/Packages/manifest.json /project/Packages/packages-lock.json; then
  unity-editor -batchmode -nographics -quit -logFile /out/resolve.log -projectPath /project -buildTarget Android
  grep -rlZ --include=*.cs "define OVRPLUGIN_UNSUPPORTED_PLATFORM" /project/Library/PackageCache 2>/dev/null \
    | xargs -0 -r sed -i "/^[[:space:]]*#\(if\|elif\)/s/UNITY_EDITOR_WIN/UNITY_EDITOR_WIN || UNITY_EDITOR_LINUX/"
  if grep -qs "^m_EditorVersion: 6000" /project/ProjectSettings/ProjectVersion.txt; then
    grep -rlZ --include=*.cs "PhysicMaterial" /project/Library/PackageCache/com.meta.xr.sdk.audio* 2>/dev/null \
      | xargs -0 -r sed -i "s/\bPhysicMaterial\b/PhysicsMaterial/g"
  fi
  echo "Meta XR SDK Linux fixups: $(grep -rl --include=*.cs "UNITY_EDITOR_WIN || UNITY_EDITOR_LINUX" /project/Library/PackageCache 2>/dev/null | wc -l) file(s) patched"
fi
unity-editor -batchmode -nographics -quit -logFile /out/unity.log -projectPath /project -buildTarget Android \
  -executeMethod RealityHack.MuseumKit.Editor.PortRecipe.Run \
  -rhOutput /out/app.apk -rhPackageId "$RH_PACKAGE" -rhProductName "$RH_TITLE" -rhRecipe "$RH_RECIPE"
# No -returnlicense: every GameCI container reports the same machine id, so returning the licence from one
# build revokes it for the builds still running in parallel (BattleFish, run 38113170675).
exit $?'
