using Cronos;

namespace HiroMuraki.Cronlet;

internal static class ConfigValidator
{
    /// <summary>timeout 上限。计时器能表达的最大延迟约 49.7 天，这里留足余量取 30 天。</summary>
    public const int MaxTimeoutSeconds = 30 * 24 * 60 * 60;

    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Local;

    public static void Validate(RunnerConfig config)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in config.tasks)
        {
            string label = string.IsNullOrWhiteSpace(t.name) ? "(未命名)" : t.name;
            if (string.IsNullOrWhiteSpace(t.name)) throw new Exception("存在没有 name 的任务");
            if (!names.Add(t.name)) throw new Exception($"任务名称重复: {t.name}");

            // 日志文件名会把符号折成下划线，两个不同的任务名可能落到同一个文件
            string logFile = Log.TaskFileName(t.name);
            if (logFiles.TryGetValue(logFile, out string? other))
                throw new Exception($"任务 {label}: 与任务 {other} 映射到同一个日志文件 {logFile}");
            logFiles[logFile] = t.name;

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
    }
}
