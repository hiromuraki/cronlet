using System.Diagnostics;
using Xunit;

namespace HiroMuraki.Cronlet.Tests;

/// <summary>
/// 集成测试：真的起一个 pwsh 跑包装命令，确认退出码映射符合预期。
/// </summary>
public class CommandWrapperTests
{
    [Fact]
    public void Successful_command_exits_zero()
        => Assert.Equal(0, Run(CommandWrapper.ForCommand("'hi'")));

    [Fact]
    public void PowerShell_failure_exits_one()
        => Assert.Equal(1, Run(CommandWrapper.ForCommand("Get-Item 'C:\\cronlet-no-such-file'")));

    [Fact]
    public void Native_exit_code_is_preserved()
        => Assert.Equal(7, Run(CommandWrapper.ForCommand("cmd /c exit 7")));

    [Fact]
    public void Native_exit_code_survives_native_error_preference()
        => Assert.Equal(7, Run(CommandWrapper.ForCommand(
            "$PSNativeCommandUseErrorActionPreference = $true; cmd /c exit 7")));

    [Fact]
    public void Command_ending_with_line_comment_still_runs()
        => Assert.Equal(0, Run(CommandWrapper.ForCommand("Write-Output ready # note")));

    [Fact]
    public void Failing_script_file_exits_nonzero()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cronlet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "fail.ps1");
        File.WriteAllText(file, "Get-Item 'C:\\cronlet-no-such-file'\n");
        try
        {
            Assert.NotEqual(0, Run(CommandWrapper.ForScript(file)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int Run(string command)
    {
        var psi = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        Task.WaitAll(stdout, stderr);
        return proc.ExitCode;
    }
}
