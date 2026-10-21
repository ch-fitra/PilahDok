#!/usr/bin/env sh
set -eu

repo_root="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$repo_root"

# Git Bash uses the same native gate as PowerShell. Linux/macOS keep the optional container gate.
if command -v powershell.exe >/dev/null 2>&1; then
  exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/test-all.ps1 "$@"
fi

cleanup() {
  docker compose -f docker-compose.yml -f docker-compose.e2e.yml \
    -p pilahdok-e2e --profile test down --volumes --remove-orphans
}
trap cleanup EXIT INT TERM

docker build -f tests/Dockerfile --target test-backend -t pilahdok-tests-backend .
docker build -f tests/Dockerfile --target test-web -t pilahdok-tests-web .
docker compose -f docker-compose.yml -f docker-compose.e2e.yml \
  -p pilahdok-e2e --profile test build api worker web e2e
docker compose -f docker-compose.yml -f docker-compose.e2e.yml \
  -p pilahdok-e2e --profile test up -d redis api worker web
docker compose -f docker-compose.yml -f docker-compose.e2e.yml \
  -p pilahdok-e2e --profile test run --rm --no-deps e2e

echo "Semua formatter, lint, build, unit, integration, security, audit, dan E2E test lulus."
