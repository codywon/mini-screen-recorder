using System.IO;
using System.Text.Json;
using MiniRec.Native;
using Forms = System.Windows.Forms;

namespace MiniRec.Services;

internal enum CloseButtonBehavior
{
    Ask = 0,
    MinimizeToTray = 1,
    Exit = 2
}

internal sealed class AppConfig
{
    public static readonly int[] SupportedFps = [15, 24, 30, 60];

    public uint RecordModifiers { get; set; } = Win32Interop.MOD_SHIFT;
    public uint RecordVirtualKey { get; set; } = 0x70; // F1

    public uint ToggleUiModifiers { get; set; } = Win32Interop.MOD_CONTROL | Win32Interop.MOD_ALT;
    public uint ToggleUiVirtualKey { get; set; } = 0x48; // H

    public uint ScreenshotModifiers { get; set; } = 0;
    public uint ScreenshotVirtualKey { get; set; } = 0x70; // F1

    public string OutputDirectory { get; set; } = GetDefaultOutputDirectory();
    public string ScreenshotDirectory { get; set; } = GetDefaultScreenshotDirectory();
    public int Fps { get; set; } = 60;
    public AudioCaptureMode AudioMode { get; set; } = AudioCaptureMode.Both;
    public CloseButtonBehavior CloseBehavior { get; set; } = CloseButtonBehavior.Ask;

    public static string GetDefaultOutputDirectory()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return Path.Combine(videos, "MiniRec");
    }

    public static string GetDefaultScreenshotDirectory()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return Path.Combine(pictures, "MiniRec");
    }
}

internal sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath { get; }

    public ConfigStore(string? customPath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(customPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MiniRec",
                "config.json")
            : customPath;
    }

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppConfig();
            }

            string json = File.ReadAllText(FilePath);
            AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (loaded is null)
            {
                return new AppConfig();
            }

            if (string.IsNullOrWhiteSpace(loaded.OutputDirectory))
            {
                loaded.OutputDirectory = AppConfig.GetDefaultOutputDirectory();
            }
            if (string.IsNullOrWhiteSpace(loaded.ScreenshotDirectory))
            {
                loaded.ScreenshotDirectory = AppConfig.GetDefaultScreenshotDirectory();
            }
            if (!AppConfig.SupportedFps.Contains(loaded.Fps))
            {
                loaded.Fps = 60;
            }
            if (!Enum.IsDefined(loaded.AudioMode))
            {
                loaded.AudioMode = AudioCaptureMode.Both;
            }
            if (!Enum.IsDefined(loaded.CloseBehavior))
            {
                loaded.CloseBehavior = CloseButtonBehavior.Ask;
            }
            if (loaded.RecordVirtualKey == 0)
            {
                loaded.RecordModifiers = Win32Interop.MOD_SHIFT;
                loaded.RecordVirtualKey = 0x70;
            }
            if (loaded.ToggleUiVirtualKey == 0)
            {
                loaded.ToggleUiModifiers = Win32Interop.MOD_CONTROL | Win32Interop.MOD_ALT;
                loaded.ToggleUiVirtualKey = 0x48;
            }
            if (loaded.ScreenshotVirtualKey == 0)
            {
                loaded.ScreenshotModifiers = 0;
                loaded.ScreenshotVirtualKey = 0x70;
            }

            return loaded;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        string? dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}

internal static class HotkeyFormatter
{
    public static string Format(uint modifiers, uint vk)
    {
        List<string> parts = [];
        if ((modifiers & Win32Interop.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32Interop.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32Interop.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32Interop.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(FormatVirtualKey(vk));
        return string.Join("+", parts);
    }

    public static string FormatVirtualKey(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A) // A-Z
        {
            return ((char)vk).ToString();
        }
        if (vk is >= 0x30 and <= 0x39) // 0-9
        {
            return ((char)vk).ToString();
        }
        if (vk is >= 0x70 and <= 0x87) // F1-F24
        {
            return $"F{vk - 0x6F}";
        }

        return vk switch
        {
            0x20 => "Space",
            0x0D => "Enter",
            0x09 => "Tab",
            0x08 => "Backspace",
            0x1B => "Esc",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => FormatFallbackKey(vk)
        };
    }

    private static string FormatFallbackKey(uint vk)
    {
        var key = (Forms.Keys)vk;
        string name = key.ToString();
        return int.TryParse(name, out _) ? $"VK_{vk:X2}" : name;
    }
}
