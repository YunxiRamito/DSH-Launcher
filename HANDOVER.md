# 交接:DSH Launcher 1.4.1

> 面向下一个接手窗口的完整对接报告。当前启动器已经是 `1.4.1`，
> 已经发布到 npm、npmmirror 和 GitHub Release。旧版 1.3.x 交接内容保留在
> 本文末尾的“历史记录”部分，只用来查背景，不要拿旧状态继续开发。

最后更新:2026-09-21

---

## 一、先看结论

### 当前发布状态

| 项目 | 仓库 | 本地 HEAD | 最新 tag | 状态 |
|------|------|-----------|----------|------|
| DSH Launcher | `https://github.com/YunxiRamito/DSH-Launcher` | `9bdf0a8` | `v1.4.1` | 已发布 |
| DSH Installer | `https://github.com/YunxiRamito/dsh-installer` | `b550288` | `v1.0.0` | 本轮未修改 |

启动器 1.4.1 已发布:

- npm:`@yunxiramito/dsh-launcher@1.4.1`
- npmmirror:
  `https://registry.npmmirror.com/@yunxiramito/dsh-launcher/-/dsh-launcher-1.4.1.tgz`
- GitHub Release:
  `https://github.com/YunxiRamito/DSH-Launcher/releases/tag/v1.4.1`
- 资产:
  `https://github.com/YunxiRamito/DSH-Launcher/releases/download/v1.4.1/DeepSeekHarness-1.4.1.zip`
- SHA256:
  `f74bdb4980a5cb4585bf4d9c0e8ef7e2e2c6fc246eb0089fe9b43adff5a81b2d`

远端 `main/manifest.json` 已确认是 `1.4.1`，下载 URL 返回 HTTP 200。

用户在 2026-09-21 已确认虚拟机验证通过。下面的“验证状态”保留为后续
版本升级时的回归清单，不再表示当前版本仍未验证。

### 两个项目的关系

- 安装器负责:检测环境、补运行库、安装 DSH 本体、下载并部署启动器、快捷方式、卸载。
- 启动器负责:托盘常驻、启动和管理 DSH、余额、设置、自更新、DSH 更新、提醒。
- 安装器不内置当前启动器。安装时读取启动器仓库的 `manifest.json`，
  再下载对应版本的 zip。
- 启动器发新版不需要重发安装器，只要 npm 和 GitHub Release 发布完成，
  安装器下一次新装就会拿到新版。

---

## 二、启动器代码地图

仓库根目录:

`G:\DeepSeek DSH\DSH Works\Project\DeepSeek Starter`

主要文件:

| 文件 | 作用 |
|------|------|
| `source/WinUIProgram.cs` | 主入口、托盘、服务生命周期、更新策略、提醒、单实例、退出 |
| `source/SettingsWindow.xaml` | 设置窗口布局、页面、控件、SettingsExpander |
| `source/SettingsWindow.xaml.cs` | 设置窗交互、即时保存、服务状态、更新状态反馈 |
| `source/SettingsWindowHost.cs` | 设置窗与启动器主进程之间的回调接口 |
| `source/LauncherSettings.cs` | `LauncherSettings.json` 数据模型 |
| `source/LauncherSettingsStore.cs` | 配置读写、旧配置迁移、DPAPI API Key |
| `source/LauncherAppearance.cs` | 设置窗、托盘、进度窗、通知窗共享的主题和材质 |
| `source/CornerRadiusHelper.cs` | Windows 10/11 圆角策略 |
| `source/UpdateSupport.cs` | 启动器更新清单、下载、npm tgz 解包、替换脚本 |
| `source/DshUpdateService.cs` | DSH npm 版本检查、npm 安装、失败回滚 |
| `source/UpdateUiSnapshot.cs` | 设置窗更新状态模型 |
| `source/BilibiliProfileService.cs` | 关于作者头像 API 获取和进程缓存 |
| `source/InstallerRegistration.cs` | 同步安装器状态文件和卸载注册表 |
| `source/BalanceSupport.cs` | API Key 读取/加密、余额查询 |
| `source/BalanceAlerts.cs` | 余额、消费、充值提醒 |

设置窗资源:

| 路径 | 作用 |
|------|------|
| `source/assets/SettingsNavIcons/*.svg` | Win11 彩色 Fluent 导航图标 |
| `source/assets/donate-alipay.jpg` | 支付宝收款码 |
| `source/assets/donate-wechat.png` | 微信收款码 |
| `source/assets/DeepSeek-icon.png` | 标题栏图标 |

`THIRD-PARTY-NOTICES.md` 记录了 Fluent System Icons 的 MIT 来源。

---

## 三、1.4.1 的实际功能

### 设置窗口

- 默认 `1200x720`，最小 `800x560`，可缩放、可最大化。
- 48 像素自绘标题栏，标题是“DeepSeek Harness 启动器设置”。
- 左侧 NavigationView，内容最大宽度 760。
- 页面:常规、主题、API、提醒、服务、更新、关于。
- 关于页内部包含作者区块，不是独立导航项。
- 设置窗是启动器同进程内的 owner 窗口，关闭后销毁，再次打开重建。
- 页面切换有淡入和轻微位移动画。

### 常规

- 端口模式:随机、固定、默认 3080。
- 默认端口显示黄色安全提示。
- 默认选择是固定端口 `8787`。
- 已有 DSH 服务运行时，设置端口不会强行覆盖当前端口；
  页面显示绿色“下次启动服务时生效”。
- 如果启动器发现已有 DSH 端口和设置不一致，只弹 Windows 气泡。
- 开机自启动立即写当前用户的自启配置。
- 静默启动:全部、仅开机自启、关闭，只决定是否自动打开网页。

### 主题

- 主题:浅色、深色、跟随系统。
- 主题色来源:跟随 Windows、自选。
- 材质:云母、云母 Alt、轻薄亚克力、标准亚克力、纯色。
- 设置窗、托盘菜单、更新进度窗、通知窗共用 `LauncherAppearance`。
- Win11 使用圆角，Win10 使用直角。
- Win11 左侧导航使用微软官方 Fluent System Icons 彩色 SVG。
- Win10 保持单色 Fluent 字形。

### API

- API Key 使用 DPAPI 加密后写入 `LauncherSettings.json`。
- 打开设置页时会异步读取并显示余额。
- 格式不对时不保存并恢复原值。
- 格式正确但远端验证失败时仍保存，并显示失败原因。
- 清空 API Key 后不会再回退读取 DSH 的 `.credentials.yaml`。

### 提醒

- 更新提醒、服务启动提醒、充值提醒都有独立开关。
- 今日消费提醒:`5/10/20/50/自定义`，默认全选。
- 余额提醒:`20/10/5/1/自定义`，默认全选。
- 多个余额/消费阈值同时触发时合并为一条通知。
- 同一余额阈值跌落后，只有回升到阈值以上才重新武装。
- 充值判定门槛是余额增加 `0.10` 元。
- 第一次读取到的余额只建立基线，不判定充值。

### 服务

- DSH 目录:文件夹选择器，校验 `node_modules\@deepseek-ai\dsh\lib\bin.js`。
- Node 路径:文件选择器，只接受 `node.exe`。
- 服务正在运行时变更路径，只提示下次启动生效。
- 重启服务时重新读取设置中的路径和端口。
- DSH 目录变化会同步:
  - `%LOCALAPPDATA%\DeepSeekHarness\installer-state.json`
  - 已存在的卸载注册表项
- 同时存在 HKLM/HKCU 卸载项时优先机器级。
- 非安装器环境只写启动器自己的配置，不创建卸载注册表。

### 更新

- 更新源:加速源、官方源。
- 启动器更新:自动下载并安装、仅检查更新、关闭。
- DSH 更新:自动下载并安装、仅检查更新、关闭。
- 检查周期:每次启动、三天、七天、一个月。
- 周期从上次检查时间开始算，离线失败不额外重试。
- 两个更新策略都关闭时，周期选项和更新提醒自动禁用。
- 手动检查始终可用。
- 手动检查按钮状态:
  - `检测更新中`
  - `已是新版本`
  - `立即更新`
  - 下载/安装进度
- 更新线程属于启动器主进程，不依赖设置窗。
- 关闭设置窗后更新继续。
- 启动器更新只重启启动器，DSH 进程不动。
- DSH 更新会停止并重启 DSH 服务，设置页显示“重启中”。

### 托盘

菜单顺序:

1. 余额
2. 分割线
3. 打开页面
4. 重启服务
5. 强制停止
6. 分割线
7. 检查更新
8. 设置
9. 分割线
10. 退出

- 点击余额打开设置里的 API 页。
- 设置按钮已加入和其他按钮一致的 hover/pressed 逻辑。
- 空闲提示格式:`DSH运行中 · 剩余 ￥x.xx`。
- Win11/Win10 圆角由 `CornerRadiusHelper` 统一。

### 关于

- 启动器版本。
- GitHub 链接。
- 打开启动器日志目录。
- 作者信息:头像、名称、B站、抖音。
- 头像只在第一次进入关于时调用 B 站 API，之后复用进程缓存。
- 支付宝、微信收款码。

---

## 四、配置文件与数据位置

### 启动器配置

`%LOCALAPPDATA%\DeepSeekHarness\LauncherSettings.json`

关键字段:

- `portMode` / `fixedPort`
- `startWithWindows`
- `silentStart`
- `theme` / `accentSource` / `accentColor` / `material`
- `updateSource`
- `launcherUpdateMode` / `dshUpdateMode` / `updateInterval`
- `lastUpdateCheckUtc`
- `lastNotifiedLauncherVersion` / `lastNotifiedDshVersion`
- `apiKeyProtected`
- `legacyApiKeyMigrationCompleted`
- 提醒开关和阈值
- `dshRoot` / `nodePath`

旧数据迁移:

- `<DSH根>\.dsh\launcher-api-key.bin`
- `<DSH根>\.dsh\.credentials.yaml`
- 启动器同目录的 `launcher.json`
- 当前开机自启动状态

### 日志

- 启动器日志:`%LOCALAPPDATA%\DeepSeekHarness\launcher.log`
- 启动早期日志:`%LOCALAPPDATA%\DeepSeekHarness\launcher-boot.log`
- 更新替换日志:`<DSH根>\logs\update.log`
- DSH 日志:`<DSH根>\logs`
- 安装器日志:`%LOCALAPPDATA%\DeepSeekHarness\installer.log`

### 更新临时目录

- `%TEMP%\DeepSeekHarnessUpdate`
- 替换前备份:`%TEMP%\DeepSeekHarnessBackup-*`

---

## 五、发布流程

### 本地构建

```powershell
cd 'G:\DeepSeek DSH\DSH Works\Project\DeepSeek Starter'
.\release.ps1
```

产物:

- `source\dist-1.4.1`
- `DeepSeekHarness-1.4.1.zip`
- 更新根目录 `manifest.json`
- 生成 `manifest-1.4.1.json`

自检:

```powershell
.\verify.ps1 -Dist '.\source\dist-1.4.1' -DshRoot 'G:\DeepSeek DSH'
```

### 发布顺序

必须按此顺序，不能颠倒:

1. 构建并更新本地 manifest。
2. 发 npm:

```powershell
.\publish-npm.ps1 -ZipPath '.\DeepSeekHarness-1.4.1.zip'
```

3. 等 npmmirror 返回 200。
4. 创建 GitHub Release、上传 zip、提交 main、推送 tag:

```powershell
.\publish-release.ps1 -Commit
```

5. 验证:
   - `raw.githubusercontent.com/.../main/manifest.json`
   - GitHub Release 资产 URL
   - npmmirror tarball URL

### 版本号位置

1.4.1 涉及:

- `source/DeepSeekHarness.csproj`
- `source/app.manifest`
- `source/RuntimeBootstrap.cs`
- `source/RuntimeBootstrap.manifest`
- `source/WinUIProgram.cs`
- `source/SettingsWindow.xaml` 的关于页显示值
- `CHANGELOG.md`

`release.ps1` 会从 csproj 读版本并更新 manifest。

---

## 六、本轮已验证

### 编译与静态自检

- `release.ps1` 构建通过。
- `verify.ps1` 通过，0 条提醒。
- 产物包含 39 个 DLL，约 37.7 MB 解压后体积。
- Windows App Runtime、.NET 8、Bootstrap.dll 检查通过。

### 更新引擎

实际调用已编译的 `UpdateSupport.PrepareStaging`:

- 从 npmmirror 下载 `dsh-launcher-1.4.0.tgz`
- 解出 zip
- SHA256 与 manifest 完全一致
- 解压后的核心程序版本是 `1.4.0.0`
- `restart-launcher.cmd` 已改为短命令，避免 `schtasks /tr` 长参数失败

1.4.1 发布时再次验证了 npm、npmmirror、GitHub 资产顺序和远端 manifest。

### 页面

以下页面做过运行时冒烟测试:

- 更新页
- 关于页
- SettingsExpander
- 关于作者头像流程

头像 API 单独验证:

- B站资料 API 返回成功
- 头像 URL 返回 103776 字节
- 进程内只缓存一次

---

## 七、真机 VM 验证状态

当前 1.4.1 已由用户在虚拟机验证通过。以下项目保留为后续版本发布前的回归清单:

1. Windows 10 1809 安装和启动。
2. Windows 11 安装和启动。
3. 从 1.3.21 升级到 1.4.x，确认配置迁移。
4. 启动器自更新完整替换和自动重启。
5. DSH 更新完整 npm 安装、重启、失败回滚。
6. 卸载器是否能识别设置页改过的 `DshRoot`。
7. 退出时异常弹窗是否彻底消失。
8. 托盘菜单 hover、pressed、设置按钮焦点是否一致。
9. Win10 直角、Win11 圆角的实际视觉。
10. Win11 7 个导航彩色图标是否都能加载。

VM 测试后重点看:

- `%LOCALAPPDATA%\DeepSeekHarness\launcher.log`
- `%LOCALAPPDATA%\DeepSeekHarness\launcher-boot.log`
- `<DSH根>\logs\update.log`
- `%LOCALAPPDATA%\DeepSeekHarness\installer.log`

---

## 八、虚拟机环境

- Windows 10 1809(17763)
- 用户:`KitamaruRamito`
- IP:`192.168.188.130`
- SSH 助手:
  `G:\DeepSeek DSH\DSH Works\.fix-lasso-state\vm-ssh.ps1 -Script '<powershell>'`
- 文件服务器:
  `vm-server.ps1`，根目录 `vm-share\`，地址 `http://192.168.188.1:8899/`
- 回滚快照后 sshd 可能被杀，跑 `fix-sshd.ps1`
- 不要连续点击两次安装

---

## 九、已知风险

1. **退出异常弹窗没有在干净 VM 上复现确认修复。**
2. **DSH 更新没有真正对一台干净 DSH 执行完整 npm 升版和回滚。**
3. **启动器自更新没有在真实旧版本上执行完整替换。**
4. **安装器 1.0.1 的 Release 还没发。**
5. **Win11 彩色 SVG 依赖 `ms-appx` 资源路径，发布包中要确认 7 个 SVG 都在。**
6. **Fluent System Icons 使用 MIT，仓库已加 THIRD-PARTY-NOTICES.md。**
7. **测试目录 `DeepSeekHarness-*/` 已加入 `.gitignore`，避免发布时误提交。**

---

## 十、继续开发前先读

1. 先读本文件顶部。
2. 再看 `RELEASE.md`。
3. 再读 `CHANGELOG.md` 的 1.4.1 和 1.4.0。
4. 修改设置 UI 前读 `SettingsWindow.xaml` 和 `SettingsWindowHost.cs`。
5. 修改更新前读 `UpdateSupport.cs`、`DshUpdateService.cs`、`UpdateUiSnapshot.cs`。
6. 修改主题/圆角前读 `LauncherAppearance.cs`、`CornerRadiusHelper.cs`。
7. 修改路径/卸载前读 `InstallerRegistration.cs` 和安装器仓库的 `ConfigStore.cs`。

---

# 历史记录:2026-09-19 旧交接

> 以下内容只用于查历史背景。旧版本号、旧待办和旧验证结论不要直接当作当前状态。

---

## 一、两个项目在哪、什么关系

| 项目 | 路径 | 干什么 |
|------|------|--------|
| **DSH Installer** | `G:\DeepSeek DSH\DSH Works\DSH Installer` | 装机程序。检测环境 → 补运行库 → 装 DSH 本体 → 部署启动器 → 建快捷方式 → 可卸载 |
| **DSH Launcher** | `G:\DeepSeek DSH\DSH Works\Project\DeepSeek Starter` | 托盘启动器(WinUI3),负责跑 DSH 本体、自更新、开机静默启动 |

**关系**:安装器**不把启动器打进包里**(那样每发一次启动器就得重发安装器)。
装的时候去读启动器仓库的 `manifest.json` 拿版本号和校验值,再去下载。

两个仓库:
- https://github.com/YunxiRamito/dsh-installer
- https://github.com/YunxiRamito/DSH-Launcher

---

## 二、本轮(2026-09-19)做完的事

全部由**虚拟机实测反馈**驱动,每条都对应一个用户看得见的现象。

### 下载这条路(本轮改动最大的部分)

**分流规则(用户定的)**:界面上选**加速线路** → 优先国内镜像,GitHub 只当兜底;
选**官方线路** → 全程官方源,一个镜像都不塞(那通常是人在墙外)。

实测数据(2026-09-19,虚拟机,同一个文件):

| 组件 | 加速线路首选 | 对照 |
|------|-------------|------|
| Git/MinGit 46 MB | 华为云 **10,527 KB/s** | github.com 17 KB/s |
| Python 10.8 MB | 华为云 **14,961 KB/s** | python.org 45 KB/s |
| 启动器 10.4 MB | npmmirror **7,365 KB/s**(1.4 秒下完) | gh-proxy 20~126 KB/s(抖) |
| pnpm 20 MB | npmmirror `@pnpm/win-x64` 的 tgz **9,822 KB/s** | github 48 KB/s |
| 运行库 | aka.ms / 微软 CDN(还行,没动) | — |

**为什么启动器最后走了 npm**:Git/Python 有华为云现成镜像,**启动器没有**。
试过两轮:
- **GitHub 加速前缀**(ghproxy 那几家):几十到一百多 KB/s,而且**轮流失效**;
- **jsDelivr**:一开始测出 1.5 MB/s 就上了,结果是**假象** ——
  它只对热门仓库的**已缓存**文件快,我们自己那个冷门仓库走它是回源 GitHub 的速度
  (实测 130 KB/s,八连接甚至 0 字节)。已撤。
- **npm/npmmirror**(现在这条):全量自动镜像,7.3 MB/s,而且**地址由版本号拼出来**,
  以后发版两边都不用改代码。

### 引导程序(Boot.exe)

- 缺运行库的确认弹窗;单根总进度条 + 阶段序号/速度/剩余时间
- WebClient **没有超时** → 加 40 秒无数据看门狗
- `windowsappruntimeinstall.exe` 装完不退出 → 改成轮询"运行库真的好用了吗"
- **TLS 1.2 必须在进程启动第一件事设置**(`ServicePointManager` 是进程级的),
  以前只在下载方法里设,导致"探测分段永远失败"
- 引导程序也走 8 连接分段下载;拿不到 Range 就回落单连接

### 安装向导

- 文案全书面语化;组件页改左右两列(必选在左);页面顺序 DSH 位置 → 组件
- 目录需要管理员权限时当场提示;`NeedsElevation` 也认用户选的目录
- 右键/Alt+F4 关闭会先回滚(`AppWindow.Closing` 订阅以前被误写在拖拽回调里)
- 计划列表只列**这次真要做的事**(快捷方式/PATH/自启没勾就不排)
- 「安装完成后立即启动」以前**根本没实现**,现在点"完成"时按复选框启动
- 开始菜单快捷方式(默认建、可取消、卸载会清)
- 三个 exe 的属性页信息统一从 `Directory.Build.props` 来

### 卸载

- **卸载向导从来没把路径传给步骤**(`session.UninstallOptions` 从来没被创建,
  于是 `new UninstallOptions()` 三个路径全 null,逐个"目录未记录,跳过",
  **一个字节没删**,而跳过只写界面不落盘)—— 这是"卸载像空壳"的真因
- **卸载器删不掉自己**(`DSH-Uninstall.exe` 躺在 DSH 根目录里、自己又在跑):
  现在拉起安装器就退出
- `Directory.Delete(recursive)` 一锤子买卖 → 改**逐项删**,一项失败不再拖累整棵
- 删不干净不再判失败(卸载语义是"能清多少清多少"),只记日志
- 注册表里记 `DshRoot`/`LauncherRoot`/`ComponentsRoot`/`PathEntries`;
  `ConfigStore.Load()` 读不到状态文件就退到注册表

### 下载引擎

- **删掉开下前测速**(分流后候选本身短,测速只是让用户干等)
- 慢下来的处理改成用户要的语义:**后台量一圈别的源,真更快(>1.2 倍)才换**,
  没有更快的就继续用当前这条;换源走 `.part` 续传
- 单连接按速率判(连续 5 秒 < 60 KB/s 触发后台测速);
  分段那条也加了同样的速率判据(注意是**速率**不是"字节数变没变"——
  八条连接慢慢爬时字节数一直在涨)

### 版本与发布

- 版本号收敛到 **`Directory.Build.props` 一处**;`WellKnown.InstallerVersion`
  改成从程序集读(以前两处硬编码,改一处漏一处)
- 安装器 `1.0.1`;GitHub Release `v1.0.0` 已发(资产换成带版本信息的那份)
- 安装器仓库加了 `.github/workflows/release.yml` + `RELEASE.md`

---

## 三、待验证(接手第一件事)

本鲸娘(上一个 AI)只做到了"代码路径通 + 单元级的地址/速度实测",
下面这些**没有完整跑通过一次**,需要真机走一遍:

1. **pnpm 从 tgz 解包** —— 只验过 `@pnpm/win-x64` 的 tgz 里确实有 `package/pnpm.exe`
   (`tar -tzf` 看过),**没验过解出来的能不能跑**。
   走的是加速线路时的必经之路。
2. **启动器走 npmmirror 的完整链路** —— 地址、速度都验了,
   但没验过"安装器读清单 → 拼 npm 地址 → 下 tgz → 解出 zip → 校验 → 解压"这条完整流程。
3. **启动器自更新** —— 刚接上 npmmirror(见启动器仓库最近一次提交),
   **一次都没跑过**。升级到旧版本再点"检查更新"就能验。
4. **安装器 1.0.1 的 Release** —— 只在虚拟机上试,还没发 Release。

跑完把 `%LOCALAPPDATA%\DeepSeekHarness\installer.log` 和启动器的 `launcher.log` 拿来看,
里面会写明用了哪个源、多少速度、从哪儿解包。

---

## 四、开发环境与虚拟机

### 编译

```powershell
# 安装器
cd 'G:\DeepSeek DSH\DSH Works\DSH Installer'
.\pack-release.ps1                 # 出包并拷到桌面
.\pack-release.ps1 -NoDesktop      # 只出包

# 启动器
cd 'G:\DeepSeek DSH\DSH Works\Project\DeepSeek Starter'
.\release.ps1                      # 出包 + 更新清单
.\publish-npm.ps1                  # 发 npm(见下)
```

本机约定:便携 SDK 在 `G:\DeepSeek DSH\.tools\dotnet`,
NuGet 缓存在 `G:\DeepSeek DSH\.nuget-packages`,代理 `127.0.0.1:7890`。
**这些路径只允许出现在脚本里且必须"存在才用"** —— 写死进仓库会让 CI 挂
(启动器的 CI 就是这么连挂八次的,见第五节)。

### 测试虚拟机

- Windows 10 1809(17763),用户 `KitamaruRamito`,`192.168.188.130`
- SSH 助手:`G:\DeepSeek DSH\DSH Works\.fix-lasso-state\vm-ssh.ps1 -Script '<powershell>'`
- 文件下发服务:`vm-server.ps1`(监听 `http://192.168.188.1:8899/`,根目录 `vm-share\`)。
  **它有时会掉**,掉了就重启它,否则推文件会静默失败(哈希打不出来)
- 推送套路:拷到 `vm-share` → VM 上 `Invoke-WebRequest http://192.168.188.1:8899/DSH-Installer-Setup.exe`
  → 比对 SHA256(推之前先杀 `DSH-Installer-Setup`/`DSH-Installer`/`node`/`DeepSeek Harness`)
- **回滚快照会杀掉 sshd**,重跑 `fix-sshd.ps1`(幂等)
- **别点两次安装**:并发会撞车(历史上撞出过 5 个野 node 和一次崩溃)

---

## 五、踩过的坑(改动前先读,能省半天)

1. **本地路径/本机设置写进仓库** → CI 必挂。启动器的 `NuGet.config` 里
   写着 `globalPackagesFolder = G:\...` 和 `http_proxy = 127.0.0.1:7890`,
   CI 上直接报"找不到路径",**连挂八次后工作流被人手动关掉**。
   规则:仓库里只放机器无关的东西,本机设置走环境变量。
2. **jsDelivr 只对热门缓存文件快**。拿别人缓存过的文件测速会得出错误结论。
   冷门仓库走它 = 回源 GitHub。
3. **"只测前三个节点"和"镜像排在最后"凑一起 = 镜像从来没被测到**
   (表现是"配了 jsdmirror 却还走 GitHub")。改动顺序相关的逻辑时,
   一定要把**排序和截断放一起看**。
4. **npm 发布到 npmmirror 有同步延迟**。安装器认的版本号来自 `manifest.json`,
   所以**发版顺序必须是**:发 npm → 等 npmmirror 就绪 → 才更新清单 →
   才发 GitHub Release。`publish-npm.ps1` 里那道闸就是干这个的。
   (安装器侧也做了兜底:探不到就催同步 + 等一会儿再试。)
5. **界面文案与逻辑写在两处会被后执行的覆盖**。卸载完成页显示"安装未完全完成"
   就是 `OnLoaded` 把 `ApplyText` 设好的标题盖掉了。
6. **卸载/回滚的"跳过"只写界面不落盘** → 日志干净得像成功。
   凡是"跳过了什么"都要落盘。
7. **csc 编出来的 exe 默认没有版本资源**。要从程序集特性生成;
   而且"原始文件名"取的是编译时的 `/out` 名字。
8. **`.ps1` 含中文要存 UTF-8 带 BOM**(Windows PowerShell 5.1);
   csc 读 .cs 同理,不给 BOM 就按 ANSI 读,中文变乱码。
9. **Job Object**(`KILL_ON_JOB_CLOSE`)是子进程清理的兜底:
   安装器被中断不会留野 node。
10. 安装器 UI 文案必须是**书面语**,代码注释可以随便说。

---

## 六、npm 通道(启动器发行)

- 包名:`@yunxiramito/dsh-launcher`(**不是** `dsh-launcher`,那个名字被别人占了)
- 版本号 = 启动器版本号;包内容 = 那份 zip
- 安装器/启动器都**由版本号拼地址**:
  `https://registry.npmmirror.com/@yunxiramito/dsh-launcher/-/dsh-launcher-<版本>.tgz`
- 发布:`publish-npm.ps1`(本机);CI 里打 tag 自动发(用仓库 secret `NPM_TOKEN`)
- npm 账号 `yunxiramito` 开了 2FA:命令行发布要么 `-Otp <6位码>`,
  要么用勾了 bypass 2FA 的 token(本机 `.npmrc` 里存了一个;
  npm 正在收紧这类 token,**长期建议改用 Trusted Publishing/OIDC**)

---

## 七、还没做、但可能想做的

- **安装器自己要不要也发 npm** —— 用户说不用,他要传蓝奏云(README 里一句话带过就行)
- **README 补一句国内下载方式**(等用户给蓝奏云链接)
- `docs\文案清单.md` 已过时(文案改过好几轮)
- 安装器仓库的 `payload\launcher.zip` 兜底**没打进正式包**
  (设计决定:断网就装不上启动器,用户明确说不搞)
- 启动器仓库的 `assets\*.zip` 每发一版会多一份 10 MB —— 攒多了要定个清理策略
- jsDelivr 那两条 mirrors 现在还有用(缓存命中的时候 4.7 MB/s),
  但**新版本发布后要等它冷缓存**,所以只能当备胎,别当主力
