using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class TomlConfigTests
{
    [Fact]
    public void Parses_top_level_pwsh_and_a_task()
    {
        var config = TomlConfig.Parse("""
            pwsh = "C:/tools/pwsh.exe"

            [[tasks]]
            name = "a"
            enabled = false
            command = "echo hi"
            cron = "*/5 * * * *"
            jitter = 3
            timeout = 9
            overlap = "queue"
            cwd = "C:/tmp"
            """);

        Assert.Equal("C:/tools/pwsh.exe", config.pwsh);

        var task = Assert.Single(config.tasks);
        Assert.Equal("a", task.name);
        Assert.False(task.enabled);
        Assert.Equal("echo hi", task.command);
        Assert.Equal("*/5 * * * *", task.cron);
        Assert.Equal(3, task.jitter);
        Assert.Equal(9, task.timeout);
        Assert.Equal("queue", task.overlap);
        Assert.Equal("C:/tmp", task.cwd);
    }

    [Fact]
    public void Comments_are_stripped_only_outside_strings()
    {
        var task = Assert.Single(TomlConfig.Parse("""
            [[tasks]]
            name = "a"          # 行尾注释
            command = "./run.ps1 # 不是注释"
            """).tasks);

        Assert.Equal("a", task.name);
        Assert.Equal("./run.ps1 # 不是注释", task.command);
    }

    [Fact]
    public void Single_quotes_are_literal()
    {
        var task = Assert.Single(TomlConfig.Parse("""
            [[tasks]]
            command = 'C:\temp\nope'
            """).tasks);

        Assert.Equal(@"C:\temp\nope", task.command);
    }

    [Fact]
    public void Double_quote_escapes_are_decoded()
    {
        var task = Assert.Single(TomlConfig.Parse("""
            [[tasks]]
            command = "echo \"hi\"\n"
            """).tasks);

        Assert.Equal("echo \"hi\"\n", task.command);
    }

    [Fact]
    public void Defaults_apply_when_fields_are_missing()
    {
        var task = Assert.Single(TomlConfig.Parse("""
            [[tasks]]
            command = "echo hi"
            """).tasks);

        Assert.True(task.enabled);
        Assert.Equal("skip", task.overlap);
        Assert.Equal(0, task.jitter);
        Assert.Null(task.cwd);
    }

    [Theory]
    [InlineData("[[other]]", "只支持")]
    [InlineData("[[tasks]]\nnope = 1", "未知字段")]
    [InlineData("[[tasks]]\njitter = \"x\"", "整数")]
    [InlineData("[[tasks]]\nenabled = maybe", "true/false")]
    [InlineData("[[tasks]]\ncommand = unquoted", "引号")]
    [InlineData("top = 1", "顶层未知字段")]
    public void Invalid_input_throws(string text, string expected)
    {
        var ex = Assert.Throws<Exception>(() => TomlConfig.Parse(text));
        Assert.Contains(expected, ex.Message);
    }
}
