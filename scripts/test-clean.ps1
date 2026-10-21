$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('pilahdok-clean-test-' + [Guid]::NewGuid().ToString('N'))))
$workspace = Join-Path $testRoot 'workspace'
$outside = Join-Path $testRoot 'outside'
$junction = Join-Path $workspace 'apps/web/.next'
function Assert-Clean([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
try {
    foreach ($directory in @('scripts', 'apps/web/app', 'apps/web/.next', 'apps/web/node_modules', '.pilahdok-data/jobs', 'apps/api/bin', 'tests/fixtures', 'tests/PilahDok.Testing')) {
        New-Item -ItemType Directory -Force (Join-Path $workspace $directory) | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'clean.ps1') -Destination (Join-Path $workspace 'scripts/clean.ps1')
    foreach ($relative in @('PilahDok.slnx', 'apps/web/app/page.tsx', 'apps/web/package-lock.json', '.env', 'apps/web/.next/output.txt', 'apps/web/node_modules/dependency.txt', '.pilahdok-data/jobs/source.docx', 'apps/api/bin/generated.dll', 'tests/fixtures/generated.docx', 'tests/PilahDok.Testing/FixtureDocumentBuilder.cs')) {
        [IO.File]::WriteAllText((Join-Path $workspace $relative), 'sentinel')
    }
    $cleaner = Join-Path $workspace 'scripts/clean.ps1'
    & $cleaner -DryRun -Dependencies -JobData | Out-Null
    Assert-Clean (Test-Path -LiteralPath (Join-Path $workspace 'apps/web/.next/output.txt')) 'DryRun menghapus data.'
    & $cleaner | Out-Null
    Assert-Clean (!(Test-Path -LiteralPath (Join-Path $workspace 'apps/web/.next'))) 'Hasil build tidak dibersihkan.'
    Assert-Clean (!(Test-Path -LiteralPath (Join-Path $workspace 'tests/fixtures'))) 'Fixture hasil generasi tidak dibersihkan.'
    Assert-Clean (Test-Path -LiteralPath (Join-Path $workspace 'apps/web/node_modules/dependency.txt')) 'Dependency terhapus tanpa opsi Dependencies.'
    Assert-Clean (Test-Path -LiteralPath (Join-Path $workspace '.pilahdok-data/jobs/source.docx')) 'Job terhapus tanpa opsi JobData.'
    & $cleaner -Dependencies -JobData | Out-Null
    foreach ($relative in @('apps/web/app/page.tsx', 'apps/web/package-lock.json', '.env', 'tests/PilahDok.Testing/FixtureDocumentBuilder.cs')) {
        Assert-Clean (Test-Path -LiteralPath (Join-Path $workspace $relative)) 'Source, lockfile, atau konfigurasi lokal terhapus.'
    }
    Assert-Clean (!(Test-Path -LiteralPath (Join-Path $workspace '.pilahdok-data'))) 'Opsi JobData tidak membersihkan job.'
    New-Item -ItemType Directory -Force $outside | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'do not delete')
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    $rejected = $false
    try { & $cleaner -Dependencies -JobData | Out-Null } catch { $rejected = $_.Exception.Message -match 'Link/junction' }
    Assert-Clean $rejected 'Cleaner tidak menolak junction keluar workspace.'
    Assert-Clean (Test-Path -LiteralPath (Join-Path $outside 'keep.txt')) 'Target di luar workspace ikut terhapus.'
    Write-Output 'Safety cleaner lulus: dry-run, source/config/lockfile, opt-in job/dependency, dan junction.'
} finally {
    if (Test-Path -LiteralPath $junction) {
        $entry = Get-Item -LiteralPath $junction -Force
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($junction) }
    }
    $expectedPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar + 'pilahdok-clean-test-'
    if (!$testRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path test cleanup tidak aman.' }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
