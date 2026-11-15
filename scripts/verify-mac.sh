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
node_path="${NODE_BIN:-}"
if [[ -z "$node_path" ]]; then node_path="$(command -v node || true)"; fi
[[ -n "$node_path" && -x "$node_path" ]] || { echo "검증에는 Node 24가 필요합니다. PATH 또는 NODE_BIN에 실행 파일을 지정하세요." >&2; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "패키지 검증에 Python 3가 필요합니다." >&2; exit 1; }
"$dotnet_path" --info
"$dotnet_path" clean AirWaveApMonitor.slnx -c Release
"$dotnet_path" restore AirWaveApMonitor.slnx
"$dotnet_path" build AirWaveApMonitor.slnx -c Release --no-restore
"$dotnet_path" test AirWaveApMonitor.slnx -c Release --no-build
for script in src/AirWaveApMonitor.App/wwwroot/*.js; do "$node_path" --check "$script"; done
for test_file in tests/Frontend/*.test.mjs; do "$node_path" "$test_file"; done
python3 -m unittest discover -s tests/Packaging -v
printf '%s\n' 'macOS 검증 완료. Windows 실행·사내 연동 검증은 별도입니다.'
