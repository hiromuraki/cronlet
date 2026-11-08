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
/// </summary>
internal static class Log
{
    private const long HighWatermark = 16L * 1024 * 1024;
    private const long LowWatermark = HighWatermark / 2;

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
        lock (Gate)
        {
            try
            {
                Append(TaskFileName(task), BuildLine("task-output",
                    new (string, object?)[] { ("task", task), ("stream", stream), ("line", line) }));
            }
            catch (Exception ex)
            {
                DebugWrite(ex);
            }
        }
    }

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

            byte[] tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Max(0, fs.Length - LowWatermark);
                fs.Position = start;
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                tail = ms.ToArray();
            }

            File.WriteAllBytes(path, RotateTail(tail));
        }
        catch (Exception ex)
        {
            DebugWrite(ex);
        }
    }

    /// <summary>
    /// 从低水位窗口里裁出完整的记录：丢掉窗口开头的残行，再丢掉可能不完整的末行。
    /// 窗口里没有任何完整记录时返回空数组，而不是把半行写回文件。
    /// </summary>
    internal static byte[] RotateTail(byte[] tail)
    {
        int first = Array.IndexOf(tail, (byte)'\n');
        if (first < 0) return [];

        byte[] keep = tail[(first + 1)..];
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
