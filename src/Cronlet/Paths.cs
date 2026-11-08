namespace HiroMuraki.Cronlet;

/// <summary>程序用到的目录。命令里的相对路径以 <see cref="Base"/> 为当前目录。</summary>
internal static class Paths
{
    public static string Base => AppContext.BaseDirectory;

    public static string Scripts => Path.Combine(Base, "scripts");

    public static string Logs => Path.Combine(Base, "logs");
}
