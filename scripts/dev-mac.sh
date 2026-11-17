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
exec "$dotnet_path" run --project "$repo_root/src/AirWaveApMonitor.App/AirWaveApMonitor.App.csproj" -- --mode demo "$@"
