param(
    [string]$ToolsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\win-x64\tools')
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
chcp 65001 > $null

$tools = [System.IO.Path]::GetFullPath($ToolsDirectory)
$manifestPath = Join-Path $tools 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "缺少工具清单：$manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$listed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$toolsPrefix = $tools.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relative) -or [System.IO.Path]::IsPathRooted($relative)) { throw "工具清单包含非法路径：$relative" }
    $full = [System.IO.Path]::GetFullPath((Join-Path $tools $relative))
    if (-not $full.StartsWith($toolsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw "工具清单路径越界：$relative" }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "工具文件缺失：$relative" }
    $file = Get-Item -LiteralPath $full
    if ($file.Length -ne [long]$entry.length) { throw "工具文件长度不匹配：$relative" }
    $actualHash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    if (-not $actualHash.Equals([string]$entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) { throw "工具文件哈希不匹配：$relative" }
    [void]$listed.Add(($relative -replace '\\', '/'))
}

$required = @('python/python.exe', 'rpa/safe_rpa_extract.py', 'unrpyc/unrpyc.py')
foreach ($relative in $required) {
    if (-not $listed.Contains($relative)) { throw "必要工具未纳入清单：$relative" }
}
Write-Host "工具运行时清单校验通过，共 $($listed.Count) 个文件。"
