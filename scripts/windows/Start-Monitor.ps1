param(
    [ValidateSet('live', 'demo')]
    [string]$Mode = 'live',
    [string]$DataDirectory,
    [ValidateRange(1, 65535)]
    [int]$Port = 8765
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'app\AirWaveApMonitor.App.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'app 폴더의 실행 파일이 없습니다. ZIP 전체를 새 폴더에 풀어 주세요.'
}
$appArguments = @('--mode', $Mode, '--port', [string]$Port)
if (-not [string]::IsNullOrWhiteSpace($DataDirectory)) {
    $appArguments += @('--data-dir', $DataDirectory)
}
Write-Host ('브라우저 주소: http://127.0.0.1:' + $Port)
Write-Host '초기 연결 진단용입니다. 자동 감시·장애 이력·서비스 설치는 아직 제공하지 않습니다.'
Write-Host '이 창을 닫거나 Ctrl+C를 누르면 종료됩니다.'
& $exe @appArguments
exit $LASTEXITCODE
