# Shared by the explicitly invoked Windows operation scripts. No system changes on import.
function Assert-MonitorWindows {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '이 스크립트는 Windows에서 실행하세요.' }
}
function Assert-MonitorAdministrator {
    Assert-MonitorWindows
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '관리자 PowerShell에서 실행하세요.' }
    } finally { $identity.Dispose() }
}
function Test-MonitorRegistrationName {
    param([string]$Name)
    if ($Name -cnotmatch '\A[A-Za-z0-9_-]{1,80}\z') { throw '등록 이름은 영문·숫자·밑줄·하이픈 1~80자로 지정하세요.' }
    return $Name
}
function ConvertTo-MonitorNativeArgument {
    param([AllowEmptyString()][string]$Value)
    # Windows CommandLineToArgvW/CRT quoting, including quotes and a trailing backslash.
    if ($Value.IndexOf([char]0) -ge 0) { throw '인수에 NUL 문자를 사용할 수 없습니다.' }
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * ($slashes * 2 + 1)))
        } else { [void]$builder.Append(('\' * $slashes)) }
        [void]$builder.Append($character)
        $slashes = 0
    }
    [void]$builder.Append(('\' * ($slashes * 2)))
    [void]$builder.Append('"')
    return $builder.ToString()
}
function Get-MonitorLaunchSpec {
    param([Parameter(Mandatory=$true)][string]$ExecutablePath,
          [Parameter(Mandatory=$true)][string]$DataDirectory,
          [ValidateSet('demo','live')][string]$Mode = 'live',
          [ValidateRange(1,65535)][int]$Port = 8765,
          [string]$ServiceName,
          [switch]$RequireExecutable)
    foreach ($value in @($ExecutablePath, $DataDirectory)) {
        if ([string]::IsNullOrWhiteSpace($value) -or -not [IO.Path]::IsPathRooted($value) -or $value -match '[\x00-\x1f"]') {
            throw '실행 파일과 데이터 폴더는 제어 문자 없는 절대 경로로 지정하세요.'
        }
    }
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        foreach ($value in @($ExecutablePath, $DataDirectory)) {
            if ($value -notmatch '\A[A-Za-z]:[\\/]') { throw 'Windows 로컬 드라이브의 완전한 경로를 지정하세요. UNC·드라이브 상대 경로는 사용하지 않습니다.' }
        }
        $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($DataDirectory))
        if ($drive.DriveType -eq [IO.DriveType]::Network) { throw 'SQLite 데이터 폴더는 네트워크 드라이브에 둘 수 없습니다.' }
    }
    $exe = [IO.Path]::GetFullPath($ExecutablePath)
    $data = [IO.Path]::GetFullPath($DataDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ([IO.Path]::GetFileName($exe) -cne 'AirWaveApMonitor.App.exe') { throw 'AirWaveApMonitor.App.exe를 지정하세요.' }
    if ($RequireExecutable -and -not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '실행 파일이 없습니다. ZIP 전체를 새 폴더에 풀어 주세요.' }
    $working = [IO.Path]::GetDirectoryName($exe)
    $package = if ([IO.Path]::GetFileName($working) -ieq 'app') { [IO.Path]::GetDirectoryName($working) } else { $working }
    $packagePrefix = $package.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($data.Equals($package, [StringComparison]::OrdinalIgnoreCase) -or $data.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw '데이터 폴더는 프로그램 버전 폴더 밖에 지정하세요.'
    }
    if ([string]::IsNullOrWhiteSpace($data) -or [IO.Path]::GetPathRoot($data).TrimEnd('\','/') -eq $data.TrimEnd('\','/')) {
        throw '드라이브 루트 대신 전용 데이터 폴더를 지정하세요.'
    }
    $arguments = @('--mode', $Mode, '--data-dir', $data, '--port', [string]$Port)
    if (-not [string]::IsNullOrEmpty($ServiceName)) { $arguments += @('--service-name', (Test-MonitorRegistrationName $ServiceName)) }
    $quoted = ($arguments | ForEach-Object { ConvertTo-MonitorNativeArgument $_ }) -join ' '
    return [pscustomobject]@{
        Executable = $exe; DataDirectory = $data; WorkingDirectory = $working
        Arguments = $quoted; CommandLine = (ConvertTo-MonitorNativeArgument $exe) + ' ' + $quoted
        OwnershipMarker = 'AirWaveAPMonitor managed registration v1'
    }
}
function Assert-MonitorOwnedService {
    param($Service, $Launch)
    if ($null -eq $Service -or $Service.Description -cne $Launch.OwnershipMarker -or
        -not ([string]$Service.PathName).Equals($Launch.CommandLine, [StringComparison]::OrdinalIgnoreCase)) {
        throw '등록 설명·실행 파일·모드·데이터 경로·포트·이름이 일치하지 않아 다른 서비스를 변경하지 않습니다.'
    }
}
function Assert-MonitorOwnedTask {
    param($Task, $Launch)
    $actions = @($Task.Actions)
    if ($null -eq $Task -or $Task.Description -cne $Launch.OwnershipMarker -or $actions.Count -ne 1 -or
        -not ([string]$actions[0].Execute).Equals($Launch.Executable, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$actions[0].Arguments).Equals($Launch.Arguments, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$actions[0].WorkingDirectory).Equals($Launch.WorkingDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw '등록 설명과 실행 대상이 일치하지 않아 다른 예약 작업을 변경하지 않습니다.'
    }
}
function Invoke-MonitorSc {
    param([string[]]$Arguments)
    $output = & (Join-Path $env:SystemRoot 'System32\sc.exe') @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('서비스 관리자 명령이 실패했습니다. 코드: ' + $LASTEXITCODE) }
}

function Get-MonitorRegisteredTask {
    param([string]$Name)
    # Read failures remain errors; absence is determined from a successful enumeration.
    return Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskPath -eq '\AirWaveAPMonitor\' -and $_.TaskName -eq $Name }
}

function Assert-MonitorAccountSelection {
    param([string]$ParameterSetName, [string]$Mode, [bool]$DemoLocalSystem)
    if ($ParameterSetName -eq 'DemoSystem' -and (-not $DemoLocalSystem -or $Mode -ne 'demo')) {
        throw 'LocalSystem은 DemoLocalSystem을 명시적으로 켠 가상 Demo 검사에만 허용합니다. Live에는 수집 계정을 지정하세요.'
    }
}

. (Join-Path $PSScriptRoot 'Monitor-ServiceRecovery.ps1')
