param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$CacheDirectory = $(if ($env:RLS_TOOL_CACHE) { $env:RLS_TOOL_CACHE } else { Join-Path $env:LOCALAPPDATA 'RenPyLocalizationStudio\downloads' })
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
chcp 65001 > $null
$repoRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $repoRoot 'tools\tool-runtime.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Encoding UTF8 -Raw | ConvertFrom-Json
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$cache = [System.IO.Path]::GetFullPath($CacheDirectory)
$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ('RLS-Tools-' + [Guid]::NewGuid().ToString('N'))
$pythonDir = Join-Path $output 'python'
$unrpycDir = Join-Path $output 'unrpyc'
$rpaDir = Join-Path $output 'rpatool'

function Get-LockedDownload([object]$component) {
    $target = Join-Path $cache $component.fileName
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $component.sha256) {
        Remove-Item -LiteralPath $target -Force
    }
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host "下载 $($component.fileName)"
        Invoke-WebRequest -Uri $component.url -OutFile $target
    }
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($actual -ne $component.sha256) { throw "下载校验失败：$($component.fileName)，期望 $($component.sha256)，实际 $actual" }
    return $target
}

try {
    New-Item -ItemType Directory -Path $workspace,$output,$cache -Force | Out-Null
    $pythonZip = Get-LockedDownload $lock.components.python
    $unrpycZip = Get-LockedDownload $lock.components.unrpyc
    $rpatoolFile = Get-LockedDownload $lock.components.rpatool
    foreach ($managedPath in @($pythonDir, $unrpycDir, $rpaDir, (Join-Path $output 'manifest.json'))) {
        if (Test-Path -LiteralPath $managedPath) { Remove-Item -LiteralPath $managedPath -Recurse -Force }
    }
    New-Item -ItemType Directory -Path $pythonDir,$unrpycDir,$rpaDir -Force | Out-Null
    Expand-Archive -LiteralPath $pythonZip -DestinationPath $pythonDir -Force
    Expand-Archive -LiteralPath $unrpycZip -DestinationPath $workspace -Force
    $unrpycSource = Join-Path $workspace "unrpyc-$($lock.components.unrpyc.version)"
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'unrpyc.py') -Destination $unrpycDir -Force
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'deobfuscate.py') -Destination $unrpycDir -Force
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'decompiler') -Destination $unrpycDir -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'LICENSE') -Destination $unrpycDir -Force
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'README.md') -Destination $unrpycDir -Force
    Add-Content -LiteralPath (Join-Path $pythonDir 'python313._pth') -Value '..\unrpyc','..\rpatool' -Encoding ascii
    Copy-Item -LiteralPath $rpatoolFile -Destination (Join-Path $rpaDir 'rpatool.py') -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\safe_rpa_extract.py') -Destination (Join-Path $rpaDir 'safe_rpa_extract.py') -Force
    Copy-Item -LiteralPath $lockPath -Destination (Join-Path $output 'tool-runtime.lock.json') -Force
    $manifest = [ordered]@{
        schemaVersion = 1
        components = $lock.components
        files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = $_.FullName.Substring($output.Length).TrimStart('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
    }
    [System.IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
}
finally {
    if (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}
