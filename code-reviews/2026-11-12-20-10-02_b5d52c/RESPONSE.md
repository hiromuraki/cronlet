# 回复：Cronlet 复审（b5d52c5）

- 对应审查记录：[REVIEW.md](REVIEW.md)，被审提交 `b5d52c512bc669ed86f873171931c3a75ca7a45b`（`main`）。
- 回复时间：2026-11-12 20:52 +08:00（香港时间）。
- 修复与本文档一并提交，提交说明引用本目录名。

| 编号 | 级别 | 结论 |
| --- | --- | --- |
| F1 | P2 | 已修复 |
| F2 | P3 | 未修复，见下方理由 |
| F3 | P3 | 未修复，见下方理由 |
| F4 | P3 | 已修复 |
| F5 | P3 | 已修复 |

## F1 已修复：RunNow 报告真实的启动结果

- 位置：[Scheduler.cs 第 238 行起](../../src/Cronlet/Scheduler.cs#L238-L274)（`FireLocked`）、[第 277 行起](../../src/Cronlet/Scheduler.cs#L277-L320)（`ExecuteAsync`）、[第 565 行起](../../src/Cronlet/Scheduler.cs#L565-L615)（`RunNow`）。
- 做法：`FireLocked` 增加 `out Task<Exception?>? startup`，只在真正尝试启动时给出与本次运行配对的 `TaskCompletionSource`。`ExecuteAsync` 在 `proc.Start()` 返回后完成它为 `null`，启动段抛异常时完成为该异常，`started == false`（已被停止或替换）时完成为一个明确异常。`RunNow` 在锁外最多等 1 秒：拿到异常就把结果改成 `Started=false` 并带上原因；超时按已触发处理（线程池忙时不再硬等）。
- `_gate` 的边界没有动：启动仍在 `_stopRequested`/`RunId` 复检的同一临界区内完成，上一轮修掉的启动竞态保持原样。等待发生在锁外，`ExecuteAsync` 才拿得到 `_gate`。
- 验证：新增 `SchedulerTests.RunNow_reports_a_failed_start`（pwsh 指向不存在的路径）。复跑审查探针得到 `started=False msg=启动 bad 失败：... 系统找不到指定的文件。`，日志随即出现 `task-error` 与 `task-end exit=-1`，与返回值一致。

## F2 未修复：托盘线程同步等待 `_gate`

- 根因是上一轮为消除启动竞态，把 `proc.Start()` 与 `_stopRequested`/`RunId` 复检、句柄登记放进同一个临界区。把 `proc.Start()` 移出锁要求同时补一条"发现已被停止或替换就杀掉刚起的进程"的路径，这是用 P2 级竞态换 P3 级延迟，不划算。
- `GetUpcoming` 改为读快照并不会减少锁内总开销：快照要么在每次派发时复制一次，要么仍在菜单打开时复制一次；前者只是把成本从交互路径挪到调度路径，代码却多一份需要同步的状态。
- 本轮新增的 `startup.Wait` 只作用于用户主动点击的那一次运行，上限 1 秒，正常路径仍在实测的 5–30 ms 量级。

## F3 未修复：重载窗口内的条目仍可点

- 让条目在重载期间不可点，需要把 `_reloading` 从 [Scheduler](../../src/Cronlet/Scheduler.cs#L95-L152) 传到 [TrayContext](../../src/Cronlet/TrayContext.cs#L75-L104)：要么新增一个跨模块状态源，要么把"是否可用"塞进 `NextRun`。为最长 30 秒的窗口引入一层需要同步的状态，与"最小化可变状态"相冲突。
- 当前行为是明确的：点击返回"配置正在重载，未执行"，并且用 Warning 气泡显示。若实际用起来仍然碍事，再按维护者的选择加 `IsDisabled`。

## F4 已修复：超长行只截断一次

- 位置：[LineAssembler.cs 第 66 行起](../../src/Cronlet/LineAssembler.cs#L66-L71)、[Log.cs 第 41 行起](../../src/Cronlet/Log.cs#L41-L57)。
- 做法：`LineAssembler.TakeLine` 不再调用 `Log.TruncateLine`，只返回容量内的原文（容量仍是 `maxBytes + 前瞻`）；`Log.TaskOutput` 成为全程序唯一的截断点，截断标记也只由它加一次。
- 不丢信息：容量是 `maxBytes` 个字符再加 4096 字符前瞻，而 UTF-8 下每个字符至少 1 字节，所以任何被容量截掉尾巴的行，其保留部分本身就超过 `maxBytes` 字节，下游必然判定超限并按字形簇截断。前瞻仍覆盖截断点之后的完整簇。
- 验证：新增 `LineAssemblerTests.Truncation_happens_once_in_the_log_layer`（组装器输出 20 个 `a`，`Log` 截成 8 个加标记）；三个字形簇用例改为对组装器输出调用 `Log.TruncateLine`，结论不变。复跑探针，"double truncation"一节里组装器输出已变为 `assemblerOutBytes=12288` 的原文，第二遍截断不再出现在生产路径上。

## F5 已修复：删除测试专用包装与未使用属性

- 删除 `LineAssembler.MaxBytes`（[LineAssembler.cs 第 21 行](../../src/Cronlet/LineAssembler.cs#L19-L24) 的构造参数仍在用，只是不再暴露）。
- 删除 `Log.RotateTail`；[LogRotationTests](../../tests/Cronlet/LogRotationTests.cs#L6-L16) 改为取 `RotateRange` 返回的 `(offset, count)` 自行切片。

## 待确认项

1. **KeySelect 不弹菜单**：已确认。DotTray 3.0.0 的 `PopupMenuHandler.HandleInteraction` 只处理 `Select`、`ContextMenu`、`PopupOpen`、`PopupClose`。已从 [TrayContext.OnInteracted](../../src/Cronlet/TrayContext.cs#L62-L69) 删掉 `KeySelect` 分支及注释里的错误说法。
2. **`upcoming` 没有上限**：不修。实测 10,000 个任务每次菜单准备 0.756 ms / 454 KB，现实规模远达不到；加上限属于给未出现的需求加配置。
3. **标签与 `<Version>` 没有一致性校验**：不修。这是新的 CI 需求，不在本轮审查范围内；需要做时单独提。
4. **首簇本身超过 8192 字节时整行只剩标记**：不修。这是 `TruncateLine` 的既定边界行为（`cut=0`），本轮没有改变，是否保留一个标量由维护者决定。
5. **旧 Release 的资产名**：不修。改名前的 Release 不存在，`--clobber` 不清理旧名资产的风险没有实际载体；真要统一时再处理。

## 构建与测试

- `dotnet build Cronlet.slnx -c Release --nologo`：成功，0 警告、0 错误，12.87 秒。
- `dotnet test Cronlet.slnx -c Release --nologo`：99 通过 / 0 失败 / 0 跳过，13.04 秒。测试数 98 → 99（新增 `RunNow_reports_a_failed_start`；`LineAssemblerTests` 的用例改写后数量不变）。
- 审查探针 `scripts/run-probe.ps1` 复跑通过：启动失败路径返回 `started=False`；轮转 17,825,792 → 8,388,608 字节、分配 8,390,448 字节；`GetUpcoming` 100/10,000 任务 0.097/0.756 ms；2000 条日志 872 ms。

## 未自动验证项

- 真实桌面上的托盘行为（气泡文案、菜单刷新时机、左键与右键）。
- 本机缺 `vswhere.exe`，NativeAOT 发布仍未验证；框架依赖单文件发布未在本次重跑。
- `RunNow` 等待启动结果时与调度器抢 `_gate` 的实际延迟，只按探针的热态数据（5–30 ms）推断。
