# NIST AI RMF 1.0 中文知识脑图

以公开 PDF 制作的 38 节点示例：7 个主分支、37 条父子关系、13 条跨主题跳转，每个节点包含中文 Markdown、来源页码和 PDF 链接。笔记区分原文概括与作者的实践提示；不是官方译文，也不代表 NIST 认可本项目。PDF 通过笔记链接引用，没有嵌入归档。

[下载成品 BRZ](nist-ai-rmf.brz) · [逐项验收报告](verification.json)。该成品已经在 TheBrain 14.0.102.0 完成独立导入、关闭重开和原生再导出；38 份笔记字节、50 条关系及 7 个背景色均保留。成品 SHA-256：`0af7b502f6fa9cf6fd169a0b65ae18212e6d76433124466eafa6396f7b2678e5`。首次打开请选择独立测试脑图，勿合并到日常脑图；下文生成步骤用于重建或调整内容。

## 无需底稿直接生成

从本仓库或[完整独立 skill 包](../../releases/brz-edit.zip)的运行代码，可以直接将本例 `content.json` 生成为新的 BRZ，无须现成归档、Git 或正在运行的 TheBrain：

```sh
python -X utf8 -m brain_toolkit create --content examples/nist-ai-rmf/content.json
python -X utf8 -m brain_toolkit create --content examples/nist-ai-rmf/content.json --output work/nist-new.brz
python -X utf8 -m brain_toolkit validate work/nist-new.brz
```

在命令前准备输出目录 `work/`。独立包将命令前缀替换为 `python -X utf8 "技能目录/scripts/brz.py"`，内容 JSON 使用实际文件路径。每次新建分配全新的脑图身份；首次导入选择“创建新脑图”，不用提前建立空白脑图。[无底稿生成的成品](nist-from-content.brz)与[原生往返核对报告](create-verification.json)展示本入口的结果：38 个节点、50 条关系、38 份来源笔记。此内容文件未指定背景色，采用应用默认样式；需要颜色时给相应节点增加 `background_color` 字段。

下面保留早期“已有归档生成编辑计划”的复现方式。它用于保留已有身份的编辑示例；全新作品使用上面的 `create` 入口。

## 已有归档的计划生成方式

需要 Python 3.10+ 和本仓库，无额外 Python 依赖。先在 TheBrain 原生创建一个可丢弃的空白脑图，导出为 `work/nist-empty.brz`。本脚本不能从零制造原生 BRZ，也不修改运行中的数据库。保留原导出文件，并按根目录 `skills/brz-edit/SKILL.md` 和 `docs/FORMAT.md` 操作。

下面命令在仓库根目录执行；Windows 可将 `python` 替换为已验证可用的 `py -3`。先检查输入，再从 `inspect` 列表中明确选择普通根节点（`Kind=1`）的 UUID：

```sh
python -X utf8 -m brain_toolkit validate "work/nist-empty.brz"
python -X utf8 -m brain_toolkit inspect "work/nist-empty.brz" --list
python -X utf8 examples/nist-ai-rmf/build_plan.py --source "work/nist-empty.brz" --root-id "此处替换为实际根UUID" --output "work/nist.plan.json"
```

`build_plan.py` 只生成绑定输入 SHA-256 的计划，并在内存中预演校验；不写 BRZ。它将选中根节点改名、替换其整份 Markdown 笔记，新增其余 37 个普通节点和 50 条关系，并为 7 个主分支设置浅色背景。节点与连线 UUID v4 在计划生成时分配；再次生成会产生新 UUID。输出存在时拒绝覆盖。脚本也可以使用绝对路径从任意工作目录调用，其代码与 `content.json` 均按脚本位置定位。

如果输入含其他普通节点，脚本会保留它们，并在报告的 `preserved_other_ordinary_thoughts` 中给出数量；这类输入适合演示预演，不应冒充干净的新作品。名称相同不意味着同一节点；只按明确的根 UUID 定位。

## 预演、写副本与应用验收

```sh
python -X utf8 -m brain_toolkit apply "work/nist-empty.brz" --plan "work/nist.plan.json"
python -X utf8 -m brain_toolkit apply "work/nist-empty.brz" --plan "work/nist.plan.json" --output "work/nist-ai-rmf.brz"
python -X utf8 -m brain_toolkit validate "work/nist-ai-rmf.brz"
python -X utf8 -m brain_toolkit compare "work/nist-empty.brz" "work/nist-ai-rmf.brz" --limit 200
```

保存 CLI 报告时使用 UTF-8。省略 `--output` 就是预演；不支持 `--dry-run`。原件与输出必须是不同路径，输出不能已存在。同一计划只适用于其绑定的原件；把已生成作品再次作为输入会需要新摘要与针对性的编辑计划，不要重复套用这个生成器。

在可丢弃测试脑图中导入成品，检查七个分支、笔记、跳转及颜色，关闭重开，再导出为第三个文件并核验。导入会修改当前脑图，首次验证不能合并到日常脑图。离线校验或生成计划成功都不等于应用验收通过；本次实测范围见仓库 `docs/VALIDATION.md`。

成品保留空白 seed 的 `BrainId`。如果“创建新脑图”导入提示该 ID 已在本机存在，先保留 seed 的原生导出备份；仅对本次新建、可丢弃的 seed 使用原应用“分配脑图的新 GUID...”，再创建新脑图导入成品。本轮验收采用了此步骤。它用于解决测试 seed 的身份冲突，不是普通编辑的固定流程，也不适用于日常脑图；不需要改写 BRZ 内部 UUID。

## 来源与改编

Elham Tabassi (2023), *Artificial Intelligence Risk Management Framework (AI RMF 1.0)*, NIST AI 100-1, National Institute of Standards and Technology. [DOI](https://doi.org/10.6028/NIST.AI.100-1) · [官方 PDF](https://nvlpubs.nist.gov/nistpubs/ai/NIST.AI.100-1.pdf) · [NIST 使用与许可说明](https://www.nist.gov/open/copyright-fair-use-and-licensing-statements-srd-data-software-and-technical-series-publications)

Republished courtesy of the National Institute of Standards and Technology.

2026-11-10 由 Codex 将资料重组为中文知识结构，并加入原创实践问题。引用页码为正文页码，PDF 阅读器页码比正文多 5。原 PDF 不随仓库分发。内容固定解释 2023 年 1.0 版；制作时 [NIST 项目页](https://www.nist.gov/itl/ai-risk-management-framework)说明该版本正在修订，使用时应自行核对版本。
