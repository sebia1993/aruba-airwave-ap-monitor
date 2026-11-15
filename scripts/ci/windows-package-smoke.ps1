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
    $version = Get-Content -LiteralPath (Join-Path $expanded 'VERSION.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($version.rid -ne 'win-x64' -or $version.stage -ne 'initial-diagnostics' -or $version.onsite_verified -ne $false) {
        throw 'Package scope metadata is invalid.'
    }
    $fileCount = 0
    foreach ($line in Get-Content -LiteralPath (Join-Path $expanded 'SHA256SUMS.txt') -Encoding UTF8) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid package checksum entry.' }
        $expectedHash = $Matches[1]
        $relativePath = $Matches[2]
        $filePath = [System.IO.Path]::GetFullPath((Join-Path $expanded $relativePath))
        if (-not $filePath.StartsWith($expanded + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Package checksum path escaped the package.'
        }
        if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $expectedHash) {
            throw ('Package checksum mismatch: ' + $relativePath)
        }
        $fileCount++
    }
    $actualFileCount = @(Get-ChildItem -LiteralPath $expanded -Recurse -File).Count
    if ($fileCount -ne $actualFileCount - 1) { throw 'Package checksum manifest is incomplete.' }
    $parseTokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $expanded 'Start-Monitor.ps1'), [ref]$parseTokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -ne 0) { throw 'Windows PowerShell could not parse the launcher.' }
    & (Join-Path $PSScriptRoot 'windows-smoke.ps1') -AppDirectory (Join-Path $expanded 'app')
    Write-Host ('PASS: extracted initial ZIP, ' + $fileCount + ' file hashes, scope metadata and Windows PowerShell launcher syntax.')
} finally {
    if (Test-Path -LiteralPath $expanded) { Remove-Item -LiteralPath $expanded -Recurse -Force }
}
