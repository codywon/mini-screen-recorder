using System.Text;
using MiniRec.Native;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Services;

internal sealed record OcrWordResult(string Text, Wpf.Rect PhysicalBounds);

internal sealed record OcrLineResult(
    string Text,
    Wpf.Rect PhysicalBounds,
    IReadOnlyList<OcrWordResult> Words);

internal sealed record OcrDocumentResult(
    bool Success,
    string FullText,
    IReadOnlyList<OcrLineResult> Lines,
    string? ErrorMessage = null);

internal static class NativeOcrService
{
    public static Task<OcrDocumentResult> RecognizeBitmapAsync(Imaging.BitmapSource bitmap)
    {
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        if (w <= 0 || h <= 0)
        {
            return Task.FromResult(new OcrDocumentResult(false, string.Empty, [], "空图像无法识别"));
        }

        Imaging.BitmapSource src = bitmap;
        if (src.Format != Media.PixelFormats.Bgra32 && src.Format != Media.PixelFormats.Bgr32)
        {
            var converted = new Imaging.FormatConvertedBitmap(src, Media.PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            src = converted;
        }

        byte[] bgra = new byte[checked(w * h * 4)];
        src.CopyPixels(bgra, w * 4, 0);
        for (int i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }

        return RecognizeBgraAsync(bgra, w, h);
    }

    public static Task<OcrDocumentResult> RecognizeBgraAsync(byte[] bgra, int width, int height)
    {
        var tcs = new TaskCompletionSource<OcrDocumentResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(RecognizeBgraCore(bgra, width, height));
            }
            catch (Exception ex)
            {
                tcs.SetResult(new OcrDocumentResult(false, string.Empty, [], ex.Message));
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    private static OcrDocumentResult RecognizeBgraCore(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return new OcrDocumentResult(false, string.Empty, [], "无效的图像尺寸");
        }

        // Upscale small crops 2x with 16px white padding to boost Windows OcrEngine accuracy.
        bool upscaled = width < 80 || height < 48;
        int pad = upscaled ? 16 : 0;
        int scale = upscaled ? 2 : 1;

        byte[] inputBytes;
        int inW;
        int inH;

        if (upscaled)
        {
            inW = width * 2 + pad * 2;
            inH = height * 2 + pad * 2;
            inputBytes = CreateUpscaledPaddedBgra(bgra, width, height, pad);
        }
        else
        {
            inW = width;
            inH = height;
            inputBytes = bgra;
        }

        (bool ok, List<WinRtOcrInterop.RawOcrLine> rawLines, string? err) =
            WinRtOcrInterop.RecognizeBgraOnMta(inputBytes, inW, inH);

        if (!ok)
        {
            return new OcrDocumentResult(false, string.Empty, [], err ?? "离线 OCR 识别失败");
        }

        var lines = new List<OcrLineResult>(rawLines.Count);

        foreach (WinRtOcrInterop.RawOcrLine rawLine in rawLines)
        {
            string cleaned = CleanCjkSpaces(rawLine.Text);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            var words = new List<OcrWordResult>(rawLine.Words.Count);

            foreach (WinRtOcrInterop.RawOcrWord word in rawLine.Words)
            {
                if (word.Bounds.Width <= 0 || word.Bounds.Height <= 0 || string.IsNullOrEmpty(word.Text)) continue;

                minX = Math.Min(minX, word.Bounds.X);
                minY = Math.Min(minY, word.Bounds.Y);
                maxX = Math.Max(maxX, word.Bounds.X + word.Bounds.Width);
                maxY = Math.Max(maxY, word.Bounds.Y + word.Bounds.Height);

                var wordRect = new Wpf.Rect(
                    Math.Clamp((word.Bounds.X - pad) / scale, 0, width),
                    Math.Clamp((word.Bounds.Y - pad) / scale, 0, height),
                    Math.Max(2, word.Bounds.Width / scale),
                    Math.Max(2, word.Bounds.Height / scale));
                words.Add(new OcrWordResult(word.Text, wordRect));
            }

            if (words.Count == 0) continue;
            words.Sort((a, b) => a.PhysicalBounds.X.CompareTo(b.PhysicalBounds.X));

            Wpf.Rect bounds = minX < double.MaxValue
                ? new Wpf.Rect(
                    Math.Clamp((minX - pad) / scale, 0, width),
                    Math.Clamp((minY - pad) / scale, 0, height),
                    Math.Max(4, (maxX - minX) / scale),
                    Math.Max(4, (maxY - minY) / scale))
                : new Wpf.Rect(0, 0, 0, 0);

            lines.Add(new OcrLineResult(cleaned, bounds, words));
        }

        lines.Sort((a, b) => a.PhysicalBounds.Y.CompareTo(b.PhysicalBounds.Y));

        var fullSb = new StringBuilder();
        foreach (OcrLineResult line in lines)
        {
            if (fullSb.Length > 0) fullSb.AppendLine();
            fullSb.Append(line.Text);
        }

        return new OcrDocumentResult(true, fullSb.ToString(), lines, null);
    }

    internal static string ExtractSelectedText(
        IReadOnlyList<OcrLineResult> lines,
        int startLine,
        int startWord,
        int endLine,
        int endWord,
        bool joinLines = false)
    {
        if (lines.Count == 0) return string.Empty;

        if (startLine > endLine || (startLine == endLine && startWord > endWord))
        {
            (startLine, endLine) = (endLine, startLine);
            (startWord, endWord) = (endWord, startWord);
        }

        startLine = Math.Clamp(startLine, 0, lines.Count - 1);
        endLine = Math.Clamp(endLine, 0, lines.Count - 1);

        var lineTexts = new List<string>();
        for (int li = startLine; li <= endLine; li++)
        {
            IReadOnlyList<OcrWordResult> words = lines[li].Words;
            if (words.Count == 0) continue;

            int fromW = li == startLine ? Math.Clamp(startWord, 0, words.Count - 1) : 0;
            int toW = li == endLine ? Math.Clamp(endWord, 0, words.Count - 1) : words.Count - 1;
            if (fromW > toW) continue;

            var rawJoin = new StringBuilder();
            for (int wi = fromW; wi <= toW; wi++)
            {
                if (rawJoin.Length > 0) rawJoin.Append(' ');
                rawJoin.Append(words[wi].Text);
            }

            string cleaned = CleanCjkSpaces(rawJoin.ToString());
            if (!string.IsNullOrEmpty(cleaned))
            {
                lineTexts.Add(cleaned);
            }
        }

        return string.Join(joinLines ? " " : Environment.NewLine, lineTexts);
    }

    private static byte[] CreateUpscaledPaddedBgra(byte[] src, int srcW, int srcH, int pad)
    {
        int dstW = srcW * 2 + pad * 2;
        int dstH = srcH * 2 + pad * 2;
        byte[] dst = new byte[checked(dstW * dstH * 4)];
        Array.Fill(dst, (byte)255);

        int srcStride = srcW * 4;
        int dstStride = dstW * 4;

        for (int sy = 0; sy < srcH; sy++)
        {
            for (int sx = 0; sx < srcW; sx++)
            {
                int sIdx = sy * srcStride + sx * 4;
                byte b = src[sIdx];
                byte g = src[sIdx + 1];
                byte r = src[sIdx + 2];

                int dxBase = (pad + sx * 2) * 4;
                int dy0 = pad + sy * 2;

                for (int oy = 0; oy < 2; oy++)
                {
                    int rowOff = (dy0 + oy) * dstStride + dxBase;
                    dst[rowOff] = b; dst[rowOff + 1] = g; dst[rowOff + 2] = r; dst[rowOff + 3] = 255;
                    dst[rowOff + 4] = b; dst[rowOff + 5] = g; dst[rowOff + 6] = r; dst[rowOff + 7] = 255;
                }
            }
        }

        return dst;
    }

    internal static string CleanCjkSpaces(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string trimmed = raw.Trim();
        var sb = new StringBuilder(trimmed.Length);

        for (int i = 0; i < trimmed.Length; i++)
        {
            char ch = trimmed[i];
            if (ch == ' ' && sb.Length > 0 && i + 1 < trimmed.Length)
            {
                char prev = sb[^1];
                char next = trimmed[i + 1];
                if (IsCjkOrFullWidth(prev) && IsCjkOrFullWidth(next))
                {
                    continue;
                }
            }
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool IsCjkOrFullWidth(char ch) =>
        ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\u3000' and <= '\u303F')
            or (>= '\uFF00' and <= '\uFFEF');
}
