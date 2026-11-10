# 通过代码维护 TheBrain 脑图

本仓库现在包含一套 **实验性的 BRZ 离线编辑工具**。无需找回原软件源代码，即可读取现有导出包、按 UUID 修改节点及关系，输出新的 BRZ 副本。工具只用 Python 标准库，要求 Python 3.10 或更新版本。

已验证五份现有 BRZ 的文件结构，并用官方数据模型完成六份文件共 876 条记录的 JSON 往返检查。**11 项操作演示及 38 节点的 NIST AI RMF 中文成品脑图，已在 Windows 的 TheBrain 14.0.102.0 中完成导入、关闭重开和再导出**，具体界面检查与数据核对范围见[验证记录](docs/VALIDATION.md)。这些结果不代表所有 BRZ 或软件版本都兼容。首次使用仍应导入独立测试脑图；原始四卷和其中的原始 BRZ 均保持不变。

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

从 TheBrain 原生导出的空白测试脑图开始，运行 [build_plan.py](examples/nist-ai-rmf/build_plan.py) 生成绑定该输入摘要的 133 操作计划，再用同一 CLI 预演、写出和验收。可修改 [content.json](examples/nist-ai-rmf/content.json) 中的知识结构后生成新计划；生成器会明确替换选定根节点的名称与整份笔记，其他已有节点保持不变。完整命令和输入限制见示例说明。

本次原生再导出后，全量核对名称、关系、笔记、来源链接和颜色均保留；再基于该文件修改一个节点的 Label 和笔记也通过离线校验。第二次编辑尚未重新导入桌面应用，详细变更范围见[成品验收记录](docs/VALIDATION.md#公开-pdf-成品nist-ai-rmf-10)。

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

仓库的 `AGENTS.md` 也会引导后续代码助手读取这个 skill。也可将 skill 安装到助手的技能目录，但仍需保留可定位的 Brainmind checkout；仅复制 `SKILL.md` 不会安装 Python 编辑器。解释器选择与路径规则见 skill 的“定位与启动”。

## 开发检查

```sh
python3 -X utf8 -m unittest discover -s tests -v
```

合成测试无需任何第三方库或原始分卷，适合开源贡献者直接运行。[跨平台 CI](.github/workflows/tests.yml) 在 Ubuntu 和 Windows、Python 3.10/3.13/3.14 上运行同一测试命令；CI 通过不代替 TheBrain 桌面验收。恢复到上述路径的五份原始导出还会参加可选的实样本检查，未提供这些文件时该项会明确跳过。

测试重点是引用一致性、事务回退、原始字节保留和文件写入边界，还覆盖 Windows 文件同步、旧代码页下中文/emoji 报告、原生访问记录复合键、保守保留导出壁纸，以及笔记文件被其他附件共用时拒绝覆盖。

第一批稳定性修复提交 `e7f3b34489a14d41601d34d26a6c8916cc1de6da` 的 [6 组 CI 检查](https://github.com/hongjinrui/Brainmind/actions/runs/42829276774) 全部通过。加入成品计划生成器的 3 项回归后，本地 Windows 测试为 78 项通过；完整记录与具体版本见 [VALIDATION.md](docs/VALIDATION.md)。后续提交应查看自身的 CI 结果。
