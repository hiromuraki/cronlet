namespace HiroMuraki.Cronlet;

/// <summary>
/// 跨会话单实例：文件独占锁（Local\ 命名互斥量按会话隔离，
/// 会漏掉 Session 0 的后台实例，所以不能用）。
/// </summary>
internal static class SingleInstance
{
    public static string LockPath => Path.Combine(AppContext.BaseDirectory, "cronlet.lock");

    public static FileStream? TryAcquire()
    {
        try
        {
            return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
