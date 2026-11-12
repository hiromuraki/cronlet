using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HiroMuraki.Cronlet;

const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

// ---------- 1. 截断：行组装与整行截断是否一致 ----------
Console.WriteLine("== truncation ==");
(string Name, string Tail)[] clusters =
[
    ("BMP", "e\u0301z"),
    ("sup", "e\U0001D165z"),
    ("ZWJ", "e\u200Dz"),
    ("family", "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466z"),
    ("flag", "\U0001F1E8\U0001F1F3z"),
    ("prepend", "\u0600z"),
    ("hangul", "\u1100\u1161\u11A8z"),
    ("cjk", "作z"),
    ("accent", "éz"),
];

foreach (var (name, tail) in clusters)
{
    string full = new string('a', Log.MaxLineBytes - 1) + tail;
    string expected = Log.TruncateLine(full);
    foreach (int chunk in new[] { 1, 7, 4096 })
    {
        var assembler = new LineAssembler(Log.MaxLineBytes);
        var lines = new List<string>();
        for (int i = 0; i < full.Length; i += chunk)
        {
            lines.AddRange(assembler.Feed(full.AsSpan(i, Math.Min(chunk, full.Length - i))));
        }
        lines.AddRange(assembler.Feed("\n".AsSpan()));
        string actual = lines.Single();
        Console.WriteLine($"{name,-8} chunk={chunk,-4} equal={actual == expected} actualBytes={Encoding.UTF8.GetByteCount(actual),5} expectedBytes={Encoding.UTF8.GetByteCount(expected),5} replacement={actual.Contains('\uFFFD')}");
    }

    string first = StringInfo.GetNextTextElementLength(full.AsSpan(Log.MaxLineBytes - 1)).ToString();
    Console.WriteLine($"{name,-8} firstCluster={first} clusterBytes={Encoding.UTF8.GetByteCount(tail)}");
}

// 单簇本身超过上限：整行只剩标记？
{
    string huge = "a" + new string('\u0301', 5000);
    string cut = Log.TruncateLine(huge);
    Console.WriteLine($"singleHugeCluster inputBytes={Encoding.UTF8.GetByteCount(huge)} outputChars={cut.Length} outputIsOnlyMarker={cut == Log.TruncatedSuffix}");
}

// ---------- 2. 排程输出会不会被第二次截断 ----------
Console.WriteLine("== double truncation ==");
{
    var assembler = new LineAssembler(Log.MaxLineBytes);
    string one = assembler.Feed((new string('a', 20000) + "\n").AsSpan()).Single();
    int firstBytes = Encoding.UTF8.GetByteCount(one);
    long before = GC.GetAllocatedBytesForCurrentThread();
    string second = Log.TruncateLine(one);
    long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine($"assemblerOutBytes={firstBytes} maxBytes={Log.MaxLineBytes} over={firstBytes > Log.MaxLineBytes} secondEqual={second == one} secondAlloc={alloc}");
}

// ---------- 3. 轮转：分配量与结果 ----------
Console.WriteLine("== rotation ==");
{
    string dir = Path.Combine(AppContext.BaseDirectory, "rotation");
    Directory.CreateDirectory(dir);
    string file = Path.Combine(dir, "runner.jsonl");
    byte[] record = new byte[4096];
    Array.Fill(record, (byte)'a');
    "{\"line\":\""u8.CopyTo(record);
    "\"}\n"u8.CopyTo(record.AsSpan(record.Length - 3));
    using (var stream = File.Create(file))
    {
        for (int i = 0; i < 17 * 256; i++) stream.Write(record);
    }

    long sizeBefore = new FileInfo(file).Length;
    long before = GC.GetAllocatedBytesForCurrentThread();
    var rotate = typeof(Log).GetMethod("RotateIfNeeded", PrivateStatic)!;
    rotate.Invoke(null, [file]);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    long sizeAfter = new FileInfo(file).Length;
    string[] lines = File.ReadAllLines(file);
    bool allJson = lines.All(l => l.StartsWith("{\"line\":\"", StringComparison.Ordinal) && l.EndsWith("\"}", StringComparison.Ordinal));
    Console.WriteLine($"before={sizeBefore} after={sizeAfter} allocated={allocated} lines={lines.Length} allJson={allJson} tempLeft={File.Exists(file + ".rotating")}");
}

// ---------- 4. GetUpcoming 成本与正确性 ----------
Console.WriteLine("== upcoming ==");
foreach (int n in new[] { 100, 10000 })
{
    var config = new RunnerConfig { upcoming = 10 };
    for (int i = 0; i < n; i++) config.tasks.Add(new TaskConfig { name = "t" + i, command = "echo x", cron = "0 0 1 1 *" });
    using var cts = new CancellationTokenSource();
    var scheduler = new Scheduler(config, "pwsh", cts.Token);
    scheduler.Start();

    var upcoming = scheduler.GetUpcoming();
    long before = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    for (int i = 0; i < 20; i++) upcoming = scheduler.GetUpcoming();
    double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / 20;
    long alloc = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
    Console.WriteLine($"tasks={n} count={upcoming.Count} ordered={upcoming.Zip(upcoming.Skip(1)).All(p => p.First.When <= p.Second.When)} perCallMs={ms:F3} perCallAlloc={alloc}");

    cts.Cancel();
    scheduler.Stop();
}

// ---------- 5. RunNow 端到端 ----------
Console.WriteLine("== run-now ==");
{
    var task = new TaskConfig
    {
        name = "rn",
        command = "Start-Sleep -Seconds 2; Write-Output done",
        cron = "0 0 1 1 *",
        timeout = 30,
        overlap = "queue",
    };
    var config = new RunnerConfig { tasks = [task] };
    using var cts = new CancellationTokenSource();
    var scheduler = new Scheduler(config, ShellResolver.Resolve("pwsh") ?? "pwsh", cts.Token);
    string log = Path.Combine(Paths.Logs, "runner.jsonl");
    if (File.Exists(log)) File.Delete(log);
    try
    {
        var first = scheduler.RunNow("rn");
        var second = scheduler.RunNow("rn");
        Console.WriteLine($"first started={first.Started} msg={first.Message}");
        Console.WriteLine($"second started={second.Started} msg={second.Message}");

        var wait = Stopwatch.StartNew();
        int ends = 0;
        while (wait.Elapsed < TimeSpan.FromSeconds(25))
        {
            ends = File.Exists(log) ? File.ReadAllText(log).Split('\n').Count(l => l.Contains("task-end", StringComparison.Ordinal)) : 0;
            if (ends >= 2) break;
            Thread.Sleep(50);
        }
        Console.WriteLine($"taskEnds={ends} elapsedMs={wait.ElapsedMilliseconds}");
    }
    finally
    {
        cts.Cancel();
        scheduler.Stop();
    }
}

// ---------- 6. RunNow 在 pwsh 不存在时的返回值 ----------
Console.WriteLine("== run-now start failure ==");
{
    var task = new TaskConfig
    {
        name = "bad",
        command = "'hi'",
        cron = "0 0 1 1 *",
        timeout = 5,
    };
    using var cts = new CancellationTokenSource();
    var scheduler = new Scheduler(new RunnerConfig { tasks = [task] },
        Path.Combine(Path.GetTempPath(), "cronlet-no-such-pwsh-" + Guid.NewGuid().ToString("N") + ".exe"), cts.Token);
    string runner = Path.Combine(Paths.Logs, "runner.jsonl");
    if (File.Exists(runner)) File.Delete(runner);
    try
    {
        var result = scheduler.RunNow("bad");
        Console.WriteLine($"started={result.Started} msg={result.Message}");
        Thread.Sleep(1000);
        Console.WriteLine(File.Exists(runner) ? File.ReadAllText(runner).Trim() : "no log");
    }
    finally
    {
        cts.Cancel();
        scheduler.Stop();
    }
}

// ---------- 6. 日志吞吐 ----------
Console.WriteLine("== log throughput ==");
{
    string log = Path.Combine(Paths.Logs, "probe-throughput.jsonl");
    if (File.Exists(log)) File.Delete(log);
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 2000; i++) Log.Event("probe", ("i", i), ("text", new string('x', 200)));
    sw.Stop();
    Console.WriteLine($"events=2000 elapsedMs={sw.ElapsedMilliseconds} perEventUs={sw.Elapsed.TotalMilliseconds * 1000 / 2000:F0}");
}