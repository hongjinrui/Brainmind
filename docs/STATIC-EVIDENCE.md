# 官方程序集的静态格式证据

本文件记录对官方安装器载荷 `TheBrainCore.dll` 的静态元数据和 CIL 指令分析，这一步没有执行 DLL。随后另行开展了官方数据模型的 JSON 运行时检查，记录在 [OFFICIAL-MODEL-CHECK.md](OFFICIAL-MODEL-CHECK.md)。两者都没有运行授权工具，也不能替代原应用的导入、重开、再导出测试。

分析对象 SHA-256：

```text
70356c4e3c243b8072d839754e69d34346803dec4a3c38dca336d2fb0a50d2fa
```

下面的 TypeDef、MethodDef 编号只针对这一个构建。可复核脚本 [inspect_format_metadata.py](../tools/inspect_format_metadata.py) 先核对摘要，不会将固定编号套用到其他版本。脚本只读取所列格式相关方法，不涉及授权逻辑。它依赖开发工具 `dnfile`、`dncil`，脑图编辑器本身仍无第三方运行依赖。

若已将官方载荷静态提取到 `work/official/app/`，可以复现：

```sh
python3 -m venv work/metadata-venv
work/metadata-venv/bin/pip install dnfile dncil
work/metadata-venv/bin/python tools/inspect_format_metadata.py work/official/app/TheBrainCore.dll > work/format-evidence.txt
```

脚本输出的 `body+0x...` 是相对方法体起始的字节偏移，**包含方法头**；不是某些反编译器扣除方法头后的 `IL_...` 编号。

## 新节点的默认值

节点创建路径 `MethodDef[570]`（token `0x0600023a`）包含以下明确分支：

- `body+0x003d` 至 `0x0044`：若 `Kind == 2`，将 `Label` 初始化为节点名称。
- `body+0x0054` 至 `0x0067`：若 `Kind == 4`，图标类别传入 `8`；否则传入 `1`。其余构造参数为 `""`、`0`、`false`、`false`、`0`、`null`。

因此新普通节点或类型节点的初始状态为 `1::0:False:False:0:`，新标签节点为 `8::0:False:False:0:`。上一版将所有类别都初始化为 `1`，与官方标签创建路径不一致。现有五份导出中的六个标签节点也全部是前缀 `8`：包括 `5G技术` 的“感知”，以及 `AIGC` 的五个标签，构成独立的样本印证。

该路径调用 `MethodDef[2170]`（token `0x0600087a`），后者明确调用 `Guid.NewGuid`，设置 `Name`，并用 `DateTime.UtcNow` 设置创建和修改时间。这直接支持新实体使用随机 UUID；不需要从名称推算已有 UUID，也不能据此恢复历史 UUID v5 的生成输入。

## ThoughtIconInfo 的七段结构

`TypeDef[274]` 表示该状态对象：

- `MethodDef[2608]` / token `0x06000a30`：解析字符串。
- `MethodDef[2609]` / token `0x06000a31`：七参数构造函数。
- `MethodDef[2610]` / token `0x06000a32`：按七个字段序列化。
- `MethodDef[2201]`、`[2202]`：节点的 Raw 属性分别调用该序列化和解析过程。

对已研究的七段简单形式，位置从 **0** 开始：

| 位置 | 类型 | 静态证据支持的含义 |
| --- | --- | --- |
| 0 | 整数枚举 | 图标类别；创建路径证明普通默认 1、标签默认 8，图标附件路径证明自定义 PNG 为 7 |
| 1 | 字符串 | 与图标类别配套的位置/名称信息 |
| 2 | 整数 | 经附件过滤后的计数，图标附件会扣除；本次不重建该字段 |
| 3 | 布尔值 | 最新笔记附件的 `DataLength > 0` |
| 4 | 布尔值 | 过滤后是否存在 `Type=2` 或 `Type=8` 的附件；本次不改写 |
| 5 | 整数 | 关联日历事件的计数，**不是继承的类型图标数量** |
| 6 | 字符串 | 一种特殊附件路径；本次不重建 |

完整解析器还包含对含盘符路径的额外处理，所以不能对任何未知状态串直接 `split(':')` 后任意改写。受限编辑应只处理已识别的七段形式，并保留不相关字段。

## 笔记标记的计算证据

`MethodDef[607]`（token `0x0600025f`）是状态重算路径。`body+0x0023` 至 `0x0093`：

1. 取得当前节点的附件。
2. 以 `MethodDef[1782]` 筛选笔记：`Type == 4`，或 `Type == 1` 且 `Name == "Notes.md"`。
3. 用 `MethodDef[3367]` 取得 `ModificationDateTime`，传入 `OrderByDescending`（`MethodSpec[107]`）。
4. 用 `FirstOrDefault`（`MethodSpec[108]`）选取最新记录。
5. 该记录存在且 `DataLength > 0` 时，传入七参数构造函数的第 4 个参数为真，即字符串位置 3 为 `True`。

`Notes.md` 文件名来自格式辅助类型的常量；复核脚本仅对这两个文件名常量做静态字符运算，输出 `Static note filename: Notes.md`，没有执行 DLL。

因此，新写入非空 Markdown 笔记却保留 `1::0:False:False:0:`，与官方状态重算结果不一致。修改时应根据实际最新笔记附件更新位置 3，保留位置 0–2、4–6；清空笔记也需要重新计算，不能固定写 `True`。若旧 HTML 笔记与 Markdown 共存，要比较修改时间，不能只按文件类型推断哪一份最新。

`body+0x0094` 至 `0x00ab` 调用关联事件查询 `MethodDef[2893]` 并 `Count`，作为位置 5；查询排除 `Status=3` 及具有 `RecurringEventId` 的事件。这说明普通节点增加类型关系不应该据此修改位置 5。

这证明的是文件状态与官方重算规则的匹配，**不证明导入器一定会或不会自动刷新这些字段**。

## 属性类型

`TypeDef[245]` 的 `DataType` 字段签名 `06118390` 指向 `TypeDef[228]`，其枚举常量可直接读取：

| DataType | 官方名称 |
| ---: | --- |
| 1 | Text |
| 2 | Integer |
| 3 | Decimal |
| 4 | Date |
| 5 | Boolean |

`TypeDef[246]` 的属性实例只有 `TextValue` 和 `DecimalValue` 两种值存储字段；`DecimalValue` 的 CLR 类型是 `System.Decimal`。这不代表所有 DataType 的编码细节均已恢复。

仓库实际的 `systemField.thought-type-is-hidden` 定义为 `DataType=2`，属于整数，即使它在业务上看起来像开关，现有值又序列化为 `1.0`，也不能将类型 2 解释为 Boolean。对这类已研究实例，允许小数部分非零的值会违背字段定义。日期、布尔值及其他存储形式应在取得完整映射前拒绝不确定写入。
