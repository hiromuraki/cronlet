using System.Text;
using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class LogRotationTests
{
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Keeps_only_complete_records()
        => Assert.Equal("full1\nfull2\n", Text(Log.RotateTail(Bytes("tail-of-big-line\nfull1\nfull2\n"), hasSentinel: true)));

    [Fact]
    public void Drops_trailing_partial_record()
        => Assert.Equal("full1\n", Text(Log.RotateTail(Bytes("cut\nfull1\nhal"), hasSentinel: true)));

    [Fact]
    public void Record_longer_than_window_yields_nothing()
        => Assert.Empty(Log.RotateTail(Bytes(new string('x', 64) + "\n"), hasSentinel: true));

    [Fact]
    public void Window_without_newline_yields_nothing()
        => Assert.Empty(Log.RotateTail(Bytes("no newline at all"), hasSentinel: true));

    [Fact]
    public void Window_starting_on_a_record_boundary_keeps_the_first_record()
        => Assert.Equal("full1\nfull2\n", Text(Log.RotateTail(Bytes("\nfull1\nfull2\n"), hasSentinel: true)));

    [Fact]
    public void Window_from_file_start_keeps_every_complete_record()
        => Assert.Equal("full1\nfull2\n", Text(Log.RotateTail(Bytes("full1\nfull2\n"), hasSentinel: false)));

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
