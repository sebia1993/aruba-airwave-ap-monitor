#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
# Publish and package to a new unique path; existing artifacts are preserved.
exec "$repo_root/scripts/publish-win.sh" "$@"
