param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path -LiteralPath $PackageDirectory).Path
. (Join-Path $packageSource 'Monitor-Common.ps1')
Assert-MonitorAdministrator
$caseId = [guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AirWave 운영 검증 ' + $caseId)
$packageRoot = Join-Path $testRoot '패키지 공백'
$serviceData = Join-Path $testRoot '서비스 데이터'
$taskData = Join-Path $testRoot '예약 작업 데이터'
$serviceName = 'AirWave-CI-Service-' + $caseId
$taskName = 'AirWave-CI-Task-' + $caseId
$taskPath = '\AirWaveAPMonitor\'
$serviceLaunch = $null
$taskLaunch = $null
$passed = $false
$cleanupOk = $true
$evidence = [ordered]@{ kind='windows-operations'; serviceName=$serviceName; taskName=$taskName; rebootVerified=$false; managedFatalVerified=$false }

function Get-FreePort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}
function Get-OwnedService {
    param($Launch)
    $registered = Get-CimInstance -ClassName Win32_Service -Filter "Name = '$serviceName'"
    if ($null -eq $registered) { throw 'Owned test service registration disappeared.' }
    Assert-MonitorOwnedService -Service $registered -Launch $Launch
    return $registered
}
function Stop-OwnedService {
    param($Launch)
    $registered = Get-OwnedService -Launch $Launch
    $ownedProcess = $null
    if ([long]$registered.ProcessId -gt 0) { $ownedProcess = Get-Process -Id $registered.ProcessId -ErrorAction SilentlyContinue }
    $controller = Get-Service -Name $serviceName
    try {
        if ($controller.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -InputObject $controller
            $controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        }
        if ($null -ne $ownedProcess -and -not $ownedProcess.HasExited -and -not $ownedProcess.WaitForExit(30000)) {
            throw 'Owned service process did not exit after SCM stopped; maintenance is not safe.'
        }
    } finally { $controller.Dispose(); if ($null -ne $ownedProcess) { $ownedProcess.Dispose() } }
}
function Assert-LoopbackProcess {
    param([long]$ProcessId, [int]$Port, [string]$Executable)
    $native = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $ProcessId)
    if ($null -eq $native -or -not ([string]$native.ExecutablePath).Equals($Executable, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Listener process is not the owned extracted package executable.'
    }
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $ProcessId)
    if ($listeners.Count -eq 0 -or @($listeners | Where-Object { $_.LocalPort -eq $Port }).Count -eq 0) { throw 'Expected owned listener is absent.' }
    if (@($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1','::1') }).Count -gt 0) { throw 'Owned collector listened on a non-loopback address.' }
}
function Wait-OperationalHealth {
    param([int]$Port, [long]$AfterRunId=0, [long]$PreviousProcessId=0, [int]$Seconds=90, [switch]$Service)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lastState = 'no health response'
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $currentProcessId = 0
        if ($Service) {
            $registered = Get-OwnedService -Launch $serviceLaunch
            $currentProcessId = [long]$registered.ProcessId
            if ($registered.State -ne 'Running' -or $currentProcessId -le 0 -or $currentProcessId -eq $PreviousProcessId) { Start-Sleep -Milliseconds 500; continue }
        }
        try {
            $candidate = Invoke-RestMethod -Uri ('http://127.0.0.1:' + $Port + '/api/health') -TimeoutSec 3
            $lastState = ($candidate.collector | ConvertTo-Json -Compress)
            if ($candidate.mode -eq 'demo' -and $candidate.collection -eq 'automatic' -and $candidate.persistence -eq 'sqlite-ready' -and
                $candidate.collector.phase -eq 'Idle' -and $candidate.collector.lastOutcome -in @('Succeeded','Partial') -and
                $null -ne $candidate.collector.lastValidCommittedAtUtc -and [long]$candidate.collector.lastFinalRunId -gt $AfterRunId) {
                if (-not $Service) {
                    $owners = @(Get-NetTCPConnection -State Listen -LocalPort $Port | Select-Object -ExpandProperty OwningProcess -Unique)
                    if ($owners.Count -ne 1) { throw 'Scheduled task listener ownership is ambiguous.' }
                    $currentProcessId = [long]$owners[0]
                }
                Assert-LoopbackProcess -ProcessId $currentProcessId -Port $Port -Executable $exe
                return [pscustomobject]@{ Health=$candidate; ProcessId=$currentProcessId; ElapsedSeconds=$watch.Elapsed.TotalSeconds }
            }
        } catch { $lastState = $_.Exception.Message }
        Start-Sleep -Milliseconds 500
    }
    throw ('Collector failed to reach a valid committed idle run within ' + $Seconds + 's: ' + $lastState)
}
function Invoke-MaintenanceJson {
    param([string]$Operation, [string]$BackupFile)
    $arguments = @('--maintenance',$Operation,'--mode','demo','--data-dir',$serviceData)
    if (-not [string]::IsNullOrEmpty($BackupFile)) { $arguments += @('--backup-file',$BackupFile) }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.WorkingDirectory = $serviceLaunch.WorkingDirectory
    $start.Arguments = ($arguments | ForEach-Object { ConvertTo-MonitorNativeArgument $_ }) -join ' '
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(90000)) { $process.Kill(); $process.WaitForExit(); throw 'Owned maintenance process timed out.' }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $testRoot ($Operation + '.stdout.json')), $stdout, [Text.Encoding]::UTF8)
        [IO.File]::WriteAllText((Join-Path $testRoot ($Operation + '.stderr.log')), $stderr, [Text.Encoding]::UTF8)
        if ($process.ExitCode -ne 0) { throw ('Maintenance ' + $Operation + ' exited ' + $process.ExitCode + ': ' + $stderr) }
        $result = $stdout | ConvertFrom-Json
        if ($result.operation -ne $Operation) { throw 'Maintenance JSON operation differs.' }
        return $result
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
function Assert-SqliteFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw ('SQLite file missing: ' + $Path) }
    $stream = [IO.File]::OpenRead($Path)
    try {
        $header = New-Object byte[] 16
        if ($stream.Read($header,0,16) -ne 16 -or [Text.Encoding]::ASCII.GetString($header) -ne "SQLite format 3`0") { throw 'Preserved database has no SQLite header.' }
    } finally { $stream.Dispose() }
}
function Assert-MissingRun {
    param([int]$Port,[long]$RunId)
    try { $null = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $Port + '/api/runs/' + $RunId) -UseBasicParsing -TimeoutSec 10 }
    catch {
        if ($null -ne $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return }
        throw
    }
    throw 'A run lost by restoring the older backup was silently reused or retained.'
}

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $packageSource -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $packageRoot -Recurse }
    $exe = Join-Path $packageRoot 'app\AirWaveApMonitor.App.exe'
    foreach ($scriptName in @('Install-Service.ps1','Remove-Service.ps1','Install-ScheduledTask.ps1','Remove-ScheduledTask.ps1','Monitor-Common.ps1','Monitor-ServiceRecovery.ps1','Backup-Restore.ps1','Start-Monitor.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $scriptName) -PathType Leaf)) { throw ('Packaged operation script missing: ' + $scriptName) }
    }
    . (Join-Path $packageRoot 'Monitor-Common.ps1')
    $servicePort = Get-FreePort
    $serviceLaunch = Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory $serviceData -Mode demo -Port $servicePort -ServiceName $serviceName -RequireExecutable
    & (Join-Path $packageRoot 'Install-Service.ps1') -ServiceName $serviceName -DataDirectory $serviceData -Mode demo -Port $servicePort -ExecutablePath $exe -DemoLocalSystem -StartNow
    $initial = Wait-OperationalHealth -Port $servicePort -Service
    $policy = Get-MonitorServiceRecovery -ServiceName $serviceName
    if (($policy.ActionTypes -join ',') -ne '1,1,1,0' -or ($policy.Delays -join ',') -ne '60000,60000,60000,0' -or $policy.ResetPeriod -ne 86400 -or -not $policy.NonCrashFailures) { throw 'Actual SCM recovery policy differs.' }
    $profilePath = Join-Path $serviceData 'profile.json'
    $profileHash = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
    $profile = Get-Content -LiteralPath $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $evidence.serviceInitialRunId = [long]$initial.Health.collector.lastFinalRunId
    $evidence.serviceInitialProcessId = $initial.ProcessId
    Assert-LoopbackProcess -ProcessId $initial.ProcessId -Port $servicePort -Executable $exe
    Stop-Process -Id $initial.ProcessId -Force
    $restarted = Wait-OperationalHealth -Port $servicePort -AfterRunId $evidence.serviceInitialRunId -PreviousProcessId $initial.ProcessId -Seconds 120 -Service
    $evidence.unexpectedProcessKillRecovered = $true
    $evidence.recoveryElapsedSeconds = [Math]::Round($restarted.ElapsedSeconds,3)
    $evidence.restartedProcessId = $restarted.ProcessId
    $evidence.restartedRunId = [long]$restarted.Health.collector.lastFinalRunId
    Write-Host 'PASS: unexpected Stop-Process crash recovered through SCM; this does not reproduce a managed fatal exception or reboot.'

    Stop-OwnedService -Launch $serviceLaunch
    $backup = Invoke-MaintenanceJson -Operation pre-update-backup
    if ($backup.datasetId -ne $profile.DatasetId -or $backup.schemaVersion -lt 1 -or -not (Test-Path -LiteralPath ($backup.backupPath + '.manifest.json'))) { throw 'Verified backup identity/manifest is missing.' }
    Assert-SqliteFile -Path $backup.backupPath
    if ((Get-FileHash -LiteralPath $backup.backupPath -Algorithm SHA256).Hash -ne $backup.sha256) { throw 'Backup JSON SHA256 differs from the actual file.' }
    Start-Service -Name $serviceName
    $later = Wait-OperationalHealth -Port $servicePort -AfterRunId $evidence.restartedRunId -Service
    $lostRunId = [long]$later.Health.collector.lastFinalRunId
    $baseUrl = 'http://127.0.0.1:' + $servicePort
    $beforeRestore = Invoke-RestMethod -Uri ($baseUrl + '/api/runs?pageSize=500') -TimeoutSec 15
    $lostHighWater = [long](($beforeRestore.rows | Measure-Object -Property runId -Maximum).Maximum)
    if ($lostHighWater -lt $lostRunId) { throw 'Run history did not include the post-backup committed run.' }
    Stop-OwnedService -Launch $serviceLaunch
    $restore = Invoke-MaintenanceJson -Operation restore -BackupFile $backup.backupPath
    if ($restore.datasetId -ne $backup.datasetId -or $restore.schemaVersion -lt $backup.schemaVersion -or $restore.requiresCredentialRegistration -ne $false -or [string]::IsNullOrWhiteSpace($restore.restoreEventId)) { throw 'Demo restore receipt is invalid.' }
    Assert-SqliteFile -Path $restore.preservedDatabasePath
    $preservedHash = (Get-FileHash -LiteralPath $restore.preservedDatabasePath -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash -ne $profileHash) { throw 'Restore changed the current profile.' }
    Start-Service -Name $serviceName
    $restored = Wait-OperationalHealth -Port $servicePort -AfterRunId $lostHighWater -Service
    $runs = Invoke-RestMethod -Uri ($baseUrl + '/api/runs?pageSize=500') -TimeoutSec 15
    $restoreRuns = @($runs.rows | Where-Object { $_.trigger -eq 'DatabaseRestore' -and $_.lifecycle -eq 'Interrupted' -and [long]$_.runId -gt $lostHighWater })
    if ($restoreRuns.Count -ne 1) { throw 'Restore did not produce exactly one fresh interrupted DatabaseRestore run.' }
    $restoredRunId = [long]$restored.Health.collector.lastFinalRunId
    if ($restoredRunId -le [long]$restoreRuns[0].runId) { throw 'A normal committed run did not follow the restoration gap.' }
    Assert-MissingRun -Port $servicePort -RunId $lostRunId
    $restoration = Invoke-RestMethod -Uri ($baseUrl + '/api/runs/' + $restoreRuns[0].runId) -TimeoutSec 15
    if ($restoration.lifecycle -ne 'Interrupted' -or $restoration.outcome -ne 'Failed' -or $null -ne $restoration.summary.confirmedDown -or $restoration.summary.unknown -le 0) { throw 'Restore gap snapshot does not preserve unknown observation semantics.' }
    $null = Invoke-RestMethod -Uri ($baseUrl + '/api/runs/' + $evidence.restartedRunId) -TimeoutSec 15
    if ((Get-FileHash -LiteralPath $restore.preservedDatabasePath -Algorithm SHA256).Hash -ne $preservedHash -or
        (Get-FileHash -LiteralPath $backup.backupPath -Algorithm SHA256).Hash -ne $backup.sha256) { throw 'New collection modified the preserved old DB or backup.' }
    $evidence.backupSchemaVersion = $backup.schemaVersion
    $evidence.backupSha256 = $backup.sha256
    $evidence.lostRunId = $lostRunId
    $evidence.lostHighWater = $lostHighWater
    $evidence.restorationRunId = [long]$restoreRuns[0].runId
    $evidence.restoredCurrentRunId = $restoredRunId
    $evidence.preservedDatabaseSha256 = $preservedHash
    Stop-OwnedService -Launch $serviceLaunch
    $databaseHash = (Get-FileHash -LiteralPath (Join-Path $serviceData 'monitor.db') -Algorithm SHA256).Hash
    & (Join-Path $packageRoot 'Remove-Service.ps1') -ServiceName $serviceName -DataDirectory $serviceData -Mode demo -Port $servicePort -ExecutablePath $exe
    if ($null -ne (Get-CimInstance -ClassName Win32_Service -Filter "Name = '$serviceName'")) { throw 'Owned service registration remains.' }
    if ((Get-FileHash -LiteralPath (Join-Path $serviceData 'monitor.db') -Algorithm SHA256).Hash -ne $databaseHash -or
        (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash -ne $profileHash) { throw 'Service removal changed retained DB/profile files.' }
    $evidence.serviceRemovalPreservedData = $true

    $taskPort = Get-FreePort
    $taskLaunch = Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory $taskData -Mode demo -Port $taskPort -RequireExecutable
    & (Join-Path $packageRoot 'Install-ScheduledTask.ps1') -TaskName $taskName -DataDirectory $taskData -Mode demo -Port $taskPort -ExecutablePath $exe -DemoLocalSystem -StartNow
    $taskReady = Wait-OperationalHealth -Port $taskPort
    $registeredTask = Get-MonitorRegisteredTask -Name $taskName
    Assert-MonitorOwnedTask -Task $registeredTask -Launch $taskLaunch
    [xml]$taskXml = Export-ScheduledTask -TaskPath $taskPath -TaskName $taskName
    $ns = New-Object Xml.XmlNamespaceManager($taskXml.NameTable)
    $ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    $settings = $taskXml.SelectSingleNode('/t:Task/t:Settings',$ns)
    if ($settings.MultipleInstancesPolicy -ne 'IgnoreNew' -or [Xml.XmlConvert]::ToTimeSpan([string]$settings.ExecutionTimeLimit) -ne [TimeSpan]::Zero -or
        [int]$settings.RestartOnFailure.Count -ne 3 -or [Xml.XmlConvert]::ToTimeSpan([string]$settings.RestartOnFailure.Interval) -ne [TimeSpan]::FromMinutes(1) -or
        $settings.StartWhenAvailable -ne 'true' -or $null -eq $taskXml.SelectSingleNode('/t:Task/t:Triggers/t:BootTrigger',$ns)) { throw 'Actual scheduled task settings do not match the always-on contract.' }
    $taskProfilePath = Join-Path $taskData 'profile.json'
    $taskProfileHash = (Get-FileHash -LiteralPath $taskProfilePath -Algorithm SHA256).Hash
    $taskProcess = Get-Process -Id $taskReady.ProcessId
    try {
        & (Join-Path $packageRoot 'Remove-ScheduledTask.ps1') -TaskName $taskName -DataDirectory $taskData -Mode demo -Port $taskPort -ExecutablePath $exe
        if (-not $taskProcess.HasExited -and -not $taskProcess.WaitForExit(30000)) { throw 'Owned task process did not exit after task removal; preserve its data folder.' }
    } finally { $taskProcess.Dispose() }
    if ($null -ne (Get-MonitorRegisteredTask -Name $taskName)) { throw 'Owned scheduled task registration remains.' }
    Assert-SqliteFile -Path (Join-Path $taskData 'monitor.db')
    if ((Get-FileHash -LiteralPath $taskProfilePath -Algorithm SHA256).Hash -ne $taskProfileHash) { throw 'Task removal changed the retained profile.' }
    $evidence.taskCommittedRunId = [long]$taskReady.Health.collector.lastFinalRunId
    $evidence.taskProcessId = $taskReady.ProcessId
    $evidence.taskExecutionTimeLimit = [string]$settings.ExecutionTimeLimit
    $evidence.taskRemovalPreservedData = $true
    $evidence.taskProcessExited = $true
    $passed = $true
    Write-Host 'PASS: packaged service/task collection, loopback ownership, SCM crash recovery, stopped verified backup/restore with gap and RunId high-water preservation, owned registration removal and retained data.'
} finally {
    # A pending SCM crash-restart can outlive Stop. Disable only our fully verified registration before cleanup.
    if ($null -ne $serviceLaunch) {
        try {
            $remainingService = Get-CimInstance -ClassName Win32_Service -Filter "Name = '$serviceName'"
            if ($null -ne $remainingService) {
                Assert-MonitorOwnedService -Service $remainingService -Launch $serviceLaunch
                Set-Service -Name $serviceName -StartupType Disabled
                & (Join-Path $packageRoot 'Remove-Service.ps1') -ServiceName $serviceName -DataDirectory $serviceData -Mode demo -Port $servicePort -ExecutablePath $exe
            }
        } catch { $cleanupOk = $false; Write-Warning ('Owned service cleanup needs inspection: ' + $_.Exception.Message) }
    }
    if ($null -ne $taskLaunch) {
        try {
            $remainingTask = Get-MonitorRegisteredTask -Name $taskName
            if ($null -ne $remainingTask) {
                Assert-MonitorOwnedTask -Task $remainingTask -Launch $taskLaunch
                & (Join-Path $packageRoot 'Remove-ScheduledTask.ps1') -TaskName $taskName -DataDirectory $taskData -Mode demo -Port $taskPort -ExecutablePath $exe
            }
        } catch { $cleanupOk = $false; Write-Warning ('Owned task cleanup needs inspection: ' + $_.Exception.Message) }
    }
    $evidence.passed = $passed
    $evidence.cleanupSucceeded = $cleanupOk
    if (Test-Path -LiteralPath $testRoot) {
        $json = $evidence | ConvertTo-Json -Depth 6
        [IO.File]::WriteAllText((Join-Path $testRoot 'operations-evidence.json'),$json,[Text.Encoding]::UTF8)
        Write-Host ('AIRWAVE_WINDOWS_OPERATIONS_RESULT=' + ($evidence | ConvertTo-Json -Depth 6 -Compress))
        if ($passed -and $cleanupOk) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
        else { Write-Warning ('Preserved owned test folder for diagnosis: ' + $testRoot) }
    }
    if (-not $cleanupOk -and $passed) { throw 'Operations checks passed but owned registration cleanup could not be verified.' }
}
