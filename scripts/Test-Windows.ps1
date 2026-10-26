[CmdletBinding()]
param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')
if (-not $ReportPath) { $ReportPath = Join-Path $projectRoot 'artifacts/verification/P2.3/windows-tests.local.json' }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
& $dotnet run --project (Join-Path $projectRoot 'tests/PeripheralBatteryTray.Windows.Tests') -c Release -- --report $ReportPath
if ($LASTEXITCODE -ne 0) { throw 'Windows fake/isolated tests failed.' }
$result = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$expected = @(1..23 | ForEach-Object { 'W{0:00}' -f $_ })
$expected += 'F01'
$expected += @(1..23 | ForEach-Object { 'G{0:00}' -f $_ })
$expected += 'G24'
$expected += @('W24','P06','P07','P08','P09','P10','P11','P12','P13')
$expected += 'J02'
$expected += @('AT01','AT02','AT03','AT04')
$actual = @($result.Checks | ForEach-Object { $_.Id })
if (-not $result.Passed -or @($result.Checks | Where-Object { -not $_.Passed }).Count -gt 0 -or
    $actual.Count -ne $expected.Count -or @($actual | Select-Object -Unique).Count -ne $actual.Count -or
    (Compare-Object $expected $actual)) { throw 'Windows test case manifest mismatch.' }
$assemblyNames = @('PeripheralBatteryTray.Windows.Tests.dll','PeripheralBatteryTray.Windows.dll','PeripheralBatteryTray.Core.dll')
if (@($result.Assemblies).Count -ne 3 -or (Compare-Object $assemblyNames @($result.Assemblies.File))) { throw 'Assembly manifest mismatch.' }
foreach ($assembly in $result.Assemblies) {
    $path = Join-Path $projectRoot ('tests/PeripheralBatteryTray.Windows.Tests/bin/Release/net10.0-windows10.0.19041.0/' + $assembly.File)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $assembly.Sha256) { throw 'Assembly hash mismatch.' }
}
Write-Output "Windows fake/isolated tests: $($actual.Count) cases, $($result.AssertionCount) assertions. No real device queries."
