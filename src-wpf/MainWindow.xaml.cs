using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using WinForms = System.Windows.Forms;
using DrawingBitmap = System.Drawing.Bitmap;

namespace TarkovAutoShade
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings settings = SettingsStore.Load();
        private readonly GammaRampController gammaController = new GammaRampController();
        private readonly DdcCiController ddcController = new DdcCiController();
        private readonly ScreenshotWatcher screenshotWatcher = new ScreenshotWatcher();

        private DispatcherTimer timestampTimer;
        private DispatcherTimer statusDotTimer;
        private DispatcherTimer settingsTimer;
        private DispatcherTimer previewRefreshTimer;
        private DispatcherTimer notificationTimer;
        private DispatcherTimer processWatchTimer;
        private PreviewControl previewControl;
        private GlobalHotkey toggleHotkey;
        private WinForms.NotifyIcon trayIcon;
        private WinForms.ContextMenuStrip trayMenu;
        private AnalysisResult currentAnalysis;
        private string lastAnalyzedPath;
        private string displayDevice;
        private int analysisVersion;
        private int previewVersion;
        private bool filterModeEnabled = true;
        private bool initializing = true;
        private bool closing;
        private bool trayExitRequested;
        private Window closeChoiceDialog;
        private bool updatingHardwareControls;
        private bool promotingCustomPreset;
        private bool processWasDetected;
        private bool autoPausedByProcess;
        private WinEventDelegate foregroundWindowEventHandler;
        private IntPtr foregroundWindowEventHook;
        private MonitorCapabilities monitorCapabilities = new MonitorCapabilities();

        public MainWindow()
        {
            settings.Normalize();
            InitializeComponent();
            InitializePreviewControl();
            InitializeTrayIcon();
            InitializeHotkey();
            InitializeTimers();
            BindSliderEvents();
            BindButtonEvents();
            BindPresetEvents();
            BindDisplayEvents();
            BindProcessWatchEvents();
            BindFolderPicker();
            AboutButton.Click += delegate { ShowAboutWindow(); };
            InitializeDisplay();
            ApplySettingsToSliders();
            InitializeProcessWatcher();
            ConfigureScreenshotFolder();
            RecoverPreviousSession();
            ObsFilterStateStore.WriteDisabled();

            screenshotWatcher.ScreenshotReady += OnScreenshotReady;
            screenshotWatcher.WatcherFaulted += OnWatcherFaulted;

            Loaded += delegate
            {
                initializing = false;
                UpdateWatcherUi();
                UpdateProcessWatchUi();
            };
            Closed += OnClosed;

            RefreshSliderLabels();
            UpdateMetrics();
            UpdateWatcherUi();
            UpdateModeUi();
        }

        private void InitializeDisplay()
        {
            var displays = GammaRampController.EnumerateDisplays();
            DisplayTarget selected = null;
            foreach (DisplayTarget display in displays)
            {
                if (selected == null && display.Primary) selected = display;
                if (string.Equals(display.DeviceName, settings.DisplayDevice,
                    StringComparison.OrdinalIgnoreCase)) selected = display;
            }

            if (selected == null && displays.Count > 0) selected = displays[0];
            if (selected == null)
            {
                DisplayComboBox.Items.Clear();
                DisplayComboBox.Items.Add("未检测到显示器");
                DisplayComboBox.SelectedIndex = 0;
                RefreshMonitorCapabilities();
                return;
            }

            DisplayComboBox.Items.Clear();
            foreach (DisplayTarget display in displays) DisplayComboBox.Items.Add(display);
            displayDevice = selected.DeviceName;
            settings.DisplayDevice = displayDevice;
            DisplayComboBox.SelectedItem = selected;
            RefreshMonitorCapabilities();
        }

        private void ConfigureScreenshotFolder()
        {
            string configuredFolder = settings.ScreenshotFolder;
            string folder = Directory.Exists(configuredFolder) ? configuredFolder : null;
            if (string.IsNullOrWhiteSpace(folder))
            {
                string discovered = ScreenshotFolderLocator.Find();
                if (!string.IsNullOrWhiteSpace(discovered))
                {
                    folder = discovered;
                    settings.ScreenshotFolder = discovered;
                    SettingsStore.Save(settings);
                }
            }

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                folder = null;
            FolderPath.Text = folder ?? "未找到截图目录";
            screenshotWatcher.SetFolder(folder);
            screenshotWatcher.Enabled = settings.AutoWatch;
            if (folder == null)
                ShowNotification("未找到截图目录，请点击“选择目录”按钮手动选择。",
                    NotificationType.Warning);
        }

        private void BindFolderPicker()
        {
            FolderPath.Cursor = Cursors.Hand;
            FolderPath.ToolTip = "点击选择截图目录";
            FolderPath.MouseLeftButtonDown += delegate { OpenScreenshotFolderPicker(); };
            BrowseFolderButton.Click += delegate { OpenScreenshotFolderPicker(); };
        }

        private void OpenScreenshotFolderPicker()
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = "选择 Escape from Tarkov 的 Screenshots 文件夹";
                dialog.SelectedPath = Directory.Exists(settings.ScreenshotFolder) ?
                    settings.ScreenshotFolder : Environment.GetFolderPath(
                        Environment.SpecialFolder.MyDocuments);
                if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

                settings.ScreenshotFolder = dialog.SelectedPath;
                FolderPath.Text = dialog.SelectedPath;
                screenshotWatcher.SetFolder(dialog.SelectedPath);
                screenshotWatcher.Enabled = settings.AutoWatch;
                SettingsStore.Save(settings);
                UpdateWatcherUi();
                ShowNotification("截图目录已更新，自动监听已准备就绪。", NotificationType.Success);
            }
        }

        private void InitializePreviewControl()
        {
            previewControl = new PreviewControl
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            PreviewContainer.Children.Insert(0, previewControl);
            PreviewEmptyState.Visibility = Visibility.Visible;
        }

        private void InitializeTrayIcon()
        {
            System.Drawing.Icon icon = null;
            try
            {
                icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch { }
            if (icon == null) icon = System.Drawing.SystemIcons.Application;

            trayMenu = new WinForms.ContextMenuStrip();
            trayMenu.Items.Add("显示主窗口", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("开启 / 关闭滤镜 (" +
                FormatHotkey(settings.HotkeyKeyCode, settings.HotkeyModifiers) + ")",
                null, delegate { ToggleFilter(); });
            trayMenu.Items.Add(new WinForms.ToolStripSeparator());
            trayMenu.Items.Add("退出程序", null, delegate { RequestExitFromTray(); });

            trayIcon = new WinForms.NotifyIcon
            {
                Icon = icon,
                Text = "TarkovAutoShade",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ShowFromTray(); };

            StateChanged += delegate
            {
                if (WindowState != WindowState.Minimized) return;
                Hide();
                trayIcon.ShowBalloonTip(1800, "TarkovAutoShade",
                    "已最小化到系统托盘。双击图标恢复窗口。",
                    WinForms.ToolTipIcon.Info);
            };
        }

        private void ShowFromTray()
        {
            if (closing) return;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void RequestExitFromTray()
        {
            if (closing) return;
            trayExitRequested = true;

            // The close prompt owns a nested dispatcher loop. Close it first;
            // the original OnClosing call will then finish the main-window exit.
            if (closeChoiceDialog != null)
            {
                closeChoiceDialog.DialogResult = false;
                return;
            }

            Close();
        }

        private void InitializeHotkey()
        {
            toggleHotkey = new GlobalHotkey(
                this, 0x5441,
                (GlobalHotkey.Modifiers)settings.HotkeyModifiers,
                (WinForms.Keys)settings.HotkeyKeyCode);
            toggleHotkey.HotkeyPressed += delegate
            {
                Dispatcher.BeginInvoke(new Action(ToggleFilter));
            };
            UpdateHotkeyUi();
        }

        private void UpdateHotkeyUi()
        {
            string hotkey = FormatHotkey(settings.HotkeyKeyCode, settings.HotkeyModifiers);
            HotkeyText.Text = hotkey;
            ToggleButton.Content = (IsFilterRunning() ? "关闭滤镜  " : "开启滤镜  ") + hotkey;
        }

        private void ShowHotkeyCapture()
        {
            int capturedKey = settings.HotkeyKeyCode;
            int capturedModifiers = settings.HotkeyModifiers;
            var dialog = new Window
            {
                Title = "设置滤镜快捷键",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 390,
                Height = 260,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("CrtSurfaceBrush"),
                Foreground = (Brush)FindResource("PhosphorWhiteBrush")
            };

            var root = new Grid { Margin = new Thickness(22) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            var hint = new TextBlock
            {
                Text = "请按下组合键（例如 Ctrl + F8），再点击确定。",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontUi"),
                FontSize = 11,
                Foreground = (Brush)FindResource("PhosphorDimBrush")
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            var captured = new TextBlock
            {
                Text = FormatHotkey(capturedKey, capturedModifiers),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("AmberAlertBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(captured, 1);
            root.Children.Add(captured);

            var note = new TextBlock
            {
                Text = "Esc 取消本次设置",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontUi"),
                FontSize = 10,
                Foreground = (Brush)FindResource("PhosphorDimBrush"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetRow(note, 2);
            root.Children.Add(note);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var cancel = new Button { Content = "取消", Width = 72, Height = 30, Style = (Style)FindResource("TacticalButton") };
            var confirm = new Button { Content = "确定", Width = 72, Height = 30, Margin = new Thickness(10, 0, 0, 0), Style = (Style)FindResource("TacticalButtonPrimary") };
            cancel.Click += delegate { dialog.DialogResult = false; };
            confirm.Click += delegate { dialog.DialogResult = true; };
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dialog.DialogResult = false; e.Handled = true; return; }
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                    key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift ||
                    key == Key.LWin || key == Key.RWin) return;
                int virtualKey = KeyInterop.VirtualKeyFromKey(key);
                if (virtualKey <= 0) return;
                ModifierKeys modifiers = Keyboard.Modifiers;
                capturedKey = virtualKey;
                capturedModifiers = 0;
                if ((modifiers & ModifierKeys.Alt) != 0) capturedModifiers |= 0x0001;
                if ((modifiers & ModifierKeys.Control) != 0) capturedModifiers |= 0x0002;
                if ((modifiers & ModifierKeys.Shift) != 0) capturedModifiers |= 0x0004;
                captured.Text = FormatHotkey(capturedKey, capturedModifiers);
                e.Handled = true;
            };
            dialog.Content = root;
            dialog.Loaded += delegate { dialog.Focus(); };

            if (dialog.ShowDialog() != true) return;
            int previousKey = settings.HotkeyKeyCode;
            int previousModifiers = settings.HotkeyModifiers;
            settings.HotkeyKeyCode = capturedKey;
            settings.HotkeyModifiers = capturedModifiers;
            bool registered = toggleHotkey != null && toggleHotkey.Rebind(
                (GlobalHotkey.Modifiers)capturedModifiers,
                (WinForms.Keys)capturedKey);
            if (!registered)
            {
                settings.HotkeyKeyCode = previousKey;
                settings.HotkeyModifiers = previousModifiers;
                if (toggleHotkey != null)
                {
                    toggleHotkey.Rebind(
                        (GlobalHotkey.Modifiers)previousModifiers,
                        (WinForms.Keys)previousKey);
                }
                UpdateHotkeyUi();
                UpdateModeUi();
                ShowNotification("快捷键注册失败，可能已被其他程序占用；已恢复原按键。",
                    NotificationType.Warning);
                return;
            }
            SaveSettings();
            UpdateHotkeyUi();
            UpdateModeUi();
            ShowNotification("全局快捷键已更新为 " + FormatHotkey(capturedKey, capturedModifiers),
                NotificationType.Success);
        }

        private static string FormatHotkey(int keyCode, int modifiers)
        {
            if (keyCode <= 0) return "未设置";
            var parts = new List<string>();
            if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((modifiers & 0x0001) != 0) parts.Add("Alt");
            if ((modifiers & 0x0004) != 0) parts.Add("Shift");
            parts.Add(((WinForms.Keys)keyCode).ToString());
            return string.Join(" + ", parts.ToArray());
        }

        private void InitializeTimers()
        {
            timestampTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timestampTimer.Tick += UpdateTimestamp;
            timestampTimer.Start();
            UpdateTimestamp(null, null);

            statusDotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            statusDotTimer.Tick += delegate
            {
                if (!IsFilterRunning())
                {
                    StatusDot.BeginAnimation(UIElement.OpacityProperty, null);
                    StatusDot.Opacity = 0.65;
                    return;
                }
                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.4,
                    Duration = TimeSpan.FromSeconds(1),
                    AutoReverse = true
                };
                StatusDot.BeginAnimation(UIElement.OpacityProperty, animation);
            };
            statusDotTimer.Start();

            settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            settingsTimer.Tick += delegate
            {
                settingsTimer.Stop();
                SaveSettings();
            };

            previewRefreshTimer = new DispatcherTimer { Interval =
                TimeSpan.FromMilliseconds(180) };
            previewRefreshTimer.Tick += delegate
            {
                previewRefreshTimer.Stop();
                RefreshCurrentPreview();
            };
        }

        private void UpdateTimestamp(object sender, EventArgs e)
        {
            TimestampText.Text = string.Format("{0:yyyy.MM.dd} — {0:HH:mm:ss}", DateTime.Now);
        }

        private void BindSliderEvents()
        {
            BindSlider(ShadowSlider, ShadowValue, false);
            BindSlider(HighlightSlider, HighlightValue, false);
            BindSlider(ColorSlider, ColorValue, false);
            BindSlider(IndoorSlider, IndoorValue, false);
            BindSlider(GuardSlider, GuardValue, false);
            BindSlider(BlackSlider, BlackValue, false);
            BindSlider(StrengthSlider, StrengthValue, false);
            BindSlider(ExposureSlider, ExposureValue, true);
            BindSlider(ContrastSlider, ContrastValue, true);
            BindSlider(WarmthSlider, WarmthValue, true);
            BindSlider(SaturationSlider, SaturationValue, true);
        }

        private void BindSlider(Slider slider, TextBlock value, bool signed)
        {
            slider.ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> e)
            {
                int number = (int)Math.Round(e.NewValue);
                value.Text = signed ? FormatSigned(number) : number.ToString();
                if (initializing) return;
                if (PresetComboBox.SelectedIndex != 5)
                {
                    promotingCustomPreset = true;
                    PresetComboBox.SelectedIndex = 5;
                    promotingCustomPreset = false;
                }
                SyncSettingsFromSliders();
                settingsTimer.Stop();
                settingsTimer.Start();
                UpdateCurrentRecommendation();
            };
        }

        private void BindButtonEvents()
        {
            AnalyzeButton.Click += delegate { AnalyzeLatest(false); };
            ToggleButton.Click += delegate { ToggleFilter(); };
            ChangeHotkeyButton.Click += delegate { ShowHotkeyCapture(); };
            ResetDefaultsButton.Click += delegate { RestoreDefaultFilterValues(); };
            SmoothTransitionCheckBox.Checked += delegate
            {
                settings.SmoothTransition = true;
                SaveSettings();
            };
            SmoothTransitionCheckBox.Unchecked += delegate
            {
                settings.SmoothTransition = false;
                SaveSettings();
            };

            MonitorBrightnessSlider.ValueChanged += OnMonitorSliderChanged;
            MonitorContrastSlider.ValueChanged += OnMonitorSliderChanged;
        }

        private void BindDisplayEvents()
        {
            DisplayComboBox.PreviewMouseLeftButtonDown += ForceOpenComboBox;
            PresetComboBox.PreviewMouseLeftButtonDown += ForceOpenComboBox;
            DisplayComboBox.SelectionChanged += delegate
            {
                if (initializing) return;
                var selected = DisplayComboBox.SelectedItem as DisplayTarget;
                if (selected == null || string.IsNullOrWhiteSpace(selected.DeviceName)) return;

                bool wasRunning = IsFilterRunning();
                string error;
                gammaController.RestoreAll(out error);
                RecoveryStore.Clear();
                displayDevice = selected.DeviceName;
                settings.DisplayDevice = displayDevice;
                RefreshMonitorCapabilities();
                if (wasRunning && currentAnalysis != null && currentAnalysis.IsUsable)
                    ApplyRecommendation(currentAnalysis);
                else
                    UpdateModeUi();
                SaveSettings();
                ShowNotification("目标显示器已切换", NotificationType.Success);
            };

            DisplayComboBox.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                if (!DisplayComboBox.IsDropDownOpen) e.Handled = true;
            };
            PresetComboBox.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                if (!PresetComboBox.IsDropDownOpen) e.Handled = true;
            };
        }

        private static void ForceOpenComboBox(object sender, MouseButtonEventArgs e)
        {
            ComboBox comboBox = sender as ComboBox;
            if (comboBox == null || comboBox.IsDropDownOpen) return;
            comboBox.IsDropDownOpen = true;
            e.Handled = true;
        }

        private void BindProcessWatchEvents()
        {
            ProcessComboBox.PreviewMouseLeftButtonDown += ForceOpenComboBox;
            ProcessComboBox.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                if (!ProcessComboBox.IsDropDownOpen) e.Handled = true;
            };
            ProcessWatchCheckBox.Checked += delegate
            {
                settings.ProcessWatchEnabled = true;
                settings.ProcessWatchConfigured = true;
                processWasDetected = IsWatchedProcessActive();
                RefreshProcessList();
                UpdateProcessWatchUi();
                if (!initializing) SaveSettings();
            };
            ProcessWatchCheckBox.Unchecked += delegate
            {
                settings.ProcessWatchEnabled = false;
                settings.ProcessWatchConfigured = true;
                autoPausedByProcess = false;
                UpdateProcessWatchUi();
                if (!initializing) SaveSettings();
            };
            ProcessComboBox.SelectionChanged += delegate
            {
                string selected = ProcessComboBox.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(selected)) return;
                settings.WatchedProcessName = selected;
                processWasDetected = IsWatchedProcessActive();
                UpdateProcessWatchUi();
                if (!initializing) SaveSettings();
            };
        }

        private void InitializeProcessWatcher()
        {
            RefreshProcessList();
            ProcessWatchCheckBox.IsChecked = settings.ProcessWatchEnabled;
            processWasDetected = IsWatchedProcessActive();
            UpdateProcessWatchUi(processWasDetected);
            foregroundWindowEventHandler = OnForegroundWindowChanged;
            foregroundWindowEventHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                foregroundWindowEventHandler,
                0,
                0,
                WineventOutOfContext);
            processWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            processWatchTimer.Tick += delegate { EvaluateProcessWatch(); };
            processWatchTimer.Start();
        }

        private void RefreshProcessList()
        {
            string selected = string.IsNullOrWhiteSpace(settings.WatchedProcessName) ?
                "EscapeFromTarkov.exe" : settings.WatchedProcessName;
            var names = new List<string> { "EscapeFromTarkov.exe" };
            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    try
                    {
                        string name = process.ProcessName + ".exe";
                        if (!names.Contains(name)) names.Add(name);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(selected)) names.Insert(0, selected);

            bool wasInitializing = initializing;
            initializing = true;
            try
            {
                ProcessComboBox.Items.Clear();
                foreach (string name in names) ProcessComboBox.Items.Add(name);
                ProcessComboBox.SelectedItem = selected;
            }
            finally
            {
                initializing = wasInitializing;
            }
        }

        private bool IsWatchedProcessRunning()
        {
            string processName = GetWatchedProcessName();
            if (string.IsNullOrWhiteSpace(processName)) return false;
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (Process process in processes) process.Dispose();
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsWatchedProcessActive()
        {
            string watchedName = GetWatchedProcessName();
            if (string.IsNullOrWhiteSpace(watchedName)) return false;

            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero) return false;
            uint processId;
            if (GetWindowThreadProcessId(foregroundWindow, out processId) == 0 ||
                processId == 0) return false;

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return string.Equals(process.ProcessName, watchedName,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private string GetWatchedProcessName()
        {
            string configured = settings.WatchedProcessName;
            return string.IsNullOrWhiteSpace(configured) ? "" :
                Path.GetFileNameWithoutExtension(configured);
        }

        private void EvaluateProcessWatch()
        {
            if (!settings.ProcessWatchEnabled) return;
            bool detected = IsWatchedProcessActive();
            if (processWasDetected && !detected && IsFilterRunning())
                PauseFilterForProcess();
            else if (!processWasDetected && detected && autoPausedByProcess)
                ResumeFilterForProcess();
            processWasDetected = detected;
            UpdateProcessWatchUi(detected);
        }

        private void OnForegroundWindowChanged(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThreadId,
            uint eventTime)
        {
            if (closing || Dispatcher.HasShutdownStarted) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(EvaluateProcessWatch));
            }
            catch { }
        }

        private void PauseFilterForProcess()
        {
            string error;
            if (!gammaController.RestoreAll(out error))
            {
                ShowNotification("自动关闭滤镜失败：" + error, NotificationType.Warning);
                return;
            }
            RecoveryStore.Clear();
            autoPausedByProcess = true;
            ObsFilterStateStore.WriteDisabled();
            UpdateModeUi();
            ShowNotification("已离开游戏，滤镜已自动关闭", NotificationType.Info);
        }

        private void ResumeFilterForProcess()
        {
            autoPausedByProcess = false;
            if (currentAnalysis != null && currentAnalysis.IsUsable)
            {
                ApplyRecommendation(currentAnalysis);
                ShowNotification("已回到游戏，滤镜已自动开启", NotificationType.Success);
            }
            else
            {
                UpdateModeUi();
                ShowNotification("已回到游戏，请先分析一张截图", NotificationType.Info);
            }
        }

        private void UpdateProcessWatchUi()
        {
            UpdateProcessWatchUi(IsWatchedProcessActive());
        }

        private void UpdateProcessWatchUi(bool active)
        {
            bool enabled = settings.ProcessWatchEnabled;
            ProcessWatchControls.IsEnabled = enabled;
            ProcessWatchControls.Opacity = enabled ? 1.0 : 0.45;
            if (!enabled)
            {
                ProcessWatchStatusText.Text = "未启用";
                return;
            }
            ProcessWatchStatusText.Text = active ? "游戏前台" :
                (IsWatchedProcessRunning() ? "进程后台运行" : "等待进程");
        }

        private void RefreshMonitorCapabilities()
        {
            if (string.IsNullOrWhiteSpace(displayDevice))
            {
                monitorCapabilities = new MonitorCapabilities();
            }
            else
            {
                monitorCapabilities = ddcController.Probe(displayDevice);
            }

            updatingHardwareControls = true;
            try
            {
                bool supported = monitorCapabilities.BrightnessSupported ||
                    monitorCapabilities.ContrastSupported;
                MonitorHardwarePanel.Opacity = supported ? 1.0 : 0.48;
                MonitorHardwarePanel.ToolTip = supported ? null :
                    "硬件无法使用：当前显示器未提供 DDC/CI 亮度或对比度控制。";
                MonitorHardwareDetail.Text = supported ? monitorCapabilities.Detail :
                    "硬件无法使用：" + monitorCapabilities.Detail;
                ConfigureMonitorSlider(MonitorBrightnessSlider, MonitorBrightnessValue,
                    monitorCapabilities.BrightnessSupported,
                    monitorCapabilities.Brightness, monitorCapabilities.BrightnessMaximum);
                ConfigureMonitorSlider(MonitorContrastSlider, MonitorContrastValue,
                    monitorCapabilities.ContrastSupported,
                    monitorCapabilities.Contrast, monitorCapabilities.ContrastMaximum);
            }
            finally
            {
                updatingHardwareControls = false;
            }
        }

        private static void ConfigureMonitorSlider(Slider slider, TextBlock value,
            bool supported, int current, int maximum)
        {
            slider.Maximum = Math.Max(1, maximum);
            slider.IsEnabled = supported;
            slider.Value = Math.Max(0, Math.Min(slider.Maximum, current));
            value.Text = supported ? ((int)Math.Round(slider.Value)).ToString() : "--";
        }

        private void OnMonitorSliderChanged(object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (initializing || updatingHardwareControls || string.IsNullOrWhiteSpace(displayDevice)) return;
            Slider slider = sender as Slider;
            int value = (int)Math.Round(e.NewValue);
            if (slider == MonitorBrightnessSlider)
            {
                MonitorBrightnessValue.Text = value.ToString();
                string error;
                if (!ddcController.SetBrightness(displayDevice, value, out error))
                    ShowNotification(error, NotificationType.Warning);
            }
            else
            {
                MonitorContrastValue.Text = value.ToString();
                string error;
                if (!ddcController.SetContrast(displayDevice, value, out error))
                    ShowNotification(error, NotificationType.Warning);
            }
        }

        private void BindPresetEvents()
        {
            PresetComboBox.SelectionChanged += delegate
            {
                if (initializing || PresetComboBox.SelectedIndex < 0) return;
                int index = PresetComboBox.SelectedIndex;
                if (promotingCustomPreset) return;
                if (index == 5)
                {
                    try
                    {
                        initializing = true;
                        ApplyCustomPresetValues();
                    }
                    finally
                    {
                        initializing = false;
                    }
                    RefreshSliderLabels();
                    SyncSettingsFromSliders();
                    SaveSettings();
                    UpdateCurrentRecommendation();
                    ShowNotification("已加载自定义预设", NotificationType.Success);
                    return;
                }
                PresetValues preset = GetBuiltInPreset(index);
                try
                {
                    initializing = true;
                    ApplyPresetValues(preset);
                }
                finally
                {
                    initializing = false;
                }
                SyncSettingsFromSliders();
                SaveSettings();
                UpdateCurrentRecommendation();
                ShowNotification("预设已加载", NotificationType.Success);
            };
        }

        private void ApplyCustomPresetValues()
        {
            ShadowSlider.Value = settings.CustomShadowTarget;
            HighlightSlider.Value = settings.CustomHighlightProtection;
            ColorSlider.Value = settings.CustomColorCorrection;
            IndoorSlider.Value = settings.CustomIndoorComfort;
            GuardSlider.Value = settings.CustomSceneGuard;
            BlackSlider.Value = settings.CustomBlackPoint;
            StrengthSlider.Value = settings.CustomMaxStrength;
            ExposureSlider.Value = settings.CustomExposureBias;
            ContrastSlider.Value = settings.CustomContrastBias;
            WarmthSlider.Value = settings.CustomWarmth;
            SaturationSlider.Value = settings.CustomSaturationBias;
        }

        private static PresetValues GetBuiltInPreset(int index)
        {
            switch (index)
            {
                case 1:
                    // Current Tarkov samples contain dark red and muted green
                    // lighting. Keep this preset neutral instead of removing
                    // too much of the scene's own color cast.
                    return new PresetValues(55, 86, 52, 76, 91, 50, 64, -1, -2, -1, -1);
                case 2:
                    // More shadow reach for indoor corridors, with a softer
                    // contrast and neutral color balance than the old preset.
                    return new PresetValues(74, 90, 78, 96, 92, 54, 74, 2, 1, 0, 1);
                case 3:
                    // Preserve the game's green night lighting while keeping
                    // highlights and black levels restrained.
                    return new PresetValues(58, 97, 58, 90, 100, 44, 64, 1, 2, -2, 0);
                case 4:
                    // Lift shadowed buildings without overexposing the sky.
                    return new PresetValues(62, 100, 68, 72, 100, 52, 70, -2, -1, -1, -2);
                default:
                    return new PresetValues(70, 76, 72, 72, 88, 56, 82, 0, 0, 0, 0);
            }
        }

        private void ApplyPresetValues(PresetValues preset)
        {
            ShadowSlider.Value = preset.Shadow;
            HighlightSlider.Value = preset.Highlight;
            ColorSlider.Value = preset.Color;
            IndoorSlider.Value = preset.Indoor;
            GuardSlider.Value = preset.Guard;
            BlackSlider.Value = preset.Black;
            StrengthSlider.Value = preset.Strength;
            ExposureSlider.Value = preset.Exposure;
            ContrastSlider.Value = preset.Contrast;
            WarmthSlider.Value = preset.Warmth;
            SaturationSlider.Value = preset.Saturation;
        }

        private void RestoreDefaultFilterValues()
        {
            int selectedIndex = PresetComboBox.SelectedIndex;
            // Custom preset has no separate built-in table; its factory baseline
            // is the same neutral starting point as the automatic preset.
            PresetValues defaults = GetBuiltInPreset(
                selectedIndex == 5 ? 0 : selectedIndex);
            try
            {
                initializing = true;
                ApplyPresetValues(defaults);
                SmoothTransitionCheckBox.IsChecked =
                    AppSettings.CreateDefault().SmoothTransition;
            }
            finally
            {
                initializing = false;
            }

            SyncSettingsFromSliders();
            RefreshSliderLabels();
            UpdateCurrentRecommendation();
            SaveSettings();
            UpdateModeUi();
            ShowNotification(selectedIndex == 5 ?
                "已恢复自定义预设初始数值" : "已恢复所选预设默认数值",
                NotificationType.Success);
        }

        private void AnalyzeLatest(bool applyWhenReady)
        {
            string latest = screenshotWatcher.FindLatest();
            if (string.IsNullOrWhiteSpace(latest) &&
                !string.IsNullOrWhiteSpace(lastAnalyzedPath) &&
                File.Exists(lastAnalyzedPath)) latest = lastAnalyzedPath;
            if (string.IsNullOrWhiteSpace(latest))
            {
                ShowNotification("没有找到可分析的截图", NotificationType.Warning);
                return;
            }
            AnalyzePath(latest, applyWhenReady);
        }

        private void OnScreenshotReady(string filePath)
        {
            if (closing) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!closing && settings.AutoWatch) AnalyzePath(filePath, true);
                }));
            }
            catch { }
        }

        private void OnWatcherFaulted(string message)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!closing) ShowNotification("监听错误：" + message,
                        NotificationType.Warning);
                }));
            }
            catch { }
        }

        private void AnalyzePath(string filePath, bool applyWhenReady)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                ShowNotification("截图文件不存在", NotificationType.Warning);
                return;
            }

            int version = Interlocked.Increment(ref analysisVersion);
            ApplySettingsToModel();
            AppSettings snapshot = CreateSettingsSnapshot();
            AnalyzeButton.IsEnabled = false;
            ShowNotification("正在分析截图…", NotificationType.Info);

            Task.Factory.StartNew(delegate
            {
                var package = new AnalysisPackage();
                package.Analysis = ImageAnalyzer.Analyze(filePath, snapshot);
                using (DrawingBitmap source = ImageAnalyzer.LoadStableBitmap(filePath))
                {
                    package.Original = ImageAnalyzer.BuildOriginalPreview(source);
                    if (package.Analysis.IsUsable)
                        package.Filtered = ImageAnalyzer.BuildPreview(
                            source, package.Analysis.Recommendation);
                }
                return package;
            }).ContinueWith(delegate(Task<AnalysisPackage> task)
            {
                if (closing || Dispatcher.HasShutdownStarted)
                {
                    DisposeTaskResult(task);
                    return;
                }
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        CompleteAnalysis(task, version, applyWhenReady);
                    }));
                }
                catch
                {
                    DisposeTaskResult(task);
                }
            });
        }

        private void CompleteAnalysis(
            Task<AnalysisPackage> task, int version, bool applyWhenReady)
        {
            AnalyzeButton.IsEnabled = true;
            if (closing || version != analysisVersion)
            {
                DisposeTaskResult(task);
                return;
            }

            if (task.IsFaulted)
            {
                Exception error = task.Exception == null ? null :
                    task.Exception.GetBaseException();
                ShowNotification("分析失败：" +
                    (error == null ? "UNKNOWN_ERROR" : error.Message),
                    NotificationType.Error);
                return;
            }

            AnalysisPackage package = task.Result;
            currentAnalysis = package.Analysis;
            lastAnalyzedPath = currentAnalysis.FilePath;
            previewControl.SetContent(package.Original, package.Filtered, currentAnalysis);
            package.ReleaseOwnership();
            PreviewEmptyState.Visibility = Visibility.Collapsed;
            UpdateAnalysisMetrics(currentAnalysis);

            if (!currentAnalysis.IsUsable)
            {
                ShowNotification("截图已跳过：" + currentAnalysis.SkipReason,
                    NotificationType.Warning);
                return;
            }

            ShowNotification("分析完成：" +
                currentAnalysis.Recommendation.ProfileName, NotificationType.Success);
            if (applyWhenReady)
            {
                // A new watched screenshot is an explicit request to refresh
                // and re-enable the filter, even after a manual hotkey disable.
                filterModeEnabled = true;
                ApplyRecommendation(currentAnalysis);
            }
        }

        private static void DisposeTaskResult(Task<AnalysisPackage> task)
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
                task.Result.Dispose();
        }

        private void RefreshCurrentPreview()
        {
            if (currentAnalysis == null || !File.Exists(currentAnalysis.FilePath)) return;
            AnalysisResult analysis = currentAnalysis;
            analysis.Recommendation = ToneCurve.Recommend(
                analysis, CreateSettingsSnapshot());
            UpdateAnalysisMetrics(analysis);

            int version = Interlocked.Increment(ref previewVersion);
            FilterRecommendation recommendation = analysis.Recommendation;
            string path = analysis.FilePath;
            Task.Factory.StartNew(delegate
            {
                var package = new AnalysisPackage { Analysis = analysis };
                using (DrawingBitmap source = ImageAnalyzer.LoadStableBitmap(path))
                {
                    package.Original = ImageAnalyzer.BuildOriginalPreview(source);
                    package.Filtered = ImageAnalyzer.BuildPreview(source, recommendation);
                }
                return package;
            }).ContinueWith(delegate(Task<AnalysisPackage> task)
            {
                if (closing || Dispatcher.HasShutdownStarted)
                {
                    DisposeTaskResult(task);
                    return;
                }
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (closing || version != previewVersion)
                        {
                            DisposeTaskResult(task);
                            return;
                        }
                        if (task.IsFaulted)
                        {
                            DisposeTaskResult(task);
                            return;
                        }
                        AnalysisPackage package = task.Result;
                        previewControl.SetContent(package.Original, package.Filtered, package.Analysis);
                        package.ReleaseOwnership();
                    }));
                }
                catch
                {
                    DisposeTaskResult(task);
                }
            });
        }

        private void UpdateCurrentRecommendation()
        {
            if (currentAnalysis == null || !currentAnalysis.IsUsable) return;
            currentAnalysis.Recommendation = ToneCurve.Recommend(
                currentAnalysis, CreateSettingsSnapshot());
            UpdateAnalysisMetrics(currentAnalysis);
            previewRefreshTimer.Stop();
            previewRefreshTimer.Start();
        }

        private void ApplyRecommendation(AnalysisResult analysis)
        {
            if (analysis == null || analysis.Recommendation == null) return;
            if (settings.ProcessWatchEnabled && !IsWatchedProcessActive())
            {
                autoPausedByProcess = true;
                filterModeEnabled = true;
                UpdateModeUi();
                return;
            }
            FilterRecommendation recommendation = analysis.Recommendation;
            if (recommendation.StrengthBlend <= 0.0001)
            {
                RestoreOriginal(false);
                filterModeEnabled = true;
                UpdateModeUi();
                ShowNotification("最大调整强度为 0，已保持原画", NotificationType.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(displayDevice))
            {
                ShowNotification("没有可用的显示器", NotificationType.Error);
                return;
            }

            string error;
            if (!gammaController.CaptureBaseline(displayDevice, out error))
            {
                ShowNotification("读取显示器原始曲线失败：" + error,
                    NotificationType.Error);
                return;
            }
            GammaRamp? baseline = gammaController.GetBaseline(displayDevice);
            if (baseline.HasValue) RecoveryStore.Save(displayDevice, baseline.Value);

            int duration = settings.SmoothTransition ?
                280 + (int)Math.Round(recommendation.ChangeStrength * 120.0) : 0;
            if (!gammaController.TransitionTo(displayDevice, recommendation,
                duration, out error))
            {
                ShowNotification("应用滤镜失败：" + error,
                    NotificationType.Error);
                return;
            }

            ObsFilterStateStore.WriteActive(recommendation, duration);
            filterModeEnabled = true;
            UpdateModeUi();
            ShowNotification("已应用：" + recommendation.ProfileName,
                NotificationType.Success);
        }

        private void ToggleFilter()
        {
            if (IsFilterRunning())
            {
                string error;
                if (!gammaController.RestoreAll(out error))
                {
                    ShowNotification("恢复原画失败：" + error, NotificationType.Error);
                    return;
                }
                RecoveryStore.Clear();
                filterModeEnabled = false;
                ObsFilterStateStore.WriteDisabled();
                UpdateModeUi();
                ShowNotification("滤镜已关闭", NotificationType.Info);
                return;
            }

            if (settings.ProcessWatchEnabled && !IsWatchedProcessActive())
            {
                autoPausedByProcess = true;
                filterModeEnabled = true;
                ObsFilterStateStore.WriteDisabled();
                UpdateModeUi();
                ShowNotification("未检测到侦听进程，滤镜保持关闭", NotificationType.Info);
                return;
            }

            filterModeEnabled = true;
            if (currentAnalysis != null && currentAnalysis.IsUsable)
                ApplyRecommendation(currentAnalysis);
            else
            {
                UpdateModeUi();
                ShowNotification("滤镜已开启，等待分析截图", NotificationType.Info);
            }
        }

        private void RestoreOriginal(bool showNotification)
        {
            string error;
            if (gammaController.RestoreAll(out error))
            {
                RecoveryStore.Clear();
                filterModeEnabled = false;
                ObsFilterStateStore.WriteDisabled();
                UpdateModeUi();
                if (showNotification) ShowNotification("已恢复原始曲线",
                    NotificationType.Success);
            }
            else if (showNotification)
            {
                ShowNotification("恢复原画失败：" + error, NotificationType.Error);
            }
        }

        private void UpdateModeUi()
        {
            string[] modes = { "自动分析", "自然中性", "室内柔和", "夜视护眼", "高光保护", "自定义预设" };
            int index = PresetComboBox == null ? 0 : PresetComboBox.SelectedIndex;
            if (index < 0 || index >= modes.Length) index = 0;
            ModeText.Text = modes[index];
            bool running = IsFilterRunning();
            RunStateText.Text = running ? "运行中" : "未运行";
            RunStateText.Foreground = running ?
                (System.Windows.Media.Brush)FindResource("AmberAlertBrush") :
                (System.Windows.Media.Brush)FindResource("PhosphorDimBrush");
            ToggleButton.Content = running ?
                "关闭滤镜  " + FormatHotkey(settings.HotkeyKeyCode, settings.HotkeyModifiers) :
                "开启滤镜  " + FormatHotkey(settings.HotkeyKeyCode, settings.HotkeyModifiers);
            ToggleButton.Style = (Style)FindResource(running ?
                "TacticalButtonActive" : "TacticalButtonPrimary");
            StatusDot.Fill = running ?
                (System.Windows.Media.Brush)FindResource("TerminalGreenBrush") :
                (System.Windows.Media.Brush)FindResource("PhosphorDimBrush");
            if (trayIcon != null)
                trayIcon.Text = "TarkovAutoShade - " +
                    (running ? "FILTER ON" : "FILTER OFF");
        }

        private bool IsFilterRunning()
        {
            return filterModeEnabled && gammaController.HasActiveFilter;
        }

        private void UpdateWatcherUi()
        {
            bool active = screenshotWatcher.IsActive;
            StatusDot.ToolTip = IsFilterRunning() ? "滤镜正在运行；截图监听：" +
                (active ? "已启用" : "未启用") : "滤镜未运行；截图监听：" +
                (active ? "已启用" : "未启用");
            bool folderReady = Directory.Exists(settings.ScreenshotFolder);
            FolderPath.Foreground = folderReady ?
                (System.Windows.Media.Brush)FindResource("AmberAlertBrush") :
                (System.Windows.Media.Brush)FindResource("HazardRedBrush");
            BrowseFolderButton.BorderBrush = folderReady ?
                (System.Windows.Media.Brush)FindResource("PhosphorDimBrush") :
                (System.Windows.Media.Brush)FindResource("HazardRedBrush");
            BrowseFolderButton.Foreground = folderReady ?
                (System.Windows.Media.Brush)FindResource("PhosphorWhiteBrush") :
                (System.Windows.Media.Brush)FindResource("HazardRedBrush");
            UpdateModeUi();
        }

        private void UpdateAnalysisMetrics(AnalysisResult result)
        {
            if (result == null)
            {
                P10Value.Text = MedianValue.Text = P95Value.Text = "--";
                DynamicRangeValue.Text = "--";
                return;
            }
            P10Value.Text = FormatByteValue(result.P10);
            MedianValue.Text = FormatByteValue(result.Median);
            P95Value.Text = FormatByteValue(result.P95);
            DynamicRangeValue.Text = FormatByteValue(result.DynamicRange);
            UpdateMetrics();
        }

        private void UpdateMetrics()
        {
            if (currentAnalysis == null || currentAnalysis.Recommendation == null)
            {
                GammaMetric.Text = "--";
                BrightnessMetric.Text = "--";
                ContrastMetric.Text = "--";
                return;
            }
            FilterRecommendation recommendation = currentAnalysis.Recommendation;
            GammaMetric.Text = recommendation.EquivalentGamma.ToString("0.00");
            BrightnessMetric.Text = "+" + Math.Round(
                recommendation.BrightnessBoost).ToString("0");
            ContrastMetric.Text = "+" + Math.Round(
                recommendation.ContrastBoost).ToString("0");
        }

        private void ApplySettingsToSliders()
        {
            SmoothTransitionCheckBox.IsChecked = settings.SmoothTransition;
            PresetComboBox.SelectedIndex = Math.Max(0, Math.Min(
                PresetComboBox.Items.Count - 1, settings.PresetIndex));
            ShadowSlider.Value = settings.ShadowTarget;
            HighlightSlider.Value = settings.HighlightProtection;
            ColorSlider.Value = settings.ColorCorrection;
            IndoorSlider.Value = settings.IndoorComfort;
            GuardSlider.Value = settings.SceneGuard;
            BlackSlider.Value = settings.BlackPoint;
            StrengthSlider.Value = settings.MaxStrength;
            ExposureSlider.Value = settings.ExposureBias;
            ContrastSlider.Value = settings.ContrastBias;
            WarmthSlider.Value = settings.Warmth;
            SaturationSlider.Value = settings.SaturationBias;
            UpdateHotkeyUi();
        }

        private void RefreshSliderLabels()
        {
            ShadowValue.Text = ((int)ShadowSlider.Value).ToString();
            HighlightValue.Text = ((int)HighlightSlider.Value).ToString();
            ColorValue.Text = ((int)ColorSlider.Value).ToString();
            IndoorValue.Text = ((int)IndoorSlider.Value).ToString();
            GuardValue.Text = ((int)GuardSlider.Value).ToString();
            BlackValue.Text = ((int)BlackSlider.Value).ToString();
            StrengthValue.Text = ((int)StrengthSlider.Value).ToString();
            ExposureValue.Text = FormatSigned((int)ExposureSlider.Value);
            ContrastValue.Text = FormatSigned((int)ContrastSlider.Value);
            WarmthValue.Text = FormatSigned((int)WarmthSlider.Value);
            SaturationValue.Text = FormatSigned((int)SaturationSlider.Value);
        }

        private void ApplySettingsToModel()
        {
            settings.ShadowTarget = (int)ShadowSlider.Value;
            settings.HighlightProtection = (int)HighlightSlider.Value;
            settings.ColorCorrection = (int)ColorSlider.Value;
            settings.IndoorComfort = (int)IndoorSlider.Value;
            settings.SceneGuard = (int)GuardSlider.Value;
            settings.BlackPoint = (int)BlackSlider.Value;
            settings.MaxStrength = (int)StrengthSlider.Value;
            settings.ExposureBias = (int)ExposureSlider.Value;
            settings.ContrastBias = (int)ContrastSlider.Value;
            settings.Warmth = (int)WarmthSlider.Value;
            settings.SaturationBias = (int)SaturationSlider.Value;
            settings.SmoothTransition = SmoothTransitionCheckBox.IsChecked == true;
        }

        private void SyncSettingsFromSliders()
        {
            ApplySettingsToModel();
            if (PresetComboBox.SelectedIndex == 5)
            {
                settings.CustomPresetInitialized = true;
                settings.CustomShadowTarget = settings.ShadowTarget;
                settings.CustomHighlightProtection = settings.HighlightProtection;
                settings.CustomExposureBias = settings.ExposureBias;
                settings.CustomContrastBias = settings.ContrastBias;
                settings.CustomMaxStrength = settings.MaxStrength;
                settings.CustomWarmth = settings.Warmth;
                settings.CustomColorCorrection = settings.ColorCorrection;
                settings.CustomIndoorComfort = settings.IndoorComfort;
                settings.CustomSceneGuard = settings.SceneGuard;
                settings.CustomBlackPoint = settings.BlackPoint;
                settings.CustomSaturationBias = settings.SaturationBias;
            }
            settings.PresetIndex = Math.Max(0, PresetComboBox.SelectedIndex);
        }

        private void SaveSettings()
        {
            if (closing) return;
            SyncSettingsFromSliders();
            settings.ScreenshotFolder = FolderPath.Text == "未找到截图目录" ?
                settings.ScreenshotFolder : FolderPath.Text;
            SettingsStore.Save(settings);
        }

        private AppSettings CreateSettingsSnapshot()
        {
            ApplySettingsToModel();
            var snapshot = AppSettings.CreateDefault();
            snapshot.AlgorithmVersion = settings.AlgorithmVersion;
            snapshot.ShadowTarget = settings.ShadowTarget;
            snapshot.HighlightProtection = settings.HighlightProtection;
            snapshot.ExposureBias = settings.ExposureBias;
            snapshot.ContrastBias = settings.ContrastBias;
            snapshot.MaxStrength = settings.MaxStrength;
            snapshot.Warmth = settings.Warmth;
            snapshot.ColorCorrection = settings.ColorCorrection;
            snapshot.IndoorComfort = settings.IndoorComfort;
            snapshot.SceneGuard = settings.SceneGuard;
            snapshot.BlackPoint = settings.BlackPoint;
            snapshot.SaturationBias = settings.SaturationBias;
            snapshot.Normalize();
            return snapshot;
        }

        private void RecoverPreviousSession()
        {
            string deviceName;
            GammaRamp ramp;
            if (!RecoveryStore.TryLoad(out deviceName, out ramp)) return;
            string error;
            if (gammaController.ApplyDirect(deviceName, ramp, out error))
            {
                RecoveryStore.Clear();
                ShowNotification("已恢复上次异常退出前的画面", NotificationType.Success);
            }
            else
            {
                ShowNotification("异常退出恢复失败：" + error, NotificationType.Warning);
            }
        }

        private void ShowNotification(string message, NotificationType type)
        {
            string prefix = type == NotificationType.Success ? "完成" :
                type == NotificationType.Error ? "错误" :
                type == NotificationType.Warning ? "提醒" : "信息";
            Brush accent = type == NotificationType.Error ?
                (Brush)FindResource("HazardRedBrush") : type == NotificationType.Warning ?
                (Brush)FindResource("AmberAlertBrush") : type == NotificationType.Success ?
                (Brush)FindResource("TerminalGreenBrush") :
                (Brush)FindResource("PhosphorDimBrush");
            ActionMessageTypeText.Text = prefix;
            ActionMessageText.Text = message;
            ActionMessageTypeText.Foreground = accent;
            ActionMessageText.Foreground = (Brush)FindResource("PhosphorWhiteBrush");
            ActionMessageDot.Fill = accent;
            ActionMessagePanel.BorderBrush = accent;
            ActionMessagePanel.Opacity = 1.0;
            ActionMessagePanel.IsHitTestVisible = true;
            if (notificationTimer != null) notificationTimer.Stop();
            notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            notificationTimer.Tick += delegate
            {
                notificationTimer.Stop();
                ActionMessageText.Text = string.Empty;
                ActionMessageTypeText.Text = string.Empty;
                ActionMessagePanel.Opacity = 0.0;
                ActionMessagePanel.IsHitTestVisible = false;
            };
            notificationTimer.Start();
        }

        private void ShowAboutWindow()
        {
            var about = new Window
            {
                Title = "关于 TarkovAutoShade",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 460,
                Height = 330,
                MinWidth = 460,
                MinHeight = 330,
                MaxWidth = 460,
                MaxHeight = 330,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("CrtSurfaceBrush"),
                Foreground = (Brush)FindResource("PhosphorWhiteBrush")
            };

            var content = new Grid
            {
                Margin = new Thickness(24, 24, 24, 12)
            };
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            content.RowDefinitions.Add(new RowDefinition {
                Height = new GridLength(1, GridUnitType.Star),
                MinHeight = 38
            });

            var title = new TextBlock
            {
                Text = "TarkovAutoShade",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontDisplay"),
                FontSize = 24,
                FontWeight = FontWeights.Black,
                Foreground = (Brush)FindResource("PhosphorWhiteBrush")
            };
            Grid.SetRow(title, 0);
            content.Children.Add(title);

            var details = new TextBlock
            {
                Text = "版本：1.0.0\n作者：lub大萝卜\n免费分享，禁止倒卖",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"),
                FontSize = 12,
                Foreground = (Brush)FindResource("PhosphorDimBrush"),
                LineHeight = 20
            };
            Grid.SetRow(details, 1);
            content.Children.Add(details);

            var bilibili = new TextBlock
            {
                Text = "B站主页：lub大萝卜",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TerminalGreenBrush"),
                TextDecorations = TextDecorations.Underline,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            bilibili.MouseLeftButtonUp += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://space.bilibili.com/66741964",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            Grid.SetRow(bilibili, 2);
            content.Children.Add(bilibili);

            var afdian = new TextBlock
            {
                Text = "爱发电主页：lub大萝卜",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TerminalGreenBrush"),
                TextDecorations = TextDecorations.Underline,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            afdian.MouseLeftButtonUp += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://afdian.com/a/lublub",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            Grid.SetRow(afdian, 3);
            content.Children.Add(afdian);

            var github = new TextBlock
            {
                Text = "GitHub仓库：TarkovAutoShade",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TerminalGreenBrush"),
                TextDecorations = TextDecorations.Underline,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            github.MouseLeftButtonUp += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/lu1249765686/TarkovAutoShade",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            Grid.SetRow(github, 4);
            content.Children.Add(github);

            var notice = new TextBlock
            {
                Text = "本工具仅调整显示器 Gamma / DDC/CI，不读取游戏进程。",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"),
                FontSize = 11,
                Foreground = (Brush)FindResource("PhosphorDimBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(notice, 5);
            content.Children.Add(notice);

            var close = new Button
            {
                Content = "关闭",
                Width = 72,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1),
                Style = (Style)FindResource("TacticalButton")
            };
            close.Click += delegate { about.Close(); };
            Grid.SetRow(close, 6);
            content.Children.Add(close);

            about.Content = content;
            about.ShowDialog();
        }

        private static string FormatByteValue(double value)
        {
            return Math.Round(Math.Max(0.0, Math.Min(1.0, value)) * 255.0).ToString("0");
        }

        private static string FormatSigned(int value)
        {
            return value >= 0 ? "+" + value.ToString() : value.ToString();
        }

        private enum NotificationType
        {
            Info,
            Success,
            Warning,
            Error
        }

        private struct PresetValues
        {
            public readonly int Shadow;
            public readonly int Highlight;
            public readonly int Color;
            public readonly int Indoor;
            public readonly int Guard;
            public readonly int Black;
            public readonly int Strength;
            public readonly int Exposure;
            public readonly int Contrast;
            public readonly int Warmth;
            public readonly int Saturation;

            public PresetValues(int shadow, int highlight, int color, int indoor,
                int guard, int black, int strength, int exposure, int contrast,
                int warmth, int saturation)
            {
                Shadow = shadow;
                Highlight = highlight;
                Color = color;
                Indoor = indoor;
                Guard = guard;
                Black = black;
                Strength = strength;
                Exposure = exposure;
                Contrast = contrast;
                Warmth = warmth;
                Saturation = saturation;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            bool exitRequested = trayExitRequested;
            trayExitRequested = false;

            if (!closing && !exitRequested)
            {
                if (settings.CloseBehavior == 1)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }

                if (settings.CloseBehavior == 0)
                {
                    bool remember;
                    bool toTray;
                    if (!ShowCloseChoice(out remember, out toTray))
                    {
                        if (trayExitRequested)
                        {
                            trayExitRequested = false;
                            exitRequested = true;
                        }
                        else
                        {
                            e.Cancel = true;
                            return;
                        }
                    }
                    if (!exitRequested)
                    {
                        if (remember)
                        {
                            settings.CloseBehavior = toTray ? 1 : 2;
                            SaveSettings();
                        }
                        if (toTray)
                        {
                            e.Cancel = true;
                            Hide();
                            return;
                        }
                    }
                }
            }
            closing = true;
            base.OnClosing(e);
        }

        private bool ShowCloseChoice(out bool remember, out bool toTray)
        {
            remember = false;
            toTray = false;
            int choice = 0;
            var dialog = new Window
            {
                Title = "关闭 TarkovAutoShade",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 456,
                Height = 224,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("CrtSurfaceBrush"),
                Foreground = (Brush)FindResource("PhosphorWhiteBrush")
            };
            var root = new Grid { Margin = new Thickness(26, 18, 26, 12) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var heading = new TextBlock
            {
                Text = "关闭窗口",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontUi"),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("PhosphorWhiteBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);
            var message = new TextBlock
            {
                Text = "关闭后会恢复原始画面。请选择接下来的处理方式。",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontUi"),
                FontSize = 13,
                Foreground = (Brush)FindResource("PhosphorDimBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(message, 1);
            root.Children.Add(message);
            var rememberBox = new CheckBox
            {
                Content = "记住我的选择",
                Style = (Style)FindResource("TacticalCheckBox")
            };
            var bottom = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(rememberBox, 0);
            bottom.Children.Add(rememberBox);
            var buttons = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            var tray = new Button { Content = "最小化到托盘", Height = 34, Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("TacticalButton") };
            var exit = new Button { Content = "退出程序", Height = 34, Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("TacticalButtonPrimary") };
            var cancel = new Button { Content = "取消", Height = 34, Style = (Style)FindResource("TacticalButton") };
            tray.Click += delegate { choice = 1; dialog.DialogResult = true; };
            exit.Click += delegate { choice = 2; dialog.DialogResult = true; };
            cancel.Click += delegate { dialog.DialogResult = false; };
            buttons.Children.Add(tray);
            buttons.Children.Add(exit);
            buttons.Children.Add(cancel);
            Grid.SetColumn(exit, 1);
            Grid.SetColumn(cancel, 2);
            Grid.SetColumn(buttons, 1);
            bottom.Children.Add(buttons);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);
            dialog.Content = root;
            closeChoiceDialog = dialog;
            try
            {
                if (dialog.ShowDialog() != true || choice == 0) return false;
                remember = rememberBox.IsChecked == true;
                toTray = choice == 1;
                return true;
            }
            finally
            {
                if (ReferenceEquals(closeChoiceDialog, dialog))
                    closeChoiceDialog = null;
            }
        }

        private sealed class AnalysisPackage : IDisposable
        {
            public AnalysisResult Analysis;
            public DrawingBitmap Original;
            public DrawingBitmap Filtered;

            public void ReleaseOwnership()
            {
                Original = null;
                Filtered = null;
            }

            public void Dispose()
            {
                if (Original != null) Original.Dispose();
                if (Filtered != null) Filtered.Dispose();
                Original = null;
                Filtered = null;
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            closing = true;
            Interlocked.Increment(ref analysisVersion);
            Interlocked.Increment(ref previewVersion);
            if (timestampTimer != null) timestampTimer.Stop();
            if (statusDotTimer != null) statusDotTimer.Stop();
            if (settingsTimer != null) settingsTimer.Stop();
            if (previewRefreshTimer != null) previewRefreshTimer.Stop();
            if (processWatchTimer != null) processWatchTimer.Stop();
            if (foregroundWindowEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(foregroundWindowEventHook);
                foregroundWindowEventHook = IntPtr.Zero;
            }
            screenshotWatcher.Dispose();
            toggleHotkey?.Dispose();
            SyncSettingsFromSliders();
            SettingsStore.Save(settings);
            string ignored;
            gammaController.RestoreAll(out ignored);
            ObsFilterStateStore.WriteDisabled();
            RecoveryStore.Clear();
            gammaController.Dispose();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (trayMenu != null) trayMenu.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private const uint EventSystemForeground = 0x0003;
        private const uint WineventOutOfContext = 0x0000;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WinEventDelegate(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThreadId,
            uint eventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr moduleHandle,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle, out uint processId);
    }
}
