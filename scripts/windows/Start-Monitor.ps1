param(
    [ValidateSet('live', 'demo')][string]$Mode = 'live',
    [string]$DataDirectory,
    [ValidateRange(1, 65535)][int]$Port = 8765
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Monitor-Common.ps1')
Assert-MonitorWindows
$exe = Get-MonitorDefaultExecutable
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $base = if ($Mode -eq 'live') { [Environment]::GetFolderPath('CommonApplicationData') } else { [Environment]::GetFolderPath('LocalApplicationData') }
    $DataDirectory = Join-Path (Join-Path $base 'AirWaveAPMonitor') $Mode
}
$launch = Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory $DataDirectory -Mode $Mode -Port $Port -RequireExecutable
Write-Host ('브라우저 주소: http://127.0.0.1:' + $Port)
Write-Host '브라우저를 닫아도 수집은 계속됩니다. 이 콘솔 창을 닫거나 Ctrl+C를 누르면 수집기가 종료됩니다.'
Write-Host '서비스·예약 작업 등록은 별도 설치 스크립트에서 명시적으로 수행합니다.'
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $launch.Executable
$start.WorkingDirectory = $launch.WorkingDirectory
$start.Arguments = $launch.Arguments
$start.UseShellExecute = $false
$process = New-Object Diagnostics.Process
$process.StartInfo = $start
$started = $false
$exitCode = 1
try {
    $started = $process.Start()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
} finally {
    if ($started -and -not $process.HasExited) {
        if (-not $process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
    }
    $process.Dispose()
}
exit $exitCode
