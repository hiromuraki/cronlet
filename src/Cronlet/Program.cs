using DotTray;
using DotTray.Items;

namespace HiroMuraki.Cronlet;

internal static class Program
{
    private const int ExitConfigError = 2;
    private const int ExitStartupError = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        bool headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase))
                        || !Environment.UserInteractive;

        try
        {
            return Run(headless);
        }
        catch (Exception ex)
        {
            Log.Event("fatal", ("message", ex.Message));
            if (!headless) ShowFatal($"启动失败：{ex.Message}");
            return ExitStartupError;
        }
    }

    private static int Run(bool headless)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Event("unhandled-exception", ("message", ex.Message));
        };

        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // 已有实例在跑：不算启动失败，退出码保持 0
            Log.Event("instance-exit", ("reason", "already-running"), ("headless", headless));
            return 0;
        }

        using var instanceHandle = instance;

        RunnerConfig config;
        string pwsh;
        try
        {
            config = ConfigLoader.LoadOrCreate(ConfigLoader.ConfigPath);
            pwsh = ConfigValidator.Validate(config);
        }
        catch (Exception ex)
        {
            Log.Event("config-error", ("message", ex.Message));
            if (!headless) ShowFatal($"配置错误：{ex.Message}");
            return ExitConfigError;
        }

        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(config, pwsh, cts.Token);
        scheduler.Start();
        Log.Event("start", ("headless", headless), ("tasks", config.tasks.Count(x => x.enabled)));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => scheduler.Stop();

        TrayContext? tray = null;
        try
        {
            if (!headless)
            {
                tray = new TrayContext(
                    ConfigLoader.ConfigPath,
                    () =>
                    {
                        cts.Cancel();
                        scheduler.Stop();
                    },
                    async () =>
                    {
                        try
                        {
                            var newConfig = ConfigLoader.LoadOrCreate(ConfigLoader.ConfigPath);
                            // 校验可能包含一次 PowerShell 探测，放到后台线程，别卡住托盘
                            string newPwsh = await Task.Run(
                                () => ConfigValidator.Validate(newConfig, cts.Token), cts.Token);
                            string? error = await scheduler.ReloadAsync(newConfig, newPwsh);
                            if (error is not null) return error;
                            Log.Event("config-reload", ("tasks", newConfig.tasks.Count(x => x.enabled)));
                            return null;
                        }
                        catch (Exception ex)
                        {
                            Log.Event("config-error", ("message", ex.Message));
                            return "配置错误: " + ex.Message;
                        }
                    },
                    () => scheduler.GetUpcoming(5));
            }

            // 主线程只负责等待退出信号；调度与托盘各自跑在后台线程上
            cts.Token.WaitHandle.WaitOne();
            return 0;
        }
        finally
        {
            tray?.Dispose();
            scheduler.Stop();
        }
    }

    /// <summary>交互模式下用一个临时托盘图标把错误气泡出来，几秒后退出。</summary>
    private static void ShowFatal(string message)
    {
        try
        {
            using var done = new ManualResetEventSlim();
            using var menu = new DefaultPopupMenuHandler();
            menu.Items.Add(new MenuItem { Text = "关闭", Clicked = _ => done.Set() });

            IntPtr handle = AppIcon.Extract();
            try
            {
                // 与正式托盘一致：DotTray 不拿令牌，关闭只走 Dispose
                using var icon = NotifyIcon.Run(handle, menu, CancellationToken.None);
                icon.SetToolTip("Cronlet 启动失败");
                icon.ShowBalloon(new BalloonNotification
                {
                    Title = "Cronlet",
                    Message = message,
                    Icon = BalloonNotificationIcon.Error,
                });

                done.Wait(TimeSpan.FromSeconds(6));
            }
            finally
            {
                AppIcon.Release(handle);
            }
        }
        catch
        {
            // 托盘不可用时只留日志
        }
    }
}
