---
name: brz-edit
description: 将用户提供的 PDF、文字或资料整理为可在 TheBrain 14 打开的新 BRZ 脑图，无需底稿；也可检查和编辑已有 BRZ 副本，维护节点、关系、来源笔记、类型和标签。不编辑运行中的数据库。
---

# BRZ 脑图维护

适用于用户把资料做成新脑图，或整理、补充已有 BRZ 的任务。模型负责理解资料与组织知识，Python 工具负责构建、校验和写出归档。完整分发包内含运行代码，只需 Python 3.10+ 即可生成 BRZ；生成阶段不需要 Git、现有 BRZ 或正在运行的 TheBrain。TheBrain 用于打开成品及应用验收。

## 定位与启动

先按本 skill 的实际位置选择入口：

- **完整独立包**：存在 `scripts/brain_toolkit/__main__.py`，使用 `python -X utf8 "技能目录/scripts/brz.py"`，可从任意工作目录执行。参考文档在 `references/README.md`、`references/docs/FORMAT.md`、`references/docs/VALIDATION.md`。
- **仓库源码**：skill 位于 `skills/brz-edit/`，向上两级可找到 `brain_toolkit/__main__.py`。可在仓库根用 `python -X utf8 -m brain_toolkit`，也可用 skill 的 `scripts/brz.py` 绝对路径启动。参考文档在仓库根的 `README.md` 和 `docs/`。
- **旧的单文件安装**：若只有 `SKILL.md`，优先补装 [完整 brz-edit.zip](https://github.com/hongjinrui/Brainmind/raw/refs/heads/main/releases/brz-edit.zip)；或定位现有 Brainmind checkout 按源码方式运行。只复制说明文件不会安装编辑器。

阅读对应位置的格式文档和当前 CLI 帮助。先检查解释器；Windows 优先 `py -3`，也可使用实际可用的 `python`；不要假设 Windows 的 `python3` Store 别名已安装 Python。下文展示仓库命令；独立包将 `python -X utf8 -m brain_toolkit` 替换为 `python -X utf8 "技能目录/scripts/brz.py"`，使用实际绝对路径。若选择 `py -3` 或 `python3`，替换 `python` 前缀。

```sh
python -X utf8 --version
python -X utf8 -m brain_toolkit --help
```

输入输出的相对路径均以当前工作目录为准，启动器不会改变工作目录。将本次资料、内容 JSON、计划和报告放入任务的 `work/`，不要写入安装的 skill 目录。含空格的路径加引号。JSON 保存为 UTF-8（允许 UTF-8 BOM）；捕获 CLI 报告时按 UTF-8 解码和保存。Windows PowerShell 5 的 `>` 可能产生 UTF-16 文本，需显式以 UTF-8 写报告，或用 Python 捕获子进程输出后保存。

已完成的验证范围见对应文档目录的 `VALIDATION.md` 和 `OFFICIAL-MODEL-CHECK.md`。每次交付分别说明离线验证与本次原应用导入、重开、再导出的实际结果。CLI 的 `application_import_verified=false` 表示 CLI 本身没有执行应用测试，不能通过手改该字段代替验收。

## 从 PDF 或文字创建新脑图

1. 先完整读取用户资料。PDF 语义提取和摘要由模型及环境中的阅读工具完成，CLI 接收整理后的 JSON。扫描件先提取可读文字；读取不完整时说明缺口，不伪造结论、来源或页码。
2. 组织主题、父子关系和有实际意义的跨主题跳转。在 Markdown 笔记中保留来源链接或文件名、PDF 页码，并区分原文结论和自己的解释。外部 PDF 在笔记中引用，当前不会作为文件附件嵌入归档。
3. 保存 `work/content.json`。新建内容没有 `source_sha256`，不要求用户先准备底稿。顶层 `nodes` 为非空数组；每个节点必需唯一 `key` 和 `name`，可选 `note`（完整 Markdown）、`parent_key`（省略或 `null` 为根）、`label`、`background_color`（有符号 ARGB 整数）。可选顶层 `title` 用作脑图名称，省略时取第一个根节点名称；首个根节点作为主页。`jumps` 可选，使用节点 key 的 `a`、`b` 和可选 `reason`；多父关系可用 `edges`，元素含 `a`、`b`、`relation`（`parent` 或 `jump`）及可选 `reason`。关系端点必须存在，重复关系、自连接、父子环会被拒绝。
4. `python -X utf8 -m brain_toolkit create --content "work/content.json"` 预演；确认内容和规模后增加 `--output "work/new-brain.brz"` 写出新文件。输出不能已存在。每次 create 分配新的脑图和实体 UUID；它用于新作品，已有作品修改改走下节 `apply`，不要重建身份。
5. 运行 `validate` 与 `inspect --list` 核对节点数量、关系、笔记和来源，再按内容 JSON 逐项核对。没有输入 BRZ 时不使用 `compare` 假装存在原件；生成后的继续编辑才比较前后副本。保留内容 JSON、创建报告、输出摘要和验证结果。
6. 交付 BRZ 与简短使用说明；能访问正常可用的 TheBrain 时按文末做独立测试导入和重开，否则明确本次应用测试未执行。

独立包自带 [example.content.json](references/example.content.json)，可以直接试跑：

```sh
python -X utf8 "技能目录/scripts/brz.py" create --content "技能目录/references/example.content.json"
python -X utf8 "技能目录/scripts/brz.py" create --content "技能目录/references/example.content.json" --output "work/reading.brz"
python -X utf8 "技能目录/scripts/brz.py" validate "work/reading.brz"
```

`reason` 和顶层 `source`、`design` 等辅助元数据不自动变成应用字段；需要读者看到的来源或跳转说明要写入 `note`。新建计划合计最多 10000 项操作：每个节点、每次样式设置、每条关系和每份笔记分别占一项；大资料先合理归纳，超限时明确缩小范围。完整示例格式以当前 `create --help` 和随包示例为准。

## 编辑已有 BRZ

1. 使用上述已确认的 CLI 入口。若输入是四卷原始包，使用仓库说明恢复到 `work/`；保留四卷和原 BRZ。不要执行包内授权或激活工具。
2. 运行 `python -X utf8 -m brain_toolkit inspect "work/input.brz" --list` 和 `python -X utf8 -m brain_toolkit validate "work/input.brz"`；使用 `inspect "work/input.brz" --id UUID --notes` 读取目标详情和 Markdown/HTML 笔记。`inspect` 成功退出仍可能含 `validation.errors`，不能代替 `validate`。笔记默认每份限制 20000 字符；若 `truncated=true`，用 `--note-limit` 增加上限（最多 1000000）或另读完整内容。检查版本、节点和关系数量、UUID、类型/标签、附件、警告；读取目标及其父子、跳转、类型、标签和笔记上下文。
3. 以 UUID 选择已有节点。名称可能重复，不可仅凭文字自动选中第一个。能从上下文唯一定位时继续；存在影响编辑结果的歧义时向用户澄清，同时推进独立工作。
4. 根据用户目标创建 JSON 计划，必须含原 BRZ 的 `source_sha256`；列清具体操作、目标 UUID、新名称/笔记、关系端点、复用模板。操作按数组顺序执行，先创建节点再引用。`set_note.text` 会替换整份 Markdown，补充笔记时先读全文再合并。把草稿、检查结果和中间文件置于 `work/`。用户已授权的日常可逆编辑可直接执行，无须额外确认。
5. 运行 `python -X utf8 -m brain_toolkit apply "work/input.brz" --plan "work/edit.plan.json"` 预演；**省略 `--output` 即为 dry-run，没有 `--dry-run` 参数**。检查修改清单后，增加 `--output "work/edited.brz"` 执行计划写副本并保存报告，再运行 `python -X utf8 -m brain_toolkit validate "work/edited.brz"`。输入输出必须为不同路径，且输出文件不能已经存在。预演与实际写出可能有不同的自动生成 UUID 和时间戳，按计划操作与最终输出核验，不要求二进制摘要相同。
6. 运行 `python -X utf8 -m brain_toolkit compare "work/input.brz" "work/edited.brz"` 并以 UTF-8 保存为 `work/edit.diff.json`，核对原件 SHA-256 未变、未涉及成员及记录未变、计划操作均完成、没有悬空引用；保留变更报告和新文件摘要。比较报告默认每类至多 100 项明细，若有 `truncated=true`，使用 `--limit` 增大上限；不要把截断列表当成全部变化。若发现原样本已有警告，单列为原有问题，不随意扩展修改范围。
7. 交付编辑副本、改动说明和验证状态。如果环境能运行正常可用的原应用，按格式文档完成测试脑图导入；否则明确报告“离线结构通过，原应用导入未验证”，保留后续人工检查步骤。

## 操作选择

| 需求 | 工具操作 | 必须维护的约束 |
| --- | --- | --- |
| 改名/标签文字/背景色 | `update_thought` | 仅允许 `Name`、`Label`、`BackgroundColor`，保留 UUID；前景色修改不支持 |
| 新增普通/类型/标签节点 | `add_thought` | `Kind` 仅 1、2、4；新 UUID 不重复；类型默认 Label=Name、标签默认图标前缀 8 |
| 添加/移除普通父子或跳转 | `add_link` / `remove_link` | 父子 A=父 B=子；跳转为联系；不得使用普通移除误删类型/标签关系 |
| 改普通节点类型 | `set_type` | 仅 `Kind=1` 目标，同步 `TypeId` 与 `Meaning=2` link；已有 `Kind=2` 类型层级用 `Meaning=3`，保留且不编辑 |
| 添加/移除标签 | `set_tag` | 使用 `Kind=4` 标签与 `Meaning=5` link；不靠改 `TagIds` |
| 更新/新建 Markdown 笔记 | `set_note` | 同步附件元数据、UTF-8 长度、文件成员和状态串的笔记布尔段；按修改时间选最新笔记，保留其他状态段及旧 HTML |
| 复用已有图标 | `reuse_icon` | 同 BRZ、同 `Kind` 的简单图标模板；目标无类型/标签/笔记/其他附件，已有图标不替换 |
| 修改已有属性值 | `update_field` | 仅已有 Integer/Decimal 定义的 DecimalValue；整数必须整值、数值满足 .NET Decimal 范围和精度 |

已有 BRZ 的编辑计划顶层只允许 `source_sha256` 与 `operations`，与新建内容 JSON 不同。各操作参数如下；未知键会被拒绝，创建记录的 `id` 可省略由工具生成，但需要在后续操作引用的新节点应预先分配 UUID v4。

| `op` | 参数 |
| --- | --- |
| `update_thought` | `id`, `set` 对象 |
| `add_thought` | `name`, 可选 `id`, 可选 `kind`（默认 1） |
| `add_link` | `a`, `b`, `relation`（`parent` 或 `jump`），可选 `id`；两端均需为普通节点 |
| `remove_link` | `id`（link UUID） |
| `set_type` | `id`（普通节点），`type_id`（类型 UUID，或 `null` 清除） |
| `set_tag` | `id`（普通节点），`tag_id`，`present`（布尔值） |
| `set_note` | `id`（普通节点），`text`（完整 Markdown 字符串） |
| `reuse_icon` | `id`（目标），`from_id`（同 `Kind` 模板节点）；来源缓存串为 `7::0:False:False:0:`，目标为简单 `1::0:False:False:0:` 或 `7::0:False:False:0:`，目标无类型/标签/附件状态 |
| `update_field` | `id`（字段实例 UUID），`value`（数值）；只支持 DataType=2/3 的 DecimalValue |

最小计划示例（将摘要和 UUID 替换为本次 `inspect` 的实际值）：

```json
{
  "source_sha256": "原BRZ的64位SHA-256摘要",
  "operations": [
    {
      "op": "update_thought",
      "id": "目标节点UUID",
      "set": {"Name": "新名称"}
    }
  ]
}
```

仓库 `examples/demo.plan.json` 提供针对原始 `test2 2025-03-21 (1).brz` 的 11 项操作示例：新增普通节点、标签节点、类型节点，为新类型复用同类图标，再将普通节点分配到该类型。不能原样套到其他输入。重新执行已编辑文件需要按新摘要建立新计划；同一计划不会自动幂等应用。

## 格式约束

- BRZ 样本可作为 ZIP 读取；`meta.json` 是单对象，其余表为带 BOM 的 NDJSON，空表可能只有 BOM。官方手册 p247–248 同时警告手工使用 ZIP 工具可能损坏归档，故此流程始终视为实验并保留原件。
- 父子、联系、类型、标签、固定共用 links 表：普通父子 `(Relation,Meaning)=(1,1)`，跳转 `(3,1)`，普通节点类型分配 `(1,2)`，类型层级 `(1,3)`，标签 `(1,5)`，固定 `(1,6)`。`Direction=-1` 不表示父子方向。工具只编辑普通节点类型分配，保留已有类型层级。
- 同名节点保留各自身份；已有 UUID v4/v5 不重算。新 UUID 可用 v4，未知 v5 namespace 不猜测。
- 保留未知字段、日志、同步点、墓碑、权限和未修改附件；不宣称实现云同步。不要直接编辑运行中的数据库或用户描述但尚无样本的 `.dll` 存储。
- 不批量修正无关历史问题。原样本的附件 `DataLength` 可能已有差异，依据当前编辑范围解释警告。
- 重复设置相同类型/标签是无变更操作。带笔记、属性、事件或成员文件的连线不能直接删除；更换类型需要丢弃旧连线样式或未知字段时也拒绝执行。遇到这类拒绝，先说明附属内容及需要保留的信息，不直接删除依赖来绕过检查。
- 笔记编辑要求可识别的七段状态串和时间戳，只更新位置 3；位置 5 是日历事件数量。旧 HTML 若时间戳更晚，刚写入的 Markdown 不一定成为应用显示笔记，需结合报告和应用检查说明。
- Markdown 笔记必须采用受支持的 `Name=Notes.md` 和 `{ThoughtId}/Notes.md` 路径。其他附件共用同一路径或存在未登记的笔记文件时拒绝覆盖，不删除附件登记来绕过检查。
- 属性编码不确定、或 JSON 数字会发生精度损失时停止对应写操作；不把这些值强行转为浮点数或猜测日期/布尔编码。格式依据见 `docs/STATIC-EVIDENCE.md`。

## 原应用验证

在正常可用的 TheBrain 中验证：`create` 的新作品直接导入并选择“创建新脑图”，该导入结果即为独立测试脑图，无需提前手工创建空白脑图。`apply` 的编辑副本则使用可丢弃的测试目标。检查本次内容与未改附件，关闭重开，再导出并比对。随包手册 p252–253 指出向当前脑图导入会修改其内容且不能撤销；首次验证不能合并进用户的日常脑图。

`create` 生成新的 `BrainId`，首次导入时选择创建新脑图。`apply` 输出保留输入身份：仅在使用已有 seed 的旧流程中，如果创建新脑图导入被本机相同 ID 的 seed 阻挡，才可在确认 seed 为本次自建、可丢弃且已有原生导出备份后，用应用“分配脑图的新 GUID...”为该 seed 分配新身份再试。新建流程无需此步骤，日常脑图不适用；不要手改 BRZ 内部 UUID 来绕过冲突。

原应用可能重分配 UUID。此时 `compare` 的按 UUID 差异会显示大量增删，需另按本次计划核对节点、父子方向、类型/标签、Markdown 和附件字节；不能仅凭记录总数或同名节点判定成功。若再导出版本不被当前工具接受，记录完整错误与样本，不能手工删除权限记录或改写未知引用以强行通过。

遇到未知版本、无效输入、缺失引用、冲突 UUID、计划摘要不匹配或超出已研究格式的需求时，停止对应写操作，报告具体原因和已完成工作。不要通过修改授权文件或猜测本地数据库来绕过阻碍。
