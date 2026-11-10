# 通过代码维护 TheBrain 脑图

把 PDF 或文字资料整理成可供 TheBrain 14 打开的新脑图，或维护已有 BRZ 副本。**新建作品不需要已有脑图、空白底稿或 Git checkout**：完整 skill 包内含 Python 编辑器，AI 负责阅读资料、组织节点与来源笔记，工具负责生成和校验 BRZ。生成只需 Python 3.10+ 标准库；TheBrain 用于打开成品和应用验收。格式支持仍属实验范围。

已验证五份现有 BRZ 的文件结构，并用官方数据模型完成六份文件共 876 条记录的 JSON 往返检查。**11 项编辑演示及完全无底稿创建的 38 节点 NIST AI RMF 脑图，已在 Windows 的 TheBrain 14.0.102.0 中完成导入、关闭重开和再导出**。全量核对节点、关系、来源笔记和脑图名称/主页设置均保留，再导出的文件也能继续离线编辑；具体界面抽查与数据核对范围见[验证记录](docs/VALIDATION.md)。这些结果不代表所有 BRZ 或软件版本都兼容。首次使用仍应导入独立测试脑图；原始四卷和其中的原始 BRZ 均保持不变。

## 从哪里开始

- [操作 skill](skills/brz-edit/SKILL.md)：以后让助手整理资料时使用的工作流程。
- [格式分析](docs/FORMAT.md)：节点、关系、UUID、标签、类型、笔记、属性及图标的证据和边界。
- [官方程序静态证据](docs/STATIC-EVIDENCE.md)：补强新节点默认值、笔记状态和属性类型的判断，附可复现脚本。
- [官方数据模型检查](docs/OFFICIAL-MODEL-CHECK.md)：实际加载官方模型验证 JSON 往返，明确与完整应用测试的区别。
- [输入清单与哈希](docs/INVENTORY.json)：四卷及五份 BRZ 的摘要和记录数量。
- [验证记录](docs/VALIDATION.md)：自动检查、示例变化、原应用测试状态。
- [演示计划](examples/demo.plan.json)、[演示 BRZ](examples/test2-code-edit-demo.brz)、[演示报告](examples/demo.report.json)、[逐项差异](examples/demo.diff.json)。
- [PDF 到成品脑图示例](examples/nist-ai-rmf/README.md)：NIST AI RMF 1.0 中文知识图，提供 38 节点的来源笔记及可复现计划生成器。

这是对现有文件格式的研究与工具实现，没有恢复 TheBrain 的软件源码，也没有重建其桌面应用。

## 给其他人直接使用

下载 [完整 brz-edit.zip](https://github.com/hongjinrui/Brainmind/raw/refs/heads/main/releases/brz-edit.zip)，解压后得到 `brz-edit/`。将整个目录安装到所用 AI 助手的 skill 目录，或让能读本地文件、执行 Python 的助手按其中 `SKILL.md` 操作。包内已有运行代码和参考文档；只复制 `SKILL.md` 会缺少编辑器。

给助手提供 PDF 或文字资料，然后提出：

> 用 brz-edit 将这些资料整理成 TheBrain 脑图。为主题建立父子和跨主题关系，笔记保留来源页码与链接，交付可打开的 BRZ。

助手先阅读资料并生成内容 JSON，再调用 `create`。CLI 不直接理解 PDF；PDF 阅读能力由助手及其可用阅读工具提供。完整包无需 pip 依赖、Git、任何原始分卷或预先导出的 BRZ，生成时也不用启动 TheBrain。

可先使用解压目录中的最小示例试跑（将 `brz-edit` 替换为实际安装路径，Windows 可把 `python` 换成 `py -3`）：

```sh
python -X utf8 brz-edit/scripts/brz.py create --content brz-edit/references/example.content.json
python -X utf8 brz-edit/scripts/brz.py create --content brz-edit/references/example.content.json --output work/reading.brz
python -X utf8 brz-edit/scripts/brz.py validate work/reading.brz
```

第一条只预演；第二条生成 3 个中文节点、2 条父子关系、1 条跳转及来源笔记。输出路径必须未存在。把生成的 `.brz` 导入 TheBrain，选择创建新脑图；后续维护用 `apply`，保留已有节点身份。

## 从内容 JSON 新建

仓库使用 `python -X utf8 -m brain_toolkit`；独立包用 `python -X utf8 安装目录/scripts/brz.py`。`create --content` 接收如下内容，无 `source_sha256`，不读取任何底稿：

```json
{
  "nodes": [
    {"key": "root", "name": "资料主题", "note": "# 资料主题\n\n来源：用户提供的资料。"},
    {"key": "idea", "parent_key": "root", "name": "关键观点", "note": "在这里写概括、依据和页码。"}
  ]
}
```

`key` 用于内容中的关系引用；工具为新脑图、节点、连线和笔记分配新 UUID。节点还可包含 `label`、`background_color`，`jumps` 表示跨主题联系，`edges` 可表达多个父节点。参数与限制见 [skill](skills/brz-edit/SKILL.md) 和[格式说明](docs/FORMAT.md#无底稿创建)。来源、页码及关系解释应写入 `note`；仅放在 JSON 辅助元数据中不会自动显示在应用里。

```sh
python -X utf8 -m brain_toolkit create --content work/content.json
python -X utf8 -m brain_toolkit create --content work/content.json --output work/new-brain.brz
python -X utf8 -m brain_toolkit inspect work/new-brain.brz --list
python -X utf8 -m brain_toolkit validate work/new-brain.brz
```

## 恢复原始材料

将四卷保持原名并放在同一目录。安装正常的 7-Zip 后，在仓库根目录执行：

```sh
7z x Brainmind.7z.001 -owork/extracted
```

也可用 7-Zip 图形界面打开 `.001` 并解压到 `work/extracted`。若解压工具要求拼接，应按 `.001`、`.002`、`.003`、`.004` 顺序二进制拼接，再按 7z 解压；不是分别解压四份文件。

相关 BRZ 位于 `work/extracted/TheBrain14 - v2/`。离线编辑工具不需要安装或执行包内的任何 EXE。

## 查看与修改

以下命令在仓库根目录执行；Windows 可将 `python3` 换为已安装的 `py -3` 或 `python`，不要把 Windows Store 的命令别名当成已安装的解释器。CLI 输出 UTF-8 JSON；计划也应保存为 UTF-8。Windows PowerShell 5 的 `>` 可能生成 UTF-16 报告，可改用显式 UTF-8 文件写入。

```sh
python3 -X utf8 -m brain_toolkit --help
python3 -X utf8 -m brain_toolkit inspect "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz" --list
python3 -X utf8 -m brain_toolkit validate "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz"
```

`inspect` 给出源文件 SHA-256，`--list` 给出名称与 UUID。用 `--id UUID` 查看某个节点及其连线、附件、属性，加 `--notes` 读取 Markdown/HTML 笔记原文。每份笔记默认最多显示 20000 字符，并明确标记是否截断；可用 `--note-limit` 调整。修改名称不会改变已有 UUID；同名节点需结合关系上下文判断。`inspect` 成功退出仍可能包含校验错误，编辑前必须检查 `validate` 的结果。

创建 JSON 计划，绑定源文件 SHA-256，列出具体修改。下面只演示改名；摘要和 UUID 必须替换为本次输入的实际值：

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

先预演，再写入新路径；没有 `--output` 就只预演：

```sh
python3 -X utf8 -m brain_toolkit apply "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz" --plan examples/demo.plan.json
python3 -X utf8 -m brain_toolkit apply "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz" --plan examples/demo.plan.json --output work/my-demo.brz > work/my-demo.report.json
python3 -X utf8 -m brain_toolkit validate work/my-demo.brz
python3 -X utf8 -m brain_toolkit compare "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz" work/my-demo.brz > work/my-demo.diff.json
```

工具不覆盖已有输出；若需重新生成，请另取一个新文件名。计划中的新节点采用预分配 UUID v4，后续操作可引用它。未指定 UUID 的新实体会自动生成，因此同一计划重复从原件生成的二进制摘要可以不同。

## 从公开 PDF 制作成品

[NIST AI RMF 示例](examples/nist-ai-rmf/README.md) 将 [2023 年官方 PDF](https://nvlpubs.nist.gov/nistpubs/ai/NIST.AI.100-1.pdf) 重组为“AI 风险管理：从框架到行动”：7 个主分支、38 个节点、37 条父子关系、13 条跨主题跳转和 38 份中文来源笔记。摘要与原创实践提示分开标明；笔记附页码与 PDF 链接，未嵌入 PDF 文件。

现在可直接用 `create --content examples/nist-ai-rmf/content.json --output work/nist.brz` 从这份知识结构生成新脑图，此入口已完成独立导入、重开和再导出。此前基于原生空白导出的 [build_plan.py](examples/nist-ai-rmf/build_plan.py) 仍可用于把内容写入明确选择的现有根节点；它生成绑定源摘要的 133 操作计划，会替换该根的名称和整份笔记并保留其他已有节点。两条流程的具体应用结果分开记录在验证文档中。

无底稿作品原生再导出后，全量核对名称、关系、笔记、来源链接及脑图名称/主页设置均保留；再基于该文件修改一个节点的 Label 也通过离线校验。该内容 JSON 没有显式颜色，黄色背景已由独立小样例验证。第二次编辑尚未重新导入桌面应用，详细变更范围见[新建验收记录](docs/VALIDATION.md#无底稿创建与独立-skill-包)。

## 支持的操作

| 操作 | 能力 |
| --- | --- |
| `update_thought` | 修改名称、Label 文字、背景颜色，保留 UUID |
| `add_thought` | 创建普通、类型、标签节点 |
| `add_link` / `remove_link` | 添加或移除普通父子、跳转关系 |
| `set_type` | 同步普通节点的 TypeId 和类型连线 |
| `set_tag` | 添加或移除标签连线 |
| `set_note` | 新建或更新 Markdown 笔记及附件登记，保留旧 HTML 笔记 |
| `reuse_icon` | 在已研究的简单同类节点间复用已有 PNG 图标模板 |
| `update_field` | 更新已有整数/小数属性的 `DecimalValue`，校验类型和数值范围 |
| `compare` | 按 UUID 查看记录增删改、字段前后值和成员字节变化 |

精确参数和限制见 [skill](skills/brz-edit/SKILL.md)。工具保留未知字段、不相关成员、未改记录原始字节及日志。重复设置同一类型或标签不会重建关系；有附属内容的连线会拒绝直接删除。整个计划失败时不产生部分编辑结果，源摘要不匹配、引用错误或输出冲突会被拒绝；输出发布前还会重新读入并核对实际字节。

## 使用边界

- 仅编辑 `ExchangeFormatVersion=5` 的已研究结构；父子关系允许多个父节点，不将图强行改为树。
- 图标状态串已确认笔记标记等部分字段；笔记编辑会同步标记并保留其他字段，图标复用仍有严格模板限制。同步协议及完整属性编码未实现，显示效果需应用检查。
- 对会在 JSON 解析时损失精度的数字明确拒绝，避免悄悄改变数据；暂不支持文本、日期、布尔属性写入。
- 不删除节点、不合并身份、不创建未知属性定义，不写事件、固定关系或权限。
- 仓库没有实际本地脑图存储目录，不能确定你提到的 `.dll` 数据文件格式；本工具不直接写本地数据库或同步目录。
- `validation.errors=[]` 只表示离线检查通过。CLI 不操作桌面应用，报告中的 `application_import_verified` 始终为 `false`；实际应用验收须另记版本、步骤和语义核对结果。

原应用导入检查步骤见 [格式分析中的验证流程](docs/FORMAT.md#分级验证及导入检查)。

## 以后如何调用

可以直接向助手提出：

> 按本仓库 `skills/brz-edit/SKILL.md`，读取这份 BRZ，把这些资料整理成父子节点并补充笔记，保留已有节点身份，交付新的 BRZ 和修改报告。

仓库的 `AGENTS.md` 也会引导后续代码助手读取这个 skill。安装完整 ZIP 后不需要保留 checkout；旧的单文件安装可补装完整包或继续定位已有 checkout。解释器选择与路径规则见 skill 的“定位与启动”。

## 开发检查

```sh
python3 -X utf8 -m unittest discover -s tests -v
```

合成测试无需任何第三方库或原始分卷，适合开源贡献者直接运行。[跨平台 CI](.github/workflows/tests.yml) 在 Ubuntu 和 Windows、Python 3.10/3.13/3.14 上运行同一测试命令；CI 通过不代替 TheBrain 桌面验收。恢复到上述路径的五份原始导出还会参加可选的实样本检查，未提供这些文件时该项会明确跳过。

测试重点是引用一致性、事务回退、原始字节保留和文件写入边界，还覆盖 Windows 文件同步、旧代码页下中文/emoji 报告、原生访问记录复合键、保守保留导出壁纸，以及笔记文件被其他附件共用时拒绝覆盖。

第一批稳定性修复提交 `e7f3b34489a14d41601d34d26a6c8916cc1de6da` 的 [6 组 CI 检查](https://github.com/hongjinrui/Brainmind/actions/runs/42829276774) 全部通过。此后加入成品计划生成器、无底稿创建和独立包回归，当前本地 Windows 测试为 91 项通过；完整记录与具体版本见 [VALIDATION.md](docs/VALIDATION.md)。历史 CI 成功只覆盖当时的提交，后续提交应查看自身结果。

维护者修改代码、skill 或随包文档后，需要重建独立包。先使用尚未存在的临时输出路径，成功生成后明确替换发布文件，再只读检查一致性：

```sh
python -X utf8 tools/package_skill.py --output work/brz-edit.next.zip
python -c "from pathlib import Path; import os; Path('releases').mkdir(exist_ok=True); os.replace('work/brz-edit.next.zip', 'releases/brz-edit.zip')"
python -X utf8 tools/package_skill.py --check releases/brz-edit.zip
```

打包器将 skill、核心 Python 代码、参考文档和最小内容示例放入顶层 `brz-edit/`，附逐文件 SHA-256 清单；代码不在源码 skill 中重复维护。文件顺序、时间戳、权限和换行固定，使用无压缩 ZIP，相同源码产生相同字节。`--output` 拒绝覆盖已有文件；`--check` 不写文件，发布包与当前源码不一致或缺失时退出失败。跨平台 CI 在测试后运行此检查，防止核心已更新而下载包仍旧。测试还会把包解压到独立目录，在隔离 Python 进程中完成无底稿创建和继续编辑。
