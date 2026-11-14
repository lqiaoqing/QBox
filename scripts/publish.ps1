$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $workspace 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspace 'work\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
Push-Location $workspace
try {
    dotnet publish src\QBox.Cli -c Release --no-restore -o artifacts\QBox.Cli -m:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw '发布失败' }
    & (Join-Path $PSScriptRoot 'publish-desktop.ps1')
} finally { Pop-Location }
