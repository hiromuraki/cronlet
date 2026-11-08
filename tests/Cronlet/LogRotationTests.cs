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
    public void Long_line_is_truncated_at_the_limit()
    {
        string text = new string('a', Log.MaxLineLength + 100);
        string result = Log.TruncateLine(text);

        Assert.StartsWith(new string('a', Log.MaxLineLength), result, StringComparison.Ordinal);
        Assert.EndsWith("truncated]", result, StringComparison.Ordinal);
    }
}
