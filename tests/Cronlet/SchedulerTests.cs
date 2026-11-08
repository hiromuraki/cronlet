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

        var psi = Scheduler.BuildStartInfo(task, out string? cwd);
        Assert.Equal(CommandWrapper.ForCommand("'hello'"), psi.ArgumentList[^1]);
        Assert.Equal(Paths.Base, cwd);
    }

    [Fact]
    public void Command_with_spaces_is_not_split()
    {
        var task = Task();
        task.command = "Write-Output 'a b'";
        Validate(task);

        var psi = Scheduler.BuildStartInfo(task, out _);
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

            var psi = Scheduler.BuildStartInfo(task, out string? cwd);
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
}
