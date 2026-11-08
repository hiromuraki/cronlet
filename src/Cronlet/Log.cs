using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HiroMuraki.Cronlet;

/// <summary>
/// JSONL 日志（每行一个 JSON，首字段固定为 RFC3339 带时区的 ts）：
///   logs/runner.jsonl      —— Runner 自身事件
///   logs/task-&lt;name&gt;.jsonl —— 每个任务的 stdout/stderr
/// 每次写入时打开、写完即关闭；按高低水位轮转：超过 16MB 时裁剪到最近 8MB。
/// 单条输出行超过 <see cref="MaxLineLength"/> 会被截断，避免一条巨行把窗口挤满、
/// 让轮转把整份历史清掉。
/// </summary>
internal static class Log
{
    private const long HighWatermark = 16L * 1024 * 1024;
    private const long LowWatermark = HighWatermark / 2;

    /// <summary>单条输出保留的最大字符数。</summary>
    internal const int MaxLineLength = 8 * 1024;

    private const string TruncatedSuffix = "…[truncated]";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Event(string eventName, params (string Key, object? Value)[] fields)
    {
        lock (Gate)
        {
            try
            {
                Append("runner.jsonl", BuildLine(eventName, fields));
            }
            catch (Exception ex)
            {
                DebugWrite(ex);
            }
        }
    }

    public static void TaskOutput(string task, string stream, string line)
    {
        string text = TruncateLine(line);

        lock (Gate)
        {
            try
            {
                Append(TaskFileName(task), BuildLine("task-output",
                    new (string, object?)[] { ("task", task), ("stream", stream), ("line", text) }));
            }
            catch (Exception ex)
            {
                DebugWrite(ex);
            }
        }
    }

    /// <summary>单条输出超过上限时截断，避免一条巨行挤满轮转窗口。</summary>
    internal static string TruncateLine(string line) =>
        line.Length <= MaxLineLength ? line : string.Concat(line.AsSpan(0, MaxLineLength), TruncatedSuffix);

    /// <summary>任务名已被校验限制为字母、数字、下划线和短横线，可以直接拼进文件名。</summary>
    internal static string TaskFileName(string name) => $"task-{name}.jsonl";

    private static string BuildLine(string eventName, (string Key, object? Value)[] fields)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"ts\":").Append(JsonSerializer.Serialize(Timestamp(), JsonOpts));
        sb.Append(",\"event\":").Append(JsonSerializer.Serialize(eventName, JsonOpts));
        foreach (var (key, value) in fields)
        {
            sb.Append(',').Append(JsonSerializer.Serialize(key, JsonOpts)).Append(':');
            sb.Append(value is null ? "null" : JsonSerializer.Serialize(value, value.GetType(), JsonOpts));
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string Timestamp() =>
        DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private static void Append(string file, string line)
    {
        string dir = Paths.Logs;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, file);
        RotateIfNeeded(path);
        File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
    }

    /// <summary>超过高水位时，把文件裁剪到最近的低水位长度（只保留完整记录，避免切坏 JSON）。</summary>
    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= HighWatermark) return;

            byte[] window;
            bool hasSentinel;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long windowStart = Math.Max(0, fs.Length - LowWatermark);
                hasSentinel = windowStart > 0;

                // 多读一个字节作为哨兵，用来判断窗口是否正好落在行首
                fs.Position = hasSentinel ? windowStart - 1 : 0;
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                window = ms.ToArray();
            }

            File.WriteAllBytes(path, RotateTail(window, hasSentinel));
        }
        catch (Exception ex)
        {
            DebugWrite(ex);
        }
    }

    /// <summary>
    /// 从低水位窗口里裁出完整记录。<paramref name="hasSentinel"/> 为 true 时 window 的
    /// 首字节是"窗口前一个字节"：它是换行说明窗口正好从行首开始，此时第一行本身是完整
    /// 记录，不能丢弃；否则窗口从记录中间开始，要跳过那段残行。末尾同样只保留完整行。
    /// </summary>
    internal static byte[] RotateTail(byte[] window, bool hasSentinel)
    {
        int offset = 0;

        if (hasSentinel)
        {
            if (window.Length == 0) return [];

            if (window[0] == (byte)'\n')
            {
                offset = 1; // 哨兵是换行：窗口首字节即新记录的开始
            }
            else
            {
                int first = Array.IndexOf(window, (byte)'\n', 1);
                if (first < 0) return [];
                offset = first + 1;
            }
        }

        byte[] keep = window[offset..];
        int last = Array.LastIndexOf(keep, (byte)'\n');
        if (last < 0) return [];

        return last + 1 < keep.Length ? keep[..(last + 1)] : keep;
    }

    private static void DebugWrite(Exception ex)
    {
        try
        {
            string dir = Paths.Logs;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log-error.txt"),
                $"{DateTimeOffset.Now:O} {ex}\n");
        }
        catch
        {
            // 忽略
        }
    }
}
