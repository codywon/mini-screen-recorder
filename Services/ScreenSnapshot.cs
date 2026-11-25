using System.Runtime.InteropServices;
using MiniRec.Native;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Services;

internal sealed unsafe class ScreenSnapshot
{
    private readonly byte[] _pixels;

    public int VirtualX { get; }
    public int VirtualY { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    internal ScreenSnapshot(int virtualX, int virtualY, int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "快照宽高必须大于 0");
        }
        int expectedLength = checked(width * height * 4);
        if (pixels.Length < expectedLength)
        {
            throw new ArgumentException("像素缓冲区大小不足", nameof(pixels));
        }

        VirtualX = virtualX;
        VirtualY = virtualY;
        Width = width;
        Height = height;
        Stride = width * 4;
        _pixels = pixels;
    }

    public static ScreenSnapshot CaptureVirtualScreen()
    {
        (int vx, int vy, int vw, int vh) = Win32Interop.GetVirtualScreenBounds();
        if (vw <= 0 || vh <= 0)
        {
            (vw, vh) = Win32Interop.GetPrimaryScreenSize();
            vx = 0;
            vy = 0;
        }

        byte[] buffer = new byte[checked(vw * vh * 4)];

        IntPtr screenDc = Win32Interop.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法获取桌面绘图上下文 (GetDC)");
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr dibBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            memDc = Win32Interop.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建兼容绘图上下文 (CreateCompatibleDC)");
            }

            var bmi = new Win32Interop.BITMAPINFO
            {
                bmiHeader = new Win32Interop.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                    biWidth = vw,
                    biHeight = -vh, // Top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            dibBitmap = Win32Interop.CreateDIBSection(memDc, ref bmi, 0, out IntPtr bitsPtr, IntPtr.Zero, 0);
            if (dibBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建屏幕快照缓冲区 (CreateDIBSection)");
            }

            oldBitmap = Win32Interop.SelectObject(memDc, dibBitmap);

            if (!Win32Interop.BitBlt(
                    memDc,
                    0,
                    0,
                    vw,
                    vh,
                    screenDc,
                    vx,
                    vy,
                    Win32Interop.SRCCOPY | Win32Interop.CAPTUREBLT))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"桌面抓屏失败 (BitBlt, error={err})");
            }

            Marshal.Copy(bitsPtr, buffer, 0, buffer.Length);
        }
        finally
        {
            if (memDc != IntPtr.Zero)
            {
                if (oldBitmap != IntPtr.Zero)
                {
                    Win32Interop.SelectObject(memDc, oldBitmap);
                }
                if (dibBitmap != IntPtr.Zero)
                {
                    Win32Interop.DeleteObject(dibBitmap);
                }
                Win32Interop.DeleteDC(memDc);
            }

            Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
        }

        return new ScreenSnapshot(vx, vy, vw, vh, buffer);
    }

    public Imaging.BitmapSource CreateFullBitmapSource()
    {
        var bmp = Imaging.BitmapSource.Create(
            Width,
            Height,
            96,
            96,
            Media.PixelFormats.Bgr32,
            null,
            _pixels,
            Stride);
        bmp.Freeze();
        return bmp;
    }

    public bool TryGetPixel(int physX, int physY, out byte r, out byte g, out byte b)
    {
        int localX = physX - VirtualX;
        int localY = physY - VirtualY;
        if (localX < 0 || localX >= Width || localY < 0 || localY >= Height)
        {
            r = g = b = 0;
            return false;
        }

        int idx = localY * Stride + localX * 4;
        b = _pixels[idx];
        g = _pixels[idx + 1];
        r = _pixels[idx + 2];
        return true;
    }

    public void CopyMagnifierPatch(int centerPhysX, int centerPhysY, int patchSize, byte[] destBgr32)
    {
        int expectedLen = checked(patchSize * patchSize * 4);
        if (destBgr32.Length < expectedLen)
        {
            throw new ArgumentException("放大镜缓冲区大小不足", nameof(destBgr32));
        }

        int half = patchSize / 2;
        int startX = centerPhysX - half;
        int startY = centerPhysY - half;

        for (int row = 0; row < patchSize; row++)
        {
            int py = startY + row - VirtualY;
            int destRowOffset = row * patchSize * 4;
            bool validY = py >= 0 && py < Height;

            for (int col = 0; col < patchSize; col++)
            {
                int px = startX + col - VirtualX;
                int destIdx = destRowOffset + col * 4;

                if (validY && px >= 0 && px < Width)
                {
                    int srcIdx = py * Stride + px * 4;
                    destBgr32[destIdx] = _pixels[srcIdx];
                    destBgr32[destIdx + 1] = _pixels[srcIdx + 1];
                    destBgr32[destIdx + 2] = _pixels[srcIdx + 2];
                    destBgr32[destIdx + 3] = 255;
                }
                else
                {
                    destBgr32[destIdx] = 42;
                    destBgr32[destIdx + 1] = 23;
                    destBgr32[destIdx + 2] = 15;
                    destBgr32[destIdx + 3] = 255;
                }
            }
        }
    }

    public byte[] CropRegionBgraBytes(CaptureRegion region, out int outW, out int outH)
    {
        int vRight = VirtualX + Width;
        int vBottom = VirtualY + Height;

        int left = Math.Clamp(region.X, VirtualX, vRight - 1);
        int top = Math.Clamp(region.Y, VirtualY, vBottom - 1);
        int right = Math.Clamp(region.X + Math.Max(1, region.Width), left + 1, vRight);
        int bottom = Math.Clamp(region.Y + Math.Max(1, region.Height), top + 1, vBottom);

        outW = right - left;
        outH = bottom - top;

        int dstStride = outW * 4;
        byte[] result = new byte[checked(outH * dstStride)];

        int localLeft = left - VirtualX;
        int localTop = top - VirtualY;

        fixed (byte* srcBase = _pixels)
        fixed (byte* dstBase = result)
        {
            for (int row = 0; row < outH; row++)
            {
                byte* srcRow = srcBase + (localTop + row) * Stride + localLeft * 4;
                byte* dstRow = dstBase + row * dstStride;
                Buffer.MemoryCopy(srcRow, dstRow, dstStride, dstStride);

                // Ensure alpha byte is 255 for consistent BGRA32 composition/OCR usage.
                for (int col = 0; col < outW; col++)
                {
                    dstRow[col * 4 + 3] = 255;
                }
            }
        }

        return result;
    }

    public Imaging.BitmapSource CropRegionBitmap(CaptureRegion region)
    {
        byte[] cropped = CropRegionBgraBytes(region, out int outW, out int outH);
        var bmp = Imaging.BitmapSource.Create(
            outW,
            outH,
            96,
            96,
            Media.PixelFormats.Bgr32,
            null,
            cropped,
            outW * 4);
        bmp.Freeze();
        return bmp;
    }
}
