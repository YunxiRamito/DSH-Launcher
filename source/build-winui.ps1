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

# 本机有离线 NuGet 缓存就用它(省流量);没有就不设,交给默认缓存。
# **别写死**:这个脚本本机/CI 都可能跑,写死本机路径会让 CI 上找不到目录。
$localNuGet = 'G:\DeepSeek DSH\.nuget-packages'
if (Test-Path -LiteralPath $localNuGet) { $env:NUGET_PACKAGES = $localNuGet }

# 本机走代理(仓库里的 NuGet.config 不写这个,那是本机专属的)
if (-not $env:HTTP_PROXY) { $env:HTTP_PROXY = 'http://127.0.0.1:7890' }
if (-not $env:HTTPS_PROXY) { $env:HTTPS_PROXY = 'http://127.0.0.1:7890' }

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
