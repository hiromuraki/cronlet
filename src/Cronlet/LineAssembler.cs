using System.Globalization;
using System.Text;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 把字符流切成行，并让"单行"占用的内存有界：超过上限后继续丢弃到换行，
/// 只保留前缀加截断标记。
/// 进程的 <c>BeginOutputReadLine</c> 会先缓存完整行，一条几百 MiB 的无换行输出
/// 会把宿主的内存整个顶上去；这里在读取时就设上限。
/// </summary>
internal sealed class LineAssembler
{
    private readonly StringBuilder _line = new();
    private readonly bool _truncate;

    public LineAssembler(int maxChars, bool truncate = true)
    {
        MaxChars = maxChars;
        _truncate = truncate;
    }

    public int MaxChars { get; }

    /// <summary>喂入一块字符，返回其中已成形的行（可能 0 到多条）。</summary>
    public List<string> Feed(ReadOnlySpan<char> chunk)
    {
        var lines = new List<string>();

        foreach (char c in chunk)
        {
            if (c == '\r')
            {
                // CR 单独也是行结束；紧随其后的 LF 不再另起一行
                lines.Add(TakeLine());
                _skipNextLf = true;
                continue;
            }

            if (c == '\n')
            {
                if (_skipNextLf)
                {
                    _skipNextLf = false;
                    continue;
                }

                lines.Add(TakeLine());
                continue;
            }

            _skipNextLf = false;

            if (!_truncated && _line.Length < MaxChars)
            {
                _line.Append(c);
                continue;
            }

            if (_truncate && !_truncated)
            {
                // 截断点若落在簇中间（当前字符是组合标记），把整个簇丢掉，
                // 否则会留下半个字形：e 而没有重音
                if (IsCombining(c) && _line.Length > 0)
                {
                    int i = _line.Length - 1;
                    while (i >= 0 && IsCombining(_line[i])) i--;
                    if (i >= 0) i--;
                    _line.Length = Math.Max(i + 1, 0);
                }

                _truncated = true;
            }
        }

        return lines;
    }

    /// <summary>流结束时取出残留内容；没有残留返回 null。</summary>
    public string? Flush() => _line.Length == 0 && !_truncated ? null : TakeLine();

    private static bool IsCombining(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;

    private bool _truncated;
    private bool _skipNextLf;

    private string TakeLine()
    {
        string text = _truncated
            ? string.Concat(_line.ToString(), Log.TruncatedSuffix)
            : _line.ToString();

        _line.Clear();
        _truncated = false;
        return text;
    }
}
