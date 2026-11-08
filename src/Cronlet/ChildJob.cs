using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 子进程 Job 对象：KILL_ON_JOB_CLOSE。
/// Runner 无论正常退出还是被强杀，OS 都会关闭 Job 句柄，
/// 从而连带结束所有子进程，避免残留 pwsh。
/// 注意：只覆盖加入 Job 之后的进程。命令在 <c>Start()</c> 之后、<c>TryAssign</c> 之前
/// 就派生出的后代可能逃逸，所以这是尽力清理，不是硬保证。
/// </summary>
internal static class ChildJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private static readonly IntPtr Handle;
    private static readonly string? InitError;

    static ChildJob()
    {
        try
        {
            Handle = CreateJobObjectW(IntPtr.Zero, null);
            if (Handle == IntPtr.Zero)
            {
                InitError = $"CreateJobObject failed: {Marshal.GetLastWin32Error()}";
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            if (!SetInformationJobObject(Handle, JobObjectExtendedLimitInformation, ref info,
                    Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                InitError = $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}";
            }
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
        }
    }

    /// <summary>把子进程加入 Job；失败时返回 false 与原因，由调用方决定是否记录。</summary>
    public static bool TryAssign(Process process, out string? error)
    {
        if (Handle == IntPtr.Zero)
        {
            error = InitError ?? "job object unavailable";
            return false;
        }

        try
        {
            if (!AssignProcessToJobObject(Handle, process.Handle))
            {
                error = $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, int cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
