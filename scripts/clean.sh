#!/usr/bin/env bash
set -euo pipefail
task_repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$task_repo_root"
if ! command -v powershell.exe >/dev/null 2>&1; then
  echo "Cleaner ini untuk Windows/Git Bash; tidak menghapus data pada server Linux." >&2
  exit 1
fi
exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/clean.ps1 "$@"
