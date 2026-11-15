#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$repo_root"
if command -v dotnet >/dev/null 2>&1; then
  dotnet_path="$(command -v dotnet)"
elif [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  dotnet_path="${HOME}/.dotnet/dotnet"
else
  echo "정식 .NET 10 SDK가 필요합니다. docs/guides/MAC_DEVELOPMENT.md를 확인하세요." >&2
  exit 1
fi
command -v python3 >/dev/null 2>&1 || { echo "패키지 생성에 Python 3가 필요합니다." >&2; exit 1; }
artifact_root="$repo_root/artifacts/win-x64"
mkdir -p -- "$artifact_root"
staging="$(mktemp -d "$artifact_root/.publish-XXXXXX")"
trap 'rm -rf -- "$staging"' EXIT
"$dotnet_path" publish "$repo_root/src/AirWaveApMonitor.App/AirWaveApMonitor.App.csproj" \
  -c Release -r win-x64 --self-contained true \
  -p:PublishAot=false -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -o "$staging/app"
python3 "$repo_root/scripts/package_initial.py" "$staging/app" "$artifact_root"
