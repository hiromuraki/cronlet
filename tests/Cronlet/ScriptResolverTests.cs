using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class ScriptResolverTests
{
    [Theory]
    [InlineData("eportal-login.ps1", true)]
    [InlineData("JOBS/run.cmd", true)]
    [InlineData(@"C:\tools\task.exe", true)]
    [InlineData("scripts\\a.ps1", true)]
    [InlineData("module.psm1", true)]
    [InlineData("\"C:\\Program Files\\a.ps1\"", true)]
    [InlineData("echo hello", false)]
    [InlineData("pwsh -NoProfile -File x", false)]
    [InlineData("Invoke-WebRequest https://example.com", false)]
    [InlineData("https://example.com", false)]
    [InlineData("Get-ChildItem C:\\", false)]
    public void LooksLikePath_detects_file_references(string script, bool expected)
        => Assert.Equal(expected, ScriptResolver.LooksLikePath(script));

    [Fact]
    public void Resolve_strips_surrounding_quotes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cronlet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "quoted.ps1");
        File.WriteAllText(file, "");
        try
        {
            Assert.Equal(file, ScriptResolver.Resolve("\"" + file + "\""));
            Assert.Equal(file, ScriptResolver.Resolve("'" + file + "'"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_returns_null_for_command_with_url()
        => Assert.Null(ScriptResolver.Resolve("Invoke-WebRequest https://example.com"));
}
