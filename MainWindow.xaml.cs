using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MiniRec.Native;
using MiniRec.Services;
using Wpf = System.Windows;
using Media = System.Windows.Media;

namespace MiniRec;

public partial class MainWindow : Wpf.Window
{
    private const int HotkeyRecordId = 1;
    private const int HotkeyToggleUiId = 2;

    private readonly ConfigStore _configStore = new();
    private readonly ScreenRecorder _recorder = new();
    private readonly TrayService _tray = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };

    private AppConfig _config;
    private DateTime _recordStartUtc;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private bool _isConfiguringHotkeys;
    private bool _isBusy;
    private bool _allowRealClose;
    private string? _lastSavedFilePath;
    private CaptureRegion? _selectedRegion;
    private RegionGuideWindow? _guideWindow;
    private Storyboard? _pulseStoryboard;

    public MainWindow()
    {
        InitializeComponent();

        _config = _configStore.Load();
        UpdateRegionButton();
        UpdateFpsButton();
        UpdateAudioButton();
        UpdateTooltipsWithHotkeys();

        _clockTimer.Tick += (_, _) =>
        {
            if (_recorder.IsRecording)
            {
                TimeSpan elapsed = DateTime.UtcNow - _recordStartUtc;
                TimerText.Text = elapsed.TotalHours >= 1
                    ? elapsed.ToString(@"hh\:mm\:ss")
                    : elapsed.ToString(@"mm\:ss");
            }
        };

        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            StatusBanner.Visibility = Wpf.Visibility.Collapsed;
        };

        _recorder.UnexpectedStopped += msg =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _clockTimer.Stop();
                CloseGuideWindow();
                SetRecordingVisualState(false);
                ShowBanner(msg, isError: true, persistent: true);
            });
        };

        _tray.RecordClicked += () => Dispatcher.BeginInvoke(async () => await ToggleRecordingAsync());
        _tray.SelectRegionClicked += () => Dispatcher.BeginInvoke(SelectRegion);
        _tray.ClearRegionClicked += () => Dispatcher.BeginInvoke(ClearRegion);
        _tray.FpsSelected += fps => Dispatcher.BeginInvoke(() => SetFps(fps));
        _tray.AudioModeSelected += mode => Dispatcher.BeginInvoke(() => SetAudioMode(mode));
        _tray.ConfigureHotkeysClicked += () => Dispatcher.BeginInvoke(OpenHotkeySettings);
        _tray.ToggleUiClicked += () => Dispatcher.BeginInvoke(ToggleVisibility);
        _tray.OpenFolderClicked += () => Dispatcher.BeginInvoke(OpenOutputFolder);
        _tray.ExitClicked += () => Dispatcher.BeginInvoke(RequestExit);

        Loaded += (_, _) => PositionTopRight();
        PositionTopRight();
    }

    private void PositionTopRight()
    {
        Wpf.Rect area = Wpf.SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 320;
        Left = area.Right - w - 16;
        Top = area.Top + 18;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.TryEnableRoundedCorners(_hwnd);

        bool excluded = Win32Interop.ExcludeFromCapture(_hwnd);
        if (!excluded)
        {
            ShowBanner("当前系统不支持录屏隐身 (WDA_EXCLUDEFROMCAPTURE)", isError: true, persistent: true);
        }

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        RegisterAllHotkeys(showFeedbackOnError: true);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Interop.WM_HOTKEY && !_isConfiguringHotkeys)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyRecordId)
            {
                handled = true;
                _ = ToggleRecordingAsync();
            }
            else if (id == HotkeyToggleUiId)
            {
                handled = true;
                ToggleVisibility();
            }
        }

        return IntPtr.Zero;
    }

    private void RegisterAllHotkeys(bool showFeedbackOnError)
    {
        if (_hwnd == IntPtr.Zero) return;

        Win32Interop.UnregisterHotKey(_hwnd, HotkeyRecordId);
        Win32Interop.UnregisterHotKey(_hwnd, HotkeyToggleUiId);

        bool recOk = Win32Interop.RegisterHotKey(
            _hwnd,
            HotkeyRecordId,
            _config.RecordModifiers | Win32Interop.MOD_NOREPEAT,
            _config.RecordVirtualKey);

        bool uiOk = Win32Interop.RegisterHotKey(
            _hwnd,
            HotkeyToggleUiId,
            _config.ToggleUiModifiers | Win32Interop.MOD_NOREPEAT,
            _config.ToggleUiVirtualKey);

        if (showFeedbackOnError)
        {
            if (!recOk)
            {
                ShowBanner($"快捷键 {HotkeyFormatter.Format(_config.RecordModifiers, _config.RecordVirtualKey)} 被占用，请点击按钮修改", isError: true, persistent: true);
            }
            else if (!uiOk)
            {
                ShowBanner($"显隐快捷键 {HotkeyFormatter.Format(_config.ToggleUiModifiers, _config.ToggleUiVirtualKey)} 被占用", isError: true);
            }
        }

        SyncTrayState();
    }

    private async Task ToggleRecordingAsync()
    {
        if (_isBusy) return;
        _isBusy = true;
        RecordButton.IsEnabled = false;

        try
        {
            if (_recorder.IsRecording)
            {
                _clockTimer.Stop();
                CloseGuideWindow();
                RecordButton.Content = "保存中...";
                RecordStopResult result = await _recorder.StopAsync();
                SetRecordingVisualState(false);

                if (result.Success && !string.IsNullOrWhiteSpace(result.FilePath))
                {
                    _lastSavedFilePath = result.FilePath;
                    string name = Path.GetFileName(result.FilePath);
                    ShowBanner($"✓ 已保存 {name}（点击定位文件）", isError: false);
                }
                else
                {
                    ShowBanner(result.Error ?? "停止录屏时出错", isError: true, persistent: true);
                }
            }
            else
            {
                _lastSavedFilePath = null;
                RecordButton.Content = "启动中...";
                RecordStartResult result = await _recorder.StartAsync(
                    _config.OutputDirectory,
                    _config.Fps,
                    _selectedRegion,
                    _config.AudioMode);

                if (result.Success)
                {
                    _recordStartUtc = DateTime.UtcNow;
                    TimerText.Text = "00:00";
                    _clockTimer.Start();
                    SetRecordingVisualState(true);
                    if (_selectedRegion is not null)
                    {
                        _guideWindow = new RegionGuideWindow(_selectedRegion);
                        _guideWindow.Show();
                    }

                    string hotkey = HotkeyFormatter.Format(_config.RecordModifiers, _config.RecordVirtualKey);
                    string bannerText = string.IsNullOrWhiteSpace(result.Warning)
                        ? $"录制中 ({result.Backend} · {_config.Fps}FPS) · 按 {hotkey} 停止"
                        : $"录制中 · {result.Warning}";
                    ShowBanner(bannerText, isError: false, persistent: !string.IsNullOrWhiteSpace(result.Warning));
                }
                else
                {
                    CloseGuideWindow();
                    SetRecordingVisualState(false);
                    ShowBanner(result.Error ?? "启动录屏失败", isError: true, persistent: true);
                }
            }
        }
        finally
        {
            RecordButton.IsEnabled = true;
            _isBusy = false;
        }
    }

    private void SetRecordingVisualState(bool isRecording)
    {
        RegionButton.IsEnabled = !isRecording;
        RegionButton.Opacity = isRecording ? 0.5 : 1.0;
        FpsButton.IsEnabled = !isRecording;
        FpsButton.Opacity = isRecording ? 0.5 : 1.0;
        AudioButton.IsEnabled = !isRecording;
        AudioButton.Opacity = isRecording ? 0.5 : 1.0;

        if (isRecording)
        {
            RecordButton.Content = "■ 停止";
            RecordButton.Background = new Media.SolidColorBrush(Media.Color.FromRgb(220, 38, 38));
            StatusDot.Fill = new Media.SolidColorBrush(Media.Color.FromRgb(239, 68, 68));
            StartDotPulse();
        }
        else
        {
            StopDotPulse();
            RecordButton.Content = "● 录制";
            RecordButton.Background = new Media.SolidColorBrush(Media.Color.FromRgb(239, 68, 68));
            StatusDot.Fill = new Media.SolidColorBrush(Media.Color.FromRgb(100, 116, 139));
            TimerText.Text = "00:00";
        }

        SyncTrayState();
    }

    private void StartDotPulse()
    {
        StopDotPulse();
        var anim = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        _pulseStoryboard = new Storyboard();
        Storyboard.SetTarget(anim, StatusDot);
        Storyboard.SetTargetProperty(anim, new Wpf.PropertyPath("Opacity"));
        _pulseStoryboard.Children.Add(anim);
        _pulseStoryboard.Begin();
    }

    private void StopDotPulse()
    {
        _pulseStoryboard?.Stop();
        _pulseStoryboard = null;
        StatusDot.Opacity = 1.0;
    }

    private void SyncTrayState()
    {
        string recKey = HotkeyFormatter.Format(_config.RecordModifiers, _config.RecordVirtualKey);
        string uiKey = HotkeyFormatter.Format(_config.ToggleUiModifiers, _config.ToggleUiVirtualKey);
        _tray.UpdateState(_recorder.IsRecording, recKey, uiKey, _selectedRegion, _config.Fps, _config.AudioMode);
    }

    private void UpdateFpsButton()
    {
        FpsButton.Content = $"{_config.Fps}fps";
    }

    private void UpdateAudioButton()
    {
        switch (_config.AudioMode)
        {
            case AudioCaptureMode.None:
                AudioButton.Content = "🔇";
                AudioButton.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(148, 163, 184));
                AudioButton.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(51, 65, 85));
                AudioButton.ToolTip = "当前：静音（点击切换录音模式）";
                break;
            case AudioCaptureMode.System:
                AudioButton.Content = "🔊";
                AudioButton.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(52, 211, 153));
                AudioButton.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(16, 185, 129));
                AudioButton.ToolTip = "当前：仅录制系统声音（点击切换）";
                break;
            case AudioCaptureMode.Microphone:
                AudioButton.Content = "🎤";
                AudioButton.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(52, 211, 153));
                AudioButton.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(16, 185, 129));
                AudioButton.ToolTip = "当前：仅录制麦克风（点击切换）";
                break;
            case AudioCaptureMode.Both:
                AudioButton.Content = "🎧";
                AudioButton.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(52, 211, 153));
                AudioButton.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(16, 185, 129));
                AudioButton.ToolTip = "当前：系统声音 + 麦克风混音（点击切换）";
                break;
        }
    }

    private void UpdateTooltipsWithHotkeys()
    {
        string recKey = HotkeyFormatter.Format(_config.RecordModifiers, _config.RecordVirtualKey);
        string uiKey = HotkeyFormatter.Format(_config.ToggleUiModifiers, _config.ToggleUiVirtualKey);
        RecordButton.ToolTip = $"开始/停止录制（快捷键 {recKey}，托盘可修改）";
        HideButton.ToolTip = $"隐藏到系统托盘（快捷键 {uiKey} 恢复）";
    }

    private void SetFps(int fps)
    {
        if (_recorder.IsRecording || _isBusy) return;
        if (!AppConfig.SupportedFps.Contains(fps)) return;

        _config.Fps = fps;
        _configStore.Save(_config);
        UpdateFpsButton();
        SyncTrayState();
        ShowBanner($"已切换帧率为 {fps} FPS", isError: false);
    }

    private void SetAudioMode(AudioCaptureMode mode)
    {
        if (_recorder.IsRecording || _isBusy) return;

        _config.AudioMode = mode;
        _configStore.Save(_config);
        UpdateAudioButton();
        SyncTrayState();

        string label = mode switch
        {
            AudioCaptureMode.None => "静音（不录声音）",
            AudioCaptureMode.System => "仅录制系统声音",
            AudioCaptureMode.Microphone => "仅录制麦克风",
            AudioCaptureMode.Both => "系统声音 + 麦克风混音",
            _ => ""
        };
        ShowBanner($"录音模式：{label}", isError: false);
    }

    private void UpdateRegionButton()
    {
        if (_selectedRegion is null)
        {
            RegionButton.Content = "⛶";
            RegionButton.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(148, 163, 184));
            RegionButton.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(51, 65, 85));
            RegionButton.ToolTip = "左键框选录制区域（当前：全屏）";
        }
        else
        {
            RegionButton.Content = $"⛶ {_selectedRegion.Width}×{_selectedRegion.Height}";
            RegionButton.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(56, 189, 248));
            RegionButton.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(2, 132, 199));
            RegionButton.ToolTip = "左键重新框选 · 右键恢复全屏";
        }
    }

    private void SelectRegion()
    {
        if (_recorder.IsRecording || _isBusy)
        {
            ShowBanner("请先停止录制再更改区域", isError: true);
            return;
        }

        bool wasVisible = IsVisible;
        if (wasVisible)
        {
            Hide();
        }

        List<(int X, int Y, int Width, int Height)> windowRects =
            Win32Interop.EnumerateVisibleWindowRects(Environment.ProcessId);

        var overlay = new RegionOverlayWindow(windowRects);
        bool? dialogResult = overlay.ShowDialog();

        if (wasVisible)
        {
            Show();
            Activate();
        }

        if (dialogResult == true)
        {
            if (overlay.ClearedToFullScreen)
            {
                _selectedRegion = null;
                ShowBanner("已恢复全屏录制", isError: false);
            }
            else if (overlay.SelectedRegion is not null)
            {
                _selectedRegion = overlay.SelectedRegion;
                if (!overlay.StartRecordingImmediately)
                {
                    ShowBanner($"已选定区域 {_selectedRegion.Width}×{_selectedRegion.Height}（右键选区按钮可恢复全屏）", isError: false);
                }
            }

            UpdateRegionButton();
            SyncTrayState();

            if (overlay.StartRecordingImmediately && _selectedRegion is not null)
            {
                _ = ToggleRecordingAsync();
            }
        }
    }

    private void ClearRegion()
    {
        if (_recorder.IsRecording || _isBusy || _selectedRegion is null)
        {
            return;
        }

        _selectedRegion = null;
        UpdateRegionButton();
        SyncTrayState();
        ShowBanner("已恢复全屏录制", isError: false);
    }

    private void CloseGuideWindow()
    {
        if (_guideWindow is not null)
        {
            try
            {
                _guideWindow.Close();
            }
            catch
            {
                // Ignore if already closed.
            }
            _guideWindow = null;
        }
    }

    private void OpenHotkeySettings()
    {
        if (_recorder.IsRecording || _isBusy)
        {
            Show();
            ShowBanner("请先停止录制再修改快捷键", isError: true);
            return;
        }

        if (_isConfiguringHotkeys) return;
        _isConfiguringHotkeys = true;

        try
        {
            var dlg = new HotkeySettingsWindow(
                _config.RecordModifiers,
                _config.RecordVirtualKey,
                _config.ToggleUiModifiers,
                _config.ToggleUiVirtualKey,
                TryApplyHotkeys);

            if (dlg.ShowDialog() == true)
            {
                _config.RecordModifiers = dlg.RecordModifiers;
                _config.RecordVirtualKey = dlg.RecordVirtualKey;
                _config.ToggleUiModifiers = dlg.ToggleUiModifiers;
                _config.ToggleUiVirtualKey = dlg.ToggleUiVirtualKey;
                _configStore.Save(_config);

                UpdateTooltipsWithHotkeys();
                SyncTrayState();

                if (IsVisible)
                {
                    ShowBanner(
                        $"✓ 快捷键已更新（录制 {HotkeyFormatter.Format(_config.RecordModifiers, _config.RecordVirtualKey)}）",
                        isError: false);
                }
            }
        }
        finally
        {
            _isConfiguringHotkeys = false;
        }
    }

    private string? TryApplyHotkeys(uint recMods, uint recVk, uint uiMods, uint uiVk)
    {
        if (_hwnd == IntPtr.Zero) return "窗口尚未就绪";

        uint oldRecMods = _config.RecordModifiers;
        uint oldRecVk = _config.RecordVirtualKey;
        uint oldUiMods = _config.ToggleUiModifiers;
        uint oldUiVk = _config.ToggleUiVirtualKey;

        Win32Interop.UnregisterHotKey(_hwnd, HotkeyRecordId);
        Win32Interop.UnregisterHotKey(_hwnd, HotkeyToggleUiId);

        bool recOk = Win32Interop.RegisterHotKey(_hwnd, HotkeyRecordId, recMods | Win32Interop.MOD_NOREPEAT, recVk);
        if (!recOk)
        {
            Win32Interop.RegisterHotKey(_hwnd, HotkeyRecordId, oldRecMods | Win32Interop.MOD_NOREPEAT, oldRecVk);
            Win32Interop.RegisterHotKey(_hwnd, HotkeyToggleUiId, oldUiMods | Win32Interop.MOD_NOREPEAT, oldUiVk);
            return $"录制快捷键 {HotkeyFormatter.Format(recMods, recVk)} 已被其他程序占用";
        }

        bool uiOk = Win32Interop.RegisterHotKey(_hwnd, HotkeyToggleUiId, uiMods | Win32Interop.MOD_NOREPEAT, uiVk);
        if (!uiOk)
        {
            Win32Interop.UnregisterHotKey(_hwnd, HotkeyRecordId);
            Win32Interop.RegisterHotKey(_hwnd, HotkeyRecordId, oldRecMods | Win32Interop.MOD_NOREPEAT, oldRecVk);
            Win32Interop.RegisterHotKey(_hwnd, HotkeyToggleUiId, oldUiMods | Win32Interop.MOD_NOREPEAT, oldUiVk);
            return $"显隐快捷键 {HotkeyFormatter.Format(uiMods, uiVk)} 已被其他程序占用";
        }

        return null;
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            Activate();
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(_config.OutputDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _config.OutputDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowBanner($"无法打开目录：{ex.Message}", isError: true);
        }
    }

    private void ShowBanner(string message, bool isError, bool persistent = false)
    {
        _bannerTimer.Stop();
        StatusBannerText.Text = message;
        StatusBannerText.Foreground = isError
            ? new Media.SolidColorBrush(Media.Color.FromRgb(252, 165, 165))
            : new Media.SolidColorBrush(Media.Color.FromRgb(147, 197, 253));
        StatusBanner.Visibility = Wpf.Visibility.Visible;

        if (!persistent)
        {
            _bannerTimer.Start();
        }
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void RecordButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        await ToggleRecordingAsync();
    }

    private void RegionButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        SelectRegion();
    }

    private void RegionButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ClearRegion();
    }

    private void FpsButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        int idx = Array.IndexOf(AppConfig.SupportedFps, _config.Fps);
        int next = AppConfig.SupportedFps[(idx + 1) % AppConfig.SupportedFps.Length];
        SetFps(next);
    }

    private void AudioButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        AudioCaptureMode next = _config.AudioMode switch
        {
            AudioCaptureMode.None => AudioCaptureMode.System,
            AudioCaptureMode.System => AudioCaptureMode.Microphone,
            AudioCaptureMode.Microphone => AudioCaptureMode.Both,
            _ => AudioCaptureMode.None
        };
        SetAudioMode(next);
    }

    private void OpenFolderButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        OpenOutputFolder();
    }

    private void HideButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Hide();
    }

    private void CloseButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        RequestExit();
    }

    private void StatusBanner_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastSavedFilePath) && File.Exists(_lastSavedFilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_lastSavedFilePath}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                OpenOutputFolder();
            }
        }
    }

    private async void RequestExit()
    {
        if (_recorder.IsRecording)
        {
            Show();
            ShowBanner("正在停止并保存 MP4，请稍候...", isError: false, persistent: true);
            await ToggleRecordingAsync();
        }

        _allowRealClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowRealClose && _recorder.IsRecording)
        {
            e.Cancel = true;
            RequestExit();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _bannerTimer.Stop();
        StopDotPulse();
        CloseGuideWindow();

        if (_hwnd != IntPtr.Zero)
        {
            Win32Interop.UnregisterHotKey(_hwnd, HotkeyRecordId);
            Win32Interop.UnregisterHotKey(_hwnd, HotkeyToggleUiId);
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        _tray.Dispose();
        _recorder.Dispose();

        base.OnClosed(e);
        Wpf.Application.Current.Shutdown();
    }
}
