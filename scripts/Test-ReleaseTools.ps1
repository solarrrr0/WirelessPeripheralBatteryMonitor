[CmdletBinding()]
param([string]$ReportPath, [string]$BuildId = 'Phase2-contract-tests')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
. (Join-Path $PSScriptRoot 'ResourceTools.ps1')
$root = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Assert-ReleasePath (Join-Path $root ('artifacts/verification/release-contract-' + [Guid]::NewGuid().ToString('N'))) (Join-Path $root 'artifacts/verification')
[void][IO.Directory]::CreateDirectory($fixtureRoot)
if (!$ReportPath) { $ReportPath = Join-Path $fixtureRoot 'release-tests.local.json' }
$script:assertions = 0; $checks = [Collections.Generic.List[object]]::new()
function Check([bool]$value) { $script:assertions++; if (!$value) { throw "Assertion $script:assertions failed." } }
function Reject([scriptblock]$action) { $rejected = $false; try { & $action | Out-Null } catch { $rejected = $true }; Check $rejected }
function Case([string]$id, [scriptblock]$action) {
    $before = $script:assertions
    try { & $action; $checks.Add([pscustomobject]@{ Id=$id; Passed=$true; Assertions=$script:assertions-$before }); Write-Host "PASS $id" }
    catch { $checks.Add([pscustomobject]@{ Id=$id; Passed=$false; Error=$_.Exception.Message }); Write-Host "FAIL $id $($_.Exception.Message)" }
}
function FixtureSummaries {
    foreach ($suite in @('Core','Windows','App','Regression','Release')) {
        [pscustomobject]@{ Suite=$suite; Cases=@(Get-ReleaseCases $suite).Count; Status='Passed'; ReportSha256=('A'*64) }
    }
}
function Fixture {
    $dir = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($dir)
    foreach ($name in Get-ReleaseAllowlist) {
        $path = Join-Path $dir $name; [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)); [IO.File]::WriteAllText($path, 'Fixture, never executed.')
    }
    New-CandidateAcceptance 'Phase2-fixture' @(FixtureSummaries) | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dir 'docs/VALIDATION.json') -Encoding utf8
    $manifest = [pscustomobject]@{ SchemaVersion=1; BuildId='Phase2-fixture'; Stage='Phase2'; Capability='WindowsPropertyAndDeviceSettings';
        Tfm='net10.0-windows10.0.19041.0'; Rid='win-x64'; SelfContained=$false; ConfigSchema=4; PdbIncluded=$false; BuildSdk='10.0.402';
        SourceInputs=@([pscustomobject]@{ Path='src/Fixture.cs'; Sha256=('A'*64) }); Files=@(Get-ReleaseFiles $dir | ForEach-Object {
            [pscustomobject]@{ Path=$_.FullName.Substring($dir.Length+1).Replace('\','/'); Length=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash }
        }) }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dir 'release-manifest.json') -Encoding utf8
    return $dir
}
function SaveManifest($dir,$m) { $m | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dir 'release-manifest.json') -Encoding utf8 }
Case P14 {
    $allowed = Join-Path $fixtureRoot 'allowed'; [void][IO.Directory]::CreateDirectory($allowed)
    Reject { Assert-ReleasePath ($allowed + '-sibling/file') $allowed }
    Reject { Assert-ReleasePath (Join-Path $allowed '../escape') $allowed }
    Reject { & (Join-Path $PSScriptRoot 'Publish-Phase2Candidate.ps1') -Destination $fixtureRoot }
    $existing = Join-Path $root ('artifacts/Phase2-candidates/contract-existing-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($existing); [IO.File]::WriteAllText((Join-Path $existing 'old.dll'),'keep')
    Reject { & (Join-Path $PSScriptRoot 'Publish-Phase2Candidate.ps1') -Destination $existing }
    Check ((Get-Content -Raw -LiteralPath (Join-Path $existing 'old.dll')) -eq 'keep')
    Reject { & (Join-Path $PSScriptRoot 'publish-win-x64.ps1') }
    $zipOnly = $existing + '-zip-only'; [IO.File]::WriteAllText(($zipOnly+'.zip'),'keep zip')
    Reject { & (Join-Path $PSScriptRoot 'Publish-Phase2Candidate.ps1') -Destination $zipOnly }
    Check (!(Test-Path -LiteralPath $zipOnly) -and (Get-Content -Raw -LiteralPath ($zipOnly+'.zip')) -eq 'keep zip')
    $target = Join-Path $fixtureRoot 'junction-target'; [void][IO.Directory]::CreateDirectory($target)
    $junction = Join-Path $allowed 'junction'; New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
    Reject { Assert-ReleasePath (Join-Path $junction 'child') $allowed }
    Reject { Get-ReleaseFiles $allowed }
    # Remove only this fixture junction itself, never recursively or its target.
    [IO.Directory]::Delete($junction)
    $candidate = Fixture
    [IO.File]::WriteAllText((Join-Path $candidate 'private.local.json'),'private')
    Reject { Assert-CandidateTree $candidate }
    $inputs = @(Get-ReleaseInputFiles $root)
    Check ($inputs.Count -gt 30 -and @($inputs.Path | Where-Object { $_ -match '(^|/)(bin|obj)/|\.local\.' }).Count -eq 0)
}
Case P15 {
    $dir = Fixture; Check ($null -ne (Assert-CandidateTree $dir))
    $phase5 = Fixture
    $m5 = Get-Content -Raw -LiteralPath (Join-Path $phase5 'release-manifest.json') | ConvertFrom-Json
    $m5.BuildId = 'Phase5-fixture'; $m5.Stage = 'Phase5'
    $v5 = Get-Content -Raw -LiteralPath (Join-Path $phase5 'docs/VALIDATION.json') | ConvertFrom-Json
    $v5.BuildId = 'Phase5-fixture'
    $v5 | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $phase5 'docs/VALIDATION.json') -Encoding utf8
    $entry5 = $m5.Files | Where-Object Path -eq 'docs/VALIDATION.json'
    $entry5.Length = (Get-Item -LiteralPath (Join-Path $phase5 $entry5.Path)).Length
    $entry5.Sha256 = (Get-FileHash -LiteralPath (Join-Path $phase5 $entry5.Path)).Hash
    SaveManifest $phase5 $m5
    Check ($null -ne (Assert-CandidateTree $phase5))
    $m5.Stage = 'Phase2'; SaveManifest $phase5 $m5; Reject { Assert-CandidateTree $phase5 }
    $m = Get-Content -Raw -LiteralPath (Join-Path $dir 'release-manifest.json') | ConvertFrom-Json
    $original = $m.Files[0].Path; $m.Files[0].Path = '../escape'; SaveManifest $dir $m; Reject { Assert-CandidateTree $dir }
    $m.Files[0].Path = $original; $m.Files += $m.Files[0]; SaveManifest $dir $m; Reject { Assert-CandidateTree $dir }
    $m.Files = @($m.Files | Select-Object -SkipLast 1); SaveManifest $dir $m
    [IO.File]::WriteAllText((Join-Path $dir $original),'corruption'); Reject { Assert-CandidateTree $dir }
    $dir = Fixture; $missing = Join-Path $dir 'app/WinRT.Runtime.dll'; Remove-Item -LiteralPath $missing; Reject { Assert-CandidateTree $dir }
    $dir = Fixture; $zip = $dir + '.zip'; Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($dir,$zip)
    (Get-FileHash -LiteralPath $zip).Hash | Set-Content -LiteralPath ($zip+'.sha256') -Encoding ascii
    $verified = & (Join-Path $PSScriptRoot 'Verify-Phase2Candidate.ps1') -CandidatePath $dir -ZipPath $zip
    Check ($verified.Passed -and $verified.ZipVerified)
    [IO.File]::WriteAllText(($zip+'.sha256'),('0'*64)); Reject { & (Join-Path $PSScriptRoot 'Verify-Phase2Candidate.ps1') -CandidatePath $dir -ZipPath $zip }
    $badZip = $dir + '-bad.zip'; $archive = [IO.Compression.ZipFile]::Open($badZip,[IO.Compression.ZipArchiveMode]::Create)
    try { $null = $archive.CreateEntry('../outside.txt') } finally { $archive.Dispose() }
    (Get-FileHash -LiteralPath $badZip).Hash | Set-Content -LiteralPath ($badZip+'.sha256') -Encoding ascii
    Reject { & (Join-Path $PSScriptRoot 'Verify-Phase2Candidate.ps1') -CandidatePath $dir -ZipPath $badZip }
    $duplicateZip = $dir + '-duplicate.zip'; $archive = [IO.Compression.ZipFile]::Open($duplicateZip,[IO.Compression.ZipArchiveMode]::Create)
    try { $null=$archive.CreateEntry('README_中文.md'); $null=$archive.CreateEntry('README_中文.md') } finally { $archive.Dispose() }
    (Get-FileHash -LiteralPath $duplicateZip).Hash | Set-Content -LiteralPath ($duplicateZip+'.sha256') -Encoding ascii
    Reject { & (Join-Path $PSScriptRoot 'Verify-Phase2Candidate.ps1') -CandidatePath $dir -ZipPath $duplicateZip }
}
Case P16 {
    $dir = Join-Path $fixtureRoot 'report-assemblies'; [void][IO.Directory]::CreateDirectory($dir)
    foreach ($name in @('PeripheralBatteryTray.Tests.dll','PeripheralBatteryTray.Core.dll')) { [IO.File]::WriteAllText((Join-Path $dir $name),'Never executed, hash contract fixture.') }
    $r = [pscustomobject]@{ Passed=$true; BuildId=$BuildId; Checks=@(Get-ReleaseCases Core | ForEach-Object { [pscustomobject]@{ Id=$_; Passed=$true } });
        Assemblies=@(Get-ChildItem -LiteralPath $dir -File | ForEach-Object { [pscustomobject]@{ File=$_.Name; Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash } }) }
    Assert-ReleaseReport $r 0 $BuildId Core $dir $dir
    Check ($r.Checks.Count -eq 35 -and $r.Assemblies.Count -eq 2)
    Reject { Assert-ReleaseReport $r 2 $BuildId Core $dir $dir }
    Reject { Assert-ReleaseReport $r 0 'Phase2-other' Core $dir $dir }
    $r.Passed=1; Reject { Assert-ReleaseReport $r 0 $BuildId Core $dir $dir }; $r.Passed=$true
    $r.Checks = @($r.Checks | Select-Object -Skip 1); Reject { Assert-ReleaseReport $r 0 $BuildId Core $dir $dir }
    $r.Checks = @(Get-ReleaseCases Core | ForEach-Object { [pscustomobject]@{ Id=$_; Passed=$true } }); $r.Checks[0].Passed=$false
    Reject { Assert-ReleaseReport $r 0 $BuildId Core $dir $dir }; $r.Checks[0].Passed=$true
    $r.Assemblies[0].Sha256='0'*64; Reject { Assert-ReleaseReport $r 0 $BuildId Core $dir $dir }
    # Matching test report hashes must also match the product, not just the test directory.
    $r.Assemblies[0].Sha256=(Get-FileHash -LiteralPath (Join-Path $dir $r.Assemblies[0].File)).Hash
    $product=Join-Path $fixtureRoot 'wrong-product'; [void][IO.Directory]::CreateDirectory($product)
    [IO.File]::WriteAllText((Join-Path $product 'PeripheralBatteryTray.Core.dll'),'different')
    Reject { Assert-ReleaseReport $r 0 $BuildId Core $dir $product }
}
Case P17 {
    $rows = @(
        @(1,10,0,0,100), @(1,10,1,0,200), @(1,10,4,1,800),
        @(2,20,0,100,10), @(2,20,1,100.2,10), @(2,30,2,1000,20), @(2,30,4,1000.4,20)
    ) | ForEach-Object { [pscustomobject]@{ Pid=$_[0]; StartTicks=$_[1]; ElapsedSeconds=$_[2]; CpuSeconds=$_[3]; WorkingSetBytes=$_[4]; PrivateBytes=$_[4]/2 } }
    $s = Get-ResourceSummary $rows 1 10 0 8
    Check ([Math]::Abs($s.AverageWorkingSetBytes-425) -lt 0.00001)
    Check ([Math]::Abs($s.CpuDeltaSeconds-1.6) -lt 0.00001 -and [Math]::Abs($s.CpuWholeMachinePercent-5) -lt 0.00001)
    Check ($s.Lifetimes.Count -eq 3 -and $s.StableSeconds -eq 4)
    $zero = @($rows | Where-Object Pid -eq 1 | ForEach-Object { [pscustomobject]@{ Pid=1; StartTicks=10; ElapsedSeconds=$_.ElapsedSeconds; CpuSeconds=0; WorkingSetBytes=$_.WorkingSetBytes; PrivateBytes=$_.PrivateBytes } })
    Check ((Get-ResourceSummary $zero 1 10 0 8).NoMeasurableCpuIncrement)
    $warm = Get-ResourceSummary $rows 1 10 1 8; Check ([Math]::Abs($warm.AverageWorkingSetBytes-1540/3) -lt 0.00001)
    Reject { Get-ResourceSummary @($rows[0]) 1 10 0 8 }
    Reject { Get-ResourceSummary $rows 1 10 0 0 }
    $window=[pscustomobject]@{ StableStartSeconds=65; StableSeconds=900 }
    $app=[pscustomobject]@{ Mode='Real'; BaselineSeconds=60; DurationSeconds=965; Scenario='OfflineObserved';
        Final=[pscustomobject]@{ PollIntervalMinutes=5 }; Shutdown=[pscustomobject]@{ FullyStopped=$true; CleanupErrors=0 } }
    Check (Test-ResourceWindow $window $app $true $true $false '' 0)
    $window.StableSeconds=899.9; Check (!(Test-ResourceWindow $window $app $true $true $false '' 0)); $window.StableSeconds=900
    Check (!(Test-ResourceWindow $window $app $true $true $true '' 0))
    $app.Final.PollIntervalMinutes=30; Check (!(Test-ResourceWindow $window $app $true $true $false '' 0)); $app.Final.PollIntervalMinutes=5
    $app.Shutdown.CleanupErrors=1; Check (!(Test-ResourceWindow $window $app $true $true $false '' 0))
}
Case P18 {
    $r = New-CandidateAcceptance $BuildId @(FixtureSummaries); Assert-CandidateAcceptance $r $BuildId
    $appSummary = $r.Automatic | Where-Object Suite -eq 'App'
    $appSummary.Cases=27; Reject { Assert-CandidateAcceptance $r $BuildId }
    $r.PSObject.Properties.Remove('AutomaticContractVersion'); Assert-CandidateAcceptance $r $BuildId
    Check ($appSummary.Cases -eq 27)
    $r = New-CandidateAcceptance $BuildId @(FixtureSummaries)
    Check (@($r.Gates | Where-Object Status -eq 'NotRun').Count -eq 29)
    $r.Gates[0].Status='Passed'; Reject { Assert-CandidateAcceptance $r $BuildId }; $r.Gates[0].Status='NotRun'
    $r.Capability='GattEnabled'; Reject { Assert-CandidateAcceptance $r $BuildId }; $r.Capability='WindowsPropertyAndDeviceSettings'
    $r.Overall='Complete'; Reject { Assert-CandidateAcceptance $r $BuildId }; $r.Overall='CandidateIncompleteHardware'
    $r.Gates=@($r.Gates | Select-Object -Skip 1); Reject { Assert-CandidateAcceptance $r $BuildId }
    $r = New-CandidateAcceptance $BuildId @(FixtureSummaries); $r.Automatic[0].Cases=1
    Reject { Assert-CandidateAcceptance $r $BuildId }
}
$expected = @(Get-ReleaseCases Release)
$passed = @($checks | Where-Object { !$_.Passed }).Count -eq 0 -and !(Compare-Object $expected @($checks.Id))
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath)))
[pscustomobject]@{ BuildId=$BuildId; Passed=$passed; TimestampUtc=[DateTimeOffset]::UtcNow.ToString('o'); AssertionCount=$script:assertions;
    Scope='Actual release/report/resource contracts with isolated filesystem and synthetic counters; fixture DLLs are never executed; no devices.';
    Checks=$checks.ToArray(); Scripts=@('ReleaseTools.ps1','ResourceTools.ps1','Publish-Phase2Candidate.ps1','Verify-Phase2Candidate.ps1','Test-ReleaseTools.ps1' | ForEach-Object {
        [pscustomobject]@{ File=$_; Sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_)).Hash }
    }) } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ReportPath -Encoding utf8
if (!$passed) { throw 'Release contract checks failed; see local report.' }
Write-Output "Release: 5 cases, $script:assertions assertions. Report: $ReportPath"
