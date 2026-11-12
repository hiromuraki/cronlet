using System.Text;
using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class LogRotationTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>把 <see cref="Log.RotateRange"/> 返回的区间切出来，方便断言内容。</summary>
    private static string Tail(byte[] window, bool hasSentinel)
    {
        var (offset, count) = Log.RotateRange(window, hasSentinel);
        return Encoding.UTF8.GetString(window, offset, count);
    }

    [Fact]
    public void Keeps_only_complete_records()
        => Assert.Equal("full1\nfull2\n", Tail(Bytes("tail-of-big-line\nfull1\nfull2\n"), hasSentinel: true));

    [Fact]
    public void Drops_trailing_partial_record()
        => Assert.Equal("full1\n", Tail(Bytes("cut\nfull1\nhal"), hasSentinel: true));

    [Fact]
    public void Record_longer_than_window_yields_nothing()
        => Assert.Empty(Tail(Bytes(new string('x', 64) + "\n"), hasSentinel: true));

    [Fact]
    public void Window_without_newline_yields_nothing()
        => Assert.Empty(Tail(Bytes("no newline at all"), hasSentinel: true));

    [Fact]
    public void Window_starting_on_a_record_boundary_keeps_the_first_record()
        => Assert.Equal("full1\nfull2\n", Tail(Bytes("\nfull1\nfull2\n"), hasSentinel: true));

    [Fact]
    public void Window_from_file_start_keeps_every_complete_record()
        => Assert.Equal("full1\nfull2\n", Tail(Bytes("full1\nfull2\n"), hasSentinel: false));

    [Fact]
    public void Short_line_is_not_touched()
        => Assert.Equal("hi", Log.TruncateLine("hi"));

    [Fact]
    public void Long_line_is_truncated_at_the_byte_limit()
    {
        string text = new string('a', Log.MaxLineBytes + 100);
        string result = Log.TruncateLine(text);

        Assert.Equal(new string('a', Log.MaxLineBytes) + Log.TruncatedSuffix, result);
    }

    [Fact]
    public void Multi_byte_characters_are_cut_on_a_character_boundary()
    {
        string text = new string('作', 4000); // 每个 3 字节，合计 12000
        string result = Log.TruncateLine(text);

        Assert.EndsWith(Log.TruncatedSuffix, result, StringComparison.Ordinal);

        string body = result[..^Log.TruncatedSuffix.Length];
        Assert.True(Encoding.UTF8.GetByteCount(body) <= Log.MaxLineBytes, "截断后不能超过字节上限");
        Assert.DoesNotContain('\uFFFD', body);
        Assert.All(body, c => Assert.Equal('作', c));
    }

    [Fact]
    public void Surrogate_pairs_are_not_split()
    {
        string text = string.Concat(Enumerable.Repeat("😀", 3000)); // 每个 4 字节
        string result = Log.TruncateLine(text);

        string body = result[..^Log.TruncatedSuffix.Length];
        Assert.True(Encoding.UTF8.GetByteCount(body) <= Log.MaxLineBytes);
        Assert.DoesNotContain('\uFFFD', body);
        for (int i = 0; i < body.Length; i += 2)
        {
            Assert.True(char.IsHighSurrogate(body[i]) && char.IsLowSurrogate(body[i + 1]));
        }
    }
}
