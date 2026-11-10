---
name: brz-edit
description: 将用户提供的 PDF、文字或资料整理为可在 TheBrain 14 打开的新 BRZ 脑图，无需底稿；也可检查和编辑已有 BRZ 副本，维护节点、关系、来源笔记、类型和标签。不编辑运行中的数据库。
---

# BRZ 脑图维护

模型负责理解资料与组织知识，Python 工具负责构建、校验和写出 BRZ。完整包只需 Python 3.10+，生成时不需要已有脑图、Git 或启动 TheBrain。

## 入口与参考

- **独立包**：使用 `python -X utf8 "技能目录/scripts/brz.py"`；运行代码位于同目录的 `brain_toolkit/`，可从任意工作目录启动。格式与验收说明在 `references/docs/FORMAT.md`、`references/docs/VALIDATION.md`。
- **仓库源码**：skill 位于 `skills/brz-edit/`，向上两级是仓库根。在该根使用 `python -X utf8 -m brain_toolkit`，或用 `scripts/brz.py` 的绝对路径。参考文档在仓库 `docs/`。
- 只有 `SKILL.md` 的安装需补装[完整 ZIP](https://github.com/hongjinrui/Brainmind/raw/refs/heads/main/releases/brz-edit.zip)，或定位已有 checkout；不要把说明文件目录当作编辑器。

先读对应的 `FORMAT.md` 与 CLI `--help`。Windows 优先 `py -3` 或实际可用的 `python`，不要假设 Store 的 `python3` 别名是已安装的解释器。下文展示仓库命令，独立包替换为上述启动器前缀。

相对路径以当前工作目录为准，启动器不会改变目录。任务中间文件放 `work/`，不写入技能安装目录。JSON 和报告使用 UTF-8，路径含空格时加引号；PowerShell 5 的 `>` 可能产生 UTF-16，需显式以 UTF-8 保存。

## 从资料创建新脑图

1. 阅读用户资料。PDF 提取与摘要由模型和可用阅读工具完成；读取不完整时说明缺口，不伪造内容或页码。
2. 组织主题、父子关系和有意义的跨主题跳转。每份笔记保留来源文件/链接及 PDF 页码，区分原文结论与自己的解释。PDF 当前以笔记链接引用，不作为文件嵌入。
3. 写 `work/content.json`：`nodes` 非空，每项必需唯一 `key`、`name`，可选 `note`、`parent_key`、`label`、`background_color`。`title` 为脑图名，默认首个根节点名；首个根节点作为主页。`jumps` 使用 key 的 `a`、`b`，多父关系可用 `edges`（另含 `relation`）。完整 schema 和上限见格式文档。需要读者看到的来源或跳转理由要写进 `note`，辅助元数据不会自动显示。
4. 预演，检查内容与规模，再写到新路径：

```sh
python -X utf8 -m brain_toolkit create --content "work/content.json"
python -X utf8 -m brain_toolkit create --content "work/content.json" --output "work/new-brain.brz"
python -X utf8 -m brain_toolkit validate "work/new-brain.brz"
python -X utf8 -m brain_toolkit inspect "work/new-brain.brz" --list
```

新建没有 `source_sha256`，每次分配新身份；不带 `--output` 就是预演，没有 `--dry-run` 参数。按内容 JSON 核对节点、关系和笔记，保存内容、创建报告与输出摘要。包内 [example.content.json](references/example.content.json) 可直接试跑。继续维护该作品应走下面的 `apply`，避免重新创建身份。

## 编辑已有 BRZ

1. `inspect "work/input.brz" --list` 和 `validate "work/input.brz"` 检查输入；用 `inspect ... --id UUID --notes` 读取目标及关系、附件、笔记上下文。同名节点按 UUID 和上下文定位，不能默认选第一个。`inspect` 退出成功不代表没有校验错误。
2. 创建仅含 `source_sha256`、`operations` 的 JSON 计划。摘要取实际输入；先创建节点再引用，需后续引用的新节点预分配 UUID v4。`set_note` 替换整份 Markdown，补充前必须读全文。
3. 先预演，再写副本和比较：

```sh
python -X utf8 -m brain_toolkit apply "work/input.brz" --plan "work/edit.plan.json"
python -X utf8 -m brain_toolkit apply "work/input.brz" --plan "work/edit.plan.json" --output "work/edited.brz"
python -X utf8 -m brain_toolkit validate "work/edited.brz"
python -X utf8 -m brain_toolkit compare "work/input.brz" "work/edited.brz"
```

输入输出必须不同，输出不能已存在。核对原件 SHA-256、计划操作和无关成员字节，保存报告。重复生成可能有不同时间戳与自动 UUID，按语义和范围验收。编辑操作的精确参数见 `FORMAT.md` 的“编辑计划”。已有授权内的可逆编辑可直接执行，无须再添加例行审批。

## 必须保留的约束

- 笔记或比较报告有 `truncated=true` 时增加 `--note-limit` / `--limit` 或另读全文，不能把截断结果当完整内容。
- 父子 A=父、B=子；跳转没有父子方向。类型需同步 `TypeId` 和类型连线，标签由标签连线维护；使用专用操作，不直接改 JSON 表。
- 保留已有 UUID、未知字段、无关成员、日志和未改记录字节。输入错误、摘要不符或不支持的操作应停止对应写入，不能删除依赖来绕过校验。
- 笔记需受支持的七段状态串、时间戳和独占 `Notes.md` 路径；旧 HTML 可能因时间更晚而显示优先。图标仅允许受限的同类模板复用，已有数值属性受 .NET Decimal 精度限制。细节按格式文档执行。
- 不编辑运行中的数据库、同步数据或授权文件，不把历史附件警告扩展为无关批量修复。

## 交付与应用验证

交付新的 BRZ、主要改动和实际验证状态。`create` 成品直接在 TheBrain 中导入并选择“创建新脑图”，无需提前建空白脑图；`apply` 副本使用独立可丢弃目标。首次验证不能合并进日常脑图。

能访问正常可用的原应用时检查内容、关系和附件，关闭重开，再导出核对；应用可能重分配 UUID，需按计划检查语义，不能只比较总数。导入发生身份冲突时保留输入并核对目标，不自行改写 UUID。

离线结构检查通过与原应用验收分开说明。CLI 的 `application_import_verified=false` 表示它没有执行桌面测试；不能手改字段代替验收。无法运行原应用时明确说明本次未验证，当前已测范围见 `VALIDATION.md`。
