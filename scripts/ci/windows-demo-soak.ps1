param(
    [Parameter(Mandatory = $true)]
    [string]$AppDirectory,
    [int]$Cycles = 12
)
$ErrorActionPreference = 'Stop'
if ($Cycles -lt 3 -or $Cycles -gt 30) { throw 'Cycles must be 3..30.' }
$port = 19077
$origin = 'http://127.0.0.1:' + $port
$root = Join-Path ([IO.Path]::GetTempPath()) ('AirWave 데모 반복검증 ' + [guid]::NewGuid().ToString('N'))
$dataDir = Join-Path $root 'demo-data'
$exe = Join-Path $AppDirectory 'AirWaveApMonitor.App.exe'
$process = $null
$samples = @()
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Published Demo executable missing.' }
    $args = '--mode demo --data-dir "' + $dataDir + '" --port ' + $port
    $process = Start-Process -FilePath $exe -ArgumentList $args -WorkingDirectory $AppDirectory -PassThru -WindowStyle Hidden
    function Wait-Run([long]$after, [int]$seconds = 100) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($timer.Elapsed.TotalSeconds -lt $seconds) {
            if ($process.HasExited) { throw 'Demo exited before collection completed.' }
            try {
                $h = Invoke-RestMethod -Uri ($origin + '/api/health') -TimeoutSec 3
                if ($h.mode -eq 'demo' -and $h.collector.phase -eq 'Idle' -and
                    $h.collector.lastFinalRunId -gt $after -and $null -ne $h.collector.lastValidCommittedAtUtc) { return $h }
            } catch { }
            Start-Sleep -Milliseconds 250
        }
        throw ('Demo collection timeout after RunId ' + $after)
    }
    $ready = Wait-Run 0
    $current = [long]$ready.collector.lastFinalRunId
    $csrf = Invoke-RestMethod -Uri ($origin + '/api/antiforgery') -TimeoutSec 5 -SessionVariable webSession
    if (-not $csrf.token) { throw 'Missing CSRF token for test-only manual AP reads.' }
    $headers = @{ 'X-CSRF-TOKEN' = [string]$csrf.token; 'Origin' = $origin }
    for($i=1; $i -le $Cycles; $i++) {
        $start = [Diagnostics.Stopwatch]::StartNew()
        $receipt = Invoke-RestMethod -Uri ($origin + '/api/collector/requests') -Method Post -Headers $headers -WebSession $webSession -ContentType 'application/json' -Body '{"kind":"ApStatus","trigger":"Manual"}' -TimeoutSec 10
        if ($receipt.status -notin @('Accepted','Queued')) { throw ('Manual Demo request rejected at cycle ' + $i) }
        $health = Wait-Run $current
        $newRun = [long]$health.collector.lastFinalRunId
        if ($newRun -le $current) { throw 'Run IDs did not increase.' }
        if ($health.collector.lastOutcome -ne 'Succeeded') { throw ('Unexpected Demo result: ' + $health.collector.lastOutcome) }
        $current = $newRun
        $last = Invoke-RestMethod -Uri ($origin + '/api/runs?kind=ApStatus&page=1&pageSize=1') -TimeoutSec 10
        if ($last.rows.Count -lt 1 -or [long]$last.rows[0].runId -ne $newRun) { throw 'Committed AP run history mismatch.' }
        $process.Refresh()
        $db = Join-Path $dataDir 'monitor.db'
        if (-not (Test-Path -LiteralPath $db)) { throw 'Native SQLite database missing.' }
        $samples += [pscustomobject]@{
            cycle = $i
            runId = $newRun
            elapsedMs = [math]::Round($start.Elapsed.TotalMilliseconds,0)
            memoryMiB = [math]::Round($process.WorkingSet64 / 1MB,2)
            databaseMiB = [math]::Round((Get-Item -LiteralPath $db).Length / 1MB,2)
        }
        if ($process.WorkingSet64 -gt 1600MB) { throw 'Demo process memory crossed 1.6 GiB safety ceiling.' }
        if ((Get-Item -LiteralPath $db).Length -gt 2GB) { throw 'Demo database crossed 2 GiB safety ceiling.' }
    }
    $elapsed = @($samples | ForEach-Object {[double]$_.elapsedMs} | Sort-Object)
    $p95Index = [math]::Min($elapsed.Count-1,[math]::Ceiling($elapsed.Count*0.95)-1)
    $result = [pscustomobject]@{
        fixture = 'Demo ScenarioApSource'
        fixtureApCount = 2048
        cycles = $Cycles
        monotonicRunIds = $true
        allCommitted = $true
        p95CycleElapsedMs = $elapsed[$p95Index]
        peakWorkingSetMiB = ($samples | Measure-Object -Property memoryMiB -Maximum).Maximum
        finalDatabaseMiB = $samples[-1].databaseMiB
        samples = $samples
        liveAccess = $false
    }
    $dest = Join-Path $PSScriptRoot '../../artifacts/demo-soak'
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    $file = Join-Path $dest 'demo-soak-2048.json'
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($file),($result | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
    Write-Host ('PASS: {0} repeated 2048-AP Demo collections, monotonic RunId, committed SQLite, memory and DB ceilings. p95={1} ms' -f $Cycles,$result.p95CycleElapsedMs)
} finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
