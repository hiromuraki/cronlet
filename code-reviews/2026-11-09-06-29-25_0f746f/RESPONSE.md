# Cronlet 分支修复复审回复（0f746f）

- 对应审查：`CODE_REVIEW_2026-11-09-06-29-25_0f746f.md`（提交 `0f746f4`，`try/dottray`）
- 回复时间 2026-11-09 07:30 +08:00（香港时间）
- 处理结果：四项全部修复
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 88 项全部通过；CI 已产出 AOT 并替换 live

## [P2] 启动进程先退出后，任务超时失效并被记录为成功 —— 已修复

复述：`timeout` 只包住 `WaitForExitAsync`，之后两路读取另有固定的 5 秒等待。启动进程提前退出、后代还占着输出管道时：较短的 `timeout` 形同虚设（实测 `timeout = 2` 却在 5.9 秒后记 `exit: 0, timeout: false`），较长的 `timeout` 又会把仍在工作的后代提前截断。

修复（[Scheduler.cs](../../src/Cronlet/Scheduler.cs)）：把配置超时提升为**进程退出与两路读取的联合截止时间**。

```csharp
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
    timedOut = true;          // 先标记结果
    job?.Terminate();         // 再终止整个进程组
    try { proc.Kill(true); } catch { }

    using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try { await proc.WaitForExitAsync(graceCts.Token); } catch { }

    readCts.Cancel();         // 用单独的短宽限收尾
    try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
}
```

顺序按审查建议：先标记超时，再终止 Job，最后用独立的短宽限收尾。`exitCode` 在超时分支保持 `-1`，不再拿已退出进程的 0 冒充成功。

语义变化写进 README：**启动进程先退出、后代仍占着输出管道，也算超时**。

## [P2] 任务结束后未显式关闭标准输出和标准错误的读端 —— 已修复

复述：`proc.StandardOutput` / `StandardError` 拿到的 `StreamReader` 由调用方负责，`Process.Dispose` 不关闭它们。任务结束后外部仍能向管道写入。

修复：`finally` 里显式释放，覆盖所有退出路径；`proc` 变量提到 `try` 外，便于在 `finally` 统一处理。

```csharp
finally
{
    if (proc is not null)
    {
        try { proc.StandardOutput.Dispose(); } catch { }
        try { proc.StandardError.Dispose(); } catch { }
        proc.Dispose();
    }

    job?.Dispose();
    Complete(task, st, runId, exitCode, timedOut, sw.ElapsedMilliseconds);
}
```

超时分支里先 `readCts.Cancel()` 并等读取收尾，再进 `finally`，避免在读取进行中关流。

## [P3] 分块截断可切断字形簇 —— 已修复

复述：`LineAssembler` 按 UTF-16 码元裁断，`8191 × a` + `e` + `U+0301` 会留下单独的 `e` 而丢掉重音。

修复（[LineAssembler.cs](../../src/Cronlet/LineAssembler.cs)）：达到上限时，如果当前字符是组合标记，就把整个簇一起丢掉。

```csharp
if (_truncate && !_truncated)
{
    if (IsCombining(c) && _line.Length > 0)
    {
        int i = _line.Length - 1;
        while (i >= 0 && IsCombining(_line[i])) i--;
        if (i >= 0) i--;
        _line.Length = Math.Max(i + 1, 0);
    }

    _truncated = true;
}
```

`IsCombining` 判 `NonSpacingMark` / `SpacingCombiningMark` / `EnclosingMark`。同时把追加条件收紧成 `!_truncated && _line.Length < MaxChars`——不然回退腾出的空间会被后续字符重新填进去（这也是修这个 bug 时发现的一处连带问题）。

`Log.TruncateLine` 保留作二次保险，按 UTF-8 字节和字形边界再检查一次。

## [P3] Job 终止与释放缺少句柄生命周期同步 —— 已修复

复述：`Stop` 在锁外终止先前取出的 Job，运行线程可同时在 `finally` 里释放同一个 Job；`Terminate` 只检查裸 `IntPtr`，检查与原生调用之间句柄可能已被关闭、甚至被复用。

修复：`ChildJob` 内部用一把锁串行化终止与释放（审查给的两个选项里选了后者，避免把 `SafeHandle` 传进 `P/Invoke` 的额外改动）。

```csharp
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
```

## 已复核项

- 独立 Job 解决末级残留：上一轮已实测两种场景（直接调用 / `Start-Process`），本机复测超时 3 秒内结束且无残留。
- 输出内存：64 MiB 单行场景的宿主峰值从 421 MiB 降到约 20 MiB（审查数据），本轮只改了截断边界，不影响这个结论。
- 配置未知键、CI 权限、README 保持上一轮状态。

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    88 通过 / 0 失败
```

测试数量 86 → 88：新增「截断丢弃整个簇」与「ASCII 前缀完整保留」两例。

未自动化验证：超时与读取的联合等待需要真实派生进程，未写成测试；改完后本机会用审查那套两层 PowerShell 场景复测一次。
