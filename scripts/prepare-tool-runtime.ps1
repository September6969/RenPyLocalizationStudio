param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
chcp 65001 > $null

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ('RLS-Tools-' + [Guid]::NewGuid().ToString('N'))
$pythonZip = Join-Path $workspace 'python.zip'
$unrpycZip = Join-Path $workspace 'unrpyc.zip'
$pythonDir = Join-Path $output 'python'
$unrpycDir = Join-Path $output 'unrpyc'
$rpaDir = Join-Path $output 'rpatool'

try {
    New-Item -ItemType Directory -Path $workspace -Force | Out-Null
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    # Clean managed directories to avoid mixing old manifests or python caches.
    foreach ($managedPath in @($pythonDir, $unrpycDir, $rpaDir, (Join-Path $output 'manifest.json'))) {
        if (Test-Path -LiteralPath $managedPath) {
            Remove-Item -LiteralPath $managedPath -Recurse -Force
        }
    }
    Invoke-WebRequest 'https://www.python.org/ftp/python/3.13.7/python-3.13.7-embed-amd64.zip' -OutFile $pythonZip
    Invoke-WebRequest 'https://codeload.github.com/CensoredUsername/unrpyc/zip/refs/tags/v2.0.4' -OutFile $unrpycZip
    New-Item -ItemType Directory -Path $pythonDir,$unrpycDir,$rpaDir -Force | Out-Null
    Expand-Archive -LiteralPath $pythonZip -DestinationPath $pythonDir -Force
    Expand-Archive -LiteralPath $unrpycZip -DestinationPath $workspace -Force
    $unrpycSource = Join-Path $workspace 'unrpyc-2.0.4'
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'unrpyc.py') -Destination $unrpycDir -Force
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'deobfuscate.py') -Destination $unrpycDir -Force
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'decompiler') -Destination $unrpycDir -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'LICENSE') -Destination $unrpycDir -Force
    Copy-Item -LiteralPath (Join-Path $unrpycSource 'README.md') -Destination $unrpycDir -Force
    Add-Content -LiteralPath (Join-Path $pythonDir 'python313._pth') -Value '..\unrpyc','..\rpatool' -Encoding ascii
    Invoke-WebRequest 'https://raw.githubusercontent.com/Shizmob/rpatool/master/rpatool' -OutFile (Join-Path $rpaDir 'rpatool.py')
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\safe_rpa_extract.py') -Destination (Join-Path $rpaDir 'safe_rpa_extract.py') -Force

    $manifest = Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName.Substring($output.Length).TrimStart('\', '/'); Length = $_.Length; Sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    }
    $json = $manifest | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText((Join-Path $output 'manifest.json'), $json)
}
finally {
    if (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}
