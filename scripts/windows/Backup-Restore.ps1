[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('backup','pre-update-backup','restore')][string]$Operation,
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [ValidateSet('live','demo')][string]$Mode = 'live',
    [string]$BackupFile,
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'app\AirWaveApMonitor.App.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Monitor-Common.ps1')
Assert-MonitorWindows
$launch = Get-MonitorLaunchSpec -ExecutablePath $ExecutablePath -DataDirectory $DataDirectory -Mode $Mode -RequireExecutable
if ($Operation -eq 'restore') {
    if ([string]::IsNullOrWhiteSpace($BackupFile) -or -not (Test-Path -LiteralPath $BackupFile -PathType Leaf)) { throw '복원할 검증 백업 DB 파일을 지정하세요.' }
} elseif (-not [string]::IsNullOrWhiteSpace($BackupFile)) { throw 'BackupFile은 restore에만 사용합니다.' }
$arguments = @('--maintenance', $Operation, '--mode', $Mode, '--data-dir', $launch.DataDirectory)
if ($Operation -eq 'restore') { $arguments += @('--backup-file', [IO.Path]::GetFullPath($BackupFile)) }
# ProcessStartInfo uses the same explicit quoting as SCM and Task Scheduler on PowerShell 5.1.
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $launch.Executable
$start.WorkingDirectory = $launch.WorkingDirectory
$start.Arguments = ($arguments | ForEach-Object { ConvertTo-MonitorNativeArgument $_ }) -join ' '
$start.UseShellExecute = $false
$process = New-Object Diagnostics.Process
$process.StartInfo = $start
try {
    [void]$process.Start()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw ('유지 관리 실패. 수집기 정지·같은 실행 계정·경로·백업 검증 결과를 확인하세요. 종료 코드: ' + $process.ExitCode) }
} finally { $process.Dispose() }
