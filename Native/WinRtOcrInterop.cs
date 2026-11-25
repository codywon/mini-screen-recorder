using System.Runtime.InteropServices;

namespace MiniRec.Native;

internal static unsafe class WinRtOcrInterop
{
    private const uint RO_INIT_MULTITHREADED = 1;
    private const int BitmapPixelFormat_Bgra8 = 87;
    private const int BitmapAlphaMode_Ignore = 2;
    private const int BitmapBufferAccessMode_Write = 2;
    private const int AsyncStatus_Started = 0;
    private const int AsyncStatus_Completed = 1;

    private static readonly Guid IID_ISoftwareBitmapFactory = new("c99feb69-2d62-4d47-a6b3-4fdb6a07fdf8");
    private static readonly Guid IID_IBitmapBuffer = new("a53e04c4-399c-438c-b28f-a63a6b83d1a1");
    private static readonly Guid IID_IMemoryBuffer = new("fbc4dd2a-245b-11e4-af98-689423260cf8");
    private static readonly Guid IID_IMemoryBufferByteAccess = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");
    private static readonly Guid IID_IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    private static readonly Guid IID_IOcrEngineStatics = new("5bffa85a-3384-3540-9940-699120d428a8");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private static readonly Guid IID_IAsyncOperation_OcrResult = new("c7d7118e-ae36-59c0-ac76-7badee711c8b");

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapPlaneDescription
    {
        public int StartIndex;
        public int Width;
        public int Height;
        public int Stride;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }

    internal readonly record struct RawOcrWord(string Text, RectF Bounds);
    internal readonly record struct RawOcrLine(string Text, IReadOnlyList<RawOcrWord> Words);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    private static extern int RoInitialize(uint initType);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    private static extern void RoUninitialize();

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", ExactSpelling = true)]
    private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    public static (bool Success, List<RawOcrLine> Lines, string? ErrorMessage) RecognizeBgraOnMta(
        byte[] bgra,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return (false, [], "无效的图像缓冲区");
        }

        int roHr = RoInitialize(RO_INIT_MULTITHREADED);
        bool shouldUninit = roHr is 0 or 1;

        IntPtr bmpFactory = IntPtr.Zero;
        IntPtr softwareBitmap = IntPtr.Zero;
        IntPtr ocrStatics = IntPtr.Zero;
        IntPtr ocrEngine = IntPtr.Zero;
        IntPtr langVec = IntPtr.Zero;
        IntPtr firstLang = IntPtr.Zero;
        IntPtr asyncOp = IntPtr.Zero;
        IntPtr asyncInfo = IntPtr.Zero;
        IntPtr ocrResult = IntPtr.Zero;
        IntPtr linesVec = IntPtr.Zero;

        try
        {
            ocrStatics = GetActivationFactory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
            if (ocrStatics == IntPtr.Zero)
            {
                return (false, [], "当前系统不支持 Windows OCR 引擎");
            }

            // IOcrEngineStatics::TryCreateFromUserProfileLanguages (slot 10)
            var tryCreateUserProfile = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetSlot(ocrStatics, 10);
            int hr = tryCreateUserProfile(ocrStatics, &ocrEngine);
            if (hr < 0 || ocrEngine == IntPtr.Zero)
            {
                // Fallback: get_AvailableRecognizerLanguages (slot 7) -> TryCreateFromLanguage (slot 9)
                var getAvailLangs = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetSlot(ocrStatics, 7);
                if (getAvailLangs(ocrStatics, &langVec) >= 0 && langVec != IntPtr.Zero)
                {
                    uint langCount = VectorViewGetSize(langVec);
                    if (langCount > 0)
                    {
                        firstLang = VectorViewGetAt(langVec, 0);
                        if (firstLang != IntPtr.Zero)
                        {
                            var tryCreateLang = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)GetSlot(ocrStatics, 9);
                            _ = tryCreateLang(ocrStatics, firstLang, &ocrEngine);
                        }
                    }
                }
            }

            if (ocrEngine == IntPtr.Zero)
            {
                return (false, [], "系统未安装 Windows OCR 语言包（可在“Windows 设置 → 时间和语言”添加中文/英文语言包）");
            }

            bmpFactory = GetActivationFactory("Windows.Graphics.Imaging.SoftwareBitmap", IID_ISoftwareBitmapFactory);
            if (bmpFactory == IntPtr.Zero)
            {
                return (false, [], "无法激活 SoftwareBitmap 工厂");
            }

            // ISoftwareBitmapFactory::CreateWithAlpha (slot 7)
            var createWithAlpha = (delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int, IntPtr*, int>)GetSlot(bmpFactory, 7);
            hr = createWithAlpha(bmpFactory, BitmapPixelFormat_Bgra8, width, height, BitmapAlphaMode_Ignore, &softwareBitmap);
            if (hr < 0 || softwareBitmap == IntPtr.Zero)
            {
                return (false, [], $"创建 SoftwareBitmap 失败 (0x{hr:X8})");
            }

            CopyPixelsIntoSoftwareBitmap(softwareBitmap, bgra, width, height);

            // IOcrEngine::RecognizeAsync (slot 6)
            var recognizeAsync = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)GetSlot(ocrEngine, 6);
            hr = recognizeAsync(ocrEngine, softwareBitmap, &asyncOp);
            if (hr < 0 || asyncOp == IntPtr.Zero)
            {
                return (false, [], $"启动离线 OCR 失败 (0x{hr:X8})");
            }

            asyncInfo = QueryInterface(asyncOp, IID_IAsyncInfo);
            if (asyncInfo == IntPtr.Zero)
            {
                return (false, [], "无法获取 OCR 异步任务接口");
            }

            // Poll IAsyncInfo::get_Status (slot 7)
            var getStatus = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)GetSlot(asyncInfo, 7);
            int status = AsyncStatus_Started;
            int waitedMs = 0;
            while (waitedMs < 10_000)
            {
                hr = getStatus(asyncInfo, &status);
                if (hr < 0 || status != AsyncStatus_Started)
                {
                    break;
                }
                Thread.Sleep(5);
                waitedMs += 5;
            }

            if (status != AsyncStatus_Completed)
            {
                int asyncErr = 0;
                var getErr = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)GetSlot(asyncInfo, 8);
                _ = getErr(asyncInfo, &asyncErr);
                return (false, [], $"OCR 识别未完成 (status={status}, hr=0x{asyncErr:X8})");
            }

            IntPtr asyncResultOp = QueryInterface(asyncOp, IID_IAsyncOperation_OcrResult);
            try
            {
                IntPtr opTarget = asyncResultOp != IntPtr.Zero ? asyncResultOp : asyncOp;
                var getResults = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetSlot(opTarget, 8);
                hr = getResults(opTarget, &ocrResult);
                if (hr < 0 || ocrResult == IntPtr.Zero)
                {
                    return (false, [], $"获取 OCR 结果失败 (0x{hr:X8})");
                }
            }
            finally
            {
                ReleaseCom(asyncResultOp);
            }

            // IOcrResult::get_Lines (slot 6)
            var getLines = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetSlot(ocrResult, 6);
            hr = getLines(ocrResult, &linesVec);
            if (hr < 0 || linesVec == IntPtr.Zero)
            {
                return (false, [], $"读取 OCR 文本行失败 (0x{hr:X8})");
            }

            uint lineCount = VectorViewGetSize(linesVec);
            var parsedLines = new List<RawOcrLine>((int)lineCount);

            for (uint i = 0; i < lineCount; i++)
            {
                IntPtr linePtr = VectorViewGetAt(linesVec, i);
                if (linePtr == IntPtr.Zero) continue;

                try
                {
                    // IOcrLine::get_Text (slot 7)
                    var getLineText = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetSlot(linePtr, 7);
                    IntPtr lineHstr = IntPtr.Zero;
                    string lineText = string.Empty;
                    if (getLineText(linePtr, &lineHstr) >= 0 && lineHstr != IntPtr.Zero)
                    {
                        lineText = HStringToManagedAndFree(lineHstr);
                    }

                    // IOcrLine::get_Words (slot 6)
                    var getWords = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetSlot(linePtr, 6);
                    IntPtr wordsVec = IntPtr.Zero;
                    var words = new List<RawOcrWord>();
                    if (getWords(linePtr, &wordsVec) >= 0 && wordsVec != IntPtr.Zero)
                    {
                        try
                        {
                            uint wordCount = VectorViewGetSize(wordsVec);
                            words.Capacity = (int)wordCount;
                            for (uint w = 0; w < wordCount; w++)
                            {
                                IntPtr wordPtr = VectorViewGetAt(wordsVec, w);
                                if (wordPtr == IntPtr.Zero) continue;
                                try
                                {
                                    // IOcrWord::get_BoundingRect (slot 6), IOcrWord::get_Text (slot 7)
                                    var getRect = (delegate* unmanaged[Stdcall]<IntPtr, RectF*, int>)GetSlot(wordPtr, 6);
                                    var getWordText = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetSlot(wordPtr, 7);

                                    RectF rect = default;
                                    _ = getRect(wordPtr, &rect);

                                    IntPtr wordHstr = IntPtr.Zero;
                                    string wordText = string.Empty;
                                    if (getWordText(wordPtr, &wordHstr) >= 0 && wordHstr != IntPtr.Zero)
                                    {
                                        wordText = HStringToManagedAndFree(wordHstr);
                                    }

                                    words.Add(new RawOcrWord(wordText, rect));
                                }
                                finally
                                {
                                    ReleaseCom(wordPtr);
                                }
                            }
                        }
                        finally
                        {
                            ReleaseCom(wordsVec);
                        }
                    }

                    parsedLines.Add(new RawOcrLine(lineText, words));
                }
                finally
                {
                    ReleaseCom(linePtr);
                }
            }

            return (true, parsedLines, null);
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
        finally
        {
            ReleaseCom(linesVec);
            ReleaseCom(ocrResult);
            ReleaseCom(asyncInfo);
            ReleaseCom(asyncOp);
            ReleaseCom(firstLang);
            ReleaseCom(langVec);
            ReleaseCom(ocrEngine);
            ReleaseCom(ocrStatics);
            CloseAndRelease(softwareBitmap);
            ReleaseCom(bmpFactory);

            if (shouldUninit)
            {
                RoUninitialize();
            }
        }
    }

    private static void CopyPixelsIntoSoftwareBitmap(IntPtr softwareBitmap, byte[] bgra, int width, int height)
    {
        IntPtr bitmapBuffer = IntPtr.Zero;
        IntPtr memBuffer = IntPtr.Zero;
        IntPtr memRef = IntPtr.Zero;
        IntPtr byteAccess = IntPtr.Zero;

        try
        {
            // ISoftwareBitmap::LockBuffer (slot 15)
            var lockBuffer = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)GetSlot(softwareBitmap, 15);
            int hr = lockBuffer(softwareBitmap, BitmapBufferAccessMode_Write, &bitmapBuffer);
            if (hr < 0 || bitmapBuffer == IntPtr.Zero)
            {
                throw new InvalidOperationException($"LockBuffer 失败 (0x{hr:X8})");
            }

            IntPtr ibb = QueryInterface(bitmapBuffer, IID_IBitmapBuffer);
            BitmapPlaneDescription desc;
            try
            {
                IntPtr target = ibb != IntPtr.Zero ? ibb : bitmapBuffer;
                // IBitmapBuffer::GetPlaneDescription (slot 7)
                var getPlaneDesc = (delegate* unmanaged[Stdcall]<IntPtr, int, BitmapPlaneDescription*, int>)GetSlot(target, 7);
                hr = getPlaneDesc(target, 0, &desc);
                if (hr < 0)
                {
                    throw new InvalidOperationException($"GetPlaneDescription 失败 (0x{hr:X8})");
                }
            }
            finally
            {
                ReleaseCom(ibb);
            }

            memBuffer = QueryInterface(bitmapBuffer, IID_IMemoryBuffer);
            if (memBuffer == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法获取 IMemoryBuffer");
            }

            // IMemoryBuffer::CreateReference (slot 6)
            var createRef = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GetSlot(memBuffer, 6);
            hr = createRef(memBuffer, &memRef);
            if (hr < 0 || memRef == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateReference 失败 (0x{hr:X8})");
            }

            byteAccess = QueryInterface(memRef, IID_IMemoryBufferByteAccess);
            if (byteAccess == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法获取 IMemoryBufferByteAccess");
            }

            // IMemoryBufferByteAccess::GetBuffer (IUnknown + slot 3)
            byte* dstPtr = null;
            uint capacity = 0;
            var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)GetSlot(byteAccess, 3);
            hr = getBuffer(byteAccess, &dstPtr, &capacity);
            if (hr < 0 || dstPtr == null)
            {
                throw new InvalidOperationException($"GetBuffer 失败 (0x{hr:X8})");
            }

            int srcStride = width * 4;
            int copyHeight = Math.Min(height, desc.Height);
            int copyRowBytes = Math.Min(srcStride, Math.Abs(desc.Stride));

            fixed (byte* srcBase = bgra)
            {
                for (int row = 0; row < copyHeight; row++)
                {
                    int dstOffset = desc.StartIndex + row * desc.Stride;
                    if (dstOffset < 0 || (uint)(dstOffset + copyRowBytes) > capacity) break;
                    Buffer.MemoryCopy(srcBase + row * srcStride, dstPtr + dstOffset, copyRowBytes, copyRowBytes);
                }
            }
        }
        finally
        {
            ReleaseCom(byteAccess);
            CloseAndRelease(memRef);
            ReleaseCom(memBuffer);
            CloseAndRelease(bitmapBuffer);
        }
    }

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        if (WindowsCreateString(className, (uint)className.Length, out IntPtr hstr) < 0 || hstr == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            int hr = RoGetActivationFactory(hstr, ref iid, out IntPtr factory);
            return hr >= 0 ? factory : IntPtr.Zero;
        }
        finally
        {
            _ = WindowsDeleteString(hstr);
        }
    }

    private static uint VectorViewGetSize(IntPtr vec)
    {
        uint size = 0;
        var getSize = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)GetSlot(vec, 7);
        _ = getSize(vec, &size);
        return size;
    }

    private static IntPtr VectorViewGetAt(IntPtr vec, uint index)
    {
        IntPtr item = IntPtr.Zero;
        var getAt = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)GetSlot(vec, 6);
        _ = getAt(vec, index, &item);
        return item;
    }

    private static string HStringToManagedAndFree(IntPtr hstr)
    {
        try
        {
            char* raw = WindowsGetStringRawBuffer(hstr, out uint len);
            return raw != null && len > 0 ? new string(raw, 0, (int)len) : string.Empty;
        }
        finally
        {
            _ = WindowsDeleteString(hstr);
        }
    }

    private static IntPtr QueryInterface(IntPtr unk, Guid iid)
    {
        if (unk == IntPtr.Zero) return IntPtr.Zero;
        IntPtr ppv = IntPtr.Zero;
        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)GetSlot(unk, 0);
        int hr = qi(unk, &iid, &ppv);
        return hr >= 0 ? ppv : IntPtr.Zero;
    }

    private static void CloseAndRelease(IntPtr inspectable)
    {
        if (inspectable == IntPtr.Zero) return;
        IntPtr closable = QueryInterface(inspectable, IID_IClosable);
        if (closable != IntPtr.Zero)
        {
            try
            {
                var closeFn = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetSlot(closable, 6);
                _ = closeFn(closable);
            }
            finally
            {
                ReleaseCom(closable);
            }
        }
        ReleaseCom(inspectable);
    }

    private static void ReleaseCom(IntPtr unk)
    {
        if (unk == IntPtr.Zero) return;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetSlot(unk, 2);
        _ = release(unk);
    }

    private static IntPtr GetSlot(IntPtr comPtr, int index)
    {
        IntPtr* vtbl = *(IntPtr**)comPtr;
        return vtbl[index];
    }
}
