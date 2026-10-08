#!/usr/bin/env bash
# Build one prepared port inside a GameCI Unity image.
#   factory/docker_build.sh IMAGE PROJECT_DIR OUT_DIR PACKAGE_ID TITLE RECIPE
# License (one of):
#   UNITY_LICENSE                               contents of a .ulf (Personal; see game.ci/docs activation)
#   UNITY_SERIAL + UNITY_EMAIL + UNITY_PASSWORD  Plus/Pro seat (returned after the build)
set -euo pipefail
IMAGE=$1; PROJECT=$(cd "$2" && pwd); OUT=$(mkdir -p "$3" && cd "$3" && pwd)
export RH_PACKAGE=$4 RH_TITLE=$5 RH_RECIPE=$6
if [ -z "${UNITY_LICENSE:-}" ] && [ -z "${UNITY_SERIAL:-}" ]; then
  echo "set UNITY_LICENSE or UNITY_SERIAL/UNITY_EMAIL/UNITY_PASSWORD" >&2; exit 64
fi
docker run --rm \
  -e UNITY_LICENSE -e UNITY_SERIAL -e UNITY_EMAIL -e UNITY_PASSWORD -e RH_PACKAGE -e RH_TITLE -e RH_RECIPE \
  -v "$PROJECT":/project -v "$OUT":/out "$IMAGE" bash -c '
set -e
if [ -n "${UNITY_LICENSE:-}" ]; then
  mkdir -p /root/.local/share/unity3d/Unity
  printf "%s" "$UNITY_LICENSE" > /root/.local/share/unity3d/Unity/Unity_lic.ulf
else
  unity-editor -batchmode -nographics -quit -logFile /out/activate.log \
    -serial "$UNITY_SERIAL" -username "$UNITY_EMAIL" -password "$UNITY_PASSWORD"
fi
set +e
unity-editor -batchmode -nographics -quit -logFile /out/unity.log -projectPath /project -buildTarget Android \
  -executeMethod RealityHack.MuseumKit.Editor.PortRecipe.Run \
  -rhOutput /out/app.apk -rhPackageId "$RH_PACKAGE" -rhProductName "$RH_TITLE" -rhRecipe "$RH_RECIPE"
code=$?
if [ -n "${UNITY_SERIAL:-}" ]; then
  unity-editor -batchmode -nographics -quit -returnlicense -username "$UNITY_EMAIL" -password "$UNITY_PASSWORD" || true
fi
exit $code'
