using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow : Window
    {
        private const int DefaultWindowWidth = 1200;
        private const int DefaultWindowHeight = 720;
        private const int MinimumWindowWidth = 800;
        private const int MinimumWindowHeight = 560;
        private const int WorkAreaMargin = 48;
        private const double NavigationCompactThreshold = 880;
        private const int GwlpExtendedStyle = -20;

        /// <summary>开发者管理后台。只在回环地址上监听，由启动器自己拉起来。</summary>
        private const string DeveloperCenterUrl = "http://127.0.0.1:8788/";

        private readonly AppWindow _appWindow;
        private readonly IntPtr _windowHandle;
        private readonly SettingsWindowHost _host;
        private readonly LauncherSettings _settings;
        private bool _authorAvatarLoading;
        private bool _initializing = true;
        private bool _suppressNavigation;
        private int _versionTapCount;
        private DateTime _lastVersionTapUtc = DateTime.MinValue;

        public event Action Destroyed = delegate { };

        public SettingsWindow()
            : this(IntPtr.Zero, null)
        {
        }

        public SettingsWindow(IntPtr ownerHandle)
            : this(ownerHandle, null)
        {
        }

        public SettingsWindow(
            IntPtr ownerHandle,
            SettingsWindowHost host)
        {
            InitializeComponent();
            _host = host ?? CreatePreviewHost();
            _settings = _host.Settings ?? new LauncherSettings();
            AccentColorPicker.Color = Windows.UI.Color.FromArgb(255, 10, 132, 255);

            Title = "大肥鱼Go设置";
            VersionText.Text = "v" + Constants.Version;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            LauncherAppearance.Register(
                this,
                SettingsRoot,
                delegate(Brush brush) { SettingsRoot.Background = brush; });

            _windowHandle = WindowNative.GetWindowHandle(this);

            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            ConfigureTaskbarWindow();

            ConfigureWindow(windowId);
            SettingsRoot.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(SettingsRoot_PointerPressed),
                true);
            LoadSettingsIntoControls();
            ApplyTheme();
            ApplyAdaptiveIcons();
            ApplyAccent();
            LauncherAppearance.SetMaterial(
                ResolveMaterial(_settings.Material));
            UpdatePortModeControls();
            UpdateUpdateOptions();
            LoadPluginCardSamples();
            RefreshComponents();
            WireSettingsEvents();

            SettingsRoot.SizeChanged += SettingsRoot_SizeChanged;
            SettingsRoot.ActualThemeChanged += SettingsRoot_ActualThemeChanged;
            Closed += SettingsWindow_Closed;

            _initializing = false;
            SettingsNavigationView.SelectedItem = GeneralNavItem;
            SelectPage("General");
            ApplyResponsiveLayout(SettingsRoot.ActualWidth > 0 ? SettingsRoot.ActualWidth : DefaultWindowWidth);
            _ = RefreshApiBalanceAsync();
        }

        private void ConfigureTaskbarWindow()
        {
            try
            {
                _appWindow.IsShownInSwitchers = true;
                long style = NativeMethods.GetWindowLongPtr(
                    _windowHandle,
                    GwlpExtendedStyle).ToInt64();
                style &= ~NativeMethods.WS_EX_TOOLWINDOW;
                style |= NativeMethods.WS_EX_APPWINDOW;
                NativeMethods.SetWindowLongPtr(
                    _windowHandle,
                    GwlpExtendedStyle,
                    new IntPtr(style));
            }
            catch
            {
            }

            try
            {
                string iconPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "DeepSeekHarness.ico");
                if (File.Exists(iconPath))
                {
                    _appWindow.SetIcon(iconPath);
                }
            }
            catch
            {
            }
        }

        private static SettingsWindowHost CreatePreviewHost()
        {
            string detectedRoot = LauncherLocator.FindRoot();
            LauncherSettings settings = Program.Settings
                ?? LauncherSettingsStore.LoadOrCreate(
                    AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'),
                    detectedRoot);
            if (String.IsNullOrWhiteSpace(settings.DshRoot)
                && !String.IsNullOrWhiteSpace(detectedRoot))
            {
                settings.DshRoot = detectedRoot;
            }

            LauncherSettingsStore.EnsureLegacyApiKeyMigrated(
                settings,
                settings.DshRoot);
            LauncherSettingsStore.Save(settings);
            return new SettingsWindowHost
            {
                Settings = settings,
                GetServiceStatus = delegate
                {
                    return "预览模式，未连接服务";
                },
                Log = PreviewLog
            };
        }

        /// <summary>预览模式没有主进程，日志直接落到 launcher.log，方便排查界面数据。</summary>
        private static void PreviewLog(string message)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                File.AppendAllText(
                    Path.Combine(
                        LauncherSettingsStore.DirectoryPath,
                        "settings-preview.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  [preview] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private void ApplyAdaptiveIcons()
        {
            string folder = CornerRadiusHelper.IsWindows11
                ? "SettingsNavIcons"
                : (SettingsRoot.ActualTheme == ElementTheme.Dark
                    ? "SettingsNavIconsWin10Dark"
                    : "SettingsNavIconsWin10");
            SetSvgIcon(GeneralNavItem, folder, "general.svg");
            SetSvgIcon(ThemeNavItem, folder, "theme.svg");
            SetSvgIcon(ApiNavItem, folder, "api.svg");
            SetSvgIcon(AlertsNavItem, folder, "alerts.svg");
            SetSvgIcon(ServiceNavItem, folder, "service.svg");
            SetSvgIcon(PluginsNavItem, folder, "plugins.svg");
            SetSvgIcon(ComponentsNavItem, folder, "components.svg");
            SetSvgIcon(UpdatesNavItem, folder, "updates.svg");
            SetSvgIcon(AboutNavItem, folder, "about.svg");
        }

        private static void SetSvgIcon(
            NavigationViewItem item,
            string folder,
            string fileName)
        {
            item.Icon = new ImageIcon
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
                    new Uri(
                        "ms-appx:///assets/"
                        + folder
                        + "/"
                        + fileName))
            };
        }

        private void SettingsRoot_ActualThemeChanged(
            FrameworkElement sender,
            object args)
        {
            ApplyAdaptiveIcons();
        }

        private void LoadSettingsIntoControls()
        {
            StartWithWindowsToggle.IsOn = _settings.StartWithWindows;
            FixedPortBox.Value = _settings.FixedPort;
            RandomPortRadio.IsChecked = _settings.PortMode == "Random";
            FixedPortRadio.IsChecked = _settings.PortMode == "Fixed";
            DefaultPortRadio.IsChecked = _settings.PortMode == "Default";
            SelectTaggedItem(SilentStartComboBox, _settings.SilentStart);
            SelectTaggedItem(ThemeComboBox, _settings.Theme);
            SelectTaggedItem(AccentSourceComboBox, _settings.AccentSource);
            AccentColorPicker.Color = ParseColor(_settings.AccentColor);
            AccentColorSwatch.Background =
                new SolidColorBrush(AccentColorPicker.Color);
            SelectTaggedItem(MaterialComboBox, _settings.Material);
            SelectTaggedItem(UpdateSourceComboBox, _settings.UpdateSource);
            RebuildPluginSourceOptions();
            SelectTaggedItem(PluginSourceComboBox, _settings.PluginSource);
            SelectTaggedItem(
                LauncherUpdateModeComboBox,
                _settings.LauncherUpdateMode);
            SelectTaggedItem(DshUpdateModeComboBox, _settings.DshUpdateMode);
            SelectTaggedItem(
                UpdateIntervalComboBox,
                _settings.UpdateInterval);
            SelectTaggedItem(
                PluginUpdateModeComboBox,
                _settings.PluginUpdateMode);

            UpdateReminderToggle.IsOn = _settings.UpdateReminder;
            PluginUpdateReminderToggle.IsOn = _settings.PluginUpdateReminder;
            ServiceReminderToggle.IsOn = _settings.ServiceStartReminder;
            RechargeReminderToggle.IsOn = _settings.RechargeReminder;

            SelectRadioByTag(ProxyModeSelector, _settings.ProxyMode, "None");
            SelectRadioByTag(ProxyProtocolSelector, _settings.ProxyProtocol, "Http");
            ProxyHostBox.Text = _settings.ProxyHost;
            ProxyPortBox.Value = _settings.ProxyPort;
            UpdateProxyCustomPanel();
            _host.Log(
                "Loaded proxy settings: "
                + _settings.ProxyMode
                + " / "
                + _settings.ProxyProtocol
                + " / "
                + _settings.ProxyHost
                + ":"
                + _settings.ProxyPort);

            Spend5CheckBox.IsChecked = _settings.SpendAlert5;
            Spend10CheckBox.IsChecked = _settings.SpendAlert10;
            Spend20CheckBox.IsChecked = _settings.SpendAlert20;
            Spend50CheckBox.IsChecked = _settings.SpendAlert50;
            SpendCustomCheckBox.IsChecked = _settings.SpendAlertCustom;
            SpendCustomBox.Value = (double)_settings.SpendCustomAmount;

            Balance20CheckBox.IsChecked = _settings.BalanceAlert20;
            Balance10CheckBox.IsChecked = _settings.BalanceAlert10;
            Balance5CheckBox.IsChecked = _settings.BalanceAlert5;
            Balance1CheckBox.IsChecked = _settings.BalanceAlert1;
            BalanceCustomCheckBox.IsChecked = _settings.BalanceAlertCustom;
            BalanceCustomBox.Value = (double)_settings.BalanceCustomAmount;

            DshPathBox.Text = _settings.DshRoot ?? String.Empty;
            NodePathBox.Text = _settings.NodePath ?? String.Empty;
            ApiKeyBox.Password = LauncherSettingsStore.ReadApiKey(_settings);
            GitHubTokenBox.Password =
                LauncherSettingsStore.ReadGitHubToken(_settings);
            // 开发者入口只在本窗口会话内有效，每次重新打开设置都要重新解锁。
            _settings.DeveloperModeUnlocked = false;
            DeveloperNavItem.Visibility = Visibility.Collapsed;
            RefreshServiceState();
            UpdateCustomThresholdStates();
        }

        private void WireSettingsEvents()
        {
            StartWithWindowsToggle.Toggled += StartWithWindowsToggle_Toggled;
            SilentStartComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            UpdateSourceComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            PluginSourceComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            UpdateIntervalComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            FixedPortBox.ValueChanged += FixedPortBox_ValueChanged;
            ProxyProtocolSelector.SelectionChanged += ProxySetting_SelectionChanged;
            ProxyHostBox.TextChanged += ProxyHostBox_TextChanged;
            ProxyPortBox.ValueChanged += ProxyPortBox_ValueChanged;
            RecheckComponentsButton.Click += delegate
            {
                RefreshComponents();
            };
            PluginCategoryComboBox.SelectionChanged += OnlineFilter_Changed;
            PluginSortComboBox.SelectionChanged += OnlineFilter_Changed;
            PluginLanguageComboBox.SelectionChanged += OnlineFilter_Changed;
            PluginPageSizeComboBox.SelectionChanged += OnlinePageSize_Changed;
            PluginSearchBox.TextChanged += OnlineSearch_Changed;
            FeaturedPluginSearchBox.TextChanged += FeaturedSearch_Changed;
            FeaturedPreviousPageButton.Click += delegate
            {
                _featuredPage--;
                RebuildFeaturedPage();
            };
            FeaturedNextPageButton.Click += delegate
            {
                _featuredPage++;
                RebuildFeaturedPage();
            };
            LocalPluginSearchBox.TextChanged += LocalSearch_Changed;
            RefreshPluginCatalogButton.Click += delegate
            {
                LoadOnlinePlugins(true);
            };
            RecheckLocalPluginsButton.Click += delegate
            {
                LoadLocalPlugins();
            };
            UpdateAllPluginsButton.Click += delegate
            {
                _host.InstallPluginUpdates();
                PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
                PluginActionInfoBar.Title = "正在更新全部插件";
                PluginActionInfoBar.Message = "更新在后台继续执行。";
                PluginActionInfoBar.IsOpen = true;
            };
            CheckPluginUpdateButton.Click += delegate
            {
                UpdateUiSnapshot state = _host.GetPluginUpdateState();
                if (state.Activity == UpdateUiActivity.Available)
                {
                    _host.InstallPluginUpdates();
                }
                else
                {
                    _host.CheckPluginUpdates();
                }
            };
            PluginPreviousPageButton.Click += delegate
            {
                _catalogPage--;
                RebuildOnlinePage(false);
            };
            PluginNextPageButton.Click += delegate
            {
                _catalogPage++;
                RebuildOnlinePage(false);
            };
            LocalPageSizeComboBox.SelectionChanged += LocalPageSize_Changed;
            LocalPreviousPageButton.Click += delegate
            {
                _localPage--;
                RebuildLocalPage();
            };
            LocalNextPageButton.Click += delegate
            {
                _localPage++;
                RebuildLocalPage();
            };

            UpdateReminderToggle.Toggled += ReminderToggle_Toggled;
            PluginUpdateReminderToggle.Toggled += ReminderToggle_Toggled;
            ServiceReminderToggle.Toggled += ReminderToggle_Toggled;
            RechargeReminderToggle.Toggled += ReminderToggle_Toggled;

            Spend5CheckBox.Checked += ThresholdCheckBox_Changed;
            Spend5CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Spend10CheckBox.Checked += ThresholdCheckBox_Changed;
            Spend10CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Spend20CheckBox.Checked += ThresholdCheckBox_Changed;
            Spend20CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Spend50CheckBox.Checked += ThresholdCheckBox_Changed;
            Spend50CheckBox.Unchecked += ThresholdCheckBox_Changed;
            SpendCustomCheckBox.Checked += ThresholdCheckBox_Changed;
            SpendCustomCheckBox.Unchecked += ThresholdCheckBox_Changed;
            SpendCustomBox.ValueChanged += ThresholdNumberBox_ValueChanged;

            Balance20CheckBox.Checked += ThresholdCheckBox_Changed;
            Balance20CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Balance10CheckBox.Checked += ThresholdCheckBox_Changed;
            Balance10CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Balance5CheckBox.Checked += ThresholdCheckBox_Changed;
            Balance5CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Balance1CheckBox.Checked += ThresholdCheckBox_Changed;
            Balance1CheckBox.Unchecked += ThresholdCheckBox_Changed;
            BalanceCustomCheckBox.Checked += ThresholdCheckBox_Changed;
            BalanceCustomCheckBox.Unchecked += ThresholdCheckBox_Changed;
            BalanceCustomBox.ValueChanged += ThresholdNumberBox_ValueChanged;

            RestartServiceButton.Click += delegate
            {
                _host.RestartService();
                RefreshServiceState();
            };
            StopServiceButton.Click += delegate
            {
                _host.StopService();
                RefreshServiceState();
            };
            RecheckEnvironmentButton.Click += delegate
            {
                _host.RecheckEnvironment();
                DshPathBox.Text = _settings.DshRoot ?? String.Empty;
                NodePathBox.Text = _settings.NodePath ?? String.Empty;
            };
            CheckLauncherUpdateButton.Click += delegate
            {
                UpdateUiSnapshot state = _host.GetLauncherUpdateState();
                if (state.Activity == UpdateUiActivity.Available)
                {
                    _host.InstallLauncherUpdate();
                }
                else
                {
                    _host.CheckLauncherUpdate();
                }
            };
            CheckDshUpdateButton.Click += delegate
            {
                UpdateUiSnapshot state = _host.GetDshUpdateState();
                if (state.Activity == UpdateUiActivity.Available)
                {
                    _host.InstallDshUpdate();
                }
                else
                {
                    _host.CheckDshUpdate();
                }
            };
            _host.UpdateStateChanged += Host_UpdateStateChanged;
            _host.ServiceStateChanged += Host_ServiceStateChanged;
            RefreshUpdateStates();
        }

        private void Host_UpdateStateChanged()
        {
            RefreshUpdateStates();
            UpdateUiSnapshot pluginState = _host.GetPluginUpdateState();
            if (pluginState != null
                && (pluginState.Activity == UpdateUiActivity.Completed
                    || pluginState.Activity == UpdateUiActivity.UpToDate))
            {
                LoadLocalPlugins();
            }
        }

        private void Host_ServiceStateChanged()
        {
            RefreshServiceState();
        }

        private void RefreshUpdateStates()
        {
            ApplyUpdateState(
                _host.GetLauncherUpdateState(),
                CheckLauncherUpdateButton,
                LauncherUpdateProgressPanel,
                LauncherUpdateProgressText,
                LauncherUpdateProgressBar,
                LauncherUpdateResultInfoBar);
            ApplyUpdateState(
                _host.GetDshUpdateState(),
                CheckDshUpdateButton,
                DshUpdateProgressPanel,
                DshUpdateProgressText,
                DshUpdateProgressBar,
                DshUpdateResultInfoBar);
            ApplyUpdateState(
                _host.GetPluginUpdateState(),
                CheckPluginUpdateButton,
                PluginUpdateProgressPanel,
                PluginUpdateProgressText,
                PluginUpdateProgressBar,
                PluginUpdateResultInfoBar);
        }

        private static void ApplyUpdateState(
            UpdateUiSnapshot state,
            Button actionButton,
            StackPanel progressPanel,
            TextBlock progressText,
            ProgressBar progressBar,
            InfoBar resultBar)
        {
            if (state == null)
            {
                state = new UpdateUiSnapshot();
            }

            bool busy = state.Activity == UpdateUiActivity.Checking
                || state.Activity == UpdateUiActivity.Installing;
            actionButton.IsEnabled = !busy;
            progressPanel.Visibility = busy
                ? Visibility.Visible
                : Visibility.Collapsed;
            resultBar.IsOpen = state.Activity == UpdateUiActivity.UpToDate
                || state.Activity == UpdateUiActivity.Available
                || state.Activity == UpdateUiActivity.Completed
                || state.Activity == UpdateUiActivity.Failed;
            resultBar.Severity = state.Activity == UpdateUiActivity.Failed
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Success;

            switch (state.Activity)
            {
                case UpdateUiActivity.Checking:
                    actionButton.Content = "检测更新中";
                    progressText.Text = "检测更新中";
                    progressBar.IsIndeterminate = true;
                    resultBar.IsOpen = false;
                    break;
                case UpdateUiActivity.Installing:
                    actionButton.Content = "更新中";
                    progressText.Text =
                        state.ProgressText
                        + (String.IsNullOrWhiteSpace(state.Detail)
                            ? String.Empty
                            : " · " + state.Detail);
                    progressBar.IsIndeterminate = state.IsIndeterminate;
                    progressBar.Value = Math.Max(0, state.Progress);
                    resultBar.IsOpen = false;
                    break;
                case UpdateUiActivity.UpToDate:
                    actionButton.Content = "立即检查";
                    resultBar.Title = "已是新版本";
                    resultBar.Message = String.IsNullOrWhiteSpace(state.Version)
                        ? state.Detail
                        : "v" + state.Version;
                    break;
                case UpdateUiActivity.Available:
                    actionButton.Content = "立即更新";
                    resultBar.Title = "发现新版本";
                    resultBar.Message = String.IsNullOrWhiteSpace(state.Version)
                        ? state.Detail
                        : "v" + state.Version;
                    break;
                case UpdateUiActivity.Completed:
                    actionButton.Content = "立即检查";
                    resultBar.Title = "操作完成";
                    resultBar.Message = state.Detail;
                    break;
                case UpdateUiActivity.Failed:
                    actionButton.Content = "立即检查";
                    resultBar.Title = "检查或更新失败";
                    resultBar.Message = state.Detail;
                    break;
                default:
                    actionButton.Content = "立即检查";
                    resultBar.IsOpen = false;
                    break;
            }
        }

        private void StartWithWindowsToggle_Toggled(
            object sender,
            RoutedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            bool requested = StartWithWindowsToggle.IsOn;
            bool changed = requested
                ? StartupSupport.Enable()
                : StartupSupport.Disable();
            if (!changed)
            {
                _initializing = true;
                StartWithWindowsToggle.IsOn = !requested;
                _initializing = false;
                _ = ShowMessageDialogAsync(
                    "开机自启动",
                    "修改开机自启动设置失败，请查看 launcher.log。");
                return;
            }

            _settings.StartWithWindows = requested;
            SaveSettings();
        }

        private void SettingComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.SilentStart = GetSelectedTag(
                SilentStartComboBox,
                "StartupOnly");
            _settings.UpdateSource = GetSelectedTag(
                UpdateSourceComboBox,
                "Accelerated");
            if (ReferenceEquals(sender, UpdateSourceComboBox))
            {
                // 换档位会改变插件来源的可选项（GitHub 大陆节点 / GitHub 官方）
                RebuildPluginSourceOptions();
            }

            _settings.PluginSource = GetSelectedTag(
                PluginSourceComboBox,
                "Market");
            _settings.UpdateInterval = GetSelectedTag(
                UpdateIntervalComboBox,
                "EveryStart");
            SaveSettings();
        }

        /// <summary>按在线引擎档位重建插件来源选项，尽量保留用户已经选过的项。</summary>
        private void RebuildPluginSourceOptions()
        {
            if (PluginSourceComboBox == null || UpdateSourceComboBox == null)
            {
                return;
            }

            string engine = GetSelectedTag(UpdateSourceComboBox, "Accelerated");
            string current = _settings == null
                ? "Market"
                : _settings.PluginSource;

            PluginSourceComboBox.Items.Clear();
            PluginSourceComboBox.Items.Add(new ComboBoxItem
            {
                Content = "DSH 插件市场（优先）",
                Tag = "Market"
            });
            PluginSourceComboBox.Items.Add(new ComboBoxItem
            {
                Content = String.Equals(
                    engine,
                    "Official",
                    StringComparison.OrdinalIgnoreCase)
                    ? "GitHub 官方"
                    : "GitHub 大陆节点",
                Tag = "GitHub"
            });
            SelectTaggedItem(PluginSourceComboBox, current);
            if (PluginSourceComboBox.SelectedIndex < 0)
            {
                PluginSourceComboBox.SelectedIndex = 0;
            }
        }

        private void ReminderToggle_Toggled(
            object sender,
            RoutedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.UpdateReminder = UpdateReminderToggle.IsOn;
            _settings.PluginUpdateReminder = PluginUpdateReminderToggle.IsOn;
            _settings.ServiceStartReminder = ServiceReminderToggle.IsOn;
            _settings.RechargeReminder = RechargeReminderToggle.IsOn;
            SaveSettings();
        }

        private void ThresholdCheckBox_Changed(
            object sender,
            RoutedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.SpendAlert5 = Spend5CheckBox.IsChecked == true;
            _settings.SpendAlert10 = Spend10CheckBox.IsChecked == true;
            _settings.SpendAlert20 = Spend20CheckBox.IsChecked == true;
            _settings.SpendAlert50 = Spend50CheckBox.IsChecked == true;
            _settings.SpendAlertCustom = SpendCustomCheckBox.IsChecked == true;
            _settings.BalanceAlert20 = Balance20CheckBox.IsChecked == true;
            _settings.BalanceAlert10 = Balance10CheckBox.IsChecked == true;
            _settings.BalanceAlert5 = Balance5CheckBox.IsChecked == true;
            _settings.BalanceAlert1 = Balance1CheckBox.IsChecked == true;
            _settings.BalanceAlertCustom = BalanceCustomCheckBox.IsChecked == true;
            UpdateCustomThresholdStates();
            SaveSettings();
        }

        private void ThresholdNumberBox_ValueChanged(
            NumberBox sender,
            NumberBoxValueChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            if (ReferenceEquals(sender, SpendCustomBox)
                && !Double.IsNaN(sender.Value))
            {
                _settings.SpendCustomAmount = (decimal)sender.Value;
            }

            if (ReferenceEquals(sender, BalanceCustomBox)
                && !Double.IsNaN(sender.Value))
            {
                _settings.BalanceCustomAmount = (decimal)sender.Value;
            }

            SaveSettings();
        }

        private void UpdateCustomThresholdStates()
        {
            SpendCustomBox.IsEnabled = SpendCustomCheckBox.IsChecked == true;
            BalanceCustomBox.IsEnabled = BalanceCustomCheckBox.IsChecked == true;
        }

        private void SaveSettings()
        {
            LauncherSettingsStore.Save(_settings);
            _host.Log("Settings saved.");
        }

        private static void SelectTaggedItem(ComboBox comboBox, string tag)
        {
            for (int index = 0; index < comboBox.Items.Count; index++)
            {
                if (comboBox.Items[index] is ComboBoxItem item
                    && String.Equals(
                        item.Tag as string,
                        tag,
                        StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = index;
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- 常规：代理设置

        private void UpdateProxyCustomPanel()
        {
            if (ProxyModeSelector == null || ProxyCustomPanel == null)
            {
                return;
            }

            RadioButton selected = ProxyModeSelector.SelectedItem as RadioButton;
            ProxyCustomPanel.IsEnabled = selected != null
                && String.Equals(
                    GetTag(selected),
                    "Custom",
                    StringComparison.OrdinalIgnoreCase);
        }

        private void ProxyModeSelector_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            UpdateProxyCustomPanel();
            SaveProxySettings();
        }

        private void ProxySetting_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            SaveProxySettings();
        }

        private void ProxyHostBox_TextChanged(
            object sender,
            TextChangedEventArgs args)
        {
            SaveProxySettings();
        }

        private void ProxyPortBox_ValueChanged(
            NumberBox sender,
            NumberBoxValueChangedEventArgs args)
        {
            SaveProxySettings();
        }

        private void SaveProxySettings()
        {
            if (_initializing || _settings == null)
            {
                return;
            }

            RadioButton mode = ProxyModeSelector.SelectedItem as RadioButton;
            RadioButton protocol = ProxyProtocolSelector.SelectedItem as RadioButton;
            _settings.ProxyMode = mode == null ? "None" : GetTag(mode);
            _settings.ProxyProtocol = protocol == null
                ? "Http"
                : GetTag(protocol);
            _settings.ProxyHost = ProxyHostBox.Text;
            _settings.ProxyPort = Double.IsNaN(ProxyPortBox.Value)
                || ProxyPortBox.Value < 1
                    ? 7890
                    : (int)ProxyPortBox.Value;
            SaveSettings();
            _host.Log(
                "Proxy settings saved: "
                + _settings.ProxyMode
                + " / "
                + _settings.ProxyProtocol
                + " / "
                + _settings.ProxyHost
                + ":"
                + _settings.ProxyPort);
        }

        private static void SelectRadioByTag(
            RadioButtons selector,
            string tag,
            string fallback)
        {
            if (selector == null)
            {
                return;
            }

            RadioButton fallbackItem = null;
            for (int index = 0; index < selector.Items.Count; index++)
            {
                RadioButton item = selector.Items[index] as RadioButton;
                if (item == null)
                {
                    continue;
                }

                string itemTag = item.Tag as string;
                if (fallbackItem == null
                    && String.Equals(
                        itemTag,
                        fallback,
                        StringComparison.OrdinalIgnoreCase))
                {
                    fallbackItem = item;
                }

                if (String.Equals(
                    itemTag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
                {
                    selector.SelectedItem = item;
                    return;
                }
            }

            if (fallbackItem != null)
            {
                selector.SelectedItem = fallbackItem;
            }
        }

        // ---------------------------------------------------------------- 插件页

        /// <summary>
        /// 三个插件分页共用的首次加载入口。
        /// </summary>
        private void LoadPluginCardSamples()
        {
            LoadLocalPlugins();
            LoadFeaturedPlugins(false);
            LoadOnlinePlugins(false);
        }

        // ---------------------------------------------------------------- 官方推荐

        private List<PluginCatalogItem> _featuredItems =
            new List<PluginCatalogItem>();
        private int _featuredPage;
        private const int FeaturedPageSize = 6;

        private void LoadFeaturedPlugins(bool forceRefresh)
        {
            FeaturedSummaryText.Text = "正在同步推荐列表…";
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                FeaturedPluginResult result = FeaturedPluginService.Load(
                    _settings,
                    forceRefresh,
                    _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    _featuredItems = result == null
                        ? new List<PluginCatalogItem>()
                        : result.Items;
                    MergeFeaturedWithCatalog();
                    _featuredPage = 0;
                    RebuildFeaturedPage();
                });
            });
        }

        private void FeaturedSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _featuredPage = 0;
            RebuildFeaturedPage();
        }

        private void RebuildFeaturedPage()
        {
            if (FeaturedPluginRepeater == null)
            {
                return;
            }

            string keyword = (FeaturedPluginSearchBox.Text ?? String.Empty).Trim();
            List<PluginCatalogItem> filtered = new List<PluginCatalogItem>();
            for (int index = 0; index < _featuredItems.Count; index++)
            {
                PluginCatalogItem item = _featuredItems[index];
                if (keyword.Length == 0
                    || item.Repository.IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || item.FullName.IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.Description ?? String.Empty).IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.FeaturedNote ?? String.Empty).IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    filtered.Add(item);
                }
            }

            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)FeaturedPageSize));
            if (_featuredPage > totalPages - 1)
            {
                _featuredPage = totalPages - 1;
            }

            if (_featuredPage < 0)
            {
                _featuredPage = 0;
            }

            int start = _featuredPage * FeaturedPageSize;
            int end = Math.Min(start + FeaturedPageSize, filtered.Count);
            List<PluginCardItem> cards = new List<PluginCardItem>();
            for (int index = start; index < end; index++)
            {
                PluginCatalogItem item = filtered[index];
                PluginCardItem card = ToOnlineCard(item);
                card.Status = String.Empty;
                card.Tag1 = item.FeaturedNote ?? String.Empty;
                card.Tag2 = String.Empty;

                if (!String.IsNullOrWhiteSpace(item.FeaturedNote))
                {
                    card.DetailSubtitle = item.FeaturedNote + " · "
                        + card.DetailSubtitle;
                }

                cards.Add(card);
            }

            FeaturedPluginRepeater.ItemsSource = cards;
            FeaturedSummaryText.Text = _featuredItems.Count == 0
                ? "推荐列表暂时不可用"
                : "共 " + _featuredItems.Count + " 个官方推荐，命中 "
                    + filtered.Count + " 个";
            FeaturedPageText.Text = (_featuredPage + 1) + " / " + totalPages;
            FeaturedPreviousPageButton.IsEnabled = _featuredPage > 0;
            FeaturedNextPageButton.IsEnabled = _featuredPage + 1 < totalPages;
        }

        private void MergeFeaturedWithCatalog()
        {
            if (_featuredItems.Count == 0 || _catalogItems.Count == 0)
            {
                return;
            }

            for (int index = 0; index < _featuredItems.Count; index++)
            {
                PluginCatalogItem featured = _featuredItems[index];
                for (int catalogIndex = 0;
                    catalogIndex < _catalogItems.Count;
                    catalogIndex++)
                {
                    PluginCatalogItem catalog = _catalogItems[catalogIndex];
                    if (!String.Equals(
                        featured.FullName,
                        catalog.FullName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    featured.Description = catalog.Description;
                    featured.Language = catalog.Language;
                    featured.License = catalog.License;
                    featured.PushedAt = catalog.PushedAt;
                    featured.Category = catalog.Category;
                    featured.Stars = catalog.Stars;
                    featured.Verified = catalog.Verified;
                    featured.DefaultBranch = catalog.DefaultBranch;
                    featured.InstallSpecifier = catalog.InstallSpecifier;
                    featured.InstallSource = catalog.InstallSource;
                    featured.InstallExecutable = catalog.InstallExecutable;
                    featured.InstallStatus = catalog.InstallStatus;
                    featured.SourceSha = catalog.SourceSha;
                    featured.ImageUrl = catalog.ImageUrl;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- 在线插件

        private void OnlineFilter_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _catalogPage = 0;
            RebuildOnlinePage(false);
        }

        private void OnlinePageSize_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            int size;
            if (Int32.TryParse(
                    GetSelectedTag(PluginPageSizeComboBox, "9"),
                    out size)
                && size > 0)
            {
                _catalogPageSize = size;
            }

            _catalogPage = 0;
            RebuildOnlinePage(false);
        }

        private void LocalPageSize_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            int size;
            if (Int32.TryParse(
                    GetSelectedTag(LocalPageSizeComboBox, "9"),
                    out size)
                && size > 0)
            {
                _localPageSize = size;
            }

            _localPage = 0;
            RebuildLocalPage();
        }

        private void OnlineSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _catalogPage = 0;
            RebuildOnlinePage(false);
        }

        private void LoadOnlinePlugins(bool forceRefresh)
        {
            PluginCatalogInfoBar.Severity = InfoBarSeverity.Informational;
            PluginCatalogInfoBar.Title = "正在拉取插件目录";
            PluginCatalogInfoBar.Message = forceRefresh
                ? "正在刷新在线插件列表…"
                : "正在加载在线插件列表…";
            PluginCatalogInfoBar.IsOpen = true;

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PluginCatalogService.CatalogResult result =
                    PluginCatalogService.Load(_settings, forceRefresh, _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    ApplyCatalog(result);
                });
            });
        }

        private void ApplyCatalog(PluginCatalogService.CatalogResult result)
        {
            _catalogItems = result == null
                ? new List<PluginCatalogItem>()
                : result.Items;
            _pluginRecords = PluginInstallStore.Load();
            _catalogPage = 0;
            RebuildOnlinePage(result != null && result.FromCache);
            MergeFeaturedWithCatalog();
            RebuildFeaturedPage();

            int count = _catalogItems.Count;

            if (result != null && result.RateLimited)
            {
                PluginCatalogInfoBar.Severity = InfoBarSeverity.Warning;
                PluginCatalogInfoBar.Title = "GitHub 接口限流";
                PluginCatalogInfoBar.Message = "未认证时每分钟只能查 10 次，"
                    + "写入 GitHub Token 可以拉得更快更全。";
                PluginCatalogInfoBar.IsOpen = true;
                GitHubTokenPromptInfoBar.IsOpen = true;
            }
            else if (count == 0)
            {
                PluginCatalogInfoBar.Severity = InfoBarSeverity.Error;
                PluginCatalogInfoBar.Title = "拉取插件目录失败";
                PluginCatalogInfoBar.Message = result == null
                    ? "未知错误。"
                    : result.Error ?? "网络不通或接口没有返回数据。";
                PluginCatalogInfoBar.IsOpen = true;
            }
            else
            {
                PluginCatalogInfoBar.IsOpen = false;
            }

            _host.Log("在线插件列表：" + count + " 条，缓存="
                + (result != null && result.FromCache)
                + "，限流=" + (result != null && result.RateLimited));
        }

        // ---------------------------------------------------------------- 在线分页

        private List<PluginCatalogItem> _catalogItems =
            new List<PluginCatalogItem>();
        private Dictionary<string, PluginInstallRecord> _pluginRecords =
            new Dictionary<string, PluginInstallRecord>(
                StringComparer.OrdinalIgnoreCase);
        private int _catalogPage;
        private int _catalogPageSize = 9;

        /// <summary>只把当前页投影成卡片；2500 条全建成卡片会直接把界面拖死。</summary>
        private void RebuildOnlinePage(bool fromCache)
        {
            if (OnlinePluginRepeater == null)
            {
                return;
            }

            List<PluginCatalogItem> filtered = FilterAndSortCatalog();
            int pageSize = Math.Max(1, _catalogPageSize);
            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)pageSize));
            if (_catalogPage > totalPages - 1)
            {
                _catalogPage = totalPages - 1;
            }

            if (_catalogPage < 0)
            {
                _catalogPage = 0;
            }

            int start = _catalogPage * pageSize;
            int end = Math.Min(start + pageSize, filtered.Count);
            List<PluginCardItem> cards = new List<PluginCardItem>();
            for (int index = start; index < end; index++)
            {
                cards.Add(ToOnlineCard(filtered[index]));
            }

            OnlinePluginRepeater.ItemsSource = cards;
            PluginCatalogSummaryText.Text = _catalogItems.Count == 0
                ? "没有拉到插件"
                : "共 " + _catalogItems.Count + " 个插件，命中 "
                    + filtered.Count + " 个"
                    + (fromCache ? "（缓存）" : String.Empty);
            PluginPageText.Text = (_catalogPage + 1) + " / " + totalPages;
            PluginPreviousPageButton.IsEnabled = _catalogPage > 0;
            PluginNextPageButton.IsEnabled = _catalogPage + 1 < totalPages;
        }

        /// <summary>分类 / 已安装 / 已验证筛选 + 搜索 + 排序 + 本语言优先，全在本地做。</summary>
        private List<PluginCatalogItem> FilterAndSortCatalog()
        {
            string category = GetSelectedTag(PluginCategoryComboBox, "All");
            string keyword = PluginSearchBox == null
                ? String.Empty
                : (PluginSearchBox.Text ?? String.Empty).Trim();
            string sort = GetSelectedTag(PluginSortComboBox, "Stars");
            bool preferLocal = PluginLanguageComboBox == null
                || GetSelectedTag(PluginLanguageComboBox, "Local") != "All";

            List<PluginCatalogItem> filtered = new List<PluginCatalogItem>();
            for (int index = 0; index < _catalogItems.Count; index++)
            {
                PluginCatalogItem item = _catalogItems[index];
                bool installed = IsInstalled(item);
                switch (category)
                {
                    case "Installed":
                        if (!installed)
                        {
                            continue;
                        }

                        break;
                    case "Verified":
                        if (!item.Verified)
                        {
                            continue;
                        }

                        break;
                    case "All":
                        break;
                    default:
                        if (!String.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        break;
                }

                if (keyword.Length > 0
                    && item.Repository.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                    && item.FullName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                    && (item.Description ?? String.Empty).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                filtered.Add(item);
            }

            filtered.Sort(delegate(PluginCatalogItem left, PluginCatalogItem right)
            {
                if (preferLocal)
                {
                    int language = IsLocalLanguage(right).CompareTo(IsLocalLanguage(left));
                    if (language != 0)
                    {
                        return language;
                    }
                }

                if (left.Verified != right.Verified)
                {
                    return right.Verified.CompareTo(left.Verified);
                }

                switch (sort)
                {
                    case "Updated":
                        return String.CompareOrdinal(right.PushedAt, left.PushedAt);
                    case "Added":
                        return String.CompareOrdinal(right.VerificationUrl ?? String.Empty, left.VerificationUrl ?? String.Empty);
                    case "Name":
                        return String.Compare(left.Repository, right.Repository, StringComparison.OrdinalIgnoreCase);
                    default:
                        return right.Stars.CompareTo(left.Stars);
                }
            });
            return filtered;
        }

        private PluginCardItem ToOnlineCard(PluginCatalogItem item)
        {
            bool installed = IsInstalled(item);
            string specifier = ResolveInstallSpecifier(item);
            return new PluginCardItem
            {
                Name = item.Repository,
                Description = String.IsNullOrWhiteSpace(item.Description)
                    ? "（作者没有写简介）"
                    : item.Description,
                Status = item.Verified ? "已验证" : "未验证",
                StatusBackground = VerifiedStatusBackground(item.Verified),
                StatusForeground = VerifiedStatusForeground(item.Verified),
                Tag1 = item.Category,
                Tag2 = String.IsNullOrWhiteSpace(item.Version)
                    || String.Equals(
                        item.Version,
                        "未知",
                        StringComparison.Ordinal)
                            ? "版本未知"
                            : "v" + item.Version.TrimStart('v', 'V'),
                Meta = item.Owner + " · 更新于 " + FormatPushedAt(item.PushedAt),
                DetailSubtitle = item.FullName + " · 更新于 "
                    + FormatPushedAt(item.PushedAt),
                Stars = FormatStars(item.Stars),
                Spec = specifier,
                ExpectedKey = item.Repository,
                PushedAt = item.PushedAt,
                DefaultBranch = item.DefaultBranch,
                SourceSha = item.SourceSha,
                InstallSource = item.InstallSource,
                Repository = item.Repository,
                ConfigJson = BuildPluginConfigJson(item),
                IsOnline = true,
                Verified = item.Verified,
                IconSource = ResolveOnlineIcon(item),
                PrimaryAction = installed ? "已安装" : "安装",
                PrimaryEnabled = !installed,
                ShowLocalActions = false,
                ShowOnlineActions = true
            };
        }

        private static Brush VerifiedStatusBackground(bool verified)
        {
            return new SolidColorBrush(
                verified
                    ? Windows.UI.Color.FromArgb(32, 40, 167, 69)
                    : Windows.UI.Color.FromArgb(36, 240, 180, 41));
        }

        private static Brush VerifiedStatusForeground(bool verified)
        {
            return new SolidColorBrush(
                verified
                    ? Windows.UI.Color.FromArgb(255, 40, 167, 69)
                    : Windows.UI.Color.FromArgb(255, 183, 121, 31));
        }

        private static string BuildPluginConfigJson(PluginCatalogItem item)
        {
            System.Text.Json.Nodes.JsonArray candidates =
                new System.Text.Json.Nodes.JsonArray();
            for (int index = 0;
                index < item.InstallCandidates.Count;
                index++)
            {
                PluginInstallCandidate candidate = item.InstallCandidates[index];
                candidates.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["source"] = candidate.Source,
                    ["target"] = candidate.Target,
                    ["action"] = candidate.Action,
                    ["specifier"] = candidate.Specifier,
                    ["executable"] = candidate.Executable,
                    ["evidenceSource"] = candidate.EvidenceSource
                });
            }

            System.Text.Json.Nodes.JsonObject plugin =
                new System.Text.Json.Nodes.JsonObject
                {
                    ["owner"] = item.Owner,
                    ["repository"] = item.Repository,
                    ["repositoryUrl"] = item.Url,
                    ["description"] = item.Description,
                    ["language"] = item.Language,
                    ["license"] = item.License,
                    ["pushedAt"] = item.PushedAt,
                    ["category"] = item.Category,
                    ["version"] = item.Version,
                    ["stars"] = item.Stars,
                    ["verified"] = item.Verified,
                    ["defaultBranch"] = item.DefaultBranch,
                    ["sourceSha"] = item.SourceSha,
                    ["imageUrl"] = item.ImageUrl,
                    ["installStatus"] = item.InstallStatus,
                    ["selectedSpecifier"] = ResolveInstallSpecifier(item),
                    ["selectedSource"] = item.InstallSource,
                    ["installCandidates"] = candidates
                };
            return new System.Text.Json.Nodes.JsonObject
            {
                ["schemaVersion"] = 1,
                ["plugin"] = plugin
            }.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
        }

        private static string ResolveInstallSpecifier(PluginCatalogItem item)
        {
            if (item == null)
            {
                return String.Empty;
            }

            if (!String.IsNullOrWhiteSpace(item.InstallSpecifier))
            {
                return item.InstallSpecifier;
            }

            string fallback = item.Spec;
            if (!String.IsNullOrWhiteSpace(item.SourceSha))
            {
                fallback += "#" + item.SourceSha;
            }

            return fallback;
        }

        private bool IsInstalled(PluginCatalogItem item)
        {
            return _pluginRecords.ContainsKey(item.Repository)
                || _pluginRecords.ContainsKey(item.FullName);
        }

        /// <summary>本语言判定：简介里含中日韩字符就算本语言内容。</summary>
        private static bool IsLocalLanguage(PluginCatalogItem item)
        {
            string text = item.Description ?? String.Empty;
            for (int index = 0; index < text.Length; index++)
            {
                char ch = text[index];
                if (ch >= 0x2E80 && ch <= 0x9FFF)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>图标优先级：API 图片 → 仓库自带 icon → GitHub 默认标记。</summary>
        private static ImageSource ResolveOnlineIcon(PluginCatalogItem item)
        {
            if (!String.IsNullOrWhiteSpace(item.ImageUrl))
            {
                try
                {
                    return new BitmapImage(new Uri(item.ImageUrl));
                }
                catch
                {
                }
            }

            string reference = String.IsNullOrWhiteSpace(item.DefaultBranch)
                ? "main"
                : item.DefaultBranch.Trim();
            return RepositoryIconAtBranch(
                item.Owner,
                item.Repository,
                reference,
                "icon.svg");
        }

        /// <summary>按指定分支/提交取仓库根目录里的图标。</summary>
        private static ImageSource RepositoryIconAtBranch(
            string owner,
            string repo,
            string reference,
            string path)
        {
            if (String.IsNullOrWhiteSpace(owner)
                || String.IsNullOrWhiteSpace(repo))
            {
                return null;
            }

            try
            {
                return new BitmapImage(
                    new Uri(
                        "https://cdn.jsdelivr.net/gh/"
                        + owner + "/" + repo + "@" + reference + "/" + path));
            }
            catch
            {
                return null;
            }
        }

        private static string FormatPushedAt(string value)
        {
            DateTime parsed;
            if (DateTime.TryParse(value, out parsed))
            {
                return parsed.ToLocalTime().ToString("yyyy-MM-dd");
            }

            return "未知";
        }

        /// <summary>星数：过千折算成 2.3k / 12k / 1.2M。</summary>
        private static string FormatStars(int stars)
        {
            if (stars < 1000)
            {
                return stars.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            System.Globalization.CultureInfo culture =
                System.Globalization.CultureInfo.InvariantCulture;
            if (stars < 10000)
            {
                return (stars / 1000.0).ToString("0.0", culture) + "k";
            }

            if (stars < 1000000)
            {
                return (stars / 1000.0).ToString("0", culture) + "k";
            }

            return (stars / 1000000.0).ToString("0.0", culture) + "M";
        }

        private void InstallOnlinePlugin_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null || String.IsNullOrWhiteSpace(card.Spec))
            {
                return;
            }

            StartPluginInstall(card);
        }

        private void CheckPluginUpdate_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null || String.IsNullOrWhiteSpace(card.Name))
            {
                return;
            }

            if (String.Equals(
                card.PrimaryAction,
                "立即更新",
                StringComparison.Ordinal))
            {
                _host.InstallPluginUpdate(card.Name);
                PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
                PluginActionInfoBar.Title = "正在更新 " + card.Name;
                PluginActionInfoBar.Message = "更新在后台继续执行。";
                PluginActionInfoBar.IsOpen = true;
                return;
            }

            card.CheckEnabled = false;
            card.PrimaryAction = "检查中…";
            PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
            PluginActionInfoBar.Title = "正在检查 " + card.Name;
            PluginActionInfoBar.Message = "正在对比在线目录中的更新时间。";
            PluginActionInfoBar.IsOpen = true;

            string key = card.Name;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PluginUpdateCheckResult check = PluginUpdateService.Check(
                    _settings,
                    false,
                    _host.Log);
                PluginUpdateMatch match = null;
                for (int index = 0; index < check.Updates.Count; index++)
                {
                    if (String.Equals(
                        check.Updates[index].Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        match = check.Updates[index];
                        break;
                    }
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    if (check.RateLimited)
                    {
                        card.PrimaryAction = "检查更新";
                        card.CheckEnabled = true;
                        PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                        PluginActionInfoBar.Title = "插件目录限流";
                        PluginActionInfoBar.Message = "写入 GitHub Token 后重试。";
                    }
                    else if (!String.IsNullOrWhiteSpace(check.Error))
                    {
                        card.PrimaryAction = "检查更新";
                        card.CheckEnabled = true;
                        PluginActionInfoBar.Severity = InfoBarSeverity.Error;
                        PluginActionInfoBar.Title = "检查更新失败";
                        PluginActionInfoBar.Message = check.Error;
                    }
                    else if (match != null)
                    {
                        card.PrimaryAction = "立即更新";
                        card.CheckEnabled = true;
                        card.Tag1 = "发现新版本";
                        PluginActionInfoBar.Severity = InfoBarSeverity.Success;
                        PluginActionInfoBar.Title = key + " 有新版本";
                        PluginActionInfoBar.Message = "点击卡片里的“立即更新”安装。";
                    }
                    else
                    {
                        card.PrimaryAction = "已是最新";
                        card.CheckEnabled = false;
                        PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
                        PluginActionInfoBar.Title = key + " 已是最新";
                        PluginActionInfoBar.Message = "在线目录没有更新的提交时间。";
                    }

                    PluginActionInfoBar.IsOpen = true;
                });
            });
        }

        private static PluginCardItem CardFrom(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return null;
            }

            // ItemsRepeater 的模板不一定给按钮塞 DataContext，所以模板上把整条卡片绑到了 Tag。
            PluginCardItem card = element.Tag as PluginCardItem;
            return card ?? element.DataContext as PluginCardItem;
        }

        private void StartPluginInstall(PluginCardItem card)
        {
            if (card.IsOnline && !PackageManagerRunner.IsAvailable(_settings))
            {
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "需要 pnpm";
                PluginActionInfoBar.Message = "部分插件需要 pnpm 才能安装，"
                    + "请到组件页先装上 pnpm 组件。";
                PluginActionInfoBar.IsOpen = true;
                return;
            }

            PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
            PluginActionInfoBar.Title = "正在安装 " + card.Name;
            PluginActionInfoBar.Message = "准备下载…";
            PluginActionInfoBar.IsOpen = true;

            card.Busy = true;
            card.ProgressValue = 0;
            card.PrimaryEnabled = false;
            card.PrimaryAction = "安装中…";

            PluginSpec spec = PluginSpec.Parse(card.Spec);
            string name = card.Name;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PluginStoreService.InstallResult result = PluginStoreService.Install(
                    _settings,
                    spec,
                    card.ExpectedKey,
                    card.PushedAt,
                    card.DefaultBranch,
                    card.SourceSha,
                    delegate(string text, double fraction)
                    {
                        DispatcherQueue.TryEnqueue(delegate
                        {
                            PluginActionInfoBar.Message = text;
                            card.PrimaryAction = DescribeProgress(text);
                            card.ProgressValue = Math.Max(0, Math.Min(100, fraction));
                        });
                    },
                    _host.Log);

                DispatcherQueue.TryEnqueue(delegate
                {
                    if (result.Ok)
                    {
                        PluginActionInfoBar.Severity = result.PnpmMissing
                            || result.PnpmFailed
                                ? InfoBarSeverity.Warning
                                : InfoBarSeverity.Success;
                        PluginActionInfoBar.Title = name + " 已安装";
                        PluginActionInfoBar.Message = result.PnpmMissing
                            ? "已写入 profile，但没找到 pnpm，请到组件页安装后重启 DSH。"
                            : (result.PnpmFailed
                                ? "已写入 profile，但 pnpm install 没成功："
                                    + result.Detail + " 请重启 DSH 前先手动确认。"
                                : "重启 DSH 后生效。");
                    }
                    else
                    {
                        PluginActionInfoBar.Severity = InfoBarSeverity.Error;
                        PluginActionInfoBar.Title = name + " 安装失败";
                        PluginActionInfoBar.Message = result.Error ?? "未知错误。";
                    }

                    PluginActionInfoBar.IsOpen = true;
                    card.Busy = false;
                    card.PrimaryAction = result.Ok ? "已安装" : "重试";
                    card.PrimaryEnabled = !result.Ok;
                    LoadLocalPlugins();
                });
            });
        }

        /// <summary>按钮里放不下整句话，只取阶段词 + 百分比。</summary>
        private static string DescribeProgress(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return "安装中…";
            }

            if (text.StartsWith("下载", StringComparison.Ordinal))
            {
                return "下载中…";
            }

            if (text.StartsWith("安装", StringComparison.Ordinal))
            {
                return "安装中…";
            }

            return text.Length > 6 ? text.Substring(0, 6) : text;
        }

        // ---------------------------------------------------------------- 插件卡片动作

        private void OpenPluginFolder_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null)
            {
                return;
            }

            string folder = card.Folder;
            if (String.IsNullOrWhiteSpace(folder)
                && card.IsOnline
                && !String.IsNullOrWhiteSpace(_settings.DshRoot))
            {
                PluginSpec spec = PluginSpec.Parse(card.Spec);
                folder = Path.Combine(
                    DshProfileService.PluginsDirectory(_settings.DshRoot),
                    spec.FolderName);
            }

            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "找不到插件目录";
                PluginActionInfoBar.Message = folder ?? "插件没有链接到本地目录。";
                PluginActionInfoBar.IsOpen = true;
                return;
            }

            OpenDirectory(folder);
        }

        private async void UninstallPlugin_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null)
            {
                return;
            }

            bool owned = false;
            DshProfilePlugin target = null;
            List<DshProfilePlugin> plugins = DshProfileService.ReadPlugins(
                _settings.DshRoot,
                PluginInstallStore.Load());
            for (int index = 0; index < plugins.Count; index++)
            {
                if (String.Equals(
                    plugins[index].Key,
                    card.Name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    target = plugins[index];
                    owned = target.Record != null;
                    break;
                }
            }

            if (target == null)
            {
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "找不到这个插件";
                PluginActionInfoBar.Message = "profile 里已经没有 " + card.Name + " 了。";
                PluginActionInfoBar.IsOpen = true;
                LoadLocalPlugins();
                return;
            }

            ContentDialog confirm = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "卸载 " + card.Name,
                Content = owned
                    ? "会从 profile 里移除依赖与挂载项，并删除插件目录。"
                    : "只会从 profile 里解除引用，不会删除你的插件目录。",
                PrimaryButtonText = "卸载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            ContentDialogResult answer = await confirm.ShowAsync();
            if (answer != ContentDialogResult.Primary)
            {
                return;
            }

            string error;
            bool ok = PluginStoreService.Uninstall(_settings, target, out error);
            PluginActionInfoBar.Severity = ok
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
            PluginActionInfoBar.Title = ok ? "已卸载 " + card.Name : "卸载失败";
            PluginActionInfoBar.Message = ok
                ? "重启 DSH 后生效。"
                : (error ?? "未知错误。");
            PluginActionInfoBar.IsOpen = true;
            LoadLocalPlugins();
        }

        private PluginCardItem _detailCard;

        private void ShowPluginDetail_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null)
            {
                return;
            }

            _detailCard = card;
            _host.Log("点击：查看详情 " + card.Name);
            PluginDetailTitle.Text = card.Name;
            PluginDetailAuthor.Text = String.IsNullOrWhiteSpace(card.DetailSubtitle)
                ? card.Meta
                : card.DetailSubtitle;
            PluginDetailDescription.Text = card.Description;
            InstallOnlinePluginButton.Content = card.PrimaryAction;
            InstallOnlinePluginButton.IsEnabled = card.PrimaryEnabled;
            CopyPluginConfigButton.IsEnabled =
                !String.IsNullOrWhiteSpace(card.ConfigJson);
            _ = PluginDetailDialog.ShowAsync();
        }

        private async void CopyPluginConfig_Click(
            object sender,
            RoutedEventArgs args)
        {
            if (_detailCard == null
                || String.IsNullOrWhiteSpace(_detailCard.ConfigJson))
            {
                return;
            }

            Exception lastError = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    DataPackage package = new DataPackage();
                    package.SetText(_detailCard.ConfigJson);
                    Clipboard.SetContent(package);
                    try
                    {
                        Clipboard.Flush();
                    }
                    catch
                    {
                    }

                    PluginActionInfoBar.Severity = InfoBarSeverity.Success;
                    PluginActionInfoBar.Title = "配置方式已复制";
                    PluginActionInfoBar.Message =
                        "到开发者管理中心的“粘贴配置导入”区域粘贴即可。";
                    PluginActionInfoBar.IsOpen = true;
                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    await System.Threading.Tasks.Task.Delay(120);
                }
            }

            PluginActionInfoBar.Severity = InfoBarSeverity.Error;
            PluginActionInfoBar.Title = "复制失败";
            PluginActionInfoBar.Message = lastError == null
                ? "剪贴板当前不可用。"
                : lastError.Message;
            PluginActionInfoBar.IsOpen = true;
        }

        private void OpenPluginRepository_Click(
            object sender,
            RoutedEventArgs args)
        {
            if (_detailCard == null)
            {
                return;
            }

            string url = _detailCard.Meta.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? _detailCard.Meta
                : _detailCard.Spec;
            if (url.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://github.com/" + url.Substring("github:".Length);
            }

            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                OpenUrl(url);
            }
        }

        private void InstallDetailPlugin_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginDetailDialog.Hide();
            if (_detailCard != null && _detailCard.PrimaryEnabled)
            {
                StartPluginInstall(_detailCard);
            }
        }

        /// <summary>本地插件：直接扫当前 DSH profile 的依赖，读插件自己的 package.json 补说明和版本。</summary>
        private void LoadLocalPlugins()
        {
            List<PluginCardItem> cards = new List<PluginCardItem>();
            try
            {
                Dictionary<string, PluginInstallRecord> records =
                    PluginInstallStore.Load();
                List<DshProfilePlugin> plugins = DshProfileService.ReadPlugins(
                    _settings.DshRoot,
                    records);

                for (int index = 0; index < plugins.Count; index++)
                {
                    DshProfilePlugin plugin = plugins[index];
                    string description;
                    string version;
                    ReadLocalPluginManifest(
                        plugin.LinkedDirectory,
                        out description,
                        out version);

                    bool online = plugin.Record != null;
                    if (String.IsNullOrWhiteSpace(version)
                        && plugin.Record != null)
                    {
                        version = plugin.Record.Version;
                    }

                    cards.Add(new PluginCardItem
                    {
                        Name = plugin.Key,
                        Description = String.IsNullOrWhiteSpace(description)
                            ? (online
                                ? "由启动器安装的插件，可以检查更新。"
                                : "手动链接进 DSH 的插件，不参与更新检查。")
                            : description,
                        Status = online ? "在线安装" : "本地链接",
                        IconSource = LocalPluginIcon(plugin.LinkedDirectory),
                        Tag1 = String.IsNullOrWhiteSpace(version)
                            ? "版本未知"
                            : "v" + version.TrimStart('v', 'V'),
                        Tag2 = plugin.InBundles ? "已挂载" : "未挂载",
                        Meta = String.IsNullOrWhiteSpace(plugin.LinkedDirectory)
                            ? plugin.Dependency
                            : plugin.LinkedDirectory,
                        Folder = plugin.LinkedDirectory ?? String.Empty,
                        DetailSubtitle = String.IsNullOrWhiteSpace(plugin.LinkedDirectory)
                            ? plugin.Dependency
                            : plugin.LinkedDirectory,
                        Spec = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.Spec,
                        ExpectedKey = plugin.Key,
                        PushedAt = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.PushedAt,
                        DefaultBranch = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.DefaultBranch,
                        SourceSha = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.SourceSha,
                        InstallSource = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.InstallSource,
                        Repository = plugin.Record == null
                            ? plugin.Key
                            : plugin.Record.Repository,
                        IsOnline = online,
                        ShowLocalActions = true,
                        ShowOnlineActions = false,
                        CheckEnabled = online,
                        PrimaryAction = online ? "检查更新" : "不可更新"
                    });
                }
            }
            catch
            {
            }

            LocalPluginRepeater.ItemsSource = cards;
            _localPlugins = cards;
            RebuildLocalPage();
            _host.Log("本地插件列表：" + cards.Count + " 个（profile="
                + DshProfileService.ResolveProfileFilePath(_settings.DshRoot) + "）");
        }

        private List<PluginCardItem> _localPlugins =
            new List<PluginCardItem>();
        private int _localPage;
        private int _localPageSize = 9;
        private string _localKeyword = String.Empty;

        private void LocalSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _localKeyword = (LocalPluginSearchBox.Text ?? String.Empty).Trim();
            _localPage = 0;
            RebuildLocalPage();
        }

        /// <summary>本地插件同样按 3 的倍数分页，只渲染当前页。</summary>
        private void RebuildLocalPage()
        {
            if (LocalPluginRepeater == null || LocalPluginSummaryText == null)
            {
                return;
            }

            List<PluginCardItem> filtered = new List<PluginCardItem>();
            for (int index = 0; index < _localPlugins.Count; index++)
            {
                PluginCardItem item = _localPlugins[index];
                if (_localKeyword.Length == 0
                    || (item.Name ?? String.Empty).IndexOf(
                        _localKeyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.Description ?? String.Empty).IndexOf(
                        _localKeyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.Meta ?? String.Empty).IndexOf(
                        _localKeyword,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    filtered.Add(item);
                }
            }

            int pageSize = Math.Max(1, _localPageSize);
            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)pageSize));
            if (_localPage > totalPages - 1)
            {
                _localPage = totalPages - 1;
            }

            if (_localPage < 0)
            {
                _localPage = 0;
            }

            int start = _localPage * pageSize;
            int end = Math.Min(start + pageSize, filtered.Count);
            List<PluginCardItem> page = new List<PluginCardItem>();
            for (int index = start; index < end; index++)
            {
                page.Add(filtered[index]);
            }

            LocalPluginRepeater.ItemsSource = page;
            LocalPluginSummaryText.Text = _localKeyword.Length == 0
                ? "共 " + _localPlugins.Count + " 个已安装插件"
                : "命中 " + filtered.Count + " / " + _localPlugins.Count
                    + " 个已安装插件";
            LocalPageText.Text = (_localPage + 1) + " / " + totalPages;
            LocalPreviousPageButton.IsEnabled = _localPage > 0;
            LocalNextPageButton.IsEnabled = _localPage + 1 < totalPages;
        }

        /// <summary>读插件目录自己的 package.json：说明和版本。</summary>
        private static void ReadLocalPluginManifest(
            string directory,
            out string description,
            out string version)
        {
            description = null;
            version = null;
            if (String.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                string path = Path.Combine(directory, "package.json");
                if (!File.Exists(path))
                {
                    return;
                }

                using (System.Text.Json.JsonDocument document =
                    System.Text.Json.JsonDocument.Parse(
                        File.ReadAllText(path, System.Text.Encoding.UTF8)))
                {
                    System.Text.Json.JsonElement root = document.RootElement;
                    System.Text.Json.JsonElement value;
                    if (root.TryGetProperty("description", out value)
                        && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        description = value.GetString();
                    }

                    if (root.TryGetProperty("version", out value)
                        && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        version = value.GetString();
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>本地插件图标只认插件目录里的 icon / logo 文件。</summary>
        private static ImageSource LocalPluginIcon(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            string[] names =
            {
                "icon.png",
                "icon.svg",
                "logo.png",
                "assets\\icon.png",
                "assets\\icon.svg"
            };
            for (int index = 0; index < names.Length; index++)
            {
                try
                {
                    string path = Path.Combine(directory, names[index]);
                    if (File.Exists(path))
                    {
                        return new BitmapImage(
                            new Uri("file:///" + path.Replace('\\', '/')));
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        /// <summary>
        /// 插件图标只取仓库自带的 icon（约定放仓库根目录或指定路径）。
        /// 仓库没放 icon 时这里返回 null，卡片上会显示 GitHub 默认标记。
        /// 接上在线引擎后，这里的主机名要按"大陆 CDN 加速 / 官方源"切换。
        /// </summary>
        private static ImageSource RepositoryIcon(
            string owner,
            string repo,
            string path)
        {
            if (String.IsNullOrWhiteSpace(owner)
                || String.IsNullOrWhiteSpace(repo)
                || String.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return new BitmapImage(
                    new Uri(
                        "https://cdn.jsdelivr.net/gh/"
                        + owner + "/" + repo + "@main/" + path));
            }
            catch
            {
                return null;
            }
        }

        private void ClosePluginDetail_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginDetailDialog.Hide();
        }

        // ---------------------------------------------------------------- API：GitHub Token

        private void ShowGitHubTokenCheckBox_Changed(
            object sender,
            RoutedEventArgs args)
        {
            GitHubTokenBox.PasswordRevealMode =
                ShowGitHubTokenCheckBox.IsChecked == true
                    ? PasswordRevealMode.Visible
                    : PasswordRevealMode.Hidden;
        }

        private void OpenGitHubTokenPage_Click(
            object sender,
            RoutedEventArgs args)
        {
            OpenUrl("https://github.com/settings/tokens");
        }

        private void SaveGitHubToken_Click(
            object sender,
            RoutedEventArgs args)
        {
            LauncherSettingsStore.SetGitHubToken(
                _settings,
                GitHubTokenBox.Password);
            ShowGitHubTokenStatus(
                InfoBarSeverity.Success,
                "已保存",
                String.IsNullOrWhiteSpace(GitHubTokenBox.Password)
                    ? "GitHub Token 已清空。"
                    : "GitHub Token 已加密保存在本机。");
        }

        private void ClearGitHubToken_Click(
            object sender,
            RoutedEventArgs args)
        {
            GitHubTokenBox.Password = String.Empty;
            LauncherSettingsStore.SetGitHubToken(_settings, String.Empty);
            ShowGitHubTokenStatus(
                InfoBarSeverity.Success,
                "已清空",
                "GitHub Token 已清空。");
        }

        private void ShowGitHubTokenStatus(
            InfoBarSeverity severity,
            string title,
            string message)
        {
            GitHubTokenStatusInfoBar.Severity = severity;
            GitHubTokenStatusInfoBar.Title = title;
            GitHubTokenStatusInfoBar.Message = message;
            GitHubTokenStatusInfoBar.IsOpen = true;
        }

        private void FillGitHubToken_Click(
            object sender,
            RoutedEventArgs args)
        {
            GitHubTokenPromptInfoBar.IsOpen = false;
            SelectPage("Api");
            GitHubTokenBox.Focus(FocusState.Programmatic);
        }

        // ---------------------------------------------------------------- 开发者管理中心

        // ---------------------------------------------------------------- 组件页

        private void RefreshComponents()
        {
            List<ComponentInfo> components = ComponentService.Detect(_settings);
            StringBuilder summary = new StringBuilder();
            for (int index = 0; index < components.Count; index++)
            {
                ComponentInfo item = components[index];
                switch (item.Id)
                {
                    case ComponentService.IdDotNet:
                        ApplyComponentRow(item, DotNetStatusText, DotNetStatusPill, InstallDotNetButton);
                        break;
                    case ComponentService.IdWinAppRuntime:
                        ApplyComponentRow(item, WinAppRuntimeStatusText, WinAppRuntimeStatusPill, InstallWinAppRuntimeButton);
                        break;
                    case ComponentService.IdNode:
                        ApplyComponentRow(item, NodeStatusText, NodeStatusPill, InstallNodeButton);
                        break;
                    case ComponentService.IdGit:
                        ApplyComponentRow(item, GitStatusText, GitStatusPill, InstallGitButton);
                        break;
                    case ComponentService.IdPnpm:
                        ApplyComponentRow(item, PnpmStatusText, PnpmStatusPill, InstallPnpmButton);
                        break;
                    case ComponentService.IdPython:
                        ApplyComponentRow(item, PythonStatusText, PythonStatusPill, InstallPythonButton);
                        break;
                }

                summary.Append(item.DisplayName).Append('=')
                    .Append(item.State).Append(' ');
            }

            _host.Log("组件检测：" + summary.ToString());
        }

        private static void ApplyComponentRow(
            ComponentInfo info,
            TextBlock statusText,
            Border statusPill,
            Button installButton)
        {
            bool ready = info.State == ComponentState.Ready;
            statusText.Text = ready
                ? (String.IsNullOrWhiteSpace(info.Version)
                    ? "已就绪"
                    : info.Version)
                : (info.State == ComponentState.Missing ? "未安装" : "未检测");

            string brushKey = ready
                ? "SystemFillColorSuccessBrush"
                : (info.State == ComponentState.Missing
                    ? "SystemFillColorCautionBrush"
                    : "TextFillColorSecondaryBrush");
            try
            {
                statusText.Foreground = Application.Current.Resources[brushKey]
                    as Brush;
            }
            catch
            {
            }

            installButton.IsEnabled = !ready;
            installButton.Content = ready ? "已就绪" : "下载并安装";
        }

        private void InstallComponent_Click(
            object sender,
            RoutedEventArgs args)
        {
            FrameworkElement element = sender as FrameworkElement;
            string id = element == null ? null : element.Tag as string;
            if (String.IsNullOrWhiteSpace(id))
            {
                return;
            }

            ComponentInfoBar.Severity = InfoBarSeverity.Informational;
            ComponentInfoBar.Title = "正在安装组件";
            ComponentInfoBar.Message = "准备下载…";
            ComponentInfoBar.IsOpen = true;
            if (element is Button button)
            {
                button.IsEnabled = false;
            }

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error;
                bool ok = ComponentService.Install(
                    _settings,
                    id,
                    delegate(string text, double fraction)
                    {
                        DispatcherQueue.TryEnqueue(delegate
                        {
                            ComponentInfoBar.Message = text;
                        });
                    },
                    _host.Log,
                    out error);

                DispatcherQueue.TryEnqueue(delegate
                {
                    ComponentInfoBar.Severity = ok
                        ? InfoBarSeverity.Success
                        : InfoBarSeverity.Error;
                    ComponentInfoBar.Title = ok ? "组件已安装" : "组件安装失败";
                    ComponentInfoBar.Message = ok
                        ? "重新检测后即可看到状态更新。"
                        : (error ?? "未知错误。");
                    ComponentInfoBar.IsOpen = true;
                    RefreshComponents();
                });
            });
        }

        /// <summary>关于页的版本号连点五下解锁开发者入口。</summary>
        private void VersionText_Tapped(
            object sender,
            TappedRoutedEventArgs args)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastVersionTapUtc).TotalSeconds > 3.0)
            {
                _versionTapCount = 0;
            }

            _lastVersionTapUtc = now;
            _versionTapCount++;
            if (_versionTapCount < 5)
            {
                return;
            }

            _versionTapCount = 0;
            UnlockDeveloperCenter();
        }

        private void UnlockDeveloperCenter()
        {
            if (!DeveloperCenterServer.EnsureStarted(_settings, _host.Log))
            {
                _ = ShowMessageDialogAsync(
                    "开发者管理中心",
                    "本地后台启动失败，请查看 launcher.log。");
                return;
            }

            DeveloperNavItem.Visibility = Visibility.Visible;
            _ = ShowMessageDialogAsync(
                "开发者管理中心",
                "本地后台已启动，入口在左侧栏左下角。");
        }

        private void SettingsNavigationView_ItemInvoked(
            NavigationView sender,
            NavigationViewItemInvokedEventArgs args)
        {
            NavigationViewItem item = args.InvokedItemContainer
                as NavigationViewItem;
            if (item == null
                || !String.Equals(
                    item.Tag as string,
                    "Developer",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            OpenDeveloperCenter();
        }

        private void OpenDeveloperCenter()
        {
            if (!DeveloperCenterServer.EnsureStarted(_settings, _host.Log))
            {
                _ = ShowMessageDialogAsync(
                    "开发者管理中心",
                    "本地后台启动失败，请查看 launcher.log。");
                return;
            }

            OpenUrl(DeveloperCenterUrl);
        }

        public void ShowWindow(string pageTag)
        {
            RefreshServiceState();
            RefreshUpdateStates();
            _ = RefreshApiBalanceAsync();

            if (!String.IsNullOrEmpty(pageTag))
            {
                SelectPage(pageTag);
            }

            try
            {
                Activate();
                _appWindow.Show();
                NativeMethods.SetForegroundWindow(_windowHandle);
            }
            catch
            {
            }
        }

        public void SelectPage(string pageTag)
        {
            string target = String.IsNullOrEmpty(pageTag) ? "General" : pageTag;
            string pluginTab = null;
            int tabSeparator = target.IndexOf(':');
            if (tabSeparator > 0)
            {
                pluginTab = target.Substring(tabSeparator + 1);
                target = target.Substring(0, tabSeparator);
            }

            GeneralPage.Visibility = target == "General" ? Visibility.Visible : Visibility.Collapsed;
            ThemePage.Visibility = target == "Theme" ? Visibility.Visible : Visibility.Collapsed;
            ApiPage.Visibility = target == "Api" ? Visibility.Visible : Visibility.Collapsed;
            AlertsPage.Visibility = target == "Alerts" ? Visibility.Visible : Visibility.Collapsed;
            ServicePage.Visibility = target == "Service" ? Visibility.Visible : Visibility.Collapsed;
            PluginsPage.Visibility = target == "Plugins" ? Visibility.Visible : Visibility.Collapsed;
            ComponentsPage.Visibility = target == "Components" ? Visibility.Visible : Visibility.Collapsed;
            UpdatesPage.Visibility = target == "Updates" ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = target == "About" ? Visibility.Visible : Visibility.Collapsed;

            FrameworkElement page = target switch
            {
                "Theme" => ThemePage,
                "Api" => ApiPage,
                "Alerts" => AlertsPage,
                "Service" => ServicePage,
                "Plugins" => PluginsPage,
                "Components" => ComponentsPage,
                "Updates" => UpdatesPage,
                "About" => AboutPage,
                _ => GeneralPage
            };

            NavigationViewItem item = target switch
            {
                "Theme" => ThemeNavItem,
                "Api" => ApiNavItem,
                "Alerts" => AlertsNavItem,
                "Service" => ServiceNavItem,
                "Plugins" => PluginsNavItem,
                "Components" => ComponentsNavItem,
                "Updates" => UpdatesNavItem,
                "About" => AboutNavItem,
                _ => GeneralNavItem
            };

            _suppressNavigation = true;
            try
            {
                SettingsNavigationView.SelectedItem = item;
                SettingsScroller.ChangeView(null, 0, null, true);
            }
            finally
            {
                _suppressNavigation = false;
            }

            AnimatePage(page);
            if (target == "Plugins" && !String.IsNullOrEmpty(pluginTab))
            {
                SelectPluginTab(pluginTab);
            }

            if (target == "About")
            {
                _ = LoadAuthorAvatarAsync();
            }

            if (target == "Components")
            {
                RefreshComponents();
            }
        }

        /// <summary>预览用：允许 --settings-preview=Plugins:Online 直接落在指定分页。</summary>
        private void SelectPluginTab(string tab)
        {
            if (String.Equals(tab, "Online", StringComparison.OrdinalIgnoreCase))
            {
                PluginViewTabs.SelectedItem = OnlinePluginsTab;
                return;
            }

            if (String.Equals(tab, "Local", StringComparison.OrdinalIgnoreCase))
            {
                PluginViewTabs.SelectedItem = LocalPluginsTab;
                return;
            }

            PluginViewTabs.SelectedItem = FeaturedPluginsTab;
        }

        private void AnimatePage(FrameworkElement page)
        {
            if (page == null || SettingsRoot.XamlRoot == null)
            {
                return;
            }

            page.RenderTransform = new TranslateTransform { Y = 8 };
            page.Opacity = 0;

            DoubleAnimation opacity = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(opacity, page);
            Storyboard.SetTargetProperty(opacity, "Opacity");

            DoubleAnimation offset = new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(offset, page);
            Storyboard.SetTargetProperty(
                offset,
                "(UIElement.RenderTransform).(TranslateTransform.Y)");

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(opacity);
            storyboard.Children.Add(offset);
            storyboard.Begin();
        }

        private void ConfigureWindow(WindowId windowId)
        {
            DisplayArea displayArea = DisplayArea.GetFromWindowId(
                windowId,
                DisplayAreaFallback.Primary);
            if (displayArea == null)
            {
                return;
            }

            double scale = GetDpiScale();
            RectInt32 workArea = displayArea.WorkArea;
            int desiredWidth = ToPhysicalPixels(DefaultWindowWidth, scale);
            int desiredHeight = ToPhysicalPixels(DefaultWindowHeight, scale);
            int minimumWidth = ToPhysicalPixels(MinimumWindowWidth, scale);
            int minimumHeight = ToPhysicalPixels(MinimumWindowHeight, scale);
            int margin = ToPhysicalPixels(WorkAreaMargin, scale);

            int width = Math.Clamp(
                desiredWidth,
                1,
                Math.Max(1, workArea.Width - margin));
            int height = Math.Clamp(
                desiredHeight,
                1,
                Math.Max(1, workArea.Height - margin));
            int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

            _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
                presenter.PreferredMinimumWidth = Math.Min(minimumWidth, workArea.Width);
                presenter.PreferredMinimumHeight = Math.Min(minimumHeight, workArea.Height);
            }

            ApplyTitleBarButtonColors();
        }

        private void SettingsRoot_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            ApplyResponsiveLayout(args.NewSize.Width);
        }

        private void ApplyResponsiveLayout(double width)
        {
            bool compact = width < NavigationCompactThreshold;
            SettingsNavigationView.PaneDisplayMode = compact
                ? NavigationViewPaneDisplayMode.LeftCompact
                : NavigationViewPaneDisplayMode.Left;
            SettingsNavigationView.IsPaneOpen = !compact;

            double measuredWidth = SettingsScroller.ActualWidth > 0
                ? SettingsScroller.ActualWidth
                    - SettingsScroller.Padding.Left
                    - SettingsScroller.Padding.Right
                : width
                    - (compact
                        ? SettingsNavigationView.CompactPaneLength
                        : SettingsNavigationView.OpenPaneLength)
                    - 40;
            double availableWidth = Math.Max(320, measuredWidth);
            SettingsContentHost.Width = Math.Min(760, availableWidth);
        }

        private void SettingsNavigationView_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (_suppressNavigation || _initializing)
            {
                return;
            }

            if (args.SelectedItem is NavigationViewItem item
                && item.Tag is string pageTag)
            {
                SelectPage(pageTag);
            }
        }

        private void PortModeRadio_Checked(object sender, RoutedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            UpdatePortModeControls();
        }

        private void UpdatePortModeControls()
        {
            string mode = GetTag(DefaultPortRadio.IsChecked == true
                ? DefaultPortRadio
                : FixedPortRadio.IsChecked == true
                    ? FixedPortRadio
                    : RandomPortRadio);

            FixedPortRow.Visibility = mode == "Fixed"
                ? Visibility.Visible
                : Visibility.Collapsed;
            FixedPortBox.IsEnabled = mode == "Fixed";
            DefaultPortWarning.IsOpen = mode == "Default";

            if (!_initializing)
            {
                _settings.PortMode = mode;
                SaveSettings();
                PortChangeInfoBar.IsOpen = IsServiceRunning();
            }
        }

        private void FixedPortBox_ValueChanged(
            NumberBox sender,
            NumberBoxValueChangedEventArgs args)
        {
            if (_initializing || Double.IsNaN(sender.Value))
            {
                return;
            }

            _settings.FixedPort = (int)Math.Round(sender.Value);
            SaveSettings();
            PortChangeInfoBar.IsOpen = IsServiceRunning();
        }

        private bool IsServiceRunning()
        {
            return _host.GetServiceStatus()?.IndexOf(
                "正在运行",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefreshServiceState()
        {
            bool running = IsServiceRunning();
            ServiceStatusText.Text = _host.GetServiceStatus();
            RestartServiceButton.Content = running
                ? "重启服务"
                : "启动服务";
            StopServiceButton.IsEnabled = running;
            PortChangeInfoBar.IsOpen = running;
        }

        private void ThemeComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.Theme = GetSelectedTag(ThemeComboBox, "System");
            SaveSettings();
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            string theme = GetSelectedTag(ThemeComboBox, "System");
            ElementTheme elementTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            LauncherAppearance.SetTheme(elementTheme);
            ApplyTitleBarButtonColors();
        }

        private void AccentSourceComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.AccentSource =
                GetSelectedTag(AccentSourceComboBox, "System");
            SaveSettings();
            ApplyAccent();
        }

        private void AccentColorPicker_ColorChanged(
            ColorPicker sender,
            ColorChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            AccentColorSwatch.Background = new SolidColorBrush(args.NewColor);
            _settings.AccentColor = ToHex(args.NewColor);
            SaveSettings();
            ApplyAccent();
        }

        private void ApplyAccent()
        {
            string source = GetSelectedTag(AccentSourceComboBox, "System");
            bool custom = source == "Custom";
            CustomAccentButton.Visibility = custom
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!custom)
            {
                SettingsRoot.Resources.Remove("AccentFillColorDefaultBrush");
                return;
            }

            Windows.UI.Color color = AccentColorPicker.Color;
            SettingsRoot.Resources["AccentFillColorDefaultBrush"] =
                new SolidColorBrush(color);
            AccentColorSwatch.Background = new SolidColorBrush(color);
        }

        private void MaterialComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.Material =
                GetSelectedTag(MaterialComboBox, "Mica");
            SaveSettings();
            LauncherAppearance.SetMaterial(
                ResolveMaterial(_settings.Material));
        }

        private void ApplyTitleBarButtonColors()
        {
            if (_appWindow == null)
            {
                return;
            }

            bool dark = IsDarkTheme();
            AppWindowTitleBar titleBar = _appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonInactiveForegroundColor = dark
                ? ColorHelper.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
                : ColorHelper.FromArgb(0xB8, 0x10, 0x10, 0x10);
            titleBar.ButtonHoverBackgroundColor = dark
                ? ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : ColorHelper.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = dark
                ? ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00);
        }

        private bool IsDarkTheme()
        {
            string theme = GetSelectedTag(ThemeComboBox, "System");
            if (theme == "Dark")
            {
                return true;
            }

            if (theme == "Light")
            {
                return false;
            }

            return new UISettings().GetColorValue(UIColorType.Background).R < 128;
        }

        private void ShowApiKeyCheckBox_Changed(
            object sender,
            RoutedEventArgs args)
        {
            ApiKeyBox.PasswordRevealMode = ShowApiKeyCheckBox.IsChecked == true
                ? PasswordRevealMode.Visible
                : PasswordRevealMode.Hidden;
        }

        private async void SaveApiKey_Click(object sender, RoutedEventArgs args)
        {
            string apiKey = ApiKeyBox.Password.Trim();
            if (!LooksLikeApiKey(apiKey))
            {
                ApiKeyBox.Password = LauncherSettingsStore.ReadApiKey(_settings);
                await ShowMessageDialogAsync(
                    "API Key 格式无效",
                    "API Key 应是以 sk- 开头的完整密钥。输入内容已恢复。");
                return;
            }

            LauncherSettingsStore.SetApiKey(_settings, apiKey);
            _host.ApplyApiKey(apiKey);
            ShowApiKeyValidation(
                InfoBarSeverity.Informational,
                "验证中",
                "正在验证 API Key…");

            BalanceResult result = await System.Threading.Tasks.Task.Run(
                delegate
                {
                    return DeepSeekBalanceClient.Fetch(apiKey);
                });

            _settings.ApiKeyValidatedUtc = DateTime.UtcNow;
            _settings.ApiKeyLastValidationSucceeded = result.Ok;
            LauncherSettingsStore.Save(_settings);

            if (result.Ok)
            {
                ShowApiKeyValidation(
                    InfoBarSeverity.Success,
                    "有效",
                    "API Key 验证通过。");
                BalanceValueText.Text = result.Display;
                BalanceUpdatedText.Text = "刚刚更新";
                _host.RefreshBalance();
            }
            else
            {
                ShowApiKeyValidation(
                    InfoBarSeverity.Error,
                    "无效",
                    result.Error);
            }
        }

        private void ClearApiKey_Click(object sender, RoutedEventArgs args)
        {
            ApiKeyBox.Password = String.Empty;
            LauncherSettingsStore.SetApiKey(_settings, String.Empty);
            _host.ApplyApiKey(String.Empty);
            ApiKeyStatusInfoBar.IsOpen = false;
            BalanceValueText.Text = "未配置";
            BalanceUpdatedText.Text = "等待 API Key";
        }

        private static bool LooksLikeApiKey(string apiKey)
        {
            return !String.IsNullOrWhiteSpace(apiKey)
                && apiKey.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
                && apiKey.Length >= 20;
        }

        private void ShowApiKeyValidation(
            InfoBarSeverity severity,
            string title,
            string message)
        {
            ApiKeyStatusInfoBar.Severity = severity;
            ApiKeyStatusInfoBar.Title = title;
            ApiKeyStatusInfoBar.Message = message;
            ApiKeyStatusInfoBar.IsOpen = true;
        }

        private async System.Threading.Tasks.Task RefreshApiBalanceAsync()
        {
            string apiKey = LauncherSettingsStore.ReadApiKey(_settings);
            if (String.IsNullOrWhiteSpace(apiKey))
            {
                BalanceValueText.Text = "未配置";
                BalanceUpdatedText.Text = "等待 API Key";
                return;
            }

            BalanceValueText.Text = "查询中…";
            BalanceUpdatedText.Text = "正在读取余额";
            BalanceResult result = await System.Threading.Tasks.Task.Run(
                delegate { return DeepSeekBalanceClient.Fetch(apiKey); });
            if (result.Ok)
            {
                BalanceValueText.Text = result.Display;
                BalanceUpdatedText.Text =
                    result.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss")
                    + " 更新";
            }
            else
            {
                BalanceValueText.Text = "查询失败";
                BalanceUpdatedText.Text = result.Error;
            }
        }

        private void UpdateModeComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.LauncherUpdateMode = GetSelectedTag(
                LauncherUpdateModeComboBox,
                "Install");
            _settings.DshUpdateMode = GetSelectedTag(
                DshUpdateModeComboBox,
                "Check");
            _settings.PluginUpdateMode = GetSelectedTag(
                PluginUpdateModeComboBox,
                "Check");
            SaveSettings();
            UpdateUpdateOptions();
        }

        private void UpdateUpdateOptions()
        {
            bool launcherUpdatesEnabled =
                GetSelectedTag(LauncherUpdateModeComboBox, "Install") != "Off";
            bool dshUpdatesEnabled =
                GetSelectedTag(DshUpdateModeComboBox, "Off") != "Off";
            bool pluginUpdatesEnabled =
                GetSelectedTag(PluginUpdateModeComboBox, "Off") != "Off";
            bool anyUpdatesEnabled = launcherUpdatesEnabled
                || dshUpdatesEnabled
                || pluginUpdatesEnabled;

            UpdateIntervalComboBox.IsEnabled = anyUpdatesEnabled;
            UpdateReminderToggle.IsEnabled = anyUpdatesEnabled;
            if (!anyUpdatesEnabled)
            {
                UpdateReminderToggle.IsOn = false;
            }

            PluginUpdateReminderToggle.IsEnabled = pluginUpdatesEnabled;
            if (!pluginUpdatesEnabled)
            {
                PluginUpdateReminderToggle.IsOn = false;
            }

            DshUpdateWarning.IsOpen =
                GetSelectedTag(DshUpdateModeComboBox, "Off") == "Install";
        }

        private async void BrowseDshDirectory_Click(
            object sender,
            RoutedEventArgs args)
        {
            try
            {
                FolderPicker picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, _windowHandle);

                Windows.Storage.StorageFolder folder =
                    await picker.PickSingleFolderAsync();
                if (folder == null)
                {
                    return;
                }

                string marker = Path.Combine(
                    folder.Path,
                    @"node_modules\@deepseek-ai\dsh\lib\bin.js");
                if (!File.Exists(marker))
                {
                    await ShowMessageDialogAsync(
                        "DSH 目录无效",
                        "所选目录中未找到 node_modules\\@deepseek-ai\\dsh\\lib\\bin.js。"
                        + Environment.NewLine
                        + "请选择 DSH 的安装根目录。");
                    return;
                }

                DshPathBox.Text = folder.Path;
                _settings.DshRoot = folder.Path;
                SaveSettings();
                _host.SynchronizeInstallerPaths();
                NextServiceStartInfoBar.IsOpen = true;
            }
            catch (Exception exception)
            {
                await ShowMessageDialogAsync("无法选择目录", exception.Message);
            }
        }

        private async void BrowseNodeExecutable_Click(
            object sender,
            RoutedEventArgs args)
        {
            try
            {
                FileOpenPicker picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                InitializeWithWindow.Initialize(picker, _windowHandle);

                Windows.Storage.StorageFile file =
                    await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                if (!String.Equals(
                        file.Name,
                        "node.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await ShowMessageDialogAsync(
                        "Node 路径无效",
                        "请选择 node.exe。");
                    return;
                }

                NodePathBox.Text = file.Path;
                _settings.NodePath = file.Path;
                SaveSettings();
            }
            catch (Exception exception)
            {
                await ShowMessageDialogAsync("无法选择文件", exception.Message);
            }
        }

        private async System.Threading.Tasks.Task ShowMessageDialogAsync(
            string title,
            string message)
        {
            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "确定"
            };
            await dialog.ShowAsync();
        }

        private void OpenApiKeysPage_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl("https://platform.deepseek.com/api_keys");
        }

        private void OpenGitHub_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl("https://github.com/" + Constants.Repository);
        }

        private void OpenFeedback_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl("https://github.com/" + Constants.Repository + "/issues/new");
        }

        private void OpenBilibili_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl("https://space.bilibili.com/32823052");
        }

        private void OpenDouyin_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl(
                "https://www.douyin.com/user/MS4wLjABAAAAO-TLYuTYBCL42OGO_M4sckp_TZUfECJkbQmQOJmo5OSf-2Uw5BFB6AZ7Vr7tUSFI");
        }

        private async System.Threading.Tasks.Task LoadAuthorAvatarAsync()
        {
            if (_authorAvatarLoading)
            {
                return;
            }

            _authorAvatarLoading = true;
            AuthorAvatarLoading.IsActive = true;
            AuthorAvatarLoading.Visibility = Visibility.Visible;
            AuthorAvatarImage.Visibility = Visibility.Collapsed;
            try
            {
                BilibiliAvatarResult result =
                    await System.Threading.Tasks.Task.Run(
                        BilibiliProfileService.FetchAvatarOnceAsync);
                if (!result.Ok)
                {
                    AuthorAvatarLoading.IsActive = false;
                    _host.Log(
                        "Bilibili avatar load failed: "
                        + result.Error);
                    return;
                }

                using (InMemoryRandomAccessStream stream =
                    new InMemoryRandomAccessStream())
                {
                    using (DataWriter writer =
                        new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(result.Bytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                    }

                    stream.Seek(0);
                    Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap =
                        new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    AuthorAvatarImage.Source = bitmap;
                    AuthorAvatarImage.Visibility = Visibility.Visible;
                    AuthorAvatarLoading.IsActive = false;
                    AuthorAvatarLoading.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
            }
            finally
            {
                _authorAvatarLoading = false;
            }
        }

        private void OpenDshLogs_Click(object sender, RoutedEventArgs args)
        {
            string path = Path.Combine(DshPathBox.Text.Trim(), "logs");
            OpenDirectory(path);
        }

        private void OpenLauncherLogs_Click(object sender, RoutedEventArgs args)
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness");
            OpenDirectory(path);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private static void OpenDirectory(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private void SettingsWindow_Closed(object sender, WindowEventArgs args)
        {
            SettingsRoot.SizeChanged -= SettingsRoot_SizeChanged;
            SettingsRoot.RemoveHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(SettingsRoot_PointerPressed));
            _host.UpdateStateChanged -= Host_UpdateStateChanged;
            LauncherAppearance.Unregister(this);
            Destroyed();
        }

        private void SettingsRoot_PointerPressed(
            object sender,
            PointerRoutedEventArgs args)
        {
            DependencyObject current = args.OriginalSource as DependencyObject;
            while (current != null && !ReferenceEquals(current, SettingsRoot))
            {
                if (current is NumberBox
                    || current is TextBox
                    || current is PasswordBox
                    || current is ComboBox
                    || current is Microsoft.UI.Xaml.Controls.Button
                    || current is CheckBox
                    || current is RadioButton
                    || current is ToggleSwitch
                    || current is Slider)
                {
                    return;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            SettingsNavigationView.Focus(FocusState.Programmatic);
        }

        private double GetDpiScale()
        {
            uint dpi = NativeMethods.GetDpiForWindow(_windowHandle);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        private static int ToPhysicalPixels(int logicalPixels, double scale)
        {
            return Math.Max(
                1,
                (int)Math.Round(logicalPixels * scale, MidpointRounding.AwayFromZero));
        }

        private static LauncherMaterialKind ResolveMaterial(string tag)
        {
            return tag switch
            {
                "MicaAlt" => LauncherMaterialKind.MicaAlt,
                "AcrylicThin" => LauncherMaterialKind.AcrylicThin,
                "AcrylicBase" => LauncherMaterialKind.AcrylicBase,
                "Solid" => LauncherMaterialKind.Solid,
                _ => LauncherMaterialKind.Mica
            };
        }

        private static string ToHex(Windows.UI.Color color)
        {
            return "#"
                + color.R.ToString("X2")
                + color.G.ToString("X2")
                + color.B.ToString("X2");
        }

        private static Windows.UI.Color ParseColor(string value)
        {
            try
            {
                string hex = (value ?? String.Empty).Trim().TrimStart('#');
                if (hex.Length == 6)
                {
                    return Windows.UI.Color.FromArgb(
                        255,
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
                }
            }
            catch
            {
            }

            return Windows.UI.Color.FromArgb(255, 10, 132, 255);
        }

        private static string GetSelectedTag(ComboBox comboBox, string fallback)
        {
            if (comboBox.SelectedItem is ComboBoxItem item
                && item.Tag is string tag)
            {
                return tag;
            }

            return fallback;
        }

        private static string GetTag(FrameworkElement element)
        {
            return element.Tag as string ?? String.Empty;
        }

    }
}
