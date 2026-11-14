$ErrorActionPreference = 'Stop'
$qboxRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $qboxRoot 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $qboxRoot 'work\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'

Push-Location $qboxRoot
try {
    dotnet build QBox.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    dotnet run --project tests\QBox.Checks -c Release --no-build -- --include-holdout
    if ($LASTEXITCODE -ne 0) { throw '验证失败' }
}
finally { Pop-Location }
