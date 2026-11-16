param(
    [Parameter(Mandatory = $true)]
    [string]$Archive
)

$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$hashLine = [System.IO.File]::ReadAllText($archivePath + '.sha256').Trim()
if ($hashLine -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid ZIP checksum file.' }
if ($Matches[2] -ne [System.IO.Path]::GetFileName($archivePath)) { throw 'ZIP checksum file name differs.' }
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $Matches[1]) { throw 'ZIP checksum mismatch.' }
$expanded = Join-Path ([System.IO.Path]::GetTempPath()) ('반입 ZIP 검증 ' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $expanded
    $version = Get-Content -LiteralPath (Join-Path $expanded 'metadata/VERSION.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($version.rid -ne 'win-x64' -or $version.stage -ne 'core-monitoring' -or $version.onsite_verified -ne $false) {
        throw 'Package scope metadata is invalid.'
    }
    foreach ($required in @('AirWaveAPMonitor.exe', '시작하기.txt', 'app/AirWaveApMonitor.App.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $expanded $required) -PathType Leaf)) { throw ('Required package file missing: ' + $required) }
    }
    $manifestPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $manifestFile = [System.IO.Path]::GetFullPath((Join-Path $expanded 'metadata/SHA256SUMS.txt'))
    $fileCount = 0
    foreach ($line in Get-Content -LiteralPath $manifestFile -Encoding UTF8) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid package checksum entry.' }
        $expectedHash = $Matches[1]
        $relativePath = $Matches[2]
        $filePath = [System.IO.Path]::GetFullPath((Join-Path $expanded $relativePath))
        if (-not $filePath.StartsWith($expanded + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Package checksum path escaped the package.'
        }
        if (-not $manifestPaths.Add($filePath)) { throw ('Duplicate package checksum path: ' + $relativePath) }
        if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $expectedHash) {
            throw ('Package checksum mismatch: ' + $relativePath)
        }
        $fileCount++
    }
    $actualFileCount = @(Get-ChildItem -LiteralPath $expanded -Recurse -File).Count
    if ($fileCount -ne $actualFileCount - 1) { throw 'Package checksum manifest is incomplete.' }
    foreach ($file in Get-ChildItem -LiteralPath $expanded -Recurse -File) {
        if ($file.FullName -eq $manifestFile) { continue }
        if (-not $manifestPaths.Contains($file.FullName)) { throw ('File absent from package checksum manifest: ' + $file.FullName) }
    }
    $toolsDirectory = Join-Path $expanded 'tools'
    $requiredScripts = @('Start-Monitor.ps1', 'Monitor-Common.ps1', 'Monitor-ServiceRecovery.ps1', 'Install-Service.ps1',
        'Remove-Service.ps1', 'Install-ScheduledTask.ps1', 'Remove-ScheduledTask.ps1', 'Backup-Restore.ps1')
    $packagedScripts = @(Get-ChildItem -LiteralPath $toolsDirectory -Filter '*.ps1' -File)
    if ($packagedScripts.Count -ne $requiredScripts.Count) { throw 'Package must contain exactly eight operation scripts in tools.' }
    foreach ($scriptName in $requiredScripts) {
        $script = Get-Item -LiteralPath (Join-Path $toolsDirectory $scriptName)
        $parseTokens = $null; $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$parseTokens, [ref]$parseErrors) | Out-Null
        if ($parseErrors.Count -ne 0) { throw ('Windows PowerShell could not parse: ' + $script.Name) }
    }
    & (Join-Path $PSScriptRoot 'windows-launcher-smoke.ps1') -PackageDirectory $expanded
    & (Join-Path $PSScriptRoot 'windows-smoke.ps1') -AppDirectory (Join-Path $expanded 'app')
    & (Join-Path $PSScriptRoot 'windows-operations-smoke.ps1') -PackageDirectory $expanded
    Write-Host ('PASS: extracted core ZIP, ' + $fileCount + ' file hashes, metadata scope, native desktop launcher and eight tools scripts parsed by Windows PowerShell.')
} finally {
    if (Test-Path -LiteralPath $expanded) { Remove-Item -LiteralPath $expanded -Recurse -Force }
}
