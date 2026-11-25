using System.Runtime.InteropServices;
using MiniRec.Native;
using MF = MiniRec.Native.MediaFoundationInterop;

namespace MiniRec.Services;

internal enum AudioCaptureMode
{
    None,
    System,
    Microphone,
    Both
}

internal sealed class PcmRingBuffer
{
    private readonly byte[] _buffer;
    private readonly object _lock = new();
    private int _head;
    private int _tail;
    private int _count;

    public PcmRingBuffer(int capacityBytes)
    {
        _buffer = new byte[capacityBytes];
    }

    public int AvailableBytes
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (data.Length >= _buffer.Length)
            {
                ReadOnlySpan<byte> tailSlice = data[^_buffer.Length..];
                tailSlice.CopyTo(_buffer);
                _head = 0;
                _tail = 0;
                _count = _buffer.Length;
                return;
            }

            int overflow = (_count + data.Length) - _buffer.Length;
            if (overflow > 0)
            {
                _head = (_head + overflow) % _buffer.Length;
                _count -= overflow;
            }

            int firstPart = Math.Min(data.Length, _buffer.Length - _tail);
            data[..firstPart].CopyTo(_buffer.AsSpan(_tail, firstPart));
            int secondPart = data.Length - firstPart;
            if (secondPart > 0)
            {
                data[firstPart..].CopyTo(_buffer.AsSpan(0, secondPart));
            }

            _tail = (_tail + data.Length) % _buffer.Length;
            _count += data.Length;
        }
    }

    public int ReadOrSilence(Span<byte> destination)
    {
        lock (_lock)
        {
            int toRead = Math.Min(_count, destination.Length);
            if (toRead > 0)
            {
                int firstPart = Math.Min(toRead, _buffer.Length - _head);
                _buffer.AsSpan(_head, firstPart).CopyTo(destination[..firstPart]);
                int secondPart = toRead - firstPart;
                if (secondPart > 0)
                {
                    _buffer.AsSpan(0, secondPart).CopyTo(destination.Slice(firstPart, secondPart));
                }

                _head = (_head + toRead) % _buffer.Length;
                _count -= toRead;
            }

            if (toRead < destination.Length)
            {
                destination[toRead..].Clear();
            }

            return toRead;
        }
    }
}

internal sealed class WasapiAudioTrack : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int BytesPerFrame = Channels * (BitsPerSample / 8); // 4 bytes
    public const int Chunk20MsBytes = (SampleRate / 50) * BytesPerFrame; // 960 * 4 = 3840 bytes

    private readonly bool _isLoopback;
    private readonly PcmRingBuffer _ring = new(SampleRate * BytesPerFrame / 2); // 500ms ring
    private readonly ManualResetEventSlim _readyEvent = new(false);

    private volatile bool _stopRequested;
    private Thread? _thread;
    private bool _startedOk;
    private string? _initError;

    public WasapiAudioTrack(bool isLoopback)
    {
        _isLoopback = isLoopback;
    }

    public bool Start(out string? error)
    {
        _thread = new Thread(CaptureThreadLoop)
        {
            IsBackground = true,
            Name = _isLoopback ? "MiniRec_WASAPI_Loopback" : "MiniRec_WASAPI_Mic"
        };
        _thread.Start();
        _readyEvent.Wait();

        error = _initError;
        return _startedOk;
    }

    public void ReadChunkOrSilence(Span<byte> destination) => _ring.ReadOrSilence(destination);

    private unsafe void CaptureThreadLoop()
    {
        int coHr = MF.CoInitializeEx(IntPtr.Zero, MF.COINIT_MULTITHREADED);
        bool shouldCoUninit = coHr is 0 or 1;

        MF.IMMDeviceEnumerator? enumerator = null;
        MF.IMMDevice? device = null;
        MF.IAudioClient? audioClient = null;
        MF.IAudioCaptureClient? captureClient = null;

        try
        {
            Guid clsid = MF.CLSID_MMDeviceEnumerator;
            Guid iidEnum = MF.IID_IMMDeviceEnumerator;
            int hr = MF.CoCreateInstance(ref clsid, IntPtr.Zero, MF.CLSCTX_ALL, ref iidEnum, out object enumObj);
            if (hr < 0)
            {
                _initError = $"无法创建音频枚举器 (0x{hr:X8})";
                return;
            }
            enumerator = (MF.IMMDeviceEnumerator)enumObj;

            int dataFlow = _isLoopback ? MF.eRender : MF.eCapture;
            hr = enumerator.GetDefaultAudioEndpoint(dataFlow, MF.eConsole, out device);
            if (hr < 0 || device is null)
            {
                _initError = _isLoopback ? "未检测到默认扬声器设备" : "未检测到默认麦克风设备";
                return;
            }

            Guid iidClient = MF.IID_IAudioClient;
            hr = device.Activate(ref iidClient, MF.CLSCTX_ALL, IntPtr.Zero, out object clientObj);
            if (hr < 0)
            {
                _initError = $"激活音频设备失败 (0x{hr:X8})";
                return;
            }
            audioClient = (MF.IAudioClient)clientObj;

            var wfx = new MF.WAVEFORMATEX
            {
                wFormatTag = 1, // WAVE_FORMAT_PCM
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = BitsPerSample,
                nBlockAlign = BytesPerFrame,
                nAvgBytesPerSec = SampleRate * BytesPerFrame,
                cbSize = 0
            };

            uint streamFlags = MF.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | MF.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            if (_isLoopback)
            {
                streamFlags |= MF.AUDCLNT_STREAMFLAGS_LOOPBACK;
            }

            // 200ms buffer duration (2,000,000 * 100ns)
            hr = audioClient.Initialize(MF.AUDCLNT_SHAREMODE_SHARED, streamFlags, 2_000_000, 0, ref wfx, IntPtr.Zero);
            if (hr < 0)
            {
                _initError = $"初始化音频采集失败 (0x{hr:X8})";
                return;
            }

            Guid iidCap = MF.IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref iidCap, out object capObj);
            if (hr < 0)
            {
                _initError = $"获取音频采集服务失败 (0x{hr:X8})";
                return;
            }
            captureClient = (MF.IAudioCaptureClient)capObj;

            hr = audioClient.Start();
            if (hr < 0)
            {
                _initError = $"启动音频流失败 (0x{hr:X8})";
                return;
            }

            _startedOk = true;
            _readyEvent.Set();

            byte[] scratch = new byte[SampleRate * BytesPerFrame / 5]; // 200ms scratch

            while (!_stopRequested)
            {
                hr = captureClient.GetNextPacketSize(out uint packetFrames);
                if (hr < 0) break;

                if (packetFrames == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                while (packetFrames > 0 && !_stopRequested)
                {
                    hr = captureClient.GetBuffer(
                        out IntPtr dataPtr,
                        out uint framesRead,
                        out uint flags,
                        out _,
                        out _);
                    if (hr < 0) break;

                    int byteCount = (int)framesRead * BytesPerFrame;
                    if (byteCount > 0)
                    {
                        if (byteCount > scratch.Length)
                        {
                            scratch = new byte[byteCount];
                        }

                        if ((flags & MF.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || dataPtr == IntPtr.Zero)
                        {
                            Array.Clear(scratch, 0, byteCount);
                        }
                        else
                        {
                            fixed (byte* pDst = scratch)
                            {
                                Buffer.MemoryCopy((void*)dataPtr, pDst, scratch.Length, byteCount);
                            }
                        }

                        _ring.Write(scratch.AsSpan(0, byteCount));
                    }

                    captureClient.ReleaseBuffer(framesRead);
                    hr = captureClient.GetNextPacketSize(out packetFrames);
                    if (hr < 0) break;
                }
            }

            audioClient.Stop();
        }
        catch (Exception ex)
        {
            if (!_startedOk)
            {
                _initError = ex.Message;
            }
        }
        finally
        {
            _readyEvent.Set();

            if (captureClient is not null) Marshal.ReleaseComObject(captureClient);
            if (audioClient is not null) Marshal.ReleaseComObject(audioClient);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);

            if (shouldCoUninit)
            {
                MF.CoUninitialize();
            }
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
        _thread?.Join(1500);
        _readyEvent.Dispose();
    }
}

internal sealed class AudioCaptureSession : IDisposable
{
    private readonly WasapiAudioTrack? _systemTrack;
    private readonly WasapiAudioTrack? _micTrack;
    private readonly byte[] _scratchA = new byte[WasapiAudioTrack.Chunk20MsBytes];
    private readonly byte[] _scratchB = new byte[WasapiAudioTrack.Chunk20MsBytes];

    public bool IsActive => _systemTrack is not null || _micTrack is not null;
    public string? Warning { get; }

    private AudioCaptureSession(WasapiAudioTrack? systemTrack, WasapiAudioTrack? micTrack, string? warning)
    {
        _systemTrack = systemTrack;
        _micTrack = micTrack;
        Warning = warning;
    }

    public static AudioCaptureSession Create(AudioCaptureMode mode)
    {
        if (mode == AudioCaptureMode.None)
        {
            return new AudioCaptureSession(null, null, null);
        }

        WasapiAudioTrack? sys = null;
        WasapiAudioTrack? mic = null;
        List<string> warnings = [];

        if (mode is AudioCaptureMode.System or AudioCaptureMode.Both)
        {
            var track = new WasapiAudioTrack(isLoopback: true);
            if (track.Start(out string? err))
            {
                sys = track;
            }
            else
            {
                track.Dispose();
                warnings.Add(err ?? "系统声音不可用");
            }
        }

        if (mode is AudioCaptureMode.Microphone or AudioCaptureMode.Both)
        {
            var track = new WasapiAudioTrack(isLoopback: false);
            if (track.Start(out string? err))
            {
                mic = track;
            }
            else
            {
                track.Dispose();
                warnings.Add(err ?? "麦克风不可用");
            }
        }

        string? warning = warnings.Count > 0 ? string.Join("；", warnings) : null;
        return new AudioCaptureSession(sys, mic, warning);
    }

    public void ReadMixed20MsChunk(Span<byte> destination)
    {
        if (_systemTrack is not null && _micTrack is not null)
        {
            _systemTrack.ReadChunkOrSilence(_scratchA.AsSpan(0, destination.Length));
            _micTrack.ReadChunkOrSilence(_scratchB.AsSpan(0, destination.Length));
            MixPcm16(_scratchA.AsSpan(0, destination.Length), _scratchB.AsSpan(0, destination.Length), destination);
        }
        else if (_systemTrack is not null)
        {
            _systemTrack.ReadChunkOrSilence(destination);
        }
        else if (_micTrack is not null)
        {
            _micTrack.ReadChunkOrSilence(destination);
        }
        else
        {
            destination.Clear();
        }
    }

    internal static void MixPcm16(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dest)
    {
        ReadOnlySpan<short> sa = MemoryMarshal.Cast<byte, short>(a);
        ReadOnlySpan<short> sb = MemoryMarshal.Cast<byte, short>(b);
        Span<short> sd = MemoryMarshal.Cast<byte, short>(dest);

        int len = Math.Min(sd.Length, Math.Min(sa.Length, sb.Length));
        for (int i = 0; i < len; i++)
        {
            int sum = sa[i] + sb[i];
            sd[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
        }
    }

    public void Dispose()
    {
        _systemTrack?.Dispose();
        _micTrack?.Dispose();
    }
}
