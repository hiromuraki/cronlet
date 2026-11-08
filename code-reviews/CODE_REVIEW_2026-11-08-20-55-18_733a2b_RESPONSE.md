# Cronlet 复审回复（733a2b）

- 对应审查：`CODE_REVIEW_2026-11-08-20-55-18_733a2b.md`（提交 `733a2bf`）
- 回复时间 2026-11-08 21:41 +08:00（香港时间）
- 处理结果：四项发现中两项在后续重构里已经消失（`script_file`/`command`），两项本次修复；方案比较逐条回应
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 40 项全部通过

## [P2] 旧补跑可在重载后提前触发同名新任务 —— 已修复

复述根因：任务结束时先递减 `_running` 并通知空闲，之后才处理补跑；重载可以在这两步之间完成，于是旧任务的补跑按名字找到了**新**配置里的同名任务，并在 cron 到期前把它派发出去。

修复（[Scheduler.cs](../src/Cronlet/Scheduler.cs#L386-L402) · [第 403-419 行](../src/Cronlet/Scheduler.cs#L403-L419)）：

- 补跑请求改为绑定 `TaskConfig` **实例**：`_deferredReruns` 从 `HashSet<string>` 改成 `HashSet<TaskConfig>`（默认比较器即引用相等）。
- `TryRerun` 在按名字定位到当前任务后，追加 `ReferenceEquals(current, finished)` 校验：重载会反序列化出同名的新实例，引用不等，旧请求直接作废，不会提前触发新任务。
- `RestoreDeferredRerunsLocked` 同步改为"只恢复仍在当前配置里的原实例"（`_config.tasks.Contains(task)`），不再按名字重新查找。
- 超时分支把"恢复延后的补跑"和"清除 `_reloading`"合并进同一个 `_queueGate` 临界区（[第 76-88 行](../src/Cronlet/Scheduler.cs#L76-L88)），消除审查指出的第二个交错：否则这个缝隙里新登记的补跑会滞留在集合中，错过本次恢复。

语义变化：重载成功后，旧任务的补跑一律作废。这是有意的——重载会按新配置重新排程，计划触发不受影响，而"立即补跑"只应属于被替换掉的那份配置。

未自动化验证：构造该时序需要受控的进程完成信号与重载续体，当前测试框架没有这种注入点，本项以实例绑定的不变量为准。

## [P2] 命令参数以脚本扩展名结尾时校验失败 —— 已解决（不在本次改动）

该问题在 `4dc3683` 引入 `script_file`/`command` 后已不存在：命令走 `command` 字段，不再经过任何文件推断，`Write-Output report.ps1`、`'report.ps1'` 都按命令执行。审查建议的"为命令提供显式入口"正是这一轮的实现。

含空格的已存在路径仍在 `script_file` 分支下按文件解析，`ScriptResolver` 只用 `Path.Combine`，不做空白切分。

## [P2] 脚本在校验后消失会改变执行方式 —— 已解决（不在本次改动）

同源于显式字段。现在执行类型由字段决定，与文件是否存在无关；`script_file` 在派发时解析失败会抛 `FileNotFoundException`（[第 421-436 行](../src/Cronlet/Scheduler.cs#L421-L436)），进入 `task-error` 日志并以失败结束，不会退化成内联命令。

审查建议的"在启动及重载时固定解析后的脚本路径"这次没有做缓存：派发时重新 `Resolve` 一次的成本是一次 `File.Exists`，而缓存会引入"配置里的路径与缓存不一致"的新状态。执行类型已经固定，剩下的只是路径有效性检查。

## [P3] 启用原生命令错误处理后会丢失原生退出码 —— 已修复

根因：任务若设置 `$PSNativeCommandUseErrorActionPreference = $true`，非零退出会抛 `NativeCommandExitException`，先被通用 `catch` 吃掉并 `exit 1`。

修复（[CommandWrapper.cs](../src/Cronlet/CommandWrapper.cs#L29-L38)）：`catch` 里先识别该异常并透传其 `ExitCode`，其余情况仍是打印消息后 `exit 1`。用类型名字符串比较而不是 `-is [类型]`，避免在缺少该类型的运行时上让判断本身失败。

验证：新增 `Native_exit_code_survives_native_error_preference`（`$PSNativeCommandUseErrorActionPreference = $true; cmd /c exit 7` 应得 7），原有的 `Native_exit_code_is_preserved` 覆盖开关关闭的情形；`PowerShell_failure_exits_one` 收紧为精确断言 1。

## 方案比较的回应

| 领域 | 本次处理 |
| --- | --- |
| 调度和重载 | 未做"单一调度状态锁"的整体重构。改为把旧请求绑定实例、并把超时恢复与窗口关闭合成一个临界区，覆盖已复现的两条路径。整体重构需要重写唤醒、停止与进程回调，与当前 470 行的规模不匹配，留作后续单独变更 |
| 配置解析 | 采纳互斥 `script_file` / `command`，已在 `4dc3683` 落地。不保留旧 `script` 兼容分支：仓库只有一处部署，已随本次迁移，兼容层只会让诊断重新变模糊 |
| PowerShell 包装 | 保留换行结构，补上原生退出异常识别。`$ErrorActionPreference='Stop'` 带来的"失败即停"语义写进了类注释，避免后来者把它当噪音 |
| 构建约束 | 暂不钉 SDK 版本。`.NET 11` 仍是 RC，钉死 `11.0.100-rc.3` 会让 CI 与本地在补丁版本上互相牵制；GA 之后再统一 `global.json` 与 CI，通过独立提交升级 |

审查建议的"可控进程完成信号与超时信号"测试夹具，这次没有引入。它需要把 `Scheduler` 的进程启动与时钟抽象出来，属于为测试重构生产代码，取舍留给你定。

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    40 通过 / 0 失败
```

测试数量：39 → 40（新增原生退出码用例，PowerShell 失败用例由非零改为精确 1）。
