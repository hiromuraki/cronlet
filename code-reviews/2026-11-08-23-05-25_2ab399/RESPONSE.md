# Cronlet 复审回复（2ab399）

- 对应审查：`CODE_REVIEW_2026-11-08-23-05-25_2ab399.md`（提交 `2ab3997`）
- 回复时间 2026-11-08 23:15 +08:00（香港时间）
- 处理结果：三项 P2 全部修复，一项 P3 部分采纳（迁移说明按约定不写）
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 31 项全部通过

## [P2] 停止后仍会启动排队任务 —— 已修复

复述：`Stop` 只设停止标志并终止已在跑的进程；该进程结束后 `Complete` 仍会为 `Pending` 调 `FireLocked`，而 `FireLocked` 不检查停止标志；启动后的停止检查发生在 `proc.Start()` 之后，已经来不及。

修复（[Scheduler.cs](../../src/Cronlet/Scheduler.cs)）：

- `FireLocked` 开头加停止检查：停止后不再登记新的运行名额，补跑、恢复延后补跑、正常派发都走这一个入口。
- `Complete` 的补跑分支改成 `if (rerun && !_stopRequested)`：停止中直接丢弃，不登记也不派发。
- `ExecuteAsync` 把停止检查移到启动**之前**（与 RunId 校验合并），不再启动后再补一刀 kill。

## [P2] kill 策略会放行已被替换的启动请求 —— 已修复

复述：调度先登记 `Running` 与 `RunId`，后台执行随后才创建进程。`kill` 到达时 `st.Process` 还是 null，只覆盖了 `RunId`；旧请求进入 `ExecuteAsync` 后仍会 `Start()`，两个进程一起跑。

修复：`ExecuteAsync` 在创建 `Process` 之前先做一次锁内定夺：

```csharp
bool startable;
lock (_gate) { startable = !_stopRequested && st.RunId == runId; }
if (!startable) return;
```

`RunId` 就是这次运行的身份：`kill` 覆盖它意味着旧请求已被替换，旧请求据此放弃启动；已被替换的运行结束后，`Complete` 里 `st.RunId == runId` 不成立，也不会错误复位 `Running`。进程终止与等待仍在锁外执行。

## [P2] 重载通知可能在调度开始等待前丢失 —— 已修复

复述：循环先在锁内读队列，再在 `WaitOrWakeAsync` 里第二次加锁取唤醒任务。两次之间若发生重载，`SignalWake` 换掉 TCS，循环拿到的是**新** TCS（未完成），于是按旧队列的延迟睡满上限（空配置 30s、触发较远时 60s）。

修复：把"读下次触发时间"和"取当前唤醒任务"放进同一个临界区，作为一份快照传给等待逻辑；`WaitOrWakeAsync` 改为接收 `Task wake`，不再自己加锁。

```csharp
lock (_gate)
{
    if (_stopRequested) return;
    reloading = _reloading;
    wake = _wake.Task;
    if (!reloading && _queue.TryPeek(out _, out var when)) next = when;
}
```

这样重载在快照之后发出的唤醒一定作用在快照里的那个 TCS 上，循环立即醒来重排。

## [P3] 文档与实现不一致 —— 部分采纳

- `cwd` 一栏已在 `01930c2` 改为"空则用 exe 目录"，并补了一句默认工作目录的说明。
- 依你的要求**不写迁移指南**：现在处于快速迭代期，模板注释已覆盖字段含义。
- 含空格路径的写法补了一行示例：`command = "& './scripts/Daily Jobs/report.ps1'"`。
- `CommandWrapper.ForScript` 与对应测试一并删除——`c49f9bc` 之后它已经没有调用方。

## 实现方案比较的回应

| 领域 | 处理 |
| --- | --- |
| 调度和重载 | 保留单锁与实例归属补跑。补齐了状态快照与唤醒订阅的原子性（同一临界区），这是本轮真正的缺陷 |
| 进程生命周期 | 用 `RunId` 作为运行身份，在创建进程前定夺"停止 / 被替换"，覆盖审查复现的两条路径。没有引入完整的每运行状态机（待启动、已启动、已取消三态 + 可取消名额）：当前只有 `kill` 与 `Stop` 两个撤销来源，RunId 足够表达；如果以后加入"取消单个任务"之类的操作，再考虑显式状态 |
| 配置入口 | 采纳：`ForScript` 删除，脚本执行直接走 `ForCommand`，测试不再有独立的脚本分支 |

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    31 通过 / 0 失败
```

测试数量 32 → 31：删除 `Failing_script_file_exits_nonzero`（随 `ForScript` 一起移除）。

未自动化验证：三项修复都是时序问题，需要审查提到的"可控进程完成信号与超时信号"夹具才能写成测试；本轮以代码路径与不变量为准。实际运行验证见部署记录。
