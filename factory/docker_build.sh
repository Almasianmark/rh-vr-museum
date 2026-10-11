#!/usr/bin/env bash
# Build one prepared port inside a GameCI Unity image.
#   factory/docker_build.sh IMAGE PROJECT_DIR OUT_DIR PACKAGE_ID TITLE RECIPE
# License (one of), both returned after the build:
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
unity-editor -batchmode -nographics -quit -logFile /out/unity.log -projectPath /project -buildTarget Android \
  -executeMethod RealityHack.MuseumKit.Editor.PortRecipe.Run \
  -rhOutput /out/app.apk -rhPackageId "$RH_PACKAGE" -rhProductName "$RH_TITLE" -rhRecipe "$RH_RECIPE"
code=$?
unity-editor -batchmode -nographics -quit -returnlicense -username "$UNITY_EMAIL" -password "$UNITY_PASSWORD" || true
exit $code'
