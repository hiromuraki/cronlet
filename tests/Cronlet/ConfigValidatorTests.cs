using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class ConfigValidatorTests
{
    private static RunnerConfig Wrap(params TaskConfig[] tasks) => new() { tasks = [.. tasks] };

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
    public void Valid_config_passes() => ConfigValidator.Validate(Wrap(Valid()));

    [Fact]
    public void Missing_name_is_rejected()
    {
        var task = Valid();
        task.name = "  ";
        Assert.Contains("name", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var ex = Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(Valid("campus"), Valid("CAMPUS"))));
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public void Both_script_file_and_command_are_rejected()
    {
        var task = Valid();
        task.script_file = "whatever.ps1";
        Assert.Contains("不能同时", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Neither_script_file_nor_command_is_rejected()
    {
        var task = Valid();
        task.command = null;
        Assert.Contains("二选一", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Missing_script_file_is_rejected()
    {
        var task = Valid();
        task.command = null;
        task.script_file = "no-such-script.ps1";
        Assert.Contains("找不到脚本", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Existing_script_file_passes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cronlet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "real.ps1");
        File.WriteAllText(file, "");
        try
        {
            var task = Valid();
            task.command = null;
            task.script_file = file;
            ConfigValidator.Validate(Wrap(task));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Negative_jitter_is_rejected()
    {
        var task = Valid();
        task.jitter = -1;
        Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task)));
    }

    [Fact]
    public void Negative_timeout_is_rejected()
    {
        var task = Valid();
        task.timeout = -1;
        Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task)));
    }

    [Fact]
    public void Timeout_at_limit_passes()
    {
        var task = Valid();
        task.timeout = ConfigValidator.MaxTimeoutSeconds;
        ConfigValidator.Validate(Wrap(task));
    }

    [Fact]
    public void Timeout_above_limit_is_rejected()
    {
        var task = Valid();
        task.timeout = ConfigValidator.MaxTimeoutSeconds + 1;
        Assert.Contains("timeout", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("kill")]
    [InlineData("queue")]
    public void Known_overlap_values_pass(string overlap)
    {
        var task = Valid();
        task.overlap = overlap;
        ConfigValidator.Validate(Wrap(task));
    }

    [Fact]
    public void Unknown_overlap_is_rejected()
    {
        var task = Valid();
        task.overlap = "replace";
        Assert.Contains("overlap", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Invalid_cron_is_rejected()
    {
        var task = Valid();
        task.cron = "every five minutes";
        Assert.Contains("cron", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Jitter_at_min_interval_is_rejected()
    {
        var task = Valid();
        task.jitter = 300; // */5 * * * * 的间隔正好 300s
        Assert.Contains("jitter", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }

    [Fact]
    public void Jitter_below_min_interval_passes()
    {
        var task = Valid();
        task.jitter = 299;
        ConfigValidator.Validate(Wrap(task));
    }

    [Fact]
    public void Missing_cwd_is_rejected()
    {
        var task = Valid();
        task.cwd = Path.Combine(Path.GetTempPath(), "cronlet-no-such-dir-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("工作目录", Assert.Throws<Exception>(() => ConfigValidator.Validate(Wrap(task))).Message);
    }
}
