[CmdletBinding()]
param([ValidateSet('Phase1','Phase2','Phase5')][string]$Stage = 'Phase1', [switch]$Zip)
$ErrorActionPreference = 'Stop'
if ($Stage -eq 'Phase1') {
    throw 'Phase1 artifacts are archived; rebuilding over them is disabled. Current sources include Phase4 features. Use -Stage Phase5 -Zip for a new candidate.'
}
& (Join-Path $PSScriptRoot 'Publish-Phase2Candidate.ps1') -Stage $Stage -Zip:$Zip
