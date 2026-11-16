param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$launcher = [IO.Path]::GetFullPath((Join-Path $packageRoot 'AirWaveAPMonitor.exe'))
$backend = [IO.Path]::GetFullPath((Join-Path $packageRoot 'app/AirWaveApMonitor.App.exe'))
foreach ($required in @($launcher, $backend)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Launcher smoke requires packaged executable: ' + $required) }
}
. (Join-Path $packageRoot 'tools/Monitor-Common.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AirWave 실행기 검증 ' + [guid]::NewGuid().ToString('N'))
$dataDirectory = Join-Path $testRoot '데모 데이터 공백'
$otherDataDirectory = Join-Path $testRoot '별도 데이터 공백'
$workingDirectory = Join-Path $testRoot '앱 없는 다른 작업 폴더'
$diagnosticsDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/windows-launcher-diagnostics'))
$launches = New-Object 'System.Collections.Generic.List[object]'
$ownedBackends = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$ownedBackendIds = New-Object 'System.Collections.Generic.HashSet[int]'
$cases = New-Object 'System.Collections.Generic.List[object]'
$foreignJob = $null
$passed = $false
$cleanupOk = $true
$evidence = [ordered]@{ kind='windows-desktop-launcher'; browserGuiVerified=$false; doubleClickVerified=$false; cases=$cases; failure=$null }

function Get-LauncherFreePort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}
function Start-OwnedLauncher {
    param([string]$Case, [string[]]$Arguments, [string]$Executable = $launcher)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = ($Arguments | ForEach-Object { ConvertTo-MonitorNativeArgument $_ }) -join ' '
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { $process.Dispose(); throw ('Could not start owned launcher: ' + $Case) }
    $record = [pscustomobject]@{ Case=$Case; Process=$process; Stdout=$process.StandardOutput.ReadToEndAsync(); Stderr=$process.StandardError.ReadToEndAsync() }
    [void]$launches.Add($record)
    return $record
}
function Remember-OwnedBackends {
    # A native bootstrap waits for its child. Capture that exact child even if health never becomes reachable.
    foreach ($launch in $launches) {
        foreach ($candidate in @(Get-CimInstance -ClassName Win32_Process -Filter ('ParentProcessId = ' + $launch.Process.Id))) {
            if (-not ([string]$candidate.ExecutablePath).Equals($backend, [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($ownedBackendIds.Add([int]$candidate.ProcessId)) {
                $process = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
                if ($null -ne $process) { [void]$ownedBackends.Add($process) }
            }
        }
    }
}
function Get-LauncherHealth {
    param([int]$Port)
    return Invoke-RestMethod -Uri ('http://127.0.0.1:' + $Port + '/api/health') -TimeoutSec 2
}
function Assert-OriginalCollector {
    param([int]$Port, [long]$ExpectedProcessId, [string]$DatasetId)
    $health = Get-LauncherHealth -Port $Port
    if ($health.product -ne 'AirWaveAPMonitor' -or $health.mode -ne 'demo' -or
        [string]$health.datasetId -ne $DatasetId -or [long]$health.processId -ne $ExpectedProcessId) {
        throw 'A repeated or rejected launcher changed the original collector identity.'
    }
    $matching = @(Get-CimInstance -ClassName Win32_Process | Where-Object { ([string]$_.ExecutablePath).Equals($backend, [StringComparison]::OrdinalIgnoreCase) })
    if ($matching.Count -ne 1 -or [long]$matching[0].ProcessId -ne $ExpectedProcessId) { throw 'Launcher left an additional packaged backend process running.' }
    return $health
}
function Wait-LauncherReady {
    param($Launch, [int]$Port, [string]$ExpectedMode = 'demo')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $last = 'no application health response'
    while ($watch.Elapsed.TotalSeconds -lt 45) {
        Remember-OwnedBackends
        if ($Launch.Process.HasExited) { throw ('Initial native launcher exited before readiness: ' + $Launch.Process.ExitCode) }
        try {
            $candidate = Get-LauncherHealth -Port $Port
            $last = $candidate | ConvertTo-Json -Depth 10 -Compress
            if ($candidate.product -eq 'AirWaveAPMonitor' -and $candidate.mode -eq $ExpectedMode -and
                -not [string]::IsNullOrWhiteSpace([string]$candidate.datasetId) -and [long]$candidate.processId -gt 0) {
                $native = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + [long]$candidate.processId)
                if ($null -eq $native -or [long]$native.ParentProcessId -ne $Launch.Process.Id -or
                    -not ([string]$native.ExecutablePath).Equals($backend, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Health identity is not the child of the owned native launcher.'
                }
                Remember-OwnedBackends
                return [pscustomobject]@{ Health=$candidate; ElapsedMs=[Math]::Round($watch.Elapsed.TotalMilliseconds,3) }
            }
        } catch { $last = $_.Exception.Message }
        Start-Sleep -Milliseconds 250
    }
    throw ('Native launcher did not expose its own settings-ready health in 45 seconds: ' + $last)
}
function Wait-LauncherExit {
    param($Launch, [bool]$ExpectSuccess)
    if (-not $Launch.Process.WaitForExit(30000)) { Remember-OwnedBackends; throw ('Native launcher did not finish its bounded reuse/failure path: ' + $Launch.Case) }
    if (($Launch.Process.ExitCode -eq 0) -ne $ExpectSuccess) { throw ('Unexpected native exit code for ' + $Launch.Case + ': ' + $Launch.Process.ExitCode) }
    [void]$cases.Add([pscustomobject]@{ name=$Launch.Case; exitCode=$Launch.Process.ExitCode; expectedSuccess=$ExpectSuccess })
}

try {
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    $port = Get-LauncherFreePort
    $arguments = @('--mode','demo','--data-dir',$dataDirectory,'--port',[string]$port,'--no-browser','true')
    $initial = Start-OwnedLauncher -Case 'initial' -Arguments $arguments
    $ready = Wait-LauncherReady -Launch $initial -Port $port
    $backendProcessId = [long]$ready.Health.processId
    $datasetId = [string]$ready.Health.datasetId
    $profile = Get-Content -LiteralPath (Join-Path $dataDirectory 'profile.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$profile.DatasetId -ne $datasetId) { throw 'Launcher health dataset does not match the requested physical data directory.' }
    $null = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $port + '/') -UseBasicParsing -TimeoutSec 5
    [void]$cases.Add([pscustomobject]@{ name='initial'; product=$ready.Health.product; mode=$ready.Health.mode; datasetId=$datasetId; launcherProcessId=$initial.Process.Id; backendProcessId=$backendProcessId; readyElapsedMs=$ready.ElapsedMs; collectorPhase=$ready.Health.collector.phase; differentWorkingDirectory=$true })

    $again = Start-OwnedLauncher -Case 'same-dataset-reuse' -Arguments $arguments
    Wait-LauncherExit -Launch $again -ExpectSuccess $true
    $null = Assert-OriginalCollector -Port $port -ExpectedProcessId $backendProcessId -DatasetId $datasetId

    $different = Start-OwnedLauncher -Case 'different-dataset-rejected' -Arguments @('--mode','demo','--data-dir',$otherDataDirectory,'--port',[string]$port,'--no-browser','true')
    Wait-LauncherExit -Launch $different -ExpectSuccess $false
    $null = Assert-OriginalCollector -Port $port -ExpectedProcessId $backendProcessId -DatasetId $datasetId

    $modeConflict = Start-OwnedLauncher -Case 'different-mode-rejected' -Arguments @('--mode','live','--data-dir',$dataDirectory,'--port',[string]$port,'--no-browser','true')
    Wait-LauncherExit -Launch $modeConflict -ExpectSuccess $false
    $null = Assert-OriginalCollector -Port $port -ExpectedProcessId $backendProcessId -DatasetId $datasetId

    $otherPort = Start-OwnedLauncher -Case 'same-dataset-other-port-rejected' -Arguments @('--mode','demo','--data-dir',$dataDirectory,'--port',[string](Get-LauncherFreePort),'--no-browser','true')
    Wait-LauncherExit -Launch $otherPort -ExpectSuccess $false
    $null = Assert-OriginalCollector -Port $port -ExpectedProcessId $backendProcessId -DatasetId $datasetId

    $foreignPort = Get-LauncherFreePort
    $foreignJob = Start-Job -ArgumentList $foreignPort -ScriptBlock {
        param([int]$ListenPort)
        $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $ListenPort)
        try {
            $listener.Start()
            while ($true) {
                if (-not $listener.Pending()) { Start-Sleep -Milliseconds 50; continue }
                $client = $listener.AcceptTcpClient()
                try {
                    $stream = $client.GetStream(); $stream.ReadTimeout = 2000
                    $buffer = New-Object byte[] 8192
                    $null = $stream.Read($buffer, 0, $buffer.Length)
                    $body = '{"product":"ForeignLauncherSmoke","mode":"demo"}'
                    $response = "HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: " + [Text.Encoding]::ASCII.GetByteCount($body) + "`r`nConnection: close`r`n`r`n" + $body
                    $bytes = [Text.Encoding]::ASCII.GetBytes($response)
                    $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
                } finally { $client.Dispose() }
            }
        } finally { $listener.Stop() }
    }
    $foreignReady = $false
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 15) {
        try { if ((Get-LauncherHealth -Port $foreignPort).product -eq 'ForeignLauncherSmoke') { $foreignReady = $true; break } } catch { }
        if ($foreignJob.State -eq 'Failed') { throw 'Owned synthetic foreign HTTP server failed to start.' }
        Start-Sleep -Milliseconds 100
    }
    if (-not $foreignReady) { throw 'Owned synthetic foreign HTTP server did not start.' }
    $foreign = Start-OwnedLauncher -Case 'foreign-server-rejected' -Arguments @('--mode','demo','--data-dir',(Join-Path $testRoot '외부 포트 시험 데이터'),'--port',[string]$foreignPort,'--no-browser','true')
    Wait-LauncherExit -Launch $foreign -ExpectSuccess $false
    if ($foreignJob.State -ne 'Running' -or (Get-LauncherHealth -Port $foreignPort).product -ne 'ForeignLauncherSmoke') { throw 'Launcher stopped or changed the unrelated synthetic listener.' }
    $null = Assert-OriginalCollector -Port $port -ExpectedProcessId $backendProcessId -DatasetId $datasetId
    $evidence.foreignListenerPreserved = $true

    $missingDirectory = Join-Path $testRoot '실행기만 공백'
    New-Item -ItemType Directory -Path $missingDirectory | Out-Null
    $missingLauncher = Join-Path $missingDirectory 'AirWaveAPMonitor.exe'
    Copy-Item -LiteralPath $launcher -Destination $missingLauncher
    $missing = Start-OwnedLauncher -Case 'missing-backend-rejected' -Executable $missingLauncher -Arguments @('--mode','demo','--data-dir',(Join-Path $testRoot '누락 시험 데이터'),'--port',[string](Get-LauncherFreePort),'--no-browser','true')
    Wait-LauncherExit -Launch $missing -ExpectSuccess $false
    $null = Assert-OriginalCollector -Port $port -ExpectedProcessId $backendProcessId -DatasetId $datasetId
    $evidence.originalCollectorPreserved = $true

    # An unconfigured Live default must expose settings without waiting for an AP collection.
    # Use a new explicit data directory; never touch the machine's default ProgramData dataset.
    $liveData = Join-Path $testRoot '새 Live 설정 데이터'
    $livePort = Get-LauncherFreePort
    $live = Start-OwnedLauncher -Case 'default-live-unconfigured' -Arguments @('--data-dir',$liveData,'--port',[string]$livePort,'--no-browser','true')
    $liveReady = Wait-LauncherReady -Launch $live -Port $livePort -ExpectedMode 'live'
    if ($liveReady.Health.liveConfigured -ne $false -or $liveReady.Health.serverConfigured -ne $false -or
        $null -ne $liveReady.Health.collector.lastValidCommittedAtUtc) { throw 'Fresh default Live dataset was configured or claimed a valid AP observation.' }
    if (Test-Path -LiteralPath (Join-Path $liveData 'profile.json')) { throw 'Unconfigured desktop Live launch unexpectedly created a server profile.' }
    $liveMarker = Get-Content -LiteralPath (Join-Path $liveData 'runtime-mode.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($liveMarker.Mode -ne 'Live' -or [string]$liveMarker.DatasetId -ne [string]$liveReady.Health.datasetId) { throw 'Default Live health did not use the explicit synthetic data directory.' }
    $null = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $livePort + '/') -UseBasicParsing -TimeoutSec 5
    [void]$cases.Add([pscustomobject]@{ name='default-live-unconfigured'; product=$liveReady.Health.product; mode=$liveReady.Health.mode; liveConfigured=$liveReady.Health.liveConfigured; serverConfigured=$liveReady.Health.serverConfigured; lastValidCommittedAtUtc=$liveReady.Health.collector.lastValidCommittedAtUtc; datasetId=$liveReady.Health.datasetId; backendProcessId=$liveReady.Health.processId; readyElapsedMs=$liveReady.ElapsedMs; collectorPhase=$liveReady.Health.collector.phase })
    $passed = $true
    Write-Host 'PASS: native desktop EXE from Unicode/space package and different cwd, identity-bound reuse, dataset/mode/foreign-port rejection, missing backend failure, unrelated listener preservation, and unconfigured default Live settings readiness.'
} catch {
    $evidence.failure = $_.Exception.Message
    throw
} finally {
    try { Remember-OwnedBackends } catch { $cleanupOk = $false; Write-Warning ('Could not enumerate owned launcher children: ' + $_.Exception.Message) }
    foreach ($process in $ownedBackends) {
        try {
            if (-not $process.HasExited) { $process.Kill(); if (-not $process.WaitForExit(30000)) { throw 'Owned backend did not exit.' } }
        } catch { $cleanupOk = $false; Write-Warning ('Owned backend cleanup failed: ' + $_.Exception.Message) }
        finally { $process.Dispose() }
    }
    if ($null -ne $foreignJob) {
        try { Stop-Job -Job $foreignJob; Remove-Job -Job $foreignJob -Force }
        catch { $cleanupOk = $false; Write-Warning ('Owned synthetic listener cleanup failed: ' + $_.Exception.Message) }
    }
    New-Item -ItemType Directory -Path $diagnosticsDirectory -Force | Out-Null
    foreach ($launch in $launches) {
        try {
            if (-not $launch.Process.HasExited -and -not $launch.Process.WaitForExit(10000)) { $launch.Process.Kill(); $launch.Process.WaitForExit() }
            [IO.File]::WriteAllText((Join-Path $diagnosticsDirectory ($launch.Case + '.stdout.log')), $launch.Stdout.GetAwaiter().GetResult(), (New-Object Text.UTF8Encoding($false)))
            [IO.File]::WriteAllText((Join-Path $diagnosticsDirectory ($launch.Case + '.stderr.log')), $launch.Stderr.GetAwaiter().GetResult(), (New-Object Text.UTF8Encoding($false)))
        } catch { $cleanupOk = $false; Write-Warning ('Owned launcher cleanup or diagnostics failed: ' + $_.Exception.Message) }
        finally { $launch.Process.Dispose() }
    }
    $evidence.passed = $passed
    $evidence.cleanupSucceeded = $cleanupOk
    $json = $evidence | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText((Join-Path $diagnosticsDirectory 'launcher-result.json'), $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host ('AIRWAVE_WINDOWS_LAUNCHER_RESULT=' + ($evidence | ConvertTo-Json -Depth 10 -Compress))
    if ($cleanupOk -and (Test-Path -LiteralPath $testRoot)) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    if (-not $cleanupOk -and $passed) { throw 'Launcher assertions passed but owned-process cleanup was not verified.' }
}
