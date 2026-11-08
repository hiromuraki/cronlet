using Cronos;

namespace HiroMuraki.Cronlet;

public static class ConfigValidator
{
    /// <summary>timeout 上限。计时器能表达的最大延迟约 49.7 天，这里留足余量取 30 天。</summary>
    public const int MaxTimeoutSeconds = 30 * 24 * 60 * 60;

    private static readonly TimeZoneInfo Tz = TimeZoneInfo.Local;

    public static void Validate(RunnerConfig config)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in config.tasks)
        {
            var label = string.IsNullOrWhiteSpace(t.name) ? "(未命名)" : t.name;
            if (string.IsNullOrWhiteSpace(t.name)) throw new Exception("存在没有 name 的任务");
            if (!names.Add(t.name)) throw new Exception($"任务名称重复: {t.name}");
            if (string.IsNullOrWhiteSpace(t.script)) throw new Exception($"任务 {label}: script 不能为空");
            if (t.jitter < 0) throw new Exception($"任务 {label}: jitter 不能为负");
            if (t.timeout < 0) throw new Exception($"任务 {label}: timeout 不能为负");
            if (t.timeout > MaxTimeoutSeconds)
                throw new Exception($"任务 {label}: timeout 不能超过 {MaxTimeoutSeconds}s（30 天）");
            if (t.overlap is not ("skip" or "kill" or "queue"))
                throw new Exception($"任务 {label}: overlap 只能是 skip / kill / queue");

            if (ScriptResolver.LooksLikePath(t.script) && ScriptResolver.Resolve(t.script) is null)
                throw new Exception($"任务 {label}: 找不到脚本 {t.script}（已查 exe 目录与 scripts/）");

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

            var minInterval = MinIntervalSeconds(cron);
            if (minInterval is not null && t.jitter >= minInterval)
            {
                throw new Exception(
                    $"任务 {label}: jitter={t.jitter}s 必须小于 cron 最小间隔 {minInterval:0}s");
            }
        }
    }

    /// <summary>采样未来若干次 occurrence，取最小间隔（秒）；无法采样时返回 null。</summary>
    private static double? MinIntervalSeconds(CronExpression cron)
    {
        var current = cron.GetNextOccurrence(DateTime.UtcNow, Tz);
        if (current is null) return null;

        double? min = null;
        for (var i = 0; i < 128; i++)
        {
            var next = cron.GetNextOccurrence(current.Value, Tz);
            if (next is null) break;
            var seconds = (next.Value - current.Value).TotalSeconds;
            if (min is null || seconds < min) min = seconds;
            current = next;
        }
        return min;
    }
}
