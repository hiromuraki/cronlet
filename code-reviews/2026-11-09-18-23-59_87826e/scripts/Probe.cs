using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using HiroMuraki.Cronlet;
if (args.Length > 0)
{
    int target = int.Parse(Environment.GetEnvironmentVariable("CRONLET_AUDIT_PID")!);
    IntPtr owner = Native.OpenProcess(0x40, false, target);
    if (owner == IntPtr.Zero) throw new Exception($"OpenProcess {Marshal.GetLastWin32Error()}");
    try
    {
        IntPtr output = Duplicate(-11);
        IntPtr error = Duplicate(-12);
        Console.WriteLine("ready");
        Console.Error.WriteLine("ready");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "duplicated.txt"), $"{output.ToInt64()},{error.ToInt64()}");
    }
    finally { Native.CloseHandle(owner); }
    return;
    IntPtr Duplicate(int id)
    {
        if (!Native.DuplicateHandle(Native.GetCurrentProcess(), Native.GetStdHandle(id), owner, out IntPtr copy, 0, false, 2))
            throw new Exception($"DuplicateHandle {Marshal.GetLastWin32Error()}");
        return copy;
    }
}
foreach (var (name, tail) in new[] { ("BMP", "e\u0301z"), ("supplementary", "e\U0001D165z"), ("ZWJ", "e\u200Dz") })
{
    string line = new string('a', Log.MaxLineBytes - 1) + tail;
    var assembler = new LineAssembler(Log.MaxLineBytes);
    assembler.Feed(line.AsSpan(0, Log.MaxLineBytes));
    string actual = Log.TruncateLine(assembler.Feed(line.AsSpan(Log.MaxLineBytes).ToString() + "\n").Single());
    string expected = Log.TruncateLine(line);
    Console.WriteLine($"{name}: equal={actual == expected}; actualLength={actual.Length}; expectedLength={expected.Length}");
}
string handles = Path.Combine(AppContext.BaseDirectory, "duplicated.txt");
string log = Path.Combine(AppContext.BaseDirectory, "logs", "runner.jsonl");
File.Delete(handles);
if (File.Exists(log)) File.Delete(log);
Environment.SetEnvironmentVariable("CRONLET_AUDIT_PID", Environment.ProcessId.ToString());
var item = new TaskConfig { name = "pipecheck", command = "unused", timeout = 1 };
var scheduler = new Scheduler(new RunnerConfig { tasks = [item] }, Environment.ProcessPath!, CancellationToken.None);
const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
var gate = typeof(Scheduler).GetField("_gate", privateInstance)!.GetValue(scheduler)!;
var timer = Stopwatch.StartNew();
lock (gate) typeof(Scheduler).GetMethod("FireLocked", privateInstance)!.Invoke(scheduler, [item]);
while (timer.Elapsed < TimeSpan.FromSeconds(12) && !(File.Exists(log) && File.ReadAllText(log).Contains("task-end", StringComparison.Ordinal)))
    await Task.Delay(40);
if (!File.Exists(handles)) throw new Exception("Child failed to duplicate handles");
Console.WriteLine($"elapsedMs={timer.ElapsedMilliseconds}");
Console.WriteLine(File.ReadAllText(log));
foreach (string handle in File.ReadAllText(handles).Split(','))
{
    IntPtr writer = new(long.Parse(handle));
    try
    {
        bool wrote = Native.WriteFile(writer, new byte[] { 88 }, 1, out uint bytes, IntPtr.Zero);
        Console.WriteLine($"writer open={wrote} bytes={bytes} error={Marshal.GetLastWin32Error()}");
    }
    finally { Native.CloseHandle(writer); }
}
scheduler.Stop();

string rotationFile = Path.Combine(AppContext.BaseDirectory, "rotation.jsonl");
byte[] record = new byte[4096];
Array.Fill(record, (byte)'a');
"{\"line\":\""u8.CopyTo(record);
"\"}\n"u8.CopyTo(record.AsSpan(record.Length - 3));
using (var file = File.Create(rotationFile))
{
    for (int i = 0; i < 17 * 256; i++) file.Write(record);
}
var rotate = typeof(Log).GetMethod("RotateIfNeeded", BindingFlags.NonPublic | BindingFlags.Static)!;
object[] rotationArguments = [rotationFile];
long before = GC.GetAllocatedBytesForCurrentThread();
rotate.Invoke(null, rotationArguments);
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Console.WriteLine($"rotationAllocatedBytes={allocated}; rotatedBytes={new FileInfo(rotationFile).Length}");

static class Native
{
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DuplicateHandle(IntPtr from, IntPtr handle, IntPtr to, out IntPtr copy, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteFile(IntPtr handle, byte[] buffer, uint bytes, out uint written, IntPtr overlapped);
}
