[CmdletBinding()]
param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][long]$StartTicks,
    [Parameter(Mandatory)][string]$ExpectedPath, [Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference = 'Stop'
if (!(('ProcessMemoryInspector' -as [type]))) { Add-Type -Path (Join-Path $PSScriptRoot 'ProcessMemory.cs') }
$result = [ProcessMemoryInspector]::Capture($ProcessId, $StartTicks, $ExpectedPath)
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
$result.Buckets | Select-Object -First 12 Kind,File,@{n='ResidentMiB';e={[Math]::Round($_.ResidentBytes/1MB,2)}} | Format-Table | Out-Host
