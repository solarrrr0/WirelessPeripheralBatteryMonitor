#requires -Version 5.1
[CmdletBinding()]
param([int]$DurationSeconds=180,[int]$WarmupSeconds=60,[int]$SampleSeconds=5)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$appDir=Join-Path $projectRoot '.phase0\baseline\HaloBattery'
$exe=Join-Path $appDir 'HaloBattery.exe'
$expectedArchiveHash='92CE6DE7C5F9DD3408BB0BF1F7B37510BEA10CD84AD4231015CC27EDA020D836'
$archive=Join-Path $projectRoot '.phase0\downloads\HaloBattery-1.15.0.zip'
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedArchiveHash){throw 'Official release archive hash mismatch.'}
if(Get-Process HaloBattery -ErrorAction SilentlyContinue){throw 'An existing HaloBattery process is running; refusing to include or disturb it.'}
# Supported portable settings; upstream executable and protocol tables are unchanged.
Set-Content -LiteralPath (Join-Path $appDir 'portable.txt') -Value '' -Encoding ASCII
$configuration=[ordered]@{interval=60;notify=$false;full_alert=$false;low_sound=$false;animation=$false;update_check=$false;whats_new=$false;last_version='1.15.0';status_file=$true;bluetooth=$true;keep_disconnected=$true;playstation_full_mode=$false}
$configuration|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $appDir 'config.json') -Encoding ASCII
$logicalProcessors=[int](Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$clock=[Diagnostics.Stopwatch]::StartNew()
$appProcess=Start-Process -FilePath $exe -WorkingDirectory $appDir -WindowStyle Hidden -PassThru
$appProcess.Id|Set-Content -LiteralPath (Join-Path $projectRoot '.phase0\halo_process.local.txt')
$knownIds=@{ $appProcess.Id=$true }; $previousCpu=@{}; $previousSeconds=0.0; $readySeconds=$null
$samples=[Collections.Generic.List[object]]::new()
while($clock.Elapsed.TotalSeconds -lt $DurationSeconds){
    $all=@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name)
    do{
        $changed=$false
        foreach($entry in $all){if($knownIds.ContainsKey([int]$entry.ParentProcessId) -and -not $knownIds.ContainsKey([int]$entry.ProcessId)){$knownIds[[int]$entry.ProcessId]=$true;$changed=$true}}
    }while($changed)
    $processes=@(foreach($entry in $all){if($knownIds.ContainsKey([int]$entry.ProcessId)){Get-Process -Id $entry.ProcessId -ErrorAction SilentlyContinue}})
    if($processes.Count -eq 0){throw 'HaloBattery exited before the benchmark finished.'}
    $seconds=$clock.Elapsed.TotalSeconds; $cpuDelta=0.0
    $rows=@(foreach($process in $processes){
        $cpu=$process.TotalProcessorTime.TotalSeconds
        if($previousCpu.ContainsKey($process.Id)){$cpuDelta += [Math]::Max([double]0,[double]($cpu-$previousCpu[$process.Id]))}
        $previousCpu[$process.Id]=$cpu
        [pscustomobject]@{Id=$process.Id;Name=$process.ProcessName;WorkingSetBytes=$process.WorkingSet64;PrivateBytes=$process.PrivateMemorySize64;CpuTotalSeconds=$cpu;Handles=$process.HandleCount;Threads=$process.Threads.Count}
    })
    if($null -eq $readySeconds -and (Test-Path -LiteralPath (Join-Path $appDir 'status.json'))){$readySeconds=$seconds}
    $elapsed=$seconds-$previousSeconds
    $corePercent=if($previousSeconds -gt 0 -and $elapsed -gt 0){100*$cpuDelta/$elapsed}else{$null}
    $samples.Add([pscustomobject]@{ElapsedSeconds=$seconds;IntervalSeconds=$elapsed;CpuDeltaSeconds=if($previousSeconds -gt 0){$cpuDelta}else{$null};CpuOneCorePercent=$corePercent;CpuMachinePercent=if($null -ne $corePercent){$corePercent/$logicalProcessors}else{$null};Processes=$rows;WorkingSetBytes=($rows|Measure-Object WorkingSetBytes -Sum).Sum;PrivateBytes=($rows|Measure-Object PrivateBytes -Sum).Sum})
    $previousSeconds=$seconds
    Start-Sleep -Seconds $SampleSeconds
}
$steady=@($samples|Where-Object {($_.ElapsedSeconds-$_.IntervalSeconds) -ge $WarmupSeconds})
$steadyDuration=($steady|Measure-Object IntervalSeconds -Sum).Sum
$steadyCpu=($steady|Measure-Object CpuDeltaSeconds -Sum).Sum
$summary=[ordered]@{
    Release='v1.15.0';ArchiveSha256=$expectedArchiveHash;CollectedAt=[DateTimeOffset]::Now.ToString('o')
    RootProcessId=$appProcess.Id;LogicalProcessors=$logicalProcessors;DurationSeconds=$clock.Elapsed.TotalSeconds
    WarmupSeconds=$WarmupSeconds;SteadyMeasuredSeconds=$steadyDuration;SampleSeconds=$SampleSeconds
    StartupFirstStatusUpperBoundSeconds=$readySeconds;Configuration=$configuration
    MeasurementNote='CPU uses process CPU-time deltas; machine percent divides one-core percent by logical CPUs. Working-set sums may double-count shared pages. Includes all observed descendant processes. Startup status bound is not a measured tray-paint time. Application remains running for UI verification.'
    Steady=[ordered]@{
        TreeAverageWorkingSetMiB=(($steady|Measure-Object WorkingSetBytes -Average).Average/1MB)
        TreePeakWorkingSetMiB=(($steady|Measure-Object WorkingSetBytes -Maximum).Maximum/1MB)
        TreeAveragePrivateMiB=(($steady|Measure-Object PrivateBytes -Average).Average/1MB)
        CpuOneCorePercent=(100*$steadyCpu/$steadyDuration);CpuMachinePercent=(100*$steadyCpu/$steadyDuration/$logicalProcessors)
        FinalProcesses=@($samples[$samples.Count-1].Processes)
    }
    Samples=@($samples)
}
$summary|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $projectRoot 'halo_baseline.local.json') -Encoding UTF8
$summary.Remove('Samples')
$summary|ConvertTo-Json -Depth 8
