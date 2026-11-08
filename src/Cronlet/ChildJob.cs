using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 一次任务运行对应的 Job 对象：KILL_ON_JOB_CLOSE + 可显式 Terminate。
/// 每次运行新建一个，超时、<c>overlap = "kill"</c> 和停止路径都用它终止整组进程；
/// 运行结束时 Dispose，句柄关闭会连带结束仍留在 Job 里的后代。
/// 注意：只覆盖加入 Job 之后的进程，命令在 Start 之后、TryAssign 之前派生出的后代仍可能逃逸。
/// </summary>
internal sealed class ChildJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly object _gate = new();
    private IntPtr _handle;

    private ChildJob(IntPtr handle) => _handle = handle;

    /// <summary>创建 Job；失败返回 null 并给出原因（调用方负责记录）。</summary>
    public static ChildJob? Create(out string? error)
    {
        IntPtr handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            error = $"CreateJobObject failed: {Marshal.GetLastWin32Error()}";
            return null;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            error = $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}";
            CloseHandle(handle);
            return null;
        }

        error = null;
        return new ChildJob(handle);
    }

    /// <summary>把进程加入 Job；失败返回 false 并给出原因。</summary>
    public bool TryAssign(Process process, out string? error)
    {
        if (_handle == IntPtr.Zero)
        {
            error = "job object unavailable";
            return false;
        }

        try
        {
            if (!AssignProcessToJobObject(_handle, process.Handle))
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

    /// <summary>终止 Job 里的所有进程（包括已经脱离父进程的后代）。</summary>
    /// <summary>终止 Job 里的所有进程。与 Dispose 共用一把锁，避免句柄被关闭后继续使用。</summary>
    public void Terminate()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;

            try { TerminateJobObject(_handle, 1); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) return;

            CloseHandle(_handle);
            _handle = IntPtr.Zero;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
