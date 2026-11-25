using MiniRec.Native;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace MiniRec.Services;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _recordItem;
    private readonly Forms.ToolStripMenuItem _selectRegionItem;
    private readonly Forms.ToolStripMenuItem _clearRegionItem;
    private readonly Forms.ToolStripMenuItem _fpsMenuItem;
    private readonly Forms.ToolStripMenuItem _audioMenuItem;
    private readonly Forms.ToolStripMenuItem _hotkeyItem;
    private readonly Forms.ToolStripMenuItem _toggleUiItem;
    private readonly Forms.ToolStripMenuItem _openFolderItem;
    private readonly Forms.ToolStripMenuItem _exitItem;

    private readonly Dictionary<int, Forms.ToolStripMenuItem> _fpsItems = [];
    private readonly Dictionary<AudioCaptureMode, Forms.ToolStripMenuItem> _audioItems = [];

    private IntPtr _currentIconHandle = IntPtr.Zero;
    private Drawing.Icon? _currentIcon;
    private bool _disposed;

    public event Action? RecordClicked;
    public event Action? SelectRegionClicked;
    public event Action? ClearRegionClicked;
    public event Action<int>? FpsSelected;
    public event Action<AudioCaptureMode>? AudioModeSelected;
    public event Action? ConfigureHotkeysClicked;
    public event Action? ToggleUiClicked;
    public event Action? OpenFolderClicked;
    public event Action? ExitClicked;

    public TrayService()
    {
        _recordItem = new Forms.ToolStripMenuItem("开始录制");
        _selectRegionItem = new Forms.ToolStripMenuItem("框选区域...");
        _clearRegionItem = new Forms.ToolStripMenuItem("恢复全屏");
        _fpsMenuItem = new Forms.ToolStripMenuItem("帧率");
        _audioMenuItem = new Forms.ToolStripMenuItem("录音");
        _hotkeyItem = new Forms.ToolStripMenuItem("设置快捷键...");
        _toggleUiItem = new Forms.ToolStripMenuItem("显示/隐藏悬浮条");
        _openFolderItem = new Forms.ToolStripMenuItem("打开录制目录");
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

        _recordItem.Click += (_, _) => RecordClicked?.Invoke();
        _selectRegionItem.Click += (_, _) => SelectRegionClicked?.Invoke();
        _clearRegionItem.Click += (_, _) => ClearRegionClicked?.Invoke();
        _hotkeyItem.Click += (_, _) => ConfigureHotkeysClicked?.Invoke();
        _toggleUiItem.Click += (_, _) => ToggleUiClicked?.Invoke();
        _openFolderItem.Click += (_, _) => OpenFolderClicked?.Invoke();
        _exitItem.Click += (_, _) => ExitClicked?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_recordItem);
        menu.Items.Add(_selectRegionItem);
        menu.Items.Add(_clearRegionItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_fpsMenuItem);
        menu.Items.Add(_audioMenuItem);
        menu.Items.Add(_hotkeyItem);
        menu.Items.Add(_toggleUiItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_openFolderItem);
        menu.Items.Add(_exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "MiniRec - 极简录屏",
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => ToggleUiClicked?.Invoke();
        UpdateState(isRecording: false, "Ctrl+Alt+R", "Ctrl+Alt+H", null, 60, AudioCaptureMode.Both);
    }

    public void UpdateState(
        bool isRecording,
        string recordHotkey,
        string toggleHotkey,
        CaptureRegion? region = null,
        int fps = 60,
        AudioCaptureMode audioMode = AudioCaptureMode.Both)
    {
        if (_disposed) return;

        string scopeLabel = region is null ? "全屏" : $"{region.Width}×{region.Height}";
        _recordItem.Text = isRecording
            ? $"停止录制 ({recordHotkey})"
            : $"开始录制 · {scopeLabel} ({recordHotkey})";

        _selectRegionItem.Enabled = !isRecording;
        _clearRegionItem.Enabled = !isRecording && region is not null;
        _fpsMenuItem.Enabled = !isRecording;
        _audioMenuItem.Enabled = !isRecording;
        _hotkeyItem.Enabled = !isRecording;

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
            : $"MiniRec - {scopeLabel} · {fps}FPS ({recordHotkey})";

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
