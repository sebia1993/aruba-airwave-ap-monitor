param([string]$OutputDirectory = 'artifacts/win-x64/launcher')
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Native launcher compilation requires Windows MSVC.' }
$repository = Split-Path -Parent $PSScriptRoot
$destination = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory)) }
$source = Join-Path $repository 'src\AirWaveApMonitor.Launcher\main.cpp'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer vswhere.exe was not found.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) { throw 'MSVC x64 tools were not found.' }
$developerCommand = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
$environmentLines = & $env:ComSpec /d /s /c ('call "' + $developerCommand + '" -no_logo -arch=amd64 -host_arch=amd64 >nul && set')
if ($LASTEXITCODE -ne 0) { throw 'MSVC environment initialization failed.' }
foreach ($line in $environmentLines) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$objectDirectory = Join-Path $destination 'obj'
New-Item -ItemType Directory -Path $objectDirectory -Force | Out-Null
$output = Join-Path $destination 'AirWaveAPMonitor.exe'
$object = Join-Path $objectDirectory 'launcher.obj'
& cl.exe /nologo /std:c++17 /O2 /W4 /WX /EHsc /MT /utf-8 /DUNICODE /D_UNICODE $source ("/Fo" + $object) ("/Fe" + $output) /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT user32.lib shell32.lib
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output)) { throw 'Native launcher compilation failed.' }
Write-Output $output
