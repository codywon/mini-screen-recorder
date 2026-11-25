using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MiniRec.Native;
using MiniRec.Services;
using MF = MiniRec.Native.MediaFoundationInterop;
using Wpf = System.Windows;

namespace MiniRec;

public partial class App : Wpf.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(Wpf.StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int exitCode = RunSelfTest();
            Environment.Exit(exitCode);
            return;
        }

        _singleInstanceMutex = new Mutex(true, "Local\\MiniRec_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(Wpf.ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static int RunSelfTest()
    {
        if (!Console.IsOutputRedirected && Win32Interop.AttachConsole(-1))
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // Ignore if console handle is unavailable.
            }
        }

        int failures = 0;

        void Pass(string name, string detail) => Console.WriteLine($"[PASS] {name}: {detail}");
        void Fail(string name, string detail)
        {
            failures++;
            Console.WriteLine($"[FAIL] {name}: {detail}");
        }

        // 1. 码率与热键格式化
        string hotkeyText = HotkeyFormatter.Format(Win32Interop.MOD_CONTROL | Win32Interop.MOD_ALT, 0x52);
        uint bitrate1080p30 = ScreenRecorder.CalculateVideoBitrate(1920, 1080, 30);
        uint bitrate640x360 = ScreenRecorder.CalculateVideoBitrate(640, 360, 15);
        if (hotkeyText == "Ctrl+Alt+R" && bitrate1080p30 is >= 2_500_000 and <= 16_000_000 && bitrate640x360 == 2_500_000)
        {
            Pass("码率与热键格式化", $"hotkey={hotkeyText}, 1080p30={bitrate1080p30 / 1000}kbps");
        }
        else
        {
            Fail("码率与热键格式化", $"hotkey={hotkeyText}, br1080={bitrate1080p30}, br360={bitrate640x360}");
        }

        // 2. 区域数学与窗口枚举
        var oddRegion = new CaptureRegion(100, 100, 801, 601).Normalize();
        bool primaryInside = oddRegion.IsWithinPrimaryScreen(1920, 1080);
        bool leftMonitorOutside = new CaptureRegion(-500, 100, 800, 600).IsWithinPrimaryScreen(1920, 1080);
        List<(int X, int Y, int Width, int Height)> enumeratedWindows =
            Win32Interop.EnumerateVisibleWindowRects(Environment.ProcessId);

        var baseRect = new CaptureRegion(100, 100, 300, 200);
        CaptureRegion movedClamped = RegionEditMath.ApplyMove(baseRect, 2000, 2000, 0, 0, 1920, 1080);
        CaptureRegion resizedBR = RegionEditMath.ApplyResize(baseRect, RegionHitZone.BottomRight, 50, -30, 32, 0, 0, 1920, 1080);
        CaptureRegion resizedMin = RegionEditMath.ApplyResize(baseRect, RegionHitZone.TopLeft, 500, 500, 32, 0, 0, 1920, 1080);

        if (oddRegion.Width == 800 && oddRegion.Height == 600 &&
            primaryInside && !leftMonitorOutside &&
            enumeratedWindows is not null &&
            movedClamped.X == 1620 && movedClamped.Y == 880 &&
            resizedBR.Width == 350 && resizedBR.Height == 170 &&
            resizedMin.Width == 32 && resizedMin.Height == 32)
        {
            Pass("选区几何与窗口枚举", $"normalized=800x600, windows={enumeratedWindows.Count}");
        }
        else
        {
            Fail("选区几何与窗口枚举", "区域规整、窗口枚举或平移/缩放边界钳制异常");
        }

        // 3. 音频环形缓冲与双路软限幅混音
        var ring = new PcmRingBuffer(16);
        byte[] sampleBytes = [1, 2, 3, 4, 5, 6, 7, 8];
        ring.Write(sampleBytes);
        byte[] readBack = new byte[12];
        int readLen = ring.ReadOrSilence(readBack);

        short[] sA = [25000, -25000, 1000];
        short[] sB = [25000, -25000, -400];
        byte[] mixedBytes = new byte[6];
        AudioCaptureSession.MixPcm16(
            MemoryMarshal.AsBytes(sA.AsSpan()),
            MemoryMarshal.AsBytes(sB.AsSpan()),
            mixedBytes);
        Span<short> mixedShorts = MemoryMarshal.Cast<byte, short>(mixedBytes.AsSpan());

        if (readLen == 8 && readBack[0] == 1 && readBack[7] == 8 && readBack[8] == 0 && readBack[11] == 0 &&
            mixedShorts[0] == short.MaxValue && mixedShorts[1] == short.MinValue && mixedShorts[2] == 600)
        {
            Pass("音频缓冲与混音限幅", "ring=OK, clamp=OK");
        }
        else
        {
            Fail("音频缓冲与混音限幅", $"readLen={readLen}, mixed=({mixedShorts[0]},{mixedShorts[1]},{mixedShorts[2]})");
        }

        // 4. 配置默认值与持久化（含 FPS 与 AudioMode）
        var defaultCfg = new AppConfig();
        if (defaultCfg.Fps != 60 || defaultCfg.AudioMode != AudioCaptureMode.Both)
        {
            Fail("默认配置校验", $"expected 60fps+Both, got {defaultCfg.Fps}fps+{defaultCfg.AudioMode}");
        }
        else
        {
            Pass("默认配置校验", $"fps={defaultCfg.Fps}, audio={defaultCfg.AudioMode}");
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), $"MiniRec_SelfTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            string cfgPath = Path.Combine(tempRoot, "config.json");
            var store = new ConfigStore(cfgPath);
            var cfg = new AppConfig
            {
                RecordModifiers = Win32Interop.MOD_CONTROL | Win32Interop.MOD_SHIFT,
                RecordVirtualKey = 0x78, // F9
                Fps = 60,
                AudioMode = AudioCaptureMode.Both,
                OutputDirectory = tempRoot
            };
            store.Save(cfg);
            AppConfig reloaded = store.Load();
            if (reloaded.RecordModifiers == cfg.RecordModifiers &&
                reloaded.RecordVirtualKey == cfg.RecordVirtualKey &&
                reloaded.Fps == 60 &&
                reloaded.AudioMode == AudioCaptureMode.Both)
            {
                Pass("配置持久化", $"fps={reloaded.Fps}, audio={reloaded.AudioMode}");
            }
            else
            {
                Fail("配置持久化", "读取回来的配置与写入不一致");
            }

            // 5. Win11 原生 Media Foundation H.264 + AAC MP4 真实编码测试（在 MTA 线程执行）
            string mp4Path = Path.Combine(tempRoot, "mf_selftest.mp4");
            bool mfHw = false;
            Exception? mfEx = null;

            var mtaThread = new Thread(() =>
            {
                int coHr = MF.CoInitializeEx(IntPtr.Zero, MF.COINIT_MULTITHREADED);
                bool uninit = coHr is 0 or 1;
                try
                {
                    const int w = 640;
                    const int h = 360;
                    const int fps = 30;
                    using var writer = MfMp4Writer.Create(mp4Path, w, h, fps, enableAudio: true);
                    mfHw = writer.IsHardwareAccelerated;

                    byte[] frame = new byte[w * h * 4];
                    long frameDurHns = 10_000_000L / fps;
                    for (int f = 0; f < 30; f++)
                    {
                        FillTestPatternBgra(frame, w, h, f);
                        writer.WriteVideoFrame(frame, f * frameDurHns, frameDurHns);
                    }

                    byte[] pcm = new byte[WasapiAudioTrack.Chunk20MsBytes];
                    const long audioDurHns = 200_000L;
                    for (int c = 0; c < 50; c++)
                    {
                        FillSineWavePcm(pcm, c);
                        writer.WriteAudioChunk(pcm, c * audioDurHns, audioDurHns);
                    }

                    writer.FinalizeWriter();
                }
                catch (Exception ex)
                {
                    mfEx = ex;
                }
                finally
                {
                    if (uninit) MF.CoUninitialize();
                }
            });
            mtaThread.Start();
            mtaThread.Join();

            if (mfEx is null && File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 10_000)
            {
                byte[] header = new byte[12];
                using (FileStream fs = File.OpenRead(mp4Path))
                {
                    _ = fs.Read(header, 0, header.Length);
                }
                bool hasFtyp = Encoding.ASCII.GetString(header, 4, 4) == "ftyp";
                long size = new FileInfo(mp4Path).Length;

                if (hasFtyp)
                {
                    Pass("原生 MediaFoundation 编码", $"hw={mfHw}, size={size} bytes, ftyp=OK");
                    CrossCheckWithFfprobeIfInstalled(mp4Path, Pass, Fail);
                }
                else
                {
                    Fail("原生 MediaFoundation 编码", "生成的 MP4 缺少 ftyp 容器头");
                }
            }
            else
            {
                Fail("原生 MediaFoundation 编码", mfEx?.ToString() ?? "未生成有效 MP4 文件");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        Console.WriteLine(failures == 0 ? "ALL SELF-TESTS PASSED" : $"SELF-TESTS FAILED: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void FillTestPatternBgra(byte[] buffer, int width, int height, int frameIndex)
    {
        int barX = (frameIndex * 18) % width;
        for (int y = 0; y < height; y++)
        {
            bool topHalf = y < height / 2;
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 4;
                bool inBar = Math.Abs(x - barX) < 12;
                if (inBar)
                {
                    buffer[idx] = 255;
                    buffer[idx + 1] = 255;
                    buffer[idx + 2] = 255;
                    buffer[idx + 3] = 255;
                }
                else if (topHalf)
                {
                    buffer[idx] = 40;
                    buffer[idx + 1] = 40;
                    buffer[idx + 2] = 220;
                    buffer[idx + 3] = 255;
                }
                else
                {
                    buffer[idx] = 220;
                    buffer[idx + 1] = 100;
                    buffer[idx + 2] = 30;
                    buffer[idx + 3] = 255;
                }
            }
        }
    }

    private static void FillSineWavePcm(byte[] buffer, int chunkIndex)
    {
        Span<short> samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan());
        int frames = samples.Length / 2;
        int startFrame = chunkIndex * frames;
        for (int i = 0; i < frames; i++)
        {
            double t = (startFrame + i) / (double)WasapiAudioTrack.SampleRate;
            short val = (short)(Math.Sin(2.0 * Math.PI * 440.0 * t) * 8000);
            samples[i * 2] = val;
            samples[i * 2 + 1] = val;
        }
    }

    private static void CrossCheckWithFfprobeIfInstalled(
        string mp4Path,
        Action<string, string> pass,
        Action<string, string> fail)
    {
        string? ffprobePath = FindOnPath("ffprobe.exe");
        if (ffprobePath is null) return;

        using var probe = Process.Start(new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = $"-v error -show_entries stream=codec_type,codec_name,width,height,pix_fmt,sample_rate,channels -of default=noprint_wrappers=1 \"{mp4Path}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        });
        if (probe is null) return;

        string output = probe.StandardOutput.ReadToEnd();
        probe.WaitForExit();

        bool hasH264 = output.Contains("codec_name=h264") && output.Contains("width=640") && output.Contains("height=360") && output.Contains("pix_fmt=yuv420p");
        bool hasAac = output.Contains("codec_name=aac") && output.Contains("sample_rate=48000") && output.Contains("channels=2");
        if (probe.ExitCode == 0 && hasH264 && hasAac)
        {
            pass("ffprobe 音视频双流交叉校验", "H.264(640x360 yuv420p) + AAC(48000Hz 2ch)");
        }
        else
        {
            fail("ffprobe 音视频双流交叉校验", output);
        }
    }

    private static string? FindOnPath(string exeName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;

        foreach (string rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawDir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }
}
