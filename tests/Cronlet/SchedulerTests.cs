using Xunit;

namespace HiroMuraki.Cronlet.Tests;

/// <summary>
/// 覆盖"配置校验 → 执行入口"这条完整路径，确保 script 的解析结果与校验一致。
/// </summary>
public class SchedulerTests
{
    private static TaskConfig Task(string script) => new()
    {
        name = "probe",
        script = script,
        cron = "*/5 * * * *",
        jitter = 1,
        timeout = 0,
        overlap = "skip",
    };

    private static void Validate(TaskConfig task) =>
        ConfigValidator.Validate(new RunnerConfig { tasks = [task] });

    [Fact]
    public void Quoted_expression_is_validated_as_command()
    {
        var task = Task("'hello'");
        Validate(task);

        var psi = Scheduler.BuildStartInfo(task, out string cwd);
        Assert.Equal(CommandWrapper.ForCommand("'hello'"), psi.ArgumentList[^1]);
        Assert.Equal(AppContext.BaseDirectory, cwd);
    }

    [Fact]
    public void Path_with_spaces_is_validated_as_script()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cronlet tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "report.ps1");
        File.WriteAllText(file, "'hi'\n");
        try
        {
            var task = Task(file);
            Validate(task);

            var psi = Scheduler.BuildStartInfo(task, out string cwd);
            Assert.Equal(CommandWrapper.ForScript(file), psi.ArgumentList[^1]);
            Assert.Equal(dir, cwd);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Url_command_is_validated_as_command()
    {
        var task = Task("Invoke-WebRequest https://example.com");
        Validate(task);

        var psi = Scheduler.BuildStartInfo(task, out _);
        Assert.Equal(CommandWrapper.ForCommand("Invoke-WebRequest https://example.com"), psi.ArgumentList[^1]);
    }
}
