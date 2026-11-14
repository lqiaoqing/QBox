param(
    [ValidatePattern('^[A-Za-z]*$')][string]$DriveLetters = '',
    [string]$RunDirectory = '',
    [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$workRoot = Join-Path $workspace 'work'
if (!$RunDirectory) { $RunDirectory = Join-Path $workRoot ('native-index-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
if (!$RunDirectory.StartsWith($workRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '运行报告目录必须位于当前工作区 work 目录内'
}
$executable = Join-Path $workspace 'artifacts\QBox.Cli\QBox.Cli.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw '请先运行 scripts\publish.ps1' }
$indexDirectory = Join-Path $workRoot 'indexes'
New-Item -ItemType Directory -Force -Path $RunDirectory, $indexDirectory | Out-Null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '需要 Windows 管理员权限才能只读打开 NTFS 原始卷'
}
if (!$DriveLetters) {
    $DriveLetters = -join ([IO.DriveInfo]::GetDrives() | Where-Object {
        $_.IsReady -and $_.DriveType -eq [IO.DriveType]::Fixed -and $_.DriveFormat -eq 'NTFS'
    } | ForEach-Object { $_.Name[0] })
}
$statusPath = Join-Path $RunDirectory 'status.json'
$results = @()
foreach ($letter in ($DriveLetters.ToUpperInvariant().ToCharArray() | Select-Object -Unique)) {
    $drive = $letter + ':'
    $indexPath = Join-Path $indexDirectory ($letter + '.qbi')
    $log = Join-Path $RunDirectory ($letter + '.log')
    $command = if (!$Rebuild -and (Test-Path -LiteralPath $indexPath)) { 'index-sync' } else { 'index-build' }
    @{ State = 'running'; Drive = $drive; Results = $results } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statusPath -Encoding UTF8
    # 仅提升卷元数据读取进程；索引和日志写入当前工作区，不修改卷内容或安装服务。
    $exitCode = 2
    try {
        # Windows PowerShell 5 会把原生 stderr 包装为错误记录；进度文本不能中止索引进程。
        $ErrorActionPreference = 'Continue'
        & $executable $command --drive $drive --index $indexPath *> $log
        $exitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = 'Stop' }
    $summary = $null
    if ($exitCode -eq 0) {
        $summary = & $executable index-info --index $indexPath | Out-String | ConvertFrom-Json
    }
    $results += [pscustomobject]@{ Drive = $drive; Command = $command; ExitCode = $exitCode; Index = $indexPath; Log = $log; Summary = $summary }
}
@{ State = 'completed'; Results = $results } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statusPath -Encoding UTF8
if (@($results | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0) { exit 2 }
