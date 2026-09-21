# 把启动器 zip 发到 npm —— 目的不是给谁 npm install,而是**借 npmmirror 的带宽**。
#
# 国内直连 GitHub 只有几十到一百多 KB/s,而 npmmirror 全量镜像 npm,
# 实测 9.8 MB/s。jsDelivr 那条路走不通:它只对热门仓库的已缓存文件快,
# 我们自己这个冷门仓库走它还是回源 GitHub 的速度(实测 130 KB/s,八连接甚至 0 字节)。
#
# 用法:
#   .\publish-npm.ps1                 # 版本号从 manifest.json 读
#   .\publish-npm.ps1 -Version 1.3.22
#   .\publish-npm.ps1 -NoPublish      # 只把包目录准备出来,不发(先看内容)
#
# 安装器那边**不需要跟着改**:它按 manifest 里的 version 拼出 npmmirror 地址:
#   https://registry.npmmirror.com/@yunxiramito/dsh-launcher/-/dsh-launcher-<version>.tgz
# 所以以后发启动器就是"改版本号 → 跑这个脚本",安装器一行都不用动。

param(
    [string]$Version = '',
    [string]$PackageName = '@yunxiramito/dsh-launcher',
    [switch]$NoPublish,

    # 账号开了两步验证时,npm publish 会要一次性验证码(验证器 App 里的 6 位)。
    # 走命令行就得这样传;要想免验证码,得在 npm 网站上建一个**勾了 bypass 2FA**
    # 的 granular token 写进 .npmrc(npm 正在收紧这类 token,能用多久不好说)。
    [string]$Otp = '',

    # 不等 npmmirror 同步就返回。默认**要等** —— 见下面那段"闸门"的说明。
    [switch]$NoWait,

    # zip 在哪儿。默认找 assets\<版本>.zip;CI 里是刚构建出来的那份,用这个指定。
    [string]$ZipPath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Version) {
    $manifest = Get-Content (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
    $Version = $manifest.version
}

if (-not $Version) { throw '确定不了版本号:manifest.json 里没有 version,也没传 -Version' }

$zipName = "DeepSeekHarness-$Version.zip"
if (-not $ZipPath) {
    $ZipPath = Join-Path $root "assets\$zipName"
}

if (-not (Test-Path $ZipPath)) {
    throw "找不到 zip:$ZipPath`n(本地发版要先把它放进 assets\;CI 里用 -ZipPath 指定刚构建的那份)"
}

$stage = Join-Path $env:TEMP "dsh-launcher-npm-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# npm 包的版本号必须和启动器一致 —— 安装器就是靠这个拼地址的
$pkg = [ordered]@{
    name        = $PackageName
    version     = $Version
    description = 'Dafeiyu-Go 启动器发行包(供国内镜像分发用,不是给人 require 的库)'
    license     = 'MIT'
    author      = 'Deepseek / KitamaruRamito'
    homepage    = 'https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run'
    repository  = @{ type = 'git'; url = 'https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run.git' }
    files       = @($zipName)
    keywords    = @('dafeiyu-go', 'deepseek', 'harness', 'launcher', 'dist')
}

$json = $pkg | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $stage 'package.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
Copy-Item $ZipPath (Join-Path $stage $zipName) -Force

Write-Host "包目录:$stage"
Get-ChildItem $stage | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize

if ($NoPublish) {
    Write-Host '-NoPublish:到这一步为止,没发出去。' -ForegroundColor Yellow
    return
}

Push-Location $stage
try {
    # --access public:作用域包默认是私有的,发布必须显式声明公开
    if ($Otp) {
        Write-Host "带一次性验证码发布(otp 已提供)" -ForegroundColor DarkGray
        & npm publish --access public --otp=$Otp
    }
    else {
        & npm publish --access public
    }

    if ($LASTEXITCODE -ne 0) {
        throw "npm publish 失败(退出码 $LASTEXITCODE)。若是 403 要求 2FA,加 -Otp <验证器里的6位码> 重跑。"
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host "已发布 $PackageName@$Version" -ForegroundColor Green

# ---------------------------------------------------------------- 闸门
#
# **同步好之前不要更新 manifest.json、不要发 GitHub Release。**
#
# 为什么:npm 上是发出去了,但 npmmirror 要过一会儿才同步。而安装器认的版本号
# 来自 manifest.json —— 清单一旦先更新,正好在那个空窗里装的用户就会去拉
# npmmirror 上还不存在的 tgz,拉不到就只能落到 GitHub 那条慢路(几十 KB/s)。
#
# 所以顺序必须是:发布 npm → **等 npmmirror 备好** → 再动清单。
# 这里就是那道等待;等到了才告诉你"可以去更新清单了"。

$bare = ($PackageName -split '/')[-1]
$tarball = "https://registry.npmmirror.com/$PackageName/-/$bare-$Version.tgz"
$syncApi = "https://registry.npmmirror.com/-/package/$PackageName/syncs"

if ($NoWait) {
    Write-Host '-NoWait:不等同步。记得先确认下面这个地址能下再更新 manifest.json:' -ForegroundColor Yellow
    Write-Host "  $tarball" -ForegroundColor Yellow
    return
}

Write-Host ''
Write-Host '等 npmmirror 同步(好了才能更新 manifest.json)…' -ForegroundColor Cyan

$ready = $false
$deadline = (Get-Date).AddMinutes(25)

while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest $tarball -Method Head -UseBasicParsing -TimeoutSec 20
        if ($r.StatusCode -eq 200) { $ready = $true; break }
    }
    catch {
        # 还没同步上。催一下 npmmirror 再去等 —— 它有个按需同步的接口。
        try { Invoke-WebRequest $syncApi -Method Put -UseBasicParsing -TimeoutSec 15 | Out-Null } catch { }
    }

    Write-Host "  …还没好,20 秒后再看" -ForegroundColor DarkGray
    Start-Sleep -Seconds 20
}

if (-not $ready) {
    Write-Host ''
    Write-Host 'npmmirror 一直没同步上(超过 25 分钟)。' -ForegroundColor Red
    Write-Host '**现在别更新 manifest.json** —— 空窗期装的用户会掉到 GitHub 那条慢路。' -ForegroundColor Red
    Write-Host "可以过一会儿再去看:$tarball" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'npmmirror 已就绪 ✅' -ForegroundColor Green
Write-Host "  $tarball" -ForegroundColor DarkGray
Write-Host ''
Write-Host '现在可以更新 manifest.json 里的 version / sha256,并发 GitHub Release 了。' -ForegroundColor Green
