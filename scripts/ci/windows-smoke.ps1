param(
    [Parameter(Mandatory = $true)]
    [string]$AppDirectory
)

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('AirWave 한글 경로 ' + [guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $testRoot '앱 폴더'
$demoData = Join-Path $testRoot '데모 데이터'
$process = $null

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $AppDirectory '*') -Destination $packageRoot -Recurse
    $exe = Join-Path $packageRoot 'AirWaveApMonitor.App.exe'
    if (-not (Test-Path $exe)) { throw 'Published Windows executable missing.' }
    $arguments = '--mode demo --data-dir "' + $demoData + '" --port 18765'
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $packageRoot -PassThru -RedirectStandardOutput (Join-Path $testRoot 'stdout.log') -RedirectStandardError (Join-Path $testRoot 'stderr.log')
    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw ('App exited with code ' + $process.ExitCode) }
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/health' -TimeoutSec 2
            break
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if ($null -eq $health) { throw 'Loopback health did not respond.' }
    if ($health.mode -ne 'demo') { throw 'Demo mode not reported.' }
    if ($health.persistence -ne 'sqlite-ready') { throw 'Native SQLite storage was not initialized before serving HTTP.' }
    $databasePath = Join-Path $demoData 'monitor.db'
    if (-not (Test-Path -LiteralPath $databasePath)) { throw 'Initialized SQLite database is missing.' }
    $databaseHeader = [System.IO.File]::ReadAllBytes($databasePath)
    if ($databaseHeader.Length -lt 16 -or [System.Text.Encoding]::ASCII.GetString($databaseHeader, 0, 16) -ne "SQLite format 3`0") {
        throw 'Initialized database does not have the SQLite file header.'
    }
    $page = Invoke-WebRequest -Uri 'http://127.0.0.1:18765/' -UseBasicParsing -TimeoutSec 5
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch 'AirWave') { throw 'Local page did not load.' }
    $settings = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/settings' -TimeoutSec 5
    if ($settings.mode -ne 'demo' -or [string]::IsNullOrWhiteSpace($settings.executionAccount)) { throw 'Settings or process account missing.' }
    $csrf = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/antiforgery' -SessionVariable localSession -TimeoutSec 5
    if ([string]::IsNullOrWhiteSpace($csrf.token)) { throw 'CSRF token missing.' }
    $headers = @{ 'X-CSRF-TOKEN' = $csrf.token }
    $rejected = $false
    try {
        Invoke-WebRequest -Uri 'http://127.0.0.1:18765/api/diagnostics' -Method Post -Headers $headers -WebSession $localSession -UseBasicParsing -TimeoutSec 5 | Out-Null
    } catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 403) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Mutation without Origin was accepted.' }
    $headers['Origin'] = 'http://127.0.0.1:18765'
    $diagnostic = Invoke-RestMethod -Uri 'http://127.0.0.1:18765/api/diagnostics' -Method Post -Headers $headers -WebSession $localSession -TimeoutSec 15
    if ([string]::IsNullOrWhiteSpace($diagnostic.diagnosticId) -or $diagnostic.parsedRecords -ne 2048 -or $diagnostic.excludedRecords -ne 0) {
        throw 'Published Demo diagnostic did not run the expected parser fixture.'
    }
    if ($diagnostic.connectionPath -ne 'synthetic-memory' -or $null -ne $diagnostic.httpStatus -or $null -ne $diagnostic.runId) {
        throw 'Diagnostic reported a live HTTP result or operational run identity.'
    }
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id)
    if ($listeners.Count -eq 0) { throw 'No listening socket found.' }
    if (@($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }).Count -gt 0) {
        throw 'Application bound a non-loopback address.'
    }
    Write-Host 'PASS: self-contained Windows executable, native SQLite initialization, Korean/space path, loopback-only listener, guarded mutation and parsed 2048-record Demo diagnostic.'
} catch {
    Get-Content (Join-Path $testRoot 'stdout.log') -ErrorAction SilentlyContinue
    Get-Content (Join-Path $testRoot 'stderr.log') -ErrorAction SilentlyContinue
    throw
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    if (Test-Path $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
