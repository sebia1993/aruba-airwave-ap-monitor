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
$script:collectorWaitDiagnostics = New-Object 'System.Collections.Generic.List[object]'
$diagnosticsDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/windows-smoke-diagnostics'))
$diagnosticLogNames = @('stdout.log', 'stderr.log', 'restart-stdout.log', 'restart-stderr.log', 'duplicate-stdout.log', 'duplicate-stderr.log')

function Add-CollectorWaitDiagnostic {
    param($Monitor, [string]$Invocation, [long]$AfterRunId, $Watch, [string]$Result,
        $LastHealth, $LastHealthElapsedMs, $LastHttpException, $LastRequestSucceeded, [int]$Attempts)
    $exited = $Monitor.HasExited
    $record = [pscustomobject]@{
        invocation = $Invocation
        result = $Result
        elapsedMs = [math]::Round($Watch.Elapsed.TotalMilliseconds, 3)
        processId = $Monitor.Id
        processExited = $exited
        exitCode = $(if ($exited) { $Monitor.ExitCode } else { $null })
        afterRunId = $AfterRunId
        attempts = $Attempts
        lastRequestSucceeded = $LastRequestSucceeded
        lastHealthElapsedMs = $LastHealthElapsedMs
        lastHealth = $LastHealth
        lastHttpException = $LastHttpException
    }
    [void]$script:collectorWaitDiagnostics.Add($record)
    Write-Host ('Collector wait {0}: {1}, elapsedMs={2}, processId={3}, exited={4}, afterRunId={5}, phase={6}, lastFinalRunId={7}' -f
        $Invocation, $Result, $record.elapsedMs, $record.processId, $exited, $AfterRunId,
        $LastHealth.collector.phase, $LastHealth.collector.lastFinalRunId)
}

function Save-SmokeDiagnostics {
    # Only explicitly named synthetic process logs and health observations leave the
    # temporary directory. Never copy the data directory, profile, DB or package.
    try {
        New-Item -ItemType Directory -Path $diagnosticsDirectory -Force | Out-Null
        $json = ConvertTo-Json -InputObject @($script:collectorWaitDiagnostics.ToArray()) -Depth 20
        [System.IO.File]::WriteAllText((Join-Path $diagnosticsDirectory 'collector-waits.json'), $json,
            (New-Object System.Text.UTF8Encoding($false)))
        foreach ($name in $diagnosticLogNames) {
            try {
                $source = Join-Path $testRoot $name
                if (Test-Path -LiteralPath $source) {
                    Copy-Item -LiteralPath $source -Destination (Join-Path $diagnosticsDirectory $name) -Force
                }
            } catch {
                Write-Warning ('Could not preserve ' + $name + ': ' + $_.Exception.GetType().FullName)
            }
        }
    } catch {
        Write-Warning ('Could not preserve native smoke diagnostics: ' + $_.Exception.GetType().FullName)
    }
}

function Wait-CollectorReady {
    param([System.Diagnostics.Process]$Monitor, [long]$AfterRunId = 0, [string]$Invocation)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastHealth = $null
    $lastHealthElapsedMs = $null
    $lastHttpException = $null
    $lastRequestSucceeded = $null
    $firstPoll = $true
    $nextMilestone = 10
    $attempts = 0
    while ($watch.Elapsed.TotalSeconds -lt 45) {
        if ($Monitor.HasExited) {
            Add-CollectorWaitDiagnostic $Monitor $Invocation $AfterRunId $watch 'ProcessExited' $lastHealth $lastHealthElapsedMs $lastHttpException $lastRequestSucceeded $attempts
            throw ('App exited with code ' + $Monitor.ExitCode)
        }
        try {
            $attempts++
            $candidate = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/health' -TimeoutSec 2
            $lastHealth = $candidate
            $lastHealthElapsedMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 3)
            $lastRequestSucceeded = $true
            if ($candidate.collector.phase -eq 'Idle' -and
                $null -ne $candidate.collector.lastValidCommittedAtUtc -and
                [long]$candidate.collector.lastFinalRunId -gt $AfterRunId) {
                Add-CollectorWaitDiagnostic $Monitor $Invocation $AfterRunId $watch 'Ready' $lastHealth $lastHealthElapsedMs $lastHttpException $lastRequestSucceeded $attempts
                return $candidate
            }
        } catch {
            $message = $_.Exception.Message
            if ($message.Length -gt 2048) { $message = $message.Substring(0, 2048) }
            $lastRequestSucceeded = $false
            $lastHttpException = [pscustomobject]@{
                elapsedMs = [math]::Round($watch.Elapsed.TotalMilliseconds, 3)
                type = $_.Exception.GetType().FullName
                message = $message
                statusCode = $(if ($null -ne $_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $null })
            }
        }
        if ($firstPoll -or $watch.Elapsed.TotalSeconds -ge $nextMilestone) {
            Add-CollectorWaitDiagnostic $Monitor $Invocation $AfterRunId $watch 'Waiting' $lastHealth $lastHealthElapsedMs $lastHttpException $lastRequestSucceeded $attempts
            $firstPoll = $false
            if ($watch.Elapsed.TotalSeconds -ge $nextMilestone) { $nextMilestone += 10 }
        }
        Start-Sleep -Milliseconds 250
    }
    Add-CollectorWaitDiagnostic $Monitor $Invocation $AfterRunId $watch 'TimedOut' $lastHealth $lastHealthElapsedMs $lastHttpException $lastRequestSucceeded $attempts
    throw 'Automatic AP collection did not reach a committed idle state within 45 seconds.'
}

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $AppDirectory '*') -Destination $packageRoot -Recurse
    $exe = Join-Path $packageRoot 'AirWaveApMonitor.App.exe'
    if (-not (Test-Path $exe)) { throw 'Published Windows executable missing.' }
    $arguments = '--mode demo --data-dir "' + $demoData + '" --port 18765'
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $packageRoot -PassThru -RedirectStandardOutput (Join-Path $testRoot 'stdout.log') -RedirectStandardError (Join-Path $testRoot 'stderr.log')
    $health = Wait-CollectorReady -Monitor $process -Invocation 'initial'
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
    $dashboard = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/dashboard' -TimeoutSec 10
    if ([long]$dashboard.runId -ne [long]$health.collector.lastFinalRunId -or
        $dashboard.summary.confirmedDown -ne 8 -or $dashboard.summary.confirmedUp -ne 2040 -or
        $dashboard.summary.newDown -ne 2 -or $dashboard.summary.initialDown -ne 1 -or
        $dashboard.summary.existingDown -ne 5 -or $dashboard.summary.recovered -ne 2 -or
        $dashboard.summary.openIncidents -ne 8 -or $dashboard.totalRows -ne 8) {
        throw 'First visible Demo dashboard did not match committed parser/state/store results.'
    }
    $selectedAp = $dashboard.rows[0].apId
    $detail = Invoke-RestMethod -Uri ('http://127.0.0.1:18765/api/aps/' + $selectedAp + '?runId=' + $dashboard.runId) -TimeoutSec 10
    if ([long]$detail.selection.runId -ne [long]$dashboard.runId) { throw 'AP detail changed the selected run.' }
    $export = Invoke-WebRequest -Uri ('http://127.0.0.1:18765/api/export.csv?runId=' + $dashboard.runId + '&filter=Down&pageSize=1') -UseBasicParsing -TimeoutSec 10
    if ([long]$export.Headers['X-AirWave-Run-Id'] -ne [long]$dashboard.runId -or $export.Headers['X-AirWave-Row-Count'] -ne '8') {
        throw 'CSV did not preserve the selected run or export the whole filtered set.'
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
    $health = Wait-CollectorReady -Monitor $process -AfterRunId $firstRunId -Invocation 'restart'
    $restartedDashboard = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/dashboard' -TimeoutSec 10
    if ($restartedDashboard.summary.confirmedDown -ne 10 -or [long]$restartedDashboard.runId -le $firstRunId) {
        throw 'Demo restart replayed prehistory instead of advancing to the next visible scenario.'
    }
    $restartedInstanceId = (Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json).InstanceId
    if ($restartedInstanceId -ne $firstInstanceId) { throw 'Restart replaced the Demo physical instance identity.' }
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id)
    if ($listeners.Count -eq 0) { throw 'No listening socket found.' }
    if (@($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }).Count -gt 0) {
        throw 'Application bound a non-loopback address.'
    }
    Write-Host 'PASS: self-contained Windows executable, automatic committed collection without browser, dashboard/detail/whole-filter CSV, preserved run/profile/scenario after restart, duplicate process rejection, native SQLite, Korean/space path, loopback-only listener and guarded Demo diagnostic.'
} catch {
    Write-Host '--- Collector wait diagnostics (synthetic Demo) ---'
    Write-Host (ConvertTo-Json -InputObject @($script:collectorWaitDiagnostics.ToArray()) -Depth 20)
    foreach ($name in $diagnosticLogNames) {
        Write-Host ('--- ' + $name + ' (UTF-8) ---')
        Get-Content -LiteralPath (Join-Path $testRoot $name) -Encoding UTF8 -ErrorAction SilentlyContinue
    }
    throw
} finally {
    Save-SmokeDiagnostics
    if ($null -ne $duplicate -and -not $duplicate.HasExited) {
        Stop-Process -Id $duplicate.Id -Force
        $duplicate.WaitForExit()
    }
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    # Capture the final redirected output after process termination has flushed it.
    Save-SmokeDiagnostics
    if (Test-Path $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
