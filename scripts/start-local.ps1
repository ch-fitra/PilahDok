param(
    [int]$WebPort = 3000,
    [int]$ApiPort = 8080,
    [int]$WorkerPort = 8081,
    [int]$RedisPort = 6380,
    [string]$DataDirectory,
    [switch]$SkipBuild,
    [switch]$TestE2E
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'native-common.ps1')
if (!$DataDirectory) { $DataDirectory = Join-Path $repoRoot '.pilahdok-data' }
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$dotnet = Find-Dotnet
$node = (Get-Command node -ErrorAction Stop).Source
$redis = (Get-Command redis-server -ErrorAction Stop).Source
$children = @()
$oldEnvironment = @{}
$settings = @{
    'DOTNET_ENVIRONMENT' = 'Production'
    'PilahDok__RedisConnection' = "127.0.0.1:$RedisPort,abortConnect=false"
    'PilahDok__StorageRoot' = (Join-Path $DataDirectory 'jobs')
    'PilahDok__AllowedOrigins__0' = "http://localhost:$WebPort"
    'NEXT_PUBLIC_API_URL' = "http://localhost:$ApiPort/api/v1"
    'E2E_BASE_URL' = "http://localhost:$WebPort"
}
foreach ($key in $settings.Keys) {
    $oldEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
}
function Start-Child([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $DataDirectory "$Name.log") -RedirectStandardError (Join-Path $DataDirectory "$Name.error.log")
    return $process
}
function Wait-Ready([string]$Url) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($child in $script:children) {
            if ($child.HasExited) { throw "Proses $($child.Id) berhenti. Periksa log di $DataDirectory." }
        }
        try {
            $response = Invoke-WebRequest -UseBasicParsing $Url -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return }
        } catch { Start-Sleep -Milliseconds 400 }
    }
    throw "Service belum siap: $Url. Periksa log di $DataDirectory."
}
Push-Location $repoRoot
try {
    foreach ($port in @($WebPort, $ApiPort, $WorkerPort, $RedisPort)) {
        $listener = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object Port -eq $port
        if ($listener) { throw "Port $port sudah dipakai. Pilih port lain; launcher tidak menghentikan service lain." }
    }
    New-Item -ItemType Directory -Force $DataDirectory | Out-Null
    if (!(Test-Path apps/web/node_modules/next)) { Invoke-Checked npm.cmd @('ci') 'apps/web' }
    if (!$SkipBuild) {
        Invoke-Checked $dotnet @('restore', 'PilahDok.slnx', '--locked-mode') $repoRoot
        Invoke-Checked $dotnet @('build', 'apps/api/PilahDok.Api.csproj', '-c', 'Release', '--no-restore') $repoRoot
        Invoke-Checked $dotnet @('build', 'apps/worker/PilahDok.Worker.csproj', '-c', 'Release', '--no-restore') $repoRoot
        Invoke-Checked npm.cmd @('run', 'build') 'apps/web'
    }
    if ($TestE2E) { New-TestFixtures $dotnet $repoRoot }
    # Private Redis instance. No snapshots or append-only persistence; only this instance is stopped.
    $redisConfig = Join-Path $DataDirectory 'redis.conf'
    [IO.File]::WriteAllText($redisConfig, "bind 127.0.0.1`nport $RedisPort`nsave `"`"`nappendonly no`n")
    $children += Start-Child 'redis' $redis @('"' + $redisConfig + '"') $repoRoot
    $children += Start-Child 'api' $dotnet @('"' + (Join-Path $repoRoot 'apps/api/bin/Release/net10.0/PilahDok.Api.dll') + '"', '--urls', "http://127.0.0.1:$ApiPort") (Join-Path $repoRoot 'apps/api')
    $children += Start-Child 'worker' $dotnet @('"' + (Join-Path $repoRoot 'apps/worker/bin/Release/net10.0/PilahDok.Worker.dll') + '"', '--urls', "http://127.0.0.1:$WorkerPort") (Join-Path $repoRoot 'apps/worker')
    $children += Start-Child 'web' $node @('node_modules/next/dist/bin/next', 'start', '--hostname', '127.0.0.1', '--port', "$WebPort") (Join-Path $repoRoot 'apps/web')
    Wait-Ready "http://127.0.0.1:$ApiPort/health/ready"
    Wait-Ready "http://127.0.0.1:$WorkerPort/health/ready"
    Wait-Ready "http://localhost:$WebPort"
    Write-Output "PilahDok siap: http://localhost:$WebPort (tanpa Docker). Ctrl+C untuk berhenti."
    if ($TestE2E) {
        Invoke-Checked npm.cmd @('run', 'test:e2e') 'apps/web'
    } else {
        while ($true) {
            Start-Sleep -Seconds 2
            foreach ($child in $children) {
                if ($child.HasExited) { throw "Service berhenti. Periksa log di $DataDirectory." }
            }
        }
    }
} finally {
    foreach ($child in $children) {
        if (!$child.HasExited) { Stop-Process -Id $child.Id -ErrorAction SilentlyContinue }
    }
    foreach ($key in $oldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $oldEnvironment[$key], 'Process')
    }
    Pop-Location
}
