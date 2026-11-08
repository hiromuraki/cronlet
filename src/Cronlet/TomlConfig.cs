using System.Globalization;
using System.Text;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 极简 TOML 子集解析器。支持的语法只有本程序配置用到的这些：
/// 行注释、<c>[[tasks]]</c>、<c>key = value</c>；值是双引号字符串（含 \" \\ \n \r \t \uXXXX）、
/// 单引号字面字符串、整数（允许下划线分隔）、true/false；顶层额外支持 <c>tasks = []</c>。
/// 其余语法一律报错而不是猜——反射式 TOML 库在 trimming / NativeAOT 下不可用。
/// 兼容范围写在这里，也就是唯一的行为契约。
/// </summary>
internal static class TomlConfig
{
    public static RunnerConfig Parse(string text)
    {
        var config = new RunnerConfig();
        TaskConfig? current = null;
        var topKeys = new HashSet<string>(StringComparer.Ordinal);
        var taskKeys = new HashSet<string>(StringComparer.Ordinal);
        int lineNo = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            lineNo++;
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("\"\"\"", StringComparison.Ordinal) ||
                line.StartsWith("'''", StringComparison.Ordinal))
            {
                throw new Exception($"第 {lineNo} 行: 不支持多行字符串（三引号），请写成单行");
            }

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (!string.Equals(line, "[[tasks]]", StringComparison.Ordinal))
                    throw new Exception($"第 {lineNo} 行: 只支持 [[tasks]]，遇到 {line}");

                current = new TaskConfig();
                config.tasks.Add(current);
                taskKeys.Clear();
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0) throw new Exception($"第 {lineNo} 行: 缺少 '='");

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();

            if (key.Length == 0) throw new Exception($"第 {lineNo} 行: 缺少键名");

            if (current is null)
            {
                if (!topKeys.Add(key))
                    throw new Exception($"第 {lineNo} 行: 顶层键 {key} 重复");

                switch (key)
                {
                    case "pwsh":
                        config.pwsh = ReadString(key, value);
                        break;
                    case "tasks":
                        if (!string.Equals(value, "[]", StringComparison.Ordinal))
                            throw new Exception($"第 {lineNo} 行: tasks 只支持空数组 []");
                        break;
                    default:
                        throw new Exception($"第 {lineNo} 行: 顶层未知字段 {key}");
                }
            }
            else
            {
                if (!taskKeys.Add(key))
                    throw new Exception($"第 {lineNo} 行: 任务键 {key} 重复");

                ApplyTask(current, key, value, lineNo);
            }
        }

        return config;
    }

    private static void ApplyTask(TaskConfig task, string key, string value, int lineNo)
    {
        switch (key)
        {
            case "name": task.name = ReadString(key, value); break;
            case "enabled": task.enabled = ReadBool(key, value); break;
            case "command": task.command = ReadString(key, value); break;
            case "cron": task.cron = ReadString(key, value); break;
            case "jitter": task.jitter = ReadInt(key, value); break;
            case "timeout": task.timeout = ReadInt(key, value); break;
            case "overlap": task.overlap = ReadString(key, value); break;
            case "cwd": task.cwd = ReadString(key, value); break;
            default: throw new Exception($"第 {lineNo} 行: 任务里出现未知字段 {key}");
        }
    }

    /// <summary>去掉引号外的 # 注释；引号内的 # 是内容。</summary>
    internal static string StripComment(string line)
    {
        char quote = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (quote == '"')
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') quote = '\0';
                continue;
            }

            if (quote == '\'')
            {
                if (c == '\'') quote = '\0';
                continue;
            }

            if (c is '"' or '\'') quote = c;
            else if (c == '#') return line[..i];
        }

        return line;
    }

    internal static string ReadString(string key, string text)
    {
        if (text.StartsWith("\"\"\"", StringComparison.Ordinal) ||
            text.StartsWith("'''", StringComparison.Ordinal))
        {
            throw new Exception($"{key}: 不支持多行字符串（三引号），请写成单行");
        }

        if (text.Length < 2) throw new Exception($"{key} 的值必须用引号包起来");

        char quote = text[0];
        if (quote is not ('"' or '\'')) throw new Exception($"{key} 的值必须用引号包起来");

        int end = -1;
        for (int i = 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\') { i++; continue; }
            if (text[i] == quote) { end = i; break; }
        }

        if (end < 0) throw new Exception($"{key} 的引号没有闭合");
        if (end != text.Length - 1)
            throw new Exception($"{key} 的字符串后面有多余内容: {text[(end + 1)..]}");

        string inner = text[1..end];
        return quote == '\'' ? inner : Unescape(key, inner);
    }

    private static string Unescape(string key, string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal)) return text;

        var sb = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            i++;
            if (i >= text.Length) throw new Exception($"{key} 的转义不完整");

            switch (text[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                {
                    if (i + 4 >= text.Length) throw new Exception($"{key} 的 \\u 转义不完整");
                    string hex = text.Substring(i + 1, 4);
                    if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                        throw new Exception($"{key} 的 \\u 转义非法: \\u{hex}");
                    sb.Append((char)code);
                    i += 4;
                    break;
                }
                default:
                    throw new Exception($"{key} 不支持的转义 \\{text[i]}");
            }
        }

        return sb.ToString();
    }

    private static bool ReadBool(string key, string text) => text switch
    {
        "true" => true,
        "false" => false,
        _ => throw new Exception($"{key} 必须是 true/false"),
    };

    private static int ReadInt(string key, string text)
    {
        string cleaned = text.Replace("_", "", StringComparison.Ordinal);
        if (!int.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
            throw new Exception($"{key} 必须是整数");

        return value;
    }
}
