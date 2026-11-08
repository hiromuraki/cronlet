using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class ScriptResolverTests
{
    [Fact]
    public void Resolve_returns_absolute_path_for_existing_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cronlet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "report.ps1");
        File.WriteAllText(file, "");
        try
        {
            Assert.Equal(file, ScriptResolver.Resolve(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_accepts_path_with_spaces()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cronlet tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "report.ps1");
        File.WriteAllText(file, "");
        try
        {
            Assert.Equal(file, ScriptResolver.Resolve(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_returns_null_for_missing_file() =>
        Assert.Null(ScriptResolver.Resolve(Path.Combine(Path.GetTempPath(), "cronlet-no-such-file.ps1")));

    [Fact]
    public void Resolve_returns_null_for_blank() =>
        Assert.Null(ScriptResolver.Resolve("  "));
}
