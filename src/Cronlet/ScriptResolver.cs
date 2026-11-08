namespace HiroMuraki.Cronlet;

/// <summary>
/// script_file 解析：绝对路径直接用；相对路径先按 exe 目录解析，找不到再去 scripts/ 下找一次。
/// </summary>
internal static class ScriptResolver
{
    /// <summary>脚本目录（与 exe 同级的 scripts/）。</summary>
    internal static string ScriptsDirectory => Path.Combine(AppContext.BaseDirectory, "scripts");

    public static string? Resolve(string scriptFile)
    {
        if (string.IsNullOrWhiteSpace(scriptFile)) return null;

        if (Path.IsPathRooted(scriptFile))
        {
            return File.Exists(scriptFile) ? scriptFile : null;
        }

        string direct = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, scriptFile));
        if (File.Exists(direct)) return direct;

        string inScripts = Path.GetFullPath(Path.Combine(ScriptsDirectory, scriptFile));
        if (File.Exists(inScripts)) return inScripts;

        return null;
    }
}
