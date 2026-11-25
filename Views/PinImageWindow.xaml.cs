using System.IO;
using Controls = System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniRec.Native;
using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Views;

public partial class PinImageWindow : Wpf.Window
{
    private const double FrameMargin = 10.0;
    private const double MinScale = 0.15;
    private const double MaxScale = 4.0;

    private readonly Imaging.BitmapSource _bitmap;
    private readonly CaptureRegion _initialPhysRegion;
    private readonly string _defaultSaveDir;
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    private IntPtr _hwnd;
    private double _baseDipW;
    private double _baseDipH;
    private double _scale = 1.0;
    private double _opacity = 1.0;
    private bool _excludeFromCapture = false;
    private bool _ocrBusy;
    private Controls.MenuItem? _excludeMenuItem;

    public event Action? CloseAllRequested;

    internal PinImageWindow(
        Imaging.BitmapSource bitmap,
        CaptureRegion initialPhysRegion,
        string defaultSaveDir)
    {
        InitializeComponent();

        _bitmap = bitmap;
        _initialPhysRegion = initialPhysRegion;
        _defaultSaveDir = defaultSaveDir;

        PinImage.Source = _bitmap;
        _baseDipW = Math.Max(16, _bitmap.PixelWidth);
        _baseDipH = Math.Max(16, _bitmap.PixelHeight);

        _hudTimer.Tick += (_, _) =>
        {
            _hudTimer.Stop();
            HudBadge.Visibility = Wpf.Visibility.Collapsed;
        };

        BuildContextMenu();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.SetCaptureExclusion(_hwnd, _excludeFromCapture);
    }

    private void Window_Loaded(object sender, Wpf.RoutedEventArgs e)
    {
        Wpf.PresentationSource? source = Wpf.PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            Media.Matrix m = source.CompositionTarget.TransformFromDevice;
            Wpf.Point topLeft = m.Transform(new Wpf.Point(_initialPhysRegion.X, _initialPhysRegion.Y));
            Wpf.Point bottomRight = m.Transform(new Wpf.Point(
                _initialPhysRegion.X + _bitmap.PixelWidth,
                _initialPhysRegion.Y + _bitmap.PixelHeight));

            _baseDipW = Math.Max(16, Math.Abs(bottomRight.X - topLeft.X));
            _baseDipH = Math.Max(16, Math.Abs(bottomRight.Y - topLeft.Y));

            Left = Math.Min(topLeft.X, bottomRight.X) - FrameMargin - 1;
            Top = Math.Min(topLeft.Y, bottomRight.Y) - FrameMargin - 1;
        }
        else
        {
            Left = _initialPhysRegion.X - FrameMargin - 1;
            Top = _initialPhysRegion.Y - FrameMargin - 1;
        }

        Width = _baseDipW + FrameMargin * 2 + 2;
        Height = _baseDipH + FrameMargin * 2 + 2;
    }

    private void BuildContextMenu()
    {
        var menu = new Controls.ContextMenu();

        var copyItem = new Controls.MenuItem { Header = "复制图像 (Ctrl+C)" };
        copyItem.Click += (_, _) => CopyImage();

        var saveItem = new Controls.MenuItem { Header = "另存为 PNG (Ctrl+S)" };
        saveItem.Click += (_, _) => SaveImage();

        var resetItem = new Controls.MenuItem { Header = "恢复 100% 大小" };
        resetItem.Click += (_, _) => ResetScaleAndOpacity();

        var ocrItem = new Controls.MenuItem { Header = "提取文字 OCR (Ctrl+O)" };
        ocrItem.Click += async (_, _) => await RunOcrAsync();

        _excludeMenuItem = new Controls.MenuItem
        {
            Header = "录屏时隐身",
            IsCheckable = true,
            IsChecked = _excludeFromCapture
        };
        _excludeMenuItem.Click += (_, _) =>
        {
            _excludeFromCapture = _excludeMenuItem.IsChecked;
            if (_hwnd != IntPtr.Zero)
            {
                Win32Interop.SetCaptureExclusion(_hwnd, _excludeFromCapture);
            }
            ShowHud(_excludeFromCapture ? "录屏隐身：开" : "录屏隐身：关");
        };

        var closeItem = new Controls.MenuItem { Header = "关闭贴图 (Esc)" };
        closeItem.Click += (_, _) => Close();

        var closeAllItem = new Controls.MenuItem { Header = "关闭全部贴图" };
        closeAllItem.Click += (_, _) => CloseAllRequested?.Invoke();

        menu.Items.Add(ocrItem);
        menu.Items.Add(new Controls.Separator());
        menu.Items.Add(copyItem);
        menu.Items.Add(saveItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(new Controls.Separator());
        menu.Items.Add(_excludeMenuItem);
        menu.Items.Add(new Controls.Separator());
        menu.Items.Add(closeItem);
        menu.Items.Add(closeAllItem);

        ContextMenu = menu;
    }

    private void Window_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        if (e.LeftButton == Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_MouseWheel(object sender, Input.MouseWheelEventArgs e)
    {
        if ((Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0)
        {
            double step = e.Delta > 0 ? 0.08 : -0.08;
            _opacity = Math.Clamp(_opacity + step, 0.2, 1.0);
            Opacity = _opacity;
            ShowHud($"透明度 {Math.Round(_opacity * 100)}%");
            return;
        }

        double oldW = Width;
        double oldH = Height;
        double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);

        double newW = Math.Max(36, _baseDipW * _scale + FrameMargin * 2 + 2);
        double newH = Math.Max(36, _baseDipH * _scale + FrameMargin * 2 + 2);

        Left += (oldW - newW) / 2.0;
        Top += (oldH - newH) / 2.0;
        Width = newW;
        Height = newH;

        ShowHud($"{Math.Round(_scale * 100)}%");
    }

    private async void Window_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        if (e.Key == Input.Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if ((Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0)
        {
            if (e.Key == Input.Key.C)
            {
                e.Handled = true;
                CopyImage();
            }
            else if (e.Key == Input.Key.S)
            {
                e.Handled = true;
                SaveImage();
            }
            else if (e.Key == Input.Key.O)
            {
                e.Handled = true;
                await RunOcrAsync();
            }
        }
    }

    private async Task RunOcrAsync()
    {
        if (_ocrBusy) return;
        _ocrBusy = true;
        ShowHud("正在识别文字...");

        try
        {
            OcrDocumentResult result = await NativeOcrService.RecognizeBitmapAsync(_bitmap);
            var anchor = new Wpf.Rect(Left + FrameMargin, Top + FrameMargin, Math.Max(40, Width - FrameMargin * 2), Math.Max(40, Height - FrameMargin * 2));
            var win = new OcrResultWindow(result, anchor);
            win.Show();
        }
        finally
        {
            _ocrBusy = false;
        }
    }

    private void CopyImage()
    {
        Wpf.Clipboard.SetImage(_bitmap);
        ShowHud("已复制");
    }

    private void SaveImage()
    {
        Directory.CreateDirectory(_defaultSaveDir);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = _defaultSaveDir,
            FileName = $"Pin_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var encoder = new Imaging.PngBitmapEncoder();
        encoder.Frames.Add(Imaging.BitmapFrame.Create(_bitmap));
        using (FileStream fs = File.Create(dialog.FileName))
        {
            encoder.Save(fs);
        }
        ShowHud("已保存 PNG");
    }

    private void ResetScaleAndOpacity()
    {
        double oldW = Width;
        double oldH = Height;
        _scale = 1.0;
        _opacity = 1.0;
        Opacity = 1.0;

        double newW = _baseDipW + FrameMargin * 2 + 2;
        double newH = _baseDipH + FrameMargin * 2 + 2;
        Left += (oldW - newW) / 2.0;
        Top += (oldH - newH) / 2.0;
        Width = newW;
        Height = newH;
        ShowHud("100%");
    }

    private void ShowHud(string text)
    {
        _hudTimer.Stop();
        HudBadgeText.Text = text;
        HudBadge.Visibility = Wpf.Visibility.Visible;
        _hudTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hudTimer.Stop();
        base.OnClosed(e);
    }
}
