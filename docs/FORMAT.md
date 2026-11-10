# BRZ 格式与操作约束

本工具处理 `ExchangeFormatVersion=5` 的已研究结构。TheBrain 14.0.102.0 的应用验证范围见 [VALIDATION.md](VALIDATION.md)。以下规则约束写入；未知字段和未修改内容保持原样。

## 无底稿创建

`create --content content.json [--output new.brz]` 从内容构建新归档，无需输入 BRZ。省略 `--output` 只预演；写出时禁止覆盖并重新读入校验。输入 JSON 保持不变，报告包含 `content_sha256`、`node_ids`（内容 key → UUID）及 `root_ids`。

内容结构：

| 字段 | 约束 |
| --- | --- |
| `nodes` | 非空数组；每项必需唯一非空 `key` 和非空 `name` |
| 节点 `parent_key` | 可省略或为 `null`；否则引用父节点的 key |
| 节点 `note` | 可选 Markdown 字符串，包含读者需要看到的来源与页码 |
| 节点 `label`、`background_color` | 可选标签文字、有符号 32 位 ARGB 整数 |
| `jumps` | 可选数组，元素为 `a`、`b` 两个节点 key，可附 `reason` |
| `edges` | 可选数组，元素含 `a`、`b`、`relation`（`parent` 或 `jump`），可附 `reason`；支持多父 |
| `title` | 可选脑图名称，默认第一个根节点名称；该根节点也作为主页 |
| `schema_version` | 可选，提供时必须为整数 `1` |

端点必须存在，重复关系、自连接和父子环会被拒绝。每项节点创建、样式设置、关系或笔记各占一项操作，合计最多 10000 项。未知键拒绝。兼容的辅助字段为顶层 `subtitle`、`created_at`、`author`、`source`、`design`，节点 `category`、`source_pages`、`source_pdf_page` 和关系 `reason`；这些不会自动显示在应用中，需要展示的内容应写入 `note`。

新脑图、普通节点、连线和附件使用新的 UUID v4。`settings` 仅写脑图名称和主页：`BrainSettingKey` 分别为 19、20，设置 ID 按 `uuid5(BrainId, str(枚举整数))` 计算。该规则经官方程序集静态分析及六份原生导出交叉核对；缺少名称设置的归档可能导入后无法打开，因此不能省略。用户、权限、同步和历史表初始为空。

预演与写出各自生成 UUID 和时间戳，摘要不必相同。`create` 用于新作品；继续编辑现有作品使用 `apply`，保留身份。最小内容见 [example.content.json](../skills/brz-edit/references/example.content.json)。

## 编辑计划

`apply input.brz --plan edit.json [--output edited.brz]` 的计划顶层只允许 `source_sha256` 与 `operations`。摘要必须对应实际输入文件，操作按数组顺序执行；先创建节点再引用。计划失败时回退，输入文件不改，输出必须是另一个未存在的路径。

| `op` | 参数与范围 |
| --- | --- |
| `update_thought` | `id`、`set`；只允许 `Name`、`Label`、`BackgroundColor` |
| `add_thought` | `name`，可选 `id`、`kind`；Kind 为 1、2、4，默认 1 |
| `add_link` | `a`、`b`、`relation`（`parent`/`jump`），可选 `id`；两端为普通节点 |
| `remove_link` | `id`；仅普通父子/跳转，不能删除带附属内容的连线 |
| `set_type` | `id`（普通节点）、`type_id`（类型 UUID，或 `null` 清除） |
| `set_tag` | `id`（普通节点）、`tag_id`、布尔 `present` |
| `set_note` | `id`（普通节点）、`text`（完整 Markdown，替换整份笔记） |
| `reuse_icon` | `id`、`from_id`；受限的同类已有图标复用，见下文 |
| `update_field` | 已有字段实例 `id`、数值 `value`；只支持 Integer/Decimal |

未知参数拒绝。新记录可省略 `id` 自动生成；需要在后续操作引用时先分配 UUID v4。同名节点不合并，已有 UUID v4/v5 均不重算。重复设置相同类型或标签为无变更操作。更换类型会丢失连线样式或未知字段时拒绝执行。

## 容器、身份和关系

归档可作为 ZIP 读取；`meta.json` 为单对象，其他已知表是 UTF-8 NDJSON，通常带 BOM，空表可能只有 BOM。保留 BOM、换行风格、未知 ZIP 成员及未改记录的原始字节。`meta.AttachmentFileStates` 按附件 ID 登记，不是节点 ID。

普通实体按 `Id` 唯一，`BrainId` 须与归档一致。原生 `access.json` 可没有 `Id`，此时按 `(BrainId, AccessorId)` 复合键校验与比较，`AccessType` 必须为整数。权限记录只保留，不伪造 ID 或权限。

节点 `Kind`：普通=1、类型=2、事件=3、标签=4、系统=5。工具创建普通/类型/标签；新类型默认 `Label=Name`。普通/类型节点初始 `ThoughtIconInfo` 为 `1::0:False:False:0:`，标签为 `8::0:False:False:0:`。这些默认值已由官方创建路径及样本印证。

连线需联合解释 `Relation` 与 `Meaning`：

| 用途 | Relation | Meaning | A → B |
| --- | ---: | ---: | --- |
| 普通父子 | 1 | 1 | 父 → 子 |
| 跳转 | 3 | 1 | 两端联系，无父子方向 |
| 类型分配 | 1 | 2 | 类型 → 普通节点 |
| 类型层级 | 1 | 3 | 上级类型 → 下级类型，保留但不编辑 |
| 标签分配 | 1 | 5 | 标签 → 普通节点 |
| 固定 | 1 | 6 | 系统 Pinned → 节点，保留但不编辑 |

`Direction=-1` 不表示父子方向。`set_type` 同步 `TypeId` 与类型连线；标签通过连线维护，不靠写 `TagIds`。带附件、属性、事件、访问记录或 UUID 目录成员的连线不能直接删除。

## 笔记、图标和附件

| 内容 | 关键字段 | 成员路径 |
| --- | --- | --- |
| Markdown | `Type=1, NoteType=4, SourceType=2, Name=Location=Notes.md` | `{SourceId}/Notes.md` |
| 旧 HTML | `Type=4, NoteType=2, SourceType=2` | `{SourceId}/Notes/notes.html` |
| PNG 图标 | `Type=5, IsIcon=true, SourceType=2` | `{SourceId}/.data/Icon.png` |
| 普通文件 | `Type=1, NoteType=0` | `{SourceId}/{Location}` |
| 壁纸 | `SourceType=1, IsWallpaper=true` | 脑图来源目录中的文件 |
| URL | `Type=3` | 外部引用，一般无内嵌文件 |

`set_note` 同步 UTF-8 字节长度、附件时间和文件登记，保留旧 HTML。目标 `Notes.md` 被其他附件共用或有未登记文件时拒绝覆盖，即使共用文件当前缺失也不能重新创建冒充另一附件的内容。

`ThoughtIconInfo` 的已识别形式有七段，位置从 0 开始。官方状态重算按笔记附件的 `ModificationDateTime` 选择最新记录，以 `DataLength>0` 设置位置 3；笔记附件为 `Type=4` 或 `Type=1 && Name=Notes.md`。位置 5 是日历事件数量。工具只更新笔记布尔段并保留其他段；缺失或无法识别的状态/时间拒绝写入。旧 HTML 时间更晚时，新 Markdown 不一定成为应用显示的笔记。

图标复用限同 BRZ、相同 Kind：来源缓存串为 `7::0:False:False:0:`，且恰有一个受支持的 PNG 图标；目标为简单 `1::0:False:False:0:` 或 `7::0:False:False:0:`，无类型、标签、笔记或其他附件状态。目标已有图标不替换。

已知 `SourceType=1/2/3` 分别引用脑图/节点/连线；悬空来源拒绝。原生再导出有一种受限壁纸例外：旧来源目录不同于当前 BrainId，但 `Type=1, NoteType=0, IsWallpaper=true, IsBrainAttachment=true, IsIcon=false, IsBrainIcon=false`，内嵌成员存在，且整数 `DataLength` 与实际字节数一致。这种情况保留并警告，不移动文件或重写归属。其他缺失来源仍报错。未改附件已有长度偏差可报告警告，不批量修补无关历史内容。

## 属性与支持边界

官方属性类型为 Text=1、Integer=2、Decimal=3、Date=4、Boolean=5。当前仅修改已有 Integer/Decimal 定义的 `DecimalValue`；整数要求整值，两者均检查 .NET Decimal 的 96 位系数与最多 28 位小数。JSON 解析会损失精度时拒绝读取或写入。文本、日期、布尔编码及属性定义创建不支持。

不支持删除节点、合并身份、任意文件附件上传、前景色写入、事件/固定/权限编辑及云同步。未知字段保持原样，不猜测运行中数据库结构。格式结论来自样本、官方类型/创建规则分析和应用实测；官方模型 JSON 检查曾覆盖节点、关系、附件及属性的 876 条记录，但其本身不能证明应用导入兼容。

## 验证输出

先预演，再写出、校验。已有文件修改用 `compare` 核对记录与成员变化；默认每类最多 100 项明细，`truncated=true` 时增加 `--limit`。读取笔记默认最多 20000 字符，截断时增加 `--note-limit`（最高 1000000）或另读全文。`inspect` 成功退出仍可能含校验错误，应另外检查 `validate`。

写入器发布前重读临时 ZIP，校验结构并逐成员检查预期字节。原应用可能重分配 UUID 或规范化字段，应用往返需核对语义和附件，不能只比数量或 ID；步骤见 [VALIDATION.md](VALIDATION.md#如何验证)。
