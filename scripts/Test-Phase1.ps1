[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$app = Join-Path $projectRoot 'artifacts\Phase1-win-x64\PeripheralBatteryTray.exe'
$report = Join-Path $projectRoot 'artifacts\verification\phase1-tests.local.json'
if (-not (Test-Path -LiteralPath $app)) { throw '先运行 scripts\publish-win-x64.ps1' }
$process = Start-Process -FilePath $app -ArgumentList @('--self-test', '--report', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) { throw "Self-test timed out; inspect process $($process.Id)." }
if ($process.ExitCode -eq 2) { throw '已有应用实例，测试未执行。请先退出已有实例。' }
if ($process.ExitCode -ne 0) { throw "Self-test failed. Report: $report" }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.Passed) { throw 'Self-test reported failures' }
$result.Checks | Format-Table Name,Passed -AutoSize
