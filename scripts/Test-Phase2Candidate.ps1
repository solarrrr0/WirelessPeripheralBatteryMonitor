[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppDirectory, [Parameter(Mandatory)][string]$BuildId, [Parameter(Mandatory)][string]$VerificationDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
$root = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')
$AppDirectory = Assert-ReleasePath $AppDirectory (Join-Path $root 'artifacts')
$VerificationDirectory = Assert-ReleasePath $VerificationDirectory (Join-Path $root 'artifacts/verification')
foreach ($suite in @('Core','Windows','App')) {
    $project = switch ($suite) { Core { 'PeripheralBatteryTray.Tests' } Windows { 'PeripheralBatteryTray.Windows.Tests' } App { 'PeripheralBatteryTray.App.Tests' } }
    $out = Join-Path $VerificationDirectory ('test-binaries/' + $suite)
    if (Test-Path -LiteralPath $out) { throw 'Test output already exists; refusing stale evidence.' }
    & $dotnet publish (Join-Path $root ('tests/' + $project)) -c Release -r win-x64 --self-contained false "-p:BuildId=$BuildId" -o $out --nologo | Out-Host
    if ($LASTEXITCODE) { throw 'Test build failed.' }
    $reportPath = Join-Path $VerificationDirectory ($suite.ToLowerInvariant() + '-tests.local.json')
    & $dotnet (Join-Path $out ($project + '.dll')) --report $reportPath | Out-Host
    $code = $LASTEXITCODE
    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    Assert-ReleaseReport $report $code $BuildId $suite $out $AppDirectory
    [pscustomobject]@{ Suite = $suite; Cases = @($report.Checks).Count; Assertions = $report.AssertionCount; Status = 'Passed'; ReportSha256 = (Get-FileHash -LiteralPath $reportPath).Hash }
}
$exe = Join-Path $AppDirectory 'PeripheralBatteryTray.exe'
$reportPath = Join-Path $VerificationDirectory 'app-regression.local.json'
if (Test-Path -LiteralPath $reportPath) { throw 'Regression report already exists.' }
$process = Start-Process -FilePath $exe -ArgumentList @('--self-test','--report',('"' + $reportPath + '"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(45000)) { throw 'Candidate self-test timed out; no global exit or process-name termination is attempted.' }
$report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
Assert-ReleaseReport $report $process.ExitCode $BuildId Regression $AppDirectory $AppDirectory
[pscustomobject]@{ Suite = 'Regression'; Cases = 23; Phase2Case = 'C30'; Status = 'Passed'; ReportSha256 = (Get-FileHash -LiteralPath $reportPath).Hash }
