param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$qboxRoot = Split-Path -Parent $PSScriptRoot
$licenseDirectory = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
# 独立发布目录同时保留项目与第三方完整许可。
Copy-Item -LiteralPath (Join-Path $qboxRoot 'LICENSE'), (Join-Path $qboxRoot 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $qboxRoot 'third_party\pinyin-data\LICENSE') -Destination (Join-Path $licenseDirectory 'pinyin-data-LICENSE.txt') -Force
