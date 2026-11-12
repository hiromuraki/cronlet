using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class LineAssemblerTests
{
    private static List<string> FeedAll(LineAssembler assembler, params string[] chunks)
    {
        var lines = new List<string>();
        foreach (string chunk in chunks)
        {
            lines.AddRange(assembler.Feed(chunk.AsSpan()));
        }

        return lines;
    }

    [Fact]
    public void Emits_lines_on_newline()
    {
        var assembler = new LineAssembler(1024);

        Assert.Equal(["one", "two"], FeedAll(assembler, "one\ntwo\n"));
        Assert.Null(assembler.Flush());
    }

    [Fact]
    public void Handles_crlf_and_cr()
    {
        var assembler = new LineAssembler(1024);

        Assert.Equal(["one", "two", "three"], FeedAll(assembler, "one\r\ntwo\rthree\n"));
    }

    [Fact]
    public void Joins_chunks_split_mid_line()
    {
        var assembler = new LineAssembler(1024);

        Assert.Equal(["onetwo"], FeedAll(assembler, "one", "tw", "o\n"));
    }

    [Fact]
    public void Splits_crlf_across_chunks()
    {
        var assembler = new LineAssembler(1024);

        Assert.Equal(["one"], FeedAll(assembler, "one\r", "\n"));
    }

    [Fact]
    public void Truncation_happens_once_in_the_log_layer()
    {
        var assembler = new LineAssembler(8);

        string line = Assert.Single(FeedAll(assembler, new string('a', 20) + "\n"));

        // 组装器原样返回有界原文，不加标记；标记只由 Log 加一次
        Assert.Equal(new string('a', 20), line);
        Assert.Equal(new string('a', 8) + Log.TruncatedSuffix, Log.TruncateLine(line, 8));
    }

    [Fact]
    public void Overflow_does_not_grow_memory_but_still_ends_the_line()
    {
        var assembler = new LineAssembler(4, lookaheadChars: 0);
        var lines = new List<string>();

        // 分 64 块喂 64 KiB，确认只保留 4 个字符
        for (int i = 0; i < 64; i++)
        {
            lines.AddRange(assembler.Feed(new string('x', 1024).AsSpan()));
        }

        lines.AddRange(assembler.Feed("\n".AsSpan()));

        Assert.Equal(new string('x', 4), Assert.Single(lines));
    }

    [Fact]
    public void Flush_returns_trailing_text_without_newline()
    {
        var assembler = new LineAssembler(1024);
        Assert.Empty(FeedAll(assembler, "tail"));

        Assert.Equal("tail", assembler.Flush());
        Assert.Null(assembler.Flush());
    }

    [Fact]
    public void Empty_input_yields_nothing()
    {
        var assembler = new LineAssembler(1024);

        Assert.Empty(FeedAll(assembler));
        Assert.Null(assembler.Flush());
    }
    [Fact]
    public void Truncation_drops_a_cluster_that_would_be_split()
    {
        var assembler = new LineAssembler(8192);

        // 8191 个 a 之后是 e + 组合重音：整个簇应被丢弃，而不是留下半个 e
        string line = new string('a', 8191) + "e\u0301z";
        string result = Log.TruncateLine(Assert.Single(FeedAll(assembler, line + "\n")));

        Assert.Equal(new string('a', 8191) + Log.TruncatedSuffix, result);
    }

    [Fact]
    public void Truncation_keeps_full_ascii_prefix()
    {
        var assembler = new LineAssembler(4);
        string line = Assert.Single(FeedAll(assembler, "abcdef\n"));

        Assert.Equal("abcd" + Log.TruncatedSuffix, Log.TruncateLine(line, 4));
    }
    [Fact]
    public void Truncation_drops_a_supplementary_plane_combining_cluster()
    {
        var assembler = new LineAssembler(8192);

        // U+1D165 是补充平面的组合符号（代理对），旧实现只按单个码元分类，会漏掉它
        string line = new string('a', 8191) + "e\U0001D165z";
        string result = Log.TruncateLine(Assert.Single(FeedAll(assembler, line + "\n")));

        Assert.Equal(new string('a', 8191) + Log.TruncatedSuffix, result);
    }

    [Fact]
    public void Truncation_drops_a_zwj_cluster()
    {
        var assembler = new LineAssembler(8192);

        string line = new string('a', 8191) + "e\u200Dz";
        string result = Log.TruncateLine(Assert.Single(FeedAll(assembler, line + "\n")));

        Assert.Equal(new string('a', 8191) + Log.TruncatedSuffix, result);
    }
}