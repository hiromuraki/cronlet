# Cronlet 整库复审回复（b0fbe6）

- 对应审查：`CODE_REVIEW_2026-11-09-00-49-24_b0fbe6.md`（提交 `b0fbe65`）
- 回复时间 2026-11-09 01:17 +08:00（香港时间）
- 处理结果：三项全部修复
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 50 项全部通过

## [P2] Job 清理标志设置失败时，分配仍会报告成功 —— 已修复

复述：`CreateJobObject` 成功、`SetInformationJobObject` 失败时，静态构造保留了非空句柄；`TryAssign` 只在句柄为空时读 `InitError`，于是分配成功就返回 `true`——进程进了 Job，但 `KILL_ON_JOB_CLOSE` 没设上，强杀时不会被清理，而且没有任何告警。

修复（[ChildJob.cs](../src/Cronlet/ChildJob.cs)）：把 Job 初始化当成一次整体操作，只有创建与设置都成功才发布句柄。

```csharp
IntPtr handle = CreateJobObjectW(IntPtr.Zero, null);
// ...
if (!SetInformationJobObject(handle, ...))
{
    InitError = $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}";
    CloseHandle(handle);
    return;          // Handle 保持 IntPtr.Zero
}
Handle = handle;
```

这样设置失败后 `TryAssign` 走的是"Job 不可用"分支，返回 `false` 并带出原始错误，调度器那条 `job-assign-failed` 才能真正落到日志里。新增 `CloseHandle` 的 P/Invoke。

## [P2] 启动与首次重载交错时，同一任务会重复入队 —— 已修复

复述：`Start` 只是把循环丢给后台线程，初始入队在 `LoopAsync` 里。若首轮重载先完成，它会清空队列并装入新配置，后台循环随后按同一份配置再入队一遍，同一个任务实例在队列里出现两次，重复项还会随着重新入队延续。

修复（[Scheduler.cs](../src/Cronlet/Scheduler.cs)）：初始入队移到 `Start` 返回之前完成，后台循环只消费队列。

```csharp
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
```

`LoopAsync` 里的初始入队块删除，重载与启动因此不会各追加一遍。

验证：新增 `SchedulerTests.Start_enqueues_each_enabled_task_once_before_returning`——三个任务（两个启用）`Start()` 返回后 `QueuedCount` 必须是 2，用的 cron 是 `0 0 1 1 *`，不会在测试期间触发。

## [P3] 任务名文档未说明字符集仅限 ASCII —— 已修复

实现用的是 `char.IsAsciiLetterOrDigit`，文档却只写"字母、数字"，`作业`、全角数字会被拒但说明里看不出来。

三处统一（[README.md](../README.md) · [Config.cs](../src/Cronlet/Config.cs) · [ConfigValidator.cs](../src/Cronlet/ConfigValidator.cs)）：

- README：`只允许 A-Z、a-z、0-9、_、-，长度 1–128`
- 校验错误：`name 只能包含 A-Z、a-z、0-9、下划线和短横线，长度 1-128`
- 生成的配置模板里也单独写了一行，按你的要求让规则在文件本身就能看到：

```toml
#   name        任务名（唯一，日志里用它标识）
#               只允许 A-Z a-z 0-9 _ -，长度 1-128
```

## 方案评估的回应

| 领域 | 处理 |
| --- | --- |
| 排程 | 采纳"初始入队前移"，队列与唤醒机制不变 |
| 进程清理 | 采纳"Job 初始化作为整体操作"，声明失败即不发布句柄；仍是尽力清理 |
| 日志命名 | 保留现有实现，文档与错误信息改为准确描述 |

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    50 通过 / 0 失败
```

测试数量 49 → 50：新增 `Start_enqueues_each_enabled_task_once_before_returning`。

未自动化验证：`SetInformationJobObject` 失败路径需要故障注入（审查是在隔离副本里改参数长度复现的），本机没有对应抓手，只做了代码路径确认。
