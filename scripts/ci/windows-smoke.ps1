param(
    [Parameter(Mandatory = $true)]
    [string]$AppDirectory
)

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('AirWave 한글 경로 ' + [guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $testRoot '앱 폴더'
$demoData = Join-Path $testRoot '데모 데이터'
$process = $null
$duplicate = $null

function Wait-CollectorReady {
    param([System.Diagnostics.Process]$Monitor, [long]$AfterRunId = 0)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 45) {
        if ($Monitor.HasExited) { throw ('App exited with code ' + $Monitor.ExitCode) }
        try {
            $candidate = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/health' -TimeoutSec 2
            if ($candidate.collector.phase -eq 'Idle' -and
                $null -ne $candidate.collector.lastValidCommittedAtUtc -and
                [long]$candidate.collector.lastFinalRunId -gt $AfterRunId) {
                return $candidate
            }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    throw 'Automatic AP collection did not reach a committed idle state within 45 seconds.'
}

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $AppDirectory '*') -Destination $packageRoot -Recurse
    $exe = Join-Path $packageRoot 'AirWaveApMonitor.App.exe'
    if (-not (Test-Path $exe)) { throw 'Published Windows executable missing.' }
    $arguments = '--mode demo --data-dir "' + $demoData + '" --port 18765'
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $packageRoot -PassThru -RedirectStandardOutput (Join-Path $testRoot 'stdout.log') -RedirectStandardError (Join-Path $testRoot 'stderr.log')
    $health = Wait-CollectorReady -Monitor $process
    if ($health.mode -ne 'demo') { throw 'Demo mode not reported.' }
    if ($health.collection -ne 'automatic') { throw 'Automatic collector was not reported.' }
    if ($health.persistence -ne 'sqlite-ready') { throw 'Native SQLite storage was not initialized before serving HTTP.' }
    $databasePath = Join-Path $demoData 'monitor.db'
    if (-not (Test-Path -LiteralPath $databasePath)) { throw 'Initialized SQLite database is missing.' }
    $databaseHeader = [System.IO.File]::ReadAllBytes($databasePath)
    if ($databaseHeader.Length -lt 16 -or [System.Text.Encoding]::ASCII.GetString($databaseHeader, 0, 16) -ne "SQLite format 3`0") {
        throw 'Initialized database does not have the SQLite file header.'
    }
    $page = Invoke-WebRequest -Uri 'http://127.0.0.1:18765/' -UseBasicParsing -TimeoutSec 5
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch 'AirWave') { throw 'Local page did not load.' }
    $settings = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/settings' -TimeoutSec 5
    if ($settings.mode -ne 'demo' -or [string]::IsNullOrWhiteSpace($settings.executionAccount)) { throw 'Settings or process account missing.' }
    $csrf = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/antiforgery' -SessionVariable localSession -TimeoutSec 5
    if ([string]::IsNullOrWhiteSpace($csrf.token)) { throw 'CSRF token missing.' }
    $headers = @{ 'X-CSRF-TOKEN' = $csrf.token }
    $rejected = $false
    try {
        Invoke-WebRequest -Uri 'http://127.0.0.1:18765/api/diagnostics' -Method Post -Headers $headers -WebSession $localSession -UseBasicParsing -TimeoutSec 5 | Out-Null
    } catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 403) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Mutation without Origin was accepted.' }
    $headers['Origin'] = 'http://127.0.0.1:18765'
    $diagnostic = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/diagnostics' -Method Post -Headers $headers -WebSession $localSession -TimeoutSec 15
    if ([string]::IsNullOrWhiteSpace($diagnostic.diagnosticId) -or $diagnostic.parsedRecords -ne 2048 -or $diagnostic.excludedRecords -ne 0) {
        throw 'Published Demo diagnostic did not run the expected parser fixture.'
    }
    if ($diagnostic.connectionPath -ne 'synthetic-memory' -or $null -ne $diagnostic.httpStatus -or $null -ne $diagnostic.runId) {
        throw 'Diagnostic reported a live HTTP result or operational run identity.'
    }
    $firstRunId = [long]$health.collector.lastFinalRunId
    $profilePath = Join-Path $demoData 'profile.json'
    $firstInstanceId = (Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json).InstanceId
    if ([string]::IsNullOrWhiteSpace($firstInstanceId)) { throw 'Initial Demo profile identity was not persisted.' }
    $duplicateArguments = '--mode demo --data-dir "' + $demoData + '" --port 18767'
    $duplicate = Start-Process -FilePath $exe -ArgumentList $duplicateArguments -WorkingDirectory $packageRoot -PassThru -RedirectStandardOutput (Join-Path $testRoot 'duplicate-stdout.log') -RedirectStandardError (Join-Path $testRoot 'duplicate-stderr.log')
    if (-not $duplicate.WaitForExit(10000)) { throw 'Second process sharing the data directory was not rejected.' }
    if ($duplicate.ExitCode -eq 0) { throw 'Duplicate collector unexpectedly exited successfully.' }
    $afterDuplicate = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/health' -TimeoutSec 5
    if ([long]$afterDuplicate.collector.lastFinalRunId -ne $firstRunId) {
        throw 'Second startup changed the first collector committed run.'
    }
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit()
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $packageRoot -PassThru -RedirectStandardOutput (Join-Path $testRoot 'restart-stdout.log') -RedirectStandardError (Join-Path $testRoot 'restart-stderr.log')
    $health = Wait-CollectorReady -Monitor $process -AfterRunId $firstRunId
    $restartedInstanceId = (Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json).InstanceId
    if ($restartedInstanceId -ne $firstInstanceId) { throw 'Restart replaced the Demo physical instance identity.' }
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id)
    if ($listeners.Count -eq 0) { throw 'No listening socket found.' }
    if (@($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }).Count -gt 0) {
        throw 'Application bound a non-loopback address.'
    }
    Write-Host 'PASS: self-contained Windows executable, automatic committed collection without browser, preserved run/profile after restart, duplicate process rejection, native SQLite, Korean/space path, loopback-only listener and guarded Demo diagnostic.'
} catch {
    Get-Content (Join-Path $testRoot 'stdout.log') -ErrorAction SilentlyContinue
    Get-Content (Join-Path $testRoot 'stderr.log') -ErrorAction SilentlyContinue
    Get-Content (Join-Path $testRoot 'restart-stderr.log') -ErrorAction SilentlyContinue
    Get-Content (Join-Path $testRoot 'duplicate-stderr.log') -ErrorAction SilentlyContinue
    throw
} finally {
    if ($null -ne $duplicate -and -not $duplicate.HasExited) {
        Stop-Process -Id $duplicate.Id -Force
        $duplicate.WaitForExit()
    }
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    if (Test-Path $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
