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
    private int _running;
    private int _runSeq;
    private bool _reloading;
    private bool _stopRequested;
    private int _jobWarning;

    public Scheduler(RunnerConfig config, CancellationToken ct)
    {
        _config = config;
        _ct = ct;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>停止调度：先置停止标志，再结束所有正在运行的子进程。</summary>
    public void Stop()
    {
        List<Process> running = [];
        lock (_gate)
        {
            _stopRequested = true;
            foreach (var st in _states.Values)
            {
                if (st.Process is not null) running.Add(st.Process);
            }
        }

        SignalWake();

        foreach (var proc in running)
        {
            try { proc.Kill(true); } catch { }
        }
    }

    /// <summary>
    /// 运行期重载：暂停派发，等所有正在运行的任务结束（最多 30s），
    /// 再原子替换配置并重排。返回 null 表示成功，否则返回错误信息。
    /// </summary>
    public async Task<string?> ReloadAsync(RunnerConfig newConfig)
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
                _queue.Clear();
                _deferredReruns.Clear(); // 新配置会重新排队，旧的补跑作废
                foreach (var t in _config.tasks.Where(x => x.enabled))
                {
                    TryEnqueueLocked(t);
                }
                _reloading = false;
            }
            SignalWake();
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
        lock (_gate)
        {
            foreach (var t in _config.tasks.Where(x => x.enabled))
            {
                TryEnqueueLocked(t);
            }
        }

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
                string? policy = FireLocked(task);
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
    /// </summary>
    private string? FireLocked(TaskConfig task)
    {
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
        _ = Task.Run(() => ExecuteAsync(task, st, runId));
        return policy;
    }

    private async Task ExecuteAsync(TaskConfig task, TaskState st, int runId)
    {
        int exitCode = -1;
        bool timedOut = false;
        var sw = Stopwatch.StartNew();

        try
        {
            var psi = BuildStartInfo(task, out string? cwd);

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.TaskOutput(task.name, "stdout", e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.TaskOutput(task.name, "stderr", e.Data); };

            // 最终校验、启动与句柄登记放在同一临界区：否则 Stop 或 kill 可能卡在
            // 三者之间，看到一个还没启动的 Process，或让已被替换的请求照样启动。
            bool started;
            lock (_gate)
            {
                started = !_stopRequested && st.RunId == runId;
                if (started)
                {
                    st.Process = proc;
                    proc.Start();
                }
            }
            if (!started) return;

            // Job 只覆盖加入之后的进程；失败只记一次，避免每个任务都刷一行
            if (!ChildJob.TryAssign(proc, out string? jobError) && Interlocked.Exchange(ref _jobWarning, 1) == 0)
            {
                Log.Event("job-assign-failed", ("task", task.name), ("error", jobError));
            }

            Log.Event("task-start", ("task", task.name), ("pid", proc.Id));

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (task.timeout > 0)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(task.timeout));
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    try { proc.Kill(true); } catch { }
                    using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await proc.WaitForExitAsync(killCts.Token); } catch { }
                }
            }
            else
            {
                await proc.WaitForExitAsync();
            }

            proc.WaitForExit(2000);
            if (proc.HasExited) exitCode = proc.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Event("task-error", ("task", task.name), ("message", ex.Message));
        }
        finally
        {
            Complete(task, st, runId, exitCode, timedOut, sw.ElapsedMilliseconds);
        }
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
                    rerunPolicy = FireLocked(task);
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

            string? policy = FireLocked(task);
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

    internal static ProcessStartInfo BuildStartInfo(TaskConfig task, out string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
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

        cwd = !string.IsNullOrWhiteSpace(task.cwd) ? task.cwd! : Paths.Base;
        psi.WorkingDirectory = cwd;
        return psi;
    }

    private sealed class TaskState
    {
        public bool Running;
        public bool Pending;
        public int RunId;
        public Process? Process;
    }
}
