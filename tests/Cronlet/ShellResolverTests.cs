using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class ShellResolverTests
{
    [Fact]
    public void Default_name_is_found_on_path() =>
        Assert.NotNull(ShellResolver.Resolve(ShellResolver.DefaultName));

    [Fact]
    public void Missing_absolute_path_returns_null() =>
        Assert.Null(ShellResolver.Resolve(Path.Combine(Path.GetTempPath(), "cronlet-no-pwsh", "pwsh.exe")));

    [Fact]
    public void Blank_value_returns_null() => Assert.Null(ShellResolver.Resolve("  "));

    [Fact]
    public void Existing_absolute_path_is_resolved()
    {
        string file = Path.Combine(Path.GetTempPath(), "cronlet-tests", Guid.NewGuid().ToString("N"), "pwsh.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        try
        {
            Assert.Equal(Path.GetFullPath(file), ShellResolver.Resolve(file));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
