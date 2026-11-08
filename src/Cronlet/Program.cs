using System.Threading;
using System.Windows.Forms;

namespace HiroMuraki.Cronlet;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Log.Event("fatal", ("message", ex.Message));
            MessageBox.Show($"Cronlet 启动失败：\n\n{ex}", "Cronlet",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void Run(string[] args)
    {
        ApplicationConfiguration.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Event("unhandled-exception", ("message", ex.Message));
        };

        var headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase))
                       || !Environment.UserInteractive;

        FileStream? instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            Log.Event("instance-exit", ("reason", "already-running"), ("headless", headless));
            return;
        }

        using var instanceHandle = instance;

        RunnerConfig config;
        try
        {
            config = ConfigLoader.LoadOrCreate(ConfigLoader.ConfigPath);
            ConfigValidator.Validate(config);
        }
        catch (Exception ex)
        {
            Log.Event("config-error", ("message", ex.Message));
            if (!headless)
            {
                MessageBox.Show($"配置错误：\n\n{ex.Message}\n\n配置文件：{ConfigLoader.ConfigPath}",
                    "Cronlet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(config, cts.Token);
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
                    ConfigValidator.Validate(newConfig);
                    var error = await scheduler.ReloadAsync(newConfig);
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
    }
}
