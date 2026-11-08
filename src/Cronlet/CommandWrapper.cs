namespace HiroMuraki.Cronlet;

/// <summary>
/// 构造 pwsh -Command 的包装命令：统一控制台编码，并把两种失败都映射成非零退出码
/// —— PowerShell 自身的失败（把非终止错误提升为终止错误后由 catch 捕获）
/// 与原生程序的非零退出码。原生程序的退出码优先保留，便于调用方区分。
/// 命令体两侧用换行与包装语句隔开，避免行尾注释把闭合花括号一起注释掉。
/// 注意：预置 $ErrorActionPreference = 'Stop' 会让原本"报错但继续"的非终止错误中断任务。
/// </summary>
internal static class CommandWrapper
{
    private const string Prelude =
        "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);" +
        " $ErrorActionPreference='Stop';";

    /// <summary>执行一段用户命令。</summary>
    public static string ForCommand(string command) => Wrap(command);

    private static string Wrap(string body) =>
        Prelude + "\n" +
        "try {\n" + body + "\n} catch {\n" +
        // 任务自己打开 $PSNativeCommandUseErrorActionPreference 时，原生命令的失败会以
        // NativeCommandExitException 抛出；这里保留它携带的退出码，而不是压成 1。
        "    if ($_.Exception.GetType().Name -eq 'NativeCommandExitException') { exit $_.Exception.ExitCode }\n" +
        "    [Console]::Error.WriteLine($_.Exception.Message)\n" +
        "    exit 1\n" +
        "}\n" +
        "if ($null -ne $LASTEXITCODE -and 0 -ne $LASTEXITCODE) { exit $LASTEXITCODE }\n" +
        "exit 0";
}
