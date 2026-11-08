using System.Text;
using Tomlyn;

namespace HiroMuraki.Cronlet;

/// <summary>
/// config.toml 模型。属性名刻意用小写，与 TOML key 一一对应。
/// </summary>
internal sealed class RunnerConfig
{
    /// <summary>PowerShell 可执行文件；裸名字在 PATH 里找，也可以写完整路径。</summary>
    public string pwsh { get; set; } = ShellResolver.DefaultName;

    public List<TaskConfig> tasks { get; set; } = new();
}

internal sealed class TaskConfig
{
    public string name { get; set; } = "";
    public bool enabled { get; set; } = true;

    /// <summary>要执行的 pwsh 命令。相对路径以 <see cref="Paths.Base"/> 为当前目录。</summary>
    public string command { get; set; } = "";

    public string cron { get; set; } = "";
    public int jitter { get; set; }
    public int timeout { get; set; }
    public string overlap { get; set; } = "skip";
    public string? cwd { get; set; }
}

internal static class ConfigLoader
{
    public static string ConfigPath => Path.Combine(Paths.Base, "config.toml");

    public const string DefaultTemplate = """
# ==============================================================
# Cronlet 任务配置
#
# 每个 [[tasks]] 定义一个任务，可以写多组。
#
#   name        任务名（唯一，日志里用它标识）
#               只允许 A-Z a-z 0-9 _ -，长度 1-128
#   enabled     是否启用
#   command     pwsh 命令；相对路径以 exe 目录为当前目录
#               例：./scripts/login.ps1   或   echo hello
#   cron        5 段 unix cron：分 时 日 月 周（本地时区）
#   jitter      0..jitter 秒随机延迟；会被限制在下一次触发之前
#   timeout     秒；0 表示不限制，超时杀进程树
#   overlap     上次未结束时的策略：skip | kill | queue
#   cwd         工作目录；空则用 exe 目录
# ==============================================================

# 可选：指定 PowerShell 可执行文件（默认在 PATH 里找 pwsh）
# pwsh = "S:/tools/pwsh/pwsh.exe"

[[tasks]]
name = "example"
enabled = false
command = "./scripts/example.ps1"   # 也可以直接写一条命令，例如 "echo hello"
cron = "*/5 * * * *"                # 分 时 日 月 周（本地时区）
jitter = 10                         # 0..jitter 秒随机延迟，不会推过下一次触发
timeout = 60                        # 秒，0 表示不限制
overlap = "skip"                    # skip | kill | queue
cwd = ""                            # 工作目录，空则用 exe 目录
""";

    /// <summary>配置文件不存在时写一份模板；托盘"打开配置文件"也复用它。</summary>
    public static void EnsureExists(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, DefaultTemplate, new UTF8Encoding(false));
        }
    }

    public static RunnerConfig LoadOrCreate(string path)
    {
        EnsureExists(path);

        string text = File.ReadAllText(path);
        var config = TomlSerializer.Deserialize<RunnerConfig>(text);
        return config ?? throw new Exception("配置为空");
    }
}
