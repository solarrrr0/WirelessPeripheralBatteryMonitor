[CmdletBinding()]
param([string]$Destination = '', [switch]$Zip, [ValidateSet('Phase2','Phase5')][string]$Stage = 'Phase5')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
$root = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')
$buildId = $Stage + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$allowedRoot = Join-Path $root 'artifacts/Phase2-candidates'
if (!$Destination) { $Destination = Join-Path $allowedRoot $buildId }
$Destination = Assert-ReleasePath $Destination $allowedRoot
foreach ($path in @($Destination, ($Destination + '.zip'), ($Destination + '.zip.sha256'))) {
    if (Test-Path -LiteralPath $path) { throw 'Candidate directory or ZIP already exists; refusing overwrite.' }
}
$verification = Assert-ReleasePath (Join-Path $root ('artifacts/verification/' + $buildId)) (Join-Path $root 'artifacts/verification')
if (Test-Path -LiteralPath $verification) { throw 'Verification directory already exists.' }
$beforeInputs = @(Get-ReleaseInputFiles $root)
[void][IO.Directory]::CreateDirectory($verification)
$publish = Join-Path $verification 'publish'
& $dotnet publish (Join-Path $root 'src/PeripheralBatteryTray.App') -c Release -r win-x64 --self-contained false "-p:BuildId=$buildId" -o $publish --nologo -m:1 -nr:false | Out-Host
if ($LASTEXITCODE) { throw 'Publish failed; partial output retained locally, no candidate created.' }
$runtime = @(Get-ReleaseAllowlist | Where-Object { $_.StartsWith('app/') } | ForEach-Object { $_.Substring(4) })
$publishFiles = @(Get-ReleaseFiles $publish)
if (Compare-Object ($runtime + @('PeripheralBatteryTray.pdb','PeripheralBatteryTray.Core.pdb','PeripheralBatteryTray.Windows.pdb')) @($publishFiles.Name)) { throw 'Unexpected publish file set.' }
[void][IO.Directory]::CreateDirectory((Join-Path $Destination 'app'))
foreach ($name in $runtime) { Copy-Item -LiteralPath (Join-Path $publish $name) -Destination (Join-Path $Destination ('app/' + $name)) }
$summaries = @(& (Join-Path $PSScriptRoot 'Test-Phase2Candidate.ps1') -AppDirectory (Join-Path $Destination 'app') -BuildId $buildId -VerificationDirectory $verification)
& (Join-Path $PSScriptRoot 'Test-ReleaseTools.ps1') -ReportPath (Join-Path $verification 'release-tests.local.json') -BuildId $buildId | Out-Host
$release = Get-Content -Raw -LiteralPath (Join-Path $verification 'release-tests.local.json') | ConvertFrom-Json
if ($release.Passed -cne $true -or $release.BuildId -cne $buildId) { throw 'Release contract checks failed.' }
$summaries += [pscustomobject]@{ Suite = 'Release'; Cases = @($release.Checks).Count; Assertions = $release.AssertionCount; Status = 'Passed'; ReportSha256 = (Get-FileHash -LiteralPath (Join-Path $verification 'release-tests.local.json')).Hash }
foreach ($dir in @('docs','licenses')) { [void][IO.Directory]::CreateDirectory((Join-Path $Destination $dir)) }
Copy-Item -LiteralPath (Join-Path $root 'docs/release/README_中文.md') -Destination (Join-Path $Destination 'README_中文.md')
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $Destination 'THIRD_PARTY_NOTICES.md')
foreach ($name in @('CsWinRT-LICENSE.txt','Windows-SDK-license.rtf')) { Copy-Item -LiteralPath (Join-Path $root ('licenses/' + $name)) -Destination (Join-Path $Destination ('licenses/' + $name)) }
foreach ($name in @('DEVICE_COMPATIBILITY.md','RESOURCE_BENCHMARK.md','PHASE2_VALIDATION.md','TROUBLESHOOTING.md')) {
    Copy-Item -LiteralPath (Join-Path $root ('docs/release/' + $name)) -Destination (Join-Path $Destination ('docs/' + $name))
}
foreach ($name in @('Start.cmd','Check-ATK.cmd','Check-Bluetooth.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('launchers/' + $name)) -Destination (Join-Path $Destination $name)
}
Copy-Item -LiteralPath (Join-Path $root 'docs/ATK_PROTOCOL_RESEARCH.md') -Destination (Join-Path $Destination 'docs/ATK_PROTOCOL_RESEARCH.md')
Copy-Item -LiteralPath (Join-Path $root 'docs/PHASE4_DEVICE_SETTINGS.md') -Destination (Join-Path $Destination 'docs/PHASE4_DEVICE_SETTINGS.md')
New-CandidateAcceptance $buildId $summaries | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Destination 'docs/VALIDATION.json') -Encoding utf8
$afterInputs = @(Get-ReleaseInputFiles $root)
if (Compare-Object @($beforeInputs | ForEach-Object { $_.Path + ':' + $_.Sha256 }) @($afterInputs | ForEach-Object { $_.Path + ':' + $_.Sha256 })) { throw 'Build inputs changed during validation; candidate is incomplete.' }
$files = @(Get-ReleaseFiles $Destination | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ Path = $_.FullName.Substring($Destination.Length + 1).Replace('\','/'); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{ SchemaVersion = 1; BuildId = $buildId; Stage = $Stage; Capability = 'WindowsPropertyAndDeviceSettings'; Tfm = 'net10.0-windows10.0.19041.0';
    Rid = 'win-x64'; SelfContained = $false; PublishMode = 'framework-dependent multi-file'; BuildSdk = (& $dotnet --version | Select-Object -First 1).Trim();
    ConfigSchema = 4; PdbIncluded = $false; SourceInputs = $afterInputs; Files = $files }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Destination 'release-manifest.json') -Encoding utf8
$null = Assert-CandidateTree $Destination
if ($Zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Destination, $Destination + '.zip')
    (Get-FileHash -LiteralPath ($Destination + '.zip') -Algorithm SHA256).Hash | Set-Content -LiteralPath ($Destination + '.zip.sha256') -Encoding ascii
    & (Join-Path $PSScriptRoot 'Verify-Phase2Candidate.ps1') -CandidatePath $Destination -ZipPath ($Destination + '.zip')
} else { & (Join-Path $PSScriptRoot 'Verify-Phase2Candidate.ps1') -CandidatePath $Destination }
Write-Output "Candidate: $Destination"
Write-Output "Local evidence: $verification"
