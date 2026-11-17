param([string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'scripts/windows') -Filter '*.ps1')
foreach ($file in $files) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 239 -or $bytes[1] -ne 187 -or $bytes[2] -ne 191) { throw ('UTF-8 BOM missing: ' + $file.Name) }
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw ($file.Name + ': ' + (($errors | ForEach-Object { $_.Message }) -join '; ')) }
}
Write-Host ('PASS: ' + $files.Count + ' Windows operation scripts parsed with PowerShell ' + $PSVersionTable.PSVersion)
