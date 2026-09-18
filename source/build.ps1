param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'

$sourceImage = Join-Path $PSScriptRoot 'assets\DeepSeek-icon.png'
$iconPath = Join-Path $PSScriptRoot 'DeepSeekHarness.ico'
$sourcePath = Join-Path $PSScriptRoot 'Program.cs'
$balanceSourcePath = Join-Path $PSScriptRoot 'BalanceSupport.cs'
$balanceAlertSourcePath = Join-Path $PSScriptRoot 'BalanceAlerts.cs'
$iconScript = Join-Path $PSScriptRoot 'make-icon.ps1'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

& $iconScript -SourceImage $sourceImage -OutputPath $iconPath

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$outputPath = Join-Path $OutputDirectory 'DeepSeek Harness.exe'

& $compiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /codepage:65001 `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Security.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Windows.Forms.dll `
    "/win32icon:$iconPath" `
    "/resource:$iconPath,AppIcon.ico" `
    "/out:$outputPath" `
    $sourcePath `
    $balanceSourcePath `
    $balanceAlertSourcePath

if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}

Get-Item -LiteralPath $outputPath, $iconPath | Select-Object FullName, Length, LastWriteTime
