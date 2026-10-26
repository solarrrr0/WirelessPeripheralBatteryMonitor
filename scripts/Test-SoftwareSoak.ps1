[CmdletBinding()]
param([Parameter(Mandatory)][string]$HostDirectory,
    [Parameter(Mandatory)][string]$ReportPath,
    [ValidateRange(10,86400)][int]$DurationSeconds = 1200,
    [ValidateSet('idle','stress')][string]$Profile = 'stress')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
$root = Split-Path -Parent $PSScriptRoot
$hostDir = Assert-ReleasePath $HostDirectory (Join-Path $root 'artifacts/verification')
$report = Assert-ReleasePath $ReportPath (Join-Path $root 'artifacts/verification')
if ((Test-Path -LiteralPath $report) -or (Test-Path -LiteralPath ($report + '.checkpoint.local.json'))) { throw 'Soak evidence already exists.' }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($report))
$exe = Join-Path $hostDir 'PeripheralBatteryTray.App.Tests.exe'
$p = Start-Process -FilePath $exe -ArgumentList @('--software-soak','--report',('"'+$report+'"'),'--seconds',[string]$DurationSeconds,'--profile',$Profile) -WindowStyle Hidden -PassThru
$ticks = $p.StartTime.ToUniversalTime().Ticks
$watch = [Diagnostics.Stopwatch]::StartNew()
while (!$p.WaitForExit(1000)) {
    if ($watch.Elapsed.TotalSeconds -gt $DurationSeconds + 45) {
        # This process has an isolated single-instance prefix; never signal the daily application.
        throw "Soak exceeded its bound. Owned PID $($p.Id), start ticks $ticks. Checkpoint retained; no global exit attempted."
    }
}
if (!(Test-Path -LiteralPath $report)) { throw 'Soak exited without a final report.' }
$r = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
if ($p.ExitCode -ne 0 -or $r.Passed -cne $true -or $r.Profile -ne $Profile -or $r.RequestedSeconds -ne $DurationSeconds -or
    $r.HardwareConstructed -cne $false -or $r.PersistentSettings -cne $false -or $r.RealNotifications -cne $false) { throw 'Software soak failed; local report retained.' }
$expected = @('PeripheralBatteryTray.App.Tests.dll','PeripheralBatteryTray.dll','PeripheralBatteryTray.Core.dll','PeripheralBatteryTray.Windows.dll')
if (@($r.Assemblies).Count -ne 4 -or (Compare-Object $expected @($r.Assemblies.File))) { throw 'Missing soak binary binding.' }
foreach ($a in $r.Assemblies) {
    if ((Get-FileHash -LiteralPath (Join-Path $hostDir $a.File)).Hash -ne $a.Sha256) { throw 'Soak host changed during execution.' }
}
[pscustomobject]@{ Passed=$true; BuildId=$r.BuildId; Profile=$Profile; Seconds=$r.DurationSeconds; Iterations=$r.Iterations;
    SettingsForms=$r.SettingsForms; MaximumIcons=$r.MaximumIcons; Report=$report }
