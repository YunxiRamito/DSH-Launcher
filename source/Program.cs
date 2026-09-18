using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeepSeekHarnessLauncher
{
    internal static class Constants
    {
        public const string Title = "DeepSeek Harness";
        public const string Url = "http://127.0.0.1:8787/";
        public const string DefaultRoot = @"G:\DeepSeek DSH";
        public const string DefaultNode = @"E:\Nodejs\node.exe";
        public const int Port = 8787;
    }

    internal static class Program
    {
        private const string MutexName = @"Local\DeepSeekHarness.Launcher";
        private const string OpenPageEventName = @"Local\DeepSeekHarness.OpenPage";

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalExistingInstance();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (EventWaitHandle openPageEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    OpenPageEventName))
                using (LauncherContext context = new LauncherContext(openPageEvent))
                {
                    Application.Run(context);
                }

                GC.KeepAlive(mutex);
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

                ProcessStartInfo startInfo = new ProcessStartInfo(url);
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch
            {
                // The tray menu reports failures with a branded dialog.
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
    }

    internal sealed class LauncherContext : ApplicationContext
    {
        private readonly string _root;
        private readonly string _nodePath;
        private readonly string _dshBin;
        private readonly string _logPath;
        private readonly string _lastUrlPath;
        private readonly string _apiPromptedPath;
        private readonly Icon _appIcon;
        private readonly NotifyIcon _tray;
        private readonly EventWaitHandle _openPageEvent;
        private readonly Form _dispatcher;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _balanceItem;
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _restartItem;
        private readonly ToolStripMenuItem _forceStopItem;
        private readonly ToolStripMenuItem _exitItem;
        private readonly System.Windows.Forms.Timer _balanceTimer;
        private readonly DeepSeekBalanceAlertTracker _balanceAlertTracker;
        private readonly object _logLock = new object();
        private readonly StreamWriter _logWriter;

        private Process _service;
        private string _apiKey;
        private string _lastBalanceText;
        private DateTime _lastBalanceUpdatedUtc;
        private int _balanceRequestInProgress;
        private bool _serviceRunning;
        private bool _suppressExitNotification;
        private volatile bool _allowExit;
        private bool _disposed;
        private string _serviceUrl;

        public LauncherContext(EventWaitHandle openPageEvent)
        {
            _openPageEvent = openPageEvent;
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
            WriteLog("Launcher started.");

            _dispatcher = new Form();
            _dispatcher.ShowInTaskbar = false;
            _dispatcher.FormBorderStyle = FormBorderStyle.None;
            _dispatcher.StartPosition = FormStartPosition.Manual;
            _dispatcher.Location = new Point(-32000, -32000);
            _dispatcher.Size = new Size(1, 1);
            _dispatcher.Opacity = 0;
            IntPtr unusedHandle = _dispatcher.Handle;
            MainForm = _dispatcher;

            _appIcon = LoadAppIcon();

            _menu = new ContextMenuStrip();
            _menu.Opening += MenuOpening;

            _balanceItem = new ToolStripMenuItem();
            _balanceItem.Font = new Font(_balanceItem.Font, FontStyle.Bold);
            _balanceItem.Click += BalanceItemClick;

            _openItem = new ToolStripMenuItem("打开页面");
            _openItem.Font = new Font(_openItem.Font, FontStyle.Bold);
            _openItem.Click += OpenItemClick;

            _restartItem = new ToolStripMenuItem("重启 DSH 服务");
            _restartItem.Enabled = false;
            _restartItem.Click += RestartItemClick;

            _forceStopItem = new ToolStripMenuItem("强行终止");
            _forceStopItem.Enabled = false;
            _forceStopItem.Click += ForceStopItemClick;

            _exitItem = new ToolStripMenuItem("退出");
            _exitItem.Click += ExitItemClick;

            _menu.Items.Add(_balanceItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_openItem);
            _menu.Items.Add(_restartItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_forceStopItem);
            _menu.Items.Add(_exitItem);

            _tray = new NotifyIcon();
            _tray.Icon = _appIcon;
            _tray.Text = Constants.Title + " 正在启动...";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.DoubleClick += TrayDoubleClick;

            Thread startupThread = new Thread(StartupThreadProc);
            startupThread.IsBackground = true;
            startupThread.Name = "DeepSeekHarnessStartup";
            startupThread.Start();

            Thread openPageThread = new Thread(OpenPageEventThreadProc);
            openPageThread.IsBackground = true;
            openPageThread.Name = "DeepSeekHarnessOpenPage";
            openPageThread.Start();

            _balanceTimer = new System.Windows.Forms.Timer();
            _balanceTimer.Interval = 60000;
            _balanceTimer.Tick += delegate(object timerSender, EventArgs timerArgs)
            {
                RefreshBalanceAsync();
            };
            _balanceTimer.Start();

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

        private void OpenPageEventThreadProc()
        {
            while (!_allowExit)
            {
                try
                {
                    if (_openPageEvent.WaitOne(500))
                    {
                        InvokeOnUi(OpenServicePage);
                    }
                }
                catch
                {
                    return;
                }
            }
        }

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs eventArgs)
        {
            RefreshBalanceAsync();
        }

        private void BalanceItemClick(object sender, EventArgs eventArgs)
        {
            ShowApiSettings();
        }

        private void PromptForApiKeyOnFirstRun()
        {
            if (!String.IsNullOrEmpty(_apiKey) || File.Exists(_apiPromptedPath))
            {
                return;
            }

            System.Windows.Forms.Timer promptTimer = new System.Windows.Forms.Timer();
            promptTimer.Interval = 1500;
            promptTimer.Tick += delegate(object timerSender, EventArgs timerArgs)
            {
                promptTimer.Stop();
                promptTimer.Dispose();
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
            promptTimer.Start();
        }

        private void ShowApiSettings()
        {
            using (ApiSettingsDialog dialog = new ApiSettingsDialog(
                _appIcon,
                _apiKey))
            {
                if (dialog.ShowDialog(_dispatcher) != DialogResult.OK)
                {
                    return;
                }

                string apiKey = dialog.ApiKey;
                try
                {
                    bool apiKeyChanged = !String.Equals(
                        _apiKey,
                        apiKey,
                        StringComparison.Ordinal);
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
                    MessageBox.Show(
                        _dispatcher,
                        "API 设置保存失败：" + exception.Message,
                        Constants.Title,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
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
                _balanceItem.Text = _lastBalanceText;
                _balanceItem.ToolTipText =
                    "点击修改 API 设置。更新时间："
                    + result.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss");

                if (alertUpdate != null && alertUpdate.IsCny)
                {
                    _balanceItem.ToolTipText +=
                        "；今日已用：" + FormatMoney(alertUpdate.TodaySpend);
                }

                if (alertUpdate != null && !String.IsNullOrEmpty(alertUpdate.Notification))
                {
                    ShowNotification(
                        alertUpdate.Notification,
                        alertUpdate.Critical ? ToolTipIcon.Warning : ToolTipIcon.Info);
                }

                return;
            }

            if (!String.IsNullOrEmpty(_lastBalanceText))
            {
                _balanceItem.Text = _lastBalanceText;
                _balanceItem.ToolTipText =
                    "点击修改 API 设置。刷新失败：" + result.Error;
                return;
            }

            _balanceItem.Text = "余额：查询失败（点击设置 API）";
            _balanceItem.ToolTipText = result.Error;
        }

        private static string FormatMoney(decimal amount)
        {
            return "¥"
                + amount.ToString(
                    "0.00",
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        private void SetBalanceLoading()
        {
            if (String.IsNullOrEmpty(_lastBalanceText))
            {
                _balanceItem.Text = "余额：正在查询...";
            }

            _balanceItem.ToolTipText = "正在从 DeepSeek 查询余额，点击可修改 API 设置。";
        }

        private void SetBalanceUnconfigured()
        {
            _balanceItem.Text = "余额：未配置（点击设置 API）";
            _balanceItem.ToolTipText = "点击后填写 DeepSeek API Key，保存后自动刷新余额。";
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
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("AppIcon.ico"))
            {
                if (stream == null)
                {
                    return SystemIcons.Application;
                }

                using (Icon icon = new Icon(stream))
                {
                    return (Icon)icon.Clone();
                }
            }
        }

        private void StartupThreadProc()
        {
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
                            int exitCode = _service.ExitCode;
                            failureMessage = "DeepSeek Harness 服务启动失败，进程已退出。退出代码：" + exitCode;
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
            startInfo.Arguments = QuoteArgument(_dshBin)
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
            WriteLog("Started node process " + process.Id.ToString() + ".");

            Thread outputThread = new Thread(delegate() { ReadProcessStream(process.StandardOutput, "OUT"); });
            outputThread.IsBackground = true;
            outputThread.Start();

            Thread errorThread = new Thread(delegate() { ReadProcessStream(process.StandardError, "ERR"); });
            errorThread.IsBackground = true;
            errorThread.Start();

            return true;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private void ReadProcessStream(StreamReader reader, string prefix)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    WriteLog(prefix + " " + line);
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
                if (_allowExit)
                {
                    return;
                }

                SetTrayState(false, Constants.Title + " 已停止");
            });
        }

        private void FinishSuccessfulStartup(bool openPage)
        {
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
                    openPage
                        ? "DeepSeek Harness 服务启动已成功。"
                        : "DeepSeek Harness 服务重启成功。",
                    ToolTipIcon.Info);

                if (openPage)
                {
                    System.Windows.Forms.Timer openTimer = new System.Windows.Forms.Timer();
                    openTimer.Interval = 1200;
                    openTimer.Tick += delegate(object timerSender, EventArgs timerArgs)
                    {
                        openTimer.Stop();
                        openTimer.Dispose();
                        Program.OpenPage(url);
                    };
                    openTimer.Start();
                }
            });
        }

        private void FailStartup(string message)
        {
            WriteLog("Startup failed: " + message);
            InvokeOnUi(delegate()
            {
                SetTrayState(false, Constants.Title + " 启动失败");
                ShowNotification(message, ToolTipIcon.Error);

                System.Windows.Forms.Timer exitTimer = new System.Windows.Forms.Timer();
                exitTimer.Interval = 10000;
                exitTimer.Tick += delegate(object timerSender, EventArgs timerArgs)
                {
                    exitTimer.Stop();
                    exitTimer.Dispose();
                    _allowExit = true;
                    _openPageEvent.Set();
                    _tray.Visible = false;
                    ExitThread();
                };
                exitTimer.Start();
            });
        }

        private void SetTrayState(bool running, string tooltip)
        {
            _serviceRunning = running;
            _openItem.Enabled = running;
            _restartItem.Enabled = true;
            _forceStopItem.Enabled = running;
            if (tooltip.Length > 63)
            {
                tooltip = tooltip.Substring(0, 63);
            }

            _tray.Text = tooltip;
        }

        private void TrayDoubleClick(object sender, EventArgs eventArgs)
        {
            OpenServicePage();
        }

        private void OpenItemClick(object sender, EventArgs eventArgs)
        {
            OpenServicePage();
        }

        private void OpenServicePage()
        {
            WriteLog("Open page requested.");
            if (!_serviceRunning || !IsServiceReady())
            {
                MessageBox.Show(
                    _dispatcher,
                    "DeepSeek Harness 服务当前没有运行。请使用“重启 DSH 服务”重新启动。",
                    Constants.Title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            Program.OpenPage(_serviceUrl);
        }

        private void RestartItemClick(object sender, EventArgs eventArgs)
        {
            DialogResult result = MessageBox.Show(
                _dispatcher,
                "确定要重启 DeepSeek Harness 服务吗？当前网页连接会暂时中断。",
                Constants.Title,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.OK)
            {
                return;
            }

            _restartItem.Enabled = false;
            _openItem.Enabled = false;
            _forceStopItem.Enabled = false;
            _tray.Text = Constants.Title + " 正在重启...";
            ShowNotification("正在重启 DeepSeek Harness 服务...", ToolTipIcon.Info);

            Thread restartThread = new Thread(RestartThreadProc);
            restartThread.IsBackground = true;
            restartThread.Name = "DeepSeekHarnessRestart";
            restartThread.Start();
        }

        private void RestartThreadProc()
        {
            WriteLog("Restart requested.");
            try
            {
                StopService();
                _serviceUrl = null;
                _suppressExitNotification = false;

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
            WriteLog("Restart failed: " + message);
            InvokeOnUi(delegate()
            {
                SetTrayState(false, Constants.Title + " 重启失败");
                ShowNotification(message, ToolTipIcon.Error);
            });
        }

        private void ForceStopItemClick(object sender, EventArgs eventArgs)
        {
            DialogResult result = MessageBox.Show(
                _dispatcher,
                "确定要强行终止 DeepSeek Harness 服务吗？",
                Constants.Title,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.OK)
            {
                return;
            }

            if (StopService())
            {
                SetTrayState(false, Constants.Title + " 已停止");
                ShowNotification("DeepSeek Harness 服务已被强行终止。", ToolTipIcon.Warning);
            }
            else
            {
                ShowNotification(
                    "没有找到正在运行的 DeepSeek Harness 服务。",
                    ToolTipIcon.Error);
            }
        }

        private void ExitItemClick(object sender, EventArgs eventArgs)
        {
            if (_serviceRunning)
            {
                DialogResult result = MessageBox.Show(
                    _dispatcher,
                    "退出托盘程序将同时停止 DeepSeek Harness 服务。是否继续？",
                    Constants.Title,
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.OK)
                {
                    return;
                }

                StopService();
            }

            _allowExit = true;
            _openPageEvent.Set();
            _tray.Visible = false;
            ExitThread();
        }

        private void ShowNotification(string message, ToolTipIcon icon)
        {
            if (!_tray.Visible)
            {
                _tray.Visible = true;
            }

            _tray.BalloonTipTitle = Constants.Title;
            _tray.BalloonTipText = message;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(7000);
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

                    string[] lines = output.Split(new string[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    for (int index = 0; index < lines.Length; index++)
                    {
                        string line = lines[index].Trim();
                        string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
            catch (Exception exception)
            {
                WriteLog("Could not inspect the listening port: " + exception.Message);
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

        private void InvokeOnUi(MethodInvoker action)
        {
            try
            {
                if (_dispatcher.IsDisposed || !_dispatcher.IsHandleCreated)
                {
                    return;
                }

                _dispatcher.BeginInvoke(action);
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

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }

                if (_menu != null)
                {
                    _menu.Dispose();
                }

                if (_dispatcher != null)
                {
                    _dispatcher.Dispose();
                }

                if (_appIcon != null && !ReferenceEquals(_appIcon, SystemIcons.Application))
                {
                    _appIcon.Dispose();
                }

                if (_logWriter != null)
                {
                    WriteLog("Launcher stopped.");
                    _logWriter.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

}
