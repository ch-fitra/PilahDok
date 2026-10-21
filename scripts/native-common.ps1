function Find-Dotnet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $localSdk = Join-Path $env:LOCALAPPDATA 'PilahDok/dotnet/dotnet.exe'
    if (Test-Path $localSdk) { return $localSdk }
    throw 'Install .NET SDK 10 terlebih dahulu dan buka ulang terminal.'
}
function Invoke-Checked([string]$Executable, [string[]]$Arguments, [string]$Directory) {
    Push-Location $Directory
    try {
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Executable gagal dengan exit code $LASTEXITCODE" }
    } finally { Pop-Location }
}
function New-TestFixtures([string]$Dotnet, [string]$RepositoryRoot) {
    $project = 'tools/PilahDok.FixtureGenerator/PilahDok.FixtureGenerator.csproj'
    Invoke-Checked $Dotnet @('run', '--project', $project, '-c', 'Release', '--no-restore', '--', 'tests/fixtures/sample-structural.docx') $RepositoryRoot
    Invoke-Checked $Dotnet @('run', '--project', $project, '-c', 'Release', '--no-restore', '--', 'tests/fixtures/sample-no-headings.docx', '--no-headings') $RepositoryRoot
}
