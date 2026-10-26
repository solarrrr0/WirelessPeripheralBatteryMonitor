#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
if(-not $OutputPath){$OutputPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'device_inventory.local.json'}
$started = [DateTimeOffset]::Now
$devices = @(Get-PnpDevice)
$selected = @($devices | Where-Object {
    $_.Class -in @('Bluetooth','HIDClass','Mouse','Keyboard','AudioEndpoint','MEDIA','USB','USBDevice') -or
    $_.InstanceId -match '^(USB|BTHENUM|BTHLE|BTHLEDEVICE|BTHHFENUM)\\'
})
$presentIds = @{}
Get-PnpDevice -PresentOnly | ForEach-Object { $presentIds[$_.InstanceId] = $true }
$properties = @(Get-PnpDeviceProperty -InstanceId $selected.InstanceId -ErrorAction SilentlyContinue)
$byId = @{}
foreach ($property in $properties) {
    if (-not $byId.ContainsKey($property.InstanceId)) { $byId[$property.InstanceId] = @{} }
    $byId[$property.InstanceId][$property.KeyName] = $property.Data
}
$nodes = @($selected | ForEach-Object {
    $d = $_; $p = $byId[$d.InstanceId]
    if (-not $p) { $p = @{} }
    $vendorId = $null; $productId = $null; $mi = $null; $col = $null
    if ($d.InstanceId -match '(?i)VID_([0-9A-F]{4})&PID_([0-9A-F]{4})') { $vendorId=$Matches[1]; $productId=$Matches[2] }
    if ($d.InstanceId -match '(?i)&MI_([0-9A-F]{2})') { $mi=$Matches[1] }
    if ($d.InstanceId -match '(?i)&COL([0-9A-F]{2})') { $col=$Matches[1] }
    $battery = @($p.GetEnumerator() | Where-Object {
        $_.Key -match 'Battery|104EA319-6EE2-4701-BD47-8DDBF425BBE5|49CD1F76-5626-4B17-A4E8-18B4AA1A2213'
    } | ForEach-Object { [pscustomobject]@{Key=$_.Key;Value=$_.Value} })
    $connection = @($p.GetEnumerator() | Where-Object {
        $_.Key -match 'Connected|LastConnected|83DA6326-97A6-4088-9453-A1923F573B29.*15$'
    } | ForEach-Object { [pscustomobject]@{Key=$_.Key;Value=$_.Value} })
    [pscustomobject]@{
        Name=$d.FriendlyName; Class=$d.Class; PnpStatus=[string]$d.Status
        Present=$presentIds.ContainsKey($d.InstanceId)
        InstanceId=$d.InstanceId; Parent=$p['DEVPKEY_Device_Parent']; ContainerId=$p['DEVPKEY_Device_ContainerId']
        Bus=($d.InstanceId -split '\\')[0]; Vid=$vendorId; Pid=$productId; InterfaceNumber=$mi; CollectionNumber=$col
        BusReportedDescription=$p['DEVPKEY_Device_BusReportedDeviceDesc']; Manufacturer=$p['DEVPKEY_Device_Manufacturer']
        HardwareIds=@($p['DEVPKEY_Device_HardwareIds']); CompatibleIds=@($p['DEVPKEY_Device_CompatibleIds'])
        ServiceGuid=$p['DEVPKEY_Bluetooth_ServiceGUID']; BatteryProperties=$battery; ConnectionProperties=$connection
        WindowsBatteryPropertyObserved=($battery.Count -gt 0)
        ConnectionNote='PnP presence/status is not a live Bluetooth or receiver-child connection test.'
    }
})
$os=Get-CimInstance Win32_OperatingSystem
$cs=Get-CimInstance Win32_ComputerSystem
$version=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$toolPaths=[ordered]@{}
foreach ($name in 'dotnet','git','cl','msbuild','cmake','winget') {
    $command=Get-Command $name -ErrorAction SilentlyContinue
    $toolPaths[$name]=if($command){$command.Source}else{$null}
}
$dotnetInfo=if($toolPaths.dotnet){(& $toolPaths.dotnet --info 2>&1 | Out-String)}else{$null}
$sdkList=if($toolPaths.dotnet){@(& $toolPaths.dotnet --list-sdks)}else{@()}
$vswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vs=if(Test-Path -LiteralPath $vswhere){(& $vswhere -products '*' -format json | Out-String)}else{$null}
$kit=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
$result=[ordered]@{
    SchemaVersion=1; CollectedAt=$started.ToString('o'); CompletedAt=[DateTimeOffset]::Now.ToString('o')
    Privacy='LOCAL ONLY: includes device paths, MAC-derived identifiers and serials. Excluded by .gitignore. Do not publish.'
    Safety='OS property enumeration only. No HID handles, reports, writes, pairing or configuration changes.'
    Environment=[ordered]@{
        OS=$os.Caption; Version=$os.Version; Build=($version.CurrentBuild+'.'+$version.UBR)
        DisplayVersion=$version.DisplayVersion; Architecture=$os.OSArchitecture; SystemType=$cs.SystemType
        LogicalProcessors=$cs.NumberOfLogicalProcessors; PowerShell=$PSVersionTable.PSVersion.ToString()
        Elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        Tools=$toolPaths; DotnetInfo=$dotnetInfo; DotnetSdks=@($sdkList); VisualStudioInstances=$vs
        WindowsKitsRoot=if($kit){$kit.KitsRoot10}else{$null}
    }
    Counts=[ordered]@{TotalSelected=$nodes.Count;Present=@($nodes|Where-Object Present).Count;NonPresent=@($nodes|Where-Object {-not $_.Present}).Count}
    Devices=$nodes
}
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
[pscustomobject]@{SavedFile=[IO.Path]::GetFileName($OutputPath);Counts=$result.Counts;OS=$result.Environment.OS;Build=$result.Environment.Build;DotnetSdkCount=$sdkList.Count} | ConvertTo-Json -Depth 4
