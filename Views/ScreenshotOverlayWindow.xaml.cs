using System.IO;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniRec.Models;
using MiniRec.Native;
using MiniRec.Services;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;

namespace MiniRec.Views;

internal enum ScreenshotOverlayOutcome
{
    Cancelled,
    Copied,
    Saved,
    Pinned,
    StartRecording,
    OpenOcrWindow
}

public partial class ScreenshotOverlayWindow : Wpf.Window
{
    private enum OverlayPhase
    {
        Hovering,
        Drawing,
        Editing
    }

    private const int MinSelectionPixels = 8;
    private const int DragThresholdPixels = 5;
    private const double HandleSizeDip = 8.0;
    private const int MagnifierPatchSize = 15;
    private const string DefaultHoverHint = "悬停自动吸附控件 · 滚轮切换层级 · 拖动框选 · C 复制色值 · Esc 取消";

    private static readonly Media.Color[] PaletteColors =
    [
        Media.Color.FromRgb(239, 68, 68),   // 红
        Media.Color.FromRgb(249, 115, 22),  // 橙
        Media.Color.FromRgb(234, 179, 8),   // 黄
        Media.Color.FromRgb(7, 193, 96),    // 微信绿
        Media.Color.FromRgb(59, 130, 246),  // 蓝
        Media.Color.FromRgb(168, 85, 247),  // 紫
        Media.Color.FromRgb(255, 255, 255), // 白
        Media.Color.FromRgb(15, 23, 42)     // 黑
    ];

    private readonly ScreenSnapshot _snapshot;
    private readonly UiElementDetector _detector;
    private readonly string _defaultSaveDir;
    private readonly Shapes.Rectangle[] _handles;
    private readonly Imaging.WriteableBitmap _magnifierBitmap;
    private readonly byte[] _magnifierBuffer = new byte[MagnifierPatchSize * MagnifierPatchSize * 4];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private readonly AnnotationEngine _annotationEngine = new();
    private readonly List<Controls.Border> _colorSwatchRings = [];

    private OverlayPhase _phase = OverlayPhase.Hovering;
    private bool _hasSource;
    private (int Vx, int Vy, int Vw, int Vh) _virtualBounds;
    private Media.Matrix _physToDip = Media.Matrix.Identity;
    private double _dipPerPhys = 1.0;

    private Win32Interop.POINT _dragStartPhys;
    private Win32Interop.POINT _lastCursorPhys;
    private List<CaptureRegion> _hoverCandidates = [];
    private int _candidateIndex;
    private CaptureRegion? _hoverCandidate;
    private CaptureRegion _editRect = new(0, 0, 0, 0);
    private CaptureRegion _dragStartRect = new(0, 0, 0, 0);
    private RegionHitZone _activeHitZone = RegionHitZone.None;
    private bool _isEditingDrag;
    private bool _useRgbColorFormat;

    // Phase 2 标注工作台状态
    private AnnotationToolType _activeTool = AnnotationToolType.None;
    private AnnotationStyle _currentStyle = new(Media.Color.FromRgb(239, 68, 68), AnnotationSizeLevel.Medium, MosaicMode.Pixel);
    private bool _isDrawingAnnotation;
    private AnnotationItem? _previewAnnotation;
    private Wpf.Point _annoStartPhys;

    private bool _isMovingAnnotation;
    private AnnotationItem? _movingAnnotation;
    private Wpf.Point _moveAnnoLastPhys;
    private double _moveAnnoTotalDx;
    private double _moveAnnoTotalDy;

    private bool _isEditingInlineText;
    private Wpf.Point _inlineTextPhysPos;
    private TextAnnotation? _editingExistingTextAnno;

    // Phase 3 离线 OCR 状态
    private bool _isOcrBusy;
    private bool _isOcrActive;
    private OcrDocumentResult? _ocrResult;
    private Wpf.Rect _lastSelDipRect;

    internal ScreenshotOverlayOutcome Outcome { get; private set; } = ScreenshotOverlayOutcome.Cancelled;
    internal CaptureRegion? SelectedRegion { get; private set; }
    internal Imaging.BitmapSource? ResultBitmap { get; private set; }
    internal string? SavedFilePath { get; private set; }
    internal OcrDocumentResult? OcrResult { get; private set; }

    internal ScreenshotOverlayWindow(
        ScreenSnapshot snapshot,
        IReadOnlyList<(IntPtr Hwnd, int X, int Y, int Width, int Height)> topWindows,
        string defaultSaveDir)
    {
        InitializeComponent();

        _snapshot = snapshot;
        _defaultSaveDir = defaultSaveDir;
        _virtualBounds = (snapshot.VirtualX, snapshot.VirtualY, snapshot.Width, snapshot.Height);
        _detector = new UiElementDetector(topWindows, _virtualBounds, Environment.ProcessId);
        _handles = [HandleTL, HandleT, HandleTR, HandleR, HandleBR, HandleB, HandleBL, HandleL];

        _magnifierBitmap = new Imaging.WriteableBitmap(
            MagnifierPatchSize,
            MagnifierPatchSize,
            96,
            96,
            Media.PixelFormats.Bgr32,
            null);
        MagnifierImage.Source = _magnifierBitmap;

        AnnotationLayer.Engine = _annotationEngine;
        AnnotationLayer.MosaicProvider = (mode, strength) =>
            _annotationEngine.GetOrCreateMosaicBitmap(_snapshot, _editRect, mode, strength);
        _annotationEngine.Changed += OnAnnotationEngineChanged;

        OcrSelectionLayer.SelectionCompleted += anchorDip => ShowOcrSelectionBubble(anchorDip);
        OcrSelectionLayer.SelectionCleared += () => OcrSelectionBubble.Visibility = Wpf.Visibility.Collapsed;

        BuildColorPalette();
        RefreshToolAndStyleUi();

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (_phase == OverlayPhase.Hovering)
            {
                HintPillText.Text = DefaultHoverHint;
                PositionHintPill();
            }
            else
            {
                HintPill.Visibility = Wpf.Visibility.Collapsed;
            }
        };
    }

    private void BuildColorPalette()
    {
        ColorPalettePanel.Children.Clear();
        _colorSwatchRings.Clear();

        foreach (Media.Color color in PaletteColors)
        {
            Media.Color captured = color;
            var ring = new Controls.Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new Wpf.CornerRadius(10),
                BorderThickness = new Wpf.Thickness(1.5),
                Margin = new Wpf.Thickness(2, 0, 2, 0),
                Cursor = Input.Cursors.Hand,
                Tag = captured,
                Child = new Controls.Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new Wpf.CornerRadius(7),
                    Background = new Media.SolidColorBrush(captured),
                    BorderBrush = new Media.SolidColorBrush(Media.Color.FromRgb(148, 163, 184)),
                    BorderThickness = new Wpf.Thickness(captured == Media.Colors.White ? 1.0 : 0.4),
                    HorizontalAlignment = Wpf.HorizontalAlignment.Center,
                    VerticalAlignment = Wpf.VerticalAlignment.Center
                }
            };

            ring.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _currentStyle = _currentStyle with { Color = captured };
                ApplyStyleChangeToSelectionOrInline();
                RefreshToolAndStyleUi();
            };

            _colorSwatchRings.Add(ring);
            ColorPalettePanel.Children.Add(ring);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
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
        Wpf.PresentationSource? source = Wpf.PresentationSource.FromVisual(this);
        _hasSource = source is not null;
        if (source?.CompositionTarget is not null)
        {
            _physToDip = source.CompositionTarget.TransformFromDevice;
            _dipPerPhys = _physToDip.M11 > 0.01 ? _physToDip.M11 : 1.0;
        }
        AnnotationLayer.PhysToDipMatrix = _physToDip;

        FrozenBgImage.Source = _snapshot.CreateFullBitmapSource();

        UpdateOverlayGeometry(null);
        PositionHintPill();
        Activate();
        Focus();

        if (Win32Interop.GetCursorPos(out Win32Interop.POINT pt))
        {
            _lastCursorPhys = pt;
            UpdateHoverAtCursor(pt, resetCandidateIndex: true);
            UpdateMagnifier(pt);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _toastTimer.Stop();
        _annotationEngine.Changed -= OnAnnotationEngineChanged;
        _detector.Dispose();
        base.OnClosed(e);
    }

    private void OnAnnotationEngineChanged()
    {
        AnnotationLayer.Refresh();
        RefreshToolAndStyleUi();
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

    private void FlashToast(string message)
    {
        _toastTimer.Stop();
        HintPillText.Text = message;
        HintPill.Visibility = Wpf.Visibility.Visible;
        PositionHintPill();
        _toastTimer.Start();
    }

    private void Window_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        if (_isEditingInlineText)
        {
            // Handled inside InlineTextBox_PreviewKeyDown unless Escape falls through.
            return;
        }

        Input.Key key = e.Key == Input.Key.System ? e.SystemKey : e.Key;

        if (key == Input.Key.Escape)
        {
            e.Handled = true;
            if (_isOcrActive)
            {
                ClearOcrUi();
                return;
            }
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
            Outcome = ScreenshotOverlayOutcome.Cancelled;
            DialogResult = false;
            Close();
            return;
        }

        if ((key == Input.Key.LeftShift || key == Input.Key.RightShift) &&
            _phase == OverlayPhase.Hovering &&
            (Input.Keyboard.Modifiers & ~Input.ModifierKeys.Shift) == 0)
        {
            _useRgbColorFormat = !_useRgbColorFormat;
            UpdateMagnifier(_lastCursorPhys);
            return;
        }

        bool ctrl = (Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0;
        bool shift = (Input.Keyboard.Modifiers & Input.ModifierKeys.Shift) != 0;

        if (key == Input.Key.C && !ctrl)
        {
            e.Handled = true;
            CopyColorUnderCursor();
            return;
        }

        if (_phase != OverlayPhase.Editing) return;

        if (_isOcrActive && ctrl && key == Input.Key.A)
        {
            e.Handled = true;
            OcrSelectionLayer.SelectAll();
            return;
        }

        if (_isOcrActive && ctrl && key == Input.Key.C)
        {
            e.Handled = true;
            if (OcrSelectionLayer.HasSelection)
            {
                CopyOcrSelection(joinLines: false);
            }
            else if (_ocrResult is { Success: true } && !string.IsNullOrWhiteSpace(_ocrResult.FullText))
            {
                Wpf.Clipboard.SetText(_ocrResult.FullText);
                FlashToast("✓ 已复制全部识别文字");
            }
            return;
        }

        if (ctrl && key == Input.Key.Z)
        {
            e.Handled = true;
            if (shift)
            {
                _annotationEngine.Redo();
            }
            else
            {
                _annotationEngine.Undo();
            }
            return;
        }

        if (ctrl && key == Input.Key.Y)
        {
            e.Handled = true;
            _annotationEngine.Redo();
            return;
        }

        if ((key == Input.Key.Delete || key == Input.Key.Back) && _annotationEngine.SelectedItem is not null)
        {
            e.Handled = true;
            _annotationEngine.Delete(_annotationEngine.SelectedItem);
            return;
        }

        if (!ctrl && key is >= Input.Key.D1 and <= Input.Key.D8)
        {
            e.Handled = true;
            AnnotationToolType picked = (key - Input.Key.D1) switch
            {
                0 => AnnotationToolType.Rectangle,
                1 => AnnotationToolType.Ellipse,
                2 => AnnotationToolType.Arrow,
                3 => AnnotationToolType.Pen,
                4 => AnnotationToolType.Highlighter,
                5 => AnnotationToolType.NumberBadge,
                6 => AnnotationToolType.Mosaic,
                7 => AnnotationToolType.Text,
                _ => AnnotationToolType.None
            };
            ToggleActiveTool(picked);
            return;
        }

        if (!ctrl && key == Input.Key.O)
        {
            e.Handled = true;
            _ = RunOcrOnSelectionAsync();
            return;
        }

        if (key == Input.Key.Enter || (ctrl && key == Input.Key.C))
        {
            e.Handled = true;
            CompleteCopy();
            return;
        }

        if (ctrl && key == Input.Key.S)
        {
            e.Handled = true;
            CompleteSave();
            return;
        }

        int step = shift ? 10 : 1;
        int dx = 0;
        int dy = 0;
        switch (key)
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
        _annotationEngine.InvalidateMosaicCache();
        ClearOcrUi();
        RenderRegion(_editRect, showHandlesAndToolbar: true);
    }

    private void Window_PreviewMouseWheel(object sender, Input.MouseWheelEventArgs e)
    {
        if (_phase != OverlayPhase.Hovering || _hoverCandidates.Count <= 1) return;

        e.Handled = true;
        if (e.Delta > 0)
        {
            _candidateIndex = Math.Min(_hoverCandidates.Count - 1, _candidateIndex + 1);
        }
        else if (e.Delta < 0)
        {
            _candidateIndex = Math.Max(0, _candidateIndex - 1);
        }

        _hoverCandidate = _hoverCandidates[_candidateIndex];
        RenderRegion(_hoverCandidate, showHandlesAndToolbar: false);
    }

    private void Window_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as Wpf.DependencyObject;
        if (IsFromUiChrome(dep))
        {
            return;
        }

        if (_isEditingInlineText)
        {
            CommitInlineText();
        }

        if (!Win32Interop.GetCursorPos(out Win32Interop.POINT pt)) return;
        _lastCursorPhys = pt;

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
            if (hit == RegionHitZone.None)
            {
                return;
            }

            // 8 个手柄或边框优先允许微调选区边界
            if (hit != RegionHitZone.Move)
            {
                _annotationEngine.SelectedItem = null;
                AnnotationLayer.Refresh();

                _activeHitZone = hit;
                _dragStartPhys = pt;
                _dragStartRect = _editRect;
                _isEditingDrag = true;
                CaptureMouse();
                UpdateMagnifier(pt);
                return;
            }

            // 点在选区内部：检查当前是否激活绘制工具
            var physPt = new Wpf.Point(pt.X, pt.Y);
            if (_activeTool != AnnotationToolType.None)
            {
                HandleAnnotationMouseDown(physPt);
                return;
            }

            // 未激活工具时：先检测是否命中已有标注
            double hitTol = Math.Max(8.0, 8.0 / _dipPerPhys);
            AnnotationItem? hitAnno = _annotationEngine.HitTestTopmost(physPt, hitTol);
            if (hitAnno is not null)
            {
                _annotationEngine.SelectedItem = hitAnno;
                RefreshToolAndStyleUi();
                AnnotationLayer.Refresh();

                if (hitAnno is TextAnnotation textAnno && e.ClickCount == 2)
                {
                    BeginInlineTextEdit(textAnno.Position, textAnno);
                    return;
                }

                _isMovingAnnotation = true;
                _movingAnnotation = hitAnno;
                _moveAnnoLastPhys = physPt;
                _moveAnnoTotalDx = 0;
                _moveAnnoTotalDy = 0;
                CaptureMouse();
                return;
            }

            if (_annotationEngine.SelectedItem is not null)
            {
                _annotationEngine.SelectedItem = null;
                RefreshToolAndStyleUi();
                AnnotationLayer.Refresh();
            }

            if (e.ClickCount == 2)
            {
                CompleteCopy();
                return;
            }

            _activeHitZone = RegionHitZone.Move;
            _dragStartPhys = pt;
            _dragStartRect = _editRect;
            _isEditingDrag = true;
            CaptureMouse();
        }
    }

    private void HandleAnnotationMouseDown(Wpf.Point physPt)
    {
        if (_activeTool == AnnotationToolType.NumberBadge)
        {
            var badge = new NumberBadgeAnnotation(_currentStyle, physPt, _annotationEngine.NextBadgeNumber());
            _annotationEngine.Add(badge);
            return;
        }

        if (_activeTool == AnnotationToolType.Text)
        {
            BeginInlineTextEdit(physPt, existing: null);
            return;
        }

        _annotationEngine.SelectedItem = null;
        _annoStartPhys = physPt;
        _isDrawingAnnotation = true;

        _previewAnnotation = _activeTool switch
        {
            AnnotationToolType.Rectangle => new RectAnnotation(_currentStyle, physPt, physPt),
            AnnotationToolType.Ellipse => new EllipseAnnotation(_currentStyle, physPt, physPt),
            AnnotationToolType.Arrow => new ArrowAnnotation(_currentStyle, physPt, physPt),
            AnnotationToolType.Pen => new StrokeAnnotation(_currentStyle, isHighlighter: false, [physPt]),
            AnnotationToolType.Highlighter => new StrokeAnnotation(_currentStyle, isHighlighter: true, [physPt]),
            AnnotationToolType.Mosaic => new MosaicAnnotation(
                _currentStyle,
                _currentStyle.MosaicMode,
                _currentStyle.MosaicMode == MosaicMode.Blur ? _currentStyle.MosaicBlurRadius : _currentStyle.MosaicBlockSize,
                _currentStyle.MosaicBrushWidth,
                [physPt]),
            _ => null
        };

        AnnotationLayer.PreviewItem = _previewAnnotation;
        AnnotationLayer.Refresh();
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, Input.MouseEventArgs e)
    {
        if (!Win32Interop.GetCursorPos(out Win32Interop.POINT pt)) return;
        _lastCursorPhys = pt;

        if (_phase == OverlayPhase.Hovering)
        {
            Cursor = Input.Cursors.Cross;
            UpdateHoverAtCursor(pt, resetCandidateIndex: true);
            UpdateMagnifier(pt);
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
            UpdateMagnifier(pt);
            return;
        }

        if (_phase == OverlayPhase.Editing)
        {
            if (_isDrawingAnnotation && _previewAnnotation is not null)
            {
                UpdatePreviewAnnotation(new Wpf.Point(pt.X, pt.Y));
                return;
            }

            if (_isMovingAnnotation && _movingAnnotation is not null)
            {
                double dx = pt.X - _moveAnnoLastPhys.X;
                double dy = pt.Y - _moveAnnoLastPhys.Y;
                if (Math.Abs(dx) > 0 || Math.Abs(dy) > 0)
                {
                    _movingAnnotation.Translate(dx, dy);
                    _moveAnnoTotalDx += dx;
                    _moveAnnoTotalDy += dy;
                    _moveAnnoLastPhys = new Wpf.Point(pt.X, pt.Y);
                    AnnotationLayer.Refresh();
                }
                return;
            }

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

                _annotationEngine.InvalidateMosaicCache();
                if (_isOcrActive) ClearOcrUi();
                RenderRegion(_editRect, showHandlesAndToolbar: true);
                if (_activeHitZone != RegionHitZone.Move)
                {
                    UpdateMagnifier(pt);
                }
            }
            else if (IsFromUiChrome(e.OriginalSource as Wpf.DependencyObject))
            {
                Cursor = Input.Cursors.Arrow;
            }
            else
            {
                UpdateEditingHoverCursor(pt);
            }
        }
    }

    private void UpdatePreviewAnnotation(Wpf.Point curPhys)
    {
        bool shift = (Input.Keyboard.Modifiers & Input.ModifierKeys.Shift) != 0;
        switch (_previewAnnotation)
        {
            case RectAnnotation rect:
                rect.End = shift ? ConstrainSquare(_annoStartPhys, curPhys) : curPhys;
                break;
            case EllipseAnnotation ellipse:
                ellipse.End = shift ? ConstrainSquare(_annoStartPhys, curPhys) : curPhys;
                break;
            case ArrowAnnotation arrow:
                arrow.End = curPhys;
                break;
            case StrokeAnnotation stroke:
                AppendPointIfMoved(stroke.Points, curPhys);
                break;
            case MosaicAnnotation mosaic:
                AppendPointIfMoved(mosaic.Points, curPhys);
                break;
        }

        AnnotationLayer.Refresh();
    }

    private static void AppendPointIfMoved(List<Wpf.Point> pts, Wpf.Point pt)
    {
        if (pts.Count == 0)
        {
            pts.Add(pt);
            return;
        }
        Wpf.Point last = pts[^1];
        double dx = pt.X - last.X;
        double dy = pt.Y - last.Y;
        if (dx * dx + dy * dy >= 9.0) // >= 3px
        {
            pts.Add(pt);
        }
    }

    private static Wpf.Point ConstrainSquare(Wpf.Point start, Wpf.Point cur)
    {
        double dx = cur.X - start.X;
        double dy = cur.Y - start.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Wpf.Point(
            start.X + (dx >= 0 ? side : -side),
            start.Y + (dy >= 0 ? side : -side));
    }

    private void UpdateEditingHoverCursor(Win32Interop.POINT pt)
    {
        RegionHitZone hit = HitTestEditRect(pt);
        if (hit == RegionHitZone.None)
        {
            Cursor = Input.Cursors.Arrow;
            return;
        }

        if (hit != RegionHitZone.Move)
        {
            Cursor = GetCursorForHitZone(hit);
            return;
        }

        if (_activeTool == AnnotationToolType.Text)
        {
            Cursor = Input.Cursors.IBeam;
        }
        else if (_activeTool != AnnotationToolType.None)
        {
            Cursor = Input.Cursors.Cross;
        }
        else
        {
            double hitTol = Math.Max(8.0, 8.0 / _dipPerPhys);
            AnnotationItem? anno = _annotationEngine.HitTestTopmost(new Wpf.Point(pt.X, pt.Y), hitTol);
            Cursor = anno is not null ? Input.Cursors.Hand : Input.Cursors.SizeAll;
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
            _lastCursorPhys = endPt;

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
                    EnterHovering("点击窗口/控件或拖动至少 8×8 的区域");
                }
                return;
            }

            CaptureRegion drawn = ComputePhysicalRect(_dragStartPhys, endPt);
            if (drawn.Width < MinSelectionPixels || drawn.Height < MinSelectionPixels)
            {
                EnterHovering("选区太小（至少 8×8），请重新拖动");
                return;
            }

            EnterEditing(drawn);
            return;
        }

        if (_phase == OverlayPhase.Editing && _isDrawingAnnotation)
        {
            _isDrawingAnnotation = false;
            if (IsMouseCaptured) ReleaseMouseCapture();

            AnnotationItem? built = _previewAnnotation;
            _previewAnnotation = null;
            AnnotationLayer.PreviewItem = null;

            if (built is not null && IsValidAnnotation(built))
            {
                _annotationEngine.Add(built);
            }
            else
            {
                AnnotationLayer.Refresh();
            }
            return;
        }

        if (_phase == OverlayPhase.Editing && _isMovingAnnotation)
        {
            _isMovingAnnotation = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (_movingAnnotation is not null)
            {
                _annotationEngine.RecordMove(_movingAnnotation, _moveAnnoTotalDx, _moveAnnoTotalDy);
                _movingAnnotation = null;
            }
            return;
        }

        if (_phase == OverlayPhase.Editing && _isEditingDrag)
        {
            _isEditingDrag = false;
            _activeHitZone = RegionHitZone.None;
            MagnifierPanel.Visibility = Wpf.Visibility.Collapsed;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
    }

    private static bool IsValidAnnotation(AnnotationItem item) => item switch
    {
        RectAnnotation r => r.GetPhysicalBounds() is { Width: >= 4, Height: >= 4 },
        EllipseAnnotation e => e.GetPhysicalBounds() is { Width: >= 4, Height: >= 4 },
        ArrowAnnotation a => IsLongEnough(a.Start, a.End, 6.0),
        StrokeAnnotation => true,
        MosaicAnnotation => true,
        _ => true
    };

    private static bool IsLongEnough(Wpf.Point a, Wpf.Point b, double minLen)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return dx * dx + dy * dy >= minLen * minLen;
    }

    private void Window_MouseRightButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (_phase == OverlayPhase.Drawing)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();
            EnterHovering(DefaultHoverHint);
            return;
        }

        if (_phase == OverlayPhase.Editing)
        {
            if (_isEditingInlineText)
            {
                CancelInlineText();
                return;
            }

            if (_isDrawingAnnotation)
            {
                _isDrawingAnnotation = false;
                _previewAnnotation = null;
                AnnotationLayer.PreviewItem = null;
                if (IsMouseCaptured) ReleaseMouseCapture();
                AnnotationLayer.Refresh();
                return;
            }

            if (_isEditingDrag)
            {
                _isEditingDrag = false;
                _activeHitZone = RegionHitZone.None;
                MagnifierPanel.Visibility = Wpf.Visibility.Collapsed;
                if (IsMouseCaptured) ReleaseMouseCapture();
                _editRect = _dragStartRect;
                _annotationEngine.InvalidateMosaicCache();
                RenderRegion(_editRect, showHandlesAndToolbar: true);
                return;
            }

            if (_isOcrActive)
            {
                ClearOcrUi();
                return;
            }

            if (_activeTool != AnnotationToolType.None)
            {
                _activeTool = AnnotationToolType.None;
                RefreshToolAndStyleUi();
                return;
            }

            if (_annotationEngine.Undo())
            {
                FlashToast("↶ 已撤销上一步标注");
                return;
            }

            EnterHovering(DefaultHoverHint);
            return;
        }

        Outcome = ScreenshotOverlayOutcome.Cancelled;
        DialogResult = false;
        Close();
    }

    private void BeginInlineTextEdit(Wpf.Point physPos, TextAnnotation? existing)
    {
        _isEditingInlineText = true;
        _editingExistingTextAnno = existing;
        _inlineTextPhysPos = physPos;

        AnnotationStyle styleToUse = existing?.Style ?? _currentStyle;
        InlineTextBox.Foreground = new Media.SolidColorBrush(styleToUse.Color);
        InlineTextBox.FontSize = Math.Max(11.0, styleToUse.FontSize * _dipPerPhys);
        InlineTextBox.Text = existing?.Text ?? string.Empty;

        Wpf.Point dipPos = _physToDip.Transform(physPos);
        Canvas.SetLeft(InlineTextBoxContainer, dipPos.X);
        Canvas.SetTop(InlineTextBoxContainer, dipPos.Y);
        InlineTextBoxContainer.Visibility = Wpf.Visibility.Visible;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            InlineTextBox.Focus();
            InlineTextBox.CaretIndex = InlineTextBox.Text.Length;
        });
    }

    private void CommitInlineText()
    {
        if (!_isEditingInlineText) return;
        _isEditingInlineText = false;

        string text = InlineTextBox.Text.TrimEnd('\r', '\n');
        InlineTextBoxContainer.Visibility = Wpf.Visibility.Collapsed;

        TextAnnotation? existing = _editingExistingTextAnno;
        _editingExistingTextAnno = null;

        if (existing is not null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _annotationEngine.Delete(existing);
            }
            else
            {
                _annotationEngine.UpdateText(existing, existing.Text, text);
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            var anno = new TextAnnotation(_currentStyle, _inlineTextPhysPos, text);
            _annotationEngine.Add(anno);
        }

        Focus();
    }

    private void CancelInlineText()
    {
        _isEditingInlineText = false;
        _editingExistingTextAnno = null;
        InlineTextBoxContainer.Visibility = Wpf.Visibility.Collapsed;
        Focus();
    }

    private void InlineTextBox_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        if (e.Key == Input.Key.Escape)
        {
            e.Handled = true;
            CancelInlineText();
        }
        else if (e.Key == Input.Key.Enter && (Input.Keyboard.Modifiers & Input.ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            CommitInlineText();
        }
    }

    private void InlineTextBox_LostKeyboardFocus(object sender, Input.KeyboardFocusChangedEventArgs e)
    {
        if (_isEditingInlineText)
        {
            CommitInlineText();
        }
    }

    private void ToolButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (sender is not Controls.Button btn) return;

        AnnotationToolType tool = btn.Name switch
        {
            nameof(RectToolBtn) => AnnotationToolType.Rectangle,
            nameof(EllipseToolBtn) => AnnotationToolType.Ellipse,
            nameof(ArrowToolBtn) => AnnotationToolType.Arrow,
            nameof(PenToolBtn) => AnnotationToolType.Pen,
            nameof(HighlighterToolBtn) => AnnotationToolType.Highlighter,
            nameof(NumberToolBtn) => AnnotationToolType.NumberBadge,
            nameof(MosaicToolBtn) => AnnotationToolType.Mosaic,
            nameof(TextToolBtn) => AnnotationToolType.Text,
            _ => AnnotationToolType.None
        };

        ToggleActiveTool(tool);
    }

    private void ToggleActiveTool(AnnotationToolType tool)
    {
        if (_isEditingInlineText)
        {
            CommitInlineText();
        }
        if (_isOcrActive)
        {
            ClearOcrUi();
        }

        _activeTool = _activeTool == tool ? AnnotationToolType.None : tool;
        RefreshToolAndStyleUi();
        PositionStyleSubBar();
    }

    private void UndoButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_isEditingInlineText) CommitInlineText();
        _annotationEngine.Undo();
    }

    private void RedoButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_isEditingInlineText) CommitInlineText();
        _annotationEngine.Redo();
    }

    private void DeleteAnnoButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_annotationEngine.SelectedItem is not null)
        {
            _annotationEngine.Delete(_annotationEngine.SelectedItem);
        }
    }

    private void SizeSmallBtn_Click(object sender, Wpf.RoutedEventArgs e) => SetSizeLevel(AnnotationSizeLevel.Small);
    private void SizeMediumBtn_Click(object sender, Wpf.RoutedEventArgs e) => SetSizeLevel(AnnotationSizeLevel.Medium);
    private void SizeLargeBtn_Click(object sender, Wpf.RoutedEventArgs e) => SetSizeLevel(AnnotationSizeLevel.Large);

    private void SetSizeLevel(AnnotationSizeLevel level)
    {
        _currentStyle = _currentStyle with { SizeLevel = level };
        ApplyStyleChangeToSelectionOrInline();
        RefreshToolAndStyleUi();
    }

    private void MosaicPixelBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _currentStyle = _currentStyle with { MosaicMode = MosaicMode.Pixel };
        RefreshToolAndStyleUi();
    }

    private void MosaicBlurBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _currentStyle = _currentStyle with { MosaicMode = MosaicMode.Blur };
        RefreshToolAndStyleUi();
    }

    private void ApplyStyleChangeToSelectionOrInline()
    {
        if (_isEditingInlineText)
        {
            InlineTextBox.Foreground = new Media.SolidColorBrush(_currentStyle.Color);
            InlineTextBox.FontSize = Math.Max(11.0, _currentStyle.FontSize * _dipPerPhys);
        }

        if (_annotationEngine.SelectedItem is not null &&
            _annotationEngine.SelectedItem is not MosaicAnnotation)
        {
            _annotationEngine.SelectedItem.Style = _currentStyle;
            AnnotationLayer.Refresh();
        }
    }

    private void RefreshToolAndStyleUi()
    {
        RectToolBtn.Tag = _activeTool == AnnotationToolType.Rectangle ? "Active" : null;
        EllipseToolBtn.Tag = _activeTool == AnnotationToolType.Ellipse ? "Active" : null;
        ArrowToolBtn.Tag = _activeTool == AnnotationToolType.Arrow ? "Active" : null;
        PenToolBtn.Tag = _activeTool == AnnotationToolType.Pen ? "Active" : null;
        HighlighterToolBtn.Tag = _activeTool == AnnotationToolType.Highlighter ? "Active" : null;
        NumberToolBtn.Tag = _activeTool == AnnotationToolType.NumberBadge ? "Active" : null;
        MosaicToolBtn.Tag = _activeTool == AnnotationToolType.Mosaic ? "Active" : null;
        TextToolBtn.Tag = _activeTool == AnnotationToolType.Text ? "Active" : null;

        UndoBtn.IsEnabled = _annotationEngine.CanUndo;
        RedoBtn.IsEnabled = _annotationEngine.CanRedo;
        DeleteAnnoBtn.IsEnabled = _annotationEngine.SelectedItem is not null;

        SizeSmallBtn.Tag = _currentStyle.SizeLevel == AnnotationSizeLevel.Small ? "Active" : null;
        SizeMediumBtn.Tag = _currentStyle.SizeLevel == AnnotationSizeLevel.Medium ? "Active" : null;
        SizeLargeBtn.Tag = _currentStyle.SizeLevel == AnnotationSizeLevel.Large ? "Active" : null;

        MosaicPixelBtn.Tag = _currentStyle.MosaicMode == MosaicMode.Pixel ? "Active" : null;
        MosaicBlurBtn.Tag = _currentStyle.MosaicMode == MosaicMode.Blur ? "Active" : null;

        bool isMosaic = _activeTool == AnnotationToolType.Mosaic;
        ColorPalettePanel.Visibility = isMosaic ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        MosaicModePanel.Visibility = isMosaic ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;

        foreach (Controls.Border ring in _colorSwatchRings)
        {
            bool selected = ring.Tag is Media.Color c && c == _currentStyle.Color;
            ring.BorderBrush = selected
                ? new Media.SolidColorBrush(Media.Color.FromRgb(7, 193, 96))
                : Media.Brushes.Transparent;
        }

        if (_phase == OverlayPhase.Editing && _activeTool != AnnotationToolType.None)
        {
            StyleSubBar.Visibility = Wpf.Visibility.Visible;
        }
        else
        {
            StyleSubBar.Visibility = Wpf.Visibility.Collapsed;
        }
    }

    private async void OcrButton_Click(object sender, Wpf.RoutedEventArgs e) => await RunOcrOnSelectionAsync();
    private void CloseOcrBtn_Click(object sender, Wpf.RoutedEventArgs e) => ClearOcrUi();
    private void OcrBubbleCopyBtn_Click(object sender, Wpf.RoutedEventArgs e) => CopyOcrSelection(joinLines: false);
    private void OcrBubbleJoinCopyBtn_Click(object sender, Wpf.RoutedEventArgs e) => CopyOcrSelection(joinLines: true);
    private void OcrSelectAllBtn_Click(object sender, Wpf.RoutedEventArgs e) => OcrSelectionLayer.SelectAll();

    private void CopyOcrSelection(bool joinLines)
    {
        string selected = OcrSelectionLayer.GetSelectedText(joinLines);
        if (string.IsNullOrWhiteSpace(selected)) return;

        Wpf.Clipboard.SetText(selected);
        OcrSelectionBubble.Visibility = Wpf.Visibility.Collapsed;
        FlashToast(joinLines ? "✓ 已去换行复制所选文字" : "✓ 已复制所选文字");
    }

    private void OcrCopyAllBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_ocrResult is { Success: true } && !string.IsNullOrWhiteSpace(_ocrResult.FullText))
        {
            Wpf.Clipboard.SetText(_ocrResult.FullText);
            FlashToast("✓ 已复制全部识别文字");
        }
    }

    private void OcrOpenEditorBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_ocrResult is null) return;
        OcrResult = _ocrResult;
        SelectedRegion = _editRect;
        Outcome = ScreenshotOverlayOutcome.OpenOcrWindow;
        DialogResult = true;
        Close();
    }

    private async Task RunOcrOnSelectionAsync()
    {
        if (_phase != OverlayPhase.Editing || _isOcrBusy) return;

        if (_isOcrActive)
        {
            ClearOcrUi();
            return;
        }

        if (_isEditingInlineText) CommitInlineText();
        _activeTool = AnnotationToolType.None;
        RefreshToolAndStyleUi();

        _isOcrBusy = true;
        OcrBtn.IsEnabled = false;
        FlashToast("正在离线识别文字...");

        try
        {
            byte[] bgra = _snapshot.CropRegionBgraBytes(_editRect, out int outW, out int outH);
            OcrDocumentResult result = await NativeOcrService.RecognizeBgraAsync(bgra, outW, outH);
            if (!IsLoaded || _phase != OverlayPhase.Editing) return;

            _ocrResult = result;
            _isOcrActive = true;
            OcrBtn.Tag = "Active";
            OcrSelectionBubble.Visibility = Wpf.Visibility.Collapsed;

            if (result.Success && result.Lines.Count > 0)
            {
                OcrSelectionLayer.SetOcrData(result.Lines, _editRect, _physToDip, _lastSelDipRect);
                OcrSelectionLayer.Visibility = Wpf.Visibility.Visible;
                OcrBarStatusText.Text = $"已识别 {result.Lines.Count} 行 · 拖动原图划词 / 双击选行";
                OcrSelectAllBtn.IsEnabled = true;
                OcrCopyAllBtn.IsEnabled = true;
                OcrOpenEditorBtn.IsEnabled = true;
                FlashToast($"✓ 已识别 {result.Lines.Count} 行，直接在图上拖动即可局部划词");
            }
            else
            {
                OcrSelectionLayer.ClearData();
                OcrSelectionLayer.Visibility = Wpf.Visibility.Collapsed;
                OcrBarStatusText.Text = result.Success ? "未检测到清晰文字" : (result.ErrorMessage ?? "识别失败");
                OcrSelectAllBtn.IsEnabled = false;
                OcrCopyAllBtn.IsEnabled = false;
                OcrOpenEditorBtn.IsEnabled = !result.Success;
                FlashToast(result.Success ? "未检测到清晰文字" : (result.ErrorMessage ?? "OCR 识别失败"));
            }

            OcrActionBar.Visibility = Wpf.Visibility.Visible;
            PositionOcrActionBar();
        }
        finally
        {
            _isOcrBusy = false;
            OcrBtn.IsEnabled = true;
        }
    }

    private void ShowOcrSelectionBubble(Wpf.Rect anchorDip)
    {
        if (!_isOcrActive || !OcrSelectionLayer.HasSelection) return;

        OcrSelectionBubble.Visibility = Wpf.Visibility.Visible;
        OcrSelectionBubble.UpdateLayout();

        double bw = OcrSelectionBubble.ActualWidth > 0 ? OcrSelectionBubble.ActualWidth : 150;
        double bh = OcrSelectionBubble.ActualHeight > 0 ? OcrSelectionBubble.ActualHeight : 32;
        double canvasW = GetCanvasWidth();
        double canvasH = GetCanvasHeight();

        double left = Math.Clamp(anchorDip.Right - bw * 0.4, 8, Math.Max(8, canvasW - bw - 8));
        double top = anchorDip.Bottom + 6;
        if (top + bh > canvasH - 8)
        {
            top = Math.Max(8, anchorDip.Top - bh - 6);
        }

        Canvas.SetLeft(OcrSelectionBubble, left);
        Canvas.SetTop(OcrSelectionBubble, top);
    }

    private void PositionOcrActionBar()
    {
        if (OcrActionBar.Visibility != Wpf.Visibility.Visible || ActionToolbar.Visibility != Wpf.Visibility.Visible)
        {
            return;
        }

        OcrActionBar.UpdateLayout();
        double barW = OcrActionBar.ActualWidth;
        double barH = OcrActionBar.ActualHeight;
        double tbLeft = Canvas.GetLeft(ActionToolbar);
        double tbTop = Canvas.GetTop(ActionToolbar);
        double tbH = ActionToolbar.ActualHeight;
        double canvasW = GetCanvasWidth();
        double canvasH = GetCanvasHeight();

        double left = Math.Clamp(tbLeft, 8, Math.Max(8, canvasW - barW - 8));
        double top = tbTop + tbH + 5;
        if (top + barH > canvasH - 8)
        {
            top = Math.Max(8, tbTop - barH - 5);
        }

        Canvas.SetLeft(OcrActionBar, left);
        Canvas.SetTop(OcrActionBar, top);
    }

    private void ClearOcrUi()
    {
        _isOcrActive = false;
        _ocrResult = null;
        OcrBtn.Tag = null;
        OcrSelectionLayer.ClearData();
        OcrSelectionLayer.Visibility = Wpf.Visibility.Collapsed;
        OcrSelectionBubble.Visibility = Wpf.Visibility.Collapsed;
        OcrActionBar.Visibility = Wpf.Visibility.Collapsed;
    }

    private void PinButton_Click(object sender, Wpf.RoutedEventArgs e) => CompletePin();
    private void RecordNowButton_Click(object sender, Wpf.RoutedEventArgs e) => CompleteStartRecording();
    private void ReselectButton_Click(object sender, Wpf.RoutedEventArgs e) => EnterHovering(DefaultHoverHint);
    private void SaveButton_Click(object sender, Wpf.RoutedEventArgs e) => CompleteSave();
    private void ConfirmButton_Click(object sender, Wpf.RoutedEventArgs e) => CompleteCopy();

    private void CancelButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Outcome = ScreenshotOverlayOutcome.Cancelled;
        DialogResult = false;
        Close();
    }

    private Imaging.BitmapSource RenderFinalSelectionBitmap()
    {
        if (_isEditingInlineText)
        {
            CommitInlineText();
        }
        return _annotationEngine.RenderCompositeBitmap(_snapshot, _editRect);
    }

    private void CompleteCopy()
    {
        if (_phase != OverlayPhase.Editing) return;

        Imaging.BitmapSource bitmap = RenderFinalSelectionBitmap();
        Wpf.Clipboard.SetImage(bitmap);
        SelectedRegion = _editRect;
        ResultBitmap = bitmap;
        Outcome = ScreenshotOverlayOutcome.Copied;
        DialogResult = true;
        Close();
    }

    private void CompleteSave()
    {
        if (_phase != OverlayPhase.Editing) return;

        Imaging.BitmapSource bitmap = RenderFinalSelectionBitmap();
        Directory.CreateDirectory(_defaultSaveDir);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = _defaultSaveDir,
            FileName = $"Shot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var encoder = new Imaging.PngBitmapEncoder();
        encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
        using (FileStream fs = File.Create(dialog.FileName))
        {
            encoder.Save(fs);
        }

        SelectedRegion = _editRect;
        ResultBitmap = bitmap;
        SavedFilePath = dialog.FileName;
        Outcome = ScreenshotOverlayOutcome.Saved;
        DialogResult = true;
        Close();
    }

    private void CompletePin()
    {
        if (_phase != OverlayPhase.Editing) return;

        SelectedRegion = _editRect;
        ResultBitmap = RenderFinalSelectionBitmap();
        Outcome = ScreenshotOverlayOutcome.Pinned;
        DialogResult = true;
        Close();
    }

    private void CompleteStartRecording()
    {
        if (_phase != OverlayPhase.Editing) return;

        SelectedRegion = _editRect;
        Outcome = ScreenshotOverlayOutcome.StartRecording;
        DialogResult = true;
        Close();
    }

    private void CopyColorUnderCursor()
    {
        if (!_snapshot.TryGetPixel(_lastCursorPhys.X, _lastCursorPhys.Y, out byte r, out byte g, out byte b))
        {
            return;
        }

        string text = _useRgbColorFormat
            ? $"rgb({r}, {g}, {b})"
            : $"#{r:X2}{g:X2}{b:X2}";
        Wpf.Clipboard.SetText(text);
        FlashToast($"✓ 已复制色值 {text}");
    }

    private void EnterHovering(string hintMessage)
    {
        _toastTimer.Stop();
        if (_isEditingInlineText) CancelInlineText();
        ClearOcrUi();
        _annotationEngine.Clear();
        _activeTool = AnnotationToolType.None;
        _isDrawingAnnotation = false;
        _isMovingAnnotation = false;
        _previewAnnotation = null;
        AnnotationLayer.PreviewItem = null;
        AnnotationLayer.ClipDipRect = Wpf.Rect.Empty;
        AnnotationLayer.Refresh();

        _phase = OverlayPhase.Hovering;
        _isEditingDrag = false;
        _activeHitZone = RegionHitZone.None;
        _hoverCandidate = null;
        _hoverCandidates = [];
        _candidateIndex = 0;

        Cursor = Input.Cursors.Cross;
        ActionToolbar.Visibility = Wpf.Visibility.Collapsed;
        StyleSubBar.Visibility = Wpf.Visibility.Collapsed;
        SetHandlesVisibility(Wpf.Visibility.Collapsed);
        SelectionBorder.Visibility = Wpf.Visibility.Collapsed;
        SizeBadge.Visibility = Wpf.Visibility.Collapsed;

        HintPillText.Text = hintMessage;
        HintPill.Visibility = Wpf.Visibility.Visible;
        PositionHintPill();
        UpdateOverlayGeometry(null);

        if (Win32Interop.GetCursorPos(out Win32Interop.POINT pt))
        {
            _lastCursorPhys = pt;
            UpdateHoverAtCursor(pt, resetCandidateIndex: true);
            UpdateMagnifier(pt);
        }
    }

    private void EnterEditing(CaptureRegion rect)
    {
        _toastTimer.Stop();
        _phase = OverlayPhase.Editing;
        _isEditingDrag = false;
        _activeHitZone = RegionHitZone.None;
        _hoverCandidate = null;
        _editRect = rect;
        _annotationEngine.InvalidateMosaicCache();

        MagnifierPanel.Visibility = Wpf.Visibility.Collapsed;
        HintPill.Visibility = Wpf.Visibility.Collapsed;
        RefreshToolAndStyleUi();
        RenderRegion(_editRect, showHandlesAndToolbar: true);

        if (Win32Interop.GetCursorPos(out Win32Interop.POINT pt))
        {
            _lastCursorPhys = pt;
            UpdateEditingHoverCursor(pt);
        }
    }

    private void UpdateHoverAtCursor(Win32Interop.POINT pt, bool resetCandidateIndex)
    {
        _hoverCandidates = _detector.GetCandidateRegionsAt(pt.X, pt.Y, () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_phase == OverlayPhase.Hovering && IsLoaded)
                {
                    UpdateHoverAtCursor(_lastCursorPhys, resetCandidateIndex: true);
                }
            });
        });

        if (_hoverCandidates.Count == 0)
        {
            _hoverCandidate = null;
            SelectionBorder.Visibility = Wpf.Visibility.Collapsed;
            SizeBadge.Visibility = Wpf.Visibility.Collapsed;
            UpdateOverlayGeometry(null);
            return;
        }

        _candidateIndex = resetCandidateIndex
            ? 0
            : Math.Clamp(_candidateIndex, 0, _hoverCandidates.Count - 1);

        _hoverCandidate = _hoverCandidates[_candidateIndex];
        RenderRegion(_hoverCandidate, showHandlesAndToolbar: false);
    }

    private void UpdateMagnifier(Win32Interop.POINT pt)
    {
        if (!_hasSource) return;

        _snapshot.CopyMagnifierPatch(pt.X, pt.Y, MagnifierPatchSize, _magnifierBuffer);
        _magnifierBitmap.WritePixels(
            new Wpf.Int32Rect(0, 0, MagnifierPatchSize, MagnifierPatchSize),
            _magnifierBuffer,
            MagnifierPatchSize * 4,
            0);

        if (_snapshot.TryGetPixel(pt.X, pt.Y, out byte r, out byte g, out byte b))
        {
            ColorSwatch.Background = new Media.SolidColorBrush(Media.Color.FromRgb(r, g, b));
            ColorValueText.Text = _useRgbColorFormat
                ? $"{r}, {g}, {b}"
                : $"#{r:X2}{g:X2}{b:X2}";
        }
        CoordText.Text = $"{pt.X}, {pt.Y}";

        MagnifierPanel.Visibility = Wpf.Visibility.Visible;
        MagnifierPanel.UpdateLayout();

        Wpf.Point cursorDip = PointFromScreen(new Wpf.Point(pt.X, pt.Y));
        double magW = MagnifierPanel.ActualWidth > 0 ? MagnifierPanel.ActualWidth : 145;
        double magH = MagnifierPanel.ActualHeight > 0 ? MagnifierPanel.ActualHeight : 205;
        double canvasW = GetCanvasWidth();
        double canvasH = GetCanvasHeight();

        double left = cursorDip.X + 20;
        if (left + magW > canvasW - 8)
        {
            left = cursorDip.X - magW - 16;
        }
        left = Math.Max(8, left);

        double top = cursorDip.Y + 22;
        if (top + magH > canvasH - 8)
        {
            top = cursorDip.Y - magH - 16;
        }
        top = Math.Max(8, top);

        Canvas.SetLeft(MagnifierPanel, left);
        Canvas.SetTop(MagnifierPanel, top);
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
        _lastSelDipRect = selRect;
        UpdateOverlayGeometry(selRect);

        AnnotationLayer.ClipDipRect = showHandlesAndToolbar ? selRect : Wpf.Rect.Empty;
        AnnotationLayer.Refresh();

        SelectionBorder.Visibility = Wpf.Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, dipX);
        Canvas.SetTop(SelectionBorder, dipY);
        SelectionBorder.Width = dipW;
        SelectionBorder.Height = dipH;

        SizeBadgeText.Text = $"{region.Width} × {region.Height}";
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
            PositionStyleSubBar();
        }
        else
        {
            SetHandlesVisibility(Wpf.Visibility.Collapsed);
            ActionToolbar.Visibility = Wpf.Visibility.Collapsed;
            StyleSubBar.Visibility = Wpf.Visibility.Collapsed;
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

        double extraSubBarSpace = _activeTool != AnnotationToolType.None ? 40 : 0;
        double top = dipY + dipH + 8;
        if (top + tbH + extraSubBarSpace > canvasH - 8)
        {
            double aboveTop = dipY - tbH - 8;
            top = aboveTop - extraSubBarSpace >= 8
                ? aboveTop
                : Math.Max(8, dipY + dipH - tbH - 8);
        }

        Canvas.SetLeft(ActionToolbar, left);
        Canvas.SetTop(ActionToolbar, top);
    }

    private void PositionStyleSubBar()
    {
        if (StyleSubBar.Visibility != Wpf.Visibility.Visible || ActionToolbar.Visibility != Wpf.Visibility.Visible)
        {
            return;
        }

        StyleSubBar.UpdateLayout();
        double subW = StyleSubBar.ActualWidth;
        double subH = StyleSubBar.ActualHeight;
        double tbLeft = Canvas.GetLeft(ActionToolbar);
        double tbTop = Canvas.GetTop(ActionToolbar);
        double tbH = ActionToolbar.ActualHeight;

        double canvasW = GetCanvasWidth();
        double canvasH = GetCanvasHeight();

        double left = Math.Clamp(tbLeft, 8, Math.Max(8, canvasW - subW - 8));
        double top = tbTop + tbH + 5;
        if (top + subH > canvasH - 8)
        {
            top = Math.Max(8, tbTop - subH - 5);
        }

        Canvas.SetLeft(StyleSubBar, left);
        Canvas.SetTop(StyleSubBar, top);
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

    private bool IsFromUiChrome(Wpf.DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ActionToolbar) ||
                ReferenceEquals(source, StyleSubBar) ||
                ReferenceEquals(source, InlineTextBoxContainer) ||
                ReferenceEquals(source, OcrSelectionBubble) ||
                ReferenceEquals(source, OcrActionBar) ||
                ReferenceEquals(source, OcrSelectionLayer))
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
