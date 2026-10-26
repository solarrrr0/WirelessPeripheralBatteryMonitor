[CmdletBinding()]
param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if (-not $ReportPath) {
    $ReportPath = Join-Path $projectRoot 'artifacts\verification\P2.2-C\core-tests.local.json'
}
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
$testProject = Join-Path $projectRoot 'tests\PeripheralBatteryTray.Tests\PeripheralBatteryTray.Tests.csproj'
& $dotnet run --project $testProject -c Release --no-launch-profile -- --report $ReportPath
if ($LASTEXITCODE -ne 0) { throw "Core checks failed (exit $LASTEXITCODE). Report: $ReportPath" }
$result = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$expected = @('C01','C02','C03','C04','C05','C06','C07','C08','C09','C10','C11','C25','A01','A02','A03')
$expected += @('C12','C13','C14','C15','C16','C17','C18','C19','C20','C21','C22','C23','C24','C26','C27','C28','C29')
$expected += @('LB01','LB02','LB03')
$actual = @($result.Checks | ForEach-Object { $_.Id })
if (-not $result.Passed -or @($result.Checks | Where-Object { -not $_.Passed }).Count -gt 0 -or
    $actual.Count -ne $expected.Count -or @($actual | Select-Object -Unique).Count -ne $actual.Count -or
    (Compare-Object -ReferenceObject $expected -DifferenceObject $actual)) {
    throw 'Core report failed or did not include the complete A/B case list.'
}
$testOutput = Join-Path $projectRoot 'tests\PeripheralBatteryTray.Tests\bin\Release\net10.0'
foreach ($assembly in $result.Assemblies) {
    if ($assembly.File -notin @('PeripheralBatteryTray.Tests.dll','PeripheralBatteryTray.Core.dll')) {
        throw 'Unexpected assembly name in report.'
    }
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $testOutput $assembly.File) -Algorithm SHA256).Hash
    if ($actualHash -ne $assembly.Sha256) { throw "Assembly hash mismatch: $($assembly.File)" }
}
$assemblyNames = @($result.Assemblies | ForEach-Object { $_.File })
if ($assemblyNames.Count -ne 2 -or @($assemblyNames | Select-Object -Unique).Count -ne 2 -or
    (Compare-Object @('PeripheralBatteryTray.Tests.dll','PeripheralBatteryTray.Core.dll') $assemblyNames)) {
    throw 'Expected unique hashes for both test and Core assemblies.'
}
Write-Output "Core: $($actual.Count) cases, $($result.AssertionCount) assertions passed. No hardware queries."
Write-Output "Report: $ReportPath"
