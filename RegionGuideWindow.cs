using System.Windows.Controls;
using System.Windows.Interop;
using MiniRec.Native;
using MiniRec.Services;
using Wpf = System.Windows;
using Media = System.Windows.Media;

namespace MiniRec;

internal sealed class RegionGuideWindow : Wpf.Window
{
    private const int BorderThicknessPx = 2;
    private readonly CaptureRegion _region;

    public RegionGuideWindow(CaptureRegion region)
    {
        _region = region.Normalize();

        Title = "MiniRec Region Guide";
        WindowStyle = Wpf.WindowStyle.None;
        AllowsTransparency = true;
        Background = Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        Focusable = false;
        ResizeMode = Wpf.ResizeMode.NoResize;

        Content = new Border
        {
            BorderBrush = new Media.SolidColorBrush(Media.Color.FromArgb(230, 239, 68, 68)),
            BorderThickness = new Wpf.Thickness(BorderThicknessPx),
            Background = Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.ExcludeFromCapture(hwnd);
        Win32Interop.MakeClickThroughToolWindow(hwnd);
        Win32Interop.SetWindowPos(
            hwnd,
            Win32Interop.HWND_TOPMOST,
            _region.X - BorderThicknessPx,
            _region.Y - BorderThicknessPx,
            _region.Width + BorderThicknessPx * 2,
            _region.Height + BorderThicknessPx * 2,
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);
    }
}
