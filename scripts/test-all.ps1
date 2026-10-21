param([switch]$Docker)
$ErrorActionPreference = 'Stop'
if ($Docker) { & (Join-Path $PSScriptRoot 'test-docker.ps1'); return }
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'native-common.ps1')
$dotnet = Find-Dotnet
& (Join-Path $PSScriptRoot 'test-clean.ps1')
Invoke-Checked $dotnet @('restore', 'PilahDok.slnx', '--locked-mode') $repoRoot
Invoke-Checked $dotnet @('format', 'PilahDok.slnx', '--verify-no-changes', '--no-restore') $repoRoot
Invoke-Checked $dotnet @('build', 'PilahDok.slnx', '-c', 'Release', '--no-restore') $repoRoot
Invoke-Checked $dotnet @('test', 'tests/PilahDok.Tests/PilahDok.Tests.csproj', '-c', 'Release', '--no-build') $repoRoot
Invoke-Checked $dotnet @('list', 'PilahDok.slnx', 'package', '--vulnerable', '--include-transitive') $repoRoot
Invoke-Checked npm.cmd @('ci') (Join-Path $repoRoot 'apps/web')
Invoke-Checked npm.cmd @('run', 'format:check') (Join-Path $repoRoot 'apps/web')
Invoke-Checked npm.cmd @('run', 'lint') (Join-Path $repoRoot 'apps/web')
Invoke-Checked npm.cmd @('test') (Join-Path $repoRoot 'apps/web')
Push-Location (Join-Path $repoRoot 'apps/web')
try {
    New-Item -ItemType Directory -Force (Join-Path $repoRoot 'artifacts') | Out-Null
    & npm.cmd audit --json | Out-File -Encoding utf8 (Join-Path $repoRoot 'artifacts/npm-audit.json')
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Audit lengkap menemukan dependency rentan. Lihat artifacts/npm-audit.json dan docs/testing.md; gate runtime diperiksa terpisah.' }
} finally { Pop-Location }
Invoke-Checked npm.cmd @('audit', '--omit=dev', '--audit-level=high') (Join-Path $repoRoot 'apps/web')
Invoke-Checked npx.cmd @('playwright', 'install', 'chromium') (Join-Path $repoRoot 'apps/web')
$oldApiUrl = $env:NEXT_PUBLIC_API_URL
try {
    $env:NEXT_PUBLIC_API_URL = 'http://localhost:18080/api/v1'
    Invoke-Checked npm.cmd @('run', 'build') (Join-Path $repoRoot 'apps/web')
    & (Join-Path $PSScriptRoot 'start-local.ps1') -SkipBuild -TestE2E -WebPort 13000 -ApiPort 18080 -WorkerPort 18081 -RedisPort 16380 -DataDirectory (Join-Path $repoRoot 'artifacts/native-e2e')
} finally { $env:NEXT_PUBLIC_API_URL = $oldApiUrl }
Write-Output 'Seluruh gate native selesai. start-local tanpa SkipBuild membangun ulang web untuk port normal.'
