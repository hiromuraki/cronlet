# Cronlet DotTray 分支审查回复（a4ea15）

- 对应审查：`CODE_REVIEW_2026-11-09-04-20-13_a4ea15.md`（提交 `a4ea15b`，`try/dottray`）
- 回复时间 2026-11-09 04:40 +08:00（香港时间）
- 处理结果：五项全部处理，其中 P1 采用"明确子集 + 严格拒绝"而非恢复 Tomlyn
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 79 项全部通过；CI 的 AOT 构建与 release 已重跑

## [P1] 主分支的合法配置会被拒绝或被改写 —— 已处理（明确子集 + 补兼容项）

先说为什么没有按建议恢复 Tomlyn 的源生成：**Tomlyn 2.11.0 包里没有源生成器**。`lib/` 下只有三个 TFM 的 `Tomlyn.dll`，没有 `analyzers/` 目录；2.x 也移除了 1.x 的 `Toml.ToModel` DOM 入口。审查链接的源生成文档对应的是更新的版本线，本地这个版本拿不到。

于是把子集当成明确的产品决定，做两件事：

**补上原本合法、被误拒的写法**

| 写法 | 现在 |
|---|---|
| `jitter = 1_000` | 接受（去掉下划线后按整数解析） |
| `command = "\u4f5c"` | 接受（`\uXXXX` 转义已实现） |
| `tasks = []` | 接受（等价于没有任务） |

**把"会误解"的语法改成明确报错**

| 写法 | 之前 | 现在 |
|---|---|---|
| `"""多行"""` / `'''多行'''` | 被当成普通字符串，引号残留 | `不支持多行字符串（三引号），请写成单行` |
| `command = "Write-Output "ok""` | 接受并执行 | `字符串后面有多余内容` |
| 同一键写两次 | 后者静默生效 | `键重复` |
| 字符串未闭引号 | 可能被当作命令 | `引号没有闭合` |

兼容范围写在 [TomlConfig.cs](../src/Cronlet/TomlConfig.cs) 的类注释里，也就是唯一的行为契约。三引号多行命令确实不支持——如果需要，应该换一条更正式的解析路线，而不是继续往手写子集里加状态。

## [P2] 托盘取消与显式释放竞争，正常退出可变成启动失败 —— 已修复

复述：点退出 → 应用令牌取消 → DotTray 的令牌回调投递 `WM_CLOSE` 销毁窗口 → 主线程醒来再 `Dispose()` → 又一次 `WM_CLOSE` → `Win32Exception(1400)` → 被顶层 catch 记成 `fatal`、按启动失败返回。

修复（[TrayContext.cs](../src/Cronlet/TrayContext.cs) · [Program.cs](../src/Cronlet/Program.cs)）：托盘只保留一条关闭路径——`Dispose`。

```csharp
// 不再把应用令牌交给 DotTray
_icon = NotifyIcon.Run(handle, _menu, CancellationToken.None);
```

退出回调仍然只做 `cts.Cancel()` + `scheduler.Stop()`，主线程 `WaitOne` 返回后 `finally` 里 `tray.Dispose()`，由 `NotifyIcon.Dispose()` 移除图标并停线程。这样不存在"令牌已关窗、Dispose 再关一次"的窗口。

代价：图标不会因为令牌取消自动消失，只能靠 `Dispose`——而 `Program` 的 `finally` 保证了这一点。

## [P2] 新解析器会接受并执行原本非法的配置 —— 已修复

- **重复键**：顶层与每个任务各用一个 `HashSet` 记录已出现的键，重复即报错（`enabled = false` 后再写 `enabled = true` 现在被拒绝）。
- **未转义引号**：`ReadString` 改为扫描到真正的闭合引号，并要求它必须是最后一个字符；`"Write-Output "ok""` 因此报"字符串后面有多余内容"，而不是把它当命令执行。

这两条都有对应用例（`Duplicate_task_key_is_rejected`、`Duplicate_top_level_key_is_rejected`、`Unescaped_quote_inside_string_is_rejected`、`Unterminated_string_is_rejected`、`Triple_quoted_string_is_rejected_with_a_clear_message`）。

## [P2] AOT Release 的新标签默认指向主分支 —— 已修复

```yaml
gh release create "aot-${{ github.run_number }}"
  artifacts/publish/Cronlet/release_win-x64/Cronlet.exe
  --target ${{ github.sha }}
  --prerelease
  --title "AOT build ${{ github.run_number }}"
```

验证：新发布的 `aot-12` 指向 `3e0e12be`，与工作流检出的完整 SHA 一致（`git rev-parse refs/tags/aot-12^{commit}`）。同时标成 prerelease，不影响正式版本的最新发行标记。

## [P3] 托盘菜单和启动失败图标没有完整的释放路径 —— 已修复

- 菜单处理器在图标关闭后释放。`DefaultPopupMenuHandler` 实现的是**显式接口** `IDisposable`（直接调 `.Dispose()` 编译不过），所以写成 `((IDisposable)_menu).Dispose()`。
- `ShowFatal` 里 `NotifyIcon.Run` 抛异常时，原始 HICON 现在由 `finally` 释放。

## 资源对照

审查那张表的方向和我们的观察一致（实验分支文件更小、内存更低）。补充一点归因：差异来自四处改动叠加——换 DotTray、去 WinForms、去 Tomlyn、手写 JSON。单看 DotTray 的贡献无法从这组数据里拆出来。

另外 NativeAOT 已经跑通（CI 上 `windows-latest` 自带 MSVC），实际数字是：**2.6 MB 单文件、16 MB 工作集、4.7 MB 私有提交**，比审查表里的两个框架依赖版本都低。

## 建议的回应

| 建议 | 处理 |
|---|---|
| 保留 DotTray 作为独立托盘替换 | 已保留 |
| 补齐退出所有权 | 已修 |
| 配置解析保留 Tomlyn 源生成 | 无法采纳：该版本没有生成器；改为明确子集 + 严格拒绝 |
| JSONL 改用 `Utf8JsonWriter` | 暂不采纳。手写写入只覆盖字符串/布尔/整数/null 四种值，没有反射也没有分配问题；`Utf8JsonWriter` 会多一层 API 而收益不明。如果以后字段类型变复杂再换 |

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    79 通过 / 0 失败
```

CI：`Publish (NativeAOT)` 成功，release `aot-12` 已发布并替换 live（pid 42308，16.1 MB 工作集）。

未自动化验证：托盘退出的真实点击没有手动执行过，只验证了"唯一关闭路径"的代码结构与 DotTray 的 `Dispose` 语义。
