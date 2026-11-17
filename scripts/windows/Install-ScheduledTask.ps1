[CmdletBinding(DefaultParameterSetName='Credential')]
param(
    [Parameter(Mandatory=$true)][string]$TaskName,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [ValidateSet('live','demo')][string]$Mode = 'live',
    [ValidateRange(1,65535)][int]$Port = 8765,
    [string]$ExecutablePath,
    [Parameter(Mandatory=$true,ParameterSetName='Credential')][PSCredential]$Credential,
    [Parameter(Mandatory=$true,ParameterSetName='DemoSystem')][switch]$DemoLocalSystem,
    [switch]$StartNow
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Monitor-Common.ps1')
if (-not $PSBoundParameters.ContainsKey('ExecutablePath')) { $ExecutablePath = Get-MonitorDefaultExecutable }
Assert-MonitorAdministrator
$TaskName = Test-MonitorRegistrationName $TaskName
Assert-MonitorAccountSelection -ParameterSetName $PSCmdlet.ParameterSetName -Mode $Mode -DemoLocalSystem ([bool]$DemoLocalSystem)
$launch = Get-MonitorLaunchSpec -ExecutablePath $ExecutablePath -DataDirectory $DataDirectory -Mode $Mode -Port $Port -RequireExecutable
$taskPath = '\AirWaveAPMonitor\'
if ($null -ne (Get-MonitorRegisteredTask -Name $TaskName)) { throw '같은 이름의 예약 작업이 있습니다. 기존 등록을 덮어쓰지 않습니다.' }
New-Item -ItemType Directory -Path $launch.DataDirectory -Force | Out-Null
$action = New-ScheduledTaskAction -Execute $launch.Executable -Argument $launch.Arguments -WorkingDirectory $launch.WorkingDirectory
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1)) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
if ($DemoLocalSystem) {
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
} else {
    $principal = New-ScheduledTaskPrincipal -UserId $Credential.UserName -LogonType Password -RunLevel Limited
}
$definition = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description $launch.OwnershipMarker
if ($DemoLocalSystem) {
    Register-ScheduledTask -TaskPath $taskPath -TaskName $TaskName -InputObject $definition | Out-Null
} else {
    # The scheduler API requires a transient string; never pass it to a child process or output it.
    $passwordPointer = [IntPtr]::Zero
    $password = $null
    try {
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Credential.Password)
        $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        Register-ScheduledTask -TaskPath $taskPath -TaskName $TaskName -InputObject $definition -User $Credential.UserName -Password $password | Out-Null
    } finally {
        $password = $null
        if ($passwordPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer) }
    }
}
if ($StartNow) { Start-ScheduledTask -TaskPath $taskPath -TaskName $TaskName }
Write-Host ('예약 작업 등록: ' + $taskPath + $TaskName + ' / http://127.0.0.1:' + $Port)
Write-Host '선택한 계정의 배치 로그온·데이터 폴더 접근 권한, 로그아웃 및 재부팅 뒤 수집을 확인하세요. 절전·방화벽·실행 정책은 변경하지 않았습니다.'
