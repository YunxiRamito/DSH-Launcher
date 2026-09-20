using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
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
        private const int GwlpOwner = -8;

        private readonly AppWindow _appWindow;
        private readonly IntPtr _windowHandle;
        private readonly SettingsWindowHost _host;
        private readonly LauncherSettings _settings;
        private bool _authorAvatarLoading;
        private bool _initializing = true;
        private bool _suppressNavigation;

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

            Title = "DeepSeek Harness 启动器设置";
            VersionText.Text = "v" + Constants.Version;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            LauncherAppearance.Register(
                this,
                SettingsRoot,
                delegate(Brush brush) { SettingsRoot.Background = brush; });

            _windowHandle = WindowNative.GetWindowHandle(this);
            if (ownerHandle != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(
                    _windowHandle,
                    GwlpOwner,
                    ownerHandle);
            }

            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            ConfigureWindow(windowId);
            LoadSettingsIntoControls();
            ApplyTheme();
            ApplyAccent();
            LauncherAppearance.SetMaterial(
                ResolveMaterial(_settings.Material));
            UpdatePortModeControls();
            UpdateUpdateOptions();
            WireSettingsEvents();

            SettingsRoot.SizeChanged += SettingsRoot_SizeChanged;
            Closed += SettingsWindow_Closed;

            _initializing = false;
            SettingsNavigationView.SelectedItem = GeneralNavItem;
            SelectPage("General");
            ApplyResponsiveLayout(SettingsRoot.ActualWidth > 0 ? SettingsRoot.ActualWidth : DefaultWindowWidth);
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
                }
            };
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
            SelectTaggedItem(
                LauncherUpdateModeComboBox,
                _settings.LauncherUpdateMode);
            SelectTaggedItem(DshUpdateModeComboBox, _settings.DshUpdateMode);
            SelectTaggedItem(
                UpdateIntervalComboBox,
                _settings.UpdateInterval);

            UpdateReminderToggle.IsOn = _settings.UpdateReminder;
            ServiceReminderToggle.IsOn = _settings.ServiceStartReminder;
            RechargeReminderToggle.IsOn = _settings.RechargeReminder;

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
            ServiceStatusText.Text = _host.GetServiceStatus();
            UpdateCustomThresholdStates();
        }

        private void WireSettingsEvents()
        {
            StartWithWindowsToggle.Toggled += StartWithWindowsToggle_Toggled;
            SilentStartComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            UpdateSourceComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            UpdateIntervalComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            FixedPortBox.ValueChanged += FixedPortBox_ValueChanged;

            UpdateReminderToggle.Toggled += ReminderToggle_Toggled;
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
                ServiceStatusText.Text = _host.GetServiceStatus();
            };
            StopServiceButton.Click += delegate
            {
                _host.StopService();
                ServiceStatusText.Text = _host.GetServiceStatus();
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
            RefreshUpdateStates();
        }

        private void Host_UpdateStateChanged()
        {
            RefreshUpdateStates();
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
                        ? String.Empty
                        : "v" + state.Version;
                    break;
                case UpdateUiActivity.Available:
                    actionButton.Content = "立即更新";
                    resultBar.Title = "发现新版本";
                    resultBar.Message = String.IsNullOrWhiteSpace(state.Version)
                        ? String.Empty
                        : "v" + state.Version;
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
            _settings.UpdateInterval = GetSelectedTag(
                UpdateIntervalComboBox,
                "EveryStart");
            SaveSettings();
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

        public void ShowWindow(string pageTag)
        {
            ServiceStatusText.Text = _host.GetServiceStatus();
            PortChangeInfoBar.IsOpen = IsServiceRunning();
            RefreshUpdateStates();

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

            GeneralPage.Visibility = target == "General" ? Visibility.Visible : Visibility.Collapsed;
            ThemePage.Visibility = target == "Theme" ? Visibility.Visible : Visibility.Collapsed;
            ApiPage.Visibility = target == "Api" ? Visibility.Visible : Visibility.Collapsed;
            AlertsPage.Visibility = target == "Alerts" ? Visibility.Visible : Visibility.Collapsed;
            ServicePage.Visibility = target == "Service" ? Visibility.Visible : Visibility.Collapsed;
            UpdatesPage.Visibility = target == "Updates" ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = target == "About" ? Visibility.Visible : Visibility.Collapsed;

            FrameworkElement page = target switch
            {
                "Theme" => ThemePage,
                "Api" => ApiPage,
                "Alerts" => AlertsPage,
                "Service" => ServicePage,
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
            if (target == "About")
            {
                _ = LoadAuthorAvatarAsync();
            }
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
            SaveSettings();
            UpdateUpdateOptions();
        }

        private void UpdateUpdateOptions()
        {
            bool launcherUpdatesEnabled =
                GetSelectedTag(LauncherUpdateModeComboBox, "Install") != "Off";
            bool dshUpdatesEnabled =
                GetSelectedTag(DshUpdateModeComboBox, "Off") != "Off";
            bool anyUpdatesEnabled = launcherUpdatesEnabled || dshUpdatesEnabled;

            UpdateIntervalComboBox.IsEnabled = anyUpdatesEnabled;
            UpdateReminderToggle.IsEnabled = anyUpdatesEnabled;
            if (!anyUpdatesEnabled)
            {
                UpdateReminderToggle.IsOn = false;
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
                        + "请选择 DeepSeek Harness 的安装根目录。");
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
            OpenUrl("https://github.com/YunxiRamito/DSH-Launcher");
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
            _host.UpdateStateChanged -= Host_UpdateStateChanged;
            LauncherAppearance.Unregister(this);
            Destroyed();
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
