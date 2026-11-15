param(
    [Parameter(Mandatory = $true)]
    [string]$AppDirectory
)

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('AirWave 한글 경로 ' + [guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $testRoot '앱 폴더'
$demoData = Join-Path $testRoot '데모 데이터'
$process = $null

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $AppDirectory '*') -Destination $packageRoot -Recurse
    $exe = Join-Path $packageRoot 'AirWaveApMonitor.App.exe'
    if (-not (Test-Path $exe)) { throw 'Published Windows executable missing.' }
    $arguments = '--mode demo --data-dir "' + $demoData + '" --port 18765'
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $packageRoot -PassThru -RedirectStandardOutput (Join-Path $testRoot 'stdout.log') -RedirectStandardError (Join-Path $testRoot 'stderr.log')
    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw ('App exited with code ' + $process.ExitCode) }
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/health' -TimeoutSec 2
            break
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if ($null -eq $health) { throw 'Loopback health did not respond.' }
    if ($health.mode -ne 'demo') { throw 'Demo mode not reported.' }
    $page = Invoke-WebRequest -Uri 'http://127.0.0.1:18765/' -UseBasicParsing -TimeoutSec 5
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch 'AirWave') { throw 'Local page did not load.' }
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id)
    if ($listeners.Count -eq 0) { throw 'No listening socket found.' }
    if (@($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }).Count -gt 0) {
        throw 'Application bound a non-loopback address.'
    }
    Write-Host 'PASS: self-contained Windows executable, Korean/space path, explicit Demo, loopback HTTP and no external listener.'
} catch {
    Get-Content (Join-Path $testRoot 'stdout.log') -ErrorAction SilentlyContinue
    Get-Content (Join-Path $testRoot 'stderr.log') -ErrorAction SilentlyContinue
    throw
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    if (Test-Path $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
