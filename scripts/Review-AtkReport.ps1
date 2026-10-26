[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportPath, [string]$CandidatePath, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
# Offline files only. Never loads the app, invokes a launcher, or opens a device.
$fullReportPath = [IO.Path]::GetFullPath($ReportPath)
if ((Get-Item -LiteralPath $fullReportPath).Length -gt 256KB) { throw 'ATK report exceeds 256 KiB.' }
if ($OutputPath -and [IO.Path]::GetFullPath($OutputPath).Equals($fullReportPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Review output must not replace the source report.' }
$source = [IO.File]::ReadAllBytes($fullReportPath)
if ($source.Length -gt 256KB) { throw 'ATK report exceeds 256 KiB.' }
$report = [Text.Encoding]::UTF8.GetString($source).TrimStart([char]0xFEFF) | ConvertFrom-Json -Depth 30
$state = $report.Result
function IsCount($value) { return ($value -is [long] -or $value -is [int]) -and $value -ge 0 -and $value -le 1000000 }
if ($report.BuildId -isnot [string] -or $report.BuildId -cnotmatch '^Phase[23]-[A-Za-z0-9-]{1,80}$' -or !$state) { throw 'Invalid ATK report header.' }
foreach ($field in @('Busy','Quarantined')) { if ($state.$field -isnot [bool]) { throw "Invalid boolean: $field" } }
foreach ($field in @('CandidateCount','InspectionErrors','ReadBatches','RequestsStarted','RequestsTerminal','CleanupErrors')) {
    if (!(IsCount $state.$field)) { throw "Invalid counter: $field" }
}
$observations = @($state.Observations)
if ($observations.Count -gt 3) { throw 'Expected at most one three-command observation batch.' }
$reasons = [Collections.Generic.List[string]]::new()
$binary = 'NotChecked'
if ($CandidatePath) {
    . (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
    $candidate = (Resolve-Path -LiteralPath $CandidatePath).Path
    $manifest = Assert-CandidateTree $candidate
    $names = @('PeripheralBatteryTray.exe','PeripheralBatteryTray.dll','PeripheralBatteryTray.Core.dll','PeripheralBatteryTray.Windows.dll')
    $assemblies = @($report.Assemblies)
    $bound = $manifest.BuildId -ceq $report.BuildId -and $assemblies.Count -eq 4 -and
        @($assemblies.File | Select-Object -Unique).Count -eq 4 -and !(Compare-Object $names @($assemblies.File))
    if ($bound) { foreach ($assembly in $assemblies) {
        if ($assembly.Sha256 -isnot [string] -or $assembly.Sha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or
            (Get-FileHash -LiteralPath (Join-Path $candidate ('app/' + $assembly.File))).Hash -ine $assembly.Sha256) { $bound = $false }
    } }
    $binary = if ($bound) { 'HashMatched' } else { 'Mismatch' }
}
if ($binary -ne 'HashMatched') { $reasons.Add('BinaryBindingNotConfirmed') }
if ($state.ReadBatches -eq 0) { $reasons.Add('NoDeviceReadRecorded') }
if ($state.Busy -or $state.Quarantined -or $state.CleanupErrors -ne 0 -or
    $state.RequestsStarted -ne $state.RequestsTerminal -or $report.Shutdown.FullyStopped -isnot [bool] -or
    !$report.Shutdown.FullyStopped -or $report.Shutdown.UnresolvedOperations -ne 0 -or $report.Shutdown.CleanupErrors -ne 0) {
    $reasons.Add('RunDidNotFinishCleanly')
}
if ($state.CandidateCount -ne 1 -or $state.InspectionErrors -ne 0) { $reasons.Add('ExactInterfaceNotConfirmed') }
$sequence = @('WirelessOnline','MouseIdentity','Battery')
$complete = $state.Status -ceq 'ObservationOnly' -and $observations.Count -eq 3 -and $state.ReadBatches -ge 1
if ($complete) {
    for ($i=0; $i -lt 3; $i++) {
        $o = $observations[$i]
        $required = @(1,2,2)[$i]
        if ($o.Command -cne $sequence[$i] -or !(IsCount $o.RawStatus) -or $o.RawStatus -gt 255 -or
            !(IsCount $o.DeclaredDataLength) -or $o.DeclaredDataLength -lt $required -or $o.DeclaredDataLength -gt 10 -or
            $o.MatchesRequestChecksumConvention -isnot [bool]) { $complete = $false }
    }
    if ($observations[0].OnlineCandidate -isnot [bool] -or !$observations[0].OnlineCandidate -or
        $observations[1].DuckbillUltraCandidate -isnot [bool] -or !$observations[1].DuckbillUltraCandidate -or
        !(IsCount $observations[2].PercentCandidate) -or $observations[2].PercentCandidate -gt 100 -or
        $state.RequestsStarted -lt 3) { $complete = $false }
}
if (!$complete) { $reasons.Add('CompleteTargetObservationMissing') }
if ($null -ne $state.AcceptedPercent -or $report.HardwareValidated -isnot [bool] -or $report.HardwareValidated) {
    $reasons.Add('ReportMakesUnverifiedCurrentValueClaim')
}
# A matching arithmetic convention cannot establish the meaning of a real response's status/checksum.
$reasons.Add('ResponseStatusAndChecksumSemanticsUnverified')
$reasons.Add('DeviceCompatibilityAndCoexistenceUnverified')
$review = [ordered]@{
    SchemaVersion = 1; BuildId = $report.BuildId; SourceReportSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source))
    BinaryBinding = $binary; CompleteCandidateObservation = $complete
    CandidatePercent = $(if ($complete) { $observations[2].PercentCandidate } else { $null })
    AcceptedPercent = $null; AutomaticHidEligible = $false; HardwareValidated = $false
    PendingReasons = $reasons.ToArray()
    Scope = 'Offline consistency review of self-reported data. Hash matching is not report authenticity or hardware validation. No devices or app execution. No automatic protocol approval.'
}
if ($OutputPath) { $review | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8 }
$review | ConvertTo-Json -Depth 5
