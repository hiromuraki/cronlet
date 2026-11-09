# Cronlet 整库与 DotTray 分支审查

- 审查提交 `d49360556e5661a313c97bd4cb448b378add02c2`（`try/dottray`）
- 审查开始时间 2026-11-09 05:51:13 +08:00（香港时间）
- 审查范围为当前整库及相对 `main` 的分支差异。覆盖配置、启动、单实例、托盘、重载、调度、进程清理、日志和发布。
- 审查开始时工作区干净，既有审查均有对应回复。

## 发现

### [P2] 任务超时后仍可遗留进程，后续任务可继续派发

[Scheduler.cs 第 285–317 行](../../src/Cronlet/Scheduler.cs#L285-L317) · [第 337–353 行](../../src/Cronlet/Scheduler.cs#L337-L353) · [ChildJob.cs 第 14–23 行](../../src/Cronlet/ChildJob.cs#L14-L23) · [README 超时约定](../../README.md#L28)

配置 `timeout = 2`，任务通过 `Start-Process -NoNewWindow` 启动中间 PowerShell，中间进程再启动一个继承输出管道、休眠 75 秒的 PowerShell。两层启动进程都先退出。通过无界面程序实际按分钟派发时，日志约在 7 秒后记录 `task-end`，同时显示 `exit: 0` 和 `timeout: true`。此时末级进程仍然存活，宿主退出后才结束。对照试验只有一层子进程时，超时后子进程已退出。

任务超时目前依靠从原 PowerShell 枚举进程树。中间进程退出后，超时路径未能找到仍在运行的末级进程。宿主级 Job 仍然持有该进程，但只能在宿主退出时兜底。调度器已清除运行名额，因此重复触发时可能累积残留进程，重载也会将该任务视为空闲。此场景与先前已接受的「加入 Job 前派生」窗口不同，实测末级进程随宿主的 Job 一同结束。

建议按每次运行持有独立 Job，供 `timeout`、`overlap = "kill"` 和停止路径终止对应进程组。要兑现完整进程树保证，应在命令开始运行前完成 Job 绑定。若保留尽力清理的取舍，应收紧 README 的承诺，并在清理无法确认时记录独立状态。[Windows Job 对象](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)

### [P2] 任务输出上限只约束日志，超长单行仍显著抬高宿主内存

[Scheduler.cs 第 265–294 行](../../src/Cronlet/Scheduler.cs#L265-L294) · [Log.cs 第 41–50 行](../../src/Cronlet/Log.cs#L41-L50)

调度器用 `BeginOutputReadLine` 和 `BeginErrorReadLine` 接收完整行，日志截断在回调之后才执行。先前资源审查已指出这个入口，本轮通过完整调度路径量化。测试任务每次写 64 KiB，共输出 64 MiB ASCII，等待两秒后才写换行。约每 90 毫秒采样一次，宿主私有提交内存由约 9.8 MiB 升到无换行阶段约 162.8 MiB，整体采样峰值约 421 MiB。日志最终只写入 8,192 字节正文和截断标记。持续输出无换行数据时，读取阶段没有相应的内存上限。[.NET 行读取实现](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/AsyncStreamReader.cs)

建议两路并发分块排空，每行只累积有界前缀，超过限额后持续丢弃到换行或 EOF。保留 UTF-8 与字形边界语义，并覆盖跨块 CRLF、无换行 EOF 和两路同时输出。仅继续优化 `Log.TruncateLine` 无法约束前置缓冲。

### [P2] 配置键拼写错误可静默停掉全部任务或启用本应关闭的任务

[Config.cs 第 14–34 行](../../src/Cronlet/Config.cs#L14-L34) · [第 82–92 行](../../src/Cronlet/Config.cs#L82-L92) · [Program.cs 第 46–65 行](../../src/Cronlet/Program.cs#L46-L65)

无界面启动实测，将 `[[tasks]]` 写成 `[[task]]` 时进程继续运行，启动日志为 `tasks: 0`。在正确的 `[[tasks]]` 中将 `enabled = false` 写成 `enabeld = false`，日志为 `tasks: 1`，因为默认值是启用。同一加载路径也用于重载。现有主分支发布样本同样复现了任务表拼错后启动为零任务的行为。

建议在保留 Tomlyn 源生成绑定的前提下校验根表与任务表的未知键，错误信息指出键及任务位置。省略可选字段继续使用默认值，显式 `tasks = []` 继续表示无任务。

### [P3] 常规 CI 构建和测试继承了 Release 写权限

[build.yml 第 3–22 行](../../.github/workflows/build.yml#L3-L22) · [第 57–67 行](../../.github/workflows/build.yml#L57-L67)

分支把 `contents: write` 设在工作流级，发布步骤的分支判断只控制该步骤是否执行。对获得可写令牌的运行，构建、测试和发布前的第三方 Actions 同处可写作业，且 checkout 默认持久化 Git 凭据。建议把构建、测试和产物上传恢复为只读，独立发布作业仅在目标分支获得写权限并下载已构建的 AOT 产物。[GitHub Actions 权限文档](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#permissions) · [checkout 凭据配置](https://github.com/actions/checkout#usage)

### [P3] README 仍将托盘实现写成 WinForms

[README 第 3 行](../../README.md#L3) · [第 79 行](../../README.md#L79) · [项目依赖](../../src/Cronlet/Cronlet.csproj#L20-L25)

项目简介和目录结构仍将主程序标为 WinForms。分支已使用 DotTray。合并时应同步这两处，并标明框架依赖单文件与 NativeAOT 包的区别。

## 复核与取舍

- 用上一轮的外部写端用例复测。普通取消 1 次、强制原竞争顺序 6 次、自然超时 1 次，均返回失败，随后写端均返回 Win32 错误 232。上一轮发现已关闭。
- DotTray 和 Tomlyn 源生成可作为分支基础。本轮影响最大的是调度器对进程组和输出缓冲的管理，换托盘库不会改变这两条路径。日志轮转在低水位窗口上仍有数组复制，优先级低于任务输出的无上限缓冲。
- 当前可以继续作为实验分支试用。作为默认发布前，优先解决任务超时残留与输出内存上限，再用目标环境验证原生包及托盘启动、重载、退出。

## 构建与测试

- Release 解决方案构建通过，0 警告、0 错误。直接运行 xUnit 测试可执行文件，74 项全部通过。
- Windows x64 框架依赖单文件发布通过。仅复制 `Cronlet.exe` 到独立目录即可无界面启动并生成默认配置。第二实例退出码为 0，日志记录 `already-running`。
- NativeAOT 发布完成依赖还原，因本机缺少 `vswhere.exe` 中止。未验证最新 GitHub Actions 原生产物、桌面托盘点击、Explorer 重启或跨会话单实例。本轮的单实例实测限于同目录、同会话。
