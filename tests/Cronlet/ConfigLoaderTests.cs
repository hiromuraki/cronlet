using Tomlyn;
using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Default_template_deserializes()
    {
        var config = TomlSerializer.Deserialize<RunnerConfig>(ConfigLoader.DefaultTemplate);
        Assert.NotNull(config);
        var task = Assert.Single(config!.tasks);
        Assert.Equal("example", task.name);
        Assert.False(task.enabled);
        Assert.Equal("echo hello", task.script);
        Assert.Equal("*/5 * * * *", task.cron);
        Assert.Equal(10, task.jitter);
        Assert.Equal(60, task.timeout);
        Assert.Equal("skip", task.overlap);
    }

    [Fact]
    public void LoadOrCreate_writes_template_when_missing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cronlet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.toml");
        try
        {
            var config = ConfigLoader.LoadOrCreate(path);
            Assert.True(File.Exists(path));
            Assert.Single(config.tasks);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadOrCreate_reads_existing_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cronlet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.toml");
        try
        {
            File.WriteAllText(path, """
                [[tasks]]
                name = "campus"
                script = "echo hi"
                cron = "*/2 * * * *"
                jitter = 10
                timeout = 120
                overlap = "skip"
                """);

            var config = ConfigLoader.LoadOrCreate(path);
            var task = Assert.Single(config.tasks);
            Assert.Equal("campus", task.name);
            Assert.True(task.enabled); // 默认值
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
