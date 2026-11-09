# Cronlet 修复复审

- 审查提交 `f7345bf9d999096b04ae17498d279bc312edc269`（`main`）
- 审查开始时间 2026-11-08 20:08:39 +08:00
- 审查范围为 `1bac311..f7345bf` 的代码与测试变更，重点核对上一轮修复的行为。
- 审查开始时工作区干净。

## 发现

### [P1] 含空格的脚本路径无法运行

[ScriptResolver.cs 第 32–40 行](../../src/Cronlet/ScriptResolver.cs#L32-L40) · [Scheduler.cs 第 394–398 行](../../src/Cronlet/Scheduler.cs#L394-L398)

旧配置可直接引用含空格的脚本路径。现在，`LooksLikePath` 遇到空白就返回 `false`，导致校验跳过文件检查，执行时也不再查找文件。实测将现有脚本放入 `scripts/Daily Jobs/report.ps1`，配置为 `Daily Jobs\report.ps1` 或其绝对路径，两者均通过校验，运行时均退出 1，脚本未执行。

先尝试解析已存在的文件，再区分未找到的文件引用和内联命令。校验与执行应采用同一解析结果，避免旧配置需要额外添加引号才能运行。

### [P2] 重载仍可能漏等已进入派发路径的旧任务

[Scheduler.cs 第 59–85 行](../../src/Cronlet/Scheduler.cs#L59-L85) · [Scheduler.cs 第 230–262 行](../../src/Cronlet/Scheduler.cs#L230-L262) · [Scheduler.cs 第 362–374 行](../../src/Cronlet/Scheduler.cs#L362-L374)

重载在 `_queueGate` 外设置 `_reloading` 并检查运行计数。已持有 `_queueGate` 的派发可能通过检查，但尚未在 `Fire` 中增加 `_running`。此时重载会误判空闲，随后替换配置并清空任务状态。

定向复现先在 `Fire` 登记运行前阻塞旧任务的补跑，再发起移除该任务的重载。释放派发后，重载返回成功，此时运行计数仍为 1，日志记录了旧任务启动。

在 `_queueGate` 内暂停派发，确保已进入派发路径的任务完成登记后，再释放锁并等待空闲。

### [P2] 重载超时会丢弃旧配置下已排队的运行

[Scheduler.cs 第 68–74 行](../../src/Cronlet/Scheduler.cs#L68-L74) · [Scheduler.cs 第 333–366 行](../../src/Cronlet/Scheduler.cs#L333-L366)

代码路径显示，重载等待期间结束的任务会先清除 `Pending`，随后 `TryRerun` 因 `_reloading` 直接返回。若此时另一个任务持续运行到 30 秒超时，重载放弃，原配置继续生效，已清除的补跑却无法恢复。

等待期间保留补跑请求。重载成功后可按新配置处置，重载失败时应恢复原配置下的补跑。

### [P2] 带行尾注释的内联命令解析失败

[CommandWrapper.cs 第 21–25 行](../../src/Cronlet/CommandWrapper.cs#L21-L25)

包装器把命令体和 `try` 的闭合花括号放在同一行。内联命令以行注释结尾时，注释会吞掉闭合花括号和后续包装代码。实测 `Write-Output ready # note` 在原包装下输出 `ready` 并退出 0，在新包装下未执行并退出 1。

在命令体前后使用明确的换行边界，并添加覆盖行尾注释的回归用例。

### [P2] 单独的带引号表达式被当作缺失脚本

[ScriptResolver.cs 第 32–46 行](../../src/Cronlet/ScriptResolver.cs#L32-L46) · [ConfigValidator.cs 第 28–29 行](../../src/Cronlet/ConfigValidator.cs#L28-L29)

只要首尾引号配对，`LooksLikePath` 就认定输入是文件路径。实测把 `script` 配置为 PowerShell 表达式 `'hello'` 时，校验报脚本不存在。新增测试直接调用 `CommandWrapper.ForCommand("'hi'")`，跳过了实际入口的校验。

去除外层引号后仍需判断内容是否具有文件路径特征。回归用例应经过配置校验及执行入口。

## 验证

- Release 构建通过，0 个警告、0 个错误。
- 直接运行 Release 测试程序，41 项全部通过。`dotnet test` 因当前环境拒绝命名管道连接，未执行测试。
- 定向复现覆盖脚本路径、重载派发竞态、行尾注释和带引号表达式。重载超时丢失补跑由代码路径确认，未进行 30 秒集成测试。
