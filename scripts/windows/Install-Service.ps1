[CmdletBinding(DefaultParameterSetName='Credential')]
param(
    [Parameter(Mandatory=$true)][string]$ServiceName,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [ValidateSet('live','demo')][string]$Mode = 'live',
    [ValidateRange(1,65535)][int]$Port = 8765,
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'app\AirWaveApMonitor.App.exe'),
    [Parameter(Mandatory=$true,ParameterSetName='Credential')][PSCredential]$Credential,
    [Parameter(Mandatory=$true,ParameterSetName='DemoSystem')][switch]$DemoLocalSystem,
    [switch]$StartNow
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Monitor-Common.ps1')
Assert-MonitorAdministrator
$ServiceName = Test-MonitorRegistrationName $ServiceName
Assert-MonitorAccountSelection -ParameterSetName $PSCmdlet.ParameterSetName -Mode $Mode -DemoLocalSystem ([bool]$DemoLocalSystem)
$launch = Get-MonitorLaunchSpec -ExecutablePath $ExecutablePath -DataDirectory $DataDirectory -Mode $Mode -Port $Port -ServiceName $ServiceName -RequireExecutable
if ($null -ne (Get-CimInstance -ClassName Win32_Service -Filter "Name = '$ServiceName'")) { throw '같은 이름의 서비스가 있습니다. 기존 등록을 덮어쓰지 않습니다.' }
Initialize-MonitorServiceRecovery
New-Item -ItemType Directory -Path $launch.DataDirectory -Force | Out-Null
$parameters = @{ Name=$ServiceName; DisplayName=('AirWave AP Monitor (' + $ServiceName + ')'); BinaryPathName=$launch.CommandLine; Description=$launch.OwnershipMarker; StartupType='Automatic' }
if ($PSCmdlet.ParameterSetName -eq 'Credential') { $parameters.Credential = $Credential }
New-Service @parameters | Out-Null
# Recovery covers unexpected nonzero process exit; an operator's Stop is not a failure.
Set-MonitorServiceRecovery -ServiceName $ServiceName
if ($StartNow) {
    Start-Service -Name $ServiceName
    $service = Get-Service -Name $ServiceName
    try { $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30)) } finally { $service.Dispose() }
}
Write-Host ('서비스 등록: ' + $ServiceName + ' / http://127.0.0.1:' + $Port)
Write-Host '선택한 계정의 서비스 로그온 권한과 데이터 폴더 수정 권한을 확인하세요. 기존 자격 증명이 다른 계정으로 보호되었다면 실행 중인 서비스의 설정 화면에서 다시 등록하세요.'
