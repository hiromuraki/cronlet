using System.Text;
using Tomlyn;

namespace HiroMuraki.Cronlet;

/// <summary>
/// config.toml 模型。属性名刻意用小写，与 TOML key 一一对应。
/// </summary>
internal sealed class RunnerConfig
{
    public List<TaskConfig> tasks { get; set; } = new();
}

internal sealed class TaskConfig
{
    public string name { get; set; } = "";
    public bool enabled { get; set; } = true;
    public string script { get; set; } = "";
    public string cron { get; set; } = "";
    public int jitter { get; set; }
    public int timeout { get; set; }
    public string overlap { get; set; } = "skip";
    public string? cwd { get; set; }
}

internal static class ConfigLoader
{
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.toml");

    public const string DefaultTemplate = """
# ==============================================================
# Cronlet 任务配置
#
# 每个 [[tasks]] 定义一个任务，可以写多组。
#
#   name     任务名（唯一，日志里用它标识）
#   enabled  是否启用
#   script   脚本路径或 pwsh 命令
#            绝对路径直接使用；相对路径先按 exe 目录解析，
#            找不到再到 scripts/ 下找一次
#   cron     5 段 unix cron：分 时 日 月 周（本地时区）
#   jitter   0..jitter 秒随机延迟；必须小于 cron 间隔，否则启动报错
#   timeout  秒；0 表示不限制，超时杀进程树
#   overlap  上次未结束时的策略：skip | kill | queue
#   cwd      工作目录；空则用脚本所在目录
# ==============================================================

[[tasks]]
name = "example"
enabled = false
script = "echo hello"          # 文件路径或直接 pwsh 命令
cron = "*/5 * * * *"           # 分 时 日 月 周（本地时区）
jitter = 10                    # 0..jitter 秒随机延迟，必须小于 cron 间隔
timeout = 60                   # 秒，0 表示不限制
overlap = "skip"               # skip | kill | queue
cwd = ""                       # 工作目录，空则用脚本所在目录
""";

    public static RunnerConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, DefaultTemplate, new UTF8Encoding(false));
        }

        string text = File.ReadAllText(path);
        var config = TomlSerializer.Deserialize<RunnerConfig>(text);
        return config ?? throw new Exception("配置为空");
    }
}
