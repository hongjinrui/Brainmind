# BRZ 离线编辑格式记录

本文记录仓库样本和随包 TheBrain 14 文档中可核对的事实。这里的 coding 指通过代码维护脑图的能力，不要求存在名为 `coding-base` 的文件。

**当前状态：实验性的 BRZ 离线编辑。结构校验通过，不等于已被原应用成功导入。** 没有恢复软件源代码，也没有确认用户电脑上所谓 `.dll` 脑图存储的格式。

## 证据范围

四卷 `Brainmind.7z.001` 至 `.004` 按编号拼接后解压，相关文件在 `TheBrain14 - v2/`。样本如下：

| BRZ 文件 | thoughts | links | attachments |
| --- | ---: | ---: | ---: |
| `elon 2025-03-21.brz` | 68 | 126 | 58 |
| `test2 2025-03-21.brz` | 2 | 1 | 1 |
| `test2 2025-03-21 (1).brz` | 14 | 15 | 8 |
| `5G技术 2025-06-19 (1).brz` | 40 | 58 | 14 |
| `AIGC 2025-09-22.brz` | 163 | 199 | 60 |

下文的文件名和行号指 **BRZ 内成员**；解码时使用 `utf-8-sig`，行号从 1 开始。它们不是仓库中另存的同名 JSON 文件。

随包 `TheBrain14-User-Guide-v01.pdf` 印刷页 247–248（PDF 物理页 254–255）介绍备份为 BRZ；p248 明确警告：

> Brain Archive files are NOT .zip files. You should not use zip file utilities to modify or access your archive files as doing so may corrupt them.

本仓库五份 BRZ 的实际字节可被 Python ZIP 读取，且含明文记录和附件。这只证明这些样本的容器布局，不能推广到所有版本，也不能据此视为官方支持的编辑接口。编辑始终输出副本，原始压缩卷和原 BRZ 保持不变。

## 容器与记录

所有样本 `meta.json` 的 `ExchangeFormatVersion` 都是 `5`。`meta.json` 是单个 JSON 对象，包含 `BrainId` 和可能存在的 `AttachmentFileStates`。

以下文件是逐行 JSON（NDJSON），**不是 JSON 数组**：

```text
thoughts.json       links.json          attachments.json
settings.json       modificationlogs.json
brainusers.json     syncpoints.json     tombstones.json
calendarevents.json access.json
fielddefinitions.json                  fieldinstances.json
```

样本通常带 UTF-8 BOM，记录以换行分隔；空表仅有 BOM。应保留源文件的 BOM、换行风格和未修改记录的原始字节。不要把空表改写成 `[]` 或 `{}`。

所有实体均有 `Id`、`BrainId`、`CreationDateTime`、`ModificationDateTime`；部分记录还含 `SyncUpdateDateTime`、`SyncSentId`。后两者与同步有关，现有分析不足以实现云同步协议。日志、同步点、墓碑和访问控制表保留原状，不伪造应用历史。

## 节点及 UUID

`thoughts.json` 保存节点本身，不在节点对象中列出父子 UUID。常见字段：

| 字段 | 样本含义与处理 |
| --- | --- |
| `Id` | 节点 UUID，也是笔记和图标目录名；修改名称不改变此值 |
| `Name` | 显示名称；可能重复，不能充当唯一键 |
| `Kind` | 节点类别，见下表 |
| `TypeId` | 类型节点的 UUID；普通节点分配和类型层级的关系编码不同，见下节 |
| `TagIds` | 所有样本均为空数组；不能仅靠写此数组实现标签 |
| `Label` | 标签文字字段；与作为分类节点的 Tag 不同 |
| `BackgroundColor` | 样本有符号 32 位颜色整数 |
| `ThoughtIconInfo` | 冒号分隔的内部状态串；未完成各段语义验证，不应任意拼接 |
| `ForgottenDateTime` | 部分样本存在的遗忘标记；不是删除指令 |

官方安装包内 `TheBrainCore.dll` 的枚举元数据给出 `ThoughtKind`：`Undefined=0`、`Normal=1`、`Type=2`、`Event=3`、`Tag=4`、`System=5`。样本同时印证 `Normal/Type/Tag/System`；没有据此实现 Event 创建。

例：`test2 2025-03-21 (1).brz/thoughts.json:4` 是“加法”，UUID 为 `3ff111a6-fd5e-44eb-8e76-e58a475b8370`，`TypeId` 指向第 6 行“已学” `68a53d65-bb6e-4d9d-847c-f88f71f1e8ae`；第 14 行是系统节点 `Pinned`。

样本含 UUID v4 和 v5。`AIGC` 的 163 个节点中 162 个是 v4，`5G技术` 的 40 个节点均为 v5。v4 通常用于随机生成，v5 是由 namespace 和名字计算的标准形式；**现有材料未确定该应用使用的 v5 namespace 或输入**。不得从显示名称重算已有 UUID。新实体使用不重复的 UUID v4，并保留所有已有引用。

颜色可按常见 ARGB 32 位形式理解；例如“已学”的 `BackgroundColor=-16728064` 对应无符号十六进制 `FF00C000`。这是基于样本数值的解释，显示、继承和透明度效果仍待应用验证。`ForegroundColor` 没有本批样本值，工具不支持修改前景色。

## 关系：一份 links 表表达多种含义

每条 link 有 `Id`、`ThoughtIdA`、`ThoughtIdB`、`Kind`、`Relation`、`Direction`、`Meaning`、`Thickness` 及时间戳。必须综合 `Relation` 与 `Meaning`，不能把所有关系都当父子树。

| 用途 | `Relation` | `Meaning` | A → B | 可复查证据 |
| --- | ---: | ---: | --- | --- |
| 普通父子 | 1 | 1 | 父 → 子 | `test2 (1)/links.json:2`：“学习路线demo”→“加法” |
| 跳转 / 联系 | 3 | 1 | 两端联系；不要解释为父子 | `5G技术/links.json:1`：“物联网”→“多用户” |
| 类型分配 | 1 | 2 | 类型 → 普通节点 | `test2 (1)/links.json:14`：“已学”→“加法” |
| 类型层级 | 1 | 3 | 上级类型 → 下级类型 | `elon/links.json:95`：“Tutorial”→“Tutorial Video” |
| 标签分配 | 1 | 5 | 标签 → 被标记节点 | `5G技术` 的“感知”→“情境感知” |
| 固定 | 1 | 6 | Pinned → 被固定节点 | `test2 (1)/links.json:9`：“Pinned”→“已学” |

表中缩写 `test2 (1)`、`5G技术` 和 `elon` 分别指上文完整 BRZ 文件名。上述用途是根据样本节点类别、`TypeId` 和已知学习路线交叉核对得到的解释；应用内部 `Relation/Meaning` 的完整枚举尚未恢复。样本中还出现其他 `Meaning`，应原样保留。

全部样本 link 的 `Direction=-1`、`Kind=1`、`Thickness=-1`。因此 `Direction` 不能直接用于判断“父”还是“子”。添加普通父子关系时用 A 父 B 子，查找某节点的父节点时反向查询 B；跳转联系需查两端。脑图是图而非单父树，一个节点可以有多个父节点。

对普通 `Kind=1` 节点使用 `set_type` 时，必须同时维护其 `TypeId` 和 `Meaning=2` 关系，不能只改一方。类型节点也可能有 `TypeId`：`elon 2025-03-21.brz/thoughts.json:28` 的 `Tutorial Video`（`Kind=2`、UUID `602babe5-64ba-5e66-b8d7-96392718d6b0`）指向 `Tutorial`（`9661b46c-abf5-5707-a30c-6582fd57c7e1`），对应第 95 行 link 的 `Meaning=3`。这类现有层级原样保留，工具不支持修改；不能把所有 `TypeId` 都强行改成 `Meaning=2`。

标签操作维护 `Kind=4` 标签节点及 `Meaning=5` 关系，保留样本中的 `TagIds`。移除普通关系不得误删类型、标签或固定关系。

## 笔记、图标和附件

`attachments.json` 的 `SourceId` 关联节点或脑图；附件本身还有独立 `Id`。常见布局：

| 内容 | 元数据 | ZIP 成员路径 |
| --- | --- | --- |
| Markdown 笔记 | `Type=1, NoteType=4, SourceType=2`，`Location=Notes.md` | `{SourceId}/Notes.md` |
| 旧 HTML 笔记 | `Type=4, NoteType=2, SourceType=2`，`Location=notes.html` | `{SourceId}/Notes/notes.html` |
| 节点图标 | `Type=5, IsIcon=true, SourceType=2` | `{SourceId}/.data/Icon.png` |
| 普通文件 | 样本 `Type=1, NoteType=0` | `{SourceId}/{Location}` |
| 脑图壁纸 | `SourceType=1, IsWallpaper=true` | `{BrainId}/wallpaper.png` |
| URL / 跨脑图引用 | 样本有 `Type=3`，`Location=brain://...` | 通常没有对应文件 |

`test2 (1)/attachments.json:4` 的“加法”笔记指向 `3ff111a6-fd5e-44eb-8e76-e58a475b8370/Notes.md`，`DataLength=100`。改笔记需同步真实 UTF-8 字节长度、附件时间戳和相关成员；新建笔记还需登记 `meta.AttachmentFileStates[附件Id]=1`。这是附件 ID，不是节点 ID。`set_note` 保留 `ThoughtIconInfo` 缓存串，因为其内部协议尚未确定；导入后可能需要应用刷新状态，笔记标记及显示效果待人工核对。

图标既涉及图片成员和附件元数据，也涉及 `ThoughtIconInfo`。工具仅复用同一 BRZ、相同 `Kind` 的已有图标模板：来源缓存串必须为 `7::0:False:False:0:`，目标必须是简单的 `1::0:False:False:0:` 或 `7::0:False:False:0:` 状态，且没有类型、标签、笔记或其他附件状态；来源需恰有一个已有的 `Type=5` PNG 图标附件。目标已有图标时不支持替换。工具不声称已实现任意图标编码。示例先为新建的类型节点复用“已学”的图标，再把普通节点分配到这个新类型；类型图标可能被普通节点继承，实际显示需在应用检查。

原始样本已存在元数据偏差：`test2 (1)` 壁纸附件的 `DataLength=328177`，ZIP 中实际为 `186032` 字节。未改附件的既有差异应报告警告；不能为了“修复”无关字段而扩大编辑范围。

## 属性

`fielddefinitions.json` 定义字段；`fieldinstances.json` 保存字段实例。

本批仅 `elon` 有非空属性样本：

```json
{"Name":"systemField.thought-type-is-hidden","DataType":2,"Id":"1649d341-888d-548d-b810-8ff2ca7902fb"}
```

其 `fieldinstances.json:1` 有：

```json
{"FieldDefinitionId":"1649d341-888d-548d-b810-8ff2ca7902fb","SourceId":"9661b46c-abf5-5707-a30c-6582fd57c7e1","SourceType":2,"DecimalValue":1.0,"Id":"1305f070-4945-430e-a2a5-25b01c3207f2"}
```

上面为省略时间戳和 `BrainId` 的摘录。可确认“定义 UUID → 实例 → 来源 UUID”的引用链；不能仅凭一例推出完整 `DataType` 枚举。工具仅受限更新已有实例的已有值字段，不负责创建未知类型的属性体系。

## 本地数据库与应用边界

本仓库没有用户实际脑图存储目录，因此无法比较 BRZ 与所谓 `.dll` 数据文件，不能判断那些文件是数据库、程序库还是其他格式，更不能直接写入。

官方安装包含 `Microsoft.Data.Sqlite.dll`、`TheBrain.Data.Sqlite.dll`、`e_sqlite3.dll` 等 SQLite 相关库，说明应用含 SQLite 访问组件；它们不是用户脑图数据库样本，也不足以确定本地数据库表结构。官方载荷目标为 `net6.0` / `Microsoft.WindowsDesktop.App 6.0`；当前 Linux 环境未完成原应用导入测试。

不要操作运行中的脑图、数据库、同步目录、授权或激活文件。离线编辑不需要执行压缩包中的授权工具。

## 分级验证及导入检查

工具应在修改前验证容器、逐行 JSON、UUID 唯一性、BrainId、引用完整性；计划绑定原 BRZ 的 SHA-256，先 dry-run，再输出不同路径的新 BRZ。输出后重新校验，比较未涉及 ZIP 成员和未改记录的字节，核对原件 SHA-256 未变。记录修改清单、输入和输出摘要、校验错误与警告。

这些检查只能证明离线结构和编辑范围。**在完成以下人工测试之前，不称为“兼容原软件”或“已成功导入”。**

1. 在正常可用的 TheBrain 14 中备份现有脑图，准备一个新建、可丢弃的测试脑图；不要把第一次测试合并到日常脑图。
2. 使用应用 `File → Import → Brain Archive (brz)` 流程导入编辑副本，确认目标是测试脑图。随包手册 p252–253（PDF 物理页259–260）说明当前打开的脑图会被修改，并明确 BRZ 导入不能撤销。
3. 按修改清单检查名称、父子方向、多个父节点、跳转、类型、标签、笔记、颜色和复用图标。打开未修改节点检查其附件仍可访问；记录具体应用版本和结果。
4. 关闭并重新打开测试脑图，再导出为新的 BRZ，用工具复查图结构与笔记，区分应用自行规范化的变化和实际缺失。
5. 只有上述检查通过，才把本次样本的应用往返验证标记为通过。出现导入错误、引用丢失或显示异常时保留原件与报告，停止向正式脑图推广。
