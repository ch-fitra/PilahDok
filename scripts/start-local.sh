#!/usr/bin/env bash
set -euo pipefail
task_repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$task_repo_root"
if ! command -v powershell.exe >/dev/null 2>&1; then
  echo "Launcher ini untuk Git Bash di Windows. Untuk server Linux, lihat docs/deployment-native.md." >&2
  exit 1
fi
exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/start-local.ps1 "$@"
