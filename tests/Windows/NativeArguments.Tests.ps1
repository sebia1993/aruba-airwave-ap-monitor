param([string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$ErrorActionPreference = 'Stop'
. (Join-Path $RepositoryRoot 'scripts/windows/Monitor-Common.ps1')
Assert-MonitorWindows
$root = Join-Path ([IO.Path]::GetTempPath()) ('AirWave 인수 확인 ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $exe = Join-Path $root 'Argument Echo.exe'
    Add-Type -TypeDefinition @'
using System;
using System.Text;
public static class AirWaveArgumentEcho {
    public static void Main(string[] args) {
        foreach (var arg in args) Console.WriteLine("arg:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(arg)));
    }
}
'@ -OutputAssembly $exe -OutputType ConsoleApplication
    $expected = @('', '한글 공백', 'C:\데이터 폴더\', 'a"b', 'a\"b', '--mode', 'demo', '--service-name', 'AirWave-CI_1')
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.Arguments = ($expected | ForEach-Object { ConvertTo-MonitorNativeArgument $_ }) -join ' '
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $actual = New-Object 'Collections.Generic.List[string]'
        while ($null -ne ($line = $process.StandardOutput.ReadLine())) {
            if (-not $line.StartsWith('arg:')) { throw 'Unexpected native output.' }
            $actual.Add([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line.Substring(4))))
        }
        if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'Native argument echo failed.' }
        if ($actual.Count -ne $expected.Count) { throw 'Native argument count differs.' }
        for ($i = 0; $i -lt $expected.Count; $i++) {
            if ($actual[$i] -cne $expected[$i]) { throw ('Native argument mismatch at index ' + $i) }
        }
    } finally { if (-not $process.HasExited) { $process.Kill() }; $process.Dispose() }
    Write-Host ('PASS: ' + $expected.Count + ' native Windows argument values round-trip without corruption.')
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
