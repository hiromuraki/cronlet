namespace HiroMuraki.Cronlet;

/// <summary>
/// 跨会话单实例：文件独占锁（Local\ 命名互斥量按会话隔离，
/// 会漏掉 Session 0 的后台实例，所以不能用）。
/// </summary>
internal static class SingleInstance
{
    public static string LockPath => Path.Combine(AppContext.BaseDirectory, "cronlet.lock");

    /// <summary>
    /// 拿到锁返回句柄；锁确实被另一个实例占用时返回 null。
    /// 其他 I/O 或权限问题向上抛，由调用方区分"已有实例"和"启动失败"。
    /// </summary>
    public static FileStream? TryAcquire()
    {
        try
        {
            return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return null;
        }
        catch (UnauthorizedAccessException ex) when (IsSharingViolation(ex))
        {
            return null;
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION(32) / ERROR_LOCK_VIOLATION(33)。</summary>
    private static bool IsSharingViolation(Exception ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
