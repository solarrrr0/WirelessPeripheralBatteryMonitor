#requires -Version 5.1
# Reads only Windows HID descriptors for an exact VID/PID. No HID reports are sent.
[CmdletBinding()]
param([string]$VendorId='373B',[string]$ProductId='11FD',[string]$OutputPath)
$ErrorActionPreference='Stop'
if($VendorId -notmatch '^[0-9A-Fa-f]{4}$' -or $ProductId -notmatch '^[0-9A-Fa-f]{4}$'){throw 'Expected four hexadecimal digits for each ID.'}
if(-not $OutputPath){$OutputPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'hid_capabilities.local.json'}
if(-not ('Phase0.HidDescriptors' -as [type])){
Add-Type -Path (Join-Path $PSScriptRoot 'HidDescriptors.cs')
}
$result=[ordered]@{CollectedAt=[DateTimeOffset]::Now.ToString('o');Safety='CreateFile desired access 0, shared handles; only HidD_GetPreparsedData/HidP_GetCaps. No ReadFile, WriteFile, GetFeature, SetFeature or output reports.';Vid=$VendorId.ToUpperInvariant();Pid=$ProductId.ToUpperInvariant();Interfaces=@([Phase0.HidDescriptors]::Read($VendorId,$ProductId))}
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result.Interfaces|Select-Object InterfaceNumber,CollectionNumber,UsagePage,Usage,InputBytes,OutputBytes,FeatureBytes,Error|ConvertTo-Json -Depth 4
