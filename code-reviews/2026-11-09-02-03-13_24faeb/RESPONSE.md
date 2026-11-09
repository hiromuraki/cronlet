# Cronlet 整库复审回复（24faeb）

- 对应审查：`CODE_REVIEW_2026-11-09-02-03-13_24faeb.md`（提交 `24faeb3`）
- 回复时间 2026-11-09 02:12 +08:00（香港时间）
- 处理结果：三项全部修复
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 60 项全部通过；空 `pwsh.exe` 的启动退出码已用发布产物实测

## [P2] PowerShell 文件存在但无法运行时，启动仍报告成功 —— 已修复

复述：`Resolve` 只做 `File.Exists`，一个空的 `pwsh.exe` 也能通过；启动照常记录 `start`，直到首个任务派发才由 `Process.Start` 抛 193（不是有效的 Win32 应用）。

修复（[ShellResolver.cs](../../src/Cronlet/ShellResolver.cs)）：解析与执行能力分开。新增 `Probe`——用同一个可执行文件真跑一次

```pwsh
-NoProfile -NonInteractive -Command "$PSVersionTable.PSVersion.Major"
```

带 10 秒超时，要求退出码为 0 且输出是整数版本号。结果按完整路径缓存（`ConcurrentDictionary`），进程内只探一次，不会每个任务重启一次探测。

[ConfigValidator.ResolveShell](../../src/Cronlet/ConfigValidator.cs) 在解析之后调用它，失败按配置错误抛出（启动 `exit 2`，重载保留旧配置）。

顺带修了一个一致性问题：`pwsh` 写成相对路径时原来按**进程当前目录**解析，现在和 `command` 一样以 exe 目录为基准。

验证（发布产物 + 临时目录，实测）：

| 配置 | 期望 | 实测 |
|---|---|---|
| `pwsh = "<tmp>/pwsh.exe"`（空文件） | 2 | 2 |

```
config-error: PowerShell 无法运行: ...\pwsh.exe（... The specified executable is not a valid application for this OS platform.）
```

## [P2] 轮转直接覆盖原文件，中断可能损坏历史日志 —— 已修复

复述：`File.WriteAllBytes(path, ...)` 直接覆盖原文件，截断与写回之间被强制结束时，保留段也可能全丢。

修复（[Log.cs](../../src/Cronlet/Log.cs)）：改为写同目录临时文件再替换。

```csharp
string temp = path + ".rotating";
File.WriteAllBytes(temp, keep);

try { File.Move(temp, path, overwrite: true); }
catch (Exception ex)
{
    try { File.Delete(temp); } catch { }   // 替换失败：保留原文件
    DebugWrite(ex);
}
```

中断发生在替换之前 → 原日志完好，只多一个 `.rotating` 残留；替换之后 → 新内容是完整的一批记录。替换失败（例如日志被其他进程占用）时保留原文件并记 `log-error.txt`，下一条记录继续追加。

## [P3] 输出截断以 UTF-16 码元计数，且可能切断字符 —— 已修复

修复（[Log.TruncateLine](../../src/Cronlet/Log.cs)）：

- 上限改为 **UTF-8 字节**口径（`MaxLineBytes = 8192`），与 README 的 8 KiB 一致；
- 用 `StringInfo.ParseCombiningCharacters` 取字形簇边界，按簇累加字节数，超限即停，截断点永远落在完整字符上。

关于你提到的 NFC：**没有在截断时做规范化变换**。理由是 `Normalize(FormC)` 会改变输出内容——日志应当记录进程实际输出了什么，而不是一个等价但不同的字符串。字形簇边界本身已经覆盖了 NFC/NFD 的真正风险：组合序列（`e` + `U+0301`）会被识别为一个文本元素，截断不会把它切成两半。

如果你要的是"日志里的文本统一成 NFC"，那是另一个决定（会改变内容），需要明确说一声，我再加。

验证：新增两个用例——4000 个 `作`（每个 3 字节，合计 12000）截断后不超 8192 字节且不含 `�`；3000 个 `😀`（每个 4 字节）截断后代理对完整、不出现半个字符。

## 方案评估的回应

| 领域 | 处理 |
| --- | --- |
| 日志轮转 | 采纳"裁剪与替换分开"：裁剪仍是纯函数 `RotateTail`，替换改为临时文件 + `File.Move`；替换失败保留原文件 |
| PowerShell | 采纳有超时的真实执行探测；代价是启用任务时启动/重载多一次约 0.2–0.5 秒的进程启动，结果缓存后不再重复 |
| 输出边界 | 采纳 UTF-8 字节口径；截断发生在读取回调之后，确实无法限制读取巨行时的峰值内存——那需要在 `OutputDataReceived` 之外自己读流，本轮不做 |

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    60 通过 / 0 失败
```

测试数量 58 → 60：新增多字节字符与代理对两个截断用例，替换原来基于码元的用例。

未自动化验证：轮转替换失败路径（需要别的进程锁住日志文件）没有写成测试；`Probe` 的缓存行为只在实测中体现。
