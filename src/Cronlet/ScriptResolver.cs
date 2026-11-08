namespace HiroMuraki.Cronlet;

/// <summary>
/// script 解析：先当文件找（绝对路径、exe 目录、scripts/ 目录），找不到再当命令执行。
/// 外层成对引号会先去掉，所以含空格的路径不加引号也能解析。
/// </summary>
internal static class ScriptResolver
{
    public static string? Resolve(string script)
    {
        string raw = Unquote(script);
        if (raw.Length == 0) return null;

        if (Path.IsPathRooted(raw))
        {
            return File.Exists(raw) ? raw : null;
        }

        string direct = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, raw));
        if (File.Exists(direct)) return direct;

        string inScripts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scripts", raw));
        if (File.Exists(inScripts)) return inScripts;

        return null;
    }

    /// <summary>
    /// 文件解析失败后，判断这段文本是否"本该是一个文件引用"，用于给出明确的报错。
    /// 含 "://" 视为 URL；有脚本扩展名视为路径；含空白视为带参数的命令；其余看有没有路径分隔符。
    /// 注意：本方法不参与"是否当文件执行"的判断，是否文件由 <see cref="Resolve"/> 的结果决定。
    /// </summary>
    public static bool LooksLikePath(string script)
    {
        string s = Unquote(script);
        if (s.Length == 0) return false;
        if (s.Contains("://", StringComparison.Ordinal)) return false;
        if (HasScriptExtension(s)) return true;
        if (ContainsWhitespace(s)) return false;
        return s.Contains('\\') || s.Contains('/');
    }

    private static string Unquote(string script)
    {
        string s = script.Trim();
        return IsQuoted(s) ? s[1..^1].Trim() : s;
    }

    private static bool IsQuoted(string s) =>
        s.Length > 1 &&
        ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''));

    private static bool ContainsWhitespace(string s)
    {
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) return true;
        }
        return false;
    }

    private static bool HasScriptExtension(string s)
    {
        return s.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
               || s.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase)
               || s.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
               || s.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
               || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
