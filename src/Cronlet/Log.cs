using System.Globalization;
using System.Text;

namespace HiroMuraki.Cronlet;

/// <summary>
/// JSONL 日志（每行一个 JSON，首字段固定为 RFC3339 带时区的 ts）：
///   logs/runner.jsonl      —— Runner 自身事件
///   logs/task-&lt;name&gt;.jsonl —— 每个任务的 stdout/stderr
/// 每次写入时打开、写完即关闭；按高低水位轮转：超过 16MB 时裁剪到最近 8MB。
/// 单条输出行超过 <see cref="MaxLineBytes"/> 会被截断，避免一条巨行把窗口挤满、
/// 让轮转把整份历史清掉。
/// </summary>
internal static class Log
{
    private const long HighWatermark = 16L * 1024 * 1024;
    private const long LowWatermark = HighWatermark / 2;

    /// <summary>单条输出保留的最大 UTF-8 字节数（不含截断标记）。</summary>
    internal const int MaxLineBytes = 8 * 1024;

    internal const string TruncatedSuffix = "…[truncated]";

    private static readonly object Gate = new();

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

    /// <summary>
    /// 单条输出超过上限时截断，避免一条巨行挤满轮转窗口。上限按 UTF-8 字节计，
    /// 截断点落在完整的字形簇（grapheme cluster）边界上，不会切出半个字符或半个代理对。
    /// </summary>
    internal static string TruncateLine(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= MaxLineBytes) return line;

        // 逐簇扫描到上限即停：不为整行分配边界数组，也不为每个簇生成子字符串
        int cut = 0;
        int used = 0;
        var rest = line.AsSpan();

        while (!rest.IsEmpty)
        {
            int length = StringInfo.GetNextTextElementLength(rest);
            int size = Encoding.UTF8.GetByteCount(rest[..length]);

            if (used + size > MaxLineBytes) break;

            used += size;
            cut += length;
            rest = rest[length..];
        }

        return string.Concat(line.AsSpan(0, cut), TruncatedSuffix);
    }

    /// <summary>任务名已被校验限制为字母、数字、下划线和短横线，可以直接拼进文件名。</summary>
    internal static string TaskFileName(string name) => $"task-{name}.jsonl";

    /// <summary>
    /// 手工拼 JSON：只支持本程序用到的值类型（字符串、布尔、整数、null）。
    /// 这样不依赖 System.Text.Json 的反射序列化，trimming / NativeAOT 下可用。
    /// </summary>
    private static string BuildLine(string eventName, (string Key, object? Value)[] fields)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"ts\":");
        AppendString(sb, Timestamp());
        sb.Append(",\"event\":");
        AppendString(sb, eventName);

        foreach (var (key, value) in fields)
        {
            sb.Append(',');
            AppendString(sb, key);
            sb.Append(':');
            AppendValue(sb, value);
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                AppendString(sb, s);
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                AppendString(sb, value.ToString() ?? "");
                break;
        }
    }

    private static void AppendString(StringBuilder sb, string value)
    {
        sb.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
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

            byte[] keep = RotateTail(window, hasSentinel);

            // 先写临时文件再替换：截断与写回之间被强制结束时，原日志仍然完整
            string temp = path + ".rotating";

            try
            {
                File.WriteAllBytes(temp, keep);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // 写入或替换失败：保留原文件，清理可能残留的临时文件
                try { File.Delete(temp); } catch { }
                DebugWrite(ex);
            }
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
