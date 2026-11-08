# Cronlet 代码审查回复

- 对应审查：`CODE_REVIEW_2026-11-08-19-45-20_1bac31.md`（提交 `1bac311`）
- 回复时间 2026-11-08 19:52 +08:00（香港时间）
- 处理方式：五项发现全部按建议修入工作区，尚未提交
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 41 项全部通过（原 25 项 + 新增 16 项）
- 后续变更：`4dc3683` 起改用显式 `command` 字段，`c49f9bc` 删除了 `ScriptResolver`；本文件里关于路径判定与 `script_file` 的描述只反映当时的实现

## [P2] 配置重载成功后仍会执行旧命令 —— 已修复

原问题：补跑路径只按名字判断任务是否仍启用，然后把**旧**的 `TaskConfig` 实例交给 `Fire`；重载可以在"计数减到零"与"补跑"之间完成配置替换。

修复（[Scheduler.cs](../src/Cronlet/Scheduler.cs#L350-L374)）：

- 删掉 `IsStillActive`，改为 `TryRerun`，只按名字从**当前** `_config` 里取任务实例；取不到就不补跑，因此不会再拿旧对象执行旧命令。
- `TryRerun` 与配置替换同处 `_queueGate` 临界区，并在锁内检查 `_reloading`：重载窗口内的补跑直接丢弃（重载本身会按新配置重排，不会漏跑）。
- 派发循环的 `while` 条件补上 `!_reloading`（[第 139 行](../src/Cronlet/Scheduler.cs#L139)），堵住"循环读了 `_reloading=false`、随后重载置位、任务却被派发"的窗口。这样重载成功后不会再冒出新的子进程，配置对象也不会跨版本泄漏。

锁顺序保持 `_queueGate → _states → TaskState.Gate → _idleGate`，与 `ReloadAsync`、`Stop` 现有顺序一致，没有引入反向获取。

未自动化：该竞态需要真实子进程配合时序，单元测试覆盖成本高，本次以代码审阅与不变量说明为准。

## [P2] PowerShell 命令失败时可能记录退出码 0 —— 已修复

原问题：`exit $LASTEXITCODE` 反映不了 cmdlet 的非终止错误。

修复：新增 [CommandWrapper.cs](../src/Cronlet/CommandWrapper.cs)，统一生成 `pwsh -Command` 的包装：设置 UTF-8 输出、`$ErrorActionPreference='Stop'`，用 `try/catch` 捕获后 `exit 1`；否则原生命令的 `$LASTEXITCODE` 非零则透传；两者都不成立才 `exit 0`。[Scheduler.BuildStartInfo](../src/Cronlet/Scheduler.cs#L376-L404) 的两个分支都改为调用它。

修复过程中确认了 `$?` 不能作为判据：`& 'fail.ps1'` 执行含非终止错误的脚本后，`$?` 仍为 `True`（已实测）。所以只能靠 `$ErrorActionPreference='Stop'` 把非终止错误提升为可捕获的终止错误；脚本里显式写了 `-ErrorAction SilentlyContinue` 的调用不受影响。

验证：新增 4 项集成测试（[CommandWrapperTests.cs](../tests/Cronlet/CommandWrapperTests.cs)），真实启动 pwsh：

| 用例 | 期望 | 结果 |
|---|---|---|
| `'hi'` | 0 | 通过 |
| `Get-Item <不存在路径>` | 非 0 | 通过 |
| `cmd /c exit 7` | 7 | 通过 |
| 失败脚本文件 | 非 0 | 通过 |

## [P2] 含 URL 或路径参数的合法命令被拒绝 —— 已修复

原问题：`LooksLikePath` 见到斜杠就判为路径，`Invoke-WebRequest https://example.com` 在配置校验阶段被报"找不到脚本"。

修复（[ScriptResolver.cs](../src/Cronlet/ScriptResolver.cs#L28-L40)）：判定顺序改为

1. 整体被引号包住 → 路径（显式声明意图），`Resolve` 同步去掉外层引号；
2. 含任意空白 → 命令（带参数），不再当路径；
3. 含 `://` → URL，当命令；
4. 含 `\` 或 `/` → 路径；
5. 以 `.ps1/.psm1/.cmd/.bat/.exe` 结尾 → 路径。

代价：路径里带空格的，必须用引号包住才会按路径解析（README 已有说明位置可补）。新增 7 个判定用例与 2 个 `Resolve` 用例，含 `Invoke-WebRequest https://…`、`https://…`、引号包裹的 `C:\Program Files\a.ps1`。

## [P2] 日志轮转会把半行 JSON 写回文件 —— 已修复

原问题：低水位窗口里只有一条被截断的超长记录时，裁剪条件不成立，半行被原样写回。

修复（[Log.cs](../src/Cronlet/Log.cs#L94-L142)）：把裁剪逻辑抽成纯函数 `RotateTail`——丢掉第一个换行符之前的残段，再丢掉最后一个换行符之后不完整的末行；窗口内没有完整记录时返回空数组（清空文件），不再写回半行。

验证：新增 4 项测试（[LogRotationTests.cs](../tests/Cronlet/LogRotationTests.cs)），覆盖只有一条超长记录、无换行、末尾半行、首尾都残缺四种情况。

## [P3] 超大 timeout 通过校验后在子进程启动后抛错 —— 已修复

原问题：`timeout = 5000000`（约 57.9 天）超过计时器可表达范围（约 49.7 天），校验通过、子进程启动后 `CancellationTokenSource` 抛 `ArgumentOutOfRangeException`。

修复（[ConfigValidator.cs](../src/Cronlet/ConfigValidator.cs#L7-L9)）：新增 `MaxTimeoutSeconds = 30 天` 上限并在校验阶段拒绝，超出即在启动/重载时报错，不再走到启动子进程之后。30 天距离计时器边界留了一半余量，语义上也够用。

验证：新增 2 项测试，`timeout = MaxTimeoutSeconds` 通过、`MaxTimeoutSeconds + 1` 被拒。

## 回归结果

```
dotnet build Cronlet.slnx -c Release   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release   41 通过 / 0 失败
```

审查记录里的"当前环境拒绝 `dotnet test` 命名管道"未在本次复现，测试通过 `Microsoft.Testing.Platform` 正常执行。新增的 `global.json` 已在 `1bac31` 中，无需额外处理。
