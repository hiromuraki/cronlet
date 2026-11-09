# Cronlet 修复复审回复

- 对应审查：`CODE_REVIEW_2026-11-08-20-08-39_f7345b.md`（提交 `f7345bf`）
- 回复时间 2026-11-08 20:23 +08:00（香港时间）
- 处理结果：五项发现全部修复
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 50 项全部通过（上一轮 41 项 + 新增 9 项）
- 后续变更：`c49f9bc` 之后 `script_file` 与 `ScriptResolver` 均已删除，本文件描述的路径与引号判定逻辑已不在仓库中

## [P1] 含空格的脚本路径无法运行 —— 已修复

根因是我上一轮把"含空白即命令"写进了判定入口，而执行入口又用这个判定决定要不要解析文件：`LooksLikePath(...) ? Resolve(...) : null`。于是一个已存在的含空格路径永远走不到 `Resolve`。

修复（[ScriptResolver.cs](../../src/Cronlet/ScriptResolver.cs#L7-L40) · [Scheduler.cs](../../src/Cronlet/Scheduler.cs#L404-L410)）：

- 执行入口改成**先解析**：`var file = ScriptResolver.Resolve(task.script);`，解析成功就是脚本，失败才当命令。`Resolve` 用 `Path.Combine` 拼接，不切分空白，含空格的相对路径和绝对路径都能命中。
- `LooksLikePath` 降级为"解析失败时是否值得报错"的判据，不再参与"是否当文件执行"的判断。
- 校验与执行由此使用同一个 `Resolve` 结果，不会再出现"校验跳过文件检查、执行也不查文件"的分叉。

验证：新增 `SchedulerTests.Path_with_spaces_is_validated_as_script`（临时目录含空格，走完 `ConfigValidator` → `BuildStartInfo`，断言选中 `ForScript` 且 `cwd` 为该目录）与 `ScriptResolverTests.Resolve_accepts_path_with_spaces_without_quotes`（不加引号、加引号都能解析）。

## [P2] 重载仍可能漏等已进入派发路径的旧任务 —— 已修复

根因：`_reloading` 在 `_queueGate` 之外置位，而 `_running` 的登记（`Fire` 里的 `_running++`）在锁内。重载可以在"派发已通过检查、尚未登记运行"的窗口里读到 `_running == 0`，误判空闲。

修复（[Scheduler.cs](../../src/Cronlet/Scheduler.cs#L59-L70)）：把 `_reloading = true` 移进 `_queueGate` 临界区。

这样就有了一个明确的不变量：**所有 `Fire` 都发生在持有 `_queueGate` 期间**（调度循环与 `TryRerun` 都是），因此重载拿到锁的那一刻，正在进行或即将进行的派发要么已经完成 `_running++`，要么被 `_reloading` 挡住。之后再释放锁、等空闲，就不会漏掉任何已进入派发路径的任务。置位后的 `finally` 同样在锁内复位。

未自动化验证：需要精确阻塞补跑路径来构造时序，单元测试成本高，本项以不变量与代码审阅为准。

## [P2] 重载超时会丢弃旧配置下已排队的运行 —— 已修复

修复（[Scheduler.cs](../../src/Cronlet/Scheduler.cs#L20-L21) · [第 392-412 行](../../src/Cronlet/Scheduler.cs#L392-L412)）：

- 新增 `_deferredReruns`。`_reloading` 期间结束的任务不再直接丢弃补跑请求，而是按任务名登记。
- 重载超时（旧配置继续生效）走 `RestoreDeferredRerunsLocked`，按旧配置把登记的补跑重新派发。
- 重载成功则清空登记——新配置会重新排队，旧的"立即补跑"语义作废，这符合审查建议。

未自动化验证：需要 30 秒真实超时叠加并发运行，本项由代码路径确认。

## [P2] 带行尾注释的内联命令解析失败 —— 已修复

根因：包装器把命令体、`try` 的闭合花括号放在同一行，`#` 注释吞掉了 `} catch { ... }` 及其后的包装代码。

修复（[CommandWrapper.cs](../../src/Cronlet/CommandWrapper.cs#L21-L27)）：命令体前后各插入一个换行，`try {`、命令体、`}` 各占一行，行注释只作用于自己那一行。

动手前先确认了前提：`pwsh -Command` 通过 `ProcessStartInfo.ArgumentList` 传递含 `\n` 的参数可以正常工作；同时复现了单行版本 `Write-Output ready # note` 退出 1、多行版本退出 0 的差异。

验证：新增 `CommandWrapperTests.Command_ending_with_line_comment_still_runs`。

## [P2] 单独的带引号表达式被当作缺失脚本 —— 已修复

根因：`LooksLikePath` 把"首尾引号配对"直接当作路径证据。

修复（[ScriptResolver.cs](../../src/Cronlet/ScriptResolver.cs#L28-L40)）：去掉外层引号后再按内容判断，引号本身不再是路径证据。判定顺序为：含 `://` → 命令；有脚本扩展名 → 路径；含空白 → 命令；含路径分隔符 → 路径；否则命令。

于是 `'hello'` 走内联命令分支（`Resolve` 返回 null、`LooksLikePath` 返回 false、校验不报错），`"C:\Program Files\a.ps1"` 仍是路径。

验证：新增 `SchedulerTests.Quoted_expression_is_validated_as_command`，经 `ConfigValidator` 与 `BuildStartInfo` 两个入口，而不是只调包装器；`ScriptResolverTests` 增加 `'hello'`、`"hello world"` 两个判定用例。

## 回归结果

```
dotnet build Cronlet.slnx -c Release   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release   50 通过 / 0 失败
```

测试数量变化：41 → 50，新增 `SchedulerTests.cs`（3 项）与 6 个用例（路径含空格、行尾注释、引号表达式等）。`dotnet test` 在本环境正常执行，未出现审查记录里提到的命名管道错误。

未自动验证项集中在两个重载时序修复上，已在对应条目中注明。
