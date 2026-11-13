using Xunit;

namespace HiroMuraki.Cronlet.Tests;

/// <summary>
/// 走 Tomlyn 的源生成绑定，验证完整 TOML 语法仍然可用（不再受手写子集限制）。
/// </summary>
public class ConfigParseTests
{
    [Fact]
    public void Multiline_command_is_supported()
    {
        var config = ConfigLoader.Parse(""""
            [[tasks]]
            command = """
            Write-Output one
            Write-Output two
            """
            """");

        var task = Assert.Single(config.tasks);
        Assert.Contains("Write-Output one", task.command);
        Assert.Contains("Write-Output two", task.command);
    }

    [Fact]
    public void Underscored_integer_is_supported()
        => Assert.Equal(1000, Assert.Single(ConfigLoader.Parse("[[tasks]]\njitter = 1_000").tasks).jitter);

    [Fact]
    public void Unicode_escape_is_supported()
        => Assert.Equal("作", Assert.Single(ConfigLoader.Parse("[[tasks]]\ncommand = \"\\u4f5c\"").tasks).command);

    [Fact]
    public void Empty_task_array_is_supported()
        => Assert.Empty(ConfigLoader.Parse("tasks = []").tasks);

    [Fact]
    public void Top_level_upcoming_is_read()
        => Assert.Equal(3, ConfigLoader.Parse("upcoming = 3").upcoming);

    [Fact]
    public void Upcoming_defaults_to_10()
        => Assert.Equal(10, ConfigLoader.Parse("tasks = []").upcoming);

    [Fact]
    public void Notify_lists_default_to_non_zero_exit_codes()
    {
        var config = ConfigLoader.Parse("tasks = []");

        Assert.Empty(config.notify_if);
        Assert.Equal([0], config.notify_if_not);
    }

    [Fact]
    public void Notify_lists_are_read_at_both_levels()
    {
        var config = ConfigLoader.Parse("""
            notify_if = [1, 2]
            notify_if_not = []

            [[tasks]]
            name = "probe"
            notify_if = [7]
            """);

        Assert.Equal([1, 2], config.notify_if);
        Assert.Empty(config.notify_if_not);
        Assert.Equal([7], Assert.Single(config.tasks).notify_if);
        Assert.Null(Assert.Single(config.tasks).notify_if_not);
    }

    [Fact]
    public void Duplicate_key_is_rejected()
        => Assert.ThrowsAny<Exception>(() => ConfigLoader.Parse("[[tasks]]\nenabled = false\nenabled = true"));

    [Fact]
    public void Trailing_garbage_after_string_is_rejected()
        => Assert.ThrowsAny<Exception>(() => ConfigLoader.Parse("[[tasks]]\ncommand = \"a\" junk"));

    [Fact]
    public void Comment_inside_string_is_kept()
    {
        var task = Assert.Single(ConfigLoader.Parse("[[tasks]]\ncommand = \"./run.ps1 # not a comment\"").tasks);
        Assert.Equal("./run.ps1 # not a comment", task.command);
    }

    [Fact]
    public void Defaults_apply_when_fields_are_missing()
    {
        var task = Assert.Single(ConfigLoader.Parse("[[tasks]]\ncommand = \"echo hi\"").tasks);

        Assert.True(task.enabled);
        Assert.Equal("skip", task.overlap);
        Assert.Equal(0, task.jitter);
    }
    [Theory]
    [InlineData("[[tasks]]\njitter = 1__000")]
    [InlineData("[[tasks]]\njitter = _5")]
    [InlineData("[[tasks]]\njitter = 007")]
    [InlineData("tasks = []\n\n[[tasks]]\ncommand = \"echo hi\"")]
    [InlineData("[[tasks]]\ncommand = \"\\uD800\"")]
    public void Illegal_input_is_rejected_by_the_parser(string text)
        => Assert.ThrowsAny<Exception>(() => ConfigLoader.Parse(text));
    [Fact]
    public void Misspelled_table_name_is_rejected()
    {
        var ex = Assert.Throws<Exception>(() => ConfigLoader.Parse("[[task]]\ncommand = \"echo hi\""));
        Assert.Contains("task", ex.Message);
        Assert.Contains("未知", ex.Message);
    }

    [Fact]
    public void Misspelled_task_key_is_rejected()
    {
        var ex = Assert.Throws<Exception>(() =>
            ConfigLoader.Parse("[[tasks]]\nname = \"a\"\nenabeld = false"));
        Assert.Contains("enabeld", ex.Message);
    }

    [Fact]
    public void Misspelled_top_level_key_is_rejected()
    {
        var ex = Assert.Throws<Exception>(() => ConfigLoader.Parse("pws = \"pwsh\""));
        Assert.Contains("pws", ex.Message);
    }

    [Fact]
    public void Explicit_empty_tasks_array_is_still_accepted()
        => Assert.Empty(ConfigLoader.Parse("tasks = []").tasks);
}