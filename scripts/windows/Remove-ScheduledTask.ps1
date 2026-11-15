[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$TaskName,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [ValidateSet('live','demo')][string]$Mode = 'live',
    [ValidateRange(1,65535)][int]$Port = 8765,
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'app\AirWaveApMonitor.App.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Monitor-Common.ps1')
Assert-MonitorAdministrator
$TaskName = Test-MonitorRegistrationName $TaskName
$launch = Get-MonitorLaunchSpec -ExecutablePath $ExecutablePath -DataDirectory $DataDirectory -Mode $Mode -Port $Port
$taskPath = '\AirWaveAPMonitor\'
$task = Get-MonitorRegisteredTask -Name $TaskName
if ($null -eq $task) { Write-Host '해당 예약 작업 등록이 없습니다. 데이터는 그대로입니다.'; return }
Assert-MonitorOwnedTask -Task $task -Launch $launch
if ($task.State -eq 'Running' -or $task.State -eq 'Queued') {
    Stop-ScheduledTask -TaskPath $taskPath -TaskName $TaskName
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $task = Get-ScheduledTask -TaskPath $taskPath -TaskName $TaskName
        if ($task.State -ne 'Running' -and $task.State -ne 'Queued') { break }
        if ($watch.Elapsed.TotalSeconds -ge 30) { throw '예약 작업 종료를 확인하지 못했습니다. 등록과 데이터를 보존합니다.' }
        Start-Sleep -Milliseconds 250
    } while ($true)
}
Unregister-ScheduledTask -TaskPath $taskPath -TaskName $TaskName -Confirm:$false
Write-Host '이 앱의 예약 작업 등록만 제거했습니다. DB·설정·자격 증명·백업·프로그램 파일은 보존했습니다. 진행 중이던 수집은 다음 시작 시 중단 회차로 복원될 수 있습니다.'
