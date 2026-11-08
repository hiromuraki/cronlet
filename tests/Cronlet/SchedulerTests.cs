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
    public void Command_is_passed_to_pwsh_verbatim()
    {
        var task = Task();
        Validate(task);

        var psi = Scheduler.BuildStartInfo(task, "pwsh", out string? cwd);
        Assert.Equal(CommandWrapper.ForCommand("'hello'"), psi.ArgumentList[^1]);
        Assert.Equal(Paths.Base, cwd);
    }

    [Fact]
    public void Command_with_spaces_is_not_split()
    {
        var task = Task();
        task.command = "Write-Output 'a b'";
        Validate(task);

        var psi = Scheduler.BuildStartInfo(task, "pwsh", out _);
        Assert.Equal(CommandWrapper.ForCommand("Write-Output 'a b'"), psi.ArgumentList[^1]);
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

            var psi = Scheduler.BuildStartInfo(task, "pwsh", out string? cwd);
            Assert.Equal(dir, cwd);
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
        var scheduler = new Scheduler(config, "pwsh", cts.Token);
        try
        {
            scheduler.Start();
            Assert.Equal(2, scheduler.QueuedCount);
        }
        finally
        {
            cts.Cancel();
            scheduler.Stop();
        }
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
