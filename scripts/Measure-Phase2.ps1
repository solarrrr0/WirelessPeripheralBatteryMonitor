[CmdletBinding()]
param([Parameter(Mandatory)][string]$CandidatePath,
    [ValidateRange(10,86400)][int]$DurationSeconds = 970, [ValidateRange(1,30)][int]$SampleSeconds = 5, [switch]$Mock,
    [switch]$CapturePages)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
. (Join-Path $PSScriptRoot 'ResourceTools.ps1')
$root = Split-Path -Parent $PSScriptRoot
$candidate = Assert-ReleasePath $CandidatePath (Join-Path $root 'artifacts/Phase2-candidates')
$manifest = Assert-CandidateTree $candidate
$out = Assert-ReleasePath (Join-Path $root ('artifacts/verification/measurement-' + [Guid]::NewGuid().ToString('N'))) (Join-Path $root 'artifacts/verification')
[void][IO.Directory]::CreateDirectory($out)
$app = Join-Path $candidate 'app/PeripheralBatteryTray.exe'
$appReport = Join-Path $out 'application.local.json'
$arguments = @('--measurement-report',('"' + $appReport + '"'))
if ($Mock) { $arguments += '--mock' }
$clock = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $app -ArgumentList $arguments -WindowStyle Hidden -PassThru
$identityTicks = $process.StartTime.ToUniversalTime().Ticks
$samples = [Collections.Generic.List[object]]::new()
$known = @{}; $known[[string]$process.Id] = $identityTicks
$graceful = $false; $errorText = $null
try {
    $ready = $process.WaitForInputIdle(10000); $idleUpperBound = $clock.Elapsed.TotalSeconds
    if (!$ready -or $process.HasExited) { throw 'Process failed before UI idle; existing-instance handoff is not a valid sample.' }
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $process.Refresh(); if ($process.HasExited) { throw 'Measured application exited early.' }
        # Discover descendants only when the observed parent identity is still the same lifecycle.
        $all = @(Get-CimInstance Win32_Process)
        $changed = $true
        while ($changed) {
            $changed = $false
            foreach ($child in $all) {
                $parentKey = [string]$child.ParentProcessId; $childKey = [string]$child.ProcessId
                if (!$known.ContainsKey($parentKey) -or $known.ContainsKey($childKey)) { continue }
                try {
                    $parent = Get-Process -Id ([int]$child.ParentProcessId) -ErrorAction Stop
                    if ($parent.StartTime.ToUniversalTime().Ticks -ne $known[$parentKey]) { continue }
                    $p = Get-Process -Id ([int]$child.ProcessId) -ErrorAction Stop
                    $ticks = $p.StartTime.ToUniversalTime().Ticks
                    if ($ticks -lt $known[$parentKey]) { continue }
                    $known[$childKey] = $ticks; $changed = $true
                } catch { continue }
            }
        }
        foreach ($key in @($known.Keys)) {
            try {
                $p = Get-Process -Id ([int]$key) -ErrorAction Stop
                if ($p.StartTime.ToUniversalTime().Ticks -ne $known[$key]) { $known.Remove($key); continue }
                $samples.Add([pscustomobject]@{ Pid = $p.Id; StartTicks = $known[$key]; ElapsedSeconds = $clock.Elapsed.TotalSeconds;
                    CpuSeconds = $p.TotalProcessorTime.TotalSeconds; WorkingSetBytes = $p.WorkingSet64; PrivateBytes = $p.PrivateMemorySize64;
                    Handles = $p.HandleCount; Threads = $p.Threads.Count })
            } catch { $known.Remove($key) }
        }
        Start-Sleep -Seconds $SampleSeconds
    }
    if ($CapturePages) {
        # This diagnostic happens after all resource samples; its overhead is outside the sampled window.
        & (Join-Path $PSScriptRoot 'Read-ProcessMemory.ps1') -ProcessId $process.Id -StartTicks $identityTicks -ExpectedPath $app -ReportPath (Join-Path $out 'pages-after-sampling.local.json')
    }
} catch { $errorText = $_.Exception.Message }
finally {
    $process.Refresh()
    if (!$process.HasExited -and $process.StartTime.ToUniversalTime().Ticks -eq $identityTicks -and
        $process.Path.Equals($app,[StringComparison]::OrdinalIgnoreCase)) {
        $exit = Start-Process -FilePath $app -ArgumentList @('--exit','--expected-pid',[string]$process.Id,'--expected-start-ticks',[string]$identityTicks) -WindowStyle Hidden -PassThru
        if ($exit.WaitForExit(10000) -and $exit.ExitCode -eq 0) { $graceful = $process.WaitForExit(10000) }
    }
}
$appEvidence = if (Test-Path -LiteralPath $appReport) { Get-Content -Raw -LiteralPath $appReport | ConvertFrom-Json } else { $null }
$summary = $null
try { $summary = Get-ResourceSummary $samples.ToArray() $process.Id $identityTicks 60 ([Environment]::ProcessorCount) } catch { if (!$errorText) { $errorText = $_.Exception.Message } }
$bound = $null -ne $appEvidence -and $appEvidence.BuildId -ceq $manifest.BuildId
if ($bound) {
    foreach ($a in $appEvidence.Assemblies) { if ((Get-FileHash -LiteralPath (Join-Path $candidate ('app/' + $a.File))).Hash -ne $a.Sha256) { $bound = $false } }
    $bound = $bound -and @($appEvidence.Assemblies).Count -eq 4
}
$residuals = @()
foreach ($key in @($known.Keys)) {
    try { $p = Get-Process -Id ([int]$key) -ErrorAction Stop; if ($p.StartTime.ToUniversalTime().Ticks -eq $known[$key]) { $residuals += [pscustomobject]@{ Pid=$p.Id; StartTicks=$known[$key] } } } catch { }
}
$eligible = Test-ResourceWindow $summary $appEvidence $bound $graceful ([bool]$Mock) $errorText $residuals.Count
$report = [ordered]@{ BuildId = $manifest.BuildId; TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o'); Mode = $(if ($Mock) { 'Mock' } else { 'WindowsPropertyOnly' });
    Scenario = $appEvidence.Scenario; EvidenceBound = $bound; ResourceGateEligible = [bool]$eligible; GracefulExit = $graceful;
    ExitCode = $(if ($process.HasExited) { $process.ExitCode } else { $null }); Error = $errorText; InputIdleUpperBoundSeconds = $idleUpperBound;
    Summary = $summary; Samples = $samples.ToArray(); ApplicationEvidence = $appEvidence; ResidualObservedProcesses=$residuals;
    Scope = 'Observed scenario only. No device connection, pairing or GATT operations requested. UI idle is not tray rendering; no controlled cold start. Not Phase5 or 24-hour acceptance.' }
$report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $out 'resources.local.json') -Encoding utf8
Write-Output "Resource evidence: $out"
if ($errorText -or !$bound -or !$graceful) { throw 'Measurement incomplete; local evidence retained.' }
