$ErrorActionPreference = 'Stop'
$qboxRoot = Split-Path -Parent $PSScriptRoot
$files = & git -C $qboxRoot -c core.quotepath=false ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) { throw '无法枚举版本库文件' }
$failures = @()
foreach ($relative in ($files | Select-Object -Unique)) {
    $path = Join-Path $qboxRoot $relative
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    if ($relative -match '(?i)\.(qbi|qbd|pdb)$|(^|/)(history|settings)\.json$|(^|/)\.env($|\.(?!example$))') {
        $failures += "$relative : 不应提交的本机状态或构建数据"
        continue
    }
    if ($relative -notmatch '(?i)\.(md|cs|ps1|json|ya?ml|java|py|gz)$') { continue }
    if ($relative.EndsWith('.gz')) {
        $file = [IO.File]::OpenRead($path)
        try {
            $gzip = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionMode]::Decompress)
            try {
                $reader = New-Object IO.StreamReader($gzip, [Text.Encoding]::UTF8)
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            } finally { $gzip.Dispose() }
        } finally { $file.Dispose() }
    } else { $text = [IO.File]::ReadAllText($path) }
    if ($relative.EndsWith('.md')) {
        foreach ($link in [regex]::Matches($text, '\[[^\]\r\n]+\]\(([^)\r\n]+)\)')) {
            $reference = $link.Groups[1].Value.Trim('<', '>').Split('#')[0]
            if (!$reference -or $reference -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') { continue }
            $target = Join-Path (Split-Path -Parent $path) ([Uri]::UnescapeDataString($reference))
            if (!(Test-Path -LiteralPath $target)) { $failures += "$relative : 本地链接目标不存在：$reference" }
        }
    }
    # 检查当前材料的个人目录和 SID，不输出命中的隐私原文，也不修改历史提交。
    if ($text -match '(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b)[^\\/"\s]+' -or
        $text -match '(?i)[A-Z]:[\\/]+Work[\\/]' -or $text -match 'S-1-5-21-\d+(?:-\d+){3}') {
        $failures += "$relative : 包含需要脱敏的本机路径或 SID"
    }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Host '仓库材料检查通过（当前文本、压缩证据及文件名；不扫描 Git 历史）。'
