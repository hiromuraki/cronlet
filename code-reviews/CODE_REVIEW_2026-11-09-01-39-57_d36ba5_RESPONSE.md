# Cronlet 整库复审回复（d36ba5）

- 对应审查：`CODE_REVIEW_2026-11-09-01-39-57_d36ba5.md`（提交 `d36ba57`）
- 回复时间 2026-11-09 01:52 +08:00（香港时间）
- 处理结果：两项 P2 全部修复；顺带按你的评估意见加了单行截断与 `pwsh` 配置项
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 58 项全部通过；缺 PowerShell 的启动退出码已用发布产物实测

## [P2] 日志轮转会误删恰好对齐的完整记录 —— 已修复

复述：`RotateTail` 无条件丢掉"第一个换行之前"的内容。若低水位窗口的起点恰好是记录行首，这一步会连带丢掉一条完整记录——审查用"旧记录 8 MiB + 1 字节、最近记录恰好 8 MiB"复现了从 16 MiB 掉到 110 字节。

修复（[Log.cs](../src/Cronlet/Log.cs)）：读取窗口时往前多读一个字节作为哨兵，由它判断窗口起点是否落在行首。

```csharp
long windowStart = Math.Max(0, fs.Length - LowWatermark);
hasSentinel = windowStart > 0;
fs.Position = hasSentinel ? windowStart - 1 : 0;   // 多读一个字节
```

```csharp
if (hasSentinel)
{
    if (window[0] == (byte)'\n') offset = 1;          // 哨兵是换行：第一行本身完整
    else { /* 跳过残行到第一个换行之后 */ }
}
```

末行完整性检查保持不变。

验证：新增两个用例——`Window_starting_on_a_record_boundary_keeps_the_first_record`（哨兵是 `\n`）与 `Window_from_file_start_keeps_every_complete_record`（`start == 0`），加上原有的残行/末行/无完整行三种情形，五种窗口起点都覆盖了。

### 关于你问的两件事

**轮转不是"循环剪裁到低于低水位"**：它是一次性从尾部截 8 MiB，再对行边界对齐，结果 ≤ 8 MiB，只做一遍，没有迭代。

**8 MiB 单行确实需要商榷，但结论是"不该让它出现"**：如果单条记录超过 8 MiB，窗口里就只剩它的残段，`RotateTail` 会返回空——整份历史连同这条巨行一起丢掉。与其让轮转去处理这种极端，不如在写入端限制单条长度：`TaskOutput` 现在把超过 **8 KiB** 的行截断并附 `…[truncated]`（`Log.TruncateLine`，两个测试覆盖）。这样 8 MiB 窗口至少能装上千条记录，轮转永远有意义。

## [P2] 无界面模式缺少 PowerShell 时仍报告启动并持续运行 —— 已修复

复述：`pwsh.exe` 到派发时才由系统按 PATH 查找。缺 PowerShell 时 Cronlet 先记 `start` 并保持运行，第一次触发才记 `task-error`（退出码 -1），之后继续活着——部署检查看到进程在，却执行不了任何任务。

修复：

- 新增 [ShellResolver.cs](../src/Cronlet/ShellResolver.cs)：含路径分隔符的值按路径检查，裸名字在 PATH 里找（补 `.exe`），非法 PATH 片段跳过。
- [ConfigValidator.Validate](../src/Cronlet/ConfigValidator.cs) 改为**返回**解析后的 PowerShell 路径；配置里有启用任务却解析不到时，按配置错误抛出。启动走 `ExitConfigError(2)`，重载则保留旧配置。
- `Scheduler` 保存解析结果并在派发时复用（不再每次让系统搜索），`ReloadAsync` 接受新路径，重载后改用新配置指定的 shell。
- 按你的建议加了 `pwsh` 配置项，默认值就是 `pwsh`：

```toml
pwsh = "S:/tools/pwsh/pwsh.exe"
```

验证（发布产物 + 临时目录，实测）：

| 配置 | 期望 | 实测 |
|---|---|---|
| `pwsh = "C:/nope/pwsh.exe"` | 2 | 2 |

日志：`config-error: 找不到 PowerShell: C:/nope/pwsh.exe（可用 pwsh 配置项指定完整路径）`。

### 关于你问的检测机制

修复前**没有任何检测**：`ProcessStartInfo.FileName = "pwsh.exe"` 交给 `CreateProcess` 按 PATH 搜索，找不到就等到 `Start()` 抛 `Win32Exception`，由 `task-error` 记录、任务退出码 -1，进程继续跑。现在改成"配置生效时解析一次、派发复用"。

## 方案评估的回应

| 领域 | 处理 |
| --- | --- |
| 日志轮转 | 采纳哨兵判定；未采纳"写临时文件再替换"——写入本身是追加，中断最多影响末行，而末行完整性已经在裁剪时处理 |
| 后台启动 | 采纳"配置生效时保存解析结果"，并通过 `pwsh` 配置项支持便携部署与不同运行账户 |
| 上一轮修复 | 保持不变 |

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    58 通过 / 0 失败
```

测试数量 50 → 58：新增轮转起点 2 例、单行截断 2 例、`ShellResolver` 4 例。

未自动化验证：轮转的端到端行为（真的写到 16 MiB 再触发）只在裁剪函数层面测了边界，没有构造 16 MiB 文件；审查是用真实文件复现的。
