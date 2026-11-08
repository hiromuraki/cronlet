using System.Diagnostics;
using DotTray;
using DotTray.Items;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 托盘外壳：打开配置文件 / 打开脚本目录 / 打开日志目录 / 重载配置 / 退出。
/// 基于 DotTray（直接用 Win32 shell API），不依赖 WinForms，因此可以走 NativeAOT。
/// 图标从 exe 自身的图标资源里取 HICON，不需要额外的 .ico 文件。
/// </summary>
internal sealed class TrayContext : IDisposable
{
    private readonly DefaultPopupMenuHandler _menu = new();
    private readonly NotifyIcon<DefaultPopupMenuHandler> _icon;
    private readonly string _configPath;

    public TrayContext(string configPath, Action onExit, Func<Task<string?>> reload)
    {
        _configPath = configPath;

        _menu.Items.Add(new MenuItem { Text = "打开配置文件", Clicked = _ => OpenConfig() });
        _menu.Items.Add(new MenuItem { Text = "打开脚本目录", Clicked = _ => OpenFolder(Paths.Scripts) });
        _menu.Items.Add(new MenuItem { Text = "打开日志目录", Clicked = _ => OpenFolder(Paths.Logs) });
        _menu.Items.Add(SeparatorItem.Instance);

        var reloadItem = new MenuItem { Text = "重载配置" };
        reloadItem.Clicked = _ => Reload(reloadItem, reload);
        _menu.Items.Add(reloadItem);

        _menu.Items.Add(SeparatorItem.Instance);
        _menu.Items.Add(new MenuItem { Text = "退出", Clicked = _ => onExit() });

        // 关闭只走 Dispose 一条路：把令牌交给 DotTray 会让"取消令牌"和"Dispose"
        // 各自投递一次 WM_CLOSE，后一次在窗口已销毁时抛 Win32Exception(1400)。
        IntPtr handle = AppIcon.Extract();
        try
        {
            _icon = NotifyIcon.Run(handle, _menu, CancellationToken.None);
        }
        finally
        {
            AppIcon.Release(handle);
        }

        _icon.SetToolTip("Cronlet");
    }


    private void Reload(MenuItem item, Func<Task<string?>> reload)
    {
        item.IsDisabled = true;

        _ = Task.Run(async () =>
        {
            try
            {
                string? error = await reload();
                if (error is not null) ShowError(error);
            }
            finally
            {
                item.IsDisabled = false;
            }
        });
    }

    private void ShowError(string message) =>
        _icon.ShowBalloon(new BalloonNotification
        {
            Title = "Cronlet",
            Message = message,
            Icon = BalloonNotificationIcon.Error,
        });

    private void OpenConfig()
    {
        try
        {
            ConfigLoader.EnsureExists(_configPath);
            using var proc = Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"打开配置失败：{ex.Message}");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            using var proc = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Event("open-folder-failed", ("path", path), ("message", ex.Message));
        }
    }

    public void Dispose()
    {
        _icon.Dispose();
        ((IDisposable)_menu).Dispose();   // DotTray 是显式接口实现
    }


}
