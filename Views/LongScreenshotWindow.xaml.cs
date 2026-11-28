using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniRec.Native;
using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Views;

internal enum LongScreenshotOutcome
{
    Cancelled,
    Copied,
    Saved,
    Pinned
}

public partial class LongScreenshotWindow : Wpf.Window
{
    private const int ManualPollIntervalMs = 150;
    private const int AutoScrollIntervalMs = 260;
    private const int AutoScrollWheelDelta = -360;
    private const int AutoStopStaticThreshold = 4;

    private readonly CaptureRegion _region;
    private readonly string _defaultSaveDir;
    private readonly DispatcherTimer _captureTimer = new();
    private LongScreenshotStitcher _stitcher;
    private RegionGuideWindow? _guideWindow;

    private bool _isAutoScrolling;
    private bool _isCapturingTick;
    private int _autoScrollStaticCount;
    private Win32Interop.POINT _autoScrollAnchorPhys;

    internal LongScreenshotOutcome Outcome { get; private set; } = LongScreenshotOutcome.Cancelled;
    internal Imaging.BitmapSource? ResultBitmap { get; private set; }
    internal string? SavedFilePath { get; private set; }

    internal LongScreenshotWindow(CaptureRegion region, string defaultSaveDir)
    {
        InitializeComponent();

        _region = region;
        _defaultSaveDir = defaultSaveDir;
        _stitcher = new LongScreenshotStitcher(region.Width, region.Height);

        _captureTimer.Interval = TimeSpan.FromMilliseconds(ManualPollIntervalMs);
        _captureTimer.Tick += CaptureTimer_Tick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.ExcludeFromCapture(hwnd);
    }

    private void Window_Loaded(object sender, Wpf.RoutedEventArgs e)
    {
        // 显示穿透的微信绿选区引导框（完全不阻挡滚轮且不会被抓进长图）
        _guideWindow = new RegionGuideWindow(_region, Media.Color.FromRgb(7, 193, 96));
        _guideWindow.Show();

        PositionBesideRegion();

        // 延迟一帧等待界面就绪后抓取首帧
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            CaptureAndStitchSingleFrame(fromAutoScroll: false);
            _captureTimer.Start();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _captureTimer.Stop();
        if (_guideWindow is not null)
        {
            try { _guideWindow.Close(); } catch { }
            _guideWindow = null;
        }
        base.OnClosed(e);
    }

    private void PositionBesideRegion()
    {
        Wpf.PresentationSource? src = Wpf.PresentationSource.FromVisual(this);
        Media.Matrix physToDip = src?.CompositionTarget?.TransformFromDevice ?? Media.Matrix.Identity;
        Wpf.Point tl = physToDip.Transform(new Wpf.Point(_region.X, _region.Y));
        Wpf.Point br = physToDip.Transform(new Wpf.Point(_region.X + _region.Width, _region.Y + _region.Height));

        Wpf.Rect workArea = Wpf.SystemParameters.WorkArea;
        UpdateLayout();
        double winW = ActualWidth > 0 ? ActualWidth : Width;
        double winH = ActualHeight > 0 ? ActualHeight : 490;

        double left;
        if (br.X + 12 + winW <= workArea.Right)
        {
            left = br.X + 12;
        }
        else if (tl.X - 12 - winW >= workArea.Left)
        {
            left = tl.X - 12 - winW;
        }
        else
        {
            left = Math.Max(workArea.Left + 8, workArea.Right - winW - 12);
        }

        double top = Math.Clamp(tl.Y, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - winH - 8));
        Left = left;
        Top = top;
    }

    private async void CaptureTimer_Tick(object? sender, EventArgs e)
    {
        if (_isCapturingTick) return;
        _isCapturingTick = true;

        try
        {
            if (_isAutoScrolling)
            {
                // 如果用户主动移动了鼠标（离开自动滚动锚点超过 40px），立即礼貌停止自动滚动把控制权还给用户
                if (Win32Interop.GetCursorPos(out Win32Interop.POINT cur) &&
                    (Math.Abs(cur.X - _autoScrollAnchorPhys.X) > 40 || Math.Abs(cur.Y - _autoScrollAnchorPhys.Y) > 40))
                {
                    SetAutoScrolling(false, "检测到鼠标移动，已暂停自动滚动（可继续手动滚轮）");
                    return;
                }

                Win32Interop.SetCursorPos(_autoScrollAnchorPhys.X, _autoScrollAnchorPhys.Y);
                Win32Interop.SendVerticalMouseWheel(AutoScrollWheelDelta);
                await Task.Delay(140);
                if (!IsLoaded) return;
            }

            CaptureAndStitchSingleFrame(fromAutoScroll: _isAutoScrolling);
        }
        finally
        {
            _isCapturingTick = false;
        }
    }

    private void CaptureAndStitchSingleFrame(bool fromAutoScroll)
    {
        byte[] frame;
        try
        {
            frame = ScreenSnapshot.CaptureRegionBgra(_region);
        }
        catch (Exception ex)
        {
            SetAutoScrolling(false, $"抓帧异常：{ex.Message}");
            return;
        }

        StitchFrameResult result = _stitcher.TryAppendFrame(frame);
        switch (result.Status)
        {
            case StitchFrameStatus.FirstFrame:
                RefreshPreviewUi();
                break;

            case StitchFrameStatus.Appended:
                _autoScrollStaticCount = 0;
                RefreshPreviewUi();
                if (!fromAutoScroll)
                {
                    HintText.Text = $"已拼接 {_stitcher.AppendedFrameCount} 段 · 继续向下滚动，完成后点复制/保存";
                }
                break;

            case StitchFrameStatus.MaxHeightReached:
                RefreshPreviewUi();
                SetAutoScrolling(false, "已达到最大安全拼接高度，可保存或复制当前长图");
                break;

            case StitchFrameStatus.Static:
                if (fromAutoScroll)
                {
                    _autoScrollStaticCount++;
                    if (_autoScrollStaticCount >= AutoStopStaticThreshold)
                    {
                        SetAutoScrolling(false, "✓ 已自动滚动至页面底部，可复制、保存或贴图");
                    }
                }
                break;

            case StitchFrameStatus.NoMatch:
                if (!fromAutoScroll)
                {
                    HintText.Text = "滚动过快未能对齐上一帧，请稍微放慢滚轮速度";
                }
                break;
        }
    }

    private void RefreshPreviewUi()
    {
        SizeInfoText.Text = $"{_stitcher.Width} × {_stitcher.TotalHeight}";
        PreviewImage.Source = _stitcher.BuildPreviewBitmap(maxPreviewWidth: 230);
        PreviewScroller.ScrollToEnd();
    }

    private void SetAutoScrolling(bool enabled, string? statusMessage = null)
    {
        _isAutoScrolling = enabled;
        _autoScrollStaticCount = 0;
        _captureTimer.Interval = TimeSpan.FromMilliseconds(enabled ? AutoScrollIntervalMs : ManualPollIntervalMs);

        if (enabled)
        {
            _autoScrollAnchorPhys = new Win32Interop.POINT
            {
                X = _region.X + _region.Width / 2,
                Y = _region.Y + _region.Height / 2
            };
            Win32Interop.SetCursorPos(_autoScrollAnchorPhys.X, _autoScrollAnchorPhys.Y);
            AutoScrollBtn.Tag = "Active";
            AutoScrollBtn.Content = "⏸ 暂停自动滚动";
            StatusDot.Fill = new Media.SolidColorBrush(Media.Color.FromRgb(7, 193, 96));
            HintText.Text = statusMessage ?? "正在自动向下滚动拼接...（移动鼠标或按空格随时暂停）";
        }
        else
        {
            AutoScrollBtn.Tag = null;
            AutoScrollBtn.Content = "▶ 自动向下滚动";
            StatusDot.Fill = new Media.SolidColorBrush(Media.Color.FromRgb(59, 130, 246));
            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                HintText.Text = statusMessage;
            }
        }
    }

    private void Header_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        bool ctrl = (Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0;
        if (e.Key == Input.Key.Escape)
        {
            e.Handled = true;
            Outcome = LongScreenshotOutcome.Cancelled;
            DialogResult = false;
            Close();
        }
        else if (e.Key == Input.Key.Space)
        {
            e.Handled = true;
            SetAutoScrolling(!_isAutoScrolling, _isAutoScrolling ? "已暂停自动滚动，可继续手动滚轮" : null);
        }
        else if (e.Key == Input.Key.Enter)
        {
            e.Handled = true;
            CompleteCopy();
        }
        else if (ctrl && e.Key == Input.Key.S)
        {
            e.Handled = true;
            CompleteSave();
        }
    }

    private void AutoScrollBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        SetAutoScrolling(!_isAutoScrolling, _isAutoScrolling ? "已暂停自动滚动，可继续手动滚轮" : null);
    }

    private void ResetBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        SetAutoScrolling(false, "已重置长图，请向下滚动鼠标滚轮开始拼接");
        _stitcher = new LongScreenshotStitcher(_region.Width, _region.Height);
        CaptureAndStitchSingleFrame(fromAutoScroll: false);
    }

    private void CancelBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Outcome = LongScreenshotOutcome.Cancelled;
        DialogResult = false;
        Close();
    }

    private void PinBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _captureTimer.Stop();
        ResultBitmap = _stitcher.BuildFinalBitmap();
        Outcome = LongScreenshotOutcome.Pinned;
        DialogResult = true;
        Close();
    }

    private void SaveBtn_Click(object sender, Wpf.RoutedEventArgs e) => CompleteSave();

    private void CopyBtn_Click(object sender, Wpf.RoutedEventArgs e) => CompleteCopy();

    private void CompleteCopy()
    {
        _captureTimer.Stop();
        Imaging.BitmapSource bmp = _stitcher.BuildFinalBitmap();
        if (!ClipboardHelper.TrySetImage(bmp))
        {
            HintText.Text = "剪贴板被其他程序占用，请重试";
            _captureTimer.Start();
            return;
        }

        ResultBitmap = bmp;
        Outcome = LongScreenshotOutcome.Copied;
        DialogResult = true;
        Close();
    }

    private void CompleteSave()
    {
        _captureTimer.Stop();
        SetAutoScrolling(false);

        Imaging.BitmapSource bmp = _stitcher.BuildFinalBitmap();
        Directory.CreateDirectory(_defaultSaveDir);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = _defaultSaveDir,
            FileName = $"LongShot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            _captureTimer.Start();
            return;
        }

        var encoder = new Imaging.PngBitmapEncoder();
        encoder.Frames.Add(Imaging.BitmapFrame.Create(bmp));
        using (FileStream fs = File.Create(dialog.FileName))
        {
            encoder.Save(fs);
        }

        ResultBitmap = bmp;
        SavedFilePath = dialog.FileName;
        Outcome = LongScreenshotOutcome.Saved;
        DialogResult = true;
        Close();
    }
}
