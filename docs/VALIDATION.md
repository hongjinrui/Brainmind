# 验证记录

日期：2026-11-10。结论：**可以通过代码读取、修改和重新打包现有 BRZ 的已研究字段；本次仅完成文件层验证，原应用兼容性尚未验证。**

## 自动检查

在 Python 3.12.14 环境执行：

```sh
python3 -m unittest discover -s tests -v
```

结果：28 项测试通过，其中一项以子测试逐一读取本仓库恢复出的五份原始 BRZ。其余测试使用自包含样本，不依赖安装器或第三方库；尚未恢复 `work/extracted` 的环境会跳过实样本检查。

覆盖名称与 UUID、父子/跳转、类型/标签同步、多个同名节点、Markdown 笔记及 UTF-8 字节长度、保留 HTML、受限图标模板、已有属性值、背景色、计划哈希和事务回退、禁止覆盖、未知文件/字段、原始行和 ZIP 元数据保留，以及重复键、非有限数值、重复成员、路径穿越、加密与符号链接拒绝。CLI 另验证预演不写文件、写后重新读取校验及笔记截断标志。

所有五份原始导出的 `validation.errors` 均为空。`test2 (1)` 原本就有一条壁纸 `DataLength` 不一致警告，编辑副本保留该警告，没有改动壁纸。

## 真实 BRZ 修改演示

输入：`TheBrain14 - v2/test2 2025-03-21 (1).brz`。

输入 SHA-256：

```text
77b752d502b643830a88a05279df7b9f5c5af42391addcbf848fbaf7a88b13a5
```

按 [demo.plan.json](../examples/demo.plan.json) 的 11 个操作生成 [test2-code-edit-demo.brz](../examples/test2-code-edit-demo.brz)，修改清单和摘要见 [demo.report.json](../examples/demo.report.json)。

- 原“加法”改为“加法（代码修改验证）”并增加 Label，保留原 UUID。
- 新增普通节点“代码编辑验证”、标签“代码验证”和类型“代码验证类型”。
- “test2”成为新普通节点的父节点；新普通节点与原“加法”建立跳转联系。
- 为新普通节点设置背景色、类型、标签及中文 Markdown 笔记。
- 将“已学”的 PNG 图标按已研究的简单模板复用到新类型节点。

| 项目 | 修改前 | 修改后 |
| --- | ---: | ---: |
| 节点 | 14 | 17 |
| 连线 | 15 | 19 |
| 附件记录 | 8 | 10 |
| ZIP 成员 | 20 | 22 |

输出 SHA-256：

```text
1bb1cfc19f5187e4bf33a0778a007f2338dc91dea9f41737b0f8f0c2210f3e55
```

将输出重新打开并校验：`errors=[]`。程序重新读取新增笔记，内容完整且未截断。这里只检查已登记的图标文件与状态，未声称原应用中已正确显示。

文件范围比对结果：

- 四份原始分卷和五份恢复出的原始 BRZ，与 [INVENTORY.json](INVENTORY.json) 中的 SHA-256 全部一致。
- 原有 20 个成员均保留；16 个成员的解压后字节完全未变。
- 仅四个现有成员发生变化：`meta.json`、`thoughts.json`、`links.json`、`attachments.json`。
- 原有节点记录中，仅原“加法”发生计划内修改；其余原有节点、连线、附件记录的原始行不变。
- 仅新增新普通节点的 `Notes.md` 和新类型节点的 `.data/Icon.png` 两个成员。

重新执行计划会产生新的时间戳，以及未显式指定的新 link/附件 UUID，因此文件摘要可能不同；应核对语义和变更范围，不要求重新生成的 ZIP 与本次输出逐字节相同。

## 静态程序证据与应用测试边界

没有执行包内安装器或授权工具。对官方安装器进行静态提取后，检查官方程序集元数据和运行配置：

| 文件 | SHA-256 |
| --- | --- |
| `TheBrain 14.0.102.0 Installer.exe` | `231b8c6bad468b031bcc283c109fa8f117f4574cff0211f15c5d5fe9ca9324d6` |
| 官方载荷 `TheBrainCore.dll` | `70356c4e3c243b8072d839754e69d34346803dec4a3c38dca336d2fb0a50d2fa` |

元数据印证 Normal/Type/Tag/System 的节点类别数值；没有恢复完整业务源码。运行配置 `TheBrain.Windows.App.runtimeconfig.json` 指向 `net6.0`、`Microsoft.NETCore.App 6.0.0` 和 `Microsoft.WindowsDesktop.App 6.0.0`。

当前执行环境为 Linux，未配置运行该 Windows 桌面程序的环境，因此未完成“原应用导入 → 关闭重开 → 再导出”的测试。**原应用测试状态：未验证**。具体检查步骤见 [FORMAT.md](FORMAT.md#分级验证及导入检查)；首次应导入可丢弃的独立测试脑图。

本仓库也没有实际本地脑图数据库样本，`.dll` 存储的格式及直接改写路径仍未知。工具不会改写这些文件。
