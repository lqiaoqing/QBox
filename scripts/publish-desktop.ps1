param([string]$OutputDirectory = 'artifacts\QBox', [switch]$GlobalNameSharing)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $workspace 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspace 'work\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
Push-Location $workspace
try {
    $modeArguments = @()
    if ($GlobalNameSharing) {
        # 使用独立 SDK 产物目录，避免把全局策略 DLL 留在标准版的 bin/obj 中。
        $modeArguments = @('-p:QBoxGlobalNameSharing=true', '-p:UseArtifactsOutput=true',
            ('-p:ArtifactsPath=' + (Join-Path $workspace 'work\global-sharing-build')))
    }
    dotnet publish src\QBox.Desktop -c Release -r win-x64 --self-contained false -o $OutputDirectory `
        -p:PublishSingleFile=true -p:PublishSelfContained=false -p:SelfContained=false -p:EnableRuntimePackDownload=false `
        -p:EnableSingleFileAnalyzer=false -p:DebugType=embedded `
        -m:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo @modeArguments
    if ($LASTEXITCODE -ne 0) { throw '窗口版发布失败' }
    Push-Location (Join-Path $workspace 'rust\qbox-engine')
    try {
        cargo build --release --lib
        if ($LASTEXITCODE -ne 0) { throw 'Rust 搜索引擎构建失败' }
    } finally { Pop-Location }
    $engineDll = Join-Path $workspace 'work\rust-target\release\qbox_engine.dll'
    if (-not (Test-Path -LiteralPath $engineDll)) { throw 'Rust 搜索引擎 DLL 不存在' }
    # 单文件发布只打包托管程序，sidecar 必须与 QBox.exe 同级供 LibraryImport 加载。
    Copy-Item -LiteralPath $engineDll -Destination (Join-Path $OutputDirectory 'qbox_engine.dll') -Force
    & (Join-Path $PSScriptRoot 'copy-notices.ps1') -OutputDirectory $OutputDirectory
} finally { Pop-Location }
