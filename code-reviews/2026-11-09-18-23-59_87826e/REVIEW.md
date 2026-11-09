# Cronlet 主分支复审

- 审查提交 `87826ebd38baf9d77dbf96097457f80580ecbdbe`（`main`）
- 审查开始时间 2026-11-09 18:23:59 +08:00（香港时间）
- 开始时工作区干净，本地 `main` 比本地跟踪引用 `origin/main` 多 3 个提交。实验分支的最后一个实现提交 `d02f860` 已进入 `main`。
- 先检查 `7205766..87826eb` 的差异和[上一轮回复](../2026-11-09-06-29-25_0f746f/RESPONSE.md)，再检查配置、启动、单实例、托盘、调度、子进程、日志和发布流程。

## 上一轮修复复核

| 项目 | 结论 |
| --- | --- |
| 任务超时 | 通过。保持 stdout 和 stderr 写端开启并让启动进程退出，两次均在约 1.1 秒完成 1 秒超时路径，记录 `timeout: true, exit: -1`。 |
| 管道读端释放 | 通过。两次在 `task-end` 后分别向两路写端写入，均失败并返回 Win32 错误 232。 |
| Job 句柄同步 | 通过代码复核。`Terminate` 和 `Dispose` 在同一把锁内使用句柄，原有关闭和复用竞争窗口已消除。 |
| 字形边界 | 部分通过。原来的 `e` 加 U+0301 用例通过，其他 Unicode 边界仍有复现。 |

## 发现

### [P3] 截断仍可拆开含补充平面组合符或 ZWJ 的字形簇

方向　缺陷、实现

[LineAssembler.cs 第 51–85 行](../../src/Cronlet/LineAssembler.cs#L51-L85) · [Log.cs 第 59–84 行](../../src/Cronlet/Log.cs#L59-L84)

`LineAssembler` 到达上限后只按单个 UTF-16 码元判断三类组合标记。用 8,191 个 `a` 加 `e`，再分块输入 U+1D165 或 U+200D，随后接 `z` 和换行，最终都保留了 `e`。对原始完整行执行 `Log.TruncateLine` 则会丢弃整个跨界簇。第二次截断拿到的文本已丢失边界后的字符，无法补救。

建议保留截断点后的有界前瞻，至少读完下一个 Unicode 标量，再用同一套字形簇分割规则选择截断位置。避免在 `LineAssembler` 维护组合符分类，同时在 `Log` 维护另一套边界规则。复现见 [Probe.cs](scripts/Probe.cs)。

### [P3] 重跑已成功发布的工作流会使 Release 作业失败

方向　缺陷、实现

[build.yml 第 59–83 行](../../.github/workflows/build.yml#L59-L83)

合并后 `main` 每次成功运行都会尝试创建 `aot-<run_number>` 预发布。同一运行被重新执行时，`run_number` 保持不变。若首次已发布，`gh release create` 带附件遇到同标签的已发布 Release 会直接报错，导致重新构建和测试通过后，工作流仍以失败结束。建议重复执行时校验已存在 Release 的标签指向，确认目标提交和附件后复用，并让不匹配的情况明确报错。此项由工作流与上游实现确认，未触发远程发布。[GitHub Actions 上下文](https://docs.github.com/en/actions/reference/workflows-and-actions/contexts#github-context) · [GitHub CLI 实现](https://github.com/cli/cli/blob/trunk/pkg/cmd/release/create/create.go#L463-L477)

### [P3] 轮转 8 MiB 日志窗口时产生约 48 MiB 临时分配

方向　资源管理、实现

[Log.cs 第 177–207 行](../../src/Cronlet/Log.cs#L177-L207) · [第 227–251 行](../../src/Cronlet/Log.cs#L227-L251)

将由 4 KiB 完整 JSONL 记录构成的 17 MiB 文件轮转为 8 MiB，实测调用线程分配 50,334,584 字节。窗口经过可增长的 `MemoryStream`、`ToArray()` 与切片复制，且轮转发生在日志锁内。这是单次累计分配量。建议按窗口长度一次读入，按索引找出完整行区间，直接将该区间写入临时文件，再沿用现有替换逻辑。复现见 [Probe.cs](scripts/Probe.cs)。

## 整体复核与验证

- 缺陷审查覆盖 TOML 校验、单实例、启动与重载、调度及发布。实现审查检查托盘与探测的退出路径、任务输出和日志轮转。资源审查复测进程管道，检查 Job 句柄、输出缓冲上限和轮转临时文件。
- 新审查目录及兄弟目录链接按当前 `AGENTS.md` 记录。历史 Markdown 的相对链接检查中，15 条未解析链接均指向已删除的 `ScriptResolver.cs` 或 `TomlConfig.cs`。
- Release 解决方案在关闭 Tomlyn 默认反射时构建通过，0 警告、0 错误。直接运行 xUnit 测试可执行文件，88 项全部通过。[复现脚本](scripts/run-probe.ps1)使用同一 Release 程序集，临时产物写入 `artifacts/review-main/`。
- Windows x64 NativeAOT 发布完成还原，因本机缺少 `vswhere.exe` 中止。未实测当前提交的 CI 发布、桌面托盘或已部署程序。
