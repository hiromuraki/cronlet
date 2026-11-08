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
            if (!headless)
            {
                MessageBox.Show($"Cronlet 启动失败：\n\n{ex}", "Cronlet",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return ExitStartupError;
        }
    }

    private static int Run(bool headless)
    {
        ApplicationConfiguration.Initialize();

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
            if (!headless)
            {
                MessageBox.Show($"配置错误：\n\n{ex.Message}\n\n配置文件：{ConfigLoader.ConfigPath}",
                    "Cronlet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return ExitConfigError;
        }

        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(config, pwsh, cts.Token);
        scheduler.Start();
        Log.Event("start", ("headless", headless), ("tasks", config.tasks.Count(x => x.enabled)));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => scheduler.Stop();

        if (headless)
        {
            // 后台模式：无托盘，只跑调度；进程退出时由 ProcessExit / Job 对象收尾
            Thread.Sleep(Timeout.Infinite);
        }

        using var context = new TrayContext(ConfigLoader.ConfigPath,
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
                    string newPwsh = await Task.Run(() => ConfigValidator.Validate(newConfig));
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
            });

        Application.Run(context);
        return 0;
    }
}
