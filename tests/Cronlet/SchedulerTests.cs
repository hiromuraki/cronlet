using System.Reflection;
using Xunit;

namespace HiroMuraki.Cronlet.Tests;

/// <summary>
/// 覆盖"配置校验 → 执行入口"这条完整路径。
/// </summary>
public class SchedulerTests
{
    private static TaskConfig Task() => new()
    {
        name = "probe",
        command = "'hello'",
        cron = "*/5 * * * *",
        jitter = 1,
        timeout = 0,
        overlap = "skip",
    };

    private static void Validate(TaskConfig task) =>
        ConfigValidator.Validate(new RunnerConfig { tasks = [task] });

    [Fact]
    public void Command_is_handed_to_cmd_verbatim()
    {
        var task = Task();
        Validate(task);

        var psi = Scheduler.BuildStartInfo(task);
        Assert.Equal("/s /c \"" + task.command + "\"", psi.Arguments);
        Assert.Equal(Paths.Base, psi.WorkingDirectory);
    }

    [Fact]
    public void Command_with_quotes_keeps_them()
    {
        var task = Task();
        task.command = "pwsh -File \"C:/tools/a b/x.ps1\"";
        Validate(task);

        var psi = Scheduler.BuildStartInfo(task);
        Assert.Equal("/s /c \"pwsh -File \"C:/tools/a b/x.ps1\"\"", psi.Arguments);
    }

    [Fact]
    public void Custom_cwd_is_used()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cronlet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var task = Task();
            task.cwd = dir;
            Validate(task);

            var psi = Scheduler.BuildStartInfo(task);
            Assert.Equal(dir, psi.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(7200, 3600, 3599)]
    [InlineData(10, 120, 10)]
    [InlineData(300, 120, 119)]
    [InlineData(5, 1, 0)]
    public void ClampJitter_keeps_trigger_before_the_next_occurrence(
        int configured, int intervalSeconds, int expected)
    {
        var nominal = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var next = nominal.AddSeconds(intervalSeconds);

        Assert.Equal(expected, Scheduler.ClampJitter(configured, nominal, next));
    }

    [Fact]
    public void ClampJitter_keeps_config_when_there_is_no_next_occurrence() =>
        Assert.Equal(7200, Scheduler.ClampJitter(7200, DateTime.UtcNow, null));

    [Fact]
    public void PickJitter_is_zero_without_room() => Assert.Equal(0, Scheduler.PickJitter(0));

    [Fact]
    public void PickJitter_uses_the_random_source()
    {
        Assert.Equal(0, Scheduler.PickJitter(10, new FixedRandom(0)));
        Assert.Equal(5, Scheduler.PickJitter(10, new FixedRandom(5)));
        Assert.Equal(10, Scheduler.PickJitter(10, new FixedRandom(10)));
    }

    [Fact]
    public void PickJitter_varies_across_calls()
    {
        int distinct = Enumerable.Range(0, 50).Select(_ => Scheduler.PickJitter(10)).Distinct().Count();
        Assert.True(distinct > 1, "抖动应当随机，而不是每次都取上限");
    }

    [Fact]
    public void Start_enqueues_each_enabled_task_once_before_returning()
    {
        var config = new RunnerConfig
        {
            tasks =
            [
                FarTask("a"),
                FarTask("b"),
                FarTask("c", enabled: false),
            ],
        };

        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(config, cts.Token);
        try
        {
            scheduler.Start();

            // 直接看私有队列：这个断言只测试需要，不该给生产代码加观察接口
            object queue = typeof(Scheduler).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(scheduler)!;
            int count = (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
            Assert.Equal(2, count);
        }
        finally
        {
            cts.Cancel();
            scheduler.Stop();
        }
    }

    [Fact]
    public void GetUpcoming_respects_the_configured_count()
    {
        var config = new RunnerConfig
        {
            upcoming = 2,
            tasks = [FarTask("a"), FarTask("b"), FarTask("c")],
        };

        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(config, cts.Token);
        try
        {
            scheduler.Start();

            Assert.Equal(2, scheduler.GetUpcoming().Count);
        }
        finally
        {
            cts.Cancel();
            scheduler.Stop();
        }
    }

    [Fact]
    public void RunNow_rejects_unknown_task()
    {
        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(new RunnerConfig { tasks = [FarTask("probe")] }, cts.Token);

        var result = scheduler.RunNow("missing");

        Assert.False(result.Started);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public void RunNow_rejects_disabled_task()
    {
        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(
            new RunnerConfig { tasks = [FarTask("probe", enabled: false)] }, cts.Token);

        var result = scheduler.RunNow("probe");

        Assert.False(result.Started);
        Assert.Contains("probe", result.Message);
    }

    [Fact]
    public void RunNow_starts_the_task()
    {
        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(new RunnerConfig { tasks = [FarTask("probe")] }, cts.Token);
        try
        {
            var result = scheduler.RunNow("probe");

            Assert.True(result.Started, result.Message);
            Assert.Contains("probe", result.Message);
        }
        finally
        {
            cts.Cancel();
            scheduler.Stop();
        }
    }

    [Fact]
    public void ShouldNotify_defaults_to_non_zero_exit_codes()
    {
        var config = new RunnerConfig();
        var task = FarTask("probe");

        Assert.False(Scheduler.ShouldNotify(task, config, null, 0));
        Assert.True(Scheduler.ShouldNotify(task, config, null, 1));
        Assert.True(Scheduler.ShouldNotify(task, config, null, -1));
    }

    [Fact]
    public void ShouldNotify_skips_a_repeated_exit_code()
    {
        var config = new RunnerConfig();
        var task = FarTask("probe");

        Assert.False(Scheduler.ShouldNotify(task, config, 1, 1));
        Assert.True(Scheduler.ShouldNotify(task, config, 0, 1));
        Assert.True(Scheduler.ShouldNotify(task, config, 2, 1));
    }

    [Fact]
    public void ShouldNotify_treats_notify_if_as_a_whitelist()
    {
        var config = new RunnerConfig { notify_if = [1, 2] };
        var task = FarTask("probe");

        Assert.True(Scheduler.ShouldNotify(task, config, null, 1));
        Assert.False(Scheduler.ShouldNotify(task, config, null, 3));
        Assert.False(Scheduler.ShouldNotify(task, config, null, 0));
    }

    [Fact]
    public void ShouldNotify_lets_notify_if_not_win()
    {
        var config = new RunnerConfig { notify_if = [1], notify_if_not = [1] };

        Assert.False(Scheduler.ShouldNotify(FarTask("probe"), config, null, 1));
    }

    [Fact]
    public void Task_level_lists_override_the_top_level_ones()
    {
        var config = new RunnerConfig { notify_if_not = [0] };
        var task = FarTask("probe");
        task.notify_if_not = [];

        Assert.True(Scheduler.ShouldNotify(task, config, null, 0));
    }

    [Fact]
    public void Task_level_notify_if_narrows_the_result()
    {
        var config = new RunnerConfig();
        var task = FarTask("probe");
        task.notify_if = [5];

        Assert.True(Scheduler.ShouldNotify(task, config, null, 5));
        Assert.False(Scheduler.ShouldNotify(task, config, null, 1));
    }

    [Fact]
    public void A_failing_run_raises_a_notification()
    {
        var task = FarTask("probe");
        task.command = "exit 3";
        var config = new RunnerConfig { tasks = [task] };

        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(config, cts.Token);
        using var fired = new SemaphoreSlim(0);
        Scheduler.Notification? seen = null;
        scheduler.NotificationSink = n => { seen = n; fired.Release(); };

        try
        {
            Assert.True(scheduler.RunNow("probe").Started);
            Assert.True(
                fired.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken),
                "任务以非 0 退出，应当弹一次通知");
        }
        finally
        {
            cts.Cancel();
            scheduler.Stop();
        }

        Assert.Equal(3, seen!.Value.Exit);
        Assert.False(seen.Value.TimedOut);
    }

    [Fact]
    public void RunNow_reports_a_failed_start()
    {
        using var cts = new CancellationTokenSource();

        // 工作目录不存在时 Process.Start 会抛，配置校验在这里被绕过
        var task = FarTask("probe");
        task.cwd = Path.Combine(Path.GetTempPath(), "cronlet-missing-" + Guid.NewGuid().ToString("N"));
        var scheduler = new Scheduler(new RunnerConfig { tasks = [task] }, cts.Token);
        try
        {
            var result = scheduler.RunNow("probe");

            Assert.False(result.Started, result.Message);
            Assert.Contains("失败", result.Message);
        }
        finally
        {
            cts.Cancel();
            scheduler.Stop();
        }
    }

    [Fact]
    public void RunNow_rejects_after_stop()
    {
        using var cts = new CancellationTokenSource();
        var scheduler = new Scheduler(new RunnerConfig { tasks = [FarTask("probe")] }, cts.Token);
        scheduler.Stop();

        var result = scheduler.RunNow("probe");

        Assert.False(result.Started);
        Assert.Contains("退出", result.Message);
    }

    private static TaskConfig FarTask(string name, bool enabled = true) => new()
    {
        name = name,
        enabled = enabled,
        command = "echo " + name,
        cron = "0 0 1 1 *",
        overlap = "skip",
    };

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => value;
    }
}
