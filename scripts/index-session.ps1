param([Parameter(Mandatory = $true)][string]$SessionDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$SessionDirectory = [IO.Path]::GetFullPath($SessionDirectory)
if (!$SessionDirectory.StartsWith((Join-Path $workspace 'work') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '会话目录必须位于当前工作区 work 目录内'
}
New-Item -ItemType Directory -Force -Path $SessionDirectory | Out-Null
$requestPath = Join-Path $SessionDirectory 'request.json'
$statusPath = Join-Path $SessionDirectory 'session.json'
$deadline = [DateTime]::UtcNow.AddMinutes(15)
$lastToken = ''
$lastAttempt = ''
while ([DateTime]::UtcNow -lt $deadline) {
    @{ State = 'idle'; DeadlineUtc = $deadline.ToString('O'); LastAttempt = $lastAttempt } |
        ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
    if (!(Test-Path -LiteralPath $requestPath)) { Start-Sleep -Seconds 2; continue }
    $request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
    if ($request.Token -notmatch '^[a-zA-Z0-9-]{1,64}$' -or $request.Token -eq $lastToken) { Start-Sleep -Seconds 2; continue }
    $lastToken = $request.Token
    if ($request.Action -eq 'stop') { break }
    if ($request.Action -ne 'index' -or $request.DriveLetters -notmatch '^[CD]{1,2}$') { continue }
    $lastAttempt = Join-Path $SessionDirectory ('attempt-' + $request.Token)
    @{ State = 'indexing'; Attempt = $lastAttempt; DeadlineUtc = $deadline.ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
    # 临时会话只允许本任务 C、D 盘的索引入口，退出后不保留后台服务或计划任务。
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $PSScriptRoot 'index-local.ps1') -DriveLetters $request.DriveLetters -RunDirectory $lastAttempt
}
@{ State = 'stopped'; LastAttempt = $lastAttempt } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
