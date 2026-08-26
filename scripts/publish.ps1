param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\win-x64'),
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
chcp 65001 > $null
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [System.IO.Path]::GetFullPath($OutputDirectory)

dotnet publish (Join-Path $repoRoot 'src\RenPyLocalizationStudio.App\RenPyLocalizationStudio.App.csproj') -c Release -r win-x64 --self-contained false -o $output
& (Join-Path $PSScriptRoot 'prepare-tool-runtime.ps1') -OutputDirectory (Join-Path $output 'tools')
Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File -Recurse | Remove-Item -Force

if ($Package) {
    $releaseDirectory = Join-Path $repoRoot 'artifacts\release'
    $archive = Join-Path $releaseDirectory 'RenPyLocalizationStudio-v0.3.0-win-x64-framework-dependent.zip'
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$archive.sha256", "$hash  $([System.IO.Path]::GetFileName($archive))`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "发布包：$archive"
}
