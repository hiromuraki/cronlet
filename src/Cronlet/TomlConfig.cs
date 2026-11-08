using System.Globalization;
using System.Text;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 极简 TOML 子集解析器：只支持本程序配置用到的语法
/// —— 行注释、<c>[[tasks]]</c>、<c>key = value</c>，值可以是双引号字符串（含转义）、
/// 单引号字面字符串、整数、true/false。
/// 其余语法一律报错而不是猜——反射式 TOML 库在 trimming / NativeAOT 下不可用。
/// </summary>
internal static class TomlConfig
{
    public static RunnerConfig Parse(string text)
    {
        var config = new RunnerConfig();
        TaskConfig? current = null;
        int lineNo = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            lineNo++;
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                if (!string.Equals(line, "[[tasks]]", StringComparison.Ordinal))
                    throw new Exception($"第 {lineNo} 行: 只支持 [[tasks]]，遇到 {line}");

                current = new TaskConfig();
                config.tasks.Add(current);
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0) throw new Exception($"第 {lineNo} 行: 缺少 '='");

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();

            if (key.Length == 0) throw new Exception($"第 {lineNo} 行: 缺少键名");

            if (current is null)
            {
                if (key == "pwsh") config.pwsh = ReadString(key, value);
                else throw new Exception($"第 {lineNo} 行: 顶层未知字段 {key}");
            }
            else
            {
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
        if (text.Length < 2) throw new Exception($"{key} 的值必须用引号包起来");

        char quote = text[0];
        if (quote is not ('"' or '\'')) throw new Exception($"{key} 的值必须用引号包起来");
        if (text[^1] != quote) throw new Exception($"{key} 的引号没有闭合");

        string inner = text[1..^1];
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

            sb.Append(text[i] switch
            {
                '"' => '"',
                '\\' => '\\',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => throw new Exception($"{key} 不支持的转义 \\{text[i]}"),
            });
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
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
            throw new Exception($"{key} 必须是整数");

        return value;
    }
}
