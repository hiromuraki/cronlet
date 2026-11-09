# Cronlet 整库复审回复（6575ff）

- 对应审查：`CODE_REVIEW_2026-11-09-02-17-44_6575ff.md`（提交 `6575fff`）
- 回复时间 2026-11-09 02:27 +08:00（香港时间）
- 处理结果：五项发现全部修复，四条冗余代码全部清理
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 60 项全部通过；探测超时与同路径替换已用发布产物实测

## [P2] PowerShell 探测的 10 秒超时未覆盖输出读取 —— 已修复

复述：`WaitForExit` 只约束主进程，主进程退出后 `ReadToEndAsync` 仍会等管道关闭；后代继承了输出管道时，等待没有截止时间（审查实测被拖到 13.45 秒）。

修复（[ShellResolver.cs](../../src/Cronlet/ShellResolver.cs)）：

- 进程退出与两路读取合并成一个任务，共用同一个截止时间：

```csharp
var all = Task.WhenAll(proc.WaitForExitAsync(), version, error);
if (!all.Wait(ProbeTimeout))
{
    try { proc.Kill(true); } catch { }   // 结束整棵树，管道随之关闭
    return (false, $"未在 {ProbeTimeout.TotalSeconds:0}s 内完成");
}
```

- 输出不再整体读入内存：stdout 用 `ReadLineAsync` 只取第一行（探测只要版本号），stderr 保留用于诊断。
- 托盘重载的校验改到后台线程执行（[Program.cs](../../src/Cronlet/Program.cs)），最长 10 秒的探测不再占用 UI 线程：

```csharp
string newPwsh = await Task.Run(() => ConfigValidator.Validate(newConfig));
```

## [P2] 按路径永久缓存探测结果 —— 已修复

删除 `ConcurrentDictionary` 缓存：探测只在配置被接受时跑一次，派发复用解析结果，进程期缓存没有收益，还会让"原地修复后重载"永远失败。

验证（发布产物 + 临时目录，实测）：

| 步骤 | 结果 |
|---|---|
| `pwsh` 指向空 `pwsh.exe` | `exit=2`，`config-error` |
| 同一路径换成完整 PowerShell | 启动成功，日志记录 `start` |

代价：启动与每次重载都会多一次约 0.2–0.5 秒的进程启动。测试套件因此从 9 秒涨到 12.7 秒（`ConfigValidator.Validate` 在测试里被大量调用），生产路径上只有启动和重载各一次。

## [P2] 截断巨行前为整行分配字形边界数组 —— 已修复

修复（[Log.cs](../../src/Cronlet/Log.cs)）：改用 `StringInfo.GetTextElementEnumerator` 逐簇前进，达到字节上限立即停止，不再为整行建立 `int[]`。

```csharp
TextElementEnumerator walker = StringInfo.GetTextElementEnumerator(line);
while (walker.MoveNext())
{
    string element = walker.GetTextElement();
    int size = Encoding.UTF8.GetByteCount(element);
    if (used + size > MaxLineBytes) break;
    used += size;
    cut += element.Length;
}
```

Unicode 语义不变（仍是字形簇边界 + UTF-8 字节预算），800 万字符的输入不再额外分配约 96 MiB 的边界数组。

## [P3] 写轮转临时文件失败时缺少清理 —— 已修复

写入与替换现在同处一个 `try`：

```csharp
try
{
    File.WriteAllBytes(temp, keep);
    File.Move(temp, path, overwrite: true);
}
catch (Exception ex)
{
    try { File.Delete(temp); } catch { }   // 两类失败都清理残留
    DebugWrite(ex);
}
```

## [P3] 托盘打开文件和目录时未确定性释放进程包装对象 —— 已修复

两处 `Process.Start` 改为 `using var proc = ...`，包装对象立即释放，用户打开的窗口不受影响。

## 冗余代码

| 位置 | 处理 |
|---|---|
| `Program.Run` 的 `args` | 删除参数，命令行判断只在 `Main` 做 |
| `BuildStartInfo` 的 `out cwd` | 删除输出参数；`WorkingDirectory` 在方法内设置，测试直接断言它 |
| `ReloadAsync` 的重复唤醒 | 成功路径不再 `SignalWake`，只保留 `finally` 的最终通知 |
| 退出时重复释放托盘图标 | `ExitApp` 只隐藏图标，释放集中到 `Dispose` |

## 复核与方案

- 保留真实执行探测，收紧为"统一截止时间 + 只读首行 + 无缓存"。
- 保留临时文件替换方案，写入失败也纳入清理。
- 逐簇扫描保持 Unicode 语义并去掉大分配；调度循环、计时器与锁文件的释放路径不变，Job 仍是尽力清理。

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    60 通过 / 0 失败
```

测试数量保持 60（本轮改动集中在资源边界与冗余清理，没有新增可断言的纯逻辑）。

未自动化验证：继承输出管道导致的超时（需要构造刻意保持管道打开的后代）、巨行内存峰值、托盘句柄数，均只做了代码路径确认；同路径替换与空文件拒绝是实测的。
