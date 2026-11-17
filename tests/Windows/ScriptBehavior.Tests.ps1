param([string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$ErrorActionPreference = 'Stop'
. (Join-Path $RepositoryRoot 'scripts/windows/Monitor-Common.ps1')
$script:passed = 0
function Assert-Equal($Expected, $Actual) {
    if ($Expected -cne $Actual) { throw ('Expected [' + $Expected + '] but got [' + $Actual + ']') }
    $script:passed++
}
function Assert-Throws([scriptblock]$Action) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw 'Expected rejection.' }
    $script:passed++
}
Assert-Equal '""' (ConvertTo-MonitorNativeArgument '')
Assert-Equal '"한글 경로"' (ConvertTo-MonitorNativeArgument '한글 경로')
Assert-Equal '"C:\한글 폴더\\"' (ConvertTo-MonitorNativeArgument 'C:\한글 폴더\')
Assert-Equal '"a\"b"' (ConvertTo-MonitorNativeArgument 'a"b')
Assert-Equal '"a\\\"b"' (ConvertTo-MonitorNativeArgument 'a\"b')
Assert-MonitorAccountSelection -ParameterSetName DemoSystem -Mode demo -DemoLocalSystem $true
$script:passed++
Assert-Throws { Assert-MonitorAccountSelection -ParameterSetName DemoSystem -Mode live -DemoLocalSystem $true }
Assert-Throws { Assert-MonitorAccountSelection -ParameterSetName DemoSystem -Mode live -DemoLocalSystem $false }
Assert-Throws { Assert-MonitorAccountSelection -ParameterSetName DemoSystem -Mode demo -DemoLocalSystem $false }
Assert-Equal 'AirWave-CI_1' (Test-MonitorRegistrationName 'AirWave-CI_1')
Assert-Throws { Test-MonitorRegistrationName "name' OR name='x" }
Assert-Throws { Test-MonitorRegistrationName ('a' * 81) }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('AirWave 스크립트 ' + [guid]::NewGuid().ToString('N'))
try {
    $package = Join-Path $testRoot '패키지'
    $app = Join-Path $package 'app'
    $data = Join-Path $testRoot '데이터'
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    $exe = Join-Path $app 'AirWaveApMonitor.App.exe'
    [IO.File]::WriteAllText($exe, 'synthetic path fixture only')
    $launch = Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory $data -Mode demo -Port 18765 -ServiceName AirWave-CI_1 -RequireExecutable
    Assert-Equal ([IO.Path]::GetFullPath($exe)) $launch.Executable
    Assert-Equal ([IO.Path]::GetFullPath($data)) $launch.DataDirectory
    Assert-Equal ('"--mode" "demo" "--data-dir" "' + $data + '" "--port" "18765" "--service-name" "AirWave-CI_1"') $launch.Arguments
    Assert-Throws { Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory (Join-Path $package 'data') -Mode live -Port 8765 }
    Assert-Throws { Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory relative-data -Mode live -Port 8765 }
    Assert-Throws { Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory $data -Mode live -Port 0 }
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        Assert-Throws { Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory 'C:relative-data' -Mode live -Port 8765 }
        Assert-Throws { Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory '\relative-drive-data' -Mode live -Port 8765 }
        Assert-Throws { Get-MonitorLaunchSpec -ExecutablePath $exe -DataDirectory '\\server\share\data' -Mode live -Port 8765 }
    }
    $owned = [pscustomobject]@{ Description = $launch.OwnershipMarker; PathName = $launch.CommandLine }
    Assert-MonitorOwnedService -Service $owned -Launch $launch
    $script:passed++
    $foreign = [pscustomobject]@{ Description = $launch.OwnershipMarker; PathName = $launch.CommandLine + ' --other' }
    Assert-Throws { Assert-MonitorOwnedService -Service $foreign -Launch $launch }
    $foreign = [pscustomobject]@{ Description = 'unrelated'; PathName = $launch.CommandLine }
    Assert-Throws { Assert-MonitorOwnedService -Service $foreign -Launch $launch }
    $task = [pscustomobject]@{ Description=$launch.OwnershipMarker; Actions=@([pscustomobject]@{ Execute=$launch.Executable; Arguments=$launch.Arguments; WorkingDirectory=$launch.WorkingDirectory }) }
    Assert-MonitorOwnedTask -Task $task -Launch $launch
    $script:passed++
    $task.Actions[0].Arguments += ' --other'
    Assert-Throws { Assert-MonitorOwnedTask -Task $task -Launch $launch }
    Assert-Equal 'synthetic path fixture only' ([IO.File]::ReadAllText($exe))
} finally { if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force } }
Write-Host ('PASS: ' + $script:passed + ' script behavior assertions; no service/task registration performed.')
