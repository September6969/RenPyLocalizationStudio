param(
    [string]$SdkExe = 'E:\renpy-8.5.2-sdk\renpy.exe'
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
chcp 65001 > $null

$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot 'tests\Fixtures\SdkProject'
$coreAssembly = Join-Path $repoRoot 'src\RenPyLocalizationStudio.Core\bin\Release\net10.0-windows10.0.26100.0\RenPyLocalizationStudio.Core.dll'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('RenPyLocalizationStudio-SdkLint-' + [Guid]::NewGuid().ToString('N'))
$stdoutPath = Join-Path $tempRoot 'lint.stdout.txt'
$stderrPath = Join-Path $tempRoot 'lint.stderr.txt'

try {
    dotnet build (Join-Path $repoRoot 'RenPyLocalizationStudio.sln') -c Release --no-restore | Out-Host
    Copy-Item -LiteralPath $fixture -Destination $tempRoot -Recurse
    Add-Type -Path $coreAssembly
    $analyzer = [RenPyLocalizationStudio.Core.ProjectAnalyzer]::new()
    $snapshot = $analyzer.AnalyzeAsync($tempRoot, 'schinese', [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $writer = [RenPyLocalizationStudio.Core.ProjectWriter]::new()
    $result = $writer.SaveAsync($snapshot, $true, $true, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($result.Diagnostics | Where-Object Severity -eq 'Error') {
        throw '工具保存验证失败。'
    }

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
