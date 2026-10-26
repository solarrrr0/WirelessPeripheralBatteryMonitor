[CmdletBinding()]
param(
    [ValidateRange(30, 600)][int]$DurationSeconds = 90,
    [ValidateRange(5, 60)][int]$WarmupSeconds = 20
)
$ErrorActionPreference = 'Stop'
if ($DurationSeconds -le $WarmupSeconds + 5) { throw '测量窗口必须比预热时间至少长 5 秒。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$app = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\Phase1-win-x64\PeripheralBatteryTray.exe'))
if (-not (Test-Path -LiteralPath $app)) { throw '先运行 publish-win-x64.ps1' }
if (Get-Process -Name PeripheralBatteryTray -ErrorAction SilentlyContinue) { throw '请先退出已有应用，避免测量其他实例。' }
$clock = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $app -WindowStyle Hidden -PassThru
$samples = [Collections.Generic.List[object]]::new()
$childIds = [Collections.Generic.HashSet[int]]::new()
$gracefulExit = $false
try {
    $ready = $process.WaitForInputIdle(15000)
    $readySeconds = $clock.Elapsed.TotalSeconds
    if (-not $ready -or $process.HasExited) { throw '应用未能进入 UI 空闲。' }
    $logicalProcessors = [Environment]::ProcessorCount
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $process.Refresh()
        if ($process.HasExited) { throw '测量期间进程意外退出。' }
        $samples.Add([pscustomobject]@{
            ElapsedSeconds = $clock.Elapsed.TotalSeconds
            CpuSeconds = $process.TotalProcessorTime.TotalSeconds
            WorkingSetBytes = $process.WorkingSet64
            PrivateBytes = $process.PrivateMemorySize64
            Handles = $process.HandleCount
            Threads = $process.Threads.Count
        })
        foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)")) {
            [void]$childIds.Add([int]$child.ProcessId)
        }
        Start-Sleep -Seconds 2
    }
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        if ($process.Path -ne $app) { throw '进程路径核验失败，不发送退出请求。' }
        $exitCommand = Start-Process -FilePath $app -ArgumentList '--exit' -WindowStyle Hidden -PassThru
        if (-not $exitCommand.WaitForExit(10000)) { throw '退出命令未返回。' }
        $gracefulExit = $process.WaitForExit(10000)
        if (-not $gracefulExit) { throw "进程 $($process.Id) 未按时退出，请检查。" }
    }
}
$stable = @($samples | Where-Object ElapsedSeconds -ge $WarmupSeconds)
if ($stable.Count -lt 2) { throw '稳定区间样本不足。' }
$wallSeconds = $stable[-1].ElapsedSeconds - $stable[0].ElapsedSeconds
$cpuSeconds = $stable[-1].CpuSeconds - $stable[0].CpuSeconds
$cpuSingle = $cpuSeconds / $wallSeconds * 100
$summary = [pscustomobject]@{
    TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Scope = 'Phase 1 Mock; no user interaction; short warm-start idle measurement, not Phase 5 acceptance.'
    LogicalProcessors = $logicalProcessors
    WarmupSeconds = $WarmupSeconds
    StableSamples = $stable.Count
    StableSeconds = $wallSeconds
    AverageWorkingSetMiB = ($stable.WorkingSetBytes | Measure-Object -Average).Average / 1MB
    PeakWorkingSetMiB = ($stable.WorkingSetBytes | Measure-Object -Maximum).Maximum / 1MB
    AveragePrivateMiB = ($stable.PrivateBytes | Measure-Object -Average).Average / 1MB
    CpuDeltaSeconds = $cpuSeconds
    CpuSingleCorePercent = $cpuSingle
    CpuWholeMachinePercent = $cpuSingle / $logicalProcessors
    InputIdleUpperBoundSeconds = $readySeconds
    ObservedChildProcessIds = @($childIds)
    GracefulExit = $gracefulExit
    NoResidualProcess = -not [bool](Get-Process -Name PeripheralBatteryTray -ErrorAction SilentlyContinue)
    Samples = $samples
}
$report = Join-Path $projectRoot 'phase1-baseline.local.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding utf8
$summary | Select-Object * -ExcludeProperty Samples | Format-List
if ($childIds.Count -gt 0) { throw '发现子进程，当前汇总仅主进程，需补充进程树统计。' }
