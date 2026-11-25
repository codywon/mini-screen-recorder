using MiniRec.Models;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Services;

internal sealed class AnnotationEngine
{
    private interface IAnnotationCommand
    {
        void Execute();
        void Undo();
    }

    private sealed class AddCommand(List<AnnotationItem> items, AnnotationItem item) : IAnnotationCommand
    {
        public void Execute()
        {
            if (!items.Contains(item))
            {
                items.Add(item);
            }
        }

        public void Undo() => items.Remove(item);
    }

    private sealed class DeleteCommand(List<AnnotationItem> items, AnnotationItem item, int index) : IAnnotationCommand
    {
        public void Execute() => items.Remove(item);

        public void Undo()
        {
            int insertAt = Math.Clamp(index, 0, items.Count);
            items.Insert(insertAt, item);
        }
    }

    private sealed class MoveCommand(AnnotationItem item, double dx, double dy) : IAnnotationCommand
    {
        public void Execute() => item.Translate(dx, dy);
        public void Undo() => item.Translate(-dx, -dy);
    }

    private sealed class UpdateTextCommand(TextAnnotation item, string oldText, string newText) : IAnnotationCommand
    {
        public void Execute() => item.Text = newText;
        public void Undo() => item.Text = oldText;
    }

    private readonly List<AnnotationItem> _items = [];
    private readonly Stack<IAnnotationCommand> _undoStack = [];
    private readonly Stack<IAnnotationCommand> _redoStack = [];
    private readonly Dictionary<(MosaicMode Mode, int Strength), Imaging.BitmapSource> _mosaicCache = [];
    private CaptureRegion? _mosaicCacheRegion;

    public IReadOnlyList<AnnotationItem> Items => _items;
    public AnnotationItem? SelectedItem { get; set; }
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event Action? Changed;

    public void Add(AnnotationItem item)
    {
        var cmd = new AddCommand(_items, item);
        cmd.Execute();
        _undoStack.Push(cmd);
        _redoStack.Clear();
        SelectedItem = item;
        Changed?.Invoke();
    }

    public void RecordMove(AnnotationItem item, double totalDx, double totalDy)
    {
        if (Math.Abs(totalDx) < 0.5 && Math.Abs(totalDy) < 0.5) return;
        _undoStack.Push(new MoveCommand(item, totalDx, totalDy));
        _redoStack.Clear();
        Changed?.Invoke();
    }

    public void Delete(AnnotationItem item)
    {
        int idx = _items.IndexOf(item);
        if (idx < 0) return;

        var cmd = new DeleteCommand(_items, item, idx);
        cmd.Execute();
        _undoStack.Push(cmd);
        _redoStack.Clear();
        if (ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = null;
        }
        Changed?.Invoke();
    }

    public void UpdateText(TextAnnotation item, string oldText, string newText)
    {
        if (oldText == newText) return;
        var cmd = new UpdateTextCommand(item, oldText, newText);
        cmd.Execute();
        _undoStack.Push(cmd);
        _redoStack.Clear();
        SelectedItem = item;
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;
        IAnnotationCommand cmd = _undoStack.Pop();
        cmd.Undo();
        _redoStack.Push(cmd);
        if (SelectedItem is not null && !_items.Contains(SelectedItem))
        {
            SelectedItem = null;
        }
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;
        IAnnotationCommand cmd = _redoStack.Pop();
        cmd.Execute();
        _undoStack.Push(cmd);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        SelectedItem = null;
        InvalidateMosaicCache();
        Changed?.Invoke();
    }

    public int NextBadgeNumber()
    {
        int max = 0;
        foreach (AnnotationItem item in _items)
        {
            if (item is NumberBadgeAnnotation badge && badge.Number > max)
            {
                max = badge.Number;
            }
        }
        return max + 1;
    }

    public AnnotationItem? HitTestTopmost(Wpf.Point physPt, double tolerance)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].HitTest(physPt, tolerance))
            {
                return _items[i];
            }
        }
        return null;
    }

    public void InvalidateMosaicCache()
    {
        _mosaicCache.Clear();
        _mosaicCacheRegion = null;
    }

    public (Imaging.BitmapSource? Bitmap, CaptureRegion CacheRegion) GetOrCreateMosaicBitmap(
        ScreenSnapshot snapshot,
        CaptureRegion region,
        MosaicMode mode,
        int strength)
    {
        CaptureRegion clamped = ClampRegion(snapshot, region);
        if (_mosaicCacheRegion != clamped)
        {
            _mosaicCache.Clear();
            _mosaicCacheRegion = clamped;
        }

        var key = (mode, strength);
        if (!_mosaicCache.TryGetValue(key, out Imaging.BitmapSource? bmp))
        {
            bmp = mode == MosaicMode.Blur
                ? MosaicProcessor.CreateBlurredBitmap(snapshot, clamped, strength)
                : MosaicProcessor.CreatePixelatedBitmap(snapshot, clamped, strength);
            _mosaicCache[key] = bmp;
        }

        return (bmp, clamped);
    }

    public Imaging.BitmapSource RenderCompositeBitmap(ScreenSnapshot snapshot, CaptureRegion region)
    {
        CaptureRegion clamped = ClampRegion(snapshot, region);
        Imaging.BitmapSource baseBmp = snapshot.CropRegionBitmap(clamped);
        if (_items.Count == 0)
        {
            return baseBmp;
        }

        int outW = clamped.Width;
        int outH = clamped.Height;

        var visual = new Media.DrawingVisual();
        using (Media.DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(baseBmp, new Wpf.Rect(0, 0, outW, outH));
            dc.PushClip(new Media.RectangleGeometry(new Wpf.Rect(0, 0, outW, outH)));
            dc.PushTransform(new Media.TranslateTransform(-clamped.X, -clamped.Y));

            MosaicSourceProvider mosaicProvider = (mode, strength) =>
                GetOrCreateMosaicBitmap(snapshot, clamped, mode, strength);

            foreach (AnnotationItem item in _items)
            {
                item.Draw(dc, mosaicProvider);
            }

            dc.Pop();
            dc.Pop();
        }

        var rtb = new Imaging.RenderTargetBitmap(outW, outH, 96, 96, Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static CaptureRegion ClampRegion(ScreenSnapshot snapshot, CaptureRegion region)
    {
        int vRight = snapshot.VirtualX + snapshot.Width;
        int vBottom = snapshot.VirtualY + snapshot.Height;

        int left = Math.Clamp(region.X, snapshot.VirtualX, vRight - 1);
        int top = Math.Clamp(region.Y, snapshot.VirtualY, vBottom - 1);
        int right = Math.Clamp(region.X + Math.Max(1, region.Width), left + 1, vRight);
        int bottom = Math.Clamp(region.Y + Math.Max(1, region.Height), top + 1, vBottom);

        return new CaptureRegion(left, top, right - left, bottom - top);
    }
}
