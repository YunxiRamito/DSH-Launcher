<#
  release.ps1 —— 启动器一键发版

  干的事:
    1. 从源码里读出当前版本号
    2. 编译 WinUI3 主程序 + 外层引导
    3. 打包成 DeepSeekHarness-<版本>.zip
    4. 算 SHA256,生成/更新仓库根目录的 manifest.json
    5. 打印后续要执行的 git 命令

  用法:
    .\release.ps1                     # 构建 + 打包 + 生成 manifest
    .\release.ps1 -NoBuild            # 用现成的 dist-<版本> 重新打包
    .\release.ps1 -Push               # 顺手把 tag 推到远端的命令也打出来
#>
[CmdletBinding()]
param(
    [string]$Repository = 'YunxiRamito/DSH-Launcher',
    [string]$OutputRoot,
    [switch]$NoBuild,
    [switch]$KeepDist
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$SourceRoot = Join-Path $PSScriptRoot 'source'
$ProjectFile = Join-Path $SourceRoot 'DeepSeekHarness.csproj'
$BootstrapSource = Join-Path $SourceRoot 'RuntimeBootstrap.cs'
$BootstrapManifest = Join-Path $SourceRoot 'RuntimeBootstrap.manifest'
$IconPath = Join-Path $SourceRoot 'DeepSeekHarness.ico'
$BuildScript = Join-Path $SourceRoot 'build-winui.ps1'
$ManifestPath = Join-Path $PSScriptRoot 'manifest.json'
$Dotnet = 'G:\DeepSeek DSH\.tools\dotnet\dotnet.exe'
$NuGetCache = 'G:\DeepSeek DSH\.nuget-packages'
$Csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Say  ([string]$m) { Write-Host $m }
function Ok   ([string]$m) { Write-Host "  [OK]  $m" -ForegroundColor Green }
function Warn ([string]$m) { Write-Host "  [!!]  $m" -ForegroundColor Yellow }
function Step ([string]$m) { Write-Host "  [..]  $m" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 版本号
Say ''
Say '========================================'
Say '  DeepSeek Harness 启动器 发版'
Say '========================================'

Say ''
Say '[1/5] 读版本号'
$projectText = Get-Content -Path $ProjectFile -Raw -Encoding UTF8
$versionMatch = [regex]::Match($projectText, '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) { throw "从 csproj 里读不到 <Version>" }
$version = $versionMatch.Groups[1].Value.Trim()
Ok "版本: $version"

$distDir = if ($OutputRoot) { $OutputRoot } else { Join-Path $SourceRoot ("dist-" + $version) }
$zipName = "DeepSeekHarness-$version.zip"
$zipPath = Join-Path $PSScriptRoot $zipName
Say "    输出目录: $distDir"

# ---------------------------------------------------------------- 编译
if (-not $NoBuild) {
    Say ''
    Say '[2/5] 编译'
    if (-not (Test-Path $Dotnet)) { throw "找不到 .NET SDK: $Dotnet" }
    $env:NUGET_PACKAGES = $NuGetCache

    Step 'dotnet publish (WinUI3 主程序)'
    & $Dotnet publish $ProjectFile `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $distDir `
        -p:AssemblyName='DeepSeek Harness.Core' `
        -p:PublishSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=false `
        -p:WindowsAppSDKSelfContained=false `
        -p:DebugType=None -p:DebugSymbols=false -v minimal
    if ($LASTEXITCODE -ne 0) { throw "主程序编译失败: $LASTEXITCODE" }
    Ok '主程序完成'

    Step 'csc (外层引导程序)'
    if (-not (Test-Path $Csc)) { throw "找不到 csc.exe: $Csc" }
    & $Csc /nologo /target:winexe /platform:x64 /optimize+ /utf8output `
        "/win32icon:$IconPath" "/win32manifest:$BootstrapManifest" `
        "/out:$(Join-Path $distDir 'DeepSeek Harness.exe')" $BootstrapSource
    if ($LASTEXITCODE -ne 0) { throw "引导程序编译失败: $LASTEXITCODE" }
    Ok '引导程序完成'
} else {
    Say ''
    Say '[2/5] 跳过编译(用现成产物)'
}

# ---------------------------------------------------------------- 体检产物
Say ''
Say '[3/5] 检查产物'
$required = @('DeepSeek Harness.exe', 'DeepSeek Harness.Core.exe')
foreach ($name in $required) {
    $path = Join-Path $distDir $name
    if (-not (Test-Path $path)) { throw "缺少关键文件: $path" }
    $item = Get-Item $path
    Ok ("{0}  {1}  {2}" -f $name, $item.VersionInfo.FileVersion, [math]::Round($item.Length / 1KB, 0).ToString() + ' KB')
}

# 清掉本机换文件留下的 .old 残留,别打进包里
Get-ChildItem $distDir -Filter '*.old*' -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
    Warn ("清理残留: " + $_.Name)
}

$fileCount = (Get-ChildItem $distDir -Recurse -File | Measure-Object).Count
Ok "产物文件数: $fileCount"

# ---------------------------------------------------------------- 打包
Say ''
Say '[4/5] 打包'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipItem = Get-Item $zipPath
Ok ("{0}  {1:N1} MB" -f $zipName, ($zipItem.Length / 1MB))

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
Ok "SHA256: $hash"

# ---------------------------------------------------------------- manifest
Say ''
Say '[5/5] 生成 manifest.json'
$assetUrl = "https://github.com/$Repository/releases/download/v$version/$zipName"

$manifest = [ordered]@{
    version      = $version
    sha256       = $hash
    subDirectory = ''
    assets       = [ordered]@{
        github  = $assetUrl
        mirrors = @()
    }
    notes        = "DeepSeek Harness 启动器 v$version"
    releasedAt   = (Get-Date).ToString('s')
}

$json = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($ManifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Ok "已写入: $ManifestPath"

Say ''
Say '----------------------------------------'
Say '接下来手动执行(或者用 GitHub 网页发 release):'
Say ''
Say "  git add manifest.json"
Say "  git commit -m `"release: v$version`""
Say "  git tag v$version"
Say "  git push origin main --tags"
Say ''
Say "  然后把 $zipName 作为资产传到 v$version 这个 release 下:"
Say "  $assetUrl"
Say '----------------------------------------'
Say ''
Say '提示:推完 release 之后,安装器会自动读到这一版,不用重发安装器。'
Say ''
