namespace HiroMuraki.Cronlet;

/// <summary>
/// script 解析：绝对路径直接用；相对路径先按 exe 目录解析，找不到再去 scripts/ 下找一次。
/// 整体被引号包住的字符串先去掉引号；带空白或 URL 的字符串按命令处理，不当路径。
/// </summary>
internal static class ScriptResolver
{
    public static string? Resolve(string script)
    {
        var raw = Unquote(script);
        if (raw.Length == 0) return null;

        if (Path.IsPathRooted(raw))
        {
            return File.Exists(raw) ? raw : null;
        }

        var direct = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, raw));
        if (File.Exists(direct)) return direct;

        var inScripts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scripts", raw));
        if (File.Exists(inScripts)) return inScripts;

        return null;
    }

    /// <summary>
    /// 看起来像独立的文件路径（而不是一段带参数的命令）时返回 true。
    /// 规则：显式加引号 → 路径；含空白 → 命令；含 "://" → URL；含分隔符或脚本扩展名 → 路径。
    /// </summary>
    public static bool LooksLikePath(string script)
    {
        var s = script.Trim();
        if (s.Length == 0) return false;
        if (IsQuoted(s)) return true;
        if (ContainsWhitespace(s)) return false;
        if (s.Contains("://", StringComparison.Ordinal)) return false;
        if (s.Contains('\\') || s.Contains('/')) return true;
        return HasScriptExtension(s);
    }

    private static string Unquote(string script)
    {
        var s = script.Trim();
        return IsQuoted(s) ? s[1..^1].Trim() : s;
    }

    private static bool IsQuoted(string s) =>
        s.Length > 1 &&
        ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''));

    private static bool ContainsWhitespace(string s)
    {
        foreach (var c in s)
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
