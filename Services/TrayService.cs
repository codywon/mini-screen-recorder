using MiniRec.Native;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace MiniRec.Services;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _screenshotItem;
    private readonly Forms.ToolStripMenuItem _recordItem;
    private readonly Forms.ToolStripMenuItem _selectRegionItem;
    private readonly Forms.ToolStripMenuItem _clearRegionItem;
    private readonly Forms.ToolStripMenuItem _closeAllPinsItem;
    private readonly Forms.ToolStripMenuItem _fpsMenuItem;
    private readonly Forms.ToolStripMenuItem _audioMenuItem;
    private readonly Forms.ToolStripMenuItem _hotkeyItem;
    private readonly Forms.ToolStripMenuItem _autoStartItem;
    private readonly Forms.ToolStripMenuItem _closeBehaviorMenuItem;
    private readonly Forms.ToolStripMenuItem _toggleUiItem;
    private readonly Forms.ToolStripMenuItem _openFolderItem;
    private readonly Forms.ToolStripMenuItem _checkUpdateItem;
    private readonly Forms.ToolStripMenuItem _aboutItem;
    private readonly Forms.ToolStripMenuItem _exitItem;

    private readonly Dictionary<int, Forms.ToolStripMenuItem> _fpsItems = [];
    private readonly Dictionary<AudioCaptureMode, Forms.ToolStripMenuItem> _audioItems = [];
    private readonly Dictionary<CloseButtonBehavior, Forms.ToolStripMenuItem> _closeBehaviorItems = [];

    private IntPtr _currentIconHandle = IntPtr.Zero;
    private Drawing.Icon? _currentIcon;
    private bool _disposed;

    public event Action? ScreenshotClicked;
    public event Action? RecordClicked;
    public event Action? SelectRegionClicked;
    public event Action? ClearRegionClicked;
    public event Action? CloseAllPinsClicked;
    public event Action<int>? FpsSelected;
    public event Action<AudioCaptureMode>? AudioModeSelected;
    public event Action? ConfigureHotkeysClicked;
    public event Action? AutoStartToggled;
    public event Action<CloseButtonBehavior>? CloseBehaviorSelected;
    public event Action? ToggleUiClicked;
    public event Action? OpenFolderClicked;
    public event Action? CheckUpdateClicked;
    public event Action? AboutClicked;
    public event Action? ExitClicked;

    public TrayService()
    {
        _screenshotItem = new Forms.ToolStripMenuItem("屏幕截图");
        _recordItem = new Forms.ToolStripMenuItem("开始录制");
        _selectRegionItem = new Forms.ToolStripMenuItem("框选区域...");
        _clearRegionItem = new Forms.ToolStripMenuItem("恢复全屏");
        _closeAllPinsItem = new Forms.ToolStripMenuItem("关闭全部贴图");
        _fpsMenuItem = new Forms.ToolStripMenuItem("帧率");
        _audioMenuItem = new Forms.ToolStripMenuItem("录音");
        _hotkeyItem = new Forms.ToolStripMenuItem("设置快捷键...");
        _autoStartItem = new Forms.ToolStripMenuItem("开机自启动");
        _closeBehaviorMenuItem = new Forms.ToolStripMenuItem("关闭按钮行为");
        _toggleUiItem = new Forms.ToolStripMenuItem("显示/隐藏悬浮条");
        _openFolderItem = new Forms.ToolStripMenuItem("打开录制目录");
        _checkUpdateItem = new Forms.ToolStripMenuItem("检查更新...");
        _aboutItem = new Forms.ToolStripMenuItem("关于 MiniRec...");
        _exitItem = new Forms.ToolStripMenuItem("退出");

        foreach (int fps in AppConfig.SupportedFps)
        {
            var item = new Forms.ToolStripMenuItem($"{fps} FPS");
            int captured = fps;
            item.Click += (_, _) => FpsSelected?.Invoke(captured);
            _fpsItems[fps] = item;
            _fpsMenuItem.DropDownItems.Add(item);
        }

        (AudioCaptureMode Mode, string Label)[] audioOptions =
        [
            (AudioCaptureMode.None, "静音（不录声音）"),
            (AudioCaptureMode.System, "仅系统声音"),
            (AudioCaptureMode.Microphone, "仅麦克风"),
            (AudioCaptureMode.Both, "系统声音 + 麦克风")
        ];
        foreach ((AudioCaptureMode mode, string label) in audioOptions)
        {
            var item = new Forms.ToolStripMenuItem(label);
            AudioCaptureMode captured = mode;
            item.Click += (_, _) => AudioModeSelected?.Invoke(captured);
            _audioItems[mode] = item;
            _audioMenuItem.DropDownItems.Add(item);
        }

        (CloseButtonBehavior Behavior, string Label)[] closeOptions =
        [
            (CloseButtonBehavior.Ask, "每次询问"),
            (CloseButtonBehavior.MinimizeToTray, "最小化到托盘"),
            (CloseButtonBehavior.Exit, "彻底退出")
        ];
        foreach ((CloseButtonBehavior behavior, string label) in closeOptions)
        {
            var item = new Forms.ToolStripMenuItem(label);
            CloseButtonBehavior captured = behavior;
            item.Click += (_, _) => CloseBehaviorSelected?.Invoke(captured);
            _closeBehaviorItems[behavior] = item;
            _closeBehaviorMenuItem.DropDownItems.Add(item);
        }

        _screenshotItem.Click += (_, _) => ScreenshotClicked?.Invoke();
        _recordItem.Click += (_, _) => RecordClicked?.Invoke();
        _selectRegionItem.Click += (_, _) => SelectRegionClicked?.Invoke();
        _clearRegionItem.Click += (_, _) => ClearRegionClicked?.Invoke();
        _closeAllPinsItem.Click += (_, _) => CloseAllPinsClicked?.Invoke();
        _hotkeyItem.Click += (_, _) => ConfigureHotkeysClicked?.Invoke();
        _autoStartItem.Click += (_, _) => AutoStartToggled?.Invoke();
        _toggleUiItem.Click += (_, _) => ToggleUiClicked?.Invoke();
        _openFolderItem.Click += (_, _) => OpenFolderClicked?.Invoke();
        _checkUpdateItem.Click += (_, _) => CheckUpdateClicked?.Invoke();
        _aboutItem.Click += (_, _) => AboutClicked?.Invoke();
        _exitItem.Click += (_, _) => ExitClicked?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_screenshotItem);
        menu.Items.Add(_recordItem);
        menu.Items.Add(_selectRegionItem);
        menu.Items.Add(_clearRegionItem);
        menu.Items.Add(_closeAllPinsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_fpsMenuItem);
        menu.Items.Add(_audioMenuItem);
        menu.Items.Add(_hotkeyItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(_closeBehaviorMenuItem);
        menu.Items.Add(_toggleUiItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_openFolderItem);
        menu.Items.Add(_checkUpdateItem);
        menu.Items.Add(_aboutItem);
        menu.Items.Add(_exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "MiniRec - 极简录屏",
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => ToggleUiClicked?.Invoke();
        UpdateState(isRecording: false, "Shift+F1", "Ctrl+Alt+H", null, 60, AudioCaptureMode.Both, "F1", 0);
    }

    public void UpdateState(
        bool isRecording,
        string recordHotkey,
        string toggleHotkey,
        CaptureRegion? region = null,
        int fps = 60,
        AudioCaptureMode audioMode = AudioCaptureMode.Both,
        string screenshotHotkey = "F1",
        int pinCount = 0,
        bool autoStartEnabled = false,
        CloseButtonBehavior closeBehavior = CloseButtonBehavior.Ask)
    {
        if (_disposed) return;

        _screenshotItem.Text = $"屏幕截图 ({screenshotHotkey})";

        string scopeLabel = region is null ? "全屏" : $"{region.Width}×{region.Height}";
        _recordItem.Text = isRecording
            ? $"停止录制 ({recordHotkey})"
            : $"开始录制 · {scopeLabel} ({recordHotkey})";

        _selectRegionItem.Enabled = !isRecording;
        _clearRegionItem.Enabled = !isRecording && region is not null;
        _closeAllPinsItem.Visible = pinCount > 0;
        _closeAllPinsItem.Text = $"关闭全部贴图 ({pinCount})";
        _fpsMenuItem.Enabled = !isRecording;
        _audioMenuItem.Enabled = !isRecording;
        _hotkeyItem.Enabled = !isRecording;

        _autoStartItem.Checked = autoStartEnabled;
        foreach (var (behavior, item) in _closeBehaviorItems)
        {
            item.Checked = behavior == closeBehavior;
        }

        foreach (var (value, item) in _fpsItems)
        {
            item.Checked = value == fps;
        }
        foreach (var (mode, item) in _audioItems)
        {
            item.Checked = mode == audioMode;
        }

        _toggleUiItem.Text = $"显示/隐藏悬浮条 ({toggleHotkey})";
        _notifyIcon.Text = isRecording
            ? $"MiniRec - 正在录制 {fps}FPS ({recordHotkey})"
            : $"MiniRec - 截图 {screenshotHotkey} · 录屏 {recordHotkey}";

        SetIcon(isRecording);
    }

    private void SetIcon(bool isRecording)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            if (isRecording)
            {
                using var outerBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(239, 68, 68));
                g.FillEllipse(outerBrush, 2, 2, 28, 28);

                using var innerBrush = new Drawing.SolidBrush(Drawing.Color.White);
                g.FillRectangle(innerBrush, 11, 11, 10, 10);
            }
            else
            {
                using var bgBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(15, 23, 42));
                g.FillEllipse(bgBrush, 2, 2, 28, 28);

                using var ringPen = new Drawing.Pen(Drawing.Color.FromArgb(56, 189, 248), 2.2f);
                g.DrawEllipse(ringPen, 4, 4, 24, 24);

                using var dotBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(239, 68, 68));
                g.FillEllipse(dotBrush, 10, 10, 12, 12);
            }
        }

        IntPtr newHandle = bmp.GetHicon();
        Drawing.Icon newIcon = Drawing.Icon.FromHandle(newHandle);

        _notifyIcon.Icon = newIcon;

        _currentIcon?.Dispose();
        if (_currentIconHandle != IntPtr.Zero)
        {
            Win32Interop.DestroyIcon(_currentIconHandle);
        }

        _currentIcon = newIcon;
        _currentIconHandle = newHandle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();

        _currentIcon?.Dispose();
        if (_currentIconHandle != IntPtr.Zero)
        {
            Win32Interop.DestroyIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }
    }
}
