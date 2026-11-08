using Cronos;

namespace HiroMuraki.Cronlet;

internal static class ConfigValidator
{
    /// <summary>timeout 上限。计时器能表达的最大延迟约 49.7 天，这里留足余量取 30 天。</summary>
    public const int MaxTimeoutSeconds = 30 * 24 * 60 * 60;

    /// <summary>任务名同时是日志文件名的组成部分，因此限制长度。</summary>
    public const int MaxNameLength = 128;

    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Local;

    /// <summary>
    /// 校验配置并返回解析后的 PowerShell 路径。有启用任务时找不到 PowerShell
    /// 就算配置错误——否则启动会"成功"，但每个任务都跑不起来。
    /// </summary>
    public static string Validate(RunnerConfig config)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in config.tasks)
        {
            string label = string.IsNullOrWhiteSpace(t.name) ? "(未命名)" : t.name;
            if (string.IsNullOrWhiteSpace(t.name)) throw new Exception("存在没有 name 的任务");
            if (!IsValidName(t.name))
                throw new Exception(
                    $"任务 {label}: name 只能包含 A-Z、a-z、0-9、下划线和短横线，长度 1-{MaxNameLength}");
            if (!names.Add(t.name)) throw new Exception($"任务名称重复: {t.name}");

            if (string.IsNullOrWhiteSpace(t.command)) throw new Exception($"任务 {label}: command 不能为空");
            if (t.jitter < 0) throw new Exception($"任务 {label}: jitter 不能为负");
            if (t.timeout < 0) throw new Exception($"任务 {label}: timeout 不能为负");
            if (t.timeout > MaxTimeoutSeconds)
                throw new Exception($"任务 {label}: timeout 不能超过 {MaxTimeoutSeconds}s（30 天）");
            if (t.overlap is not ("skip" or "kill" or "queue"))
                throw new Exception($"任务 {label}: overlap 只能是 skip / kill / queue");

            if (!string.IsNullOrWhiteSpace(t.cwd) && !Directory.Exists(t.cwd))
                throw new Exception($"任务 {label}: 工作目录不存在: {t.cwd}");

            CronExpression cron;
            try
            {
                cron = CronExpression.Parse(t.cron);
            }
            catch (Exception ex)
            {
                throw new Exception($"任务 {label}: cron 非法 ({t.cron}): {ex.Message}");
            }

            // 语法合法但永远不触发的表达式（例如 2 月 31 日）在这里就拦掉
            if (t.enabled && cron.GetNextOccurrence(DateTime.UtcNow, Tz) is null)
                throw new Exception($"任务 {label}: cron 永远不会触发 ({t.cron})");
        }

        return ResolveShell(config);
    }

    private static string ResolveShell(RunnerConfig config)
    {
        string shell = string.IsNullOrWhiteSpace(config.pwsh) ? ShellResolver.DefaultName : config.pwsh;

        if (!config.tasks.Any(t => t.enabled)) return shell;

        string? resolved = ShellResolver.Resolve(shell);
        if (resolved is null)
            throw new Exception($"找不到 PowerShell: {shell}（可用 pwsh 配置项指定完整路径）");

        var probe = ShellResolver.Probe(resolved);
        if (!probe.Ok)
            throw new Exception($"PowerShell 无法运行: {resolved}（{probe.Error}）");

        return resolved;
    }

    /// <summary>任务名直接拼进日志文件名，所以只允许不会引入路径语义的字符。</summary>
    private static bool IsValidName(string name)
    {
        if (name.Length > MaxNameLength) return false;

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) return false;
        }

        return true;
    }
}
