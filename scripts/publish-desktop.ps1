param([string]$OutputDirectory = 'artifacts\QBox')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $workspace 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspace 'work\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
Push-Location $workspace
try {
    dotnet publish src\QBox.Desktop -c Release -r win-x64 --self-contained false -o $OutputDirectory `
        -p:PublishSingleFile=true -p:PublishSelfContained=false -p:SelfContained=false -p:EnableRuntimePackDownload=false `
        -p:EnableSingleFileAnalyzer=false -p:DebugType=embedded `
        -m:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw '窗口版发布失败' }
    & (Join-Path $PSScriptRoot 'copy-notices.ps1') -OutputDirectory $OutputDirectory
} finally { Pop-Location }
