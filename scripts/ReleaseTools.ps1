# Shared release contracts. Dot-sourcing has no build, device or filesystem side effects.
function Assert-ReleasePath {
    param([string]$Path, [string]$AllowedRoot, [switch]$AllowRoot)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $base = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\','/')
    if (!(($AllowRoot -and $full.Equals($base, [StringComparison]::OrdinalIgnoreCase)) -or
        $full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Path must be a descendant of the permitted root.'
    }
    $cursor = $full
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if ($null -ne $item) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not permitted.' }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

function Get-ReleaseFiles {
    param([string]$Root)
    $Root = Assert-ReleasePath $Root $Root -AllowRoot
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($Root)
    while ($pending.Count) {
        $dir = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $dir -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point in release tree.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) } else { $item }
        }
    }
}

function Get-ReleaseAllowlist {
    param([string]$Capability = 'WindowsPropertyAndDeviceSettings')
    $files = @('app/PeripheralBatteryTray.exe','app/PeripheralBatteryTray.dll','app/PeripheralBatteryTray.Core.dll',
      'app/PeripheralBatteryTray.Windows.dll','app/Microsoft.Windows.SDK.NET.dll','app/WinRT.Runtime.dll',
      'app/PeripheralBatteryTray.deps.json','app/PeripheralBatteryTray.runtimeconfig.json',
      'README_中文.md','THIRD_PARTY_NOTICES.md','licenses/CsWinRT-LICENSE.txt','licenses/Windows-SDK-license.rtf',
      'docs/DEVICE_COMPATIBILITY.md','docs/RESOURCE_BENCHMARK.md','docs/PHASE2_VALIDATION.md','docs/TROUBLESHOOTING.md',
      'docs/VALIDATION.json','docs/ATK_PROTOCOL_RESEARCH.md','Start.cmd','Check-ATK.cmd','Check-Bluetooth.cmd')
    if ($Capability -eq 'WindowsPropertyAndDeviceSettings') { $files += 'docs/PHASE4_DEVICE_SETTINGS.md' }
    $files
}

function Assert-RelativeReleaseName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -notmatch '^[^\\:]+$' -or $Name.StartsWith('/') -or
        @($Name.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count) {
        throw 'Invalid release-relative path.'
    }
}

function Get-ReleaseInputFiles {
    param([string]$Root)
    $names = @('Directory.Build.props','global.json','PeripheralBatteryTray.slnx','THIRD_PARTY_NOTICES.md','docs/ATK_PROTOCOL_RESEARCH.md','docs/PHASE3_ATK_AUTO_REFRESH_SPEC.md','docs/PHASE4_LOW_BATTERY.md','docs/PHASE4_DEVICE_SETTINGS.md',
        'licenses/CsWinRT-LICENSE.txt','licenses/Windows-SDK-license.rtf')
    foreach ($sub in @('src','tests','scripts','docs/release')) {
        # Enumerate one directory at a time; never traverse generated outputs or junctions.
        $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push((Join-Path $Root $sub))
        while ($pending.Count) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if ($item.PSIsContainer -and $item.Name -in @('bin','obj')) { continue }
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point in build inputs.' }
                if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
                if ($item.Name -match '\.local\.' -or $item.Extension -notin @('.cs','.csproj','.ps1','.md','.cmd')) { throw 'Unexpected build input.' }
                $names += $item.FullName.Substring($Root.Length + 1).Replace('\','/')
            }
        }
    }
    foreach ($name in $names | Sort-Object -Unique) {
        $file = Assert-ReleasePath (Join-Path $Root $name) $Root
        [pscustomobject]@{ Path = $name; Sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
    }
}

function Get-ReleaseCases {
    param([string]$Suite)
    switch ($Suite) {
        Core { @(1..29 | ForEach-Object { 'C{0:00}' -f $_ }) + @('A01','A02','A03','LB01','LB02','LB03') }
        Windows { @(1..24 | ForEach-Object { 'W{0:00}' -f $_ }) + @(1..24 | ForEach-Object { 'G{0:00}' -f $_ }) + @('F01','J02','AT01','AT02','AT03','AT04') + @(6..13 | ForEach-Object { 'P{0:00}' -f $_ }) }
        App { @('P01','P02','P03','P04','P05','J01','M01','H01','H02','H03','H04','H05','AA01','AA02','AA03','AA04','AA05','AA06','NA01','NA02','NA03','DP01','DP02','DP03','DP04','DP05','DP06') }
        Release { @('P14','P15','P16','P17','P18') }
        Regression {
            @('Three explicitly simulated devices','Tray configured visible; startup popup hidden','Band remains a band; offline remains offline',
            'Offline/unknown cached zero excluded from aggregation','Out-of-range percentages rejected','Real zero and 100 remain valid',
            'Provider observes cancellation and disposal','Left-click callback opens list','Closing list keeps application resident',
            'Left-click callback toggles list','Settings callback opens one settings window','Closing settings keeps tray alive',
            '100 manual refreshes complete without icon redraw','Synthetic TaskbarCreated re-registers tray without repaint',
            'Repeated refresh/icon creation releases GDI and USER handles','Geometry scale 100% fits three rows at current monitor DPI',
            'Geometry scale 125% fits three rows at current monitor DPI','Geometry scale 150% fits three rows at current monitor DPI',
            'Geometry scale 200% fits three rows at current monitor DPI','Second launch exits successfully','Second launch signals existing popup',
            'Exit menu stops message loop and hides tray','Application-owned resources disposed')
        }
        default { throw 'Unknown test suite.' }
    }
}

function Assert-ReleaseReport {
    param($Report, [int]$ExitCode, [string]$BuildId, [string]$Suite, [string]$ExecutedDirectory, [string]$ProductDirectory)
    if ($ExitCode -ne 0 -or $Report.Passed -isnot [bool] -or $Report.Passed -cne $true -or $Report.BuildId -cne $BuildId) { throw 'Failed, skipped or stale build report.' }
    $expected = @(Get-ReleaseCases $Suite)
    $actual = @($Report.Checks | ForEach-Object { if ($Suite -eq 'Regression') { $_.Name } else { $_.Id } })
    if ($actual.Count -ne $expected.Count -or @($actual | Select-Object -Unique).Count -ne $actual.Count -or
        (Compare-Object $expected $actual) -or @($Report.Checks | Where-Object { $_.Passed -isnot [bool] -or $_.Passed -cne $true }).Count) { throw 'Incomplete or failed test case list.' }
    $assemblies = switch ($Suite) {
        Core { @('PeripheralBatteryTray.Tests.dll','PeripheralBatteryTray.Core.dll') }
        Windows { @('PeripheralBatteryTray.Windows.Tests.dll','PeripheralBatteryTray.Windows.dll','PeripheralBatteryTray.Core.dll') }
        App { @('PeripheralBatteryTray.App.Tests.dll','PeripheralBatteryTray.dll','PeripheralBatteryTray.Windows.dll','PeripheralBatteryTray.Core.dll') }
        Regression { @('PeripheralBatteryTray.exe','PeripheralBatteryTray.dll','PeripheralBatteryTray.Windows.dll','PeripheralBatteryTray.Core.dll') }
        default { throw 'Unsupported assembly report.' }
    }
    if (@($Report.Assemblies).Count -ne $assemblies.Count -or (Compare-Object $assemblies @($Report.Assemblies.File))) { throw 'Incomplete assembly evidence.' }
    foreach ($a in $Report.Assemblies) {
        if ($a.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            (Get-FileHash -LiteralPath (Join-Path $ExecutedDirectory $a.File) -Algorithm SHA256).Hash -ne $a.Sha256) { throw 'Executed assembly hash mismatch.' }
        if ($a.File -in @('PeripheralBatteryTray.exe','PeripheralBatteryTray.dll','PeripheralBatteryTray.Core.dll','PeripheralBatteryTray.Windows.dll') -and
            (Get-FileHash -LiteralPath (Join-Path $ProductDirectory $a.File) -Algorithm SHA256).Hash -ne $a.Sha256) { throw 'Tested assembly differs from candidate.' }
    }
    if ($Suite -eq 'Regression' -and ($Report.Mode -ne 'Mock' -or @($Report.Phase2Cases).Count -ne 1 -or
        $Report.Phase2Cases[0].Id -ne 'C30' -or $Report.Phase2Cases[0].Passed -cne $true)) { throw 'Missing current Mock C30 evidence.' }
}

function Assert-CandidateTree {
    param([string]$Candidate)
    $files = @(Get-ReleaseFiles $Candidate)
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $Candidate 'release-manifest.json') | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.BuildId -notmatch '^Phase(2|5)-[A-Za-z0-9-]+$' -or
        $manifest.Stage -notin @('Phase2','Phase5') -or !$manifest.BuildId.StartsWith($manifest.Stage + '-', [StringComparison]::Ordinal) -or
        $manifest.Capability -notin @('WindowsPropertyOnly','WindowsPropertyAndManualAtk','WindowsPropertyAndOptInAtk','WindowsPropertyOptInAtkAndAlerts','WindowsPropertyAndDeviceSettings') -or
        $manifest.Tfm -ne 'net10.0-windows10.0.19041.0' -or $manifest.Rid -ne 'win-x64' -or
        $manifest.SelfContained -cne $false -or $manifest.PdbIncluded -cne $false -or
        $manifest.ConfigSchema -ne $(switch ($manifest.Capability) { 'WindowsPropertyAndDeviceSettings' {4} 'WindowsPropertyOptInAtkAndAlerts' {3} 'WindowsPropertyAndOptInAtk' {2} default {1} }) -or
        $manifest.BuildSdk -notmatch '^10\.0\.\d+$') { throw 'Manifest metadata mismatch.' }
    $allow = @(Get-ReleaseAllowlist $manifest.Capability)
    $listed = @($manifest.Files.Path)
    foreach ($name in $listed) { Assert-RelativeReleaseName $name }
    $actual = @($files | ForEach-Object { $_.FullName.Substring($Candidate.Length + 1).Replace('\','/') })
    $allowedDirs = @('app','docs','licenses')
    foreach ($dir in Get-ChildItem -LiteralPath $Candidate -Directory -Recurse -Force) {
        if ($dir.FullName.Substring($Candidate.Length + 1).Replace('\','/') -notin $allowedDirs) { throw 'Unexpected candidate directory.' }
    }
    if ($listed.Count -ne $allow.Count -or @($listed | Select-Object -Unique).Count -ne $listed.Count -or
        (Compare-Object $allow $listed) -or (Compare-Object ($allow + 'release-manifest.json') $actual)) { throw 'Candidate differs from explicit file allowlist.' }
    foreach ($entry in $manifest.Files) {
        $file = Join-Path $Candidate $entry.Path
        if ($entry.Length -lt 0 -or (Get-Item -LiteralPath $file).Length -ne $entry.Length -or $entry.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw 'Candidate content mismatch.' }
    }
    $inputs = @($manifest.SourceInputs)
    if (!$inputs.Count -or @($inputs.Path | Select-Object -Unique).Count -ne $inputs.Count) { throw 'Missing or duplicate source evidence.' }
    foreach ($input in $inputs) {
        Assert-RelativeReleaseName $input.Path
        if ($input.Path -match '(^|/)(bin|obj|\.tools|\.phase0)(/|$)|\.local\.' -or $input.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid source input evidence.' }
    }
    $validation = Get-Content -Raw -LiteralPath (Join-Path $Candidate 'docs/VALIDATION.json') | ConvertFrom-Json
    if ($validation.Capability -ne $manifest.Capability) { throw 'Manifest and acceptance capabilities differ.' }
    Assert-CandidateAcceptance $validation $manifest.BuildId
    return $manifest
}

function New-CandidateAcceptance {
    param([string]$BuildId, [object[]]$Automatic)
    [pscustomobject]@{ SchemaVersion = 1; BuildId = $BuildId; Capability = 'WindowsPropertyAndDeviceSettings'; Overall = 'CandidateIncompleteHardware';
        Automatic = @($Automatic); Gates = @(@(1..5 | ForEach-Object { 'H' + $_ }) + @(1..6 | ForEach-Object { 'V' + $_ }) + @('TrayMouse','ExplorerRestart','SystemDpi','ResourceOnline','ResourceOffline','24Hour','AtkInterface','AtkReplySemantics','AtkRead','AtkCoexistence','AL1','AL2','AL3','AL4','NL1','NL2','DL1','SL1') |
        ForEach-Object { [pscustomobject]@{ Id = $_; Status = 'NotRun'; Reason = 'Not executed for this candidate; historical or simulated results do not replace it.' } }) }
}
function Assert-CandidateAcceptance {
    param($Report, [string]$BuildId)
    if ($Report.BuildId -cne $BuildId -or $Report.Capability -notin @('WindowsPropertyOnly','WindowsPropertyAndManualAtk','WindowsPropertyAndOptInAtk','WindowsPropertyOptInAtkAndAlerts','WindowsPropertyAndDeviceSettings') -or $Report.Overall -ne 'CandidateIncompleteHardware') { throw 'Candidate capability or completion claim invalid.' }
    $counts = @{ Core=$(if ($Report.Capability -in @('WindowsPropertyOptInAtkAndAlerts','WindowsPropertyAndDeviceSettings')) {35} else {32}); Windows=62;
        App=$(switch ($Report.Capability) { 'WindowsPropertyAndDeviceSettings' {27} 'WindowsPropertyOptInAtkAndAlerts' {21} 'WindowsPropertyAndOptInAtk' {18} 'WindowsPropertyAndManualAtk' {12} default {10} }); Regression=23; Release=5 }
    if (@($Report.Automatic).Count -ne 5 -or (Compare-Object @($counts.Keys) @($Report.Automatic.Suite))) { throw 'Missing automatic evidence summary.' }
    foreach ($suite in $Report.Automatic) {
        if ($suite.Cases -ne $counts[$suite.Suite] -or $suite.Status -ne 'Passed' -or $suite.ReportSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid automatic evidence summary.' }
    }
    $expected = @('H1','H2','H3','H4','H5','V1','V2','V3','V4','V5','V6','TrayMouse','ExplorerRestart','SystemDpi','ResourceOnline','ResourceOffline','24Hour','AtkInterface','AtkReplySemantics','AtkRead','AtkCoexistence')
    if ($Report.Capability -in @('WindowsPropertyAndOptInAtk','WindowsPropertyOptInAtkAndAlerts','WindowsPropertyAndDeviceSettings')) { $expected += @('AL1','AL2','AL3','AL4') }
    if ($Report.Capability -in @('WindowsPropertyOptInAtkAndAlerts','WindowsPropertyAndDeviceSettings')) { $expected += @('NL1','NL2') }
    if ($Report.Capability -eq 'WindowsPropertyAndDeviceSettings') { $expected += @('DL1','SL1') }
    if (@($Report.Gates).Count -ne $expected.Count -or (Compare-Object $expected @($Report.Gates.Id)) -or
        @($Report.Gates | Where-Object { $_.Status -notin @('NotRun','Blocked') -or [string]::IsNullOrWhiteSpace($_.Reason) }).Count) { throw 'Unverified candidate gates must remain explicit.' }
}
