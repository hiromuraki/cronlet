using System.Diagnostics;
using System.Text;
using Cronos;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 调度器：每个启用的任务算出下一次 cron 时间 + jitter 后入优先队列；
/// 循环睡到最近到期时间，执行到期任务，再排下一次。
/// 支持运行期重载：暂停派发 → 等空闲 → 原子替换并重排。
///
/// 并发模型：所有调度状态（队列、配置、运行计数、补跑、重载与停止标志、空闲与唤醒信号）
/// 只由 _gate 一把锁保护。锁内只做状态变更；进程启动、等待与日志写入都在锁外完成。
/// 例外：入队失败、无下次触发、重载超时恢复这三类诊断事件仍在锁内记录，它们都不在热路径上。
/// </summary>
internal sealed class Scheduler
{
    private readonly object _gate = new();
    private readonly PriorityQueue<TaskConfig, DateTime> _queue = new();
    private readonly Dictionary<TaskConfig, TaskState> _states = new();
    private readonly HashSet<TaskConfig> _deferredReruns = new();
    private readonly CancellationToken _ct;

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _idle;
    private RunnerConfig _config;
    private string _pwsh;
    private int _running;
    private int _runSeq;
    private bool _reloading;
    private bool _stopRequested;
    private int _jobWarning;

    /// <summary>一次计划触发：任务名与 UTC 时间。</summary>
    internal readonly record struct NextRun(string Task, DateTime When);

    public Scheduler(RunnerConfig config, string pwsh, CancellationToken ct)
    {
        _config = config;
        _pwsh = pwsh;
        _ct = ct;
    }

    /// <summary>
    /// 启动调度。初始入队在返回前完成，后台循环只消费队列——否则首轮重载可能
    /// 先清空并装入新配置，后台循环随后又按同一份配置追加一遍。
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            foreach (var t in _config.tasks.Where(x => x.enabled))
            {
                TryEnqueueLocked(t);
            }
        }

        _ = Task.Run(LoopAsync);
    }

    /// <summary>停止调度：先置停止标志，再结束所有正在运行的进程组。</summary>
    public void Stop()
    {
        List<ChildJob> jobs = [];
        List<Process> running = [];

        lock (_gate)
        {
            _stopRequested = true;
            foreach (var st in _states.Values)
            {
                if (st.Job is not null) jobs.Add(st.Job);
                if (st.Process is not null) running.Add(st.Process);
            }
        }

        SignalWake();

        // Job 先终止：它覆盖已经脱离父进程的后代
        foreach (var job in jobs)
        {
            job.Terminate();
        }

        foreach (var proc in running)
        {
            try { proc.Kill(true); } catch { }
        }
    }

    /// <summary>
    /// 运行期重载：暂停派发，等所有正在运行的任务结束（最多 30s），
    /// 再原子替换配置并重排。返回 null 表示成功，否则返回错误信息。
    /// </summary>
    public async Task<string?> ReloadAsync(RunnerConfig newConfig, string pwsh)
    {
        Task? idle = null;
        lock (_gate)
        {
            _reloading = true;
            if (_running > 0)
            {
                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                idle = _idle.Task;
            }
        }
        SignalWake();

        try
        {
            if (idle is not null)
            {
                try
                {
                    await idle.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (TimeoutException)
                {
                    Log.Event("config-reload-timeout", ("waitSeconds", 30));
                    lock (_gate)
                    {
                        // 放弃重载：恢复延后的补跑与关闭重载窗口同处一个临界区，
                        // 否则这个缝隙里新登记的补跑会滞留在集合中。
                        RestoreDeferredLocked();
                        _reloading = false;
                    }
                    return "等待当前任务结束超时（30s），已放弃重载";
                }
            }

            lock (_gate)
            {
                _states.Clear();
                _config = newConfig;
                _pwsh = pwsh;
                _queue.Clear();
                _deferredReruns.Clear(); // 新配置会重新排队，旧的补跑作废
                foreach (var t in _config.tasks.Where(x => x.enabled))
                {
                    TryEnqueueLocked(t);
                }
                _reloading = false;
            }
            return null;
        }
        finally
        {
            // 异常路径也要关掉重载窗口；正常路径已置 false，这里幂等
            lock (_gate) { _reloading = false; }
            SignalWake();
        }
    }

    private async Task LoopAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            try
            {
                DateTime? next = null;
                bool reloading;
                Task wake;

                // 下次触发时间与当前唤醒任务必须在同一把锁里取，否则重载可能在这两步
                // 之间换掉唤醒源，循环会抱着旧队列的延迟睡满整个上限。
                lock (_gate)
                {
                    if (_stopRequested) return;
                    reloading = _reloading;
                    wake = _wake.Task;
                    if (!reloading && _queue.TryPeek(out _, out var when)) next = when;
                }

                if (reloading)
                {
                    await WaitOrWakeAsync(TimeSpan.FromMilliseconds(200), wake);
                    continue;
                }

                if (next is null)
                {
                    await WaitOrWakeAsync(TimeSpan.FromSeconds(30), wake);
                    continue;
                }

                var now = DateTime.UtcNow;
                if (next.Value > now)
                {
                    var delay = next.Value - now;
                    if (delay > TimeSpan.FromSeconds(60)) delay = TimeSpan.FromSeconds(60);
                    await WaitOrWakeAsync(delay, wake);
                    continue;
                }

                DispatchDue();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Event("scheduler-error", ("message", ex.Message));
                try { await Task.Delay(TimeSpan.FromSeconds(1), _ct); } catch { return; }
            }
        }
    }

    private void DispatchDue()
    {
        List<(string Task, string Policy)> overlaps = [];

        lock (_gate)
        {
            while (!_stopRequested && !_reloading &&
                   _queue.TryPeek(out var task, out var due) && due <= DateTime.UtcNow)
            {
                _queue.Dequeue();
                string? policy = FireLocked(task, out _);
                if (policy is not null) overlaps.Add((task.name, policy));
                TryEnqueueLocked(task);
            }
        }

        foreach (var (task, policy) in overlaps)
        {
            Log.Event("overlap", ("task", task), ("policy", policy));
        }
    }

    /// <summary>
    /// 派发一个任务。注册运行名额、决定 overlap 策略都在锁内完成；
    /// 返回非 null 表示本次命中了 overlap 策略（kill 仍会启动新进程）。
    /// <paramref name="startup"/> 只在真正尝试启动进程时非 null：它在
    /// <c>Process.Start()</c> 返回或抛异常后完成，成功为 null，失败为异常。
    /// 启动发生在 <see cref="ExecuteAsync"/> 里，所以调用方要等这个任务才能知道结果。
    /// </summary>
    private string? FireLocked(TaskConfig task, out Task<Exception?>? startup)
    {
        startup = null;

        // 停止之后不再登记新的运行名额：停止路径只负责终止已在跑的进程，
        // 不能因为一次补跑又拉起新的子进程。
        if (_stopRequested) return null;

        var st = GetStateLocked(task);
        string? policy = null;

        if (st.Running)
        {
            switch (task.overlap)
            {
                case "kill":
                    try { st.Job?.Terminate(); } catch { }
                    try { st.Process?.Kill(true); } catch { }
                    policy = "kill";
                    break;
                case "queue":
                    st.Pending = true;
                    return "queue";
                default:
                    return "skip";
            }
        }

        st.Running = true;
        st.RunId = ++_runSeq;
        _running++;

        int runId = st.RunId;
        var started = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        startup = started.Task;
        _ = Task.Run(() => ExecuteAsync(task, st, runId, started));
        return policy;
    }

    private async Task ExecuteAsync(TaskConfig task, TaskState st, int runId, TaskCompletionSource<Exception?> startup)
    {
        int exitCode = -1;
        bool timedOut = false;
        var sw = Stopwatch.StartNew();

        ChildJob? job = null;
        Process? proc = null;

        try
        {
            job = ChildJob.Create(out string? jobError);
            if (job is null && Interlocked.Exchange(ref _jobWarning, 1) == 0)
            {
                Log.Event("job-create-failed", ("task", task.name), ("error", jobError));
            }

            var psi = BuildStartInfo(task, _pwsh);

            proc = new Process { StartInfo = psi };

            // 最终校验、启动与句柄登记放在同一临界区：否则 Stop 或 kill 可能卡在
            // 三者之间，看到一个还没启动的 Process，或让已被替换的请求照样启动。
            bool started;
            lock (_gate)
            {
                started = !_stopRequested && st.RunId == runId;
                if (started)
                {
                    st.Process = proc;
                    st.Job = job;
                    proc.Start();
                }
            }
            if (!started)
            {
                startup.TrySetResult(new Exception("已被停止或被新的运行替换"));
                return;
            }

            startup.TrySetResult(null);

            // 进程加入本次运行的 Job；失败只记一次，避免每个任务都刷一行
            if (job is not null && !job.TryAssign(proc, out string? assignError) &&
                Interlocked.Exchange(ref _jobWarning, 1) == 0)
            {
                Log.Event("job-assign-failed", ("task", task.name), ("error", assignError));
            }

            Log.Event("task-start", ("task", task.name), ("pid", proc.Id));

            using var readCts = new CancellationTokenSource();
            var stdout = PumpAsync(proc.StandardOutput, task.name, "stdout", readCts.Token);
            var stderr = PumpAsync(proc.StandardError, task.name, "stderr", readCts.Token);

            // 超时覆盖"进程退出 + 两路读取"的联合等待：启动进程先退出、后代还占着
            // 输出管道时，只看进程退出会让较短的 timeout 形同虚设
            var all = Task.WhenAll(proc.WaitForExitAsync(), stdout, stderr);

            try
            {
                if (task.timeout > 0)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(task.timeout));
                    await all.WaitAsync(cts.Token);
                }
                else
                {
                    await all;
                }

                if (proc.HasExited) exitCode = proc.ExitCode;
            }
            catch (OperationCanceledException)
            {
                // 先标记超时再清理：Job 覆盖已经脱离进程树的后代
                timedOut = true;

                job?.Terminate();
                try { proc.Kill(true); } catch { }

                using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await proc.WaitForExitAsync(graceCts.Token); } catch { }

                readCts.Cancel();
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
        }
        catch (Exception ex)
        {
            // 启动失败要能回到 RunNow，否则托盘会把"进程起不来"报成已启动
            startup.TrySetResult(ex);
            Log.Event("task-error", ("task", task.name), ("message", ex.Message));
        }
        finally
        {
            // 显式关掉两路读端：Process 自己的 Dispose 不负责它们，
            // 不关的话句柄要等 GC，外部持有写端时读端会一直活着
            if (proc is not null)
            {
                try { proc.StandardOutput.Dispose(); } catch { }
                try { proc.StandardError.Dispose(); } catch { }
                proc.Dispose();
            }

            // 关闭句柄：仍留在 Job 里的后代（例如任务起的后台进程）会被一并结束
            job?.Dispose();
            Complete(task, st, runId, exitCode, timedOut, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 把一路输出按有界行读出来写日志。不再用 <c>BeginOutputReadLine</c>：
    /// 那个入口会先缓存完整的一行，无换行的巨量输出会把宿主内存顶满。
    /// </summary>
    private static async Task PumpAsync(StreamReader reader, string task, string stream, CancellationToken ct)
    {
        char[] buffer = new char[4096];
        var assembler = new LineAssembler(Log.MaxLineBytes);

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                foreach (string line in assembler.Feed(buffer.AsSpan(0, read)))
                {
                    Log.TaskOutput(task, stream, line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 进程结束或取消：下面把残留的一行落盘
        }

        string? tail = assembler.Flush();
        if (tail is not null) Log.TaskOutput(task, stream, tail);
    }

    /// <summary>
    /// 任务结束：更新状态、释放空闲、处理补跑，全部在一次加锁内完成。
    /// </summary>
    private void Complete(TaskConfig task, TaskState st, int runId, int exitCode, bool timedOut, long durationMs)
    {
        bool rerunFired = false;
        string? rerunPolicy = null;

        lock (_gate)
        {
            if (st.RunId == runId)
            {
                st.Process = null;
                st.Job = null;
                st.Running = false;
            }

            bool rerun = st.Pending;
            st.Pending = false;

            _running--;
            if (_running == 0)
            {
                _idle?.TrySetResult();
                _idle = null;
            }

            if (rerun && !_stopRequested)
            {
                if (_reloading)
                {
                    // 重载结果未定：先记下来，成功则作废，超时则恢复
                    _deferredReruns.Add(task);
                }
                else if (_config.tasks.Contains(task))
                {
                    // 只认原来的任务实例；重载后的同名任务不能在这里被提前触发
                    rerunPolicy = FireLocked(task, out _);
                    rerunFired = true;
                }
            }
        }

        Log.Event("task-end", ("task", task.name), ("exit", exitCode), ("timeout", timedOut), ("durationMs", durationMs));

        if (rerunFired) Log.Event("rerun", ("task", task.name));
        if (rerunPolicy is not null) Log.Event("overlap", ("task", task.name), ("policy", rerunPolicy));
    }

    /// <summary>重载失败后，按仍生效的旧配置恢复等待期间被延后的补跑（只认原来的任务实例）。</summary>
    private void RestoreDeferredLocked()
    {
        if (_deferredReruns.Count == 0) return;

        foreach (var task in _deferredReruns)
        {
            if (!_config.tasks.Contains(task)) continue;

            string? policy = FireLocked(task, out _);
            if (policy is not null) Log.Event("overlap", ("task", task.name), ("policy", policy));
            Log.Event("rerun", ("task", task.name));
        }

        _deferredReruns.Clear();
    }


    private TaskState GetStateLocked(TaskConfig task)
    {
        if (!_states.TryGetValue(task, out var st))
        {
            st = new TaskState();
            _states[task] = st;
        }
        return st;
    }

    private void TryEnqueueLocked(TaskConfig task)
    {
        try
        {
            var cron = CronExpression.Parse(task.cron);
            var nominal = cron.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local);
            if (nominal is null)
            {
                Log.Event("schedule-none", ("task", task.name), ("cron", task.cron));
                return;
            }

            var nextNominal = cron.GetNextOccurrence(nominal.Value, TimeZoneInfo.Local);
            int limit = ClampJitter(task.jitter, nominal.Value, nextNominal);
            _queue.Enqueue(task, nominal.Value.AddSeconds(PickJitter(limit)));
        }
        catch (Exception ex)
        {
            Log.Event("enqueue-error", ("task", task.name), ("message", ex.Message));
        }
    }

    /// <summary>
    /// 抖动不能把本次触发推过下一次名义触发，否则下一次会被 <c>GetNextOccurrence</c> 跳过。
    /// 间隔越短，可用的抖动余量越小；没有余量时按 0 处理。
    /// </summary>
    internal static int ClampJitter(int configured, DateTime nominal, DateTime? nextNominal)
    {
        if (configured <= 0) return 0;
        if (nextNominal is null) return configured;

        int room = (int)Math.Floor((nextNominal.Value - nominal).TotalSeconds) - 1;
        return room <= 0 ? 0 : Math.Min(configured, room);
    }

    /// <summary>在 0..上限之间取一个随机抖动；随机源可注入以便测试。</summary>
    internal static int PickJitter(int limit, Random? random = null)
    {
        if (limit <= 0) return 0;
        return (random ?? Random.Shared).Next(0, limit + 1);
    }

    /// <summary>
    /// 队列里各任务的实际下次触发（含 jitter），按时间升序取前 <see cref="RunnerConfig.upcoming"/> 条。
    /// 每个任务只保留一条，所以返回条数最多是当前启用的任务数；
    /// 更靠后的触发时刻要等这次派发后重新加抖动，现在无法预知。
    /// 条数在锁内读，重载后立即跟着新配置走。
    /// </summary>
    public IReadOnlyList<NextRun> GetUpcoming()
    {
        List<NextRun> queued;
        int count;
        lock (_gate)
        {
            count = _config.upcoming;
            queued = _queue.UnorderedItems.Select(x => new NextRun(x.Element.name, x.Priority)).ToList();
        }

        return queued.OrderBy(x => x.When).Take(count).ToList();
    }

    /// <summary>立即触发一次的结果：是否真的启动了，以及给用户看的说明。</summary>
    internal readonly record struct RunNowResult(bool Started, string Message);

    /// <summary>等待进程启动结果的上限；超时说明线程池忙，按已触发处理。</summary>
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 立即触发一个任务，不改动队列里的计划时间——到点仍会照常再跑一次。
    /// 该任务正在运行时的行为由它的 overlap 策略决定。
    /// </summary>
    public RunNowResult RunNow(string name)
    {
        RunNowResult result;
        Task<Exception?>? startup = null;

        lock (_gate)
        {
            if (_stopRequested)
            {
                result = new RunNowResult(false, "程序正在退出，未执行");
            }
            else if (_reloading)
            {
                result = new RunNowResult(false, "配置正在重载，未执行");
            }
            else
            {
                var task = _config.tasks.FirstOrDefault(t => t.name == name && t.enabled);

                if (task is null)
                {
                    result = new RunNowResult(false, $"任务 {name} 不存在或已禁用");
                }
                else
                {
                    result = FireLocked(task, out startup) switch
                    {
                        null => new RunNowResult(true, $"已启动 {name}"),
                        "kill" => new RunNowResult(true, $"已终止 {name} 的上一次运行并重新启动"),
                        "queue" => new RunNowResult(false, $"{name} 还在运行，已排队等本次结束后再跑"),
                        _ => new RunNowResult(false, $"{name} 还在运行，本次跳过"),
                    };
                }
            }
        }

        // 启动在 ExecuteAsync 里完成：等它有界时间，否则 pwsh 起不来时仍会报"已启动"。
        // 在锁外等，ExecuteAsync 拿到 _gate 才能完成启动。
        if (startup is not null && startup.Wait(StartupWait))
        {
            var error = startup.Result;
            if (error is not null)
            {
                result = new RunNowResult(false, $"启动 {name} 失败：{error.Message}");
            }
        }

        Log.Event("run-now", ("task", name), ("started", result.Started));
        return result;
    }

    private async Task WaitOrWakeAsync(TimeSpan delay, Task wake)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct);

        try
        {
            await Task.WhenAny(Task.Delay(delay, linked.Token), wake);
        }
        finally
        {
            linked.Cancel();
        }
    }

    private void SignalWake()
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            tcs = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        tcs.TrySetResult();
    }

    internal static ProcessStartInfo BuildStartInfo(TaskConfig task, string pwsh)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(CommandWrapper.ForCommand(task.command));

        psi.WorkingDirectory = !string.IsNullOrWhiteSpace(task.cwd) ? task.cwd! : Paths.Base;
        return psi;
    }

    private sealed class TaskState
    {
        public bool Running;
        public bool Pending;
        public int RunId;
        public Process? Process;
        public ChildJob? Job;
    }
}
