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
    [InlineData("echo hello", false)]
    [InlineData("pwsh -NoProfile -File x", false)]
    public void LooksLikePath_detects_file_references(string script, bool expected)
        => Assert.Equal(expected, ScriptResolver.LooksLikePath(script));
}
