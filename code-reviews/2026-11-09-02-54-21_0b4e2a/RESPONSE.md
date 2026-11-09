# Cronlet 复审回复（0b4e2a）

- 对应审查：`CODE_REVIEW_2026-11-09-02-54-21_0b4e2a.md`（提交 `0b4e2a6`，`main`）
- 回复时间 2026-11-09 04:40 +08:00（香港时间）
- 说明：这两项修复落在 `try/dottray` 分支上（`ShellResolver` 与 `Log` 在该分支已被重写），合并后 `main` 自动满足
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 79 项全部通过

## [P2] 探测取消只覆盖等待，未覆盖进程启动和管道读取 —— 已修复

复述：`Probe` 在检查取消之前就调用了 `Process.Start`；`DrainAsync` 不接收令牌；超时或取消后直接返回，把"读取端是否收尾"留给进程终止。

修复（[ShellResolver.cs](../../src/Cronlet/ShellResolver.cs)）：

1. **启动前先看取消**：

```csharp
if (ct.IsCancellationRequested) return (false, "已取消");
```

2. **超时与取消共用一处清理**，并显式观察读取端结束：

```csharp
bool completed;
try { completed = all.Wait(ProbeTimeout, ct); }
catch (OperationCanceledException) { completed = false; }

if (!completed)
{
    try { proc.Kill(true); } catch { }
    try { Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2)); } catch { }
    return (false, ct.IsCancellationRequested ? "已取消" : "未在 10s 内完成");
}
```

`all` 是 `Task.WhenAll(proc.WaitForExitAsync(ct), stdout, stderr)`，即进程退出与两路排空共用一个截止时间；`Kill(true)` 结束整棵树让管道关闭，随后的 2 秒 `WaitAll` 确认两个读任务真的收尾了，而不是把引用丢掉等 GC。

## [P3] 回复记录描述的无分配字形截断尚未落地 —— 已修复（在分支上）

这条针对的是 `main` 的 `0b4e2a6`。分支上的 `Log.TruncateLine` 在 `a52eac6` 已经改成 span 实现，用的正是上一份回复里写的 API：

```csharp
ReadOnlySpan<char> rest = line.AsSpan();
while (!rest.IsEmpty)
{
    int length = StringInfo.GetNextTextElementLength(rest);
    int size = Encoding.UTF8.GetByteCount(rest[..length]);
    if (used + size > MaxLineBytes) break;
    used += size;
    cut += length;
    rest = rest[length..];
}
```

不再调用 `GetTextElement()`，因此不会为每个簇生成子字符串、也不会为整行分配边界数组。`main` 上的旧实现会在合并时被这一版替换。

形状上没有对应的分配断言（测试里不好稳定测量 GC 分配量），只保留了对截断结果与 Unicode 边界的断言。

## 关于实测资源占用

审查的量测方式和我们自己的观察一致，几点补充：

- 空闲工作集 35–40 MiB、私有提交 9–11 MiB 是**框架依赖版**的数字。分支上换成 DotTray 后框架依赖版是 29 MiB / 7.5 MiB，AOT 版是 16 MB / 4.7 MB。
- 「无界面跳过 `ApplicationConfiguration.Initialize()`」这条建议在 DotTray 分支上已经被自然吸收：那里根本没有 WinForms，也就不存在这个初始化。
- 高频低输出的合并执行、批量追加、轮转的分块复制、空队列直接等唤醒，这四条属于优化项而非缺陷，这次没有做。分支当前的瓶颈不在这里（单次任务约 1 秒，其中大部分是 pwsh 启动）。

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    79 通过 / 0 失败
```

未自动化验证：取消/超时的运行时行为仍靠代码路径确认，没有构造"后代持有管道"的定向用例。
