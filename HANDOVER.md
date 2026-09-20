# 交接：DSH Launcher 1.4.2

> 当前启动器基线为 `1.4.2`。本文件只描述当前有效状态、操作流程、风险和下一步，
> 不再保留 1.3.x 历史开发记录。

最后更新：2026-09-21

---

## 一、当前结论

### 发布状态

| 项目 | 仓库 | 基线 | 最新 tag | 状态 |
|------|------|------|----------|------|
| DSH Launcher | `https://github.com/YunxiRamito/DSH-Launcher` | `b832345` 之后 | `v1.4.2` | 已发布 |
| DSH Installer | `https://github.com/YunxiRamito/dsh-installer` | `b550288` | `v1.0.0` | 本轮未修改 |

启动器 1.4.2：

- npm：`@yunxiramito/dsh-launcher@1.4.2`
- npmmirror：
  `https://registry.npmmirror.com/@yunxiramito/dsh-launcher/-/dsh-launcher-1.4.2.tgz`
- GitHub Release：
  `https://github.com/YunxiRamito/DSH-Launcher/releases/tag/v1.4.2`
- 资产：
  `https://github.com/YunxiRamito/DSH-Launcher/releases/download/v1.4.2/DeepSeekHarness-1.4.2.zip`
- SHA256：
  `4edb0f52b1a2b1c295a6edd54664a7cf98269a28815760a272bacaa28a03ed04`

### 两个项目的关系

- 安装器负责：检测环境、补运行库、安装 DSH 本体、下载部署启动器、快捷方式、卸载。
- 启动器负责：托盘常驻、启动和管理 DSH、余额、设置、更新、提醒和系统通知。
- 安装器不内置启动器，安装时读取启动器仓库根目录的 `manifest.json`。
- 启动器发版无需重发安装器；npm 与 GitHub Release 就绪后，新装用户会自动拿到新版。

---

## 二、代码地图

仓库根目录：

`G:\DeepSeek DSH\DSH Works\Project\DeepSeek Starter`

### 核心代码

| 文件 | 作用 |
|------|------|
| `source/WinUIProgram.cs` | 主入口、托盘、服务生命周期、更新调度、提醒、退出 |
| `source/SettingsWindow.xaml` | 设置窗口布局 |
| `source/SettingsWindow.xaml.cs` | 设置窗交互、即时保存、服务状态、更新状态 |
| `source/SettingsWindowHost.cs` | 设置窗与主进程的回调接口 |
| `source/LauncherSettings.cs` | `LauncherSettings.json` 数据模型 |
| `source/LauncherSettingsStore.cs` | 配置读写、旧配置迁移、DPAPI API Key |
| `source/LauncherAppearance.cs` | 设置窗、托盘、进度窗、通知窗共享主题材质 |
| `source/CornerRadiusHelper.cs` | Windows 10/11 圆角策略 |
| `source/UpdateSupport.cs` | 启动器清单、SemVer 比较、下载、npm tgz 解包、替换脚本 |
| `source/DshUpdateService.cs` | DSH npm 元数据、发布时间判断、tgz 下载、npm 安装与回滚 |
| `source/UpdateUiSnapshot.cs` | 设置页更新状态模型 |
| `source/BalanceSupport.cs` | API Key、余额查询 |
| `source/BalanceAlerts.cs` | 余额、消费和充值提醒 |
| `source/InstallerRegistration.cs` | 同步安装器状态文件与卸载注册表 |
| `source/BilibiliProfileService.cs` | 作者头像 API 与进程缓存 |

### 图标资源

| 路径 | 用途 |
|------|------|
| `source/assets/SettingsNavIcons/*.svg` | Windows 11 彩色 Fluent 图标 |
| `source/assets/SettingsNavIconsWin10/*.svg` | Windows 10 浅色主题图标 |
| `source/assets/SettingsNavIconsWin10Dark/*.svg` | Windows 10 深色主题图标 |

Win10 图标来自 Microsoft Fluent System Icons，`THIRD-PARTY-NOTICES.md` 已记录 MIT 来源。

---

## 三、当前功能

### 设置窗口

- 默认 `1200x720`，最小 `800x560`，可缩放、可最大化。
- 48 像素自绘标题栏，左侧 NavigationView，内容最大宽度 760。
- 页面：常规、主题、API、提醒、服务、更新、关于。
- 关闭设置窗后销毁，再次打开重建。

### 服务生命周期

- 启动时先判断更新是否到期：
  - 仅检查更新：不影响正常启动。
  - 自动下载并安装：先检查/安装更新，再启动 DSH。
- 启动器自更新时直接交棒给替换脚本，不启动 DSH。
- DSH 更新时先停止服务，安装成功后重启并等待就绪。
- 后台每 1.5 秒探测服务状态，连续两次确认后同步：
  - DSH 停止：托盘显示“启动 DSH 服务”，设置页显示“启动服务”。
  - DSH 恢复：托盘显示“重启 DSH 服务”，设置页显示“重启服务”。
- 外部停止或重新启动 DSH 后，状态会自动同步，无需重启启动器。

### 启动器更新

- 来源：`manifest.json` 和 GitHub Release。
- 支持加速源和官方源。
- 更新包支持 zip、npm tgz。
- 下载后校验 SHA256，再解压和替换。
- 进度窗显示：
  - `正在更新启动器到 vX`
  - `准备下载…`
  - `下载中`
  - `安装中 / 请不要关闭计算机`

### DSH 更新

- 读取 npm 完整元数据：
  - `dist-tags.latest`
  - 最新版 tarball
  - 每个版本的发布时间
- 更新判断优先比较发布时间，缺少时间时回退 SemVer。
- 正确识别预发布版本：
  - `0.1.5-rc.2 > 0.1.5-rc.1`
  - `0.1.5 > 0.1.5-rc.2`
- 下载阶段显示真实 `已下载/总大小`。
- 下载完成后执行本地部署，再重启 DSH。
- npm 定位顺序：
  - 内置 Node 下常见 npm-cli 布局
  - 独立 Node 相邻的 `npm.cmd`
  - DSH `node_modules/.bin/npm.cmd`
  - 系统 PATH
  - 最后直接交给 `cmd.exe` 按 PATH 解析 `npm.cmd`

### 更新完成通知

- 启动器重启并确认服务就绪后，只发一条更新完成通知。
- DSH 更新并重启成功后，只发一条更新完成通知。
- 以上通知覆盖本次普通的“服务启动成功/重启成功”通知。
- `更新提醒` 关闭时不发送更新完成通知。

### 退出

- 退出前停止定时器、注销托盘、关闭通知、停止 DSH。
- 不再调用 WinUI `Application.Exit()`。
- 清理完成后直接 `Environment.Exit(0)`，避开部分机器上的原生 `0xc0000005`。

---

## 四、配置与数据位置

### 启动器配置

`%LOCALAPPDATA%\DeepSeekHarness\LauncherSettings.json`

关键字段：

- `portMode` / `fixedPort`
- `startWithWindows` / `silentStart`
- `theme` / `accentSource` / `accentColor` / `material`
- `updateSource`
- `launcherUpdateMode` / `dshUpdateMode` / `updateInterval`
- `lastUpdateCheckUtc`
- `lastNotifiedLauncherVersion` / `lastNotifiedDshVersion`
- `apiKeyProtected`
- 提醒开关和阈值
- `dshRoot` / `nodePath`

### DSH 数据

DSH 启动时设置：

```text
DSH_HOME=<dshRoot>\.dsh
```

常见 profile：

```text
<dshRoot>\.dsh\profiles\web\package.json
<dshRoot>\.dsh\profiles\web\node_modules
```

### 日志

- 启动器：`%LOCALAPPDATA%\DeepSeekHarness\launcher.log`
- 早期启动：`%LOCALAPPDATA%\DeepSeekHarness\launcher-boot.log`
- DSH：`<dshRoot>\logs`
- 安装器：`%LOCALAPPDATA%\DeepSeekHarness\installer.log`

### 更新临时目录

- `%TEMP%\DeepSeekHarnessUpdate`
- `%TEMP%\DeepSeekHarnessBackup-*`

---

## 五、构建与发布

### 本地构建

```powershell
cd 'G:\DeepSeek DSH\DSH Works\Project\DeepSeek Starter'
.\release.ps1
```

产物：

- `source\dist-1.4.2`
- `DeepSeekHarness-1.4.2.zip`
- `manifest.json`
- `manifest-1.4.2.json`

自检：

```powershell
.\verify.ps1 -Dist '.\source\dist-1.4.2' -DshRoot 'G:\DeepSeek DSH'
```

### 发布顺序

必须按顺序执行：

1. 构建并更新 `manifest.json`。
2. 发 npm。
3. 等 npmmirror 返回 200。
4. 提交 main、推 tag、创建 GitHub Release。
5. 验证 raw manifest、GitHub 资产和 npmmirror tarball。

### 版本号位置

- `source/DeepSeekHarness.csproj`
- `source/app.manifest`
- `source/RuntimeBootstrap.cs`
- `source/RuntimeBootstrap.manifest`
- `source/WinUIProgram.cs`
- `source/SettingsWindow.xaml`
- `CHANGELOG.md`

### CI

- GitHub Actions：`.github/workflows/release.yml`
- tag 触发自动构建、发布和 Release。
- 若 npm 上已存在同名版本，CI 会跳过 npm 发布，不再报重复发布失败。
- 真正的新版本首次发布仍要求仓库 secret `NPM_TOKEN` 有效。

---

## 六、已验证状态

- 1.4.2 本机构建通过。
- `verify.ps1` 通过。
- DSH npm 发布时间判断已用真实 registry 验证。
- 加速源和官方源均能识别 `0.1.5-rc.2 > 0.1.5-rc.1`。
- DSH 主包 tgz 下载和字节进度回调已验证。
- npm 定位已验证到 `E:\Nodejs\npm.cmd`。
- 编译后的 `RunNpmInstall` 已向临时目录安装 `is-number@7.0.0`，返回成功。
- Windows 10 VM 需要复核：
  - 三套导航图标实际显示
  - 自动更新顺序
  - 服务停止/恢复状态同步
  - 退出无 `0xc0000005`
  - DSH 从 `rc.1` 更新到 `rc.2`

---

## 七、已知风险

1. Windows 10 应用内视觉与完整更新链尚未在当前交接窗口完成真机回归。
2. DSH npm 主包 tgz 很小，依赖主要在 npm 部署阶段下载；主包进度不等于完整依赖下载量。
3. `pnpm`、`npm` 等工具在不同安装环境中的目录布局不同，必须保留多候选和 PATH 兜底。
4. 更新第三方插件或依赖会执行第三方代码，UI 必须明确提示风险。
5. GitHub Search API 未认证时限流明显，插件目录需要磁盘缓存和可选 Token。

---

## 八、插件商店方案 B

参考项目：

`https://github.com/0xKcyzz/dsh-plugin-store`

### 原实现结构

它是 DSH 双面插件：

- Host：`src/index.ts` 注册 `/plugin-store/*` HTTP 路由。
- Client：`src/client/StoreTab.tsx` 注入“设置 → 插件”页面。
- 目录：GitHub Search API 搜索 `topic:dsh-plugin`，按 stars 分片、合并和缓存。
- 安装：在 Web profile 执行 `pnpm add`，再把声明 `dsh.bundle.patch` 的依赖写入 bundles。
- 卸载：修改 profile `package.json`，删除依赖/bundle，并删除 node_modules 目录。
- 更新：比较安装时记录的 `pushed_at` 与目录中的最新 `pushed_at`。
- 所有操作重启 DSH 后生效。

### 启动器原生实现目标

在设置窗新增“插件”页面，原生完成浏览、搜索、安装、更新和卸载，不要求 DSH 正在运行。

### 建议模块

| 模块 | 职责 |
|------|------|
| `PluginCatalogService` | GitHub Search、分片、归一化、分类、缓存 |
| `PluginCatalogModels` | 目录项、安装状态、更新状态 |
| `DshProfileService` | 定位 profile、读写 package.json、维护 bundles |
| `PackageManagerRunner` | 定位并执行 pnpm，处理输出、超时和错误 |
| `PluginStoreService` | 安装、更新、卸载、批量更新 |
| `PluginStorePage` | WinUI 搜索、筛选、排序、卡片和分页 |

### profile 与工具位置

默认 profile：

```text
<dshRoot>\.dsh\profiles\web
```

pnpm 定位应参考启动器 npm 定位策略：

- Node 相邻目录
- DSH 的 `.bin`
- `%APPDATA%\npm`
- 系统 PATH
- 最后交给 `cmd.exe` 解析 `pnpm.cmd`

### 安装流程

1. 用户确认安装。
2. GitHub 根 `package.json` 预检 `dsh.bundle.patch`。
3. 在 profile 目录执行 `pnpm add <spec>`。
4. 读取依赖和 package.json。
5. 只追加真实 bundle，保留 DSH 安装自带 bundle。
6. 记录仓库 `pushed_at` 和安装时间。
7. 提示“重启 DSH 后生效”。

### 卸载流程

1. 从 profile dependencies 找到依赖键。
2. 删除依赖和 bundle 项。
3. 删除 profile `node_modules/<dep>`。
4. 清理安装时间记录。
5. 提示重启。

### 更新流程

1. 读取安装元数据。
2. 比较最新 `pushed_at`。
3. 对单个插件执行安装流程覆盖更新。
4. 支持“全部更新”。

### 必须保持的规则

- 必须使用 pnpm，因为 profile 可能包含 `link:` 依赖。
- 不得删除这些安装自带 bundle：
  - `@deepseek-ai/dsh-base`
  - `@deepseek-ai/dsh-web-app`
  - headless profile 下还有 `@deepseek-ai/dsh-headless`
- 卸载不能依赖 `pnpm remove`，避免重新联网解析 git 依赖。
- 安装 URL、npm 包名、`github:owner/repo#subdir` 都要支持。

### 推荐落地顺序

1. 先实现 profile 读取、pnpm 定位和安装/卸载服务。
2. 再实现 GitHub 目录和磁盘缓存。
3. 最后做 WinUI 页面、筛选和批量更新。

---

## 九、接手第一步

1. 读本文件、`RELEASE.md`、`CHANGELOG.md` 的 1.4.2。
2. 修改设置 UI 前读 `SettingsWindow.xaml`、`SettingsWindow.xaml.cs`、`SettingsWindowHost.cs`。
3. 修改更新前读 `UpdateSupport.cs`、`DshUpdateService.cs`、`UpdateUiSnapshot.cs`。
4. 修改主题、圆角和图标前读 `LauncherAppearance.cs`、`CornerRadiusHelper.cs`。
5. 修改路径和卸载前读 `InstallerRegistration.cs` 和安装器仓库的 `ConfigStore.cs`。
6. 做插件商店方案 B 时，从 `DshProfileService` 和 `PackageManagerRunner` 开始。
