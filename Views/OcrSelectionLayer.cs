using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;
using Media = System.Windows.Media;

namespace MiniRec.Views;

internal sealed class OcrSelectionLayer : Wpf.FrameworkElement
{
    private sealed record WordNode(int LineIndex, int WordIndex, string Text, Wpf.Rect DipRect);
    private sealed record LineNode(int LineIndex, Wpf.Rect DipRect, int FirstFlatIndex, int LastFlatIndex);

    private readonly List<WordNode> _flatWords = [];
    private readonly List<LineNode> _lines = [];
    private IReadOnlyList<OcrLineResult> _sourceLines = [];
    private Wpf.Rect _clipDipRect = Wpf.Rect.Empty;

    private int _anchorIdx = -1;
    private int _activeIdx = -1;
    private bool _isDragging;
    private Wpf.Point _dragStartDip;

    public bool HasData => _flatWords.Count > 0;
    public bool HasSelection => _anchorIdx >= 0 && _activeIdx >= 0 && _flatWords.Count > 0;

    public event Action<Wpf.Rect>? SelectionCompleted;
    public event Action? SelectionCleared;

    public OcrSelectionLayer()
    {
        Cursor = Input.Cursors.IBeam;
        IsHitTestVisible = false;
    }

    public void SetOcrData(
        IReadOnlyList<OcrLineResult> lines,
        CaptureRegion editRect,
        Media.Matrix physToDip,
        Wpf.Rect clipDipRect)
    {
        _flatWords.Clear();
        _lines.Clear();
        _sourceLines = lines;
        _clipDipRect = clipDipRect;
        _anchorIdx = -1;
        _activeIdx = -1;
        _isDragging = false;

        for (int li = 0; li < lines.Count; li++)
        {
            OcrLineResult line = lines[li];
            if (line.Words.Count == 0) continue;

            int firstIdx = _flatWords.Count;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            for (int wi = 0; wi < line.Words.Count; wi++)
            {
                OcrWordResult w = line.Words[wi];
                Wpf.Point tl = physToDip.Transform(new Wpf.Point(editRect.X + w.PhysicalBounds.X, editRect.Y + w.PhysicalBounds.Y));
                Wpf.Point br = physToDip.Transform(new Wpf.Point(editRect.X + w.PhysicalBounds.Right, editRect.Y + w.PhysicalBounds.Bottom));
                var dipRect = new Wpf.Rect(
                    Math.Min(tl.X, br.X),
                    Math.Min(tl.Y, br.Y),
                    Math.Max(2, Math.Abs(br.X - tl.X)),
                    Math.Max(4, Math.Abs(br.Y - tl.Y)));

                _flatWords.Add(new WordNode(li, wi, w.Text, dipRect));
                minX = Math.Min(minX, dipRect.Left);
                minY = Math.Min(minY, dipRect.Top);
                maxX = Math.Max(maxX, dipRect.Right);
                maxY = Math.Max(maxY, dipRect.Bottom);
            }

            int lastIdx = _flatWords.Count - 1;
            if (lastIdx >= firstIdx)
            {
                _lines.Add(new LineNode(li, new Wpf.Rect(minX, minY, maxX - minX, maxY - minY), firstIdx, lastIdx));
            }
        }

        IsHitTestVisible = _flatWords.Count > 0;
        InvalidateVisual();
    }

    public void ClearData()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        _flatWords.Clear();
        _lines.Clear();
        _sourceLines = [];
        _clipDipRect = Wpf.Rect.Empty;
        _anchorIdx = -1;
        _activeIdx = -1;
        _isDragging = false;
        IsHitTestVisible = false;
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (_flatWords.Count == 0) return;
        _anchorIdx = 0;
        _activeIdx = _flatWords.Count - 1;
        InvalidateVisual();
        SelectionCompleted?.Invoke(GetSelectionAnchorRect());
    }

    public void ClearSelection()
    {
        if (!HasSelection) return;
        _anchorIdx = -1;
        _activeIdx = -1;
        InvalidateVisual();
        SelectionCleared?.Invoke();
    }

    public string GetSelectedText(bool joinLines = false)
    {
        if (!HasSelection) return string.Empty;
        WordNode a = _flatWords[_anchorIdx];
        WordNode b = _flatWords[_activeIdx];
        return NativeOcrService.ExtractSelectedText(_sourceLines, a.LineIndex, a.WordIndex, b.LineIndex, b.WordIndex, joinLines);
    }

    public Wpf.Rect GetSelectionAnchorRect()
    {
        if (!HasSelection) return _clipDipRect;
        int e = Math.Max(_anchorIdx, _activeIdx);
        return _flatWords[e].DipRect;
    }

    protected override Media.HitTestResult? HitTestCore(Media.PointHitTestParameters hitTestParameters)
    {
        if (_flatWords.Count == 0 || _clipDipRect.IsEmpty) return null;

        Wpf.Rect activeArea = _clipDipRect;
        if (activeArea.Width > 28 && activeArea.Height > 28)
        {
            // Leave outer 6px edge for resize handles.
            activeArea.Inflate(-6, -6);
        }

        return activeArea.Contains(hitTestParameters.HitPoint)
            ? new Media.PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;
    }

    protected override void OnMouseLeftButtonDown(Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_flatWords.Count == 0) return;

        e.Handled = true;
        Wpf.Point pt = e.GetPosition(this);

        if (e.ClickCount == 2)
        {
            int lineIdx = FindLineAtPoint(pt, maxVerticalDist: 10);
            if (lineIdx >= 0)
            {
                LineNode line = _lines[lineIdx];
                _anchorIdx = line.FirstFlatIndex;
                _activeIdx = line.LastFlatIndex;
                _isDragging = false;
                InvalidateVisual();
                SelectionCompleted?.Invoke(GetSelectionAnchorRect());
                return;
            }
        }

        int hitWord = FindNearestWordIndex(pt);
        if (hitWord < 0) return;

        _dragStartDip = pt;
        _anchorIdx = hitWord;
        _activeIdx = hitWord;
        _isDragging = true;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging) return;

        Wpf.Point pt = e.GetPosition(this);
        int nearest = FindNearestWordIndex(pt);
        if (nearest >= 0 && nearest != _activeIdx)
        {
            _activeIdx = nearest;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isDragging) return;

        e.Handled = true;
        _isDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();

        Wpf.Point pt = e.GetPosition(this);
        double dist = Math.Sqrt(Math.Pow(pt.X - _dragStartDip.X, 2) + Math.Pow(pt.Y - _dragStartDip.Y, 2));

        // If user just clicked empty area without dragging across words, clear selection cleanly.
        bool clickedInsideWord = _anchorIdx >= 0 && _flatWords[_anchorIdx].DipRect.Contains(_dragStartDip);
        if (dist < 4 && _anchorIdx == _activeIdx && !clickedInsideWord)
        {
            _anchorIdx = -1;
            _activeIdx = -1;
            InvalidateVisual();
            SelectionCleared?.Invoke();
            return;
        }

        if (HasSelection)
        {
            SelectionCompleted?.Invoke(GetSelectionAnchorRect());
        }
    }

    protected override void OnRender(Media.DrawingContext dc)
    {
        base.OnRender(dc);
        if (_lines.Count == 0) return;

        var hintFill = new Media.SolidColorBrush(Media.Color.FromArgb(20, 7, 193, 96));
        hintFill.Freeze();
        var hintUnderlinePen = new Media.Pen(new Media.SolidColorBrush(Media.Color.FromArgb(95, 7, 193, 96)), 1.0);
        hintUnderlinePen.Freeze();

        foreach (LineNode line in _lines)
        {
            var r = new Wpf.Rect(line.DipRect.X - 2, line.DipRect.Y - 1, line.DipRect.Width + 4, line.DipRect.Height + 2);
            dc.DrawRoundedRectangle(hintFill, null, r, 2, 2);
            dc.DrawLine(hintUnderlinePen, new Wpf.Point(r.Left, r.Bottom), new Wpf.Point(r.Right, r.Bottom));
        }

        if (!HasSelection) return;

        int s = Math.Min(_anchorIdx, _activeIdx);
        int e = Math.Max(_anchorIdx, _activeIdx);

        var selFill = new Media.SolidColorBrush(Media.Color.FromArgb(105, 37, 99, 235));
        selFill.Freeze();
        var selPen = new Media.Pen(new Media.SolidColorBrush(Media.Color.FromArgb(180, 59, 130, 246)), 1.0);
        selPen.Freeze();

        // Merge consecutive selected words on the same line into one smooth highlight block.
        int i = s;
        while (i <= e)
        {
            int curLine = _flatWords[i].LineIndex;
            Wpf.Rect merged = _flatWords[i].DipRect;
            i++;
            while (i <= e && _flatWords[i].LineIndex == curLine)
            {
                merged.Union(_flatWords[i].DipRect);
                i++;
            }

            var padded = new Wpf.Rect(merged.X - 2, merged.Y - 1.5, merged.Width + 4, merged.Height + 3);
            dc.DrawRoundedRectangle(selFill, selPen, padded, 3, 3);
        }
    }

    private int FindLineAtPoint(Wpf.Point pt, double maxVerticalDist)
    {
        int bestIdx = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < _lines.Count; i++)
        {
            Wpf.Rect r = _lines[i].DipRect;
            double vDist = pt.Y < r.Top ? r.Top - pt.Y : (pt.Y > r.Bottom ? pt.Y - r.Bottom : 0);
            if (vDist > maxVerticalDist) continue;

            double centerDist = Math.Abs(pt.Y - (r.Top + r.Bottom) / 2.0);
            if (vDist == 0 && pt.X >= r.Left - 12 && pt.X <= r.Right + 12)
            {
                return i;
            }

            if (centerDist < bestDist)
            {
                bestDist = centerDist;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private int FindNearestWordIndex(Wpf.Point pt)
    {
        if (_lines.Count == 0 || _flatWords.Count == 0) return -1;

        int lineListIdx = FindLineAtPoint(pt, maxVerticalDist: double.MaxValue);
        if (lineListIdx < 0) return 0;

        LineNode line = _lines[lineListIdx];
        if (pt.X <= _flatWords[line.FirstFlatIndex].DipRect.Left)
        {
            return line.FirstFlatIndex;
        }
        if (pt.X >= _flatWords[line.LastFlatIndex].DipRect.Right)
        {
            return line.LastFlatIndex;
        }

        int bestWord = line.FirstFlatIndex;
        double bestDist = double.MaxValue;
        for (int idx = line.FirstFlatIndex; idx <= line.LastFlatIndex; idx++)
        {
            Wpf.Rect r = _flatWords[idx].DipRect;
            if (pt.X >= r.Left && pt.X <= r.Right)
            {
                return idx;
            }
            double dist = Math.Abs(pt.X - (r.Left + r.Right) / 2.0);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestWord = idx;
            }
        }

        return bestWord;
    }
}
