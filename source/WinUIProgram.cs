using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinFormsApplication = System.Windows.Forms.Application;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;
using WinFormsMessageBox = System.Windows.Forms.MessageBox;
using WinFormsMessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using WinFormsMessageBoxIcon = System.Windows.Forms.MessageBoxIcon;
using WinUIApplication = Microsoft.UI.Xaml.Application;
using WinUIWindow = Microsoft.UI.Xaml.Window;

namespace DeepSeekHarnessLauncher
{
    internal static class Constants
    {
        public const string Title = "DeepSeek Harness";
        public const string Version = "1.3.9";
        public const string Url = "http://127.0.0.1:8787/";
        public const string DefaultRoot = @"G:\DeepSeek DSH";
        public const string DefaultNode = @"E:\Nodejs\node.exe";
        public const int Port = 8787;
        public const int TrayIconId = 1;
    }

    internal static class Program
    {
        private const string MutexName = @"Local\DeepSeekHarness.Launcher";
        private const string OpenPageEventName = @"Local\DeepSeekHarness.OpenPage";
        private const string ElevatedArgument = "--elevated";

        // 自启参数：带上这些参数启动时只驻留托盘，不自动弹浏览器
        private static readonly string[] NoBrowserArguments = new string[]
        {
            "--no-browser",
            "-no-browser",
            "--silent",
            "-silent",
            "--tray",
            "-tray",
            "--startup",
            "-startup"
        };

        private static LauncherContext _currentContext;
        private static App _application;
        private static EventWaitHandle _pendingOpenPageEvent;
        private static bool _noBrowser;

        internal static bool NoBrowser
        {
            get { return _noBrowser; }
        }

        [STAThread]
        private static void Main()
        {
            _noBrowser = HasAnyCommandLineArgument(NoBrowserArguments);

            WinFormsApplication.EnableVisualStyles();
            WinFormsApplication.SetCompatibleTextRenderingDefault(false);

            if (!IsAdministrator())
            {
                RelaunchElevated();
                return;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalExistingInstance();
                    return;
                }

                using (EventWaitHandle openPageEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    OpenPageEventName))
                {
                    _pendingOpenPageEvent = openPageEvent;
                    WinRT.ComWrappersSupport.InitializeComWrappers();
                    WinUIApplication.Start(delegate(ApplicationInitializationCallbackParams parameters)
                    {
                        DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                        SynchronizationContext.SetSynchronizationContext(
                            new DispatcherQueueSynchronizationContext(dispatcherQueue));
                        _application = new App();
                    });
                    _pendingOpenPageEvent = null;
                }

                GC.KeepAlive(mutex);
            }
        }

        internal static void OnApplicationLaunched()
        {
            if (_pendingOpenPageEvent == null)
            {
                return;
            }

            DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            EnsureXamlControlsResources();
            _currentContext = new LauncherContext(_pendingOpenPageEvent, dispatcherQueue);
            _currentContext.Start();
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void RelaunchElevated()
        {
            bool alreadyElevatedAttempt = Array.IndexOf(
                Environment.GetCommandLineArgs(),
                ElevatedArgument) >= 0;
            if (alreadyElevatedAttempt)
            {
                WinFormsMessageBox.Show(
                    "无法获得管理员权限，DeepSeek Harness 不能启动。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Error);
                return;
            }

            try
            {
                string executablePath = Environment.ProcessPath;
                if (String.IsNullOrEmpty(executablePath))
                {
                    executablePath = WinFormsApplication.ExecutablePath;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = BuildElevatedArguments(),
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                WinFormsMessageBox.Show(
                    "请求管理员权限失败：" + exception.Message,
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Error);
            }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using (EventWaitHandle openPageEvent = EventWaitHandle.OpenExisting(OpenPageEventName))
                {
                    openPageEvent.Set();
                    return;
                }
            }
            catch
            {
            }

            OpenPage();
        }

        internal static void OpenPage()
        {
            OpenPage(ReadLastUrl());
        }

        internal static void OpenPage(string url)
        {
            try
            {
                if (String.IsNullOrEmpty(url))
                {
                    url = Constants.Url;
                }

                string explorer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe");
                if (File.Exists(explorer))
                {
                    ProcessStartInfo explorerStart = new ProcessStartInfo
                    {
                        FileName = explorer,
                        Arguments = QuoteArgument(url),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(explorerStart);
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                };
                Process.Start(startInfo);
            }
            catch
            {
            }
        }

        internal static string ReadLastUrl()
        {
            try
            {
                string path = Path.Combine(Constants.DefaultRoot, @"logs\last-url.txt");
                if (!File.Exists(path))
                {
                    return Constants.Url;
                }

                string value = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (value.StartsWith("http://127.0.0.1:8787/", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
            catch
            {
            }

            return Constants.Url;
        }

        internal static string QuoteArgument(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static bool HasCommandLineArgument(string expected)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++)
            {
                if (String.Equals(arguments[index], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasAnyCommandLineArgument(string[] expected)
        {
            for (int index = 0; index < expected.Length; index++)
            {
                if (HasCommandLineArgument(expected[index]))
                {
                    return true;
                }
            }

            return false;
        }

        // 开机自启统一使用 --no-browser,启动后只驻留托盘,不弹浏览器
        internal static string BuildStartupArguments()
        {
            return "--no-browser";
        }

        // 提权重启时把原来的自启参数带上,否则提权后的实例会又去弹浏览器
        private static string BuildElevatedArguments()
        {
            StringBuilder builder = new StringBuilder();
            string[] rawArguments = Environment.GetCommandLineArgs();
            for (int index = 1; index < rawArguments.Length; index++)
            {
                string value = rawArguments[index];
                if (String.Equals(value, ElevatedArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(QuoteArgument(value));
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(ElevatedArgument);
            return builder.ToString();
        }

        private static void EnsureXamlControlsResources()
        {
            try
            {
                foreach (ResourceDictionary dictionary in
                    WinUIApplication.Current.Resources.MergedDictionaries)
                {
                    if (dictionary is Microsoft.UI.Xaml.Controls.XamlControlsResources)
                    {
                        return;
                    }
                }

                WinUIApplication.Current.Resources.MergedDictionaries.Add(
                    new Microsoft.UI.Xaml.Controls.XamlControlsResources());
            }
            catch
            {
            }
        }

    }

    /// <summary>
    /// 开机自启支持:同时维护注册表 Run 键和“启动”文件夹快捷方式。
    /// 启用时会把 --no-browser 一起写进去,开机只驻留托盘,不弹浏览器。
    /// </summary>
    internal static class StartupSupport
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "DeepSeek Harness";
        private const string ShortcutName = "DeepSeek Harness.lnk";
        private const string ApproveKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
        private const string LogPath = @"G:\DeepSeek DSH\logs\launcher.log";

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(
                    LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [startup] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string GetLauncherPath()
        {
            string processPath = Environment.ProcessPath;
            if (String.IsNullOrEmpty(processPath))
            {
                processPath = WinFormsApplication.ExecutablePath;
            }

            FileInfo info = new FileInfo(processPath);

            // 优先指向外层引导程序 “DeepSeek Harness.exe”,它带 requireAdministrator 清单,
            // 双击时直接弹一次 UAC;Core.exe 只是它的子进程。
            string bootstrapPath = Path.Combine(info.DirectoryName, "DeepSeek Harness.exe");
            if (File.Exists(bootstrapPath))
            {
                return bootstrapPath;
            }

            return processPath;
        }

        private static string ShortcutPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    ShortcutName);
            }
        }

        private static string BuildRunCommand()
        {
            return Program.QuoteArgument(GetLauncherPath())
                + " "
                + Program.BuildStartupArguments();
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key != null && key.GetValue(RunValueName) != null)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return File.Exists(ShortcutPath);
        }

        public static bool Enable()
        {
            bool runKeyWritten = false;
            bool shortcutWritten = false;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    key.SetValue(RunValueName, BuildRunCommand(), RegistryValueKind.String);
                }

                runKeyWritten = true;
            }
            catch (Exception exception)
            {
                Log("写 Run 键失败: " + exception.Message);
            }

            try
            {
                CreateShortcut();
                shortcutWritten = true;
            }
            catch (Exception exception)
            {
                Log("写启动文件夹失败: " + exception.Message);
            }

            Log(
                "开机自启启用 runKey=" + runKeyWritten.ToString()
                + " shortcut=" + shortcutWritten.ToString()
                + " command=" + BuildRunCommand());

            return runKeyWritten || shortcutWritten;
        }

        public static bool Disable()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null && key.GetValue(RunValueName) != null)
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch (Exception exception)
            {
                Log("删 Run 键失败: " + exception.Message);
            }

            try
            {
                string path = ShortcutPath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                Log("删启动文件夹快捷方式失败: " + exception.Message);
            }

            Log("开机自启已关闭。");
            return !IsEnabled();
        }

        private static void CreateShortcut()
        {
            string shortcutPath = ShortcutPath;
            string targetPath = GetLauncherPath();
            string arguments = Program.BuildStartupArguments();
            string workingDirectory = Path.GetDirectoryName(targetPath);

            // 用 WScript.Shell 生成 .lnk,免去手写 ShellLink COM 结构
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("WScript.Shell 不可用");
            }

            object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember(
                    "TargetPath",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { targetPath });
                shortcutType.InvokeMember(
                    "Arguments",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { arguments });
                shortcutType.InvokeMember(
                    "WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { workingDirectory });
                shortcutType.InvokeMember(
                    "IconLocation",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { targetPath + ",0" });
                shortcutType.InvokeMember(
                    "Description",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { "DeepSeek Harness 开机自启(静默驻留托盘)" });
                shortcutType.InvokeMember(
                    "Save",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shortcut,
                    null);
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    Marshal.ReleaseComObject(shell);
                }
            }

            MarkShortcutApproved();
        }

        // Windows 的“启动应用”开关把状态记在这里,不写的话可能出现“已禁用”的灰点
        private static void MarkShortcutApproved()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ApproveKeyPath))
                {
                    byte[] enabled = new byte[12];
                    enabled[0] = 2;
                    key.SetValue(ShortcutName, enabled, RegistryValueKind.Binary);
                }
            }
            catch (Exception exception)
            {
                Log("写启动批准项失败: " + exception.Message);
            }
        }
    }

    internal sealed class LauncherContext
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly EventWaitHandle _openPageEvent;
        private readonly RegisteredWaitHandle _openPageWait;
        private readonly string _root;
        private readonly string _nodePath;
        private readonly string _dshBin;
        private readonly string _logPath;
        private readonly string _lastUrlPath;
        private readonly string _apiPromptedPath;
        private readonly Icon _appIcon;
        private readonly NativeTrayIcon _trayIcon;
        private readonly WinUICompositionTrayMenu _trayMenu;
        private readonly DispatcherQueueTimer _balanceTimer;
        private readonly DeepSeekBalanceAlertTracker _balanceAlertTracker;
        private readonly object _logLock = new object();
        private readonly StreamWriter _logWriter;

        private Process _service;
        private DispatcherQueueTimer _apiPromptTimer;
        private string _apiKey;
        private string _lastBalanceText;
        private string _balanceToolTip = "点击修改 API 设置。";
        private DateTime _lastBalanceUpdatedUtc;
        private int _balanceRequestInProgress;
        private bool _serviceRunning;
        private bool _startupInProgress;
        private bool _pendingOpenAfterStartup;
        private bool _suppressExitNotification;
        private bool _exiting;
        private bool _apiSettingsOpen;
        private DateTime _lastMenuRefreshUtc;
        private string _serviceUrl;

        public LauncherContext(EventWaitHandle openPageEvent, DispatcherQueue dispatcherQueue)
        {
            _openPageEvent = openPageEvent;
            _dispatcherQueue = dispatcherQueue;
            _root = FindRoot();
            _nodePath = FindNode();
            _dshBin = Path.Combine(_root, @"node_modules\@deepseek-ai\dsh\lib\bin.js");

            string logDirectory = Path.Combine(_root, "logs");
            Directory.CreateDirectory(logDirectory);
            _logPath = Path.Combine(logDirectory, "launcher.log");
            _lastUrlPath = Path.Combine(logDirectory, "last-url.txt");
            _apiPromptedPath = Path.Combine(logDirectory, "api-settings-prompted.flag");
            _serviceUrl = Program.ReadLastUrl();
            _apiKey = CredentialStore.ReadApiKey(_root);
            _balanceAlertTracker = new DeepSeekBalanceAlertTracker(
                Path.Combine(_root, @".dsh\launcher-alerts.json"),
                Path.Combine(_root, @".dsh\.dshw-usage.json"));
            _logWriter = new StreamWriter(_logPath, true, new UTF8Encoding(false));
            _logWriter.AutoFlush = true;
            WriteLog("Launcher started. Version " + Constants.Version + ", elevated=" + IsAdministrator());

            _appIcon = LoadAppIcon();
            _trayIcon = new NativeTrayIcon(Constants.TrayIconId, Constants.Title + " 正在启动...", _appIcon);
            _trayMenu = new WinUICompositionTrayMenu();
            _trayMenu.BalanceClicked += BalanceItemClick;
            _trayMenu.OpenClicked += OpenItemClick;
            _trayMenu.RestartClicked += RestartItemClick;
            _trayMenu.StartupClicked += StartupItemClick;
            _trayMenu.ForceStopClicked += ForceStopItemClick;
            _trayMenu.ExitClicked += ExitItemClick;

            _trayIcon.ContextMenuRequested += TrayContextMenuRequested;
            _trayIcon.DoubleClick += TrayDoubleClick;

            bool notificationRegistered = NotificationService.Initialize(_trayIcon);
            WriteLog(
                "Windows App SDK notifications registered="
                + notificationRegistered.ToString()
                + ", setting="
                + NotificationService.SettingName);

            _balanceTimer = _dispatcherQueue.CreateTimer();
            _balanceTimer.Interval = TimeSpan.FromMinutes(1);
            _balanceTimer.IsRepeating = true;
            _balanceTimer.Tick += delegate(DispatcherQueueTimer sender, object args)
            {
                RefreshBalanceAsync();
            };

            _openPageWait = ThreadPool.RegisterWaitForSingleObject(
                _openPageEvent,
                delegate(object state, bool timedOut)
                {
                    if (!timedOut)
                    {
                        InvokeOnUi(OpenServicePage);
                    }
                },
                null,
                Timeout.Infinite,
                false);

            if (String.IsNullOrEmpty(_apiKey))
            {
                SetBalanceUnconfigured();
                PromptForApiKeyOnFirstRun();
            }
            else
            {
                SetBalanceLoading();
                RefreshBalanceAsync();
            }
        }

        public void Start()
        {
            _balanceTimer.Start();
            Thread startupThread = new Thread(StartupThreadProc);
            startupThread.IsBackground = true;
            startupThread.Name = "DeepSeekHarnessStartup";
            startupThread.Start();
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private void TrayContextMenuRequested()
        {
            _trayMenu.SetRunning(_serviceRunning);
            _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
            _trayMenu.SetStartupState(StartupSupport.IsEnabled());
            _trayMenu.ShowAtCursor();
            if ((DateTime.UtcNow - _lastMenuRefreshUtc).TotalSeconds >= 5.0)
            {
                _lastMenuRefreshUtc = DateTime.UtcNow;
                RefreshBalanceAsync();
            }
        }

        private void StartupItemClick()
        {
            bool enabled = StartupSupport.IsEnabled();
            bool changed;
            if (enabled)
            {
                changed = StartupSupport.Disable();
                _trayMenu.SetStartupState(false);
                WriteLog("Startup entry disabled by user.");
                ShowNotification(
                    changed
                        ? "已关闭开机自启动。"
                        : "关闭开机自启动失败，请看日志。",
                    !changed);
                return;
            }

            changed = StartupSupport.Enable();
            _trayMenu.SetStartupState(changed);
            WriteLog("Startup entry enabled by user, result=" + changed.ToString());
            ShowNotification(
                changed
                    ? "已开启开机自启动，开机后只驻留托盘，不弹浏览器。"
                    : "开启开机自启动失败，请看日志。",
                !changed);
        }

        private void TrayDoubleClick()
        {
            OpenServicePage();
        }

        private void BalanceItemClick()
        {
            ShowApiSettings();
        }

        private void OpenItemClick()
        {
            OpenServicePage();
        }

        private void RestartItemClick()
        {
            WinFormsDialogResult result = WinFormsMessageBox.Show(
                "确定要重启 DeepSeek Harness 服务吗？当前网页连接会暂时中断。",
                Constants.Title,
                WinFormsMessageBoxButtons.OKCancel,
                WinFormsMessageBoxIcon.Question,
                System.Windows.Forms.MessageBoxDefaultButton.Button2);
            if (result != WinFormsDialogResult.OK)
            {
                return;
            }

            _trayMenu.SetRunning(false);
            _trayIcon.UpdateTip(Constants.Title + " 正在重启...");
            ShowNotification("正在重启 DeepSeek Harness 服务...", false);

            Thread restartThread = new Thread(RestartThreadProc);
            restartThread.IsBackground = true;
            restartThread.Name = "DeepSeekHarnessRestart";
            restartThread.Start();
        }

        private void ForceStopItemClick()
        {
            WinFormsDialogResult result = WinFormsMessageBox.Show(
                "确定要强行终止 DeepSeek Harness 服务吗？",
                Constants.Title,
                WinFormsMessageBoxButtons.OKCancel,
                WinFormsMessageBoxIcon.Warning,
                System.Windows.Forms.MessageBoxDefaultButton.Button2);
            if (result != WinFormsDialogResult.OK)
            {
                return;
            }

            if (StopService())
            {
                SetTrayState(false, Constants.Title + " 已停止");
                ShowNotification("DeepSeek Harness 服务已被强行终止。", true);
            }
            else
            {
                ShowNotification("没有找到正在运行的 DeepSeek Harness 服务。", true);
            }
        }

        private void ExitItemClick()
        {
            if (_serviceRunning)
            {
                WinFormsDialogResult result = WinFormsMessageBox.Show(
                    "退出托盘程序将同时停止 DeepSeek Harness 服务。是否继续？",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OKCancel,
                    WinFormsMessageBoxIcon.Question,
                    System.Windows.Forms.MessageBoxDefaultButton.Button2);
                if (result != WinFormsDialogResult.OK)
                {
                    return;
                }

                StopService();
            }

            ExitApplication();
        }

        private void PromptForApiKeyOnFirstRun()
        {
            if (!String.IsNullOrEmpty(_apiKey) || File.Exists(_apiPromptedPath))
            {
                return;
            }

            _apiPromptTimer = _dispatcherQueue.CreateTimer();
            _apiPromptTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _apiPromptTimer.IsRepeating = false;
            _apiPromptTimer.Tick += delegate(DispatcherQueueTimer sender, object args)
            {
                sender.Stop();
                if (!String.IsNullOrEmpty(_apiKey) || File.Exists(_apiPromptedPath))
                {
                    return;
                }

                try
                {
                    File.WriteAllText(
                        _apiPromptedPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        new UTF8Encoding(false));
                }
                catch
                {
                }

                ShowApiSettings();
            };
            _apiPromptTimer.Start();
        }

        private async void ShowApiSettings()
        {
            if (_apiSettingsOpen)
            {
                return;
            }

            _apiSettingsOpen = true;
            try
            {
                WinUIApiSettingsDialog dialog = new WinUIApiSettingsDialog(_apiKey);
                WinUIApiSettingsResult settings = await dialog.ShowAsync();
                if (!settings.Confirmed)
                {
                    return;
                }

                string apiKey = settings.ApiKey;
                try
                {
                    bool apiKeyChanged = !String.Equals(_apiKey, apiKey, StringComparison.Ordinal);
                    CredentialStore.SaveApiKey(_root, apiKey);
                    _apiKey = apiKey;
                    _lastBalanceText = null;
                    _lastBalanceUpdatedUtc = DateTime.MinValue;
                    if (apiKeyChanged)
                    {
                        _balanceAlertTracker.Reset();
                    }

                    if (String.IsNullOrEmpty(_apiKey))
                    {
                        SetBalanceUnconfigured();
                    }
                    else
                    {
                        SetBalanceLoading();
                        RefreshBalanceAsync();
                    }
                }
                catch (Exception exception)
                {
                    WriteLog("API settings save failed: " + exception);
                    WinFormsMessageBox.Show(
                        "API 设置保存失败：" + exception.Message,
                        Constants.Title,
                        WinFormsMessageBoxButtons.OK,
                        WinFormsMessageBoxIcon.Error);
                }
            }
            catch (Exception exception)
            {
                WriteLog("API settings dialog failed: " + exception);
                WinFormsMessageBox.Show(
                    "无法打开 API 设置：" + exception.Message,
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Error);
            }
            finally
            {
                _apiSettingsOpen = false;
            }
        }

        private void RefreshBalanceAsync()
        {
            if (String.IsNullOrEmpty(_apiKey))
            {
                SetBalanceUnconfigured();
                return;
            }

            if (Interlocked.CompareExchange(ref _balanceRequestInProgress, 1, 0) != 0)
            {
                return;
            }

            if (String.IsNullOrEmpty(_lastBalanceText))
            {
                SetBalanceLoading();
            }

            Thread balanceThread = new Thread(BalanceThreadProc);
            balanceThread.IsBackground = true;
            balanceThread.Name = "DeepSeekHarnessBalance";
            balanceThread.Start();
        }

        private void BalanceThreadProc()
        {
            BalanceResult result;
            BalanceAlertUpdate alertUpdate = null;
            try
            {
                result = DeepSeekBalanceClient.Fetch(_apiKey);
                if (result.Ok)
                {
                    alertUpdate = _balanceAlertTracker.Observe(result);
                }
            }
            catch (Exception exception)
            {
                result = new BalanceResult
                {
                    Ok = false,
                    Error = "余额查询失败：" + exception.Message
                };
            }

            WriteLog(result.Ok
                ? "Balance refreshed: " + result.Display
                : "Balance refresh failed: " + result.Error);

            InvokeOnUi(delegate()
            {
                ApplyBalanceResult(result, alertUpdate);
            });
            Interlocked.Exchange(ref _balanceRequestInProgress, 0);
        }

        private void ApplyBalanceResult(BalanceResult result, BalanceAlertUpdate alertUpdate)
        {
            if (result.Ok)
            {
                _lastBalanceText = "余额：" + result.Display;
                _lastBalanceUpdatedUtc = result.UpdatedAtUtc;
                _balanceToolTip = "点击修改 API 设置。更新时间："
                    + result.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss");
                if (alertUpdate != null && alertUpdate.IsCny)
                {
                    _balanceToolTip += "；今日已用：" + FormatMoney(alertUpdate.TodaySpend);
                }

                _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
                _trayMenu.SetRunning(_serviceRunning);

                if (alertUpdate != null && !String.IsNullOrEmpty(alertUpdate.Notification))
                {
                    ShowNotification(alertUpdate.Notification, alertUpdate.Critical);
                }

                return;
            }

            if (!String.IsNullOrEmpty(_lastBalanceText))
            {
                _balanceToolTip = "点击修改 API 设置。刷新失败：" + result.Error;
                _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
                return;
            }

            _lastBalanceText = "余额：查询失败（点击设置 API）";
            _balanceToolTip = result.Error;
            _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
        }

        private static string FormatMoney(decimal amount)
        {
            return "¥" + amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void SetBalanceLoading()
        {
            if (String.IsNullOrEmpty(_lastBalanceText))
            {
                _lastBalanceText = "余额：正在查询...";
            }

            _balanceToolTip = "正在从 DeepSeek 查询余额，点击可修改 API 设置。";
            _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
        }

        private void SetBalanceUnconfigured()
        {
            _lastBalanceText = "余额：未配置（点击设置 API）";
            _balanceToolTip = "点击后填写 DeepSeek API Key，保存后自动刷新余额。";
            _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
        }

        private static string FindRoot()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string localBin = Path.Combine(baseDirectory, @"node_modules\@deepseek-ai\dsh\lib\bin.js");
            if (File.Exists(localBin))
            {
                return baseDirectory;
            }

            return Constants.DefaultRoot;
        }

        private static string FindNode()
        {
            if (File.Exists(Constants.DefaultNode))
            {
                return Constants.DefaultNode;
            }

            string pathValue = Environment.GetEnvironmentVariable("PATH");
            if (!String.IsNullOrEmpty(pathValue))
            {
                string[] directories = pathValue.Split(';');
                for (int index = 0; index < directories.Length; index++)
                {
                    string candidate = Path.Combine(directories[index], "node.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return Constants.DefaultNode;
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("AppIcon.ico"))
                {
                    if (stream != null)
                    {
                        using (Icon icon = new Icon(stream))
                        {
                            return (Icon)icon.Clone();
                        }
                    }
                }
            }
            catch
            {
            }

            return SystemIcons.Application;
        }

        private void StartupThreadProc()
        {
            _startupInProgress = true;
            WriteLog("Checking whether the service is already available.");
            if (IsServiceReady())
            {
                FinishSuccessfulStartup(true);
                return;
            }

            _serviceUrl = null;
            try
            {
                if (!StartService())
                {
                    FailStartup("无法创建 DeepSeek Harness 服务进程。");
                    return;
                }

                string failureMessage;
                if (!WaitForServiceReady(out failureMessage))
                {
                    _suppressExitNotification = true;
                    StopService();
                    FailStartup(failureMessage);
                    return;
                }

                FinishSuccessfulStartup(true);
            }
            catch (Exception exception)
            {
                _suppressExitNotification = true;
                StopService();
                FailStartup("DeepSeek Harness 服务启动失败：" + exception.Message + Environment.NewLine + "日志：" + _logPath);
            }
        }

        private bool WaitForServiceReady(out string failureMessage)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (_service != null)
                {
                    try
                    {
                        if (_service.HasExited)
                        {
                            failureMessage = "DeepSeek Harness 服务启动失败，进程已退出。退出代码：" + _service.ExitCode;
                            return false;
                        }
                    }
                    catch
                    {
                    }
                }

                if (IsServiceReady())
                {
                    failureMessage = null;
                    return true;
                }

                Thread.Sleep(500);
            }

            failureMessage = "DeepSeek Harness 服务启动超时。请查看日志：" + _logPath;
            return false;
        }

        private bool StartService()
        {
            if (!File.Exists(_nodePath))
            {
                throw new FileNotFoundException("找不到 Node.js：" + _nodePath);
            }

            if (!File.Exists(_dshBin))
            {
                throw new FileNotFoundException("找不到 DSH 入口文件：" + _dshBin);
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = _nodePath;
            startInfo.Arguments = Program.QuoteArgument(_dshBin)
                + " web --port "
                + Constants.Port.ToString()
                + " --no-open";
            startInfo.WorkingDirectory = _root;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.EnvironmentVariables["DSH_HOME"] = Path.Combine(_root, ".dsh");

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            process.Exited += ServiceExited;

            if (!process.Start())
            {
                return false;
            }

            _service = process;
            WriteLog("Started elevated node process " + process.Id.ToString() + ".");

            Thread outputThread = new Thread(delegate() { ReadProcessStream(process.StandardOutput, "OUT"); });
            outputThread.IsBackground = true;
            outputThread.Start();

            Thread errorThread = new Thread(delegate() { ReadProcessStream(process.StandardError, "ERR"); });
            errorThread.IsBackground = true;
            errorThread.Start();

            return true;
        }

        private void ReadProcessStream(StreamReader reader, string prefix)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    WriteLog(prefix + " " + RedactSensitiveUrl(line));
                    CaptureServiceUrl(line);
                }
            }
            catch
            {
            }
        }

        private void CaptureServiceUrl(string line)
        {
            const string marker = "http://127.0.0.1:8787/?token=";
            int markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return;
            }

            string value = line.Substring(markerIndex).Trim();
            int endIndex = value.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
            if (endIndex >= 0)
            {
                value = value.Substring(0, endIndex);
            }

            value = value.TrimEnd('.', ',', ';', '"', '\'');
            if (!value.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _serviceUrl = value;
            try
            {
                File.WriteAllText(_lastUrlPath, value, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                WriteLog("Could not save the service URL: " + exception.Message);
            }
        }

        private static string RedactSensitiveUrl(string line)
        {
            if (String.IsNullOrEmpty(line))
            {
                return line;
            }

            return Regex.Replace(
                line,
                @"(?i)([?&]token=)[^\s&""']+",
                "$1<redacted>");
        }

        private void ServiceExited(object sender, EventArgs eventArgs)
        {
            if (_suppressExitNotification)
            {
                return;
            }

            if (!Object.ReferenceEquals(sender, _service))
            {
                return;
            }

            _serviceRunning = false;
            WriteLog("Service process exited.");
            InvokeOnUi(delegate()
            {
                if (_exiting)
                {
                    return;
                }

                SetTrayState(false, Constants.Title + " 已停止");
            });
        }

        private void FinishSuccessfulStartup(bool openPage)
        {
            _startupInProgress = false;
            _serviceRunning = true;
            WriteLog("Service is ready.");

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (String.IsNullOrEmpty(_serviceUrl) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }

            string url = String.IsNullOrEmpty(_serviceUrl) ? Constants.Url : _serviceUrl;
            InvokeOnUi(delegate()
            {
                SetTrayState(true, Constants.Title + " 正在运行");
                ShowNotification(
                    openPage ? "DeepSeek Harness 服务启动已成功。" : "DeepSeek Harness 服务重启成功。",
                    false);

                if (openPage || _pendingOpenAfterStartup)
                {
                    _pendingOpenAfterStartup = false;

                    // 带自启参数时(开机自启)只驻留托盘并弹气泡,不自动拉起浏览器
                    if (Program.NoBrowser)
                    {
                        WriteLog("Browser page suppressed (no-browser startup argument).");
                    }
                    else
                    {
                        DispatcherQueueTimer openTimer = _dispatcherQueue.CreateTimer();
                        openTimer.Interval = TimeSpan.FromMilliseconds(1200);
                        openTimer.IsRepeating = false;
                        openTimer.Tick += delegate(DispatcherQueueTimer sender, object args)
                        {
                            sender.Stop();
                            Program.OpenPage(url);
                        };
                        openTimer.Start();
                    }
                }
            });
        }

        private void FailStartup(string message)
        {
            _startupInProgress = false;
            WriteLog("Startup failed: " + message);
            InvokeOnUi(delegate()
            {
                SetTrayState(false, Constants.Title + " 启动失败");
                ShowNotification(message, true);

                DispatcherQueueTimer exitTimer = _dispatcherQueue.CreateTimer();
                exitTimer.Interval = TimeSpan.FromSeconds(10);
                exitTimer.IsRepeating = false;
                exitTimer.Tick += delegate(DispatcherQueueTimer sender, object args)
                {
                    sender.Stop();
                    ExitApplication();
                };
                exitTimer.Start();
            });
        }

        private void SetTrayState(bool running, string tooltip)
        {
            _serviceRunning = running;
            _trayMenu.SetRunning(running);
            _trayIcon.UpdateTip(tooltip);
        }

        private void OpenServicePage()
        {
            WriteLog("Open page requested.");
            if (_startupInProgress || !_serviceRunning)
            {
                if (_startupInProgress)
                {
                    _pendingOpenAfterStartup = true;
                    return;
                }

                WinFormsMessageBox.Show(
                    "DeepSeek Harness 服务当前没有运行。请使用“重启 DSH 服务”重新启动。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Warning);
                return;
            }

            if (!IsServiceReady())
            {
                WinFormsMessageBox.Show(
                    "DeepSeek Harness 服务当前没有响应。请使用“重启 DSH 服务”。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Warning);
                return;
            }

            Program.OpenPage(_serviceUrl);
        }

        private void RestartThreadProc()
        {
            WriteLog("Restart requested.");
            try
            {
                StopService();
                _serviceUrl = null;
                _suppressExitNotification = false;
                _startupInProgress = true;

                if (!StartService())
                {
                    FailRestart("无法重新创建 DeepSeek Harness 服务进程。");
                    return;
                }

                string failureMessage;
                if (!WaitForServiceReady(out failureMessage))
                {
                    _suppressExitNotification = true;
                    StopService();
                    FailRestart(failureMessage);
                    return;
                }

                FinishSuccessfulStartup(false);
            }
            catch (Exception exception)
            {
                _suppressExitNotification = true;
                StopService();
                FailRestart("DeepSeek Harness 服务重启失败：" + exception.Message);
            }
        }

        private void FailRestart(string message)
        {
            _startupInProgress = false;
            WriteLog("Restart failed: " + message);
            InvokeOnUi(delegate()
            {
                SetTrayState(false, Constants.Title + " 重启失败");
                ShowNotification(message, true);
            });
        }

        private void ShowNotification(string message, bool critical)
        {
            if (!NotificationService.Show(message, critical))
            {
                WriteLog("Native balloon notification failed for: " + message);
            }
        }

        private bool StopService()
        {
            int processId = 0;
            if (_service != null)
            {
                try
                {
                    if (!_service.HasExited)
                    {
                        processId = _service.Id;
                    }
                }
                catch
                {
                }
            }

            if (processId <= 0)
            {
                processId = FindPortProcessId();
            }

            if (processId <= 0)
            {
                _serviceRunning = false;
                return false;
            }

            try
            {
                WriteLog("Force stopping process tree " + processId.ToString() + ".");
                string taskkill = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "taskkill.exe");

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = taskkill;
                startInfo.Arguments = "/PID " + processId.ToString() + " /T /F";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process killer = Process.Start(startInfo))
                {
                    if (killer != null)
                    {
                        killer.WaitForExit(5000);
                    }
                }

                WaitForPortToClose(5000);
                _suppressExitNotification = true;
                _serviceRunning = false;
                _service = null;
                return true;
            }
            catch (Exception exception)
            {
                WriteLog("Stop failed: " + exception.Message);
                return false;
            }
        }

        private int FindPortProcessId()
        {
            try
            {
                string netstat = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "netstat.exe");

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = netstat;
                startInfo.Arguments = "-ano -p tcp";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return 0;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    string[] lines = output.Split(
                        new string[] { Environment.NewLine, "\n" },
                        StringSplitOptions.RemoveEmptyEntries);
                    for (int index = 0; index < lines.Length; index++)
                    {
                        string line = lines[index].Trim();
                        string[] parts = line.Split(
                            new char[] { ' ', '\t' },
                            StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5)
                        {
                            continue;
                        }

                        string localEndpoint = parts[1];
                        string state = parts[3];
                        if (localEndpoint.EndsWith(":" + Constants.Port.ToString(), StringComparison.OrdinalIgnoreCase)
                            && state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                        {
                            int processId;
                            if (Int32.TryParse(parts[4], out processId))
                            {
                                return processId;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return 0;
        }

        private void WaitForPortToClose(int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsServiceReady())
                {
                    return;
                }

                Thread.Sleep(200);
            }
        }

        private bool IsServiceReady()
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Constants.Url);
                request.Method = "GET";
                request.Timeout = 900;
                request.ReadWriteTimeout = 900;
                request.AllowAutoRedirect = true;
                request.Proxy = null;

                using (WebResponse response = request.GetResponse())
                {
                    return response != null;
                }
            }
            catch (WebException exception)
            {
                if (exception.Response != null)
                {
                    exception.Response.Close();
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private void InvokeOnUi(Action action)
        {
            try
            {
                _dispatcherQueue.TryEnqueue(() => action());
            }
            catch
            {
            }
        }

        private void WriteLog(string message)
        {
            lock (_logLock)
            {
                try
                {
                    _logWriter.WriteLine(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + " ["
                        + Thread.CurrentThread.ManagedThreadId.ToString()
                        + "] "
                        + message);
                }
                catch
                {
                }
            }
        }

        private void ExitApplication()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            try
            {
                _balanceTimer.Stop();
                if (_openPageWait != null)
                {
                    _openPageWait.Unregister(null);
                }

                _trayMenu.Close();
                NotificationService.Shutdown();
                _trayIcon.Dispose();
                WriteLog("Launcher stopped.");
                _logWriter.Dispose();
                if (_appIcon != null && !ReferenceEquals(_appIcon, SystemIcons.Application))
                {
                    _appIcon.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                WinUIApplication.Current.Exit();
            }
            catch
            {
                Environment.Exit(0);
            }
        }

    }

    internal sealed class WinUIApiSettingsResult
    {
        public bool Confirmed { get; set; }
        public string ApiKey { get; set; }
    }

    internal sealed class WinUIApiSettingsDialog
    {
        private readonly WinUIWindow _window;
        private readonly Microsoft.UI.Xaml.Controls.PasswordBox _apiKeyBox;
        private readonly Microsoft.UI.Xaml.Controls.CheckBox _showKeyCheckBox;
        private readonly TaskCompletionSource<WinUIApiSettingsResult> _completion =
            new TaskCompletionSource<WinUIApiSettingsResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _completed;

        public WinUIApiSettingsDialog(string currentApiKey)
        {
            _window = new WinUIWindow();
            _window.Title = "DeepSeek Harness API 设置";
            _window.AppWindow.IsShownInSwitchers = true;

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            _apiKeyBox = new Microsoft.UI.Xaml.Controls.PasswordBox();
            _showKeyCheckBox = new Microsoft.UI.Xaml.Controls.CheckBox();

            Grid root = new Grid
            {
                Padding = new Thickness(26, 22, 26, 22),
                RowSpacing = 12,
                Background = GetDialogBackground()
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock heading = new TextBlock
            {
                Text = "DeepSeek API Key",
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            TextBlock description = new TextBlock
            {
                Text = "用于查询 DeepSeek 账户余额。Key 会保存到启动器自己的 Windows 加密凭据文件；未设置时读取 DSH 凭据。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            };
            Grid.SetRow(description, 1);
            root.Children.Add(description);

            _apiKeyBox.Password = currentApiKey ?? String.Empty;
            _apiKeyBox.PlaceholderText = "sk-...";
            _apiKeyBox.PasswordRevealMode = PasswordRevealMode.Hidden;
            _apiKeyBox.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
            _apiKeyBox.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
            Grid.SetRow(_apiKeyBox, 2);
            root.Children.Add(_apiKeyBox);

            _showKeyCheckBox.Content = "显示 API Key";
            _showKeyCheckBox.Checked += delegate
            {
                _apiKeyBox.PasswordRevealMode = PasswordRevealMode.Visible;
            };
            _showKeyCheckBox.Unchecked += delegate
            {
                _apiKeyBox.PasswordRevealMode = PasswordRevealMode.Hidden;
            };
            Grid.SetRow(_showKeyCheckBox, 3);
            root.Children.Add(_showKeyCheckBox);

            HyperlinkButton credentialLink = new HyperlinkButton
            {
                Content = "打开 DeepSeek API Keys 页面",
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Padding = new Thickness(0)
            };
            credentialLink.Click += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://platform.deepseek.com/api_keys")
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            };
            Grid.SetRow(credentialLink, 4);
            root.Children.Add(credentialLink);

            StackPanel buttons = new StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right
            };

            Microsoft.UI.Xaml.Controls.Button clearButton = CreateButton("清空", false);
            clearButton.Click += delegate
            {
                _apiKeyBox.Password = String.Empty;
                Complete(true, String.Empty);
            };

            Microsoft.UI.Xaml.Controls.Button cancelButton = CreateButton("取消", false);
            cancelButton.Click += delegate
            {
                Complete(false, String.Empty);
            };

            Microsoft.UI.Xaml.Controls.Button saveButton = CreateButton("保存", true);
            saveButton.Click += delegate
            {
                Complete(true, _apiKeyBox.Password.Trim());
            };

            buttons.Children.Add(clearButton);
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(saveButton);
            Grid.SetRow(buttons, 6);
            root.Children.Add(buttons);

            _apiKeyBox.Loaded += delegate
            {
                _apiKeyBox.Focus(FocusState.Programmatic);
            };
            _window.Content = root;
            _window.Closed += delegate
            {
                Complete(false, String.Empty, false);
            };
        }

        public Task<WinUIApiSettingsResult> ShowAsync()
        {
            try
            {
                PositionWindow();
                _window.AppWindow.Show();
                NativeMethods.SetForegroundWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(_window));
            }
            catch (Exception exception)
            {
                _completed = true;
                _completion.TrySetException(exception);
            }

            return _completion.Task;
        }

        private void Complete(bool confirmed, string apiKey)
        {
            Complete(confirmed, apiKey, true);
        }

        private void Complete(bool confirmed, string apiKey, bool closeWindow)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _completion.TrySetResult(new WinUIApiSettingsResult
            {
                Confirmed = confirmed,
                ApiKey = apiKey
            });
            if (closeWindow)
            {
                try
                {
                    _window.Close();
                }
                catch
                {
                }
            }
        }

        private void PositionWindow()
        {
            IntPtr hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            uint dpi = NativeMethods.GetDpiForWindow(hostHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int width = (int)Math.Round(620 * scale);
            int height = (int)Math.Round(360 * scale);
            DisplayArea displayArea = DisplayArea.GetFromWindowId(
                _window.AppWindow.Id,
                DisplayAreaFallback.Primary);
            if (displayArea == null)
            {
                return;
            }

            RectInt32 workArea = displayArea.WorkArea;
            int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
            _window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        private static Microsoft.UI.Xaml.Media.Brush GetDialogBackground()
        {
            try
            {
                Microsoft.UI.Xaml.Media.Brush brush =
                    Microsoft.UI.Xaml.Application.Current.Resources[
                        "SolidBackgroundFillColorBaseBrush"] as Microsoft.UI.Xaml.Media.Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private static Microsoft.UI.Xaml.Controls.Button CreateButton(
            string text,
            bool accent)
        {
            Microsoft.UI.Xaml.Controls.Button button =
                new Microsoft.UI.Xaml.Controls.Button
                {
                    Content = text,
                    MinWidth = 92,
                    Height = 32
                };
            if (accent)
            {
                Style style = Microsoft.UI.Xaml.Application.Current.Resources[
                    "AccentButtonStyle"] as Style;
                if (style != null)
                {
                    button.Style = style;
                }
            }

            return button;
        }
    }

    internal sealed class WinUITrayMenu
    {
        private readonly WinUIWindow _window;
        private readonly Grid _anchor;
        private readonly MenuFlyout _flyout;
        private readonly MenuFlyoutItem _balanceItem;
        private readonly MenuFlyoutItem _openItem;
        private readonly MenuFlyoutItem _restartItem;
        private readonly MenuFlyoutItem _forceStopItem;
        private readonly MenuFlyoutItem _exitItem;
        private readonly NativeMethods.WinEventDelegate _foregroundChanged;
        private readonly NativeMethods.LowLevelMouseProc _lowLevelMouse;
        private readonly DispatcherQueueTimer _showTimer;
        private IntPtr _foregroundHook;
        private IntPtr _mouseHook;
        private bool _menuOpen;
        private bool _showPending;
        private NativeMethods.POINT _pendingCursor;

        public WinUITrayMenu()
        {
            _foregroundChanged = OnForegroundChanged;
            _lowLevelMouse = OnLowLevelMouse;
            _window = new WinUIWindow();
            _window.Title = Constants.Title;
            _window.ExtendsContentIntoTitleBar = true;
            _window.AppWindow.IsShownInSwitchers = false;

            IntPtr hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            int extendedStyle = unchecked((int)NativeMethods.GetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE).ToInt64());
            NativeMethods.SetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(extendedStyle
                    | NativeMethods.WS_EX_LAYERED
                    | NativeMethods.WS_EX_TOOLWINDOW));
            NativeMethods.SetLayeredWindowAttributes(
                hostHandle,
                0,
                0,
                NativeMethods.LWA_ALPHA);

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            _anchor = new Grid
            {
                Width = 1,
                Height = 1,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                RequestedTheme = ElementTheme.Default
            };
            _window.Content = _anchor;

            _flyout = new MenuFlyout
            {
                AreOpenCloseAnimationsEnabled = false,
                MenuFlyoutPresenterStyle = CreatePresenterStyle()
            };

            _flyout.Opened += delegate
            {
                DispatcherQueue dispatcher = _anchor.DispatcherQueue;
                if (dispatcher != null)
                {
                    dispatcher.TryEnqueue(DispatcherQueuePriority.Low, PlayOpenAnimation);
                }
            };
            _flyout.Closed += delegate
            {
                StopInteractionHooks();
                HideWindow();
            };

            _showTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _showTimer.Interval = TimeSpan.FromMilliseconds(50);
            _showTimer.IsRepeating = false;
            _showTimer.Tick += delegate { ShowFlyoutAtCursor(); };

            _balanceItem = CreateItem("余额：正在查询...", "\uE8C7", false);
            _balanceItem.Click += delegate { BalanceClicked(); };

            _openItem = CreateItem("打开页面", "\uE8A7", true);
            _openItem.Click += delegate { OpenClicked(); };

            _restartItem = CreateItem("重启 DSH 服务", "\uE72C", false);
            _restartItem.Click += delegate { RestartClicked(); };

            _forceStopItem = CreateItem("强行终止", "\uE71A", false);
            _forceStopItem.Click += delegate { ForceStopClicked(); };

            _exitItem = CreateItem("退出", "\uE7E8", false);
            _exitItem.Click += delegate { ExitClicked(); };

            _flyout.Items.Add(_balanceItem);
            _flyout.Items.Add(new MenuFlyoutSeparator());
            _flyout.Items.Add(_openItem);
            _flyout.Items.Add(_restartItem);
            _flyout.Items.Add(new MenuFlyoutSeparator());
            _flyout.Items.Add(_forceStopItem);
            _flyout.Items.Add(_exitItem);
        }

        public event Action BalanceClicked = delegate { };
        public event Action OpenClicked = delegate { };
        public event Action RestartClicked = delegate { };
        public event Action ForceStopClicked = delegate { };
        public event Action ExitClicked = delegate { };

        public void ShowAtCursor()
        {
            NativeMethods.GetCursorPos(out _pendingCursor);
            _showPending = true;
            _window.AppWindow.MoveAndResize(new RectInt32(
                _pendingCursor.X,
                _pendingCursor.Y,
                1,
                1));
            _window.Activate();
            _showTimer.Stop();
            _showTimer.Start();
        }

        private void ShowFlyoutAtCursor()
        {
            if (!_showPending)
            {
                return;
            }

            _showPending = false;
            _window.AppWindow.MoveAndResize(new RectInt32(
                _pendingCursor.X,
                _pendingCursor.Y,
                1,
                1));
            if (_flyout.IsOpen)
            {
                _flyout.Hide();
            }

            _menuOpen = true;
            _flyout.ShowAt(_anchor, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(0, 0),
                ShowMode = FlyoutShowMode.Standard
            });
            StartInteractionHooks();
        }

        public void SetBalance(string text, string tooltip)
        {
            if (!String.IsNullOrEmpty(text))
            {
                _balanceItem.Text = text;
            }

            ToolTipService.SetToolTip(
                _balanceItem,
                String.IsNullOrEmpty(tooltip) ? "点击修改 API 设置。" : tooltip);
        }

        public void SetRunning(bool running)
        {
            _openItem.IsEnabled = running;
            _restartItem.IsEnabled = true;
            _forceStopItem.IsEnabled = running;
        }

        public void Close()
        {
            _showTimer.Stop();
            _showPending = false;
            _menuOpen = false;
            StopInteractionHooks();
            try
            {
                _flyout.Hide();
            }
            catch
            {
            }

            HideWindow();
        }

        private void OnForegroundChanged(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (!_menuOpen || windowHandle == IntPtr.Zero)
            {
                return;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == (uint)Environment.ProcessId)
            {
                return;
            }

            DispatcherQueue dispatcher = _anchor.DispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(delegate
                {
                    if (_menuOpen)
                    {
                        Close();
                    }
                });
            }
        }

        private IntPtr OnLowLevelMouse(int code, IntPtr message, IntPtr dataPointer)
        {
            if (code >= 0 && _menuOpen && dataPointer != IntPtr.Zero)
            {
                long mouseMessage = message.ToInt64();
                bool buttonDown = mouseMessage == NativeMethods.WM_LBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_RBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_MBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_XBUTTONDOWN;
                if (buttonDown)
                {
                    NativeMethods.MSLLHOOKSTRUCT data =
                        Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(dataPointer);
                    IntPtr target = NativeMethods.WindowFromPoint(data.Point);
                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(target, out processId);
                    if (processId != (uint)Environment.ProcessId)
                    {
                        DispatcherQueue dispatcher = _anchor.DispatcherQueue;
                        if (dispatcher != null)
                        {
                            dispatcher.TryEnqueue(delegate
                            {
                                if (_menuOpen)
                                {
                                    Close();
                                }
                            });
                        }
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
        }

        private void PlayOpenAnimation()
        {
            if (!_menuOpen || _anchor.XamlRoot == null)
            {
                return;
            }

            MenuFlyoutPresenter presenter = null;
            foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(_anchor.XamlRoot))
            {
                presenter = FindDescendant<MenuFlyoutPresenter>(popup.Child);
                if (presenter != null)
                {
                    break;
                }
            }

            if (presenter == null)
            {
                return;
            }

            Visual visual = ElementCompositionPreview.GetElementVisual(presenter);
            visual.StopAnimation("Offset");
            visual.StopAnimation("Opacity");
            Vector3 end = visual.Offset;
            Vector3 start = new Vector3(end.X, end.Y + 24, end.Z);
            visual.Offset = start;
            visual.Opacity = 0;

            Vector3KeyFrameAnimation animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(0, start);
            animation.InsertKeyFrame(
                1,
                end,
                visual.Compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.16f, 1.0f),
                    new Vector2(0.30f, 1.0f)));
            animation.Duration = TimeSpan.FromMilliseconds(190);
            visual.StartAnimation("Offset", animation);

            ScalarKeyFrameAnimation fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1);
            fade.Duration = TimeSpan.FromMilliseconds(150);
            visual.StartAnimation("Opacity", fade);
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                T match = child as T;
                if (match != null)
                {
                    return match;
                }

                match = FindDescendant<T>(child);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private void StartInteractionHooks()
        {
            StopInteractionHooks();
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundChanged,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _lowLevelMouse,
                NativeMethods.GetModuleHandle(null),
                0);
        }

        private void StopInteractionHooks()
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private void HideWindow()
        {
            try
            {
                _window.AppWindow.Hide();
            }
            catch
            {
            }
        }

        private static MenuFlyoutItem CreateItem(string text, string glyph, bool emphasized)
        {
            MenuFlyoutItem item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    Glyph = glyph
                }
            };
            if (emphasized)
            {
                item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            return item;
        }

        private static Style CreatePresenterStyle()
        {
            Style style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(
                MenuFlyoutPresenter.CornerRadiusProperty,
                new CornerRadius(8)));
            style.Setters.Add(new Setter(
                Microsoft.UI.Xaml.Controls.Control.PaddingProperty,
                new Thickness(4)));
            style.Setters.Add(new Setter(
                Microsoft.UI.Xaml.Controls.Control.BorderThicknessProperty,
                new Thickness(1)));
            style.Setters.Add(new Setter(
                Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty,
                GetThemeBrush(
                    "DividerStrokeColorDefaultBrush",
                    Windows.UI.Color.FromArgb(255, 104, 112, 128))));
            style.Setters.Add(new Setter(
                MenuFlyoutPresenter.BackgroundProperty,
                new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
            style.Setters.Add(new Setter(
                MenuFlyoutPresenter.SystemBackdropProperty,
                new MicaBackdrop
                {
                    Kind = MicaKind.BaseAlt
                }));

            return style;
        }

        private static Microsoft.UI.Xaml.Media.Brush GetThemeBrush(
            string key,
            Windows.UI.Color fallback)
        {
            try
            {
                object value = Microsoft.UI.Xaml.Application.Current.Resources[key];
                Microsoft.UI.Xaml.Media.Brush brush =
                    value as Microsoft.UI.Xaml.Media.Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }
    }

    internal sealed class WinUICompositionTrayMenu
    {
        private const int MenuWidth = 260;
        private const int MenuHeight = 260;
        private const int AnimationMilliseconds = 180;
        private const int AnimationOffset = 16;

        private readonly WinUIWindow _window;
        private readonly Border _surface;
        private readonly Microsoft.UI.Xaml.Controls.Button _balanceItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _openItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _restartItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _startupItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _forceStopItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _exitItem;
        private readonly NativeMethods.WinEventDelegate _foregroundChanged;
        private readonly NativeMethods.LowLevelMouseProc _lowLevelMouse;
        private readonly IntPtr _hostHandle;
        private IntPtr _foregroundHook;
        private IntPtr _mouseHook;
        private bool _menuOpen;
        private int _animationSerial;

        public WinUICompositionTrayMenu()
        {
            _foregroundChanged = OnForegroundChanged;
            _lowLevelMouse = OnLowLevelMouse;
            _window = new WinUIWindow();
            _window.Title = Constants.Title;
            _window.AppWindow.IsShownInSwitchers = false;
            _hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            _window.Activated += delegate
            {
                ConfigureNativeWindow(_hostHandle);
            };

            ConfigureNativeWindow(_hostHandle);

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            ConfigureNativeWindow(_hostHandle);

            try
            {
                _window.SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            catch
            {
            }

            _balanceItem = CreateItem("余额：正在查询...", "\uE8C7", false);
            _openItem = CreateItem("打开页面", "\uE8A7", true);
            _restartItem = CreateItem("重启 DSH 服务", "\uE72C", false);
            _startupItem = CreateItem("开机自启动", "\uE945", false);
            _forceStopItem = CreateItem("强行终止", "\uE71A", false);
            _exitItem = CreateItem("退出", "\uE7E8", false);

            _balanceItem.Click += delegate
            {
                Close();
                BalanceClicked();
            };
            _openItem.Click += delegate
            {
                Close();
                OpenClicked();
            };
            _restartItem.Click += delegate
            {
                Close();
                RestartClicked();
            };
            _startupItem.Click += delegate
            {
                Close();
                StartupClicked();
            };
            _forceStopItem.Click += delegate
            {
                Close();
                ForceStopClicked();
            };
            _exitItem.Click += delegate
            {
                Close();
                ExitClicked();
            };

            StackPanel items = new StackPanel
            {
                Spacing = 2
            };
            items.Children.Add(_balanceItem);
            items.Children.Add(CreateSeparator());
            items.Children.Add(_openItem);
            items.Children.Add(_restartItem);
            items.Children.Add(_startupItem);
            items.Children.Add(CreateSeparator());
            items.Children.Add(_forceStopItem);
            items.Children.Add(_exitItem);

            _surface = new Border
            {
                Width = MenuWidth,
                Padding = new Thickness(5),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = items
            };
            _window.Content = _surface;
            _window.AppWindow.Show();
            MoveOffscreen();
            SetSurfaceHidden();
        }

        public event Action BalanceClicked = delegate { };
        public event Action OpenClicked = delegate { };
        public event Action RestartClicked = delegate { };
        public event Action StartupClicked = delegate { };
        public event Action ForceStopClicked = delegate { };
        public event Action ExitClicked = delegate { };

        public void ShowAtCursor()
        {
            PrepareForOpen();

            NativeMethods.POINT cursor;
            NativeMethods.GetCursorPos(out cursor);
            uint dpi = NativeMethods.GetDpiForWindow(_hostHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int windowWidth = (int)Math.Round(MenuWidth * scale);
            int windowHeight = (int)Math.Round(MenuHeight * scale);
            Screen screen = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            System.Drawing.Rectangle workArea = screen.WorkingArea;
            int x = Math.Max(
                workArea.Left + 8,
                Math.Min(cursor.X - 12, workArea.Right - windowWidth - 8));
            int y = cursor.Y + 8;
            if (y + windowHeight > workArea.Bottom - 8)
            {
                y = cursor.Y - windowHeight - 8;
            }

            y = Math.Max(workArea.Top + 8, y);

            ConfigureNativeWindow(_hostHandle);
            _window.AppWindow.MoveAndResize(new RectInt32(
                x,
                y,
                windowWidth,
                windowHeight));
            SetSurfaceHidden();

            _menuOpen = true;
            int serial = ++_animationSerial;
            ConfigureNativeWindow(_hostHandle);
            NativeMethods.SetWindowPos(
                _hostHandle,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE
                    | NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_SHOWWINDOW);
            StartInteractionHooks();
            PlayOpenAnimation(serial);
        }

        public void SetBalance(string text, string tooltip)
        {
            if (!String.IsNullOrEmpty(text))
            {
                TextBlock label = _balanceItem.Tag as TextBlock;
                if (label != null)
                {
                    label.Text = text;
                }
            }

            ToolTipService.SetToolTip(
                _balanceItem,
                String.IsNullOrEmpty(tooltip) ? "点击修改 API 设置。" : tooltip);
        }

        public void SetRunning(bool running)
        {
            _openItem.IsEnabled = running;
            _restartItem.IsEnabled = true;
            _forceStopItem.IsEnabled = running;
        }

        public void SetStartupState(bool enabled)
        {
            // 只改文字,不能整个换 Content —— 换了会把左边的图标一起吃掉
            TextBlock label = _startupItem.Tag as TextBlock;
            if (label != null)
            {
                label.Text = enabled ? "开机自启动  ✓" : "开机自启动";
            }
        }

        public void Close()
        {
            _menuOpen = false;
            _animationSerial++;
            StopInteractionHooks();
            SetSurfaceHidden();
            MoveOffscreen();
        }

        private void PrepareForOpen()
        {
            _menuOpen = false;
            _animationSerial++;
            StopInteractionHooks();
            SetSurfaceHidden();
            ApplyThemeStates();
        }

        // 菜单按钮本身是透明的,悬停高亮得自己给,不然浅色模式下默认笔刷几乎看不见
        private void ApplyThemeStates()
        {
            bool dark;
            try
            {
                dark = _surface.ActualTheme == ElementTheme.Dark;
            }
            catch
            {
                dark = false;
            }

            Windows.UI.Color hover = dark
                ? Windows.UI.Color.FromArgb(36, 255, 255, 255)
                : Windows.UI.Color.FromArgb(28, 0, 0, 0);
            Windows.UI.Color pressed = dark
                ? Windows.UI.Color.FromArgb(64, 255, 255, 255)
                : Windows.UI.Color.FromArgb(48, 0, 0, 0);

            Microsoft.UI.Xaml.Controls.Button[] items = new Microsoft.UI.Xaml.Controls.Button[]
            {
                _balanceItem,
                _openItem,
                _restartItem,
                _startupItem,
                _forceStopItem,
                _exitItem
            };

            for (int index = 0; index < items.Length; index++)
            {
                if (items[index] == null)
                {
                    continue;
                }

                items[index].Resources["ButtonBackgroundPointerOver"] =
                    new SolidColorBrush(hover);
                items[index].Resources["ButtonBackgroundPressed"] =
                    new SolidColorBrush(pressed);
                items[index].Resources["ButtonBackgroundDisabled"] =
                    new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                items[index].Resources["ButtonBorderBrushPointerOver"] =
                    new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                items[index].Resources["ButtonBorderBrushPressed"] =
                    new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }

        private void MoveOffscreen()
        {
            try
            {
                _window.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
            }
            catch
            {
            }
        }

        private void PlayOpenAnimation(int serial)
        {
            if (!_menuOpen || serial != _animationSerial || _surface.XamlRoot == null)
            {
                return;
            }

            Visual visual = ElementCompositionPreview.GetElementVisual(_surface);
            visual.StopAnimation("Offset");
            visual.StopAnimation("Opacity");

            Vector3 end = Vector3.Zero;
            Vector3 start = new Vector3(0, AnimationOffset, 0);
            visual.Offset = start;
            visual.Opacity = 0;

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1.0f),
                new Vector2(0.30f, 1.0f));

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0, start);
            slide.InsertKeyFrame(1, end, easing);
            slide.Duration = TimeSpan.FromMilliseconds(AnimationMilliseconds);

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, easing);
            fade.Duration = TimeSpan.FromMilliseconds(AnimationMilliseconds - 20);

            visual.StartAnimation("Offset", slide);
            visual.StartAnimation("Opacity", fade);
        }

        private void SetSurfaceHidden()
        {
            try
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(_surface);
                visual.StopAnimation("Offset");
                visual.StopAnimation("Opacity");
                visual.Offset = Vector3.Zero;
                visual.Opacity = 0;
                _surface.Opacity = 0;
            }
            catch
            {
            }
        }

        private static void ConfigureNativeWindow(IntPtr hostHandle)
        {
            long windowStyle = NativeMethods.GetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_STYLE).ToInt64();
            windowStyle &= ~(
                NativeMethods.WS_CAPTION
                | NativeMethods.WS_BORDER
                | NativeMethods.WS_DLGFRAME
                | NativeMethods.WS_THICKFRAME
                | NativeMethods.WS_SYSMENU);
            windowStyle |= NativeMethods.WS_POPUP;
            NativeMethods.SetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_STYLE,
                new IntPtr(windowStyle));
            NativeMethods.SetWindowPos(
                hostHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE
                    | NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOZORDER
                    | NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_FRAMECHANGED);

            long extendedStyle = NativeMethods.GetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(
                    extendedStyle
                    | NativeMethods.WS_EX_TOOLWINDOW
                    | NativeMethods.WS_EX_NOACTIVATE));

            int cornerPreference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                Marshal.SizeOf(typeof(int)));
            int borderColor = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_BORDER_COLOR,
                ref borderColor,
                Marshal.SizeOf(typeof(int)));

            // DWM transitions are disabled so the only entrance animation is the
            // WinUI composition animation below.
            int transitionsDisabled = 1;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
                ref transitionsDisabled,
                Marshal.SizeOf(typeof(int)));
        }

        private void OnForegroundChanged(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (!_menuOpen || windowHandle == IntPtr.Zero || windowHandle == _hostHandle)
            {
                return;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == (uint)Environment.ProcessId)
            {
                return;
            }

            DispatcherQueue dispatcher = _surface.DispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(delegate
                {
                    if (_menuOpen)
                    {
                        Close();
                    }
                });
            }
        }

        private IntPtr OnLowLevelMouse(int code, IntPtr message, IntPtr dataPointer)
        {
            if (code >= 0 && _menuOpen && dataPointer != IntPtr.Zero)
            {
                long mouseMessage = message.ToInt64();
                bool buttonDown = mouseMessage == NativeMethods.WM_LBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_RBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_MBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_XBUTTONDOWN;
                if (buttonDown)
                {
                    NativeMethods.MSLLHOOKSTRUCT data =
                        Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(dataPointer);
                    IntPtr target = NativeMethods.WindowFromPoint(data.Point);
                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(target, out processId);
                    if (processId != (uint)Environment.ProcessId)
                    {
                        DispatcherQueue dispatcher = _surface.DispatcherQueue;
                        if (dispatcher != null)
                        {
                            dispatcher.TryEnqueue(delegate
                            {
                                if (_menuOpen)
                                {
                                    Close();
                                }
                            });
                        }
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
        }

        private void StartInteractionHooks()
        {
            StopInteractionHooks();
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundChanged,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _lowLevelMouse,
                NativeMethods.GetModuleHandle(null),
                0);
        }

        private void StopInteractionHooks()
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private static Microsoft.UI.Xaml.Controls.Button CreateItem(
            string text,
            string glyph,
            bool emphasized)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (emphasized)
            {
                label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            FontIcon icon = new FontIcon
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Glyph = glyph,
                FontSize = 15,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);
            Grid.SetColumn(label, 1);
            content.Children.Add(label);

            Microsoft.UI.Xaml.Controls.Button button =
                new Microsoft.UI.Xaml.Controls.Button
            {
                Content = content,
                Tag = label,
                Height = 38,
                Padding = new Thickness(10, 0, 10, 0),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                FocusVisualPrimaryThickness = new Thickness(0),
                FocusVisualSecondaryThickness = new Thickness(0)
            };
            button.Resources["ButtonBackgroundDisabled"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonBorderBrushDisabled"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonForegroundDisabled"] = GetThemeBrush(
                "TextFillColorDisabledBrush",
                Windows.UI.Color.FromArgb(255, 120, 126, 138));
            return button;
        }

        private static Border CreateSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(8, 2, 8, 2),
                Background = GetThemeBrush(
                    "DividerStrokeColorDefaultBrush",
                    Windows.UI.Color.FromArgb(255, 104, 112, 128))
            };
        }

        private static Microsoft.UI.Xaml.Media.Brush GetThemeBrush(
            string key,
            Windows.UI.Color fallback)
        {
            try
            {
                object value = Microsoft.UI.Xaml.Application.Current.Resources[key];
                Microsoft.UI.Xaml.Media.Brush brush =
                    value as Microsoft.UI.Xaml.Media.Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }
    }

    internal sealed class LegacyWinUIMicaTrayMenu
    {
        private const int MenuWidth = 260;
        private const int MenuHeight = 248;
        private const int AnimationMilliseconds = 190;

        private readonly WinUIWindow _window;
        private readonly Border _surface;
        private readonly Microsoft.UI.Xaml.Controls.Button _balanceItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _openItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _restartItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _forceStopItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _exitItem;
        private readonly NativeMethods.WinEventDelegate _foregroundChanged;
        private readonly NativeMethods.LowLevelMouseProc _lowLevelMouse;
        private IntPtr _foregroundHook;
        private IntPtr _mouseHook;
        private bool _menuOpen;

        public LegacyWinUIMicaTrayMenu()
        {
            _foregroundChanged = OnForegroundChanged;
            _lowLevelMouse = OnLowLevelMouse;
            _window = new WinUIWindow();
            _window.Title = Constants.Title;
            _window.ExtendsContentIntoTitleBar = true;
            _window.AppWindow.IsShownInSwitchers = false;

            IntPtr hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            int extendedStyle = unchecked((int)NativeMethods.GetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE).ToInt64());
            NativeMethods.SetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(extendedStyle | NativeMethods.WS_EX_TOOLWINDOW));

            int cornerPreference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                Marshal.SizeOf(typeof(int)));
            int borderColor = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_BORDER_COLOR,
                ref borderColor,
                Marshal.SizeOf(typeof(int)));
            int transitionsDisabled = 1;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
                ref transitionsDisabled,
                Marshal.SizeOf(typeof(int)));

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            try
            {
                _window.SystemBackdrop = new MicaBackdrop
                {
                    Kind = MicaKind.BaseAlt
                };
            }
            catch
            {
            }

            _balanceItem = CreateItem("余额：正在查询...", "\uE8C7", false);
            _balanceItem.Click += delegate { BalanceClicked(); };
            _openItem = CreateItem("打开页面", "\uE8A7", true);
            _openItem.Click += delegate { OpenClicked(); };
            _restartItem = CreateItem("重启 DSH 服务", "\uE72C", false);
            _restartItem.Click += delegate { RestartClicked(); };
            _forceStopItem = CreateItem("强行终止", "\uE71A", false);
            _forceStopItem.Click += delegate { ForceStopClicked(); };
            _exitItem = CreateItem("退出", "\uE7E8", false);
            _exitItem.Click += delegate { ExitClicked(); };

            StackPanel items = new StackPanel
            {
                Spacing = 2
            };
            items.Children.Add(_balanceItem);
            items.Children.Add(CreateSeparator());
            items.Children.Add(_openItem);
            items.Children.Add(_restartItem);
            items.Children.Add(CreateSeparator());
            items.Children.Add(_forceStopItem);
            items.Children.Add(_exitItem);

            _surface = new Border
            {
                Width = MenuWidth,
                Padding = new Thickness(5),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = items
            };
            _window.Content = _surface;
            _window.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
            _window.Activate();
            _window.AppWindow.Hide();
        }

        public event Action BalanceClicked = delegate { };
        public event Action OpenClicked = delegate { };
        public event Action RestartClicked = delegate { };
        public event Action ForceStopClicked = delegate { };
        public event Action ExitClicked = delegate { };

        public void ShowAtCursor()
        {
            NativeMethods.POINT cursor;
            NativeMethods.GetCursorPos(out cursor);
            IntPtr hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            uint dpi = NativeMethods.GetDpiForWindow(hostHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int windowWidth = (int)Math.Round(MenuWidth * scale);
            int windowHeight = (int)Math.Round(MenuHeight * scale);
            Screen screen = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            System.Drawing.Rectangle workArea = screen.WorkingArea;
            int x = Math.Max(
                workArea.Left + 8,
                Math.Min(cursor.X - 12, workArea.Right - windowWidth - 8));
            int y = cursor.Y + 8;
            if (y + windowHeight > workArea.Bottom - 8)
            {
                y = cursor.Y - windowHeight - 8;
            }

            y = Math.Max(workArea.Top + 8, y);
            _menuOpen = true;
            _window.AppWindow.Hide();
            _window.AppWindow.MoveAndResize(new RectInt32(
                x,
                y,
                windowWidth,
                windowHeight));
            StartInteractionHooks();
            PlayNativeAnimation(hostHandle);
        }

        public void SetBalance(string text, string tooltip)
        {
            if (!String.IsNullOrEmpty(text))
            {
                TextBlock label = _balanceItem.Tag as TextBlock;
                if (label != null)
                {
                    label.Text = text;
                }
            }

            ToolTipService.SetToolTip(
                _balanceItem,
                String.IsNullOrEmpty(tooltip) ? "点击修改 API 设置。" : tooltip);
        }

        public void SetRunning(bool running)
        {
            _openItem.IsEnabled = running;
            _restartItem.IsEnabled = true;
            _forceStopItem.IsEnabled = running;
        }

        public void Close()
        {
            _menuOpen = false;
            StopInteractionHooks();
            try
            {
                _window.AppWindow.Hide();
            }
            catch
            {
            }
        }

        private void PlayNativeAnimation(IntPtr windowHandle)
        {
            bool animated = NativeMethods.AnimateWindow(
                windowHandle,
                AnimationMilliseconds,
                NativeMethods.AW_SLIDE
                    | NativeMethods.AW_VER_NEGATIVE
                    | NativeMethods.AW_ACTIVATE);
            if (!animated)
            {
                _window.Activate();
            }
        }

        private void OnForegroundChanged(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (!_menuOpen || windowHandle == IntPtr.Zero)
            {
                return;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == (uint)Environment.ProcessId)
            {
                return;
            }

            DispatcherQueue dispatcher = _surface.DispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(delegate
                {
                    if (_menuOpen)
                    {
                        Close();
                    }
                });
            }
        }

        private IntPtr OnLowLevelMouse(int code, IntPtr message, IntPtr dataPointer)
        {
            if (code >= 0 && _menuOpen && dataPointer != IntPtr.Zero)
            {
                long mouseMessage = message.ToInt64();
                bool buttonDown = mouseMessage == NativeMethods.WM_LBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_RBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_MBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_XBUTTONDOWN;
                if (buttonDown)
                {
                    NativeMethods.MSLLHOOKSTRUCT data =
                        Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(dataPointer);
                    IntPtr target = NativeMethods.WindowFromPoint(data.Point);
                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(target, out processId);
                    if (processId != (uint)Environment.ProcessId)
                    {
                        DispatcherQueue dispatcher = _surface.DispatcherQueue;
                        if (dispatcher != null)
                        {
                            dispatcher.TryEnqueue(delegate
                            {
                                if (_menuOpen)
                                {
                                    Close();
                                }
                            });
                        }
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
        }

        private void StartInteractionHooks()
        {
            StopInteractionHooks();
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundChanged,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _lowLevelMouse,
                NativeMethods.GetModuleHandle(null),
                0);
        }

        private void StopInteractionHooks()
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private static Microsoft.UI.Xaml.Controls.Button CreateItem(
            string text,
            string glyph,
            bool emphasized)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (emphasized)
            {
                label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            FontIcon icon = new FontIcon
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Glyph = glyph,
                FontSize = 15,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);
            Grid.SetColumn(label, 1);
            content.Children.Add(label);

            Microsoft.UI.Xaml.Controls.Button button =
                new Microsoft.UI.Xaml.Controls.Button
                {
                    Content = content,
                    Tag = label,
                    Height = 38,
                    Padding = new Thickness(10, 0, 10, 0),
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(5)
                };
            return button;
        }

        private static Border CreateSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(8, 2, 8, 2),
                Background = GetThemeBrush(
                    "DividerStrokeColorDefaultBrush",
                    Windows.UI.Color.FromArgb(255, 104, 112, 128))
            };
        }

        private static Microsoft.UI.Xaml.Media.Brush GetThemeBrush(
            string key,
            Windows.UI.Color fallback)
        {
            try
            {
                object value = Microsoft.UI.Xaml.Application.Current.Resources[key];
                Microsoft.UI.Xaml.Media.Brush brush =
                    value as Microsoft.UI.Xaml.Media.Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }
    }

    internal sealed class Win32TrayMenu
    {
        private const uint MfString = 0x00000000;
        private const uint MfGrayed = 0x00000001;
        private const uint MfSeparator = 0x00000800;
        private const uint TpmRightButton = 0x00000002;
        private const uint TpmNoNotify = 0x00000080;
        private const uint TpmReturnCmd = 0x00000100;
        private const uint BalanceCommand = 100;
        private const uint OpenCommand = 101;
        private const uint RestartCommand = 102;
        private const uint ForceStopCommand = 103;
        private const uint ExitCommand = 104;

        private readonly IntPtr _ownerWindow;
        private string _balanceText = "余额：正在查询...";
        private bool _running;

        public Win32TrayMenu(IntPtr ownerWindow)
        {
            _ownerWindow = ownerWindow;
        }

        public event Action BalanceClicked = delegate { };
        public event Action OpenClicked = delegate { };
        public event Action RestartClicked = delegate { };
        public event Action ForceStopClicked = delegate { };
        public event Action ExitClicked = delegate { };

        public void ShowAtCursor()
        {
            NativeMethods.POINT cursor;
            NativeMethods.GetCursorPos(out cursor);
            IntPtr menu = NativeMethods.CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                NativeMethods.AppendMenu(menu, MfString, new UIntPtr(BalanceCommand), _balanceText);
                NativeMethods.AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                NativeMethods.AppendMenu(
                    menu,
                    MfString | (_running ? 0 : MfGrayed),
                    new UIntPtr(OpenCommand),
                    "打开页面");
                NativeMethods.AppendMenu(
                    menu,
                    MfString,
                    new UIntPtr(RestartCommand),
                    "重启 DSH 服务");
                NativeMethods.AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                NativeMethods.AppendMenu(
                    menu,
                    MfString | (_running ? 0 : MfGrayed),
                    new UIntPtr(ForceStopCommand),
                    "强行终止");
                NativeMethods.AppendMenu(
                    menu,
                    MfString,
                    new UIntPtr(ExitCommand),
                    "退出");

                NativeMethods.SetForegroundWindow(_ownerWindow);
                uint command = NativeMethods.TrackPopupMenuEx(
                    menu,
                    TpmRightButton | TpmNoNotify | TpmReturnCmd,
                    cursor.X,
                    cursor.Y,
                    _ownerWindow,
                    IntPtr.Zero);
                NativeMethods.PostMessage(
                    _ownerWindow,
                    NativeMethods.WM_NULL,
                    IntPtr.Zero,
                    IntPtr.Zero);

                switch (command)
                {
                    case BalanceCommand:
                        BalanceClicked();
                        break;
                    case OpenCommand:
                        OpenClicked();
                        break;
                    case RestartCommand:
                        RestartClicked();
                        break;
                    case ForceStopCommand:
                        ForceStopClicked();
                        break;
                    case ExitCommand:
                        ExitClicked();
                        break;
                }
            }
            finally
            {
                NativeMethods.DestroyMenu(menu);
            }
        }

        public void SetBalance(string text, string tooltip)
        {
            if (!String.IsNullOrEmpty(text))
            {
                _balanceText = text;
            }
        }

        public void SetRunning(bool running)
        {
            _running = running;
        }

        public void Close()
        {
        }
    }

    internal static class NotificationService
    {
        private static NativeTrayIcon _fallbackTray;
        private static DispatcherQueue _dispatcherQueue;
        private static bool _registered;
        private static AppNotificationSetting _setting = AppNotificationSetting.Unsupported;
        private static readonly System.Collections.Generic.List<WinUINotificationWindow> Windows =
            new System.Collections.Generic.List<WinUINotificationWindow>();

        internal const int NotificationWidth = 390;
        internal const int NotificationHeight = 116;

        public static bool Initialize(NativeTrayIcon fallbackTray)
        {
            _fallbackTray = fallbackTray;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            try
            {
                AppNotificationManager.Default.Register();
                _registered = true;
                try
                {
                    _setting = AppNotificationManager.Default.Setting;
                }
                catch
                {
                    _setting = AppNotificationSetting.Unsupported;
                }

                return true;
            }
            catch
            {
                _registered = false;
                _setting = AppNotificationSetting.Unsupported;
                return false;
            }
        }

        public static string SettingName
        {
            get { return _setting.ToString(); }
        }

        public static bool Show(string message, bool critical)
        {
            if (String.IsNullOrEmpty(message))
            {
                return true;
            }

            if (_dispatcherQueue == null)
            {
                return false;
            }

            if (!_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(() => Show(message, critical));
                return true;
            }

            return _fallbackTray != null && _fallbackTray.ShowBalloon(message, critical);
        }

        private static bool ShowWinUiNotification(string message, bool critical)
        {
            try
            {
                WinUINotificationWindow window = new WinUINotificationWindow(
                    message,
                    critical,
                    delegate
                    {
                        Program.OpenPage();
                    });
                window.Closed += delegate
                {
                    Windows.Remove(window);
                    RepositionWindows();
                };
                Windows.Insert(0, window);
                while (Windows.Count > 3)
                {
                    Windows[Windows.Count - 1].Close();
                }

                RepositionWindows();
                window.ShowNotification();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RepositionWindows()
        {
            Screen screen = Screen.PrimaryScreen;
            if (screen == null)
            {
                return;
            }

            System.Drawing.Rectangle workArea = screen.WorkingArea;
            int right = workArea.Right - NotificationWidth - 18;
            int bottom = workArea.Bottom - 18;
            for (int index = 0; index < Windows.Count; index++)
            {
                int top = bottom - NotificationHeight;
                Windows[index].MoveTo(new RectInt32(right, top, NotificationWidth, NotificationHeight));
                bottom = top - 10;
            }
        }

        public static void Shutdown()
        {
            for (int index = Windows.Count - 1; index >= 0; index--)
            {
                Windows[index].Close();
            }
            Windows.Clear();

            if (!_registered)
            {
                return;
            }

            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch
            {
            }

            _registered = false;
        }
    }

    internal sealed class WinUINotificationWindow
    {
        private readonly WinUIWindow _window;
        private readonly DispatcherQueueTimer _timer;
        private readonly Action _clickAction;
        private bool _closed;

        public WinUINotificationWindow(string message, bool critical, Action clickAction)
        {
            _clickAction = clickAction;
            _window = new WinUIWindow();
            _window.Title = Constants.Title;
            _window.ExtendsContentIntoTitleBar = true;
            _window.AppWindow.IsShownInSwitchers = false;

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            Windows.UI.Color background = critical
                ? Windows.UI.Color.FromArgb(248, 64, 24, 24)
                : Windows.UI.Color.FromArgb(248, 28, 31, 38);
            Windows.UI.Color borderColor = critical
                ? Windows.UI.Color.FromArgb(255, 220, 70, 70)
                : Windows.UI.Color.FromArgb(255, 74, 144, 226);

            Grid root = new Grid
            {
                Padding = new Thickness(16, 12, 10, 12),
                Background = new SolidColorBrush(background)
            };
            root.PointerPressed += delegate
            {
                Close();
                _clickAction();
            };

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            Border iconFrame = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 43, 57)),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Microsoft.UI.Xaml.Controls.Image icon = new Microsoft.UI.Xaml.Controls.Image
            {
                Width = 30,
                Height = 30,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconFrame.Child = icon;
            Grid.SetColumn(iconFrame, 0);
            content.Children.Add(iconFrame);
            LoadNotificationIcon(icon);

            StackPanel textPanel = new StackPanel
            {
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock title = new TextBlock
            {
                Text = Constants.Title,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            };
            TextBlock body = new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 234, 242))
            };
            textPanel.Children.Add(title);
            textPanel.Children.Add(body);
            Grid.SetColumn(textPanel, 1);
            content.Children.Add(textPanel);

            Microsoft.UI.Xaml.Controls.Button closeButton = new Microsoft.UI.Xaml.Controls.Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                Content = new FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    Glyph = "\uE711",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 205, 212, 224))
                }
            };
            closeButton.Click += delegate { Close(); };
            Grid.SetColumn(closeButton, 2);
            content.Children.Add(closeButton);

            Border accent = new Border
            {
                Width = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(borderColor),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Margin = new Thickness(-12, 0, 10, 0)
            };
            root.Children.Add(accent);
            root.Children.Add(content);
            _window.Content = root;

            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(8);
            _timer.IsRepeating = false;
            _timer.Tick += delegate { Close(); };
            _window.Closed += delegate { OnClosed(); };
        }

        private static async void LoadNotificationIcon(Microsoft.UI.Xaml.Controls.Image image)
        {
            try
            {
                using (Stream source = typeof(WinUINotificationWindow).Assembly
                    .GetManifestResourceStream("AppIcon.png"))
                {
                    if (source == null)
                    {
                        return;
                    }

                    using (MemoryStream buffer = new MemoryStream())
                    {
                        source.CopyTo(buffer);
                        using (InMemoryRandomAccessStream randomAccess = new InMemoryRandomAccessStream())
                        {
                            using (DataWriter writer = new DataWriter(randomAccess.GetOutputStreamAt(0)))
                            {
                                writer.WriteBytes(buffer.ToArray());
                                await writer.StoreAsync();
                                await writer.FlushAsync();
                                writer.DetachStream();
                            }

                            randomAccess.Seek(0);
                            BitmapImage bitmap = new BitmapImage();
                            await bitmap.SetSourceAsync(randomAccess);
                            image.Source = bitmap;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public event Action Closed = delegate { };

        public void MoveTo(RectInt32 bounds)
        {
            try
            {
                _window.AppWindow.MoveAndResize(bounds);
            }
            catch
            {
            }
        }

        public void ShowNotification()
        {
            _window.Activate();
            _timer.Start();
        }

        public void Close()
        {
            if (_closed)
            {
                return;
            }

            try
            {
                _timer.Stop();
                _window.Close();
            }
            catch
            {
                OnClosed();
            }
        }

        private void OnClosed()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Closed();
        }
    }

    internal sealed class NativeTrayIcon : IDisposable
    {
        private const int WM_APP = 0x8000;
        private const int TrayCallbackMessage = WM_APP + 1;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;
        private const int NIM_SETVERSION = 0x00000004;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_INFO = 0x00000010;
        private const int NIF_SHOWTIP = 0x00000080;
        private const int NOTIFYICON_VERSION_4 = 4;
        private const int NIIF_INFO = 0x00000001;
        private const int NIIF_WARNING = 0x00000002;
        private const int NIIF_USER = 0x00000004;
        private const int NIIF_LARGE_ICON = 0x00000020;

        private readonly int _id;
        private readonly NativeMethods.WndProcDelegate _windowProc;
        private readonly string _className;
        private readonly IntPtr _instance;
        private readonly IntPtr _windowHandle;
        private readonly IntPtr _iconHandle;
        private readonly bool _ownsIcon;
        private bool _disposed;

        public NativeTrayIcon(int id, string tooltip, Icon appIcon)
        {
            _id = id;
            _windowProc = WindowProc;
            _className = "DeepSeekHarnessTray." + Guid.NewGuid().ToString("N");
            _instance = NativeMethods.GetModuleHandle(null);

            NativeMethods.WNDCLASSEX windowClass = new NativeMethods.WNDCLASSEX();
            windowClass.cbSize = Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX));
            windowClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc);
            windowClass.hInstance = _instance;
            windowClass.lpszClassName = _className;
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    "创建托盘消息窗口失败：" + Marshal.GetLastWin32Error().ToString());
            }

            _windowHandle = NativeMethods.CreateWindowEx(
                0,
                _className,
                Constants.Title,
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                _instance,
                IntPtr.Zero);
            if (_windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "创建托盘隐藏窗口失败：" + Marshal.GetLastWin32Error().ToString());
            }

            _iconHandle = LoadTrayIcon(appIcon, out _ownsIcon);
            NativeMethods.NOTIFYICONDATA data = CreateData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            data.uCallbackMessage = TrayCallbackMessage;
            data.hIcon = _iconHandle;
            data.szTip = TrimTooltip(tooltip);

            if (!NativeMethods.Shell_NotifyIcon(NIM_ADD, ref data))
            {
                throw new InvalidOperationException(
                    "添加系统托盘图标失败：" + Marshal.GetLastWin32Error().ToString());
            }

            data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            NativeMethods.Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }

        public event Action ContextMenuRequested = delegate { };
        public event Action DoubleClick = delegate { };

        public IntPtr WindowHandle
        {
            get { return _windowHandle; }
        }

        public void UpdateTip(string tooltip)
        {
            if (_disposed)
            {
                return;
            }

            NativeMethods.NOTIFYICONDATA data = CreateData();
            data.uFlags = NIF_TIP | NIF_SHOWTIP;
            data.szTip = TrimTooltip(tooltip);
            NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        public bool ShowBalloon(string message, bool critical)
        {
            if (_disposed)
            {
                return false;
            }

            string title = Constants.Title;
            string body = message.Length > 255 ? message.Substring(0, 255) : message;

            NativeMethods.NOTIFYICONDATA data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = body;
            data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
            data.hBalloonIcon = _iconHandle;
            if (NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data))
            {
                return true;
            }

            data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = body;
            data.dwInfoFlags = NIIF_USER;
            data.hBalloonIcon = _iconHandle;
            if (NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data))
            {
                return true;
            }

            data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = body;
            data.dwInfoFlags = critical ? NIIF_WARNING : NIIF_INFO;
            return NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        private IntPtr WindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == TrayCallbackMessage)
            {
                int trayMessage = (int)((long)lParam & 0xFFFF);
                if (trayMessage == WM_CONTEXTMENU || trayMessage == WM_RBUTTONUP)
                {
                    ContextMenuRequested();
                }
                else if (trayMessage == WM_LBUTTONDBLCLK)
                {
                    DoubleClick();
                }
            }

            return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
        }

        private NativeMethods.NOTIFYICONDATA CreateData()
        {
            NativeMethods.NOTIFYICONDATA data = new NativeMethods.NOTIFYICONDATA();
            data.cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATA));
            data.hWnd = _windowHandle;
            data.uID = _id;
            return data;
        }

        private static string TrimTooltip(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return Constants.Title;
            }

            return value.Length > 127 ? value.Substring(0, 127) : value;
        }

        private static IntPtr LoadTrayIcon(Icon appIcon, out bool ownsIcon)
        {
            ownsIcon = false;
            try
            {
                string executablePath = Environment.ProcessPath;
                if (!String.IsNullOrEmpty(executablePath))
                {
                    IntPtr large;
                    IntPtr small;
                    if (NativeMethods.ExtractIconEx(executablePath, 0, out large, out small, 1) > 0)
                    {
                        if (small != IntPtr.Zero)
                        {
                            NativeMethods.DestroyIcon(large);
                            ownsIcon = true;
                            return small;
                        }

                        if (large != IntPtr.Zero)
                        {
                            ownsIcon = true;
                            return large;
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (appIcon != null)
                {
                    return appIcon.Handle;
                }
            }
            catch
            {
            }

            return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            NativeMethods.NOTIFYICONDATA data = CreateData();
            NativeMethods.Shell_NotifyIcon(NIM_DELETE, ref data);
            if (_windowHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_windowHandle);
            }

            if (!String.IsNullOrEmpty(_className))
            {
                NativeMethods.UnregisterClass(_className, _instance);
            }

            if (_ownsIcon && _iconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_iconHandle);
            }
        }
    }

    internal static class NativeMethods
    {
        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const long WS_CAPTION = 0x00C00000L;
        internal const long WS_BORDER = 0x00800000L;
        internal const long WS_DLGFRAME = 0x00400000L;
        internal const long WS_THICKFRAME = 0x00040000L;
        internal const long WS_SYSMENU = 0x00080000L;
        internal const long WS_POPUP = unchecked((int)0x80000000);
        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int LWA_ALPHA = 0x00000002;
        internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        internal const int DWMWCP_ROUND = 2;
        internal const int DWMWA_BORDER_COLOR = 34;
        internal const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        internal const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        internal const uint AW_SLIDE = 0x00040000;
        internal const uint AW_VER_NEGATIVE = 0x00000008;
        internal const uint AW_ACTIVATE = 0x00020000;
        internal const uint EVENT_SYSTEM_FOREGROUND = 0x00000003;
        internal const uint WINEVENT_OUTOFCONTEXT = 0x00000000;
        internal const int WH_MOUSE_LL = 14;
        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const int WM_RBUTTONDOWN = 0x0204;
        internal const int WM_MBUTTONDOWN = 0x0207;
        internal const int WM_XBUTTONDOWN = 0x020B;
        internal const uint WM_NULL = 0x0000;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSLLHOOKSTRUCT
        {
            public POINT Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void WinEventDelegate(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr dataPointer);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            int extendedStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint ExtractIconEx(
            string file,
            int iconIndex,
            out IntPtr largeIcon,
            out IntPtr smallIcon,
            uint iconCount);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetLayeredWindowAttributes(
            IntPtr hWnd,
            uint colorKey,
            byte alpha,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr module,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelMouseProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr message,
            IntPtr dataPointer);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int value,
            int valueSize);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenu(
            IntPtr menu,
            uint flags,
            UIntPtr itemId,
            string itemText);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint TrackPopupMenuEx(
            IntPtr menu,
            uint flags,
            int x,
            int y,
            IntPtr owner,
            IntPtr parameters);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AnimateWindow(
            IntPtr window,
            uint duration,
            uint flags);
    }
}
