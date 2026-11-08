namespace HiroMuraki.Cronlet;

/// <summary>
/// script 解析：绝对路径直接用；相对路径先按 exe 目录解析，找不到再去 scripts/ 下找一次。
/// </summary>
internal static class ScriptResolver
{
    public static string? Resolve(string script)
    {
        if (Path.IsPathRooted(script))
        {
            return File.Exists(script) ? script : null;
        }

        var direct = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, script));
        if (File.Exists(direct)) return direct;

        var inScripts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scripts", script));
        if (File.Exists(inScripts)) return inScripts;

        return null;
    }

    /// <summary>看起来像文件路径（而不是一段命令）时返回 true。</summary>
    public static bool LooksLikePath(string script)
    {
        if (script.Contains('\\') || script.Contains('/')) return true;
        return script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
               || script.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase)
               || script.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
               || script.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
               || script.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
