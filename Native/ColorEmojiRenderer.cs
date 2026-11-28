using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MiniRec.Services;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Native;

/// <summary>
/// 纯原生 Direct2D + DirectWrite 真彩色 Emoji 渲染器（利用 Windows 10/11 系统自带 Segoe UI Emoji 彩色字体，零外部依赖）。
/// </summary>
internal static unsafe class ColorEmojiRenderer
{
    public static readonly string[] PresetEmojis =
    [
        "😀", "😂", "🥹", "😍", "🤔", "😎", "😭", "🤯",
        "👍", "👏", "🙏", "👌", "💪", "👇", "👉", "🎉",
        "❤️", "🔥", "⭐", "✅", "❌", "⚠️", "💡", "📌"
    ];

    private const uint D2D1_FACTORY_TYPE_MULTI_THREADED = 1;
    private const uint D2D1_RENDER_TARGET_TYPE_DEFAULT = 0;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D2D1_ALPHA_MODE_PREMULTIPLIED = 1;
    private const uint D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT = 0x00000004;
    private const uint DWRITE_MEASURING_MODE_NATURAL = 0;
    private const uint DWRITE_FACTORY_TYPE_SHARED = 0;
    private const uint DWRITE_FONT_WEIGHT_NORMAL = 400;
    private const uint DWRITE_FONT_STYLE_NORMAL = 0;
    private const uint DWRITE_FONT_STRETCH_NORMAL = 5;
    private const uint DWRITE_TEXT_ALIGNMENT_CENTER = 2;
    private const uint DWRITE_PARAGRAPH_ALIGNMENT_CENTER = 2;

    private static readonly Guid IID_ID2D1Factory = new("06152247-6f50-465a-9245-118bfd3b6007");
    private static readonly Guid IID_IDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    private static readonly object RenderLock = new();
    private static readonly ConcurrentDictionary<(string Emoji, int PixelSize), Imaging.BitmapSource?> Cache = new();

    private static IntPtr _d2dFactory;
    private static IntPtr _dwriteFactory;
    private static IntPtr _dcRenderTarget;
    private static IntPtr _defaultBrush;
    private static bool _initFailed;

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_PIXEL_FORMAT
    {
        public uint format;
        public uint alphaMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_RENDER_TARGET_PROPERTIES
    {
        public uint type;
        public D2D1_PIXEL_FORMAT pixelFormat;
        public float dpiX;
        public float dpiY;
        public uint usage;
        public uint minLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_COLOR_F
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_RECT_F
    {
        public float left;
        public float top;
        public float right;
        public float bottom;
    }

    [DllImport("d2d1.dll", ExactSpelling = true)]
    private static extern int D2D1CreateFactory(
        uint factoryType,
        ref Guid riid,
        IntPtr pFactoryOptions,
        out IntPtr ppIFactory);

    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(
        uint factoryType,
        ref Guid iid,
        out IntPtr factory);

    public static Imaging.BitmapSource? RenderEmoji(string emoji, int pixelSize = 96)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return null;
        int clamped = Math.Clamp(pixelSize, 24, 192);
        return Cache.GetOrAdd((emoji, clamped), static key => RenderEmojiCore(key.Emoji, key.PixelSize));
    }

    private static Imaging.BitmapSource? RenderEmojiCore(string emoji, int pixelSize)
    {
        lock (RenderLock)
        {
            if (!EnsureInitialized())
            {
                return null;
            }

            IntPtr screenDc = Win32Interop.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            IntPtr memDc = IntPtr.Zero;
            IntPtr dib = IntPtr.Zero;
            IntPtr oldBmp = IntPtr.Zero;
            IntPtr textFormat = IntPtr.Zero;

            try
            {
                memDc = Win32Interop.CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return null;

                var bmi = new Win32Interop.BITMAPINFO
                {
                    bmiHeader = new Win32Interop.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                        biWidth = pixelSize,
                        biHeight = -pixelSize, // Top-down DIB
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0 // BI_RGB
                    }
                };

                dib = Win32Interop.CreateDIBSection(memDc, ref bmi, 0, out IntPtr bitsPtr, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bitsPtr == IntPtr.Zero) return null;

                oldBmp = Win32Interop.SelectObject(memDc, dib);

                // IDWriteFactory::CreateTextFormat (slot 15)
                float fontSize = pixelSize * 0.72f;
                var createTextFormat = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, uint, uint, uint, float, char*, IntPtr*, int>)GetSlot(_dwriteFactory, 15);
                fixed (char* fontName = "Segoe UI Emoji")
                fixed (char* locale = "en-US")
                {
                    int hrFmt = createTextFormat(
                        _dwriteFactory,
                        fontName,
                        IntPtr.Zero,
                        DWRITE_FONT_WEIGHT_NORMAL,
                        DWRITE_FONT_STYLE_NORMAL,
                        DWRITE_FONT_STRETCH_NORMAL,
                        fontSize,
                        locale,
                        &textFormat);
                    if (hrFmt < 0 || textFormat == IntPtr.Zero) return null;
                }

                // IDWriteTextFormat::SetTextAlignment (slot 3) & SetParagraphAlignment (slot 4)
                var setTextAlign = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)GetSlot(textFormat, 3);
                var setParaAlign = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)GetSlot(textFormat, 4);
                _ = setTextAlign(textFormat, DWRITE_TEXT_ALIGNMENT_CENTER);
                _ = setParaAlign(textFormat, DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

                // ID2D1DCRenderTarget::BindDC (slot 57)
                var bindRect = new Win32Interop.RECT { Left = 0, Top = 0, Right = pixelSize, Bottom = pixelSize };
                var bindDc = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Win32Interop.RECT*, int>)GetSlot(_dcRenderTarget, 57);
                int hrBind = bindDc(_dcRenderTarget, memDc, &bindRect);
                if (hrBind < 0) return null;

                // BeginDraw (slot 48) -> Clear (slot 47) -> DrawText (slot 27) -> EndDraw (slot 49)
                var beginDraw = (delegate* unmanaged[Stdcall]<IntPtr, void>)GetSlot(_dcRenderTarget, 48);
                var clear = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, void>)GetSlot(_dcRenderTarget, 47);
                var drawText = (delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, D2D1_RECT_F*, IntPtr, uint, uint, void>)GetSlot(_dcRenderTarget, 27);
                var endDraw = (delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int>)GetSlot(_dcRenderTarget, 49);

                var transparent = new D2D1_COLOR_F { r = 0f, g = 0f, b = 0f, a = 0f };
                var layoutRect = new D2D1_RECT_F { left = 0f, top = 0f, right = pixelSize, bottom = pixelSize };

                beginDraw(_dcRenderTarget);
                clear(_dcRenderTarget, &transparent);
                fixed (char* textPtr = emoji)
                {
                    drawText(
                        _dcRenderTarget,
                        textPtr,
                        (uint)emoji.Length,
                        textFormat,
                        &layoutRect,
                        _defaultBrush,
                        D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT,
                        DWRITE_MEASURING_MODE_NATURAL);
                }

                ulong tag1 = 0, tag2 = 0;
                int hrEnd = endDraw(_dcRenderTarget, &tag1, &tag2);
                if (hrEnd < 0) return null;

                int byteLen = pixelSize * pixelSize * 4;
                byte[] pixels = new byte[byteLen];
                Marshal.Copy(bitsPtr, pixels, 0, byteLen);

                var bmp = Imaging.BitmapSource.Create(
                    pixelSize,
                    pixelSize,
                    96,
                    96,
                    Media.PixelFormats.Pbgra32,
                    null,
                    pixels,
                    pixelSize * 4);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                ReleaseCom(ref textFormat);
                if (memDc != IntPtr.Zero)
                {
                    if (oldBmp != IntPtr.Zero) Win32Interop.SelectObject(memDc, oldBmp);
                    if (dib != IntPtr.Zero) Win32Interop.DeleteObject(dib);
                    Win32Interop.DeleteDC(memDc);
                }
                Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    private static bool EnsureInitialized()
    {
        if (_dcRenderTarget != IntPtr.Zero && _defaultBrush != IntPtr.Zero && _dwriteFactory != IntPtr.Zero)
        {
            return true;
        }
        if (_initFailed) return false;

        try
        {
            Guid d2dIid = IID_ID2D1Factory;
            int hr1 = D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, ref d2dIid, IntPtr.Zero, out _d2dFactory);
            if (hr1 < 0 || _d2dFactory == IntPtr.Zero)
            {
                _initFailed = true;
                return false;
            }

            Guid dwIid = IID_IDWriteFactory;
            int hr2 = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, ref dwIid, out _dwriteFactory);
            if (hr2 < 0 || _dwriteFactory == IntPtr.Zero)
            {
                _initFailed = true;
                return false;
            }

            // ID2D1Factory::CreateDCRenderTarget (slot 16)
            var props = new D2D1_RENDER_TARGET_PROPERTIES
            {
                type = D2D1_RENDER_TARGET_TYPE_DEFAULT,
                pixelFormat = new D2D1_PIXEL_FORMAT
                {
                    format = DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED
                },
                dpiX = 96f,
                dpiY = 96f,
                usage = 0,
                minLevel = 0
            };

            IntPtr dcRt = IntPtr.Zero;
            var createDcRt = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_RENDER_TARGET_PROPERTIES*, IntPtr*, int>)GetSlot(_d2dFactory, 16);
            int hr3 = createDcRt(_d2dFactory, &props, &dcRt);
            _dcRenderTarget = dcRt;
            if (hr3 < 0 || _dcRenderTarget == IntPtr.Zero)
            {
                _initFailed = true;
                return false;
            }

            // ID2D1RenderTarget::CreateSolidColorBrush (slot 8)
            IntPtr brush = IntPtr.Zero;
            var brushColor = new D2D1_COLOR_F { r = 0.1f, g = 0.1f, b = 0.1f, a = 1f };
            var createBrush = (delegate* unmanaged[Stdcall]<IntPtr, D2D1_COLOR_F*, IntPtr, IntPtr*, int>)GetSlot(_dcRenderTarget, 8);
            int hr4 = createBrush(_dcRenderTarget, &brushColor, IntPtr.Zero, &brush);
            _defaultBrush = brush;
            if (hr4 < 0 || _defaultBrush == IntPtr.Zero)
            {
                _initFailed = true;
                return false;
            }

            return true;
        }
        catch
        {
            _initFailed = true;
            return false;
        }
    }

    private static IntPtr GetSlot(IntPtr comPtr, int index)
    {
        IntPtr vtable = *(IntPtr*)comPtr;
        return *((IntPtr*)vtable + index);
    }

    private static void ReleaseCom(ref IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetSlot(ptr, 2);
        _ = release(ptr);
        ptr = IntPtr.Zero;
    }
}
