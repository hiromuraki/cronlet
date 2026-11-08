using System.Text;
using Xunit;

namespace HiroMuraki.Cronlet.Tests;

public class LogRotationTests
{
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Keeps_only_complete_records()
        => Assert.Equal("full1\nfull2\n", Text(Log.RotateTail(Bytes("tail-of-big-line\nfull1\nfull2\n"))));

    [Fact]
    public void Drops_trailing_partial_record()
        => Assert.Equal("full1\n", Text(Log.RotateTail(Bytes("cut\nfull1\nhal"))));

    [Fact]
    public void Record_longer_than_window_yields_nothing()
        => Assert.Empty(Log.RotateTail(Bytes(new string('x', 64) + "\n")));

    [Fact]
    public void Window_without_newline_yields_nothing()
        => Assert.Empty(Log.RotateTail(Bytes("no newline at all")));
}
