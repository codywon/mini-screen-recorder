using System.Windows.Interop;
using MiniRec.Native;
using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;
using Media = System.Windows.Media;

namespace MiniRec;

public partial class HotkeySettingsWindow : Wpf.Window
{
    private enum CaptureTarget
    {
        None,
        Record,
        ToggleUi
    }

    private readonly Func<uint, uint, uint, uint, string?> _applyCallback;

    private CaptureTarget _capturing = CaptureTarget.None;

    internal uint RecordModifiers { get; private set; }
    internal uint RecordVirtualKey { get; private set; }
    internal uint ToggleUiModifiers { get; private set; }
    internal uint ToggleUiVirtualKey { get; private set; }

    public HotkeySettingsWindow(
        uint recordModifiers,
        uint recordVirtualKey,
        uint toggleUiModifiers,
        uint toggleUiVirtualKey,
        Func<uint, uint, uint, uint, string?> applyCallback)
    {
        InitializeComponent();

        RecordModifiers = recordModifiers;
        RecordVirtualKey = recordVirtualKey;
        ToggleUiModifiers = toggleUiModifiers;
        ToggleUiVirtualKey = toggleUiVirtualKey;
        _applyCallback = applyCallback;

        RefreshButtons();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.TryEnableRoundedCorners(hwnd);
        Win32Interop.ExcludeFromCapture(hwnd);
    }

    private void RefreshButtons()
    {
        if (_capturing == CaptureTarget.Record)
        {
            RecordHotkeyBtn.Content = "请按新快捷键...";
            RecordHotkeyBtn.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(250, 204, 21));
            RecordHotkeyBtn.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(234, 179, 8));
        }
        else
        {
            RecordHotkeyBtn.Content = HotkeyFormatter.Format(RecordModifiers, RecordVirtualKey);
            RecordHotkeyBtn.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(226, 232, 240));
            RecordHotkeyBtn.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(51, 65, 85));
        }

        if (_capturing == CaptureTarget.ToggleUi)
        {
            ToggleUiHotkeyBtn.Content = "请按新快捷键...";
            ToggleUiHotkeyBtn.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(250, 204, 21));
            ToggleUiHotkeyBtn.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(234, 179, 8));
        }
        else
        {
            ToggleUiHotkeyBtn.Content = HotkeyFormatter.Format(ToggleUiModifiers, ToggleUiVirtualKey);
            ToggleUiHotkeyBtn.Foreground = new Media.SolidColorBrush(Media.Color.FromRgb(226, 232, 240));
            ToggleUiHotkeyBtn.BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(51, 65, 85));
        }
    }

    private void SetHint(string message, bool isError)
    {
        HintText.Text = message;
        HintText.Foreground = isError
            ? new Media.SolidColorBrush(Media.Color.FromRgb(252, 165, 165))
            : new Media.SolidColorBrush(Media.Color.FromRgb(148, 163, 184));
    }

    private void Header_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void RecordHotkeyBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _capturing = _capturing == CaptureTarget.Record ? CaptureTarget.None : CaptureTarget.Record;
        RefreshButtons();
        if (_capturing == CaptureTarget.Record)
        {
            Activate();
            Focus();
            SetHint("请按下「开始/停止录制」的新快捷键（Esc 取消）", isError: false);
        }
        else
        {
            SetHint("点击右侧按钮后按下新组合键（支持 F1–F12 单键）", isError: false);
        }
    }

    private void ToggleUiHotkeyBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _capturing = _capturing == CaptureTarget.ToggleUi ? CaptureTarget.None : CaptureTarget.ToggleUi;
        RefreshButtons();
        if (_capturing == CaptureTarget.ToggleUi)
        {
            Activate();
            Focus();
            SetHint("请按下「显示/隐藏悬浮条」的新快捷键（Esc 取消）", isError: false);
        }
        else
        {
            SetHint("点击右侧按钮后按下新组合键（支持 F1–F12 单键）", isError: false);
        }
    }

    private void Window_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        Input.Key key = e.Key == Input.Key.System ? e.SystemKey : e.Key;

        if (key == Input.Key.Escape)
        {
            e.Handled = true;
            if (_capturing != CaptureTarget.None)
            {
                _capturing = CaptureTarget.None;
                RefreshButtons();
                SetHint("已取消按键录入", isError: false);
            }
            else
            {
                DialogResult = false;
                Close();
            }
            return;
        }

        if (_capturing == CaptureTarget.None) return;
        e.Handled = true;

        if (key is Input.Key.LeftCtrl or Input.Key.RightCtrl or Input.Key.LeftAlt or Input.Key.RightAlt
            or Input.Key.LeftShift or Input.Key.RightShift or Input.Key.LWin or Input.Key.RWin)
        {
            return;
        }

        uint modifiers = 0;
        Input.ModifierKeys mods = Input.Keyboard.Modifiers;
        if ((mods & Input.ModifierKeys.Control) != 0) modifiers |= Win32Interop.MOD_CONTROL;
        if ((mods & Input.ModifierKeys.Alt) != 0) modifiers |= Win32Interop.MOD_ALT;
        if ((mods & Input.ModifierKeys.Shift) != 0) modifiers |= Win32Interop.MOD_SHIFT;
        if ((mods & Input.ModifierKeys.Windows) != 0) modifiers |= Win32Interop.MOD_WIN;

        uint vk = (uint)Input.KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        bool isFunctionKey = vk is >= 0x70 and <= 0x87;
        if (modifiers == 0 && !isFunctionKey)
        {
            SetHint("普通键需搭配 Ctrl/Alt/Shift 使用，或直接按 F1–F12", isError: true);
            return;
        }

        if (_capturing == CaptureTarget.Record)
        {
            if (modifiers == ToggleUiModifiers && vk == ToggleUiVirtualKey)
            {
                SetHint("不能与「显示/隐藏悬浮条」快捷键相同", isError: true);
                return;
            }
            RecordModifiers = modifiers;
            RecordVirtualKey = vk;
        }
        else if (_capturing == CaptureTarget.ToggleUi)
        {
            if (modifiers == RecordModifiers && vk == RecordVirtualKey)
            {
                SetHint("不能与「开始/停止录制」快捷键相同", isError: true);
                return;
            }
            ToggleUiModifiers = modifiers;
            ToggleUiVirtualKey = vk;
        }

        _capturing = CaptureTarget.None;
        RefreshButtons();
        SetHint("已捕获新快捷键，点击「保存」生效", isError: false);
    }

    private void ResetButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _capturing = CaptureTarget.None;
        RecordModifiers = Win32Interop.MOD_CONTROL | Win32Interop.MOD_ALT;
        RecordVirtualKey = 0x52; // R
        ToggleUiModifiers = Win32Interop.MOD_CONTROL | Win32Interop.MOD_ALT;
        ToggleUiVirtualKey = 0x48; // H
        RefreshButtons();
        SetHint("已还原默认快捷键，点击「保存」生效", isError: false);
    }

    private void SaveButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (RecordModifiers == ToggleUiModifiers && RecordVirtualKey == ToggleUiVirtualKey)
        {
            SetHint("两个快捷键不能相同", isError: true);
            return;
        }

        string? error = _applyCallback(RecordModifiers, RecordVirtualKey, ToggleUiModifiers, ToggleUiVirtualKey);
        if (!string.IsNullOrWhiteSpace(error))
        {
            SetHint(error, isError: true);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
