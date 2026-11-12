using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class ConfigValidatorTests
{
    private static RunnerConfig Wrap(params TaskConfig[] tasks) => new() { tasks = [.. tasks] };

    private static void Validate(RunnerConfig config) =>
        ConfigValidator.Validate(config, TestContext.Current.CancellationToken);

    private static TaskConfig Valid(string name = "job") => new()
    {
        name = name,
        command = "echo hello",
        cron = "*/5 * * * *",
        jitter = 1,
        timeout = 0,
        overlap = "skip",
    };

    [Fact]
    public void Valid_config_passes() => Validate(Wrap(Valid()));

    [Fact]
    public void Upcoming_must_be_positive()
    {
        var config = Wrap(Valid());
        config.upcoming = 0;
        Assert.Contains("upcoming", Assert.Throws<Exception>(() => Validate(config)).Message);
    }

    [Fact]
    public void Missing_name_is_rejected()
    {
        var task = Valid();
        task.name = "  ";
        Assert.Contains("name", Assert.Throws<Exception>(() => Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var ex = Assert.Throws<Exception>(() => Validate(Wrap(Valid("campus"), Valid("CAMPUS"))));
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public void Empty_command_is_rejected()
    {
        var task = Valid();
        task.command = "  ";
        Assert.Contains("command", Assert.Throws<Exception>(() => Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Negative_jitter_is_rejected()
    {
        var task = Valid();
        task.jitter = -1;
        Assert.Throws<Exception>(() => Validate(Wrap(task)));
    }

    [Fact]
    public void Negative_timeout_is_rejected()
    {
        var task = Valid();
        task.timeout = -1;
        Assert.Throws<Exception>(() => Validate(Wrap(task)));
    }

    [Fact]
    public void Timeout_at_limit_passes()
    {
        var task = Valid();
        task.timeout = ConfigValidator.MaxTimeoutSeconds;
        Validate(Wrap(task));
    }

    [Fact]
    public void Timeout_above_limit_is_rejected()
    {
        var task = Valid();
        task.timeout = ConfigValidator.MaxTimeoutSeconds + 1;
        Assert.Contains("timeout", Assert.Throws<Exception>(() => Validate(Wrap(task))).Message);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("kill")]
    [InlineData("queue")]
    public void Known_overlap_values_pass(string overlap)
    {
        var task = Valid();
        task.overlap = overlap;
        Validate(Wrap(task));
    }

    [Fact]
    public void Unknown_overlap_is_rejected()
    {
        var task = Valid();
        task.overlap = "replace";
        Assert.Contains("overlap", Assert.Throws<Exception>(() => Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Invalid_cron_is_rejected()
    {
        var task = Valid();
        task.cron = "every five minutes";
        Assert.Contains("cron", Assert.Throws<Exception>(() => Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Cron_that_never_fires_is_rejected()
    {
        var task = Valid();
        task.cron = "0 0 31 2 *";
        Assert.Contains("永远不会触发", Assert.Throws<Exception>(() => Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Impossible_cron_is_allowed_when_task_is_disabled()
    {
        var task = Valid();
        task.enabled = false;
        task.cron = "0 0 31 2 *";
        Validate(Wrap(task));
    }

    [Theory]
    [InlineData("job")]
    [InlineData("Job-1_2")]
    [InlineData("A")]
    public void Valid_name_characters_pass(string name) => Validate(Wrap(Valid(name)));

    [Theory]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("作业")]
    [InlineData("a.b")]
    public void Invalid_name_characters_are_rejected(string name)
    {
        var ex = Assert.Throws<Exception>(() => Validate(Wrap(Valid(name))));
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Name_of_max_length_passes() =>
        Validate(Wrap(Valid(new string('a', ConfigValidator.MaxNameLength))));

    [Fact]
    public void Name_above_max_length_is_rejected()
    {
        var ex = Assert.Throws<Exception>(() =>
            Validate(Wrap(Valid(new string('a', ConfigValidator.MaxNameLength + 1)))));
        Assert.Contains("128", ex.Message);
    }

    [Fact]
    public void Jitter_is_accepted_without_a_static_interval_check()
    {
        var task = Valid();
        task.jitter = 7200; // 超过间隔时由排程按实际余量截断
        Validate(Wrap(task));
    }

    [Fact]
    public void Missing_cwd_is_rejected()
    {
        var task = Valid();
        task.cwd = Path.Combine(Path.GetTempPath(), "cronlet-no-such-dir-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("工作目录", Assert.Throws<Exception>(() => Validate(Wrap(task))).Message);
    }
}
