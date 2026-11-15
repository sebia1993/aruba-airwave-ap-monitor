param([string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$ErrorActionPreference = 'Stop'
. (Join-Path $RepositoryRoot 'scripts/windows/Monitor-Common.ps1')
Assert-MonitorAdministrator
$root = Join-Path ([IO.Path]::GetTempPath()) ('AirWave 복구 정책 ' + [guid]::NewGuid().ToString('N'))
$serviceName = 'AirWave-Recovery-' + [guid]::NewGuid().ToString('N')
$app = Join-Path $root 'package\app'
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Path $app -Force | Out-Null
$exe = Join-Path $app 'AirWaveApMonitor.App.exe'
# Registration-only fixture: this known local executable is NEVER started as a service.
Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\cmd.exe') -Destination $exe
try {
    & (Join-Path $RepositoryRoot 'scripts/windows/Install-Service.ps1') -ServiceName $serviceName -DataDirectory $data -Mode demo -ExecutablePath $exe -DemoLocalSystem
    $policy = Get-MonitorServiceRecovery -ServiceName $serviceName
    if ($policy.ResetPeriod -ne 86400) { throw 'SCM reset period differs.' }
    if ($policy.ActionTypes.Count -ne 4 -or ($policy.ActionTypes -join ',') -ne '1,1,1,0') {
        throw 'Expected three SCM restart actions followed by terminal SC_ACTION_NONE.'
    }
    if (($policy.Delays -join ',') -ne '60000,60000,60000,0') { throw 'SCM action delays differ.' }
    if (-not $policy.NonCrashFailures) { throw 'SCM non-crash failure policy is disabled.' }
    [IO.File]::WriteAllText((Join-Path $data 'preserve.txt'), 'keep')
    & (Join-Path $RepositoryRoot 'scripts/windows/Remove-Service.ps1') -ServiceName $serviceName -DataDirectory $data -Mode demo -ExecutablePath $exe
    if ($null -ne (Get-CimInstance -ClassName Win32_Service -Filter "Name = '$serviceName'")) { throw 'Test service was not removed.' }
    if ([IO.File]::ReadAllText((Join-Path $data 'preserve.txt')) -cne 'keep') { throw 'Removal changed data.' }
    Write-Host ('PASS: actual SCM actions=' + ($policy.ActionTypes -join ',') + '; delays=' + ($policy.Delays -join ',') + '; reset=' + $policy.ResetPeriod + '; preserved data; service never started.')
} finally {
    if ($null -ne (Get-CimInstance -ClassName Win32_Service -Filter "Name = '$serviceName'")) {
        & (Join-Path $RepositoryRoot 'scripts/windows/Remove-Service.ps1') -ServiceName $serviceName -DataDirectory $data -Mode demo -ExecutablePath $exe
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
