using System.Collections.Concurrent;
using System.Diagnostics;
using Wpf = System.Windows;
using Automation = System.Windows.Automation;

namespace MiniRec.Services;

internal sealed class UiElementDetector : IDisposable
{
    private const int MaxDepth = 4;
    private const int MaxElementsPerWindow = 140;
    private const int PerWindowTimeoutMs = 100;
    private const int MinElementWidth = 48;
    private const int MinElementHeight = 36;
    private const int MinElementArea = 2800;
    private const int MinLeafWidth = 96;
    private const int MinLeafHeight = 48;

    private readonly IReadOnlyList<(IntPtr Hwnd, int X, int Y, int Width, int Height)> _topWindows;
    private readonly (int Vx, int Vy, int Vw, int Vh) _virtualBounds;
    private readonly int _excludeProcessId;

    private readonly ConcurrentDictionary<IntPtr, List<CaptureRegion>> _childCache = new();
    private readonly ConcurrentDictionary<IntPtr, byte> _queuedHwnds = new();
    private readonly object _probeLock = new();

    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public UiElementDetector(
        IReadOnlyList<(IntPtr Hwnd, int X, int Y, int Width, int Height)> topWindows,
        (int Vx, int Vy, int Vw, int Vh) virtualBounds,
        int excludeProcessId)
    {
        _topWindows = topWindows;
        _virtualBounds = virtualBounds;
        _excludeProcessId = excludeProcessId;
    }

    public (IntPtr Hwnd, CaptureRegion Region)? FindTopWindowAt(int physX, int physY)
    {
        foreach (var (hwnd, x, y, w, h) in _topWindows)
        {
            if (physX >= x && physX < x + w && physY >= y && physY < y + h)
            {
                return (hwnd, new CaptureRegion(x, y, w, h));
            }
        }
        return null;
    }

    /// <summary>
    /// 截图启动时后台静默预扫描 Z-order 最靠前的几个可见窗口，避免鼠标首次移入时出现“先闪整窗再缩成子区”的二次跳变。
    /// </summary>
    public void PrewarmTopWindows(int maxWindows = 3)
    {
        if (_disposed || _topWindows.Count == 0) return;

        int count = Math.Min(maxWindows, _topWindows.Count);
        var targets = new List<(IntPtr Hwnd, CaptureRegion Region)>(count);
        for (int i = 0; i < count; i++)
        {
            var (hwnd, x, y, w, h) = _topWindows[i];
            if (hwnd != IntPtr.Zero)
            {
                targets.Add((hwnd, new CaptureRegion(x, y, w, h)));
            }
        }

        _ = Task.Run(() =>
        {
            foreach (var (hwnd, topRegion) in targets)
            {
                if (_disposed) return;
                if (_childCache.ContainsKey(hwnd) || !_queuedHwnds.TryAdd(hwnd, 0)) continue;
                try
                {
                    List<CaptureRegion> collected = CollectWindowElements(hwnd, topRegion, CancellationToken.None);
                    if (!_disposed)
                    {
                        _childCache.TryAdd(hwnd, collected);
                    }
                }
                catch
                {
                    _childCache.TryAdd(hwnd, []);
                }
                finally
                {
                    _queuedHwnds.TryRemove(hwnd, out _);
                }
            }
        });
    }

    public List<CaptureRegion> GetCandidateRegionsAt(int physX, int physY, Action? onCacheReady = null)
    {
        var results = new List<CaptureRegion>();
        (IntPtr Hwnd, CaptureRegion Region)? topHit = FindTopWindowAt(physX, physY);
        if (topHit is null)
        {
            return results;
        }

        (IntPtr hwnd, CaptureRegion topRegion) = topHit.Value;

        if (_childCache.TryGetValue(hwnd, out List<CaptureRegion>? cachedChildren))
        {
            foreach (CaptureRegion rect in cachedChildren)
            {
                if (physX >= rect.X && physX < rect.X + rect.Width &&
                    physY >= rect.Y && physY < rect.Y + rect.Height)
                {
                    results.Add(rect);
                }
            }
        }
        else if (onCacheReady is not null && hwnd != IntPtr.Zero)
        {
            ScheduleWindowProbe(hwnd, topRegion, onCacheReady);
        }

        if (results.Count == 0 || !AreNearlyEqual(results[^1], topRegion))
        {
            results.Add(topRegion);
        }

        return results;
    }

    private void ScheduleWindowProbe(IntPtr hwnd, CaptureRegion topRegion, Action onCacheReady)
    {
        if (_disposed || _childCache.ContainsKey(hwnd)) return;

        CancellationToken token;
        lock (_probeLock)
        {
            if (_disposed) return;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(32, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || _disposed || _childCache.ContainsKey(hwnd))
            {
                return;
            }

            if (!_queuedHwnds.TryAdd(hwnd, 0))
            {
                return;
            }

            try
            {
                List<CaptureRegion> collected = CollectWindowElements(hwnd, topRegion, token);
                if (!token.IsCancellationRequested && !_disposed)
                {
                    _childCache.TryAdd(hwnd, collected);
                    if (collected.Count > 0)
                    {
                        onCacheReady();
                    }
                }
            }
            catch
            {
                _childCache.TryAdd(hwnd, []);
            }
            finally
            {
                _queuedHwnds.TryRemove(hwnd, out _);
            }
        }, CancellationToken.None);
    }

    private List<CaptureRegion> CollectWindowElements(IntPtr hwnd, CaptureRegion topRegion, CancellationToken token)
    {
        var collected = new List<CaptureRegion>();
        var sw = Stopwatch.StartNew();

        int vRight = _virtualBounds.Vx + _virtualBounds.Vw;
        int vBottom = _virtualBounds.Vy + _virtualBounds.Vh;
        int topRight = Math.Min(vRight, topRegion.X + topRegion.Width);
        int topBottom = Math.Min(vBottom, topRegion.Y + topRegion.Height);
        int topLeft = Math.Max(_virtualBounds.Vx, topRegion.X);
        int topTop = Math.Max(_virtualBounds.Vy, topRegion.Y);

        Automation.AutomationElement root;
        try
        {
            root = Automation.AutomationElement.FromHandle(hwnd);
            if (root is null) return collected;
        }
        catch
        {
            return collected;
        }

        Automation.TreeWalker walker = Automation.TreeWalker.ControlViewWalker;
        var queue = new Queue<(Automation.AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 &&
               collected.Count < MaxElementsPerWindow &&
               sw.ElapsedMilliseconds < PerWindowTimeoutMs &&
               !token.IsCancellationRequested &&
               !_disposed)
        {
            (Automation.AutomationElement el, int depth) = queue.Dequeue();
            if (depth > 0)
            {
                try
                {
                    Automation.AutomationElement.AutomationElementInformation info = el.Current;
                    if (info.ProcessId == _excludeProcessId || info.IsOffscreen)
                    {
                        continue;
                    }

                    Wpf.Rect bounds = info.BoundingRectangle;
                    if (!bounds.IsEmpty &&
                        !double.IsNaN(bounds.X) && !double.IsNaN(bounds.Y) &&
                        !double.IsNaN(bounds.Width) && !double.IsNaN(bounds.Height) &&
                        !double.IsInfinity(bounds.Width) && !double.IsInfinity(bounds.Height))
                    {
                        int x1 = Math.Max(topLeft, (int)Math.Round(bounds.Left));
                        int y1 = Math.Max(topTop, (int)Math.Round(bounds.Top));
                        int x2 = Math.Min(topRight, (int)Math.Round(bounds.Right));
                        int y2 = Math.Min(topBottom, (int)Math.Round(bounds.Bottom));

                        int w = x2 - x1;
                        int h = y2 - y1;
                        if (ShouldAcceptElement(info.ControlType, w, h))
                        {
                            var candidate = new CaptureRegion(x1, y1, w, h);
                            if (!AreNearlyEqual(candidate, topRegion))
                            {
                                collected.Add(candidate);
                            }
                        }
                    }
                }
                catch
                {
                    // Skip unavailable or transient UIA element.
                    continue;
                }
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            try
            {
                Automation.AutomationElement? child = walker.GetFirstChild(el);
                int siblingCount = 0;
                while (child is not null &&
                       siblingCount < 48 &&
                       sw.ElapsedMilliseconds < PerWindowTimeoutMs &&
                       !token.IsCancellationRequested &&
                       !_disposed)
                {
                    queue.Enqueue((child, depth + 1));
                    siblingCount++;
                    child = walker.GetNextSibling(child);
                }
            }
            catch
            {
                // Ignore tree traversal errors on dynamic controls.
            }
        }

        // Sort smallest area first and deduplicate identical/nearly identical bounds.
        collected.Sort((a, b) => ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height));

        var deduped = new List<CaptureRegion>(collected.Count);
        foreach (CaptureRegion item in collected)
        {
            bool duplicate = false;
            for (int i = deduped.Count - 1; i >= 0 && i >= deduped.Count - 8; i--)
            {
                if (AreNearlyEqual(item, deduped[i]))
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
            {
                deduped.Add(item);
            }
        }

        return deduped;
    }

    internal static bool ShouldAcceptElement(Automation.ControlType? controlType, int width, int height)
    {
        if (width < MinElementWidth || height < MinElementHeight) return false;
        if ((long)width * height < MinElementArea) return false;

        // 跳过滚动条、滑块、分隔符等纯装饰/细碎控件
        if (controlType == Automation.ControlType.ScrollBar ||
            controlType == Automation.ControlType.Thumb ||
            controlType == Automation.ControlType.Separator ||
            controlType == Automation.ControlType.MenuBar ||
            controlType == Automation.ControlType.StatusBar ||
            controlType == Automation.ControlType.TitleBar)
        {
            return false;
        }

        // 结构性大块容器直接收录（侧边栏/正文区/文档/列表/工具栏/面板/卡片组）
        bool isStructural =
            controlType == Automation.ControlType.Pane ||
            controlType == Automation.ControlType.Window ||
            controlType == Automation.ControlType.Document ||
            controlType == Automation.ControlType.List ||
            controlType == Automation.ControlType.Tree ||
            controlType == Automation.ControlType.Table ||
            controlType == Automation.ControlType.DataGrid ||
            controlType == Automation.ControlType.Group ||
            controlType == Automation.ControlType.Tab ||
            controlType == Automation.ControlType.ToolBar ||
            controlType == Automation.ControlType.Custom;

        if (isStructural)
        {
            return true;
        }

        // 叶子控件（图片、大卡片、大按钮、大输入框等）仅在尺寸足够大时才收录，彻底避免划过小按钮乱跳
        return width >= MinLeafWidth && height >= MinLeafHeight;
    }

    private static bool AreNearlyEqual(CaptureRegion a, CaptureRegion b) =>
        Math.Abs(a.X - b.X) <= 8 &&
        Math.Abs(a.Y - b.Y) <= 8 &&
        Math.Abs(a.Width - b.Width) <= 12 &&
        Math.Abs(a.Height - b.Height) <= 12;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_probeLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }
}
