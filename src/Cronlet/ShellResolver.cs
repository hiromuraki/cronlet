namespace HiroMuraki.Cronlet;

/// <summary>
/// PowerShell 可执行文件解析：含路径分隔符的值按路径检查，裸名字在 PATH 里找。
/// </summary>
internal static class ShellResolver
{
    public const string DefaultName = "pwsh";

    /// <summary>解析成功返回完整路径，失败返回 null。</summary>
    public static string? Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        if (Path.IsPathRooted(configured) || configured.Contains('\\') || configured.Contains('/'))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        string name = configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? configured
            : configured + ".exe";

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH 里可能存在非法片段，跳过继续找
            }
        }

        return null;
    }
}
