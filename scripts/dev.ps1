param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$qboxRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $qboxRoot 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $qboxRoot 'work\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$devRoot = Join-Path $qboxRoot 'work\dev'
$stateDirectory = Join-Path $devRoot 'state'
$indexDirectory = Join-Path $devRoot 'indexes'
Push-Location $qboxRoot
try {
    dotnet build tools\QBox.DevData -c $Configuration -m:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw '开发样例工具构建失败' }
    dotnet run --project tools\QBox.DevData -c $Configuration --no-build -- $devRoot
    if ($LASTEXITCODE -ne 0) { throw '生成开发样例失败' }
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    # 每次都限制开发实例的后台更新和热键，已有开发历史不动，正式配置不读写。
    $settings = [ordered]@{ version = 1; indexDirectory = $indexDirectory; hotkey = 'Ctrl+Alt+Space'; autoUpdate = $false; resultShortcuts = $false }
    $temporary = Join-Path $stateDirectory ('settings.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination (Join-Path $stateDirectory 'settings.json') -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    dotnet build src\QBox.Desktop -c $Configuration -m:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw '开发窗口构建失败' }
    Write-Host "开发状态：$stateDirectory"
    Write-Host '样例：中文资料、资料、Alpha、doubao；唤起：Ctrl+Alt+空格。'
    if ($PrepareOnly) { return }
    $executable = Join-Path $qboxRoot "src\QBox.Desktop\bin\$Configuration\net10.0-windows\QBox.exe"
    # Start-Process 会拼接参数，显式保护带空格的仓库路径。
    Start-Process -FilePath $executable -ArgumentList @('--state-directory', ('"' + $stateDirectory + '"'), '--index-directory', ('"' + $indexDirectory + '"')) | Out-Null
} finally { Pop-Location }
