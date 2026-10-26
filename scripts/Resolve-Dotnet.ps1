# Resolve a locally prepared SDK before falling back to the user's installed SDK.
$projectRoot = Split-Path -Parent $PSScriptRoot
$privateDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $privateDotnet) { $privateDotnet }
else {
    $command = Get-Command dotnet -ErrorAction Stop
    $sdkList = & $command.Source --list-sdks
    if (-not ($sdkList -match '^10\.')) { throw '需要 .NET 10 SDK；仅安装 Desktop Runtime 不能编译。' }
    $command.Source
}
