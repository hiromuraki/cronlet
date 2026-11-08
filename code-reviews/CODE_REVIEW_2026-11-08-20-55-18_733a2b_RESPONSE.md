# Cronlet 复审回复（733a2b）

- 对应审查：`CODE_REVIEW_2026-11-08-20-55-18_733a2b.md`（提交 `733a2bf`）
- 回复时间 2026-11-08 21:41 +08:00，更新于 23:04 +08:00（香港时间）
- 处理结果：四项发现中两项在后续重构里已经消失，两项在 `75c876f` 修复；方案比较里"单一锁""单一 command"两条建议已在 `c49f9bc` 落地
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 32 项全部通过（本回复首版时为 40 项，见文末"后续变更"）

## [P2] 旧补跑可在重载后提前触发同名新任务 —— 已修复

复述根因：任务结束时先递减 `_running` 并通知空闲，之后才处理补跑；重载可以在这两步之间完成，于是旧任务的补跑按名字找到了**新**配置里的同名任务，并在 cron 到期前把它派发出去。

修复（`75c876f`，[Scheduler.cs](../src/Cronlet/Scheduler.cs)）：

- 补跑请求改为绑定 `TaskConfig` **实例**：`_deferredReruns` 从 `HashSet<string>` 改成 `HashSet<TaskConfig>`（默认比较器即引用相等）。
- 补跑时按名字定位到当前任务后，追加引用校验：重载会反序列化出同名的新实例，引用不等，旧请求直接作废，不会提前触发新任务。
- 恢复延后补跑时只认"仍在当前配置里的原实例"，不再按名字重新查找。
- 超时分支把"恢复延后的补跑"和"清除 `_reloading`"合并进同一个临界区，消除审查指出的第二个交错：否则这个缝隙里新登记的补跑会滞留在集合中，错过本次恢复。

后续 `c49f9bc` 把这些合并进单锁，引用校验退化为"实例仍在当前配置里"的一次判断，不再需要跨锁推理。

语义变化：重载成功后，旧任务的补跑一律作废。这是有意的——重载会按新配置重新排程，计划触发不受影响，而"立即补跑"只应属于被替换掉的那份配置。

未自动化验证：构造该时序需要受控的进程完成信号与重载续体，当前测试框架没有这种注入点。

## [P2] 命令参数以脚本扩展名结尾时校验失败 —— 已解决

`4dc3683` 引入显式入口后，命令不再经过任何文件推断，`Write-Output report.ps1`、`'report.ps1'` 都按命令执行。审查建议的"为命令提供显式入口"正是那一轮的实现。

`c49f9bc` 进一步只保留 `command`，`script_file` 与 `ScriptResolver` 整体删除，这条发现的产生条件已不存在。

## [P2] 脚本在校验后消失会改变执行方式 —— 已解决

`4dc3683` 之后执行类型由字段决定，与文件是否存在无关。`c49f9bc` 删掉 `script_file` 后，任务只知道 `command`：命令里引用的文件消失时由 pwsh 自己报告"找不到命令/路径"，以非零退出码进 `task-error` 与 `task-<name>.jsonl`，不会静默改走另一条执行路径。

审查建议的"在启动及重载时固定解析后的脚本路径"没有做缓存：缓存会引入"配置里的路径与缓存不一致"的新状态，而执行类型已经固定，剩下的只是 PowerShell 自己的路径解析。

## [P3] 启用原生命令错误处理后会丢失原生退出码 —— 已修复

根因：任务若设置 `$PSNativeCommandUseErrorActionPreference = $true`，非零退出会抛 `NativeCommandExitException`，先被通用 `catch` 吃掉并 `exit 1`。

修复（`75c876f`，[CommandWrapper.cs](../src/Cronlet/CommandWrapper.cs)）：`catch` 里先识别该异常并透传其 `ExitCode`，其余情况仍是打印消息后 `exit 1`。用类型名字符串比较而不是 `-is [类型]`，避免在缺少该类型的运行时上让判断本身失败。

验证：新增 `Native_exit_code_survives_native_error_preference`（`$PSNativeCommandUseErrorActionPreference = $true; cmd /c exit 7` 应得 7），原有的 `Native_exit_code_is_preserved` 覆盖开关关闭的情形；`PowerShell_failure_exits_one` 收紧为精确断言 1。

## 方案比较的回应

| 领域 | 处理 |
| --- | --- |
| 调度和重载 | 先在 `75c876f` 做局部修复（旧请求绑实例、超时恢复合并临界区）；**`c49f9bc` 落地单一调度状态锁**，见文末 |
| 配置解析 | 先在 `4dc3683` 采纳互斥 `script_file` / `command`；**`c49f9bc` 收敛为单一 `command`**，`script_file` 与 `ScriptResolver` 删除。不保留旧 `script` 兼容分支：仓库只有一处部署，已随迁移，兼容层只会让诊断重新变模糊 |
| PowerShell 包装 | 保留换行结构，补上原生退出异常识别。`$ErrorActionPreference='Stop'` 带来的"失败即停"语义写进了类注释，避免后来者把它当噪音 |
| 构建约束 | 暂不钉 SDK 版本。`.NET 11` 仍是 RC，钉死 `11.0.100-rc.3` 会让 CI 与本地在补丁版本上互相牵制；GA 之后再统一 `global.json` 与 CI，通过独立提交升级 |

审查建议的"可控进程完成信号与超时信号"测试夹具仍未引入。它需要把 `Scheduler` 的进程启动与时钟抽象出来，属于为测试重构生产代码，取舍留给你定。

## 回归结果（`75c876f` 当时）

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    40 通过 / 0 失败
```

测试数量：39 → 40（新增原生退出码用例，PowerShell 失败用例由非零改为精确 1）。

## 后续变更（`c49f9bc`）

审查的两条方案建议在后续提交里落地：

- **单一调度状态锁**：`_queueGate` / `_states` / `TaskState.Gate` / `_idleGate` / `_wakeGate` 合并为一个 `_gate`。锁内只做状态变更，`proc.Start()`、`await`、日志写入都在锁外。前面两个补丁（旧请求绑实例、超时恢复合并临界区）在单锁下变成自然的顺序。
- **单一 `command`**：`script_file` 与 `ScriptResolver` 删除，相对路径以 exe 目录为当前目录，脚本写作 `./scripts/xxx.ps1`。

回归结果随之变化：

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    32 通过 / 0 失败
```

测试数量 40 → 32：删除 `ScriptResolverTests`（4 项）与四类 `script_file` 用例，新增 `Empty_command`、`Custom_cwd`、`Command_with_spaces_is_not_split`。

运行验证：23:00:02 `smoke-test` 在部署环境跑通，`cwd=S:\local\app\cronlet\content`、中文输出正常。

仍未验证：托盘"重载配置"路径在单锁重构后没有实测——命令行没有触发入口，重启不等于重载。
