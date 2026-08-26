param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\win-x64'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet publish (Join-Path $repoRoot 'src\RenPyLocalizationStudio.App\RenPyLocalizationStudio.App.csproj') -c Release -r win-x64 --self-contained false -o $OutputDirectory
& (Join-Path $PSScriptRoot 'prepare-tool-runtime.ps1') -OutputDirectory (Join-Path $OutputDirectory 'tools')
