# Cronlet

按 cron 调度执行 PowerShell 脚本/命令的托盘小工具（.NET 11 / 纯 Win32 托盘 / NativeAOT）。

## 配置

`config.toml` 与 `Cronlet.exe` 同目录，首次启动自动生成模板。

```toml
[[tasks]]
name = "campus-login"
enabled = true
command = "./scripts/eportal-login.ps1"
cron = "*/2 * * * *"
jitter = 10
timeout = 120
overlap = "skip"
cwd = ""
```

| 字段 | 说明 |
|---|---|
| `name` | 任务名，唯一（大小写不敏感）；只允许 `A-Z`、`a-z`、`0-9`、`_`、`-`，长度 1–128 |
| `enabled` | 是否启用 |
| `command` | 要执行的 pwsh 命令，原样交给 `pwsh -Command`；相对路径以 exe 目录为当前目录 |
| `cron` | 5 段 unix cron（分 时 日 月 周），本地时区 |
| `jitter` | 秒，触发时间 = cron 时间 + `random(0..jitter)`；上限被限制在下一次触发之前，间隔不足时实际抖动会变小 |
| `timeout` | 秒，0 表示不限制。计时覆盖"进程退出 + 输出读取"的联合等待：启动进程先退出、后代仍占着输出管道时同样算超时。超时会终止整个进程组（含已脱离父进程的后代）；命令启动瞬间派生的后代可能不在其中 |
| `overlap` | 上次未结束时的策略：`skip`（默认）/ `kill` / `queue` |
| `cwd` | 工作目录，空则用 exe 目录 |
| `notify_if` | 可选。哪些退出码要弹通知；非空时只认这些。不写则用全局值 |
| `notify_if_not` | 可选。哪些退出码不弹通知，优先于 `notify_if`。不写则用全局值 |

`command` 是唯一入口：脚本写 `./scripts/xxx.ps1`，临时命令直接写命令本身。

`pwsh`、`upcoming`、`notify_if`、`notify_if_not` 是全局配置项（与 `[[tasks]]` 同级）：

- `pwsh` 指定 PowerShell 可执行文件；不写则按 `pwsh` 在 PATH 里找，有启用任务却找不到时启动或重载会直接报错。
- `upcoming` 控制托盘菜单顶部最多列出多少个待触发任务，默认 10，必须大于 0。
- `notify_if` / `notify_if_not` 决定任务结束时哪些退出码弹托盘通知：先看 `notify_if_not`（命中就不弹），再看 `notify_if`（非空时只认它里面的），都不受限就弹。默认 `notify_if = []`、`notify_if_not = [0]`，即非 0 退出才提醒；两者都能在单个任务里覆盖。超时按 `exit = -1` 参与判定。

只有退出码与上一次不同才真的弹，所以连续同样的失败只提醒一次；中途成功一次、再次失败会重新提醒。

```toml
pwsh = "S:/tools/pwsh/pwsh.exe"
upcoming = 10

notify_if = []
notify_if_not = [0]
```

**默认工作目录是 `Cronlet.exe` 所在目录**；`cwd` 非空时覆盖它。命令里的相对路径都相对这个目录解析，所以 `./scripts/login.ps1` 和你在 exe 目录下手敲的命令等价。

路径里含空格时按 PowerShell 规则加引号：`command = "& './scripts/Daily Jobs/report.ps1'"`。

## 调度

每个启用的任务各自算出下一次触发，按时间放进优先队列：

1. 用 `cron`（本地时区）算出下一个名义触发时间；
2. 加上 `random(0..jitter)` 秒抖动，抖动上限被压在下一次名义触发之前；
3. 入队，每个任务始终只保留一条记录；
4. 循环取最早到期的那条派发，派发后重新计算该任务的下一次。

任务之间互不影响；`overlap` 只管同一任务上次还没结束时怎么处理。重载会清空队列并按新配置重新计算。

队列里存的是叠加抖动后的实际触发时间，每个任务一条；更靠后的触发时刻要等这次派发后重新加抖动，现在无法预知。托盘菜单顶部直接读这些记录，按时间排序取最早的 `upcoming` 条（默认 10），显示到秒。

点其中一条会立刻触发该任务一次，不改动计划时间——它到点仍会照常再跑一次，正在运行时的处理走该任务的 `overlap`。结果用气泡提示。

## 运行模式

- **用户态（默认）**：托盘图标 + 调度。悬浮提示显示版本号；菜单顶部列出接下来 `upcoming` 个待触发任务（默认 10，实际触发时间精确到秒 + 任务名，点一下立即运行一次），其下是打开配置文件 / 打开脚本目录 / 打开日志目录 / 重载配置 / 退出
- **headless**：`Cronlet.exe --headless`（非交互会话也自动启用）——无托盘，只跑调度
  启动失败会以非零退出码结束：配置错误 2、其他启动失败 1；已有实例在跑时退出码 0

重载流程：重新读取 `config.toml` → 校验 → 暂停派发 → 等运行中的任务结束（最多 30s）→ 原子替换并重排。

## 单实例

- 跨会话文件锁 `cronlet.lock`；已有实例在跑时，新实例直接退出（只写日志）
- 想要托盘：先停掉后台实例，再手动运行 `Cronlet.exe`

## 开机启动

任务计划程序：触发器“启动时”，操作 `Cronlet.exe --headless`，勾选“不管用户是否登录都要运行”。

## 日志

JSONL，每行一个 JSON，首字段固定为 `ts`（RFC3339 带时区）：

| 文件 | 内容 |
|---|---|
| `logs/runner.jsonl` | Runner 事件（start / task-start / task-end / 错误等） |
| `logs/task-<name>.jsonl` | 对应任务的 stdout / stderr |

高低水位：文件超过 **16MB** 时裁剪到最近 **8MB**（只保留完整记录，不切坏 JSON）。
单条输出超过 **8 KiB（UTF-8 字节）** 会被截断并附上 …[truncated]，截断点落在完整字符边界上，不会切出半个字符。

## 目录结构

```
Cronlet.slnx
Directory.Build.props   UseArtifactsOutput：所有产物落在 artifacts/
global.json             dotnet test 走 Microsoft.Testing.Platform
src/Cronlet/            主程序（DotTray + Tomlyn 源生成，AOT 兼容）
tests/Cronlet/          单元测试（xunit v3）
artifacts/              bin/ obj/ publish/ test/，不入库
```

## 构建 / 测试 / 发布

```powershell
dotnet build   Cronlet.slnx -c Release
dotnet test    Cronlet.slnx -c Release
dotnet publish src/Cronlet/Cronlet.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

框架依赖的单文件产物在 `artifacts/publish/Cronlet/release_win-x64/Cronlet.exe`，目标机需要装 .NET 11 运行时。

自包含的 NativeAOT 版本（目标机不需要任何运行时）：

```powershell
dotnet publish src/Cronlet/Cronlet.csproj -c Release -r win-x64 -p:PublishAot=true -p:StripSymbols=true
```

产物约 3 MB，体积与内存都比框架依赖版更小。本机编译需要 MSVC（VS Build Tools 的 C++ 工作负载）；CI 的 `windows-latest` 自带，所以 `build.yml` 会同时产出两种包。

打 `v*` 标签推送会触发 CI 发布正式 release，框架依赖版和 NativeAOT 版都会作为资产挂上去：

```powershell
git tag v0.2.0
git push origin v0.2.0
```

重跑同一标签的工作流会覆盖已有资产，不会因标签已存在而失败。

本机部署目录为 `S:\local\app\cronlet`，布局沿用 app-runner 约定：

```
S:\local\app\cronlet\
├─ content\          程序本体（Cronlet.exe、config.toml、scripts/、logs/）
├─ app.ico
└─ app.toml          启动器元数据（entry = Cronlet.exe）
```

> 从带 Low 完整性标签的目录（例如某些 sandbox 工作区）直接运行 exe 会导致托盘图标注册失败（Win32 错误 5），请从普通目录运行。

## 图标

- `src/Cronlet/runner.svg`：图标源文件
- `src/Cronlet/runner.ico`：多尺寸图标（已提交，构建时嵌入 exe 与托盘）

