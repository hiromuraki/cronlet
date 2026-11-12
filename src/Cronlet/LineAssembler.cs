using System.Text;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 把字符流切成行，并让"单行"占用的内存有界。
/// 缓冲只保留到上限加一小段前瞻，多出的字符直接丢弃：下游按字节截断时，截断点
/// 一定落在这段缓冲里，前瞻则让它仍能落在完整字形簇上。
/// 截断和截断标记只有 <see cref="Log.TruncateLine"/> 一个所有者——这里不再截断，
/// 否则同一行会被截两次、再分配一份相同的字符串。
/// 进程的 <c>BeginOutputReadLine</c> 会先缓存完整行，一条几百 MiB 的无换行输出
/// 会把宿主的内存整个顶上去；这里在读取时就设上限。
/// </summary>
internal sealed class LineAssembler
{
    private readonly StringBuilder _line = new();
    private readonly int _capacity;

    /// <param name="maxBytes">下游按 UTF-8 字节截断时使用的上限，和 <see cref="Log.MaxLineBytes"/> 一致。</param>
    /// <param name="lookaheadChars">上限之外额外保留的前瞻长度，让截断点仍能落在完整字形簇上。</param>
    public LineAssembler(int maxBytes, int lookaheadChars = 4096)
    {
        _capacity = maxBytes + lookaheadChars;
    }

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

            if (_line.Length < _capacity) _line.Append(c);
        }

        return lines;
    }

    /// <summary>流结束时取出残留内容；没有残留返回 null。</summary>
    public string? Flush() => _line.Length == 0 ? null : TakeLine();

    private bool _skipNextLf;

    private string TakeLine()
    {
        string text = _line.ToString();
        _line.Clear();
        return text;
    }
}
