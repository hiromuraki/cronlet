# Cronlet 整库审查回复（ec49a2）

- 对应审查：`CODE_REVIEW_2026-11-08-23-32-29_ec49a2.md`（提交 `ec49a2f`）
- 回复时间 2026-11-08 23:44 +08:00（香港时间）
- 处理结果：五项 P2 全部修复
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 38 项全部通过；无界面退出码已用发布产物实测

## [P2] 停止或替换可发生在启动检查之后 —— 已修复

复述：上一轮把停止检查挪到启动前，但"检查 → 登记 `Process` → `proc.Start()`"分属三段，`Stop` 或 `kill` 卡在中间时会看到尚未启动的进程（`Kill` 抛异常被吞掉），旧请求随后照样启动。

修复（[Scheduler.cs](../../src/Cronlet/Scheduler.cs)）：把最终校验、`proc.Start()` 与句柄登记压进同一个 `_gate` 临界区。

```csharp
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
```

于是 `Stop` 在锁内复制到的 `Process` 一定是已启动的，`kill` 覆盖 `RunId` 后旧请求也不可能再启动。进程终止、等待与日志写入仍在锁外。

代价（审查已指出）：启动期间会持有调度锁。当前启动频率是每任务每次触发一次，pwsh 启动约 100–200 ms，这期间其他调度操作（派发、重载登记）会等待。如果以后任务变多或频繁触发，再按任务拆窄锁。

## [P2] 抖动校验会漏掉较晚出现的短间隔 —— 已修复

复述：`MinIntervalSeconds` 只采样未来 128 次，看不到 `0 0,23 1-29/2 * *` 在 2028-02-29 23:00 之后的 1 小时间隔，于是 `jitter = 7200` 通过校验；实际触发时 `GetNextOccurrence(UtcNow)` 会跳过下一次名义触发。

修复：

- 删除 `ConfigValidator.MinIntervalSeconds` 与"jitter 必须小于最小间隔"的采样校验——它给出的是错误保证。
- 排程改为按**每次触发的实际间隔**限制抖动（[Scheduler.ClampJitter](../../src/Cronlet/Scheduler.cs)）：

```csharp
internal static int ClampJitter(int configured, DateTime nominal, DateTime? nextNominal)
{
    if (configured <= 0) return 0;
    if (nextNominal is null) return configured;

    var room = (int)Math.Floor((nextNominal.Value - nominal).TotalSeconds) - 1;
    return room <= 0 ? 0 : Math.Min(configured, room);
}
```

每次入队都取名义时间 `nominal` 与下一次名义时间 `nextNominal`，抖动余量是两者之差减一，保证 `nominal + jitter < nextNominal`，不会再跳过触发。配置里的 `jitter` 因此变成"上限"而不是硬保证，README 与模板注释同步改了口径。

未做：把已解析的 cron 与名义触发时间缓存到任务配置里复用。当前每次入队解析一次，成本是微秒级，缓存会引入"配置与缓存不一致"的新状态。

## [P2] 无界面模式启动失败仍报告成功 —— 已修复

复述：配置错误时 `Run` 只记录日志就 `return`，进程退出码 0，计划任务会当成成功。`SingleInstance` 还把所有 I/O 异常都归为 `already-running`。

修复：

- [Program.cs](../../src/Cronlet/Program.cs) 的 `Main` 改为返回 `int`：配置错误 `2`、其他启动失败 `1`、已有实例在跑 `0`。异常分支沿用无界面判断——非交互模式不弹窗，只写日志。
- [SingleInstance.cs](../../src/Cronlet/SingleInstance.cs) 只在 `ERROR_SHARING_VIOLATION(32)` / `ERROR_LOCK_VIOLATION(33)` 时返回 `null`（锁被占用），权限或磁盘错误向上抛，由 `Main` 记 `fatal` 并返回 `1`。

验证（发布产物 + 临时目录，实际执行）：

| 配置 | 期望 | 实测 |
|---|---|---|
| 缺 `name` | 2 | 2 |
| `0 0 31 2 *` | 2 | 2 |

## [P2] 合法 cron 表达式没有任何触发时间时仍通过校验 —— 已修复

修复（[ConfigValidator.cs](../../src/Cronlet/ConfigValidator.cs)）：启用的任务必须存在下一次触发，否则按配置错误拒绝。

```csharp
if (t.enabled && cron.GetNextOccurrence(DateTime.UtcNow, Tz) is null)
    throw new Exception($"任务 {label}: cron 永远不会触发 ({t.cron})");
```

禁用的任务不检查——它现在不会运行，启用时会在启动或重载阶段被拦下。

## [P2] 不同任务可以共用一个输出日志文件 —— 已修复

修复：`Log.TaskFile` 提升为 `internal Log.TaskFileName`，校验阶段用它算出每个任务的日志文件名并查重：

```csharp
string logFile = Log.TaskFileName(t.name);
if (logFiles.TryGetValue(logFile, out string? other))
    throw new Exception($"任务 {label}: 与任务 {other} 映射到同一个日志文件 {logFile}");
```

选择"校验阶段报错"而不是改编码：文件名保持人可读，冲突在启动时就暴露，用户改个任务名即可。`a/b` 与 `a_b` 现在会得到明确报错。

## 实现方案评估的回应

| 领域 | 处理 |
| --- | --- |
| 调度与进程 | 采纳"短期把最终校验、`Start()`、句柄登记放进现有锁"，代价是启动期间占用调度锁；暂不拆每任务窄锁 |
| 配置与排程 | 采纳按实际间隔限制抖动；已解析配置的复用暂不做，理由见上 |
| 启动与日志 | 采纳退出码区分；日志名用校验查重而非重编码；`ChildJob.Assign` 的失败仍未记录（下一轮可补） |
| 测试 | 补了固定 cron 时间（`ClampJitter` 四个用例）与无界面退出码（实际执行验证）；启动前暂停点仍未做，需要为 `Scheduler` 引入进程启动抽象 |

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    38 通过 / 0 失败
```

测试数量 31 → 38：新增"无触发 cron 拒绝""禁用时允许无触发""日志名冲突""jitter 不再静态校验"与 `ClampJitter` 的 4 个用例，删除两个旧的 jitter 间隔用例。

未自动化验证：启动/停止交错需要审查建议的"启动前可控暂停点"，本轮仍以代码路径与锁内原子性为准。
