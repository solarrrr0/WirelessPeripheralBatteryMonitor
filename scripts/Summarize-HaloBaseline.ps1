# Recalculate CPU and per-process metrics from captured CPU-time counters.
[CmdletBinding()]
param([string]$InputPath)
$ErrorActionPreference='Stop'
if(-not $InputPath){$InputPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'halo_baseline.local.json'}
$data=Get-Content -LiteralPath $InputPath -Raw -Encoding UTF8|ConvertFrom-Json
$counters=@{}; $lastSeconds=0.0
foreach($sample in $data.Samples){
    $delta=0.0
    foreach($process in $sample.Processes){
        if($counters.ContainsKey($process.Id)){$delta += [Math]::Max([double]0,[double]($process.CpuTotalSeconds-$counters[$process.Id]))}
        $counters[$process.Id]=[double]$process.CpuTotalSeconds
    }
    $sample.CpuDeltaSeconds=if($lastSeconds -gt 0){$delta}else{$null}
    $sample.CpuOneCorePercent=if($lastSeconds -gt 0){100*$delta/$sample.IntervalSeconds}else{$null}
    $sample.CpuMachinePercent=if($lastSeconds -gt 0){100*$delta/$sample.IntervalSeconds/$data.LogicalProcessors}else{$null}
    $lastSeconds=$sample.ElapsedSeconds
}
$steady=@($data.Samples|Where-Object {($_.ElapsedSeconds-$_.IntervalSeconds) -ge $data.WarmupSeconds})
$steadySeconds=($steady|Measure-Object IntervalSeconds -Sum).Sum
$data.Steady.CpuOneCorePercent=100*($steady|Measure-Object CpuDeltaSeconds -Sum).Sum/$steadySeconds
$data.Steady.CpuMachinePercent=$data.Steady.CpuOneCorePercent/$data.LogicalProcessors
$names=@($steady|ForEach-Object Processes|Select-Object -ExpandProperty Name -Unique)
$perProcess=@(foreach($name in $names){
    $rows=@($steady|ForEach-Object Processes|Where-Object Name -eq $name)
    [pscustomobject]@{Name=$name;AverageWorkingSetMiB=($rows|Measure-Object WorkingSetBytes -Average).Average/1MB;AveragePrivateMiB=($rows|Measure-Object PrivateBytes -Average).Average/1MB}
})
$data.Steady|Add-Member -NotePropertyName PerProcessAverage -NotePropertyValue $perProcess -Force
$data|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $InputPath -Encoding UTF8
$data.Steady|ConvertTo-Json -Depth 8
