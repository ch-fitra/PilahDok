$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$composeArgs = @(
    "-f", "docker-compose.yml",
    "-f", "docker-compose.e2e.yml",
    "-p", "pilahdok-e2e",
    "--profile", "test"
)

function Invoke-Docker {
    & docker @args
    if ($LASTEXITCODE -ne 0) {
        throw "docker gagal dengan exit code $LASTEXITCODE"
    }
}

Push-Location $repoRoot
try {
    Invoke-Docker build -f tests/Dockerfile --target test-backend -t pilahdok-tests-backend .
    Invoke-Docker build -f tests/Dockerfile --target test-web -t pilahdok-tests-web .
    Invoke-Docker compose @composeArgs build api worker web e2e
    Invoke-Docker compose @composeArgs up -d redis api worker web
    Invoke-Docker compose @composeArgs run --rm --no-deps e2e
    Write-Output "Semua formatter, lint, build, unit, integration, security, audit, dan E2E test lulus."
}
finally {
    & docker compose @composeArgs down --volumes --remove-orphans
    Pop-Location
}
