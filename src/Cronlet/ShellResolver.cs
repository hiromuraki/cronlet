using System.Collections.Concurrent;
using System.Diagnostics;

namespace HiroMuraki.Cronlet;

/// <summary>
/// PowerShell 可执行文件解析：含路径分隔符的值按路径检查，裸名字在 PATH 里找。
/// 解析只确认"文件存在"，能不能跑由 <see cref="Probe"/> 用一次真实执行确认——
/// 空文件、错误架构的 exe 都在这里暴露，而不是等到第一个任务派发。
/// </summary>
internal static class ShellResolver
{
    public const string DefaultName = "pwsh";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, (bool Ok, string? Error)> Probes =
        new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>用一次带超时的真实执行确认这个可执行文件可用；结果按路径缓存。</summary>
    public static (bool Ok, string? Error) Probe(string path) =>
        Probes.GetOrAdd(path, static p => RunProbe(p));

    private static (bool Ok, string? Error) RunProbe(string path)
    {
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

            var output = proc.StandardOutput.ReadToEndAsync();
            var error = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(ProbeTimeout))
            {
                try { proc.Kill(true); } catch { }
                return (false, $"未在 {ProbeTimeout.TotalSeconds:0}s 内退出");
            }

            if (proc.ExitCode != 0)
            {
                string message = error.GetAwaiter().GetResult().Trim();
                return (false, $"exit={proc.ExitCode}{(message.Length > 0 ? ": " + message : "")}");
            }

            string version = output.GetAwaiter().GetResult().Trim();
            if (!int.TryParse(version, out int major))
                return (false, $"输出不是版本号: {version}");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
