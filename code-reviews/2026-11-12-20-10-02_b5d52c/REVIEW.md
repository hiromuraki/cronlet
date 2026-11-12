# Cronlet 复审（b5d52c5）

- 审查提交：`b5d52c512bc669ed86f873171931c3a75ca7a45b`（`main`）
- 审查开始时间：2026-11-12 20:10:02 +08:00（香港时间）
- 审查范围：`87826eb..b5d52c5` 共 10 个提交（`868367a`、`8ef9075`、`3363052`、`b782775`、`ad92c69`、`3c9ceed`、`dd81e87`、`508af67`、`e2db943`、`b5d52c5`）的差异；并整库复核 [src/Cronlet](../../src/Cronlet) 全部 `.cs`、[tests/Cronlet](../../tests/Cronlet)、[app.toml](../../app.toml)、[README.md](../../README.md)、[.github/workflows/build.yml](../../.github/workflows/build.yml)、[publish.ps1](../../publish.ps1)、[Cronlet.slnx](../../Cronlet.slnx)、[Directory.Build.props](../../Directory.Build.props)、[Directory.Build.targets](../../Directory.Build.targets)、[global.json](../../global.json)、[.editorconfig](../../.editorconfig)。
- 审查开始时工作区干净：`git status --porcelain` 无输出，`## main...origin/main`。`app.toml`、`build-icon.ps1`、`runner-preview.png` 从 `afea9eb` 起被 `.gitignore` 忽略，`publish.ps1` 更早（`34388bf`）就被忽略；它们仍是工作区中的本地文件，本次按本地文件复核。
- 上一轮记录：[REVIEW.md](../2026-11-09-18-23-59_87826e/REVIEW.md)、[RESPONSE.md](../2026-11-09-18-23-59_87826e/RESPONSE.md)。

## 上一轮修复复核

| 项目 | 结论 |
| --- | --- |
| 截断仍可拆开字形簇 | 通过。去掉 `LineAssembler` 自己的组合字符分类，统一走 `Log.TruncateLine`；缓冲区上限是 `8192 + 4096` 字符，多出的 4096 字符是前瞻，足以覆盖截断点后的完整簇，实际也观察不到拆簇。 |
| 重跑已发布的工作流会使 Release 作业失败 | 通过。改为 `gh release view` 存在则 `gh release upload --clobber`，不存在才 `create`；v0.2.1–v0.2.3 的标签运行都成功。 |
| 轮转 8 MiB 窗口产生约 48 MiB 临时分配 | 通过。一次读入窗口、按区间直接写出；17 MiB 文件实测分配 8,390,448 字节（上一轮 50,334,584 字节）。 |

**截断**：探针覆盖 BMP 组合符、补充平面 U+1D165、ZWJ、家庭 emoji（GB11）、国旗（区域指示符）、Prepend、Hangul、CJK 和组合重音，分别以 1/7/4096 字符分块喂入；行组装结果与整行 `Log.TruncateLine` 完全一致，无 U+FFFD；保留前缀都在 8192 字节以内（含 `…[truncated]` 标记的整串为 8205 字节）。只有单个簇本身超过 4096 字符、且起点又落在 8192 字节以内时，才可能“看到半簇”；此时该簇的可见部分已经有至少 4096 字节，必然超出截断点的剩余预算，`TruncateLine` 会整簇丢弃而不是误保留。能完整落在缓冲区里的簇则能被完整判断，所以不会出现拆簇。测试侧有三个对应用例（`U+0301`、`U+1D165`、ZWJ）。残留问题见 F4 与“待确认”第 4 条。

**Release**：当前作业在标签运行中按 `cronlet-windows-amd64.exe` / `cronlet-windows-amd64-aot.exe` 上传，[build.yml 第 81–104 行](../../.github/workflows/build.yml#L81-L104)。只读查证：v0.2.1、v0.2.2、v0.2.3 的标签运行均为 success，资产名与新脚本一致；v0.2.0 的标签运行在 3c9ceed 之前因两个 `Cronlet.exe` 同名而失败（失败日志最后是 `HTTP 404 ... name=Cronlet.exe`），正好对应被审的修复目标。上一轮 RESPONSE 里写的“标签 SHA 不匹配就报错”在后续 `3363052`/`ad92c69` 中已被替换掉，现在不校验已有 Release 指向的提交；对“同一运行重跑不失败”这个目标没有影响，但回复记录与最终代码不再一一对应。重跑本身没有实际触发（需要写 Release 权限）。

**轮转**：17,825,792 字节文件轮转为 8,388,608 字节，2,048 条完整 JSON 记录，`.rotating` 临时文件已清理；分配量只剩一份窗口（8 MiB + 1 字节）加少量对象。每次轮转仍会在 LOH 上分配 8 MiB，这是窗口大小本身，不是新的缝隙；若要进一步降低，可考虑 `ArrayPool<byte>`，但属于优化。

## 发现

### [P2][F1] RunNow 在进程真正启动前就返回“已启动”

方向　缺陷（接口语义、错误处理）

[`Scheduler.RunNow`](../../src/Cronlet/Scheduler.cs#L547-L577) · [`FireLocked`](../../src/Cronlet/Scheduler.cs#L235-L268) · [`ExecuteAsync` 启动段](../../src/Cronlet/Scheduler.cs#L294-L303) · [异常分支](../../src/Cronlet/Scheduler.cs#L352-L355) · [`TrayContext.RunNow`](../../src/Cronlet/TrayContext.cs#L110-L119)

触发条件：配置校验通过后 `pwsh` 被删除、替换成不可执行文件，或 `Process.Start` 因权限、句柄等原因失败；在托盘菜单点击任一带“▶”的条目。

实际结果：`FireLocked` 只把 `ExecuteAsync` 排进线程池就返回 `null`，`RunNow` 立即构造出 `Started=true` / “已启动 X”。真正的 `proc.Start()` 在 `ExecuteAsync` 里执行，失败时只写 `task-error` 与 `task-end (exit=-1)`，用户看到的是成功气泡。探针用不存在的 pwsh 路径复现：`started=True msg=已启动 bad`，随后日志为 `task-error ... 系统找不到指定的文件` 和 `task-end exit=-1`。这与 [`RunNowResult` 注释“是否真的启动了”](../../src/Cronlet/Scheduler.cs#L540-L541) 不符；同类问题在启动校验上曾被记为 P2（PowerShell 存在但无法运行时仍报告成功，见 [2026-11-09-02-03-13 审查](../2026-11-09-02-03-13_24faeb/REVIEW.md)）。

修复建议：让“启动成功/失败”成为可观测结果。最小做法是 `FireLocked` 返回一个 `TaskCompletionSource`，在 `proc.Start()` 成功或抛异常时完成，`RunNow` 用它决定返回值和气泡；或者把字段语义改成“已触发”，气泡不再断言已启动，并在 `ExecuteAsync` 启动失败时通过回调通知托盘。不要为此把 `proc.Start()` 挪出 `_gate`，除非同时重新检查 `_stopRequested`/`RunId`（那正是上一轮修掉的竞态）。

### [P3][F2] 托盘线程同步等待调度锁并同步写日志

方向　实现、资源管理（阻塞）

[`TrayContext.OnInteracted`](../../src/Cronlet/TrayContext.cs#L62-L69) · [`RefreshUpcoming`](../../src/Cronlet/TrayContext.cs#L75-L104) · [`TrayContext.RunNow`](../../src/Cronlet/TrayContext.cs#L110-L119) · [`Scheduler.RunNow`](../../src/Cronlet/Scheduler.cs#L547-L577) · [`ExecuteAsync` 持锁启动进程](../../src/Cronlet/Scheduler.cs#L294-L303) · [`FireLocked` 持锁终止旧进程](../../src/Cronlet/Scheduler.cs#L244-L259)

触发条件：打开菜单或点击条目时，调度线程正好持有 `_gate`——例如另一个任务正在 `proc.Start()`，或 `overlap="kill"` 正在 `Kill(true)`；点击后 `Log.Event("run-now")` 还可能排在一次 8 MiB 日志轮转（持 `Log.Gate`）后面。

实际结果：`Interacted` 与 `Clicked` 都在 DotTray 的后台 STA 线程上同步执行；`GetUpcoming`、`RunNow` 都要拿 `_gate`，菜单弹出、点击响应和气泡因此被调度操作串行化。本机实测 `pwsh` 的 `Process.Start()` 为 5–25 ms（冷启动、杀毒扫描、网络路径会更长）；日志轮转是一次 8 MiB 读加 8 MiB 写。没有发现死锁或重入：锁序只有 `_gate → Log.Gate`，`FireLocked` 按约定在持 `_gate` 时调用且不再取锁，`RunNow` 也不重入 `_gate`。风险主要是：托盘线程的响应时间被别人的进程启动/终止和日志 IO 决定，DotTray 的 `Dispose` 又会 `Join` 这条线程，极端情况下退出也要等它。

修复建议：托盘回调只做 UI 工作。最直接的是缩短 `_gate` 的持有时间：把 `proc.Start()` 放到锁外完成，启动前后都用 `RunId`/`_stopRequested` 复检，发现已被停止或替换就立刻杀掉刚起的进程（这正是上一轮把启动放进锁内要解决的竞态，改动时必须一并保留复检）；`GetUpcoming` 可以维护一份锁内更新的快照，让菜单读快照而不是每次取 `_gate`。若要把整个 `_runNow` 移到线程池，气泡仍应在托盘线程显示——DotTray 的 `ShowBalloon` 是“写一个字段再 `PostMessage`”，没有显式的跨线程同步，不能直接假定可从任意线程调用。

### [P3][F3] 重载等待期间新条目仍可点，但必然失败

方向　缺陷（配置热重载与 RunNow 交互）、使用体验

[`ReloadAsync`](../../src/Cronlet/Scheduler.cs#L95-L152) · [`RunNow` 的重载判断](../../src/Cronlet/Scheduler.cs#L552-L560) · [`RefreshUpcoming`](../../src/Cronlet/TrayContext.cs#L75-L104)

触发条件：某个任务正在运行；点“重载配置”；在 `ReloadAsync` 等待该任务结束的窗口内（最长 30 秒）打开托盘菜单并点任一 upcoming 条目。

实际结果：`_reloading` 在整个等待期间为 `true`，`RunNow` 返回 `(false, "配置正在重载，未执行")`；但菜单仍把条目显示为可用，README 的“点其中一条会立刻触发”在这个窗口不成立。等满 30 秒后重载放弃，条目又可用。

修复建议：让托盘的 upcoming 列表反映重载状态——重载期间返回空列表，或让条目 `IsDisabled=true` 并在文本/气泡里说明；也可以给菜单暴露一个 `IsReloading` 状态。不要在没有重新校验 `_running == 0` 的情况下放开重载期间的 `RunNow`。

### [P3][F4] 超长行在写日志前被截断两次

方向　实现（重复实现）、资源管理（多余分配）

[`LineAssembler.TakeLine`](../../src/Cronlet/LineAssembler.cs#L67-L72) · [`Log.TaskOutput`](../../src/Cronlet/Log.cs#L41-L57) · [`Log.TruncateLine`](../../src/Cronlet/Log.cs#L63-L85)

触发条件：任意 stdout/stderr 行的 UTF-8 字节数超过 8192，且保留前缀接近上限（常见的 ASCII 长行就是这种）。

实际结果：`TakeLine` 先截断并拼上 `…[truncated]`，结果至少 8206 字节（> `MaxLineBytes`）；`TaskOutput` 再调一次 `TruncateLine`，因为超限必然进入逐簇扫描，重新分配一个字符串并再次生成同样的标记。探针测到第二遍每次分配 16,432 字节，内容与第一遍完全相同。`LineAssembler` 与 `Log` 两套“截断并加标记”的职责仍重叠。

修复建议：只保留一个所有者。比如让 `LineAssembler` 只返回“有界原文 + truncated 标志”，由 `TaskOutput` 统一格式化标记；或者让 `TaskOutput` 信任来自 assembler 的已截断文本（其他调用方仍需截断，需要明确约定）。也可以让 `TruncateLine` 识别已带标记的输入并直接返回，避免第二遍扫描。

### [P3][F5] 生产代码里残留测试专用包装和未使用属性

方向　实现（只为测试存在的生产代码、死代码）

[`Log.RotateTail`](../../src/Cronlet/Log.cs#L260-L265) · [`LineAssembler.MaxBytes`](../../src/Cronlet/LineAssembler.cs#L25)

触发条件：不适用，这是静态的代码结构问题。

实际结果：`RotateTail` 只被 [`LogRotationTests`](../../tests/Cronlet/LogRotationTests.cs#L11-L33) 调用，生产路径只用 `RotateRange`；它是上一轮为“原有测试继续有效”保留的薄包装。`LineAssembler.MaxBytes` 只在构造函数里赋值，源码和测试都没有读取。两者分别对应“不为测试便利向生产代码加成员”和死代码。

修复建议：删掉 `MaxBytes`；测试直接断言 `RotateRange` 返回的 `(offset, count)`（或让测试自己切片），删掉 `RotateTail`。如果 F4 的统一截断需要 assembler 暴露上限，则保留 `MaxBytes` 并在 `TaskOutput` 使用它。

## 待确认

1. **键盘选择不会弹菜单，但代码按会弹来刷新。** [`OnInteracted`](../../src/Cronlet/TrayContext.cs#L62-L69) 的注释说 `Select`/`ContextMenu`/`KeySelect` 三者都会弹菜单；DotTray 3.0.0 的 `PopupMenuHandler.HandleInteraction` 只处理 `Select`、`ContextMenu`、`PopupOpen`、`PopupClose`，`KeySelect`（NIN_KEYSELECT）不弹菜单（[DotTray 3.0.0 源码](https://github.com/BlyZeDev/DotTray/blob/85a17f844f65803ba5c11072ff6e3b0e020e92f6/DotTray/PopupMenuHandler.cs)）。因此 `KeySelect` 分支只会白重建一次条目。需在真实桌面确认；若属实，删掉该分支或在托盘层自行处理键盘。
2. **`upcoming` 没有上限，`GetUpcoming` 每次交互排序全部任务。** [`ConfigValidator`](../../src/Cronlet/ConfigValidator.cs#L19-L22) 只拒绝 `< 1`；[`GetUpcoming`](../../src/Cronlet/Scheduler.cs#L527-L538) 把队列全部投影、排序后再 `Take(count)`。实测 100 个任务 0.067 ms / 5 KB，10,000 个任务 1.6 ms / 467 KB（每次菜单打开）。现实规模可接受；是否需要上限（或改成有界选择）由维护者决定。
3. **标签与 `<Version>` 没有一致性校验。** [`Cronlet.csproj`](../../src/Cronlet/Cronlet.csproj#L9) 是 0.2.3，[`build.yml` 的 Release 作业](../../.github/workflows/build.yml#L81-L104) 只用 `github.ref_name` 做标题和资产名；如果打 `v0.2.4` 时忘了改 csproj，发布出的二进制悬浮提示仍显示 0.2.3。是否加一条 CI 检查由维护者决定。
4. **首簇本身超过 8192 字节时整行只剩标记。** 探针用 `'a' + 5000 × U+0301`（10,001 字节，一个簇）得到 `…[truncated]`，连开头的 `a` 也没保留。这是 `TruncateLine` 的边界行为（`cut=0`），不是本轮引入；是否需要至少保留一个标量由维护者决定。
5. **旧 Release 的资产名。** 若某个同标签的 Release 是改名前建的（旧资产名 `Cronlet.exe`），`gh release upload --clobber` 只会新增/覆盖新名字，旧资产会留下。当前 CI 的 `v*` 发布都是改名后创建的，未观察到该情况，故列为待确认。

## 整体复核与验证

### 缺陷复核

- 配置：`upcoming` 的默认值、解析、校验（`< 1` 报错）、默认模板与 README 四处一致；未识别的键仍会报错；热重载会用新值重建托盘条目（下次交互时生效）。
- 调度：入队、jitter 上限、重叠策略、`RunNow`、`GetUpcoming`、重载/停止的状态机逐路径看过。`RunNow` 在重载、停止、运行中（skip/queue/kill）的返回值和 `_running` 计数一致；重载等待期间不会启动新任务（`_reloading` 同时挡住调度循环的派发和 `RunNow`），因此“等到 `_running == 0` 再替换配置”的不变量成立。
- 托盘：`Interacted` 在 DotTray 构建菜单之前触发，所以本次刷新生效；条目点击按名字查找任务，重载后同名任务会指向新配置。发现 F1–F3。
- 发布：`v*` 标签条件、两个产物的下载/改名/上传、`--clobber` 复用路径逐行看过；未发现新的功能性缺陷（见上一轮复核和“待确认”第 3、5 条）。
- 日志：截断、轮转区间、临时文件替换、`DebugWrite` 的失败路径看过；未发现新的数据损坏路径。

### 实现复核

- 托盘：每次交互重建 upcoming 条目（`ObservableCollection.Insert/Remove`），条目数不超过 `min(upcoming, 队列条目数)`；托盘本身没有为测试加开关（测试专用成员见 F5），条目对象没有原生句柄，移除后由 GC 回收。实测菜单数据准备（`GetUpcoming`）在 100/10,000 个任务下分别为 0.067/1.6 ms，现实规模不构成瓶颈。
- 截断：统一到 `Log.TruncateLine` 后，`LineAssembler` 不再维护第二套组合字符分类；但 `TaskOutput` 仍然二次截断（F4），并留下测试专用 `RotateTail` 与未使用属性（F5）。
- 发布：只用 `gh release view` + `--clobber` 一套机制，没有再出现上一轮“先查标签 SHA”那套已删除的代码。
- 本地部署文件：`app.toml` 的 `type/entry/uuid/related_links` 与 `publish.ps1` 的 `S:\local\app\cronlet` 布局一致；`publish.ps1` 只发布框架依赖单文件版并复制 `app.toml`/`runner.ico`，未随本轮改动。两者均被 `.gitignore` 忽略，新克隆的仓库里不存在，部署时需另行准备。
- 构建配置：`Cronlet.slnx` 两个项目、`Directory.Build.props/targets` 的 `UseArtifactsOutput` 与文档文件设置、`global.json` 的 Microsoft.Testing.Platform、`.editorconfig` 的分析器/格式规则均未随本轮改动。
- 测试：新增用例覆盖 `upcoming` 的解析/校验/截取数量和 `RunNow` 的未知/禁用/停止/启动；轮转测试仍走 `RotateTail`；托盘与工作流没有自动化测试。

### 资源管理复核

- 进程/Job/管道：`ExecuteAsync` 的 finally 显式释放两路读端、`Process` 和 `ChildJob`；`Stop` 先终止 Job 再杀进程；`TrayContext.OpenConfig/OpenFolder` 用 `using` 释放 `Process`；未发现新的句柄泄漏。
- 内存上限：`LineAssembler` 每个输出泵最多保留 `8192 + 4096` 字符（约 24 KB/泵）；轮转窗口固定 8 MiB + 1 字节；`GetUpcoming` 的临时列表与任务数成正比（10,000 个任务约 467 KB/次），没有无界缓存。
- 等待：调度循环的等待有 60 秒上限，重载等待有 30 秒上限，探测有 10 秒上限；没有新增的无限期阻塞等待。F2 描述的是短时（毫秒级到一次 8 MiB 轮转）的托盘线程同步等待，不是死锁。
- 持锁 IO 与锁序：`_gate` 内的日志写入只出现在冷路径（`TryEnqueueLocked` 的 `schedule-none`/`enqueue-error`、`RestoreDeferredLocked`），调度热路径的日志都在锁外；锁序只有 `_gate → Log.Gate`，没有反向获取，F2 里托盘线程会等 `_gate` 但不会与 `Log.Gate` 形成环。
- 日志吞吐：每行仍要 `CreateDirectory` + 文件大小检查 + 打开/写入/关闭，实测 2000 条 `Log.Event` 约 905 ms（约 0.45 ms/条）。该点在更早的审查中已被列为优化项而非缺陷，本轮没有把它当新发现，但高输出任务会因此受限于约 2,000 行/秒，记录在此供后续取舍。

### 构建、测试与探针

- `dotnet build Cronlet.slnx -c Release --nologo`：成功，0 警告、0 错误，用时 13.51 秒。
- `dotnet test Cronlet.slnx -c Release --nologo`：98 通过 / 0 失败 / 0 跳过，用时 12.56 秒。测试数 90 → 98（新增 2 个配置解析、1 个校验、5 个调度/立即运行）。
- `dotnet publish src/Cronlet/Cronlet.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o %TEMP%\...`：成功，`Cronlet.exe` 641,460 字节，ProductVersion `0.2.3+b5d52c5...`；与 GitHub 上 v0.2.3 的框架依赖资产大小 641,460 一致。
- `dotnet publish ... -p:PublishAot=true -p:StripSymbols=true`：失败，`vswhere.exe not found at 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'`，本机没有 MSVC；未完成 AOT 验证。
- 探针见 [Probe.cs](scripts/Probe.cs) 与 [run-probe.ps1](scripts/run-probe.ps1)，在系统临时目录构建/运行，没有向仓库 `artifacts/` 添加审查产物。结果：字形截断 9 类 × 3 种分块全部与整行截断一致、无替换字符；轮转 17,825,792 → 8,388,608 字节、分配 8,390,448 字节、2,048 条完整记录、无残留临时文件；`GetUpcoming` 100/10,000 个任务 = 0.067/1.6 ms、5,088/467,281 字节每次；`RunNow` 的 queue 路径端到端产生 2 次 `task-end`；不存在的 pwsh 路径下 `RunNow` 仍报告 `started=True`（F1 复现）；2000 条日志 905 ms；`pwsh` 的 `Process.Start` 5–25 ms。
- CI 只读核对：`gh run list` 显示 v0.2.1/v0.2.2/v0.2.3 的标签运行 success；`gh release view` 显示资产为 `cronlet-windows-amd64.exe` / `cronlet-windows-amd64-aot.exe`，摘要各不相同；v0.2.0 的标签运行在 3c9ceed 之前失败（两个同名 `Cronlet.exe`）。
- 结束时工作区：只新增 `code-reviews/2026-11-12-20-10-02_b5d52c/`（未跟踪），没有修改任何现有文件，没有执行 `git add`/`commit`/`push`。

### 未验证项

- 真实桌面上的托盘行为：菜单刷新时机、键盘 `KeySelect`、气泡、菜单打开时退出、双显示器/高 DPI 下的坐标。
- 同一标签工作流的实际重跑（需要写 Release 权限，未执行）；只按代码和已有成功运行核对。
- NativeAOT 发布及 AOT 产物在 CI 上的运行（本机缺 `vswhere.exe`）。
- 重载 30 秒窗口内的实际点击（F3 由代码路径确认，没有构造桌面场景）。
- `publish.ps1` 没有实际执行（会写入 `S:\local\app\cronlet`）；只做静态复核。
- `Process.Start` 在冷启动、杀毒扫描、网络路径下的最坏延迟；F2 的 5–25 ms 只是本机热态数据。
- 高并发多任务下 `_gate` 的竞争与托盘响应（未构造定向负载）。