function Get-ResourceSummary {
    param([object[]]$Samples, [int]$RootPid, [long]$RootStartTicks, [double]$WarmupSeconds, [int]$LogicalProcessors)
    if ($LogicalProcessors -lt 1) { throw 'Invalid processor count.' }
    $root = @($Samples | Where-Object { $_.Pid -eq $RootPid -and $_.StartTicks -eq $RootStartTicks -and $_.ElapsedSeconds -ge $WarmupSeconds } | Sort-Object ElapsedSeconds)
    if ($root.Count -lt 2) { throw 'Insufficient stable root samples.' }
    $start = [double]$root[0].ElapsedSeconds; $end = [double]$root[-1].ElapsedSeconds; $wall = $end - $start
    if ($wall -le 0) { throw 'Invalid sample interval.' }
    [double]$cpu = 0; [double]$workingIntegral = 0; [double]$privateIntegral = 0
    $lifetimes = @()
    foreach ($group in $Samples | Group-Object { [string]$_.Pid + ':' + [string]$_.StartTicks }) {
        $rows = @($group.Group | Sort-Object ElapsedSeconds)
        [double]$processCpu = 0; [double]$observed = 0
        for ($i = 1; $i -lt $rows.Count; $i++) {
            $a = $rows[$i-1]; $b = $rows[$i]
            $dt = [double]$b.ElapsedSeconds - [double]$a.ElapsedSeconds
            if ($dt -le 0 -or [double]$b.CpuSeconds -lt [double]$a.CpuSeconds) { throw 'Invalid process lifecycle counters.' }
            $low = [Math]::Max($start, [double]$a.ElapsedSeconds); $high = [Math]::Min($end, [double]$b.ElapsedSeconds)
            if ($high -le $low) { continue }
            $span = $high - $low; $lo = ($low - $a.ElapsedSeconds) / $dt; $hi = ($high - $a.ElapsedSeconds) / $dt
            $processCpu += ([double]$b.CpuSeconds - [double]$a.CpuSeconds) * $span / $dt
            $observed += $span
            $workingIntegral += ($a.WorkingSetBytes + ($b.WorkingSetBytes - $a.WorkingSetBytes) * ($lo+$hi)/2) * $span
            $privateIntegral += ($a.PrivateBytes + ($b.PrivateBytes - $a.PrivateBytes) * ($lo+$hi)/2) * $span
        }
        $cpu += $processCpu
        $lifetimes += [pscustomobject]@{ Pid = $rows[0].Pid; StartTicks = $rows[0].StartTicks; ObservedSeconds = $observed; CpuDeltaSeconds = $processCpu }
    }
    [pscustomobject]@{ StableStartSeconds = $start; StableEndSeconds = $end; StableSeconds = $wall; StableRootSamples = $root.Count;
        AverageWorkingSetBytes = $workingIntegral / $wall; AverageWorkingSetMiB = $workingIntegral / $wall / 1MB;
        AveragePrivateBytes = $privateIntegral / $wall; AveragePrivateMiB = $privateIntegral / $wall / 1MB;
        PeakObservedRootWorkingSetBytes = ($root.WorkingSetBytes | Measure-Object -Maximum).Maximum;
        CpuDeltaSeconds = $cpu; CpuSingleCorePercent = 100 * $cpu / $wall; CpuWholeMachinePercent = 100 * $cpu / $wall / $LogicalProcessors;
        NoMeasurableCpuIncrement = ($cpu -eq 0); Lifetimes = $lifetimes;
        WorkingSet80MB = ($workingIntegral / $wall -le 80000000); WorkingSet50MB = ($workingIntegral / $wall -le 50000000);
        CpuTarget = (100 * $cpu / $wall -le 0.5);
        Method = 'Trapezoidal time-weighted observed bytes; CPU deltas per PID/start lifecycle. Unobserved short-lived children may be missed; sampling tool excluded.' }
}

function Test-ResourceWindow {
    param($Summary, $ApplicationEvidence, [bool]$Bound, [bool]$Graceful, [bool]$Mock, [string]$ErrorText, [int]$ResidualCount)
    # CPU and application telemetry have independent clock origins. Require a full
    # valid window in each rather than subtracting timestamps from different clocks.
    return [bool]($Bound -and $Graceful -and !$Mock -and !$ErrorText -and $Summary -and $ApplicationEvidence -and
        $Summary.StableStartSeconds -ge 60 -and $Summary.StableSeconds -ge 900 -and
        $null -ne $ApplicationEvidence.BaselineSeconds -and
        ($ApplicationEvidence.DurationSeconds - $ApplicationEvidence.BaselineSeconds) -ge 900 -and
        $ApplicationEvidence.Mode -eq 'Real' -and $ApplicationEvidence.Final.PollIntervalMinutes -eq 5 -and
        $ApplicationEvidence.Scenario -in @('OfflineObserved','OnlineObserved') -and
        $ApplicationEvidence.Shutdown.FullyStopped -and $ApplicationEvidence.Shutdown.CleanupErrors -eq 0 -and $ResidualCount -eq 0)
}
