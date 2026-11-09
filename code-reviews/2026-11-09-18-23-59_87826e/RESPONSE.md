# Cronlet 主分支复审回复（87826e）

- 对应审查：[REVIEW.md](REVIEW.md)（提交 `87826e`，`main`）
- 回复时间 2026-11-09 18:40 +08:00（香港时间）
- 处理结果：三项 P3 全部修复
- 回归验证：Release 构建 0 错误 0 警告；`dotnet test` 90 项全部通过

## 上一轮复核的"部分通过"项 —— 已修复

审查确认 `e` + `U+0301` 用例通过，但补充平面组合符与 ZWJ 仍会被拆开。根因是 `LineAssembler` 自己维护了一套组合字符分类（只看单个 UTF-16 码元的 `NonSpacingMark` / `SpacingCombiningMark` / `EnclosingMark`）：

- `U+1D165` 之类补充平面字符是代理对，单码元分类看不到；
- `U+200D`（ZWJ）属于 `Format` 类别，也不在那三类里。

修复方向按审查建议：**不再自己判断边界，改用同一套规则**。

- `LineAssembler` 只负责"有界缓冲 + 分行"：容量是 `maxBytes + 4096` 字符，多出来的部分是前瞻窗口；
- 截断统一交给 `Log.TruncateLine`，它用 `StringInfo.GetNextTextElementLength` 逐簇扫描，对补充平面、ZWJ、变体选择符都按 Unicode 文本元素处理；
- `Log.TruncateLine` 增加 `maxBytes` 参数（默认 `MaxLineBytes`），让 `LineAssembler` 的上限可以注入，测试用小上限仍然成立。

```csharp
private string TakeLine()
{
    string text = Log.TruncateLine(_line.ToString(), MaxBytes);
    _line.Clear();
    return text;
}
```

前瞻窗口是关键：截断点（8192 字节）必然落在缓冲区内，而缓冲比它多 4096 字符，`TruncateLine` 能看到截断点之后的完整簇，于是可以整簇丢弃。极端超长簇（超过前瞻窗口）才会退化为硬截断。

新增两个用例：

| 输入 | 期望 |
|---|---|
| `8191 × a` + `e` + `U+1D165` + `z` | `8191 × a` + `…[truncated]` |
| `8191 × a` + `e` + `U+200D` + `z` | `8191 × a` + `…[truncated]` |

原来的 `U+0301` 用例继续通过。

## [P3] 重跑已成功发布的工作流会使 Release 作业失败 —— 已修复

按审查建议改成"校验后复用"：

```bash
tag="aot-${{ github.run_number }}"
existing="$(gh api "repos/${GH_REPO}/git/ref/tags/${tag}" --jq .object.sha 2>/dev/null || true)"

if [ -n "$existing" ]; then
  if [ "$existing" != "${GITHUB_SHA}" ]; then
    echo "::error::tag ${tag} points at ${existing}, expected ${GITHUB_SHA}"
    exit 1
  fi
  gh release upload "$tag" aot/Cronlet.exe --clobber
  exit 0
fi

gh release create "$tag" ...
```

三种情况分开处理：

- 标签不存在 → 创建；
- 存在且指向同一个提交（重跑）→ 只刷新附件，不再报错；
- 存在但指向别的提交 → 明确报错，不静默复用。

步骤加了 `shell: bash`（windows runner 默认是 pwsh），顺带修掉了上一轮替换时混进注释的一个字面 `\n`。

## [P3] 轮转 8 MiB 日志窗口时产生约 48 MiB 临时分配 —— 已修复

原来的路径是 `MemoryStream`（可增长）→ `ToArray()` → `RotateTail` 再切片，一份窗口被复制三次。

改成"一次读入 + 区间写出"：

```csharp
long readFrom = hasSentinel ? windowStart - 1 : 0;
window = new byte[fs.Length - readFrom];
fs.Position = readFrom;
fs.ReadExactly(window);

var (offset, count) = RotateRange(window, hasSentinel);

using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write))
{
    output.Write(window, offset, count);
}
```

- `RotateRange` 返回 `(offset, count)`，不再构造新数组；
- 写临时文件时直接写这段区间，连"裁剪后的副本"都不需要；
- 分配量从约 48 MiB 降到一份窗口（约 8 MiB）。

`RotateTail(byte[], bool)` 保留成 `RotateRange` 的薄包装，原有测试继续有效。

## 回归结果

```
dotnet build Cronlet.slnx -c Release --no-incremental   0 警告 0 错误
dotnet test  Cronlet.slnx -c Release                    90 通过 / 0 失败
```

测试数量 88 → 90：新增补充平面组合符与 ZWJ 两例。

未自动化验证：轮转的实际分配量没有在本机复测（审查用 `Probe.cs` 测得 50,334,584 字节，改后按代码路径估算为一份窗口）；workflow 的重复发布只在本地做了文本检查，没有实际重跑远端运行。
