using System.Runtime.InteropServices;
using MiniRec.Native;

namespace MiniRec.Services;

internal sealed unsafe class GdiScreenCapturer : IDisposable
{
    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;
    private readonly int _byteLength;

    private IntPtr _memDc;
    private IntPtr _dibBitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bitsPtr;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;
    public int FrameByteLength => _byteLength;

    public GdiScreenCapturer(int x, int y, int width, int height)
    {
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _byteLength = width * height * 4;

        IntPtr screenDc = Win32Interop.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法获取桌面绘图上下文 (GetDC)");
        }

        try
        {
            _memDc = Win32Interop.CreateCompatibleDC(screenDc);
            if (_memDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建兼容绘图上下文 (CreateCompatibleDC)");
            }

            var bmi = new Win32Interop.BITMAPINFO
            {
                bmiHeader = new Win32Interop.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                    biWidth = _width,
                    biHeight = -_height, // Top-down DIB matching positive MF stride
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            _dibBitmap = Win32Interop.CreateDIBSection(_memDc, ref bmi, 0, out _bitsPtr, IntPtr.Zero, 0);
            if (_dibBitmap == IntPtr.Zero || _bitsPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建屏幕帧缓冲区 (CreateDIBSection)");
            }

            _oldBitmap = Win32Interop.SelectObject(_memDc, _dibBitmap);
        }
        finally
        {
            Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public bool TryCaptureFrame(IntPtr destPtr, int destCapacityBytes, out int lastError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lastError = 0;

        IntPtr screenDc = Win32Interop.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            lastError = Marshal.GetLastWin32Error();
            return false;
        }

        bool bltOk;
        try
        {
            bltOk = Win32Interop.BitBlt(
                _memDc,
                0,
                0,
                _width,
                _height,
                screenDc,
                _x,
                _y,
                Win32Interop.SRCCOPY | Win32Interop.CAPTUREBLT);

            if (!bltOk)
            {
                lastError = Marshal.GetLastWin32Error();
            }
        }
        finally
        {
            Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (!bltOk)
        {
            return false;
        }

        DrawCursorOverlay();

        if (destPtr != IntPtr.Zero && destCapacityBytes >= _byteLength)
        {
            Buffer.MemoryCopy((void*)_bitsPtr, (void*)destPtr, destCapacityBytes, _byteLength);
        }

        return true;
    }

    private void DrawCursorOverlay()
    {
        var ci = new Win32Interop.CURSORINFO
        {
            cbSize = (uint)Marshal.SizeOf<Win32Interop.CURSORINFO>()
        };

        if (!Win32Interop.GetCursorInfo(ref ci) ||
            (ci.flags & Win32Interop.CURSOR_SHOWING) == 0 ||
            ci.hCursor == IntPtr.Zero)
        {
            return;
        }

        int drawX = ci.ptScreenPos.X - _x;
        int drawY = ci.ptScreenPos.Y - _y;

        if (Win32Interop.GetIconInfo(ci.hCursor, out Win32Interop.ICONINFO iconInfo))
        {
            try
            {
                drawX -= iconInfo.xHotspot;
                drawY -= iconInfo.yHotspot;
            }
            finally
            {
                if (iconInfo.hbmColor != IntPtr.Zero) Win32Interop.DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero) Win32Interop.DeleteObject(iconInfo.hbmMask);
            }
        }

        if (drawX >= -64 && drawX < _width + 64 && drawY >= -64 && drawY < _height + 64)
        {
            Win32Interop.DrawIconEx(_memDc, drawX, drawY, ci.hCursor, 0, 0, 0, IntPtr.Zero, Win32Interop.DI_NORMAL);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero)
            {
                Win32Interop.SelectObject(_memDc, _oldBitmap);
                _oldBitmap = IntPtr.Zero;
            }
            if (_dibBitmap != IntPtr.Zero)
            {
                Win32Interop.DeleteObject(_dibBitmap);
                _dibBitmap = IntPtr.Zero;
            }
            Win32Interop.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }

        _bitsPtr = IntPtr.Zero;
    }
}
