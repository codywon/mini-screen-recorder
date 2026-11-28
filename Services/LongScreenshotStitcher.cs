using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Services;

internal enum StitchFrameStatus
{
    FirstFrame,
    Appended,
    Static,
    NoMatch,
    MaxHeightReached
}

internal readonly record struct StitchFrameResult(
    StitchFrameStatus Status,
    int AppendedRows,
    int TotalHeight,
    double MatchError);

/// <summary>
/// 纯原生垂直滚动长图拼接引擎：
/// 支持自动忽略两侧滚动条、自动识别并剔除固定顶栏/底栏、避开纯色区域挑选高纹理特征条带进行两阶段粗精匹配。
/// </summary>
internal sealed class LongScreenshotStitcher
{
    public const int DefaultMaxHeight = 28000;

    private readonly int _width;
    private readonly int _frameHeight;
    private readonly int _stride;
    private readonly int _maxHeight;
    private readonly int _marginX;

    private byte[] _bodyBuffer;
    private int _bodyCapacityRows;
    private int _bodyRows;

    private byte[]? _lastFrame;
    private byte[]? _latestFooter;
    private int _footerRows;
    private bool _stickyDetected;
    private int _stickyTop;
    private int _stickyBottom;

    public int Width => _width;
    public int FrameHeight => _frameHeight;
    public int TotalHeight => _bodyRows + _footerRows;
    public int AppendedFrameCount { get; private set; }
    public int ConsecutiveStaticCount { get; private set; }
    public int StickyTopRows => _stickyTop;
    public int StickyBottomRows => _stickyBottom;

    public LongScreenshotStitcher(int width, int frameHeight, int maxHeight = DefaultMaxHeight)
    {
        if (width <= 0 || frameHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "长图拼接宽高必须大于 0");
        }

        _width = width;
        _frameHeight = frameHeight;
        _stride = checked(width * 4);
        _maxHeight = Math.Max(frameHeight * 2, maxHeight);
        _marginX = width >= 96 ? Math.Min(26, Math.Max(12, width / 14)) : 0;

        _bodyCapacityRows = Math.Min(_maxHeight, frameHeight * 4);
        _bodyBuffer = new byte[checked(_bodyCapacityRows * _stride)];
    }

    public StitchFrameResult TryAppendFrame(byte[] frameBgra)
    {
        int expectedLen = _frameHeight * _stride;
        if (frameBgra.Length < expectedLen)
        {
            throw new ArgumentException("帧像素缓冲区长度不足", nameof(frameBgra));
        }

        if (_lastFrame is null)
        {
            _lastFrame = new byte[expectedLen];
            Buffer.BlockCopy(frameBgra, 0, _lastFrame, 0, expectedLen);

            EnsureCapacity(_frameHeight);
            Buffer.BlockCopy(frameBgra, 0, _bodyBuffer, 0, expectedLen);
            _bodyRows = _frameHeight;
            AppendedFrameCount = 1;
            ConsecutiveStaticCount = 0;

            return new StitchFrameResult(StitchFrameStatus.FirstFrame, _frameHeight, TotalHeight, 0);
        }

        if (TotalHeight >= _maxHeight)
        {
            return new StitchFrameResult(StitchFrameStatus.MaxHeightReached, 0, TotalHeight, 0);
        }

        // 先快速判断整帧是否几乎静止
        double wholeDiff = ComputeRegionMad(_lastFrame, frameBgra, 0, _frameHeight, _marginX, _width - _marginX, stepX: 6, stepY: 4);
        if (wholeDiff < 1.1)
        {
            ConsecutiveStaticCount++;
            return new StitchFrameResult(StitchFrameStatus.Static, 0, TotalHeight, wholeDiff);
        }

        // 第 2 帧起检测固定顶栏与固定底栏（避免网页导航栏/聊天输入框在长图里重复出现）
        int curStickyTop = DetectStaticTopRows(_lastFrame, frameBgra);
        int curStickyBottom = DetectStaticBottomRows(_lastFrame, frameBgra, curStickyTop);

        if (!_stickyDetected)
        {
            _stickyTop = curStickyTop;
            _stickyBottom = curStickyBottom;
            _stickyDetected = true;

            if (_stickyBottom > 0 && _bodyRows >= _stickyBottom)
            {
                _bodyRows -= _stickyBottom;
                _footerRows = _stickyBottom;
                _latestFooter = new byte[_footerRows * _stride];
                Buffer.BlockCopy(frameBgra, (_frameHeight - _footerRows) * _stride, _latestFooter, 0, _footerRows * _stride);
            }
        }
        else
        {
            // 若新帧中原本以为的固定区开始变动，则收缩回交集，保证不错裁真实内容
            _stickyTop = Math.Min(_stickyTop, curStickyTop);
            if (curStickyBottom < _stickyBottom && _latestFooter is not null)
            {
                int releaseRows = _stickyBottom - curStickyBottom;
                AppendRowsFromBuffer(_latestFooter, 0, releaseRows);
                _stickyBottom = curStickyBottom;
                _footerRows = _stickyBottom;
                if (_footerRows > 0)
                {
                    var newFooter = new byte[_footerRows * _stride];
                    Buffer.BlockCopy(frameBgra, (_frameHeight - _footerRows) * _stride, newFooter, 0, _footerRows * _stride);
                    _latestFooter = newFooter;
                }
                else
                {
                    _latestFooter = null;
                }
            }
        }

        int scrollTop = _stickyTop;
        int scrollBottom = _frameHeight - _stickyBottom;
        int scrollHeight = scrollBottom - scrollTop;
        if (scrollHeight < 36)
        {
            // 可滚动内容太窄，退化为全高匹配
            scrollTop = 0;
            scrollBottom = _frameHeight;
            scrollHeight = _frameHeight;
        }

        // 在可滚动区检测是否静止
        double scrollDiff = ComputeRegionMad(_lastFrame, frameBgra, scrollTop, scrollBottom, _marginX, _width - _marginX, stepX: 5, stepY: 3);
        if (scrollDiff < 1.2)
        {
            ConsecutiveStaticCount++;
            return new StitchFrameResult(StitchFrameStatus.Static, 0, TotalHeight, scrollDiff);
        }

        // 选取高纹理参考条带并做两阶段重叠匹配
        (int deltaY, double bestErr) = FindVerticalScrollDelta(_lastFrame, frameBgra, scrollTop, scrollBottom);
        if (deltaY <= 0)
        {
            return new StitchFrameResult(StitchFrameStatus.NoMatch, 0, TotalHeight, bestErr);
        }

        int remainingQuota = _maxHeight - TotalHeight;
        int rowsToAppend = Math.Min(deltaY, remainingQuota);
        if (rowsToAppend <= 0)
        {
            return new StitchFrameResult(StitchFrameStatus.MaxHeightReached, 0, TotalHeight, bestErr);
        }

        int srcStartRow = scrollBottom - deltaY;
        AppendRowsFromBuffer(frameBgra, srcStartRow, rowsToAppend);

        if (_footerRows > 0 && _latestFooter is not null)
        {
            Buffer.BlockCopy(frameBgra, (_frameHeight - _footerRows) * _stride, _latestFooter, 0, _footerRows * _stride);
        }

        Buffer.BlockCopy(frameBgra, 0, _lastFrame, 0, expectedLen);
        AppendedFrameCount++;
        ConsecutiveStaticCount = 0;

        StitchFrameStatus status = TotalHeight >= _maxHeight ? StitchFrameStatus.MaxHeightReached : StitchFrameStatus.Appended;
        return new StitchFrameResult(status, rowsToAppend, TotalHeight, bestErr);
    }

    public Imaging.BitmapSource BuildFinalBitmap()
    {
        int totalH = Math.Max(1, TotalHeight);
        byte[] finalPixels = new byte[checked(totalH * _stride)];

        if (_bodyRows > 0)
        {
            Buffer.BlockCopy(_bodyBuffer, 0, finalPixels, 0, _bodyRows * _stride);
        }
        if (_footerRows > 0 && _latestFooter is not null)
        {
            Buffer.BlockCopy(_latestFooter, 0, finalPixels, _bodyRows * _stride, _footerRows * _stride);
        }

        // 确保 Alpha 通道统一为 255（避免部分 GDI 像素带 0 Alpha）
        for (int i = 3; i < finalPixels.Length; i += 4)
        {
            finalPixels[i] = 255;
        }

        var bmp = Imaging.BitmapSource.Create(
            _width,
            totalH,
            96,
            96,
            Media.PixelFormats.Bgr32,
            null,
            finalPixels,
            _stride);
        bmp.Freeze();
        return bmp;
    }

    public Imaging.BitmapSource BuildPreviewBitmap(int maxPreviewWidth = 220, int maxPreviewHeight = 2400)
    {
        int totalH = Math.Max(1, TotalHeight);
        double scaleW = maxPreviewWidth / (double)_width;
        double scaleH = maxPreviewHeight / (double)totalH;
        double scale = Math.Min(1.0, Math.Min(scaleW, scaleH));

        int outW = Math.Max(1, (int)Math.Round(_width * scale));
        int outH = Math.Max(1, (int)Math.Round(totalH * scale));
        int outStride = outW * 4;
        byte[] preview = new byte[outH * outStride];

        for (int py = 0; py < outH; py++)
        {
            int sy = Math.Min(totalH - 1, (int)(py / scale));
            byte[] srcBuf;
            int srcRow;
            if (sy < _bodyRows)
            {
                srcBuf = _bodyBuffer;
                srcRow = sy;
            }
            else
            {
                srcBuf = _latestFooter ?? _bodyBuffer;
                srcRow = Math.Clamp(sy - _bodyRows, 0, Math.Max(0, _footerRows - 1));
            }

            int srcRowOffset = srcRow * _stride;
            int dstRowOffset = py * outStride;

            for (int px = 0; px < outW; px++)
            {
                int sx = Math.Min(_width - 1, (int)(px / scale));
                int sIdx = srcRowOffset + sx * 4;
                int dIdx = dstRowOffset + px * 4;
                preview[dIdx] = srcBuf[sIdx];
                preview[dIdx + 1] = srcBuf[sIdx + 1];
                preview[dIdx + 2] = srcBuf[sIdx + 2];
                preview[dIdx + 3] = 255;
            }
        }

        var bmp = Imaging.BitmapSource.Create(
            outW,
            outH,
            96,
            96,
            Media.PixelFormats.Bgr32,
            null,
            preview,
            outStride);
        bmp.Freeze();
        return bmp;
    }

    private void AppendRowsFromBuffer(byte[] src, int srcStartRow, int rowCount)
    {
        if (rowCount <= 0) return;
        EnsureCapacity(_bodyRows + rowCount);
        Buffer.BlockCopy(src, srcStartRow * _stride, _bodyBuffer, _bodyRows * _stride, rowCount * _stride);
        _bodyRows += rowCount;
    }

    private void EnsureCapacity(int requiredRows)
    {
        if (requiredRows <= _bodyCapacityRows) return;
        int newCap = Math.Min(_maxHeight, Math.Max(requiredRows, _bodyCapacityRows * 2));
        byte[] next = new byte[checked(newCap * _stride)];
        if (_bodyRows > 0)
        {
            Buffer.BlockCopy(_bodyBuffer, 0, next, 0, _bodyRows * _stride);
        }
        _bodyBuffer = next;
        _bodyCapacityRows = newCap;
    }

    private int DetectStaticTopRows(byte[] prev, byte[] curr)
    {
        int maxScan = Math.Min(_frameHeight / 3, 220);
        int xStart = _marginX;
        int xEnd = _width - _marginX;
        int staticRows = 0;
        for (int y = 0; y < maxScan; y++)
        {
            if (ComputeRowMad(prev, y, curr, y, xStart, xEnd, stepX: 4) < 1.5)
            {
                staticRows++;
            }
            else
            {
                break;
            }
        }
        // 至少连续 6 行静止才视为固定顶栏，避免纯白背景顶行误判
        return staticRows >= 6 ? staticRows : 0;
    }

    private int DetectStaticBottomRows(byte[] prev, byte[] curr, int topLimit)
    {
        int maxScan = Math.Min((_frameHeight - topLimit) / 3, 220);
        int xStart = _marginX;
        int xEnd = _width - _marginX;
        int staticRows = 0;
        for (int i = 0; i < maxScan; i++)
        {
            int y = _frameHeight - 1 - i;
            if (ComputeRowMad(prev, y, curr, y, xStart, xEnd, stepX: 4) < 1.5)
            {
                staticRows++;
            }
            else
            {
                break;
            }
        }
        return staticRows >= 6 ? staticRows : 0;
    }

    private (int DeltaY, double BestError) FindVerticalScrollDelta(byte[] prev, byte[] curr, int scrollTop, int scrollBottom)
    {
        int scrollH = scrollBottom - scrollTop;
        int bandH = Math.Clamp(scrollH / 6, 20, 48);
        int xStart = _marginX;
        int xEnd = _width - _marginX;
        if (xEnd - xStart < 16)
        {
            xStart = 0;
            xEnd = _width;
        }

        // 主参考条带始终紧贴 prev 底部，确保可覆盖高达 (scrollH - bandH) 的大步长滚轮；
        // 仅当底部恰好为几乎纯色留白时，才向上微移至首个有足够纹理的条带。
        int bottomRefY = scrollBottom - bandH;
        int bestRefY = bottomRefY;
        if (ComputeBandTexture(prev, bottomRefY, bandH, xStart, xEnd) < 2.0)
        {
            int minRefY = scrollTop + Math.Max(bandH, scrollH / 2);
            double bestTexture = -1;
            for (int candidateY = bottomRefY; candidateY >= minRefY; candidateY -= Math.Max(4, bandH / 3))
            {
                double tex = ComputeBandTexture(prev, candidateY, bandH, xStart, xEnd);
                if (tex > bestTexture)
                {
                    bestTexture = tex;
                    bestRefY = candidateY;
                    if (tex >= 4.0) break;
                }
            }
        }

        int maxDelta = bestRefY - scrollTop;
        int minDelta = 2;
        if (maxDelta < minDelta)
        {
            return (0, double.MaxValue);
        }

        // 阶段 1：粗搜（步长 2 行，保留前 4 个低误差候选）
        const int TopK = 4;
        int[] topDeltas = new int[TopK];
        double[] topErrs = [double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue];

        for (int delta = minDelta; delta <= maxDelta; delta += 2)
        {
            int currY = bestRefY - delta;
            double err = ComputeBandMad(prev, bestRefY, curr, currY, bandH, xStart, xEnd, stepX: 5, stepY: 2);
            if (err < topErrs[TopK - 1])
            {
                int pos = TopK - 1;
                while (pos > 0 && err < topErrs[pos - 1])
                {
                    topErrs[pos] = topErrs[pos - 1];
                    topDeltas[pos] = topDeltas[pos - 1];
                    pos--;
                }
                topErrs[pos] = err;
                topDeltas[pos] = delta;
            }
        }

        // 阶段 2：在 Top-K 邻域 ±2 行内精搜 + 扩大整块重叠区交叉校验
        int bestDelta = 0;
        double bestFineErr = double.MaxValue;
        var checkedDeltas = new HashSet<int>();

        for (int k = 0; k < TopK; k++)
        {
            if (topDeltas[k] <= 0) continue;
            for (int d = Math.Max(minDelta, topDeltas[k] - 2); d <= Math.Min(maxDelta, topDeltas[k] + 2); d++)
            {
                if (!checkedDeltas.Add(d)) continue;

                // 扩大校验范围：验证整个重叠区 [scrollTop + d, scrollBottom) 对齐到 [scrollTop, scrollBottom - d)
                int overlapH = scrollH - d;
                if (overlapH < 16) continue;

                int verifyStepY = overlapH > 200 ? 3 : 1;
                double fineErr = ComputeBandMad(
                    prev,
                    scrollTop + d,
                    curr,
                    scrollTop,
                    overlapH,
                    xStart,
                    xEnd,
                    stepX: 3,
                    stepY: verifyStepY);

                if (fineErr < bestFineErr)
                {
                    bestFineErr = fineErr;
                    bestDelta = d;
                }
            }
        }

        // 置信阈值：允许少量子像素抗锯齿或光标轻微扰动，拒绝明显失配
        if (bestDelta >= minDelta && bestFineErr <= 11.5)
        {
            return (bestDelta, bestFineErr);
        }

        return (0, bestFineErr);
    }

    private double ComputeBandTexture(byte[] buf, int startY, int height, int xStart, int xEnd)
    {
        long gradSum = 0;
        long samples = 0;
        for (int y = startY; y < startY + height - 1; y += 3)
        {
            int row0 = y * _stride;
            int row1 = (y + 1) * _stride;
            for (int x = xStart; x < xEnd - 4; x += 6)
            {
                int i0 = row0 + x * 4;
                int iRight = row0 + (x + 2) * 4;
                int iDown = row1 + x * 4;
                int dx = Math.Abs(buf[i0] - buf[iRight]) + Math.Abs(buf[i0 + 1] - buf[iRight + 1]) + Math.Abs(buf[i0 + 2] - buf[iRight + 2]);
                int dy = Math.Abs(buf[i0] - buf[iDown]) + Math.Abs(buf[i0 + 1] - buf[iDown + 1]) + Math.Abs(buf[i0 + 2] - buf[iDown + 2]);
                gradSum += dx + dy;
                samples += 6;
            }
        }
        return samples > 0 ? gradSum / (double)samples : 0;
    }

    private double ComputeBandMad(
        byte[] a,
        int aStartY,
        byte[] b,
        int bStartY,
        int height,
        int xStart,
        int xEnd,
        int stepX,
        int stepY)
    {
        long sum = 0;
        long count = 0;
        for (int dy = 0; dy < height; dy += stepY)
        {
            int rowA = (aStartY + dy) * _stride;
            int rowB = (bStartY + dy) * _stride;
            for (int x = xStart; x < xEnd; x += stepX)
            {
                int ia = rowA + x * 4;
                int ib = rowB + x * 4;
                sum += Math.Abs(a[ia] - b[ib])
                     + Math.Abs(a[ia + 1] - b[ib + 1])
                     + Math.Abs(a[ia + 2] - b[ib + 2]);
                count += 3;
            }
        }
        return count > 0 ? sum / (double)count : double.MaxValue;
    }

    private double ComputeRegionMad(
        byte[] a,
        byte[] b,
        int yStart,
        int yEnd,
        int xStart,
        int xEnd,
        int stepX,
        int stepY) =>
        ComputeBandMad(a, yStart, b, yStart, Math.Max(0, yEnd - yStart), xStart, xEnd, stepX, stepY);

    private double ComputeRowMad(byte[] a, int rowA, byte[] b, int rowB, int xStart, int xEnd, int stepX)
    {
        int offA = rowA * _stride;
        int offB = rowB * _stride;
        long sum = 0;
        long count = 0;
        for (int x = xStart; x < xEnd; x += stepX)
        {
            int ia = offA + x * 4;
            int ib = offB + x * 4;
            sum += Math.Abs(a[ia] - b[ib])
                 + Math.Abs(a[ia + 1] - b[ib + 1])
                 + Math.Abs(a[ia + 2] - b[ib + 2]);
            count += 3;
        }
        return count > 0 ? sum / (double)count : 0;
    }
}
