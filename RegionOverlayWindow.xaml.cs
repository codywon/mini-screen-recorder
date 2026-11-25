using System.Windows.Controls;
using System.Windows.Interop;
using MiniRec.Native;
using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace MiniRec;

public partial class RegionOverlayWindow : Wpf.Window
{
    private enum OverlayPhase
    {
        Hovering,
        Drawing,
        Editing
    }

    private const int MinSelectionPixels = 32;
    private const int DragThresholdPixels = 5;
    private const double HandleSizeDip = 8.0;

    private readonly IReadOnlyList<(int X, int Y, int Width, int Height)> _windowRects;
    private readonly Shapes.Rectangle[] _handles;

    private OverlayPhase _phase = OverlayPhase.Hovering;
    private bool _hasSource;
    private (int Vx, int Vy, int Vw, int Vh) _virtualBounds;

    private Win32Interop.POINT _dragStartPhys;
    private CaptureRegion? _hoverCandidate;
    private CaptureRegion _editRect = new(0, 0, 0, 0);
    private CaptureRegion _dragStartRect = new(0, 0, 0, 0);
    private RegionHitZone _activeHitZone = RegionHitZone.None;
    private bool _isEditingDrag;

    internal CaptureRegion? SelectedRegion { get; private set; }
    internal bool ClearedToFullScreen { get; private set; }
    internal bool StartRecordingImmediately { get; private set; }

    public RegionOverlayWindow(IReadOnlyList<(int X, int Y, int Width, int Height)>? windowRects = null)
    {
        InitializeComponent();
        _windowRects = windowRects ?? [];
        _handles = [HandleTL, HandleT, HandleTR, HandleR, HandleBR, HandleB, HandleBL, HandleL];
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.ExcludeFromCapture(hwnd);

        _virtualBounds = Win32Interop.GetVirtualScreenBounds();
        Win32Interop.SetWindowPos(
            hwnd,
            Win32Interop.HWND_TOPMOST,
            _virtualBounds.Vx,
            _virtualBounds.Vy,
            _virtualBounds.Vw,
            _virtualBounds.Vh,
            Win32Interop.SWP_SHOWWINDOW);
    }

    private void Window_Loaded(object sender, Wpf.RoutedEventArgs e)
    {
        _hasSource = Wpf.PresentationSource.FromVisual(this) is not null;
        if (_virtualBounds.Vw <= 0)
        {
            _virtualBounds = Win32Interop.GetVirtualScreenBounds();
        }

        UpdateOverlayGeometry(null);
        PositionHintPill();
        Activate();
        Focus();

        if (Win32Interop.GetCursorPos(out Win32Interop.POINT pt))
        {
            UpdateHoverAtCursor(pt);
        }
    }

    private void PositionHintPill()
    {
        if (!_hasSource) return;

        HintPill.UpdateLayout();
        (int primaryW, _) = Win32Interop.GetPrimaryScreenSize();
        Wpf.Point topCenter = PointFromScreen(new Wpf.Point(primaryW / 2.0, 22));
        double left = Math.Max(12, topCenter.X - HintPill.ActualWidth / 2.0);
        Canvas.SetLeft(HintPill, left);
        Canvas.SetTop(HintPill, Math.Max(12, topCenter.Y));
    }

    private void Window_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        if (e.Key == Input.Key.Escape)
        {
            e.Handled = true;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
            DialogResult = false;
            Close();
            return;
        }

        if (_phase != OverlayPhase.Editing) return;

        if (e.Key == Input.Key.Enter)
        {
            e.Handled = true;
            ConfirmSelection(startRecording: false);
            return;
        }

        int step = (Input.Keyboard.Modifiers & Input.ModifierKeys.Shift) != 0 ? 10 : 2;
        int dx = 0;
        int dy = 0;
        switch (e.Key)
        {
            case Input.Key.Left: dx = -step; break;
            case Input.Key.Right: dx = step; break;
            case Input.Key.Up: dy = -step; break;
            case Input.Key.Down: dy = step; break;
            default: return;
        }

        e.Handled = true;
        _editRect = RegionEditMath.ApplyMove(
            _editRect,
            dx,
            dy,
            _virtualBounds.Vx,
            _virtualBounds.Vy,
            _virtualBounds.Vw,
            _virtualBounds.Vh);
        RenderRegion(_editRect, showHandlesAndToolbar: true);
    }

    private void Window_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (IsFromActionToolbar(e.OriginalSource as Wpf.DependencyObject))
        {
            return;
        }

        if (!Win32Interop.GetCursorPos(out Win32Interop.POINT pt)) return;

        if (_phase == OverlayPhase.Hovering)
        {
            _dragStartPhys = pt;
            _phase = OverlayPhase.Drawing;
            CaptureMouse();
            return;
        }

        if (_phase == OverlayPhase.Editing)
        {
            RegionHitZone hit = HitTestEditRect(pt);
            if (hit == RegionHitZone.Move && e.ClickCount == 2)
            {
                ConfirmSelection(startRecording: false);
                return;
            }

            if (hit == RegionHitZone.None)
            {
                return;
            }

            _activeHitZone = hit;
            _dragStartPhys = pt;
            _dragStartRect = _editRect;
            _isEditingDrag = true;
            CaptureMouse();
        }
    }

    private void Window_MouseMove(object sender, Input.MouseEventArgs e)
    {
        if (!Win32Interop.GetCursorPos(out Win32Interop.POINT pt)) return;

        if (_phase == OverlayPhase.Hovering)
        {
            Cursor = Input.Cursors.Cross;
            UpdateHoverAtCursor(pt);
            return;
        }

        if (_phase == OverlayPhase.Drawing)
        {
            int distX = Math.Abs(pt.X - _dragStartPhys.X);
            int distY = Math.Abs(pt.Y - _dragStartPhys.Y);
            if (distX >= DragThresholdPixels || distY >= DragThresholdPixels)
            {
                _hoverCandidate = null;
                CaptureRegion drawn = ComputePhysicalRect(_dragStartPhys, pt);
                RenderRegion(drawn, showHandlesAndToolbar: false);
            }
            return;
        }

        if (_phase == OverlayPhase.Editing)
        {
            if (_isEditingDrag)
            {
                int dx = pt.X - _dragStartPhys.X;
                int dy = pt.Y - _dragStartPhys.Y;

                _editRect = _activeHitZone == RegionHitZone.Move
                    ? RegionEditMath.ApplyMove(
                        _dragStartRect,
                        dx,
                        dy,
                        _virtualBounds.Vx,
                        _virtualBounds.Vy,
                        _virtualBounds.Vw,
                        _virtualBounds.Vh)
                    : RegionEditMath.ApplyResize(
                        _dragStartRect,
                        _activeHitZone,
                        dx,
                        dy,
                        MinSelectionPixels,
                        _virtualBounds.Vx,
                        _virtualBounds.Vy,
                        _virtualBounds.Vw,
                        _virtualBounds.Vh);

                RenderRegion(_editRect, showHandlesAndToolbar: true);
            }
            else if (IsFromActionToolbar(e.OriginalSource as Wpf.DependencyObject))
            {
                Cursor = Input.Cursors.Arrow;
            }
            else
            {
                RegionHitZone hit = HitTestEditRect(pt);
                Cursor = GetCursorForHitZone(hit);
            }
        }
    }

    private void Window_MouseLeftButtonUp(object sender, Input.MouseButtonEventArgs e)
    {
        if (_phase == OverlayPhase.Drawing)
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            if (!Win32Interop.GetCursorPos(out Win32Interop.POINT endPt))
            {
                EnterHovering("未能读取鼠标坐标，请重试");
                return;
            }

            int distX = Math.Abs(endPt.X - _dragStartPhys.X);
            int distY = Math.Abs(endPt.Y - _dragStartPhys.Y);

            if (distX < DragThresholdPixels && distY < DragThresholdPixels)
            {
                if (_hoverCandidate is not null)
                {
                    EnterEditing(_hoverCandidate);
                }
                else
                {
                    EnterHovering("点击窗口或拖动至少 32×32 的区域");
                }
                return;
            }

            CaptureRegion drawn = ComputePhysicalRect(_dragStartPhys, endPt);
            if (drawn.Width < MinSelectionPixels || drawn.Height < MinSelectionPixels)
            {
                EnterHovering("选区太小（至少 32×32），请重新拖动");
                return;
            }

            EnterEditing(drawn);
            return;
        }

        if (_phase == OverlayPhase.Editing && _isEditingDrag)
        {
            _isEditingDrag = false;
            _activeHitZone = RegionHitZone.None;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
    }

    private void Window_MouseRightButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (_phase == OverlayPhase.Drawing)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();
            EnterHovering("点击锁定窗口或拖动框选 · Esc 取消 · 右键恢复全屏");
            return;
        }

        if (_phase == OverlayPhase.Editing)
        {
            if (_isEditingDrag)
            {
                _isEditingDrag = false;
                _activeHitZone = RegionHitZone.None;
                if (IsMouseCaptured) ReleaseMouseCapture();
                _editRect = _dragStartRect;
                RenderRegion(_editRect, showHandlesAndToolbar: true);
                return;
            }

            EnterHovering("点击锁定窗口或拖动框选 · Esc 取消 · 右键恢复全屏");
            return;
        }

        ClearedToFullScreen = true;
        DialogResult = true;
        Close();
    }

    private void RecordNowButton_Click(object sender, Wpf.RoutedEventArgs e) =>
        ConfirmSelection(startRecording: true);

    private void ConfirmButton_Click(object sender, Wpf.RoutedEventArgs e) =>
        ConfirmSelection(startRecording: false);

    private void ReselectButton_Click(object sender, Wpf.RoutedEventArgs e) =>
        EnterHovering("点击锁定窗口或拖动框选 · Esc 取消 · 右键恢复全屏");

    private void CancelButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmSelection(bool startRecording)
    {
        if (_phase != OverlayPhase.Editing) return;

        SelectedRegion = _editRect.Normalize();
        StartRecordingImmediately = startRecording;
        DialogResult = true;
        Close();
    }

    private void EnterHovering(string hintMessage)
    {
        _phase = OverlayPhase.Hovering;
        _isEditingDrag = false;
        _activeHitZone = RegionHitZone.None;
        _hoverCandidate = null;

        Cursor = Input.Cursors.Cross;
        ActionToolbar.Visibility = Wpf.Visibility.Collapsed;
        SetHandlesVisibility(Wpf.Visibility.Collapsed);
        SelectionBorder.Visibility = Wpf.Visibility.Collapsed;
        SizeBadge.Visibility = Wpf.Visibility.Collapsed;

        HintPillText.Text = hintMessage;
        HintPill.Visibility = Wpf.Visibility.Visible;
        PositionHintPill();
        UpdateOverlayGeometry(null);

        if (Win32Interop.GetCursorPos(out Win32Interop.POINT pt))
        {
            UpdateHoverAtCursor(pt);
        }
    }

    private void EnterEditing(CaptureRegion rect)
    {
        _phase = OverlayPhase.Editing;
        _isEditingDrag = false;
        _activeHitZone = RegionHitZone.None;
        _hoverCandidate = null;
        _editRect = rect;

        HintPill.Visibility = Wpf.Visibility.Collapsed;
        RenderRegion(_editRect, showHandlesAndToolbar: true);

        if (Win32Interop.GetCursorPos(out Win32Interop.POINT pt))
        {
            Cursor = GetCursorForHitZone(HitTestEditRect(pt));
        }
    }

    private void UpdateHoverAtCursor(Win32Interop.POINT pt)
    {
        CaptureRegion? hitWindow = null;
        foreach (var (x, y, w, h) in _windowRects)
        {
            if (pt.X >= x && pt.X < x + w && pt.Y >= y && pt.Y < y + h)
            {
                hitWindow = new CaptureRegion(x, y, w, h);
                break;
            }
        }

        _hoverCandidate = hitWindow;
        if (hitWindow is not null)
        {
            RenderRegion(hitWindow, showHandlesAndToolbar: false);
        }
        else
        {
            SelectionBorder.Visibility = Wpf.Visibility.Collapsed;
            SizeBadge.Visibility = Wpf.Visibility.Collapsed;
            UpdateOverlayGeometry(null);
        }
    }

    private void RenderRegion(CaptureRegion region, bool showHandlesAndToolbar)
    {
        if (!_hasSource) return;

        Wpf.Point topLeft = PointFromScreen(new Wpf.Point(region.X, region.Y));
        Wpf.Point bottomRight = PointFromScreen(new Wpf.Point(region.X + region.Width, region.Y + region.Height));

        double dipX = Math.Min(topLeft.X, bottomRight.X);
        double dipY = Math.Min(topLeft.Y, bottomRight.Y);
        double dipW = Math.Max(1, Math.Abs(bottomRight.X - topLeft.X));
        double dipH = Math.Max(1, Math.Abs(bottomRight.Y - topLeft.Y));

        var selRect = new Wpf.Rect(dipX, dipY, dipW, dipH);
        UpdateOverlayGeometry(selRect);

        SelectionBorder.Visibility = Wpf.Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, dipX);
        Canvas.SetTop(SelectionBorder, dipY);
        SelectionBorder.Width = dipW;
        SelectionBorder.Height = dipH;

        int normW = Math.Max(0, region.Width & ~1);
        int normH = Math.Max(0, region.Height & ~1);
        SizeBadgeText.Text = $"{normW} × {normH}";
        SizeBadge.Visibility = Wpf.Visibility.Visible;
        SizeBadge.UpdateLayout();

        double badgeTop = dipY - SizeBadge.ActualHeight - 6;
        if (badgeTop < 6)
        {
            badgeTop = dipY + 6;
        }
        double canvasW = GetCanvasWidth();
        double badgeLeft = Math.Clamp(dipX, 6, Math.Max(6, canvasW - SizeBadge.ActualWidth - 6));
        Canvas.SetLeft(SizeBadge, badgeLeft);
        Canvas.SetTop(SizeBadge, badgeTop);

        if (showHandlesAndToolbar)
        {
            SetHandlesVisibility(Wpf.Visibility.Visible);
            PositionHandles(dipX, dipY, dipW, dipH);
            PositionActionToolbar(dipX, dipY, dipW, dipH);
        }
        else
        {
            SetHandlesVisibility(Wpf.Visibility.Collapsed);
            ActionToolbar.Visibility = Wpf.Visibility.Collapsed;
        }
    }

    private void SetHandlesVisibility(Wpf.Visibility visibility)
    {
        foreach (Shapes.Rectangle h in _handles)
        {
            h.Visibility = visibility;
        }
    }

    private void PositionHandles(double x, double y, double w, double h)
    {
        double half = HandleSizeDip / 2.0;
        double midX = x + w / 2.0;
        double midY = y + h / 2.0;
        double right = x + w;
        double bottom = y + h;

        SetHandlePos(HandleTL, x - half, y - half);
        SetHandlePos(HandleT, midX - half, y - half);
        SetHandlePos(HandleTR, right - half, y - half);
        SetHandlePos(HandleR, right - half, midY - half);
        SetHandlePos(HandleBR, right - half, bottom - half);
        SetHandlePos(HandleB, midX - half, bottom - half);
        SetHandlePos(HandleBL, x - half, bottom - half);
        SetHandlePos(HandleL, x - half, midY - half);
    }

    private static void SetHandlePos(Shapes.Rectangle handle, double left, double top)
    {
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
    }

    private void PositionActionToolbar(double dipX, double dipY, double dipW, double dipH)
    {
        ActionToolbar.Visibility = Wpf.Visibility.Visible;
        ActionToolbar.UpdateLayout();

        double tbW = ActionToolbar.ActualWidth;
        double tbH = ActionToolbar.ActualHeight;
        double canvasW = GetCanvasWidth();
        double canvasH = GetCanvasHeight();

        double left = dipX + dipW - tbW;
        left = Math.Clamp(left, 8, Math.Max(8, canvasW - tbW - 8));

        double top = dipY + dipH + 8;
        if (top + tbH > canvasH - 8)
        {
            double aboveTop = dipY - tbH - 8;
            top = aboveTop >= 8
                ? aboveTop
                : Math.Max(8, dipY + dipH - tbH - 8);
        }

        Canvas.SetLeft(ActionToolbar, left);
        Canvas.SetTop(ActionToolbar, top);
    }

    private RegionHitZone HitTestEditRect(Win32Interop.POINT pt)
    {
        double dpiScale = Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        int tol = Math.Max(8, (int)Math.Round(9 * dpiScale));

        int left = _editRect.X;
        int top = _editRect.Y;
        int right = _editRect.X + _editRect.Width;
        int bottom = _editRect.Y + _editRect.Height;

        if (pt.X < left - tol || pt.X > right + tol || pt.Y < top - tol || pt.Y > bottom + tol)
        {
            return RegionHitZone.None;
        }

        bool nearLeft = Math.Abs(pt.X - left) <= tol;
        bool nearRight = Math.Abs(pt.X - right) <= tol;
        bool nearTop = Math.Abs(pt.Y - top) <= tol;
        bool nearBottom = Math.Abs(pt.Y - bottom) <= tol;

        if (nearTop && nearLeft) return RegionHitZone.TopLeft;
        if (nearTop && nearRight) return RegionHitZone.TopRight;
        if (nearBottom && nearLeft) return RegionHitZone.BottomLeft;
        if (nearBottom && nearRight) return RegionHitZone.BottomRight;
        if (nearTop) return RegionHitZone.Top;
        if (nearBottom) return RegionHitZone.Bottom;
        if (nearLeft) return RegionHitZone.Left;
        if (nearRight) return RegionHitZone.Right;

        if (pt.X >= left && pt.X <= right && pt.Y >= top && pt.Y <= bottom)
        {
            return RegionHitZone.Move;
        }

        return RegionHitZone.None;
    }

    private static Input.Cursor GetCursorForHitZone(RegionHitZone zone) => zone switch
    {
        RegionHitZone.TopLeft or RegionHitZone.BottomRight => Input.Cursors.SizeNWSE,
        RegionHitZone.TopRight or RegionHitZone.BottomLeft => Input.Cursors.SizeNESW,
        RegionHitZone.Top or RegionHitZone.Bottom => Input.Cursors.SizeNS,
        RegionHitZone.Left or RegionHitZone.Right => Input.Cursors.SizeWE,
        RegionHitZone.Move => Input.Cursors.SizeAll,
        _ => Input.Cursors.Arrow
    };

    private bool IsFromActionToolbar(Wpf.DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ActionToolbar))
            {
                return true;
            }
            source = Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private double GetCanvasWidth()
    {
        double w = RootCanvas.ActualWidth > 0 ? RootCanvas.ActualWidth : ActualWidth;
        return w > 0 ? w : 1920;
    }

    private double GetCanvasHeight()
    {
        double h = RootCanvas.ActualHeight > 0 ? RootCanvas.ActualHeight : ActualHeight;
        return h > 0 ? h : 1080;
    }

    private void UpdateOverlayGeometry(Wpf.Rect? hole)
    {
        double canvasW = RootCanvas.ActualWidth > 0 ? RootCanvas.ActualWidth : 10000;
        double canvasH = RootCanvas.ActualHeight > 0 ? RootCanvas.ActualHeight : 10000;

        var fullGeo = new Media.RectangleGeometry(new Wpf.Rect(0, 0, canvasW, canvasH));
        if (hole is { Width: > 0, Height: > 0 } h)
        {
            var selGeo = new Media.RectangleGeometry(h);
            DimOverlayPath.Data = new Media.CombinedGeometry(Media.GeometryCombineMode.Exclude, fullGeo, selGeo);
        }
        else
        {
            DimOverlayPath.Data = fullGeo;
        }
    }

    private static CaptureRegion ComputePhysicalRect(Win32Interop.POINT a, Win32Interop.POINT b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        int w = Math.Abs(b.X - a.X);
        int h = Math.Abs(b.Y - a.Y);
        return new CaptureRegion(x, y, w, h);
    }
}
