using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MiniRec.Models;
using MiniRec.Native;
using MiniRec.Services;
using MiniRec.Views;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;
using MF = MiniRec.Native.MediaFoundationInterop;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

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

        string? updatedTo = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--updated-to", StringComparison.OrdinalIgnoreCase))
            {
                updatedTo = e.Args[i + 1];
                break;
            }
        }

        var window = new MainWindow(updatedTo);
        MainWindow = window;

        bool startMinimized = e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (startMinimized)
        {
            // Ensure HWND is created so global hotkeys register silently without flashing UI.
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        }
        else
        {
            window.Show();
        }
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

        // 4. 配置默认值与持久化（含 FPS、AudioMode 与默认零冲突热键）
        var defaultCfg = new AppConfig();
        string defaultShotKey = HotkeyFormatter.Format(defaultCfg.ScreenshotModifiers, defaultCfg.ScreenshotVirtualKey);
        string defaultRecKey = HotkeyFormatter.Format(defaultCfg.RecordModifiers, defaultCfg.RecordVirtualKey);
        if (defaultCfg.Fps != 60 || defaultCfg.AudioMode != AudioCaptureMode.Both ||
            defaultShotKey != "F1" || defaultRecKey != "Shift+F1" ||
            string.IsNullOrWhiteSpace(defaultCfg.ScreenshotDirectory))
        {
            Fail("默认配置校验", $"expected 60fps+Both+F1+Shift+F1, got {defaultCfg.Fps}fps+{defaultCfg.AudioMode}+{defaultShotKey}+{defaultRecKey}");
        }
        else
        {
            Pass("默认配置校验", $"fps={defaultCfg.Fps}, audio={defaultCfg.AudioMode}, shot={defaultShotKey}, rec={defaultRecKey}");
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
                ScreenshotModifiers = Win32Interop.MOD_ALT,
                ScreenshotVirtualKey = 0x41, // A
                Fps = 60,
                AudioMode = AudioCaptureMode.Both,
                OutputDirectory = tempRoot,
                ScreenshotDirectory = tempRoot
            };
            store.Save(cfg);
            AppConfig reloaded = store.Load();
            if (reloaded.RecordModifiers == cfg.RecordModifiers &&
                reloaded.RecordVirtualKey == cfg.RecordVirtualKey &&
                reloaded.ScreenshotModifiers == cfg.ScreenshotModifiers &&
                reloaded.ScreenshotVirtualKey == cfg.ScreenshotVirtualKey &&
                reloaded.Fps == 60 &&
                reloaded.AudioMode == AudioCaptureMode.Both)
            {
                Pass("配置持久化", $"fps={reloaded.Fps}, audio={reloaded.AudioMode}, shotVk=0x{reloaded.ScreenshotVirtualKey:X2}");
            }
            else
            {
                Fail("配置持久化", "读取回来的配置与写入不一致");
            }

            // 4.5 冻结截图快照、取色裁剪与 UIA 探测校验
            byte[] snapBuf = new byte[16 * 16 * 4];
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int idx = (y * 16 + x) * 4;
                    snapBuf[idx] = (byte)(x * 10);     // B
                    snapBuf[idx + 1] = (byte)(y * 12); // G
                    snapBuf[idx + 2] = 200;            // R
                    snapBuf[idx + 3] = 0;              // simulated GDI zero alpha
                }
            }

            var testSnap = new ScreenSnapshot(100, 200, 16, 16, snapBuf);
            bool pxOk = testSnap.TryGetPixel(103, 205, out byte pr, out byte pg, out byte pb) && pr == 200 && pg == 60 && pb == 30;
            byte[] magBuf = new byte[5 * 5 * 4];
            testSnap.CopyMagnifierPatch(103, 205, 5, magBuf);
            int magCenter = (2 * 5 + 2) * 4;
            bool magOk = magBuf[magCenter] == 30 && magBuf[magCenter + 1] == 60 && magBuf[magCenter + 2] == 200 && magBuf[magCenter + 3] == 255;
            byte[] croppedBytes = testSnap.CropRegionBgraBytes(new CaptureRegion(102, 204, 20, 20), out int cw, out int ch);
            bool cropOk = cw == 14 && ch == 12 && croppedBytes.Length == 14 * 12 * 4 && croppedBytes[3] == 255;

            string liveInfo;
            int detVx = 0, detVy = 0, detVw = 1920, detVh = 1080;
            try
            {
                ScreenSnapshot liveSnap = ScreenSnapshot.CaptureVirtualScreen();
                liveInfo = $"{liveSnap.Width}x{liveSnap.Height}";
                detVx = liveSnap.VirtualX;
                detVy = liveSnap.VirtualY;
                detVw = liveSnap.Width;
                detVh = liveSnap.Height;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("error=5", StringComparison.Ordinal))
            {
                // Locked / non-interactive desktop session denies BitBlt with ERROR_ACCESS_DENIED.
                liveInfo = "skipped(no-interactive-desktop)";
            }

            var winList = Win32Interop.EnumerateVisibleWindowsWithHwnd(Environment.ProcessId);
            using var detector = new UiElementDetector(winList, (detVx, detVy, detVw, detVh), Environment.ProcessId);
            _ = detector.GetCandidateRegionsAt(detVx + detVw / 2, detVy + detVh / 2);

            if (pxOk && magOk && cropOk)
            {
                Pass("截图快照与 UIA 探测", $"live={liveInfo}, crop={cw}x{ch}, windows={winList.Count}");
            }
            else
            {
                Fail("截图快照与 UIA 探测", $"px={pxOk}, mag={magOk}, crop={cropOk}, live={liveInfo}");
            }

            // 4.6 Phase 2 马赛克处理器、标注命令栈与物理像素合成校验
            byte[] pixData = new byte[16 * 16 * 4];
            byte[] blurData = new byte[16 * 16 * 4];
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int idx = (y * 16 + x) * 4;
                    byte val = ((x + y) % 2 == 0) ? (byte)240 : (byte)20;
                    pixData[idx] = val; pixData[idx + 1] = val; pixData[idx + 2] = val; pixData[idx + 3] = 255;
                    blurData[idx] = val; blurData[idx + 1] = val; blurData[idx + 2] = val; blurData[idx + 3] = 255;
                }
            }
            MosaicProcessor.ApplyPixelate(pixData, 16, 16, 4);
            MosaicProcessor.ApplyBoxBlur(blurData, 16, 16, 2);
            bool mosaicOk = pixData[0] == pixData[4] && pixData[3] == 255 &&
                            blurData[0] > 40 && blurData[0] < 220 && blurData[3] == 255;

            var engine = new AnnotationEngine();
            var annoStyle = new AnnotationStyle(Media.Color.FromRgb(239, 68, 68));
            var r1 = new RectAnnotation(annoStyle, new Wpf.Point(10, 10), new Wpf.Point(60, 50));
            var a1 = new ArrowAnnotation(annoStyle, new Wpf.Point(15, 60), new Wpf.Point(90, 80));
            var b1 = new NumberBadgeAnnotation(annoStyle, new Wpf.Point(30, 30), engine.NextBadgeNumber());
            engine.Add(r1);
            engine.Add(a1);
            engine.Add(b1);
            int num2 = engine.NextBadgeNumber();
            var b2 = new NumberBadgeAnnotation(annoStyle, new Wpf.Point(60, 30), num2);
            engine.Add(b2);
            r1.Translate(5, 5);
            engine.RecordMove(r1, 5, 5);
            engine.Delete(a1);
            bool undoRedoOk = engine.Items.Count == 3 &&
                              engine.Undo() && engine.Items.Count == 4 &&
                              engine.Undo() && r1.Start.X == 10 &&
                              engine.Redo() && r1.Start.X == 15 &&
                              num2 == 2;

            byte[] compBase = new byte[120 * 100 * 4];
            var compSnap = new ScreenSnapshot(0, 0, 120, 100, compBase);
            var compRegion = new CaptureRegion(0, 0, 120, 100);
            engine.Add(new MosaicAnnotation(annoStyle, MosaicMode.Pixel, 8, 18, [new Wpf.Point(20, 20), new Wpf.Point(80, 20)]));
            engine.Add(new TextAnnotation(annoStyle, new Wpf.Point(20, 70), "MiniRec"));
            Imaging.BitmapSource compBmp = engine.RenderCompositeBitmap(compSnap, compRegion);
            byte[] compOut = new byte[120 * 100 * 4];
            compBmp.CopyPixels(compOut, 120 * 4, 0);
            bool hasNonZeroPixel = false;
            for (int i = 0; i < compOut.Length; i += 4)
            {
                if (compOut[i] != 0 || compOut[i + 1] != 0 || compOut[i + 2] != 0)
                {
                    hasNonZeroPixel = true;
                    break;
                }
            }

            if (mosaicOk && undoRedoOk && compBmp.PixelWidth == 120 && compBmp.PixelHeight == 100 && hasNonZeroPixel)
            {
                Pass("标注引擎与物理像素合成", $"mosaic=OK, undoRedo=OK, composite={compBmp.PixelWidth}x{compBmp.PixelHeight}");
            }
            else
            {
                Fail("标注引擎与物理像素合成", $"mosaic={mosaicOk}, undoRedo={undoRedoOk}, size={compBmp.PixelWidth}x{compBmp.PixelHeight}, nonZero={hasNonZeroPixel}");
            }

            // 4.7 Phase 3 关闭行为配置 + Win11 原生离线 OCR 端到端实测
            var closeCfg = new AppConfig { CloseBehavior = CloseButtonBehavior.MinimizeToTray };
            store.Save(closeCfg);
            AppConfig reloadedClose = store.Load();
            string cleanedCjk = NativeOcrService.CleanCjkSpaces("微 信 截 图 MiniRec 2026 测试");
            bool cjkCleanOk = reloadedClose.CloseBehavior == CloseButtonBehavior.MinimizeToTray &&
                              cleanedCjk == "微信截图 MiniRec 2026 测试";

            byte[] ocrBgra = RenderOcrTestBgra("MiniRec 2026 测试", 400, 100);
            OcrDocumentResult ocrRes = NativeOcrService.RecognizeBgraAsync(ocrBgra, 400, 100).GetAwaiter().GetResult();
            bool hasWords = ocrRes.Lines.Count > 0 && ocrRes.Lines[0].Words.Count > 0;
            string partialSelect = hasWords && ocrRes.Lines[0].Words.Count >= 2
                ? NativeOcrService.ExtractSelectedText(ocrRes.Lines, 0, 0, 0, 1, joinLines: false)
                : string.Empty;
            var syntheticLines = new List<OcrLineResult>
            {
                new("微信截图", new Wpf.Rect(0, 0, 80, 20), [
                    new OcrWordResult("微", new Wpf.Rect(0, 0, 20, 20)),
                    new OcrWordResult("信", new Wpf.Rect(20, 0, 20, 20)),
                    new OcrWordResult("截", new Wpf.Rect(40, 0, 20, 20)),
                    new OcrWordResult("图", new Wpf.Rect(60, 0, 20, 20))
                ]),
                new("MiniRec 2026", new Wpf.Rect(0, 30, 120, 20), [
                    new OcrWordResult("MiniRec", new Wpf.Rect(0, 30, 60, 20)),
                    new OcrWordResult("2026", new Wpf.Rect(65, 30, 50, 20))
                ])
            };
            string rangeText = NativeOcrService.ExtractSelectedText(syntheticLines, 0, 1, 1, 0, joinLines: false);
            string joinedRangeText = NativeOcrService.ExtractSelectedText(syntheticLines, 0, 1, 1, 0, joinLines: true);
            bool extractOk = rangeText == $"信截图{Environment.NewLine}MiniRec" && joinedRangeText == "信截图 MiniRec";

            bool ocrOk = ocrRes.Success &&
                         hasWords &&
                         ocrRes.Lines[0].PhysicalBounds.Width > 0 &&
                         (ocrRes.FullText.Contains("MiniRec", StringComparison.OrdinalIgnoreCase) ||
                          ocrRes.FullText.Contains("2026", StringComparison.Ordinal));

            if (cjkCleanOk && extractOk && ocrOk)
            {
                Pass("Win11 原生离线 OCR", $"lines={ocrRes.Lines.Count}, words={ocrRes.Lines[0].Words.Count}, partial=\"{partialSelect}\"");
            }
            else
            {
                Fail("Win11 原生离线 OCR", $"cjkClean={cjkCleanOk}, extract={extractOk}, success={ocrRes.Success}, lines={ocrRes.Lines.Count}, text=\"{ocrRes.FullText}\", err={ocrRes.ErrorMessage}");
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

            // 6. 自动更新解析、校验与替换脚本自测
            bool verParseOk =
                UpdateService.TryParseVersion("v1.2.3", out Version v123) &&
                UpdateService.TryParseVersion("1.2.0-beta.1", out Version v120) &&
                UpdateService.IsNewerVersion(v123, v120) &&
                !UpdateService.IsNewerVersion(v120, v123);

            string sampleManifest = """
            {
              "version": "1.3.0",
              "tag": "v1.3.0",
              "publishedAt": "2026-11-28T10:00:00Z",
              "assetName": "MiniRec-win-x64.exe",
              "downloadUrl": "https://github.com/codywon/mini-screen-recorder/releases/download/v1.3.0/MiniRec-win-x64.exe",
              "releaseUrl": "https://github.com/codywon/mini-screen-recorder/releases/tag/v1.3.0",
              "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "notes": "- 微信风格截图精修"
            }
            """;
            UpdateReleaseInfo? manifestInfo = UpdateService.ParseManifestJson(sampleManifest);

            string sampleGhApi = """
            {
              "tag_name": "v1.4.0",
              "draft": false,
              "prerelease": false,
              "html_url": "https://github.com/codywon/mini-screen-recorder/releases/tag/v1.4.0",
              "body": "API fallback release notes",
              "assets": [
                { "name": "MiniRec-win-x64.exe", "browser_download_url": "https://example.com/MiniRec-win-x64.exe" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.com/SHA256SUMS.txt" }
              ]
            }
            """;
            UpdateReleaseInfo? apiInfo = UpdateService.ParseGitHubReleaseJson(sampleGhApi);
            string? parsedSum = UpdateService.ExtractSha256FromSums(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  MiniRec-win-x64.exe\n",
                "MiniRec-win-x64.exe");

            string fakeExePath = Path.Combine(tempRoot, "fake-update.exe");
            byte[] fakeExeBytes = new byte[40 * 1024];
            fakeExeBytes[0] = (byte)'M';
            fakeExeBytes[1] = (byte)'Z';
            File.WriteAllBytes(fakeExePath, fakeExeBytes);
            bool peOk = UpdateService.IsValidWindowsExecutable(fakeExePath);
            string sha = UpdateService.ComputeFileSha256(fakeExePath);
            string updaterScript = UpdateService.BuildUpdaterScript(
                12345,
                Path.Combine(tempRoot, "MiniRec.exe"),
                fakeExePath,
                "1.0.0",
                "1.3.0",
                Path.Combine(tempRoot, "apply.ps1"));

            if (verParseOk &&
                manifestInfo is { Version: { Major: 1, Minor: 3, Build: 0 }, Sha256.Length: 64 } &&
                apiInfo is { Version: { Major: 1, Minor: 4, Build: 0 }, Sha256SumsUrl: not null } &&
                parsedSum == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" &&
                peOk && sha.Length == 64 &&
                updaterScript.Contains("--updated-to", StringComparison.Ordinal))
            {
                Pass("自动更新解析与校验", $"manifest={manifestInfo.Tag}, api={apiInfo.Tag}, sha256=OK");
            }
            else
            {
                Fail("自动更新解析与校验", "版本号/清单/API/SHA256或脚本生成异常");
            }

            // 7. Direct2D 真彩色 Emoji 贴纸渲染自测
            Imaging.BitmapSource? emojiBmp = ColorEmojiRenderer.RenderEmoji("😀", 96);
            int coloredEmojiPixels = 0;
            if (emojiBmp is not null)
            {
                byte[] ePixels = new byte[96 * 96 * 4];
                emojiBmp.CopyPixels(ePixels, 96 * 4, 0);
                for (int i = 0; i < ePixels.Length; i += 4)
                {
                    byte eb = ePixels[i];
                    byte eg = ePixels[i + 1];
                    byte er = ePixels[i + 2];
                    byte ea = ePixels[i + 3];
                    if (ea > 180 && (Math.Abs(er - eg) > 15 || Math.Abs(eg - eb) > 15))
                    {
                        coloredEmojiPixels++;
                    }
                }
            }
            if (coloredEmojiPixels > 100)
            {
                Pass("Direct2D 真彩色 Emoji 渲染", $"96x96 coloredPixels={coloredEmojiPixels}");
            }
            else
            {
                Fail("Direct2D 真彩色 Emoji 渲染", $"coloredPixels={coloredEmojiPixels}");
            }

            // 8. 滚动截长图拼接引擎自测（含固定顶栏/固定底栏剔除与多步长精确位移匹配）
            const int lsW = 300;
            const int lsViewH = 260;
            const int lsHeaderH = 34;
            const int lsFooterH = 26;
            const int lsScrollBodyH = lsViewH - lsHeaderH - lsFooterH; // 200
            const int lsVirtualContentH = 620;

            byte[] MakeSyntheticViewport(int scrollOffset)
            {
                byte[] buf = new byte[lsW * lsViewH * 4];
                for (int y = 0; y < lsViewH; y++)
                {
                    int rowOff = y * lsW * 4;
                    for (int x = 0; x < lsW; x++)
                    {
                        int idx = rowOff + x * 4;
                        if (y < lsHeaderH)
                        {
                            buf[idx] = 45; buf[idx + 1] = 45; buf[idx + 2] = 52; buf[idx + 3] = 255;
                        }
                        else if (y >= lsViewH - lsFooterH)
                        {
                            buf[idx] = 240; buf[idx + 1] = 240; buf[idx + 2] = 245; buf[idx + 3] = 255;
                        }
                        else
                        {
                            int contentY = Math.Min(lsVirtualContentH - 1, scrollOffset + (y - lsHeaderH));
                            int block = contentY / 17;
                            int stripe = ((x / 11) + block * 3 + (contentY % 13)) % 7;
                            buf[idx] = (byte)((contentY * 5 + x * 2 + stripe * 31) % 256);
                            buf[idx + 1] = (byte)((block * 47 + stripe * 29 + (contentY % 23) * 9) % 256);
                            buf[idx + 2] = (byte)((255 - (contentY * 3 % 200) + (x % 19) * 5) % 256);
                            buf[idx + 3] = 255;
                        }
                    }
                }
                return buf;
            }

            var stitcher = new LongScreenshotStitcher(lsW, lsViewH);
            int[] offsets = [0, 85, 190, 305, 420];
            bool allStepsOk = true;
            foreach (int off in offsets)
            {
                StitchFrameResult r = stitcher.TryAppendFrame(MakeSyntheticViewport(off));
                if (off == 0 && r.Status != StitchFrameStatus.FirstFrame) allStepsOk = false;
                if (off > 0 && r.Status != StitchFrameStatus.Appended) allStepsOk = false;
            }
            int expectedTotalH = lsHeaderH + lsScrollBodyH + offsets[^1] + lsFooterH; // 34 + 200 + 420 + 26 = 680
            Imaging.BitmapSource stitchedFinal = stitcher.BuildFinalBitmap();
            Imaging.BitmapSource stitchedPreview = stitcher.BuildPreviewBitmap();

            if (allStepsOk &&
                Math.Abs(stitcher.TotalHeight - expectedTotalH) <= 2 &&
                stitcher.StickyTopRows >= lsHeaderH - 2 &&
                stitcher.StickyBottomRows >= lsFooterH - 2 &&
                stitchedFinal.PixelHeight == stitcher.TotalHeight &&
                stitchedPreview.PixelWidth > 0)
            {
                Pass("滚动截长图拼接引擎", $"height={stitcher.TotalHeight}/{expectedTotalH}, sticky=({stitcher.StickyTopRows},{stitcher.StickyBottomRows})");
            }
            else
            {
                Fail("滚动截长图拼接引擎", $"allStepsOk={allStepsOk}, height={stitcher.TotalHeight}/{expectedTotalH}, sticky=({stitcher.StickyTopRows},{stitcher.StickyBottomRows})");
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

    private static byte[] RenderOcrTestBgra(string text, int width, int height)
    {
        using var bmp = new Drawing.Bitmap(width, height, DrawingImaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Drawing.Font("Microsoft YaHei UI", 26f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var brush = new Drawing.SolidBrush(Drawing.Color.Black);
            g.DrawString(text, font, brush, new Drawing.PointF(24f, 30f));
        }

        var rect = new Drawing.Rectangle(0, 0, width, height);
        DrawingImaging.BitmapData bmpData = bmp.LockBits(rect, DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(bmpData.Scan0 + y * bmpData.Stride, bgra, y * width * 4, width * 4);
            }
            return bgra;
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
    }
}
