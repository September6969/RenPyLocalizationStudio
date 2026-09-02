param(
    [string]$SdkExe = $env:RLS_RENPY_SDK
)

$ErrorActionPreference = 'Stop'
$SdkExe = [string]$SdkExe
if ([string]::IsNullOrWhiteSpace($SdkExe)) {
    throw "请通过 -SdkExe 或 RLS_RENPY_SDK 指定 Ren'Py SDK 的 renpy.exe。"
}
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
chcp 65001 > $null

$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot 'tests\Fixtures\SdkProject'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('RenPyLocalizationStudio-SdkLint-' + [Guid]::NewGuid().ToString('N'))
$stdoutPath = Join-Path $tempRoot 'lint.stdout.txt'
$stderrPath = Join-Path $tempRoot 'lint.stderr.txt'

try {
    dotnet build (Join-Path $repoRoot 'RenPyLocalizationStudio.sln') -c Release --no-restore | Out-Host
    Copy-Item -LiteralPath $fixture -Destination $tempRoot -Recurse
    $process = Start-Process -FilePath $SdkExe -ArgumentList @($tempRoot, 'lint') -Wait -PassThru -NoNewWindow -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    Get-Content -LiteralPath $stdoutPath -Encoding UTF8
    if (Test-Path -LiteralPath $stderrPath) {
        Get-Content -LiteralPath $stderrPath -Encoding UTF8
    }

    if ($process.ExitCode -ne 0) {
        throw "Ren'Py lint 失败，退出码：$($process.ExitCode)"
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
