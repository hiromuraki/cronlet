# Cronlet 整库与分支审查回复（d49360）

- 对应审查：`CODE_REVIEW_2026-11-09-05-51-13_d49360.md`（提交 `d493605`，`try/dottray`）
- 回复时间 2026-11-09 06:20 +08:00（香港时间）
- 处理结果：五项全部修复
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 86 项全部通过

## [P2] 任务超时后仍可遗留进程 —— 已修复

复述：`timeout` 靠 `Process.Kill(entireProcessTree)` 从原 PowerShell 枚举进程树。中间进程先退出后，末级进程已经不在树里，枚举不到；Job 只是宿主级的兜底，要等宿主退出才生效，而调度器已经清掉运行名额。

修复：**每次运行持有独立 Job**（[ChildJob.cs](../../src/Cronlet/ChildJob.cs) · [Scheduler.cs](../../src/Cronlet/Scheduler.cs)）。

```csharp
job = ChildJob.Create(out string? jobError);
// ...
lock (_gate) { st.Job = job; proc.Start(); }
job.TryAssign(proc, out string? assignError);
```

三条终止路径都改成终止 Job：

| 路径 | 之前 | 现在 |
|---|---|---|
| `timeout` | `proc.Kill(entireProcessTree)` | `job.Terminate()` 再 `proc.Kill(true)` |
| `overlap = "kill"` | `st.Process?.Kill(true)` | `st.Job?.Terminate()` 再杀进程 |
| 停止（`Stop`） | 逐个 `Kill(true)` | 先终止所有 Job，再逐个杀进程 |

关键是 Windows Job 的语义：**加入 Job 的进程所创建的后代自动属于同一个 Job**。所以即使中间进程退出、末级进程"脱离"了进程树，它仍在 Job 里，`TerminateJobObject` 能一并收掉。

另外 `ExecuteAsync` 的 `finally` 里 `job.Dispose()`——句柄关闭触发 `KILL_ON_JOB_CLOSE`，任务正常结束后仍留在 Job 里的后代（例如任务起的后台进程）也会被清掉。这是行为变化：任务不再能留下常驻后台进程，已写进 README 的超时说明。

## [P2] 任务输出上限只约束日志，超长单行仍显著抬高宿主内存 —— 已修复

复述：`BeginOutputReadLine` 会先把完整一行攒进内存，日志截断发生在回调之后。64 MiB 无换行输出把宿主私有提交顶到 162.8 MiB、峰值 421 MiB，而日志最后只写 8 KiB。

修复：不再用 `BeginOutputReadLine`，改为自己分块读 + 有界行装配（[LineAssembler.cs](../../src/Cronlet/LineAssembler.cs) · `Scheduler.PumpAsync`）。

```csharp
while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
{
    foreach (string line in assembler.Feed(buffer.AsSpan(0, read)))
        Log.TaskOutput(task, stream, line);
}
```

`LineAssembler` 的规则：

- 每行最多留 `MaxChars` 个字符，超出部分只置一个"已截断"标记，**不再累积**；
- 行结束时输出前缀 + `…[truncated]`；
- `\r` 也断行，紧随的 `\n` 不产生空行（CRLF 跨块也成立）；
- EOF 时 `Flush()` 落盘最后一行。

内存因此与"最长行"无关，只与 4 KiB 的读缓冲和单个上限有关。两路各自独立装配，互不阻塞。

测试覆盖：单行、CRLF、`\r`、跨块拼接、跨块 CRLF、超长截断、64 次分块喂 64 KiB、EOF 残留、空输入。

## [P2] 配置键拼写错误可静默停掉全部任务 —— 已修复

复述：`[[tasks]]` 写成 `[[task]]` 时进程照跑、`tasks: 0`；`enabled = false` 写成 `enabeld = false` 时按默认值启用。Tomlyn 默认忽略未知键。

修复：用 `[TomlExtensionData]` 收集未映射的键，反序列化后检查（[Config.cs](../../src/Cronlet/Config.cs)）。

```csharp
[TomlExtensionData] public Dictionary<string, object>? extra { get; set; }
```

- 顶层未知键 → `顶层有未知的键: task（检查拼写，例如表名应为 [[tasks]]）`
- 任务内未知键 → `任务 a: 有未知字段: enabeld`

`tasks = []` 仍然表示"没有任务"（它是已知键），省略可选字段仍走默认值。源生成绑定不变。

## [P3] 常规 CI 构建和测试继承了 Release 写权限 —— 已修复

工作流拆成两个作业：

- `build`：`contents: read`，`checkout` 加 `persist-credentials: false`，只做构建/测试/上传
- `release`：`needs: build`、`permissions: contents: write`，**不检出代码、不跑构建**，只下载已构建的 AOT 产物后用 `gh release create` 发布

写权限不再覆盖构建与测试步骤，checkout 也不再在工作区留下可复用的凭据。

## [P3] README 仍将托盘实现写成 WinForms —— 已修复

- 简介改为「.NET 11 / 纯 Win32 托盘 / NativeAOT」
- 目录结构里 `src/Cronlet/` 标注为「DotTray + Tomlyn 源生成，AOT 兼容」
- 构建章节区分两种产物：框架依赖单文件（目标机需 .NET 11）与 NativeAOT 自包含（约 3 MB，不需要运行时，本机编译需 MSVC，CI 自带）
- `timeout` 说明改为「终止整个进程组（含已脱离父进程的后代）」

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    86 通过 / 0 失败
```

测试数量 82 → 86：新增未知键 3 例与显式空数组 1 例；`LineAssembler` 8 例。

未自动化验证：审查那套"两层启动、末级休眠 75 秒"的超时场景没有在测试里复现（要真实派生进程），需要手工验证。
