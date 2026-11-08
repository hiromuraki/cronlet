using System.Diagnostics;
using System.Text;
using Cronos;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 调度器：每个启用的任务算出下一次 cron 时间 + jitter 后入优先队列；
/// 循环睡到最近到期时间，执行到期任务，再排下一次。
/// 支持运行期重载：读到临时配置 → 暂停派发 → 等空闲 → 原子替换并重排。
/// </summary>
internal sealed class Scheduler
{
    private readonly CancellationToken _ct;
    private readonly PriorityQueue<TaskConfig, DateTime> _queue = new();
    private readonly Dictionary<TaskConfig, TaskState> _states = new();
    private readonly object _queueGate = new();
    private readonly object _idleGate = new();
    private readonly object _wakeGate = new();

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RunnerConfig _config;
    private int _running;
    private int _runSeq;
    private TaskCompletionSource? _idleTcs;
    private readonly HashSet<TaskConfig> _deferredReruns = new();
    private volatile bool _reloading;
    private volatile bool _stopRequested;

    public Scheduler(RunnerConfig config, CancellationToken ct)
    {
        _config = config;
        _ct = ct;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>停止调度：先置停止标志，再结束所有正在运行的子进程。</summary>
    public void Stop()
    {
        _stopRequested = true;
        SignalWake();

        lock (_states)
        {
            foreach (var st in _states.Values)
            {
                lock (st.Gate)
                {
                    try { st.Process?.Kill(true); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// 运行期重载：暂停派发，等所有正在运行的任务结束（最多 30s），
    /// 再原子替换配置并重排。返回 null 表示成功，否则返回错误信息。
    /// </summary>
    public async Task<string?> ReloadAsync(RunnerConfig newConfig)
    {
        // 置位必须在 _queueGate 内：派发也在同一把锁里进行，这样能保证
        // 已经进入派发路径的任务完成运行登记（_running++）之后才去判断空闲。
        lock (_queueGate)
        {
            _reloading = true;
        }
        SignalWake();

        try
        {
            try
            {
                await WaitForIdleAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Log.Event("config-reload-timeout", ("waitSeconds", 30));
                lock (_queueGate)
                {
                    // 放弃重载：恢复延后的补跑与关闭重载窗口必须同处一个临界区，
                    // 否则这个缝隙里新登记的补跑会滞留在集合中无人处理。
                    RestoreDeferredRerunsLocked();
                    _reloading = false;
                }
                SignalWake();
                return "等待当前任务结束超时（30s），已放弃重载";
            }

            lock (_queueGate)
            {
                lock (_states) { _states.Clear(); }
                _config = newConfig;
                _queue.Clear();
                _deferredReruns.Clear(); // 新配置会重新排队，旧的补跑作废
                foreach (var t in _config.tasks.Where(x => x.enabled))
                {
                    TryEnqueueLocked(t);
                }
            }
        }
        finally
        {
            lock (_queueGate) { _reloading = false; }
            SignalWake();
        }

        return null;
    }

    private async Task LoopAsync()
    {
        lock (_queueGate)
        {
            foreach (var t in _config.tasks.Where(x => x.enabled))
            {
                TryEnqueueLocked(t);
            }
        }

        while (!_ct.IsCancellationRequested && !_stopRequested)
        {
            try
            {
                if (_reloading)
                {
                    await WaitOrWakeAsync(TimeSpan.FromMilliseconds(200));
                    continue;
                }

                DateTime? next = null;
                lock (_queueGate)
                {
                    if (_queue.TryPeek(out _, out var when)) next = when;
                }

                if (next is null)
                {
                    await WaitOrWakeAsync(TimeSpan.FromSeconds(30));
                    continue;
                }

                var now = DateTime.UtcNow;
                if (next.Value > now)
                {
                    var delay = next.Value - now;
                    if (delay > TimeSpan.FromSeconds(60)) delay = TimeSpan.FromSeconds(60);
                    await WaitOrWakeAsync(delay);
                    continue;
                }

                lock (_queueGate)
                {
                    while (!_stopRequested && !_reloading &&
                           _queue.TryPeek(out var task, out var due) && due <= DateTime.UtcNow)
                    {
                        _queue.Dequeue();
                        Fire(task);
                        TryEnqueueLocked(task);
                    }
                }
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

    private async Task WaitOrWakeAsync(TimeSpan delay)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        Task wake;
        lock (_wakeGate) { wake = _wake.Task; }

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
        lock (_wakeGate)
        {
            tcs = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        tcs.TrySetResult();
    }

    private void TryEnqueueLocked(TaskConfig task)
    {
        try
        {
            var cron = CronExpression.Parse(task.cron);
            var next = cron.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local);
            if (next is null)
            {
                Log.Event("schedule-none", ("task", task.name), ("cron", task.cron));
                return;
            }
            int jitter = task.jitter > 0 ? Random.Shared.Next(0, task.jitter + 1) : 0;
            _queue.Enqueue(task, next.Value.AddSeconds(jitter));
        }
        catch (Exception ex)
        {
            Log.Event("enqueue-error", ("task", task.name), ("message", ex.Message));
        }
    }

    private Task WaitForIdleAsync(TimeSpan timeout)
    {
        lock (_idleGate)
        {
            if (_running == 0) return Task.CompletedTask;
            _idleTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idleTcs.Task.WaitAsync(timeout);
        }
    }

    private TaskState GetState(TaskConfig task)
    {
        lock (_states)
        {
            if (!_states.TryGetValue(task, out var st))
            {
                st = new TaskState();
                _states[task] = st;
            }
            return st;
        }
    }

    private void Fire(TaskConfig task)
    {
        if (_stopRequested) return;

        var st = GetState(task);
        int runId;

        lock (st.Gate)
        {
            if (st.Running)
            {
                switch (task.overlap)
                {
                    case "kill":
                        try { st.Process?.Kill(true); } catch { }
                        Log.Event("overlap", ("task", task.name), ("policy", "kill"));
                        break;
                    case "queue":
                        st.Pending = true;
                        Log.Event("overlap", ("task", task.name), ("policy", "queue"));
                        return;
                    default:
                        Log.Event("overlap", ("task", task.name), ("policy", "skip"));
                        return;
                }
            }

            st.Running = true;
            runId = st.RunId = Interlocked.Increment(ref _runSeq);
            lock (_idleGate) { _running++; }
        }

        _ = Task.Run(() => ExecuteAsync(task, st, runId));
    }

    private async Task ExecuteAsync(TaskConfig task, TaskState st, int runId)
    {
        int exitCode = -1;
        bool timedOut = false;
        var sw = Stopwatch.StartNew();

        try
        {
            var psi = BuildStartInfo(task, out string cwd);
            psi.WorkingDirectory = cwd;

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.TaskOutput(task.name, "stdout", e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.TaskOutput(task.name, "stderr", e.Data); };

            lock (st.Gate) { st.Process = proc; }

            proc.Start();
            ChildJob.Assign(proc);
            Log.Event("task-start", ("task", task.name), ("pid", proc.Id));

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (_stopRequested)
            {
                try { proc.Kill(true); } catch { }
            }

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
            lock (st.Gate)
            {
                if (st.RunId == runId)
                {
                    st.Process = null;
                    st.Running = false;
                }
            }

            Log.Event("task-end", ("task", task.name), ("exit", exitCode), ("timeout", timedOut), ("durationMs", sw.ElapsedMilliseconds));

            bool rerun;
            lock (st.Gate)
            {
                rerun = st.Pending;
                st.Pending = false;
            }

            lock (_idleGate)
            {
                _running--;
                if (_running == 0)
                {
                    _idleTcs?.TrySetResult();
                    _idleTcs = null;
                }
            }

            if (rerun)
            {
                TryRerun(task);
            }
        }
    }

    /// <summary>
    /// 补跑排队的任务。与重载互斥：重载窗口内只登记、不派发，等重载结果决定是丢弃
    /// （新配置已重排）还是恢复（重载超时，旧配置继续生效）。
    /// 派发时只按名字从**当前**配置里取任务，避免重载后拿旧对象跑旧命令。
    /// </summary>
    private void TryRerun(TaskConfig finished)
    {
        lock (_queueGate)
        {
            if (_stopRequested) return;

            if (_reloading)
            {
                _deferredReruns.Add(finished);
                return;
            }

            var current = _config.tasks.FirstOrDefault(t =>
                t.enabled && string.Equals(t.name, finished.name, StringComparison.OrdinalIgnoreCase));
            if (current is null) return;

            // 重载会造出同名的新实例：旧任务的补跑请求不能落到新任务上，
            // 否则新任务会在 cron 到期前被提前触发。
            if (!ReferenceEquals(current, finished)) return;

            Log.Event("rerun", ("task", current.name));
            Fire(current);
        }
    }

    /// <summary>重载失败后，按仍生效的旧配置恢复等待期间被延后的补跑（只认原来的任务实例）。</summary>
    private void RestoreDeferredRerunsLocked()
    {
        if (_deferredReruns.Count == 0) return;

        foreach (var task in _deferredReruns)
        {
            if (!_config.tasks.Contains(task)) continue;

            Log.Event("rerun", ("task", task.name));
            Fire(task);
        }

        _deferredReruns.Clear();
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

        // script_file 与 command 互斥且必选其一，由配置校验保证；运行期文件被删则报错退出。
        string? file = null;
        if (!string.IsNullOrWhiteSpace(task.script_file))
        {
            file = ScriptResolver.Resolve(task.script_file!)
                   ?? throw new FileNotFoundException($"找不到脚本: {task.script_file}");
        }

        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(file is not null
            ? CommandWrapper.ForScript(file)
            : CommandWrapper.ForCommand(task.command ?? ""));

        cwd = !string.IsNullOrWhiteSpace(task.cwd)
            ? task.cwd!
            : file is not null ? Path.GetDirectoryName(file)! : AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(cwd)) cwd = AppContext.BaseDirectory;
        return psi;
    }

    private sealed class TaskState
    {
        public readonly object Gate = new();
        public bool Running;
        public bool Pending;
        public int RunId;
        public Process? Process;
    }
}
