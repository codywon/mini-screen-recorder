using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiniRec.Services;

internal sealed record UpdateReleaseInfo(
    Version Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    string ReleaseUrl,
    string? Sha256,
    string? Sha256SumsUrl,
    string Notes,
    DateTimeOffset? PublishedAt);

internal enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

internal sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    UpdateReleaseInfo? Release = null,
    string? ErrorMessage = null);

internal readonly record struct UpdateDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    string StageText)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0.0, 100.0)
        : null;
}

internal sealed class UpdateService
{
    internal const string RepoOwner = "codywon";
    internal const string RepoName = "mini-screen-recorder";
    internal const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    internal const string LatestManifestUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/latest.json";
    internal const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    internal const string DefaultAssetName = "MiniRec-win-x64.exe";
    internal static readonly TimeSpan AutoCheckCooldown = TimeSpan.FromHours(12);

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MiniRec", GetCurrentVersionString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static Version GetCurrentVersion()
    {
        Version? raw = Assembly.GetExecutingAssembly().GetName().Version;
        if (raw is null)
        {
            return new Version(1, 0, 0);
        }
        return NormalizeVersion(raw);
    }

    public static string GetCurrentVersionString() => FormatVersion(GetCurrentVersion());

    public static string FormatVersion(Version version)
    {
        Version v = NormalizeVersion(version);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    internal static Version NormalizeVersion(Version version) =>
        new(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));

    internal static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string cleaned = raw.Trim().TrimStart('v', 'V');
        int suffixIdx = cleaned.IndexOfAny(['-', '+']);
        if (suffixIdx >= 0)
        {
            cleaned = cleaned[..suffixIdx];
        }

        string[] parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4) return false;

        if (!int.TryParse(parts[0], out int major) || major < 0) return false;
        if (!int.TryParse(parts[1], out int minor) || minor < 0) return false;
        int build = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out build) || build < 0)) return false;

        version = new Version(major, minor, build);
        return true;
    }

    internal static bool IsNewerVersion(Version remote, Version current) =>
        NormalizeVersion(remote) > NormalizeVersion(current);

    public static bool ShouldRunAutoCheck(AppConfig config)
    {
        if (!config.AutoCheckUpdates) return false;
        if (config.LastUpdateCheckUtc == DateTime.MinValue) return true;
        return DateTime.UtcNow - config.LastUpdateCheckUtc >= AutoCheckCooldown;
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        Version current = GetCurrentVersion();

        using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        checkCts.CancelAfter(TimeSpan.FromSeconds(10));

        // 1. 优先拉取 Release 里的 latest.json（不占用 GitHub REST API 匿名限流配额）
        string? manifestError = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestManifestUrl);
            using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, checkCts.Token);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(checkCts.Token);
                UpdateReleaseInfo? parsed = ParseManifestJson(json);
                if (parsed is not null)
                {
                    return BuildResult(current, parsed);
                }
            }
            else if (resp.StatusCode != HttpStatusCode.NotFound)
            {
                manifestError = $"HTTP {(int)resp.StatusCode}";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            manifestError = "连接 GitHub 超时";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            manifestError = ex.Message;
        }

        // 2. 回退到 GitHub Releases API（兼容尚未附带 latest.json 的手动 Release）
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, checkCts.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // 仓库还没有发布任何正式 Release，视为已是最新，避免误报错
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current);
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, current, ErrorMessage: "GitHub 接口请求过于频繁，请稍后再试");
            }
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, current, ErrorMessage: $"检查更新失败 (HTTP {(int)resp.StatusCode})");
            }

            string json = await resp.Content.ReadAsStringAsync(checkCts.Token);
            UpdateReleaseInfo? parsed = ParseGitHubReleaseJson(json);
            if (parsed is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current);
            }

            return BuildResult(current, parsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current, ErrorMessage: "连接 GitHub 超时，请检查网络或代理");
        }
        catch (Exception ex)
        {
            string reason = !string.IsNullOrWhiteSpace(manifestError) ? manifestError : ex.Message;
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current, ErrorMessage: $"无法连接更新服务器：{reason}");
        }
    }

    private static UpdateCheckResult BuildResult(Version current, UpdateReleaseInfo release)
    {
        if (IsNewerVersion(release.Version, current))
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, current, release);
        }
        return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, release);
    }

    internal static UpdateReleaseInfo? ParseManifestJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? verRaw = ReadString(root, "version") ?? ReadString(root, "tag");
            if (!TryParseVersion(verRaw, out Version version)) return null;

            string? downloadUrl = ReadString(root, "downloadUrl");
            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

            string tag = ReadString(root, "tag") ?? $"v{FormatVersion(version)}";
            string assetName = ReadString(root, "assetName") ?? DefaultAssetName;
            string releaseUrl = ReadString(root, "releaseUrl") ?? $"{ReleasesPageUrl}/tag/{tag}";
            string? sha256 = NormalizeSha256(ReadString(root, "sha256"));
            string notes = ReadString(root, "notes") ?? string.Empty;
            DateTimeOffset? publishedAt = ReadDateTimeOffset(root, "publishedAt");

            return new UpdateReleaseInfo(
                version,
                tag,
                assetName,
                downloadUrl,
                releaseUrl,
                sha256,
                Sha256SumsUrl: null,
                notes,
                publishedAt);
        }
        catch
        {
            return null;
        }
    }

    internal static UpdateReleaseInfo? ParseGitHubReleaseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("draft", out JsonElement draftProp) && draftProp.ValueKind == JsonValueKind.True)
            {
                return null;
            }
            if (root.TryGetProperty("prerelease", out JsonElement preProp) && preProp.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            string? tag = ReadString(root, "tag_name");
            if (!TryParseVersion(tag, out Version version)) return null;

            string releaseUrl = ReadString(root, "html_url") ?? $"{ReleasesPageUrl}/tag/{tag}";
            string notes = ReadString(root, "body") ?? string.Empty;
            DateTimeOffset? publishedAt = ReadDateTimeOffset(root, "published_at");

            string? exeUrl = null;
            string? exeName = null;
            string? sumsUrl = null;

            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string? name = ReadString(asset, "name");
                    string? url = ReadString(asset, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;

                    if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sumsUrl = url;
                        continue;
                    }

                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        bool preferred = name.Equals(DefaultAssetName, StringComparison.OrdinalIgnoreCase)
                            || name.Equals("MiniRec.exe", StringComparison.OrdinalIgnoreCase);
                        if (exeUrl is null || preferred)
                        {
                            exeUrl = url;
                            exeName = name;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(exeUrl) || string.IsNullOrWhiteSpace(exeName))
            {
                return null;
            }

            return new UpdateReleaseInfo(
                version,
                tag!,
                exeName,
                exeUrl,
                releaseUrl,
                Sha256: null,
                Sha256SumsUrl: sumsUrl,
                notes,
                publishedAt);
        }
        catch
        {
            return null;
        }
    }

    internal static string? ExtractSha256FromSums(string sumsContent, string assetName)
    {
        if (string.IsNullOrWhiteSpace(sumsContent)) return null;

        string[] lines = sumsContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length < 64) continue;

            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            string? hash = NormalizeSha256(tokens[0]);
            if (hash is null) continue;

            if (tokens.Length == 1)
            {
                return hash;
            }

            string fileToken = tokens[^1].TrimStart('*');
            if (fileToken.Equals(assetName, StringComparison.OrdinalIgnoreCase) ||
                fileToken.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return hash;
            }
        }

        return null;
    }

    internal static string? NormalizeSha256(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (trimmed.Length != 64) return null;
        foreach (char c in trimmed)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return null;
        }
        return trimmed.ToLowerInvariant();
    }

    public static string GetUpdatesDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiniRec",
            "updates");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task<string> DownloadUpdateAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        string updatesDir = GetUpdatesDirectory();
        string targetPath = Path.Combine(updatesDir, $"MiniRec-{FormatVersion(release.Version)}.exe");
        string partPath = targetPath + ".part";

        try
        {
            if (File.Exists(partPath)) File.Delete(partPath);
        }
        catch
        {
            // Ignore stale partial cleanup errors.
        }

        progress?.Report(new UpdateDownloadProgress(0, null, "正在连接下载服务器..."));

        using var req = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
        using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long? totalBytes = resp.Content.Headers.ContentLength;
        long received = 0;

        await using (Stream remoteStream = await resp.Content.ReadAsStreamAsync(ct))
        await using (var localStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await remoteStream.ReadAsync(buffer, ct)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress?.Report(new UpdateDownloadProgress(received, totalBytes, "正在下载安装包..."));
            }
            await localStream.FlushAsync(ct);
        }

        // 基本可执行文件完整性检查（PE MZ 头 + 合理体积，防止误下载到错误页 HTML）
        progress?.Report(new UpdateDownloadProgress(received, totalBytes ?? received, "正在校验文件完整性..."));
        if (!IsValidWindowsExecutable(partPath))
        {
            TryDeleteQuietly(partPath);
            throw new InvalidOperationException("下载的文件不是有效的 Windows 可执行程序，请重试或前往发布页手动下载");
        }

        string? expectedSha256 = release.Sha256;
        if (expectedSha256 is null && !string.IsNullOrWhiteSpace(release.Sha256SumsUrl))
        {
            try
            {
                string sumsText = await Http.GetStringAsync(release.Sha256SumsUrl, ct);
                expectedSha256 = ExtractSha256FromSums(sumsText, release.AssetName);
            }
            catch
            {
                // Optional fallback sums download failed; fail safely only if hash was provided.
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actualSha256 = ComputeFileSha256(partPath);
            if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteQuietly(partPath);
                throw new InvalidOperationException("文件 SHA256 校验未通过，已自动丢弃以确保安全");
            }
        }

        File.Move(partPath, targetPath, overwrite: true);
        return targetPath;
    }

    internal static bool IsValidWindowsExecutable(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 32 * 1024) return false;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int b0 = fs.ReadByte();
            int b1 = fs.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch
        {
            return false;
        }
    }

    internal static string ComputeFileSha256(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool CanSelfReplaceCurrentExecutable(out string? reason)
    {
        if (Debugger.IsAttached)
        {
            reason = "当前处于调试模式，已跳过原位覆盖。";
            return false;
        }

        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            reason = "无法定位当前程序路径。";
            return false;
        }

        string? dir = Path.GetDirectoryName(currentExe);
        if (string.IsNullOrWhiteSpace(dir))
        {
            reason = "无法定位当前程序目录。";
            return false;
        }

        // 如果同目录存在 MiniRec.dll（例如 bin\Debug 或 dotnet run），属于非单文件开发态，避免覆盖构建产物
        if (File.Exists(Path.Combine(dir, "MiniRec.dll")) &&
            Path.GetFileName(currentExe).Equals("MiniRec.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "当前为本地开发构建模式，不自动覆盖开发目录。你可以直接打开下载目录手动替换。";
            return false;
        }

        // 探测所在目录写权限
        string probeFile = Path.Combine(dir, $".minirec_write_test_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
        }
        catch
        {
            reason = "当前程序所在目录需要管理员权限才能写入，请打开下载目录手动替换。";
            return false;
        }

        reason = null;
        return true;
    }

    public static bool TryLaunchSelfUpdater(string downloadedExePath, string targetVersionText, out string? error)
    {
        string? currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            error = "无法定位当前程序路径";
            return false;
        }

        string scriptPath = Path.Combine(GetUpdatesDirectory(), $"apply-update-{Guid.NewGuid():N}.ps1");
        string script = BuildUpdaterScript(
            Environment.ProcessId,
            currentExe,
            downloadedExePath,
            GetCurrentVersionString(),
            targetVersionText,
            scriptPath);

        try
        {
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(currentExe) ?? Environment.CurrentDirectory
            });

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            TryDeleteQuietly(scriptPath);
            error = $"启动更新脚本失败：{ex.Message}";
            return false;
        }
    }

    internal static string BuildUpdaterScript(
        int oldPid,
        string targetExePath,
        string newExePath,
        string fromVersion,
        string toVersion,
        string selfScriptPath)
    {
        static string EscapePsSingleQuoted(string s) => s.Replace("'", "''");

        string targetEsc = EscapePsSingleQuoted(targetExePath);
        string newEsc = EscapePsSingleQuoted(newExePath);
        string backupEsc = EscapePsSingleQuoted(targetExePath + ".bak");
        string selfEsc = EscapePsSingleQuoted(selfScriptPath);
        string fromEsc = EscapePsSingleQuoted(fromVersion);
        string toEsc = EscapePsSingleQuoted(toVersion);

        return $$"""
$ErrorActionPreference = 'Stop'
$oldPid = {{oldPid}}
$target = '{{targetEsc}}'
$source = '{{newEsc}}'
$backup = '{{backupEsc}}'
$self   = '{{selfEsc}}'

# 1. 等待旧进程完全退出并释放单实例互斥锁与文件句柄
for ($i = 0; $i -lt 80; $i++) {
    $proc = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
    if ($null -eq $proc) { break }
    Start-Sleep -Milliseconds 250
}
Start-Sleep -Milliseconds 350

# 2. 带重试原子备份与替换，失败自动回滚
$replaced = $false
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    try {
        if (Test-Path -LiteralPath $backup) {
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination $backup -Force
        }
        Move-Item -LiteralPath $source -Destination $target -Force
        $replaced = $true
        break
    }
    catch {
        if (-not (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $target -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 350
    }
}

if ($replaced) {
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $target -ArgumentList '--updated-from', '{{fromEsc}}', '--updated-to', '{{toEsc}}'
} elseif (Test-Path -LiteralPath $target) {
    Start-Process -FilePath $target
}

Start-Sleep -Milliseconds 300
Remove-Item -LiteralPath $self -Force -ErrorAction SilentlyContinue
""";
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch errors.
        }
    }

    public static void RevealFileInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch
        {
            // Ignore explorer launch errors.
        }
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement obj, string name)
    {
        string? raw = ReadString(obj, name);
        return DateTimeOffset.TryParse(raw, out DateTimeOffset dto) ? dto : null;
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
