using System.Diagnostics;
using System.Text;

namespace HiroMuraki.Cronlet;

/// <summary>
/// PowerShell 可执行文件解析：含路径分隔符的值按路径检查，裸名字在 PATH 里找。
/// 解析只确认"文件存在"，能不能跑由 <see cref="Probe"/> 用一次真实执行确认——
/// 空文件、错误架构的 exe 都在这里暴露，而不是等到第一个任务派发。
/// 探测只在配置被接受时跑一次，不做进程期缓存：同一个路径被替换后要能重新判断。
/// </summary>
internal static class ShellResolver
{
    public const string DefaultName = "pwsh";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private const int MaxVersionChars = 256;
    private const int MaxErrorChars = 2048;

    /// <summary>解析成功返回完整路径，失败返回 null。</summary>
    public static string? Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        if (Path.IsPathRooted(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        // 相对路径与 command 一样以 exe 目录为基准
        if (configured.Contains('\\') || configured.Contains('/'))
        {
            string relative = Path.GetFullPath(Path.Combine(Paths.Base, configured));
            return File.Exists(relative) ? relative : null;
        }

        string name = configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? configured
            : configured + ".exe";

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH 里可能存在非法片段，跳过继续找
            }
        }

        return null;
    }

    /// <summary>
    /// 用一次带超时的真实执行确认这个可执行文件可用。
    /// 进程退出与两路管道排空共用同一个截止时间；读取会一直读到流结束，
    /// 这样写满管道的子进程不会把自己堵死。
    /// </summary>
    public static (bool Ok, string? Error) Probe(string path, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return (false, "已取消");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("$PSVersionTable.PSVersion.Major");

            using var proc = Process.Start(psi);
            if (proc is null) return (false, "process did not start");

            // 探测进程也挂进 Job：宿主被强杀时不会留下一个还在跑的 pwsh
            ChildJob.TryAssign(proc, out _);

            // 一个截止时间同时约束进程退出与两路读取：取消或超时后读取任务自己结束，
            // 不会留下仍在等外部写端的读任务
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ProbeTimeout);

            try
            {
                var stdout = DrainAsync(proc.StandardOutput, MaxVersionChars, deadline.Token);
                var stderr = DrainAsync(proc.StandardError, MaxErrorChars, deadline.Token);
                var all = Task.WhenAll(proc.WaitForExitAsync(deadline.Token), stdout, stderr);

                try
                {
                    all.Wait(ProbeTimeout + TimeSpan.FromSeconds(2), deadline.Token);
                }
                catch
                {
                    // 取消或超时：下面统一清理
                }

                // 不能只看聚合任务是否"成功"：子进程先退出、写端还在时，读任务被取消
                // 也会让 WhenAll 变成 canceled；反过来，读任务若把取消吞成正常返回，
                // WhenAll 又会显示成功。两个信号一起看才可靠。
                if (deadline.IsCancellationRequested || !all.IsCompletedSuccessfully)
                {
                    try { Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2)); } catch { }

                    return (false, ct.IsCancellationRequested
                        ? "已取消"
                        : $"未在 {ProbeTimeout.TotalSeconds:0}s 内完成");
                }

                if (proc.ExitCode != 0)
                {
                    string message = stderr.GetAwaiter().GetResult().Trim();
                    return (false, $"exit={proc.ExitCode}{(message.Length > 0 ? ": " + message : "")}");
                }

                string? version = FirstLine(stdout.GetAwaiter().GetResult());
                if (version is null) return (false, "没有输出版本号");
                if (!int.TryParse(version, out _)) return (false, $"输出不是版本号: {version}");

                return (true, null);
            }
            finally
            {
                // 所有退出路径都终结进程树并显式关掉读端，
                // 释放与否不再取决于聚合任务的状态
                try { proc.Kill(true); } catch { }
                try { proc.StandardOutput.Dispose(); } catch { }
                try { proc.StandardError.Dispose(); } catch { }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        string line = (newline >= 0 ? text[..newline] : text).Trim();
        return line.Length == 0 ? null : line;
    }

    /// <summary>
    /// 持续排空一路管道，只保留前 <paramref name="keep"/> 个字符。
    /// 读到底而不是"读完首行就撒手"，否则剩余输出会填满管道把子进程卡住。
    /// </summary>
    private static async Task<string> DrainAsync(StreamReader reader, int keep, CancellationToken ct)
    {
        char[] buffer = new char[4096];
        var kept = new StringBuilder(keep);

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                int room = keep - kept.Length;
                if (room > 0) kept.Append(buffer, 0, Math.Min(room, read));
            }
        }
        catch (IOException)
        {
            // 读取端被关掉（进程结束或被显式释放），这一路读完了
        }
        catch (ObjectDisposedException)
        {
        }
        // OperationCanceledException 不吞：取消必须体现为任务状态，
        // 否则调用方会把"被取消的读取"当成正常结束

        return kept.ToString();
    }
}
