param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'

$dotnet = 'G:\DeepSeek DSH\.tools\dotnet\dotnet.exe'
$project = Join-Path $PSScriptRoot 'DeepSeekHarness.csproj'
$iconScript = Join-Path $PSScriptRoot 'make-icon.ps1'
$sourceImage = Join-Path $PSScriptRoot 'assets\DeepSeek-icon.png'
$iconPath = Join-Path $PSScriptRoot 'DeepSeekHarness.ico'
$nugetConfig = Join-Path $PSScriptRoot 'NuGet.config'
$bootstrapSource = Join-Path $PSScriptRoot 'RuntimeBootstrap.cs'
$bootstrapManifest = Join-Path $PSScriptRoot 'RuntimeBootstrap.manifest'
$frameworkCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "未找到 .NET 8 SDK：$dotnet"
}

$sourceItem = Get-Item -LiteralPath $sourceImage
$iconItem = Get-Item -LiteralPath $iconPath -ErrorAction SilentlyContinue
if ($null -eq $iconItem -or $iconItem.LastWriteTimeUtc -lt $sourceItem.LastWriteTimeUtc) {
    & $iconScript -SourceImage $sourceImage -OutputPath $iconPath
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Get-ChildItem -LiteralPath $OutputDirectory -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$env:NUGET_PACKAGES = 'G:\DeepSeek DSH\.nuget-packages'

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --configfile $nugetConfig `
    --output $OutputDirectory `
    -p:AssemblyName='DeepSeek Harness.Core' `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:WindowsAppSDKSelfContained=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "WinUI 3 发布失败，退出代码：$LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $frameworkCompiler)) {
    throw "未找到 .NET Framework 编译器：$frameworkCompiler"
}

$bootstrapPath = Join-Path $OutputDirectory 'DeepSeek Harness.exe'
& $frameworkCompiler `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /utf8output `
    "/win32icon:$iconPath" `
    "/win32manifest:$bootstrapManifest" `
    "/out:$bootstrapPath" `
    $bootstrapSource

if ($LASTEXITCODE -ne 0) {
    throw "运行库引导程序编译失败，退出代码：$LASTEXITCODE"
}

Get-ChildItem -LiteralPath $OutputDirectory -File |
    Select-Object Name, Length, LastWriteTime
