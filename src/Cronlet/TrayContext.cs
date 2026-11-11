using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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
    private const int UpcomingCount = 5;

    private readonly DefaultPopupMenuHandler _menu = new();
    private readonly NotifyIcon<DefaultPopupMenuHandler> _icon;
    private readonly string _configPath;
    private readonly Func<IReadOnlyList<Scheduler.NextRun>> _upcoming;
    private readonly List<ItemBase> _upcomingItems = [];

    public TrayContext(
        string configPath,
        Action onExit,
        Func<Task<string?>> reload,
        Func<IReadOnlyList<Scheduler.NextRun>> upcoming)
    {
        _configPath = configPath;
        _upcoming = upcoming;

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

        _icon.SetToolTip($"Cronlet {VersionText()}");
        _icon.Interacted += OnInteracted;
    }

    private void OnInteracted(NotifyIconInteractedEventArgs e)
    {
        // DotTray 对左键单击发 Select、右键发 ContextMenu、键盘发 KeySelect，三者都会弹菜单
        if (e.Type is IconInteractionType.ContextMenu or IconInteractionType.Select or IconInteractionType.KeySelect)
        {
            RefreshUpcoming();
        }
    }

    /// <summary>
    /// 重建菜单顶部的待触发列表（队列里各任务的下一次）。DotTray 先触发 Interacted、
    /// 再让 handler 构建菜单，所以这里改完 Items，本次弹出的菜单就是新内容。
    /// </summary>
    private void RefreshUpcoming()
    {
        foreach (var item in _upcomingItems)
        {
            _menu.Items.Remove(item);
        }

        _upcomingItems.Clear();

        var runs = _upcoming().Take(UpcomingCount).ToList();
        Log.Event("tray-menu", ("upcoming", runs.Count));
        if (runs.Count == 0)
        {
            return;
        }

        int index = 0;
        foreach (var run in runs)
        {
            var item = new MenuItem
            {
                Text = FormatUpcoming(run),
                IsDisabled = true,
            };
            _upcomingItems.Add(item);
            _menu.Items.Insert(index++, item);
        }

        _upcomingItems.Add(SeparatorItem.Instance);
        _menu.Items.Insert(index, SeparatorItem.Instance);
    }

    private static string FormatUpcoming(Scheduler.NextRun run)
    {
        var local = run.When.ToLocalTime();
        string when = local.Date == DateTime.Today
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return $"{when}  {run.Task}";
    }

    /// <summary>程序集版本：优先取 InformationalVersion，去掉构建工具追加的 +commit 后缀。</summary>
    private static string VersionText()
    {
        var asm = typeof(TrayContext).Assembly;
        string? informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
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
