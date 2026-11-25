using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MiniRec.Native;
using MF = MiniRec.Native.MediaFoundationInterop;

namespace MiniRec.Services;

internal sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public CaptureRegion Normalize()
    {
        int evenWidth = Math.Max(16, Width & ~1);
        int evenHeight = Math.Max(16, Height & ~1);
        return new CaptureRegion(X, Y, evenWidth, evenHeight);
    }

    public bool IsWithinPrimaryScreen(int primaryWidth, int primaryHeight) =>
        X >= 0 && Y >= 0 && X + Width <= primaryWidth && Y + Height <= primaryHeight;
}

internal enum RegionHitZone
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left
}

internal static class RegionEditMath
{
    public static CaptureRegion ApplyMove(
        CaptureRegion rect,
        int dx,
        int dy,
        int vx,
        int vy,
        int vw,
        int vh)
    {
        int maxX = Math.Max(vx, vx + vw - rect.Width);
        int maxY = Math.Max(vy, vy + vh - rect.Height);
        int nx = Math.Clamp(rect.X + dx, vx, maxX);
        int ny = Math.Clamp(rect.Y + dy, vy, maxY);
        return new CaptureRegion(nx, ny, rect.Width, rect.Height);
    }

    public static CaptureRegion ApplyResize(
        CaptureRegion rect,
        RegionHitZone handle,
        int dx,
        int dy,
        int minSize,
        int vx,
        int vy,
        int vw,
        int vh)
    {
        int left = rect.X;
        int top = rect.Y;
        int right = rect.X + rect.Width;
        int bottom = rect.Y + rect.Height;

        int vRight = vx + vw;
        int vBottom = vy + vh;

        switch (handle)
        {
            case RegionHitZone.TopLeft:
                left = Math.Clamp(left + dx, vx, right - minSize);
                top = Math.Clamp(top + dy, vy, bottom - minSize);
                break;
            case RegionHitZone.Top:
                top = Math.Clamp(top + dy, vy, bottom - minSize);
                break;
            case RegionHitZone.TopRight:
                right = Math.Clamp(right + dx, left + minSize, vRight);
                top = Math.Clamp(top + dy, vy, bottom - minSize);
                break;
            case RegionHitZone.Right:
                right = Math.Clamp(right + dx, left + minSize, vRight);
                break;
            case RegionHitZone.BottomRight:
                right = Math.Clamp(right + dx, left + minSize, vRight);
                bottom = Math.Clamp(bottom + dy, top + minSize, vBottom);
                break;
            case RegionHitZone.Bottom:
                bottom = Math.Clamp(bottom + dy, top + minSize, vBottom);
                break;
            case RegionHitZone.BottomLeft:
                left = Math.Clamp(left + dx, vx, right - minSize);
                bottom = Math.Clamp(bottom + dy, top + minSize, vBottom);
                break;
            case RegionHitZone.Left:
                left = Math.Clamp(left + dx, vx, right - minSize);
                break;
        }

        return new CaptureRegion(left, top, right - left, bottom - top);
    }
}

internal sealed record RecordStartResult(
    bool Success,
    string? Backend,
    string? OutputPath,
    string? Error,
    string? Warning = null);

internal sealed record RecordStopResult(bool Success, string? FilePath, string? Error);

internal sealed unsafe class MfMp4Writer : IDisposable
{
    private readonly object _writeLock = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameBytes;

    private MF.IMFSinkWriter? _writer;
    private uint _videoStreamIndex;
    private uint _audioStreamIndex;
    private bool _hasAudio;
    private bool _mfStarted;
    private bool _finalized;
    private bool _disposed;

    public bool IsHardwareAccelerated { get; private set; }
    public bool HasAudio => _hasAudio;

    private MfMp4Writer(int width, int height)
    {
        _width = width;
        _height = height;
        _frameBytes = width * height * 4;
    }

    public static MfMp4Writer Create(string outputPath, int width, int height, int fps, bool enableAudio)
    {
        var instance = new MfMp4Writer(width, height);
        try
        {
            instance.Initialize(outputPath, fps, enableAudio);
            return instance;
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    private void Initialize(string outputPath, int fps, bool enableAudio)
    {
        int hr = MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_NOSOCKET);
        ThrowIfFailed(hr, "MFStartup");
        _mfStarted = true;

        if (!TryInitSinkWriter(outputPath, fps, enableAudio, tryHardware: true, out string? hwError))
        {
            if (!TryInitSinkWriter(outputPath, fps, enableAudio, tryHardware: false, out string? swError))
            {
                throw new InvalidOperationException(swError ?? hwError ?? "无法初始化 Media Foundation 编码器");
            }
            IsHardwareAccelerated = false;
        }
        else
        {
            IsHardwareAccelerated = true;
        }
    }

    private bool TryInitSinkWriter(string outputPath, int fps, bool enableAudio, bool tryHardware, out string? error)
    {
        error = null;
        MF.IMFAttributes? attrs = null;
        MF.IMFSinkWriter? writer = null;
        MF.IMFMediaType? videoOut = null;
        MF.IMFMediaType? videoIn = null;
        MF.IMFMediaType? audioOut = null;
        MF.IMFMediaType? audioIn = null;

        try
        {
            int hr = MF.MFCreateAttributes(out attrs, 4);
            if (hr < 0) { error = $"MFCreateAttributes: 0x{hr:X8}"; return false; }

            Guid keyHw = MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
            attrs.SetUINT32(ref keyHw, tryHardware ? 1u : 0u);

            Guid keyThrottle = MF.MF_SINK_WRITER_DISABLE_THROTTLING;
            attrs.SetUINT32(ref keyThrottle, 1u);

            hr = MF.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, attrs, out writer);
            if (hr < 0 || writer is null) { error = $"MFCreateSinkWriterFromURL: 0x{hr:X8}"; return false; }

            // Video output (H.264)
            hr = MF.MFCreateMediaType(out videoOut);
            if (hr < 0) { error = $"MFCreateMediaType(videoOut): 0x{hr:X8}"; return false; }

            ConfigureVideoOutType(videoOut, _width, _height, fps);
            hr = writer.AddStream(videoOut, out _videoStreamIndex);
            if (hr < 0) { error = $"AddStream(H264): 0x{hr:X8}"; return false; }

            // Video input (RGB32 top-down)
            hr = MF.MFCreateMediaType(out videoIn);
            if (hr < 0) { error = $"MFCreateMediaType(videoIn): 0x{hr:X8}"; return false; }

            ConfigureVideoInType(videoIn, _width, _height, fps);
            hr = writer.SetInputMediaType(_videoStreamIndex, videoIn, null);
            if (hr < 0) { error = $"SetInputMediaType(RGB32): 0x{hr:X8}"; return false; }

            if (enableAudio)
            {
                hr = MF.MFCreateMediaType(out audioOut);
                if (hr < 0) { error = $"MFCreateMediaType(audioOut): 0x{hr:X8}"; return false; }

                ConfigureAudioOutType(audioOut);
                hr = writer.AddStream(audioOut, out _audioStreamIndex);
                if (hr < 0) { error = $"AddStream(AAC): 0x{hr:X8}"; return false; }

                hr = MF.MFCreateMediaType(out audioIn);
                if (hr < 0) { error = $"MFCreateMediaType(audioIn): 0x{hr:X8}"; return false; }

                ConfigureAudioInType(audioIn);
                hr = writer.SetInputMediaType(_audioStreamIndex, audioIn, null);
                if (hr < 0) { error = $"SetInputMediaType(PCM): 0x{hr:X8}"; return false; }
            }

            hr = writer.BeginWriting();
            if (hr < 0)
            {
                error = $"BeginWriting: 0x{hr:X8}";
                return false;
            }

            _writer = writer;
            writer = null; // Ownership transferred
            _hasAudio = enableAudio;
            return true;
        }
        finally
        {
            if (audioIn is not null) Marshal.ReleaseComObject(audioIn);
            if (audioOut is not null) Marshal.ReleaseComObject(audioOut);
            if (videoIn is not null) Marshal.ReleaseComObject(videoIn);
            if (videoOut is not null) Marshal.ReleaseComObject(videoOut);
            if (writer is not null) Marshal.ReleaseComObject(writer);
            if (attrs is not null) Marshal.ReleaseComObject(attrs);
        }
    }

    private static void ConfigureVideoOutType(MF.IMFMediaType type, int width, int height, int fps)
    {
        Guid kMajor = MF.MF_MT_MAJOR_TYPE;
        Guid vMajor = MF.MFMediaType_Video;
        ThrowIfFailed(type.SetGUID(ref kMajor, ref vMajor), "VideoOut.MajorType");

        Guid kSub = MF.MF_MT_SUBTYPE;
        Guid vSub = MF.MFVideoFormat_H264;
        ThrowIfFailed(type.SetGUID(ref kSub, ref vSub), "VideoOut.Subtype");

        Guid kBitrate = MF.MF_MT_AVG_BITRATE;
        ThrowIfFailed(type.SetUINT32(ref kBitrate, (uint)ScreenRecorder.CalculateVideoBitrate(width, height, fps)), "VideoOut.Bitrate");

        Guid kInterlace = MF.MF_MT_INTERLACE_MODE;
        ThrowIfFailed(type.SetUINT32(ref kInterlace, MF.MFVideoInterlace_Progressive), "VideoOut.Interlace");

        Guid kProfile = MF.MF_MT_MPEG2_PROFILE;
        ThrowIfFailed(type.SetUINT32(ref kProfile, MF.eAVEncH264VProfile_Main), "VideoOut.Profile");

        Guid kSize = MF.MF_MT_FRAME_SIZE;
        ThrowIfFailed(type.SetUINT64(ref kSize, MF.PackUint64((uint)width, (uint)height)), "VideoOut.FrameSize");

        Guid kRate = MF.MF_MT_FRAME_RATE;
        ThrowIfFailed(type.SetUINT64(ref kRate, MF.PackUint64((uint)fps, 1u)), "VideoOut.FrameRate");

        Guid kAspect = MF.MF_MT_PIXEL_ASPECT_RATIO;
        ThrowIfFailed(type.SetUINT64(ref kAspect, MF.PackUint64(1u, 1u)), "VideoOut.Aspect");
    }

    private static void ConfigureVideoInType(MF.IMFMediaType type, int width, int height, int fps)
    {
        Guid kMajor = MF.MF_MT_MAJOR_TYPE;
        Guid vMajor = MF.MFMediaType_Video;
        ThrowIfFailed(type.SetGUID(ref kMajor, ref vMajor), "VideoIn.MajorType");

        Guid kSub = MF.MF_MT_SUBTYPE;
        Guid vSub = MF.MFVideoFormat_RGB32;
        ThrowIfFailed(type.SetGUID(ref kSub, ref vSub), "VideoIn.Subtype");

        Guid kInterlace = MF.MF_MT_INTERLACE_MODE;
        ThrowIfFailed(type.SetUINT32(ref kInterlace, MF.MFVideoInterlace_Progressive), "VideoIn.Interlace");

        Guid kStride = MF.MF_MT_DEFAULT_STRIDE;
        ThrowIfFailed(type.SetUINT32(ref kStride, (uint)(width * 4)), "VideoIn.Stride");

        Guid kSize = MF.MF_MT_FRAME_SIZE;
        ThrowIfFailed(type.SetUINT64(ref kSize, MF.PackUint64((uint)width, (uint)height)), "VideoIn.FrameSize");

        Guid kRate = MF.MF_MT_FRAME_RATE;
        ThrowIfFailed(type.SetUINT64(ref kRate, MF.PackUint64((uint)fps, 1u)), "VideoIn.FrameRate");

        Guid kAspect = MF.MF_MT_PIXEL_ASPECT_RATIO;
        ThrowIfFailed(type.SetUINT64(ref kAspect, MF.PackUint64(1u, 1u)), "VideoIn.Aspect");
    }

    private static void ConfigureAudioOutType(MF.IMFMediaType type)
    {
        Guid kMajor = MF.MF_MT_MAJOR_TYPE;
        Guid vMajor = MF.MFMediaType_Audio;
        ThrowIfFailed(type.SetGUID(ref kMajor, ref vMajor), "AudioOut.MajorType");

        Guid kSub = MF.MF_MT_SUBTYPE;
        Guid vSub = MF.MFAudioFormat_AAC;
        ThrowIfFailed(type.SetGUID(ref kSub, ref vSub), "AudioOut.Subtype");

        Guid kCh = MF.MF_MT_AUDIO_NUM_CHANNELS;
        ThrowIfFailed(type.SetUINT32(ref kCh, WasapiAudioTrack.Channels), "AudioOut.Channels");

        Guid kRate = MF.MF_MT_AUDIO_SAMPLES_PER_SECOND;
        ThrowIfFailed(type.SetUINT32(ref kRate, WasapiAudioTrack.SampleRate), "AudioOut.SampleRate");

        Guid kBits = MF.MF_MT_AUDIO_BITS_PER_SAMPLE;
        ThrowIfFailed(type.SetUINT32(ref kBits, WasapiAudioTrack.BitsPerSample), "AudioOut.Bits");

        Guid kAvg = MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND;
        ThrowIfFailed(type.SetUINT32(ref kAvg, 24000u), "AudioOut.AvgBytes"); // 192 kbps

        Guid kPayload = MF.MF_MT_AAC_PAYLOAD_TYPE;
        ThrowIfFailed(type.SetUINT32(ref kPayload, 0u), "AudioOut.PayloadType");

        Guid kProfile = MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION;
        ThrowIfFailed(type.SetUINT32(ref kProfile, 0x29u), "AudioOut.ProfileLevel");
    }

    private static void ConfigureAudioInType(MF.IMFMediaType type)
    {
        Guid kMajor = MF.MF_MT_MAJOR_TYPE;
        Guid vMajor = MF.MFMediaType_Audio;
        ThrowIfFailed(type.SetGUID(ref kMajor, ref vMajor), "AudioIn.MajorType");

        Guid kSub = MF.MF_MT_SUBTYPE;
        Guid vSub = MF.MFAudioFormat_PCM;
        ThrowIfFailed(type.SetGUID(ref kSub, ref vSub), "AudioIn.Subtype");

        Guid kCh = MF.MF_MT_AUDIO_NUM_CHANNELS;
        ThrowIfFailed(type.SetUINT32(ref kCh, WasapiAudioTrack.Channels), "AudioIn.Channels");

        Guid kRate = MF.MF_MT_AUDIO_SAMPLES_PER_SECOND;
        ThrowIfFailed(type.SetUINT32(ref kRate, WasapiAudioTrack.SampleRate), "AudioIn.SampleRate");

        Guid kBits = MF.MF_MT_AUDIO_BITS_PER_SAMPLE;
        ThrowIfFailed(type.SetUINT32(ref kBits, WasapiAudioTrack.BitsPerSample), "AudioIn.Bits");

        Guid kAlign = MF.MF_MT_AUDIO_BLOCK_ALIGNMENT;
        ThrowIfFailed(type.SetUINT32(ref kAlign, WasapiAudioTrack.BytesPerFrame), "AudioIn.Align");

        Guid kAvg = MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND;
        ThrowIfFailed(type.SetUINT32(ref kAvg, WasapiAudioTrack.SampleRate * WasapiAudioTrack.BytesPerFrame), "AudioIn.AvgBytes");
    }

    public bool WriteVideoFrameWithCallback(Func<IntPtr, int, bool> fillBuffer, long sampleTimeHns, long durationHns)
    {
        MF.IMFMediaBuffer? buffer = null;
        MF.IMFSample? sample = null;

        try
        {
            int hr = MF.MFCreateMemoryBuffer((uint)_frameBytes, out buffer);
            ThrowIfFailed(hr, "MFCreateMemoryBuffer(Video)");

            hr = buffer.Lock(out IntPtr dstPtr, out uint maxLen, out _);
            ThrowIfFailed(hr, "Buffer.Lock(Video)");

            bool filled;
            try
            {
                filled = fillBuffer(dstPtr, (int)maxLen);
            }
            finally
            {
                buffer.Unlock();
            }

            if (!filled)
            {
                return false;
            }

            ThrowIfFailed(buffer.SetCurrentLength((uint)_frameBytes), "SetCurrentLength(Video)");
            ThrowIfFailed(MF.MFCreateSample(out sample), "MFCreateSample(Video)");
            ThrowIfFailed(sample.AddBuffer(buffer), "AddBuffer(Video)");
            ThrowIfFailed(sample.SetSampleTime(sampleTimeHns), "SetSampleTime(Video)");
            ThrowIfFailed(sample.SetSampleDuration(durationHns), "SetSampleDuration(Video)");

            lock (_writeLock)
            {
                if (_disposed || _finalized || _writer is null) return false;
                ThrowIfFailed(_writer.WriteSample(_videoStreamIndex, sample), "WriteSample(Video)");
            }

            return true;
        }
        finally
        {
            if (sample is not null) Marshal.ReleaseComObject(sample);
            if (buffer is not null) Marshal.ReleaseComObject(buffer);
        }
    }

    public void WriteVideoFrame(ReadOnlySpan<byte> bgraTopDown, long sampleTimeHns, long durationHns)
    {
        if (bgraTopDown.Length < _frameBytes)
        {
            throw new ArgumentException("Frame buffer is smaller than expected video frame size.");
        }

        fixed (byte* pSrc = bgraTopDown)
        {
            IntPtr srcPtr = (IntPtr)pSrc;
            WriteVideoFrameWithCallback((dstPtr, dstCap) =>
            {
                Buffer.MemoryCopy((void*)srcPtr, (void*)dstPtr, dstCap, _frameBytes);
                return true;
            }, sampleTimeHns, durationHns);
        }
    }

    public void WriteAudioChunk(ReadOnlySpan<byte> pcmBytes, long sampleTimeHns, long durationHns)
    {
        if (!_hasAudio || pcmBytes.IsEmpty) return;

        MF.IMFMediaBuffer? buffer = null;
        MF.IMFSample? sample = null;

        try
        {
            int length = pcmBytes.Length;
            ThrowIfFailed(MF.MFCreateMemoryBuffer((uint)length, out buffer), "MFCreateMemoryBuffer(Audio)");
            ThrowIfFailed(buffer.Lock(out IntPtr dstPtr, out uint maxLen, out _), "Buffer.Lock(Audio)");
            try
            {
                fixed (byte* pSrc = pcmBytes)
                {
                    Buffer.MemoryCopy(pSrc, (void*)dstPtr, maxLen, length);
                }
            }
            finally
            {
                buffer.Unlock();
            }

            ThrowIfFailed(buffer.SetCurrentLength((uint)length), "SetCurrentLength(Audio)");
            ThrowIfFailed(MF.MFCreateSample(out sample), "MFCreateSample(Audio)");
            ThrowIfFailed(sample.AddBuffer(buffer), "AddBuffer(Audio)");
            ThrowIfFailed(sample.SetSampleTime(sampleTimeHns), "SetSampleTime(Audio)");
            ThrowIfFailed(sample.SetSampleDuration(durationHns), "SetSampleDuration(Audio)");

            lock (_writeLock)
            {
                if (_disposed || _finalized || _writer is null) return;
                ThrowIfFailed(_writer.WriteSample(_audioStreamIndex, sample), "WriteSample(Audio)");
            }
        }
        finally
        {
            if (sample is not null) Marshal.ReleaseComObject(sample);
            if (buffer is not null) Marshal.ReleaseComObject(buffer);
        }
    }

    public void FinalizeWriter()
    {
        lock (_writeLock)
        {
            if (_finalized || _writer is null) return;
            _finalized = true;
            int hr = _writer.Finalize();
            ThrowIfFailed(hr, "SinkWriter.Finalize");
        }
    }

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"{operation} 失败 (0x{hr:X8})", hr);
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_writer is not null)
            {
                if (!_finalized)
                {
                    _finalized = true;
                    try { _writer.Finalize(); } catch { }
                }
                Marshal.ReleaseComObject(_writer);
                _writer = null;
            }
        }

        if (_mfStarted)
        {
            _mfStarted = false;
            try { MF.MFShutdown(); } catch { }
        }
    }
}

internal sealed class ScreenRecorder : IDisposable
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private GdiScreenCapturer? _screenCapturer;
    private AudioCaptureSession? _audioSession;
    private MfMp4Writer? _writer;
    private Stopwatch? _clock;
    private CancellationTokenSource? _cts;
    private Thread? _videoThread;
    private Thread? _audioThread;
    private bool _timerPeriodActive;
    private volatile bool _isStopping;
    private bool _disposed;

    public bool IsRecording { get; private set; }
    public string? CurrentOutputPath { get; private set; }
    public string? ActiveBackend { get; private set; }

    public event Action<string>? UnexpectedStopped;

    internal static uint CalculateVideoBitrate(int width, int height, int fps)
    {
        long raw = (long)width * height * fps / 7;
        return (uint)Math.Clamp(raw, 2_500_000L, 16_000_000L);
    }

    public async Task<RecordStartResult> StartAsync(
        string outputDir,
        int fps = 30,
        CaptureRegion? region = null,
        AudioCaptureMode audioMode = AudioCaptureMode.System)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsRecording)
            {
                return new RecordStartResult(false, ActiveBackend, CurrentOutputPath, "已在录制中");
            }

            CaptureRegion targetRegion;
            if (region is not null)
            {
                targetRegion = region.Normalize();
            }
            else
            {
                (int pw, int ph) = Win32Interop.GetPrimaryScreenSize();
                targetRegion = new CaptureRegion(0, 0, pw, ph).Normalize();
            }

            Directory.CreateDirectory(outputDir);
            string fileName = $"Rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            string outputPath = Path.Combine(outputDir, fileName);

            GdiScreenCapturer? capturer = null;
            AudioCaptureSession? audio = null;
            MfMp4Writer? writer = null;

            try
            {
                capturer = new GdiScreenCapturer(targetRegion.X, targetRegion.Y, targetRegion.Width, targetRegion.Height);

                // Probe 1 frame before creating output file
                if (!capturer.TryCaptureFrame(IntPtr.Zero, 0, out int probeErr))
                {
                    capturer.Dispose();
                    return new RecordStartResult(false, null, null, $"无法读取桌面画面 (BitBlt 错误 {probeErr})");
                }

                audio = AudioCaptureSession.Create(audioMode);

                // Initialize Media Foundation Writer on MTA thread
                MfMp4Writer? createdWriter = null;
                Exception? initEx = null;
                var initDone = new ManualResetEventSlim(false);

                var mtaInitThread = new Thread(() =>
                {
                    int coHr = MF.CoInitializeEx(IntPtr.Zero, MF.COINIT_MULTITHREADED);
                    bool shouldUninit = coHr is 0 or 1;
                    try
                    {
                        createdWriter = MfMp4Writer.Create(
                            outputPath,
                            targetRegion.Width,
                            targetRegion.Height,
                            fps,
                            audio.IsActive);
                    }
                    catch (Exception ex)
                    {
                        initEx = ex;
                    }
                    finally
                    {
                        initDone.Set();
                        if (shouldUninit) MF.CoUninitialize();
                    }
                })
                { IsBackground = true };
                mtaInitThread.Start();
                initDone.Wait();
                mtaInitThread.Join();
                initDone.Dispose();

                if (initEx is not null || createdWriter is null)
                {
                    throw initEx ?? new InvalidOperationException("Media Foundation 初始化失败");
                }
                writer = createdWriter;

                MF.timeBeginPeriod(1);
                _timerPeriodActive = true;

                _isStopping = false;
                _screenCapturer = capturer;
                _audioSession = audio;
                _writer = writer;
                _clock = Stopwatch.StartNew();
                _cts = new CancellationTokenSource();

                CurrentOutputPath = outputPath;
                string videoTag = writer.IsHardwareAccelerated ? "MF-HW" : "MF-SW";
                ActiveBackend = audio.IsActive ? $"{videoTag}+WASAPI" : videoTag;
                IsRecording = true;

                CancellationToken token = _cts.Token;
                _videoThread = new Thread(() => VideoCaptureLoop(fps, token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = "MiniRec_VideoLoop"
                };
                _videoThread.Start();

                if (audio.IsActive)
                {
                    _audioThread = new Thread(() => AudioMixLoop(token))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.AboveNormal,
                        Name = "MiniRec_AudioLoop"
                    };
                    _audioThread.Start();
                }

                return new RecordStartResult(true, ActiveBackend, outputPath, null, audio.Warning);
            }
            catch (Exception ex)
            {
                writer?.Dispose();
                audio?.Dispose();
                capturer?.Dispose();
                if (_timerPeriodActive)
                {
                    MF.timeEndPeriod(1);
                    _timerPeriodActive = false;
                }
                CleanupEmptyFile(outputPath);

                return new RecordStartResult(false, null, null, $"无法启动录屏：{ex.Message}");
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void VideoCaptureLoop(int fps, CancellationToken token)
    {
        int coHr = MF.CoInitializeEx(IntPtr.Zero, MF.COINIT_MULTITHREADED);
        bool shouldUninit = coHr is 0 or 1;

        try
        {
            long frameDurationHns = 10_000_000L / fps;
            double frameIntervalMs = 1000.0 / fps;
            long frameIndex = 0;

            while (!token.IsCancellationRequested)
            {
                long sampleTimeHns = (long)(_clock!.Elapsed.TotalSeconds * 10_000_000.0);
                if (frameIndex == 0) sampleTimeHns = 0;

                bool ok = _writer!.WriteVideoFrameWithCallback(
                    (dstPtr, dstCap) => _screenCapturer!.TryCaptureFrame(dstPtr, dstCap, out _),
                    sampleTimeHns,
                    frameDurationHns);

                if (!ok)
                {
                    throw new InvalidOperationException("桌面画面采集失败");
                }

                frameIndex++;
                double targetMs = frameIndex * frameIntervalMs;
                double remainingMs = targetMs - _clock.Elapsed.TotalMilliseconds;
                if (remainingMs > 1.5 && !token.IsCancellationRequested)
                {
                    Thread.Sleep((int)Math.Floor(remainingMs));
                }
                while (_clock.Elapsed.TotalMilliseconds < targetMs && !token.IsCancellationRequested)
                {
                    Thread.SpinWait(50);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_isStopping && !token.IsCancellationRequested)
            {
                NotifyUnexpectedStop($"录制意外中断：{ex.Message}");
            }
        }
        finally
        {
            if (shouldUninit) MF.CoUninitialize();
        }
    }

    private void AudioMixLoop(CancellationToken token)
    {
        int coHr = MF.CoInitializeEx(IntPtr.Zero, MF.COINIT_MULTITHREADED);
        bool shouldUninit = coHr is 0 or 1;

        try
        {
            const long chunkDurationHns = 200_000L; // 20ms in 100ns units
            const double chunkMs = 20.0;
            byte[] pcmChunk = new byte[WasapiAudioTrack.Chunk20MsBytes];
            long chunkIndex = 0;

            while (!token.IsCancellationRequested)
            {
                double targetMs = (chunkIndex + 1) * chunkMs;
                double waitMs = targetMs - _clock!.Elapsed.TotalMilliseconds;
                if (waitMs > 1.0 && !token.IsCancellationRequested)
                {
                    Thread.Sleep((int)Math.Floor(waitMs));
                }
                if (token.IsCancellationRequested) break;

                _audioSession!.ReadMixed20MsChunk(pcmChunk);
                long sampleTimeHns = chunkIndex * chunkDurationHns;
                _writer!.WriteAudioChunk(pcmChunk, sampleTimeHns, chunkDurationHns);
                chunkIndex++;
            }
        }
        catch (Exception ex)
        {
            if (!_isStopping && !token.IsCancellationRequested)
            {
                NotifyUnexpectedStop($"音频录制中断：{ex.Message}");
            }
        }
        finally
        {
            if (shouldUninit) MF.CoUninitialize();
        }
    }

    private void NotifyUnexpectedStop(string message)
    {
        _cts?.Cancel();
        UnexpectedStopped?.Invoke(message);
    }

    public async Task<RecordStopResult> StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording && _writer is null)
            {
                return new RecordStopResult(false, null, "当前未在录制");
            }

            _isStopping = true;
            string? outputPath = CurrentOutputPath;

            await Task.Run(() =>
            {
                _cts?.Cancel();
                _videoThread?.Join(3000);
                _audioThread?.Join(2000);

                _audioSession?.Dispose();
                _audioSession = null;

                _screenCapturer?.Dispose();
                _screenCapturer = null;

                if (_writer is not null)
                {
                    int coHr = MF.CoInitializeEx(IntPtr.Zero, MF.COINIT_MULTITHREADED);
                    bool shouldUninit = coHr is 0 or 1;
                    try
                    {
                        _writer.FinalizeWriter();
                    }
                    finally
                    {
                        _writer.Dispose();
                        _writer = null;
                        if (shouldUninit) MF.CoUninitialize();
                    }
                }

                if (_timerPeriodActive)
                {
                    MF.timeEndPeriod(1);
                    _timerPeriodActive = false;
                }

                _cts?.Dispose();
                _cts = null;
                _videoThread = null;
                _audioThread = null;
                _clock = null;
                IsRecording = false;
                ActiveBackend = null;
                CurrentOutputPath = null;
            }).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath))
            {
                var info = new FileInfo(outputPath);
                if (info.Length > 0)
                {
                    return new RecordStopResult(true, outputPath, null);
                }
            }

            return new RecordStopResult(false, outputPath, "未生成有效 MP4 文件");
        }
        catch (Exception ex)
        {
            return new RecordStopResult(false, CurrentOutputPath, $"保存失败：{ex.Message}");
        }
        finally
        {
            _isStopping = false;
            _lifecycleLock.Release();
        }
    }

    private static void CleanupEmptyFile(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isStopping = true;

        _cts?.Cancel();
        _videoThread?.Join(1500);
        _audioThread?.Join(1500);

        _audioSession?.Dispose();
        _screenCapturer?.Dispose();
        _writer?.Dispose();
        _cts?.Dispose();

        if (_timerPeriodActive)
        {
            MF.timeEndPeriod(1);
            _timerPeriodActive = false;
        }

        _lifecycleLock.Dispose();
    }
}
