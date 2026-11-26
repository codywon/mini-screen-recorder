using System.Runtime.InteropServices;
using Wpf = System.Windows;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Services;

/// <summary>
/// 剪贴板写入辅助类：规避 WPF Clipboard.Flush 的二次开锁竞争与短暂占锁导致的 CLIPBRD_E_CANT_OPEN 崩溃。
/// </summary>
internal static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public static bool TrySetText(string text)
    {
        if (text is null) return false;

        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard())
                    {
                        return false;
                    }

                    int byteCount = checked((text.Length + 1) * sizeof(char));
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
                    if (hGlobal == IntPtr.Zero)
                    {
                        return false;
                    }

                    IntPtr locked = GlobalLock(hGlobal);
                    if (locked == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    try
                    {
                        if (text.Length > 0)
                        {
                            Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                        }
                        Marshal.WriteInt16(locked, text.Length * sizeof(char), 0);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    // 所有权已移交系统剪贴板，成功时禁止释放 hGlobal。
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (attempt < 11)
            {
                Thread.Sleep(15);
            }
        }

        return false;
    }

    public static bool TrySetImage(Imaging.BitmapSource bitmap)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Wpf.Clipboard.SetImage(bitmap);
                return true;
            }
            catch (ExternalException)
            {
                if (attempt < 9)
                {
                    Thread.Sleep(20);
                }
            }
        }

        try
        {
            Wpf.Clipboard.SetDataObject(bitmap, copy: false);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
