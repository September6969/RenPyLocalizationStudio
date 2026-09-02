param(
    [string]$OutputDirectory,
    [switch]$Package,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
chcp 65001 > $null
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $(if ($SelfContained) { 'artifacts\win-x64-self-contained' } else { 'artifacts\win-x64' })
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "为避免误删，发布输出目录必须位于 artifacts 内：$output"
}
$projectPath = Join-Path $repoRoot 'src\RenPyLocalizationStudio.App\RenPyLocalizationStudio.App.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { throw '无法从应用项目读取版本号。' }

# 发布前清空精确输出目录，避免旧版本文件混入新压缩包。
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path $output -Force | Out-Null

$selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()
dotnet publish $projectPath -c Release -r win-x64 --self-contained $selfContainedValue -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码：$LASTEXITCODE" }
& (Join-Path $PSScriptRoot 'prepare-tool-runtime.ps1') -OutputDirectory (Join-Path $output 'tools')
Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File -Recurse | Remove-Item -Force

if ($Package) {
    $releaseDirectory = Join-Path $repoRoot 'artifacts\release'
    $deploymentName = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
    $archive = Join-Path $releaseDirectory "RenPyLocalizationStudio-v$version-win-x64-$deploymentName.zip"
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    if (Test-Path -LiteralPath "$archive.sha256") { Remove-Item -LiteralPath "$archive.sha256" -Force }
    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [System.IO.File]::Open($archive, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new($archiveStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $fixedTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName) {
                $relative = $file.FullName.Substring($output.Length).TrimStart('\', '/') -replace '\\', '/'
                $entry = $zip.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp
                $entryStream = $entry.Open()
                try {
                    $sourceStream = [System.IO.File]::OpenRead($file.FullName)
                    try { $sourceStream.CopyTo($entryStream) } finally { $sourceStream.Dispose() }
                }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $archiveStream.Dispose() }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$archive.sha256", "$hash  $([System.IO.Path]::GetFileName($archive))`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "发布包：$archive"
}
