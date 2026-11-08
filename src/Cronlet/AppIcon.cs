using System.Runtime.InteropServices;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 从 exe 自身的图标资源（ApplicationIcon）取 HICON，不需要额外的 .ico 文件。
/// DotTray 会复制句柄，调用方用完即可 <see cref="Release"/>。
/// </summary>
internal static class AppIcon
{
    public static IntPtr Extract()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return IntPtr.Zero;

        ExtractIconEx(exe, 0, out IntPtr large, out IntPtr small, 1);

        // 只要 32x32 的那份，16x16 的先用完就释放
        if (small != IntPtr.Zero) DestroyIcon(small);

        return large;
    }

    public static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero) DestroyIcon(handle);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
