[CmdletBinding()]
param([Parameter(Mandatory)][string]$CandidatePath, [string]$ZipPath, [string]$ZipHashPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseTools.ps1')
$artifactRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'
$candidate = Assert-ReleasePath $CandidatePath $artifactRoot
$manifest = Assert-CandidateTree $candidate
if ($ZipPath) {
    $zip = Assert-ReleasePath $ZipPath $artifactRoot
    if (!$ZipHashPath) { $ZipHashPath = $zip + '.sha256' }
    $hashFile = Assert-ReleasePath $ZipHashPath $artifactRoot
    if ((Get-Content -Raw -LiteralPath $hashFile).Trim() -ne (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash) { throw 'External ZIP hash mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $names = @($archive.Entries.FullName)
        $expected = @(Get-ReleaseAllowlist $manifest.Capability) + 'release-manifest.json'
        foreach ($entry in $archive.Entries) {
            Assert-RelativeReleaseName $entry.FullName
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or $entry.Length -gt 100MB) { throw 'Unsafe ZIP entry.' }
        }
        if ($names.Count -ne $expected.Count -or @($names | Select-Object -Unique).Count -ne $names.Count -or (Compare-Object $expected $names)) { throw 'ZIP file set mismatch.' }
    } finally { $archive.Dispose() }
    $parent = Assert-ReleasePath (Join-Path $artifactRoot 'verification/zip-extracts') $artifactRoot
    [void][IO.Directory]::CreateDirectory($parent)
    $unpack = Assert-ReleasePath (Join-Path $parent ([Guid]::NewGuid().ToString('N'))) $parent
    if (Test-Path -LiteralPath $unpack) { throw 'Extraction directory already exists.' }
    # Keep this unique directory as local evidence; no recursive cleanup of computed paths.
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $unpack)
    $unpackedManifest = Assert-CandidateTree $unpack
    if ((Get-FileHash -LiteralPath (Join-Path $candidate 'release-manifest.json')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $unpack 'release-manifest.json')).Hash -or $unpackedManifest.BuildId -cne $manifest.BuildId) { throw 'ZIP and directory manifests differ.' }
}
[pscustomobject]@{ Passed = $true; BuildId = $manifest.BuildId; Files = @($manifest.Files).Count; ZipVerified = [bool]$ZipPath }
