[CmdletBinding()]
param([Parameter(Mandatory)][string]$CandidatePath,
    [ValidateRange(70,600)][int]$DurationSeconds = 95,
    [ValidatePattern('^10\.0\.\d+$')][string]$RuntimeVersion = '10.0.12')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
. (Join-Path $PSScriptRoot 'ResourceTools.ps1')
$root = Split-Path -Parent $PSScriptRoot
$candidate = Assert-ReleasePath $CandidatePath (Join-Path $root 'artifacts')
$manifest = Assert-CandidateTree $candidate
$out = Assert-ReleasePath (Join-Path $root ('artifacts/verification/publish-comparison-' + [Guid]::NewGuid().ToString('N'))) (Join-Path $root 'artifacts/verification')
[void][IO.Directory]::CreateDirectory($out)
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')
$sc = Join-Path $out 'self-contained'
$pin = Join-Path $out 'runtime-pin.targets'
# A global RuntimeFrameworkVersion also overrides the Windows SDK framework reference.
# Pin only the two runtime references; keep Windows SDK targeting-pack versions intact.
@"
<Project>
  <Target Name="PinComparisonRuntime" BeforeTargets="ProcessFrameworkReferences">
    <ItemGroup>
      <FrameworkReference Condition="'%(FrameworkReference.Identity)' == 'Microsoft.NETCore.App' Or '%(FrameworkReference.Identity)' == 'Microsoft.WindowsDesktop.App.WindowsForms'">
        <RuntimeFrameworkVersion>$RuntimeVersion</RuntimeFrameworkVersion>
      </FrameworkReference>
      <KnownFrameworkReference Condition="'%(KnownFrameworkReference.Identity)' == 'Microsoft.WindowsDesktop.App' Or '%(KnownFrameworkReference.Identity)' == 'Microsoft.WindowsDesktop.App.WindowsForms'">
        <LatestRuntimeFrameworkVersion>$RuntimeVersion</LatestRuntimeFrameworkVersion>
        <DefaultRuntimeFrameworkVersion>$RuntimeVersion</DefaultRuntimeFrameworkVersion>
      </KnownFrameworkReference>
    </ItemGroup>
  </Target>
</Project>
"@ | Set-Content -LiteralPath $pin -Encoding utf8
& $dotnet publish (Join-Path $root 'src/PeripheralBatteryTray.App') -c Release -r win-x64 --self-contained true "-p:CustomAfterMicrosoftCommonTargets=$pin" "-p:BuildId=$($manifest.BuildId)" -o $sc --nologo | Out-Host
if ($LASTEXITCODE) { throw 'Self-contained comparison build failed; local output retained.' }
$runtimeFiles = @('System.Private.CoreLib.dll','System.Windows.Forms.dll' | ForEach-Object {
    $file = Join-Path $sc $_
    $version = (Get-Item -LiteralPath $file).VersionInfo.ProductVersion
    if ($version -notmatch ('^' + [regex]::Escape($RuntimeVersion) + '([-+]|$)')) { throw 'Actual runtime file version differs from the comparison pin.' }
    [pscustomobject]@{ File=$_; ProductVersion=$version; Sha256=(Get-FileHash -LiteralPath $file).Hash }
})
foreach ($name in @('PeripheralBatteryTray.dll','PeripheralBatteryTray.Core.dll','PeripheralBatteryTray.Windows.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $sc $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $candidate ('app/'+$name))).Hash) { throw 'Comparison product bytes differ from the candidate.' }
}
# This directory is an experiment, not a second release. Exact file metadata is kept locally.
$scFiles = @(Get-ReleaseFiles $sc | Where-Object Extension -ne '.pdb')
$fdd = Join-Path $candidate 'app'
$fddFiles = @(Get-ReleaseFiles $fdd)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $out 'self-contained-experiment.zip'
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try { foreach ($file in $scFiles) {
    $relative = $file.FullName.Substring($sc.Length + 1).Replace('\','/')
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative)
} } finally { $archive.Dispose() }
$results = @()
foreach ($mode in @('framework-dependent','self-contained')) {
    $dir = if ($mode -eq 'framework-dependent') { $fdd } else { $sc }
    $app = Join-Path $dir 'PeripheralBatteryTray.exe'
    $appReport = Join-Path $out ($mode + '.application.local.json')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $app -ArgumentList @('--mock','--measurement-report',('"'+$appReport+'"')) -WindowStyle Hidden -PassThru
    $ticks = $p.StartTime.ToUniversalTime().Ticks
    $samples = [Collections.Generic.List[object]]::new(); $graceful = $false; $errorText = $null
    try {
        $ready = $p.WaitForInputIdle(10000); $startup = $clock.Elapsed.TotalSeconds
        if (!$ready -or $p.HasExited) { throw 'Comparison did not own a live UI process; close the existing application before comparing.' }
        while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
            $p.Refresh(); if ($p.HasExited) { throw 'Comparison process exited early.' }
            $samples.Add([pscustomobject]@{ Pid=$p.Id; StartTicks=$ticks; ElapsedSeconds=$clock.Elapsed.TotalSeconds;
                CpuSeconds=$p.TotalProcessorTime.TotalSeconds; WorkingSetBytes=$p.WorkingSet64; PrivateBytes=$p.PrivateMemorySize64 })
            Start-Sleep -Seconds 2
        }
    } catch { $errorText = $_.Exception.Message }
    finally {
        $p.Refresh()
        if (!$p.HasExited -and $p.StartTime.ToUniversalTime().Ticks -eq $ticks -and $p.Path.Equals($app,[StringComparison]::OrdinalIgnoreCase)) {
            $stop = Start-Process -FilePath $app -ArgumentList @('--exit','--expected-pid',[string]$p.Id,'--expected-start-ticks',[string]$ticks) -WindowStyle Hidden -PassThru
            if ($stop.WaitForExit(10000) -and $stop.ExitCode -eq 0) { $graceful = $p.WaitForExit(10000) }
        }
    }
    if (!$graceful -or $errorText -or !(Test-Path -LiteralPath $appReport)) { throw "Comparison incomplete: $errorText" }
    $appEvidence = Get-Content -Raw -LiteralPath $appReport | ConvertFrom-Json
    $bound = $appEvidence.BuildId -ceq $manifest.BuildId -and $appEvidence.Mode -eq 'Mock' -and $appEvidence.Shutdown.FullyStopped
    foreach ($a in $appEvidence.Assemblies) {
        if ((Get-FileHash -LiteralPath (Join-Path $dir $a.File)).Hash -ne $a.Sha256) { $bound = $false }
    }
    if (!$bound -or @($appEvidence.Assemblies).Count -ne 4 -or $appEvidence.Runtime -ne $RuntimeVersion) { throw 'Comparison binary/runtime evidence mismatch.' }
    $summary = Get-ResourceSummary $samples.ToArray() $p.Id $ticks 60 ([Environment]::ProcessorCount)
    $results += [pscustomobject]@{ Mode=$mode; Runtime=$appEvidence.Runtime; BuildId=$manifest.BuildId;
        InputIdleUpperBoundSeconds=$startup; Summary=$summary; Samples=$samples.ToArray();
        GracefulExit=$graceful; ExitCode=$p.ExitCode; ApplicationReportSha256=(Get-FileHash -LiteralPath $appReport).Hash }
}
$regression = Join-Path $out 'self-contained-regression.local.json'
$p = Start-Process -FilePath (Join-Path $sc 'PeripheralBatteryTray.exe') -ArgumentList @('--self-test','--report',('"'+$regression+'"')) -WindowStyle Hidden -PassThru
if (!$p.WaitForExit(45000)) { throw 'Self-contained Mock regression timed out.' }
$r = Get-Content -Raw -LiteralPath $regression | ConvertFrom-Json
Assert-ReleaseReport $r $p.ExitCode $manifest.BuildId Regression $sc $sc
$fileRows = @($scFiles | ForEach-Object { [pscustomobject]@{ File=$_.FullName.Substring($sc.Length+1).Replace('\','/'); Bytes=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash } })
$report = [pscustomobject]@{ BuildId=$manifest.BuildId; Passed=$true; TimestampUtc=[DateTimeOffset]::UtcNow.ToString('o');
    RuntimeVersion=$RuntimeVersion; FrameworkDependentBytes=($fddFiles | Measure-Object Length -Sum).Sum;
    SelfContainedBytes=($scFiles | Measure-Object Length -Sum).Sum; SelfContainedFiles=$scFiles.Count;
    ExperimentalZipBytes=(Get-Item -LiteralPath $zip).Length; ExperimentalZipIncludesPdb=$false;
    Measurements=$results; SelfContainedMockRegressionCases=23; SelfContainedPhase2Case='C30'; SelfContainedRegressionRuntime=$r.Environment.Runtime;
    SelfContainedFilesManifest=$fileRows; ActualRuntimeFiles=$runtimeFiles;
    Scope='Same source/BuildId and pinned runtime; warmed short Mock scenarios, sequential order, root process only. No hardware, controlled cold-start, 900-second resource gate or clean-machine runtime-absence acceptance. Experimental runtime files not distributed.' }
$report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $out 'comparison.local.json') -Encoding utf8
Write-Output "Comparison evidence: $out"
