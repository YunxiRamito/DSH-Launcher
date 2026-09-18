# DeepSeek Harness Launcher

Windows WinUI 3 托盘启动器，用于以管理员权限启动本地 DSH Web 服务，并提供现代菜单、Windows 通知、余额显示和余额告警。

## 版本信息

```text
版本：1.3.6
作者：Deepseek/KitamaruRamito
公司：高性能萝卜子鲸鲸有限公司
描述：这是由高性能萝卜子编写的启动高性能萝卜子的启动器喵
```

## 运行结构

```text
DeepSeek Starter
├─ README.md
└─ source
   ├─ RuntimeBootstrap.cs
   ├─ RuntimeBootstrap.manifest
   ├─ App.xaml
   ├─ App.xaml.cs
   ├─ WinUIProgram.cs
   ├─ BalanceSupport.cs
   ├─ BalanceAlerts.cs
   ├─ DeepSeekHarness.csproj
   ├─ app.manifest
   ├─ build-winui.ps1
   ├─ make-icon.ps1
   ├─ NuGet.config
   ├─ assets
   ├─ legacy
   └─ dist
      ├─ DeepSeek Harness.exe             运行库检查与启动引导
      ├─ DeepSeek Harness.Core.exe        WinUI 3 主程序
      ├─ Microsoft.Windows.SDK.NET.dll
      ├─ Microsoft.WinUI.dll
      └─ Microsoft.WindowsAppRuntime.Bootstrap.dll
```

## 启动行为

1. 外层 `DeepSeek Harness.exe` 是小体积 .NET Framework 引导程序，清单声明 `requireAdministrator`。
2. 引导程序检查 `.NET 8 Desktop Runtime` 和 `Windows App Runtime 1.8`。
3. 缺少运行库时，弹出 Windows 原生提示框并显示微软官方下载地址。
4. 依赖齐全后启动同目录的 `DeepSeek Harness.Core.exe`，不会再次请求第二遍 UAC。
5. 主程序使用管理员令牌启动 Node，因此 DSH 服务也以管理员权限运行。
6. 使用命名 Mutex 保证单实例。
7. 检测 `127.0.0.1:8787` 是否已有 DSH。
8. 隐藏命令行启动 DSH Web 服务，并从输出中捕获 Token URL。
9. 使用 Windows 原生托盘气泡提示启动结果，并在系统通知区保留 DeepSeek 图标。

程序已经在运行时再次双击快捷方式，会通知原进程打开页面。

## 命令行参数

```text
--no-browser / --silent / --tray / --startup
    静默自启：正常拉起服务并驻留托盘，但不自动弹出浏览器页面。
    （托盘菜单里的「开机自启动」写的就是 --no-browser）
--elevated
    内部参数，由程序自己提权重启时追加，用户不用管。
```

参数由外层引导程序 `DeepSeek Harness.exe` 透传给内核 `DeepSeek Harness.Core.exe`。
带 `--no-browser` 启动时，启动成功的托盘气泡照常弹，只是不打开 `127.0.0.1:8787`。

## 开机自启

托盘右键菜单里的「开机自启动」可以直接开关，开启状态显示为「开机自启动 ✓」。

开关会同时维护两处，保证在任务管理器的「启动应用」里也是启用状态：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run         值名 DeepSeek Harness
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\DeepSeek Harness.lnk
```

写入的命令固定带 `--no-browser`，所以开机只驻留托盘，不会蹦出浏览器。

## WinUI 3 生命周期

`App.xaml` 负责创建 WinUI 应用资源和 `Application.Resources`。`Program.Main` 只负责管理员权限、单实例、服务事件以及初始化 WinUI 消息循环：

```text
Main
  -> Application.Start
  -> new App()
  -> App.OnLaunched
  -> Program.OnApplicationLaunched
  -> LauncherContext.Start
```

托盘、服务管理、余额刷新和 API 设置窗口都延迟到 `App.OnLaunched` 之后才创建，避免在 WinUI `Application` 和 `XamlControlsResources` 尚未完成初始化时访问控件。

API 设置窗口使用 WinUI 3 `Window`、`PasswordBox`、`CheckBox` 和 `Button`。旧的 WinForms `ApiSettingsDialog` 已移除；当前窗口的显示、居中和关闭都由 WinUI 生命周期管理。

## WinUI 3 托盘菜单

托盘图标仍由 Windows 通知区托管，右键菜单由独立的 WinUI 3 无边框云母窗口实现：

```text
余额：¥xx.xx
────────────
打开页面
重启 DSH 服务
────────────
强行终止
退出
```

菜单使用 Segoe Fluent Icons，并沿用 Windows 的浅色/深色主题。`MicaBackdrop` 直接挂在 WinUI 3 `Window` 上，窗口本体由 `OverlappedPresenter` 去掉标题栏和边框，DWM 边框颜色设为 `NONE`，因此不会再叠加旧版的粗描边。

动画不再使用 `AnimateWindow`。窗口显示前先停止旧动画并将菜单内容隐藏，禁用一个 DWM 隐式过渡，然后仅由 WinUI Composition 播放一次 `Offset +16px -> 0` 与透明度过渡。关闭菜单、重新打开菜单和点击菜单项都会递增动画序号，确保过期动画不会重复播放。

菜单项点击后会先关闭菜单，再执行打开页面、重启、终止或退出动作。程序同时监听前台窗口和全局鼠标按键，点击其他应用、任务栏或桌面都会关闭菜单。

## 通知

通知只使用 Windows 原生托盘气泡 `Shell_NotifyIcon`。程序依次尝试大图标自定义气泡、普通自定义图标气泡和 Windows 默认信息气泡，自绘 WinUI 通知窗不会参与显示。

通知注册状态会写入：

```text
G:\DeepSeek DSH\logs\launcher.log
```

示例日志：

```text
Windows App SDK notifications registered=True, setting=Unsupported
```

`setting=Unsupported` 表示系统不允许当前未打包/提权进程直接显示 Windows 系统 Toast，不影响托盘气泡。

Windows 的“专注助手”、系统通知设置或组策略仍可能在系统层面隐藏通知，这是 Windows 的全局行为。

右键菜单的 WinUI `MenuFlyout` 使用一个完全透明的 1×1 锚点窗口，因此不会再出现鼠标右下角的黑色方块。

## 余额与告警

菜单顶部显示当前余额。刷新时机：

- 程序启动
- 每次打开托盘菜单
- 每 60 秒自动刷新

点击余额项可修改 API Key，设置窗口使用 WinUI 3 控件。Key 使用当前用户 DPAPI 加密保存：

```text
G:\DeepSeek DSH\.dsh\launcher-api-key.bin
```

程序不会直接改写 DSH 的 `.credentials.yaml`。

默认告警阈值：

```text
今日花费达到 ¥15.00
余额降至 ¥10.00
余额降至 ¥5.00
余额降至 ¥1.00
```

告警状态保存在：

```text
G:\DeepSeek DSH\.dsh\launcher-alerts.json
```

## 管理员权限

管理员权限由：

```text
source\app.manifest
source\RuntimeBootstrap.manifest
```

中的以下节点声明：

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

引导程序和主程序都声明管理员权限。引导程序以管理员令牌启动主程序后，Node 子进程继续继承管理员令牌。

检查当前服务是否由管理员启动，可查看日志：

```text
Launcher started. Version 1.3.6, elevated=True
Started elevated node process <PID>.
```

## 构建

本机 SDK 位置：

```text
G:\DeepSeek DSH\.tools\dotnet\dotnet.exe
```

NuGet 缓存位置：

```text
G:\DeepSeek DSH\.nuget-packages
```

构建命令：

```powershell
Set-Location 'G:\DeepSeek DSH\DSH Works\Project\DeepSeek Starter\source'
.\build-winui.ps1
```

输出：

```text
source\dist\
├─ DeepSeek Harness.exe
├─ DeepSeek Harness.Core.exe
└─ 其他 .NET / Windows App SDK 依赖 DLL
```

当前使用框架依赖发布：

```xml
<WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
<SelfContained>false</SelfContained>
<PublishSingleFile>false</PublishSingleFile>
```

完整目录约 `37.16 MiB`，不会再因为单文件自解压向 `%TEMP%\.net` 写入约 282 MiB 的缓存。

## 运行库缺失提示

引导程序会检查：

```text
.NET 8 Desktop Runtime
Windows App Runtime 1.8，最低版本 8000.946.1701.0
```

缺失时显示原生 MessageBox，并提供官方下载地址：

```text
.NET 8:
https://dotnet.microsoft.com/download/dotnet/8.0

Windows App Runtime 1.8:
https://aka.ms/windowsappsdk/1.8/1.8.260804001/windowsappruntimeinstall-x64.exe
```

用户选择“是”后，程序会依次打开缺失运行库的下载页面。

## 日志

主日志：

```text
G:\DeepSeek DSH\logs\launcher.log
```

最近一次 Token URL：

```text
G:\DeepSeek DSH\logs\last-url.txt
```

日志会隐藏 URL 中的 Token；API Key 不会写入日志。

## 旧版源码

早期 .NET Framework/WinForms 编译产物保留在：

```text
source\legacy
```

当前构建不会编译旧版 `Program.cs`。

项目仍保留 `Microsoft.WindowsDesktop.App.WindowsForms` 框架引用，用于兼容旧代码中的托盘气泡、`MessageBox` 和 WinForms 应用 API，但主启动器实际运行在 WinUI 3 上。当前版本要求 Windows App Runtime 1.8，最低支持 Windows 10 1809；Windows 7、8 和 8.1 不在当前 WinUI 3 版本的运行范围内。如果确实需要这些旧系统，需要单独维护一个纯 WinForms 启动器分支。
