param(
    [switch]$Dependencies,
    [switch]$JobData,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (!(Test-Path -LiteralPath (Join-Path $repoRoot 'PilahDok.slnx') -PathType Leaf)) {
    throw 'Root PilahDok tidak ditemukan. Pembersihan dibatalkan.'
}

# Only generated paths are allowed, including reproducible E2E fixture output.
# Source, fixture builders, lockfiles, .env and .git are never targets.
$relativeTargets = @(
    'apps/web/.next', 'apps/web/next-env.d.ts', 'apps/web/tsconfig.tsbuildinfo',
    'apps/web/playwright-report', 'apps/web/test-results', 'apps/web/coverage',
    'artifacts', 'coverage', 'TestResults', 'tests/fixtures'
)
$projects = @('apps/api', 'apps/worker', 'libs/PilahDok.Core', 'libs/PilahDok.Infrastructure',
    'tests/PilahDok.Tests', 'tests/PilahDok.Testing', 'tools/PilahDok.FixtureGenerator')
foreach ($project in $projects) {
    foreach ($generated in @('bin', 'obj', 'TestResults')) { $relativeTargets += "$project/$generated" }
}
if ($Dependencies) { $relativeTargets += 'apps/web/node_modules' }
if ($JobData) { $relativeTargets += '.pilahdok-data' }

if (!$DryRun) {
    $running = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='node.exe' OR Name='redis-server.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Replace('/', '\').IndexOf($repoRoot.Replace('/', '\'), [StringComparison]::OrdinalIgnoreCase) -ge 0 }
    if ($running) { throw 'Hentikan aplikasi PilahDok dengan Ctrl+C sebelum membersihkan file build/data.' }
}

$targets = @()
[long]$totalBytes = 0
foreach ($relative in $relativeTargets) {
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
    if (!$path.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Target keluar dari workspace. Pembersihan dibatalkan.'
    }
    # Check parents too: an allowlisted path may sit under an unexpected junction.
    $ancestor = $path
    while ($ancestor.Length -gt $repoRoot.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            $entry = Get-Item -LiteralPath $ancestor -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Link/junction ditemukan pada target $relative. Pembersihan dibatalkan." }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (!(Test-Path -LiteralPath $path)) { continue }
    $rootEntry = Get-Item -LiteralPath $path -Force
    if ($rootEntry -is [IO.DirectoryInfo]) {
        $pending = New-Object 'System.Collections.Generic.Stack[System.IO.DirectoryInfo]'
        $pending.Push($rootEntry)
        while ($pending.Count -gt 0) {
            foreach ($entry in $pending.Pop().EnumerateFileSystemInfos()) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Link/junction ditemukan dalam target $relative. Pembersihan dibatalkan." }
                if ($entry -is [IO.DirectoryInfo]) { $pending.Push($entry) }
                else { $totalBytes += $entry.Length }
            }
        }
    } else { $totalBytes += $rootEntry.Length }
    $targets += [pscustomobject]@{ Relative = $relative; Path = $path }
}

# Every absolute target and nested link has been checked before the first recursive deletion.
foreach ($target in $targets) {
    if ($DryRun) { Write-Output "Akan dihapus: $($target.Relative)" }
    else {
        Remove-Item -LiteralPath $target.Path -Recurse -Force
        Write-Output "Dihapus: $($target.Relative)"
    }
}
$action = if ($DryRun) { 'Pratinjau' } else { 'Selesai' }
Write-Output ("{0}: {1} target, {2:N2} MiB. Source dan lockfile dipertahankan." -f $action, $targets.Count, ($totalBytes / 1MB))
