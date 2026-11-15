[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ServiceName,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [ValidateSet('live','demo')][string]$Mode = 'live',
    [ValidateRange(1,65535)][int]$Port = 8765,
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'app\AirWaveApMonitor.App.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Monitor-Common.ps1')
Assert-MonitorAdministrator
$ServiceName = Test-MonitorRegistrationName $ServiceName
$launch = Get-MonitorLaunchSpec -ExecutablePath $ExecutablePath -DataDirectory $DataDirectory -Mode $Mode -Port $Port -ServiceName $ServiceName
$registered = Get-CimInstance -ClassName Win32_Service -Filter "Name = '$ServiceName'"
if ($null -eq $registered) { Write-Host '해당 서비스 등록이 없습니다. 데이터는 그대로입니다.'; return }
Assert-MonitorOwnedService -Service $registered -Launch $launch
$service = Get-Service -Name $ServiceName
try {
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -InputObject $service
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }
} finally { $service.Dispose() }
Invoke-MonitorSc -Arguments @('delete', $ServiceName)
Write-Host '이 앱의 서비스 등록만 제거했습니다. DB·설정·자격 증명·백업·프로그램 파일은 보존했습니다.'
