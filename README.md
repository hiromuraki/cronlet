# Cronlet

按 cron 调度执行 PowerShell 脚本/命令的托盘小工具（.NET 11 / WinForms）。

## 配置

`config.toml` 与 `Cronlet.exe` 同目录，首次启动自动生成模板。

```toml
[[tasks]]
name = "campus-login"
enabled = true
script = "eportal-login.ps1"
cron = "*/2 * * * *"
jitter = 10
timeout = 120
overlap = "skip"
cwd = ""
```

| 字段 | 说明 |
|---|---|
| `name` | 任务名，唯一（大小写不敏感） |
| `enabled` | 是否启用 |
| `script` | 绝对路径直接使用；相对路径**先按 exe 目录解析，找不到再到 `scripts/` 下找一次**；都不存在则当作 pwsh 命令执行 |
| `cron` | 5 段 unix cron（分 时 日 月 周），本地时区 |
| `jitter` | 秒，触发时间 = cron 时间 + `random(0..jitter)`；必须小于 cron 的最小间隔，否则启动报错 |
| `timeout` | 秒，0 表示不限制；超时杀整个进程树 |
| `overlap` | 上次未结束时的策略：`skip`（默认）/ `kill` / `queue` |
| `cwd` | 工作目录，空则用脚本所在目录 |

## 运行模式

- **用户态（默认）**：托盘图标 + 调度。菜单：打开配置 TOML / 重载配置 / 退出
- **headless**：`Cronlet.exe --headless`（非交互会话也自动启用）——无托盘，只跑调度

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

高低水位：文件超过 **16MB** 时裁剪到最近 **8MB**（从行首开始，不切坏 JSON）。

## 构建 / 发布

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

> 从带 Low 完整性标签的目录（例如某些 sandbox 工作区）直接运行 exe 会导致托盘图标注册失败（Win32 错误 5），请从普通目录运行。

## 图标

`runner.svg` → `build-icon.ps1`（resvg 渲染多尺寸 PNG 并打包）→ `runner.ico`。

