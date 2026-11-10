# 官方数据模型的实际读取验证

这项验证在 Linux 上使用 .NET 加载官方 `TheBrainCore.dll` 中的数据模型，通过同包 `Newtonsoft.Json.dll` 实际反序列化、再序列化 BRZ 内的记录。它比仅使用 Python 检查 JSON 多验证了一层：**官方模型接受这些字段并能保留其值**。

它没有启动 TheBrain 桌面程序，没有调用 BRZ 导入器、数据库导入、同步或授权流程；不能将结果标记为“已成功导入原软件”。

## 本次结果

2026-11-10 使用 .NET SDK `6.0.428`、运行时 `.NET 6.0.36` 完成验证。机器可读结果见 [official-model-check.json](official-model-check.json)，源码见 [official-model-check](../tools/official-model-check/)。

| 输入 | 检查记录数 |
| --- | ---: |
| `5G技术 2025-06-19 (1).brz` | 112 |
| `AIGC 2025-09-22.brz` | 422 |
| `elon 2025-03-21.brz` | 255 |
| `test2 2025-03-21 (1).brz` | 37 |
| `test2 2025-03-21.brz` | 4 |
| 修订后的 `test2-code-edit-demo.brz` | 46 |
| 总计 | **876** |

检查范围为 `thoughts.json`、`links.json`、`attachments.json`、`fielddefinitions.json`、`fieldinstances.json` 的全部非空记录。结果：**0 次解析失败、0 项原字段语义差异**。两处新记录时间戳的末尾零被序列化器省略，按 .NET UTC ticks（100 纳秒精度）比较仍完全相同；已在报告单列。

演示输入摘要：

```text
3e4d9973eb67a3d71f0a78da2a1472134b5ac5c131642727c94fe17d7fcaa0a5
```

比较逐一检查原始 JSON 中的字段，数字按 `System.Decimal` 值比较，数组和对象递归比较；时间戳仅在表示同一 UTC tick 时视为格式规范化。官方序列化器额外输出的默认值和 `null` 不算丢失原字段。本验证不将官方重序列化的数据写回 BRZ，因此不会丢弃工具保留的未知扩展字段。

## 可复现步骤

需要 .NET 6 SDK，以及来自同一官方构建的应用文件目录。可使用正常安装后的程序目录，或静态提取出的官方载荷。程序启动前检查 `TheBrainCore.dll`、`Newtonsoft.Json.dll`、`TheBrain.Sys.dll` 的 SHA-256；摘要不符时拒绝运行，避免将此构建混淆过的类型名称套到其他版本。摘要详见结果 JSON。

下面命令从仓库根目录执行，假设 `dotnet` 已可调用，官方载荷位于 `work/official/app/`：

```sh
DOTNET_CLI_HOME="$PWD/work/dotnet-home" \
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
NUGET_PACKAGES="$PWD/work/nuget" \
dotnet build tools/official-model-check/OfficialModelCheck.csproj \
  --configuration Release --nologo \
  -p:BaseIntermediateOutputPath="$PWD/work/modelcheck-obj/" \
  -o work/modelcheck-bin

dotnet work/modelcheck-bin/OfficialModelCheck.dll \
  work/official/app \
  "work/extracted/TheBrain14 - v2/"*.brz \
  examples/test2-code-edit-demo.brz \
  > work/official-model-check.json
```

本次实际使用本地安装的 `work/dotnet/dotnet` 替代命令中的 `dotnet`。项目没有 NuGet 包依赖，`NuGet.Config` 清空外部包源；只需要 SDK 自带的 .NET 6 引用包。项目源码不包含或再分发任何官方 DLL。读取失败或原始字段语义变化会返回非零退出码。

## 尚未覆盖

- BRZ 导入器是否接纳整份归档、如何映射目标脑图 UUID、是否自动重建缓存。
- 数据库写入、原应用界面显示、关闭重开及再导出。
- 日志、同步点、墓碑、权限及其他表的官方处理过程。
- 各附件文件在原应用中的实际打开效果。

完成上述应用层验证仍应使用独立、可丢弃的测试脑图，按 [FORMAT.md](FORMAT.md#分级验证及导入检查) 操作。
