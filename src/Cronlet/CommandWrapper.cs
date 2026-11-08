namespace HiroMuraki.Cronlet;

/// <summary>
/// 构造 pwsh -Command 的包装命令：统一控制台编码，并把两种失败都映射成非零退出码
/// —— PowerShell 自身的失败（把非终止错误提升为终止错误后由 catch 捕获）
/// 与原生程序的非零退出码。原生程序的退出码优先保留，便于调用方区分。
/// 命令体两侧用换行与包装语句隔开，避免行尾注释把闭合花括号一起注释掉。
/// </summary>
internal static class CommandWrapper
{
    private const string Prelude =
        "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);" +
        " $ErrorActionPreference='Stop';";

    /// <summary>执行脚本文件：路径中的单引号需要转义。</summary>
    public static string ForScript(string path) =>
        Wrap("& '" + path.Replace("'", "''") + "'");

    /// <summary>执行一段用户命令。</summary>
    public static string ForCommand(string command) => Wrap(command);

    private static string Wrap(string body) =>
        Prelude + "\n" +
        "try {\n" + body + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n" +
        "if ($null -ne $LASTEXITCODE -and 0 -ne $LASTEXITCODE) { exit $LASTEXITCODE }\n" +
        "exit 0";
}
