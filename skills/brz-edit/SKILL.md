---
name: brz-edit
description: 使用本仓库 Python 工具离线检查与修改 TheBrain 14 的 BRZ 导出副本，维护节点、父子/跳转、类型/标签、Markdown 笔记及受限样式。实验能力；不编辑运行中的脑图或未知本地数据库。
---

# BRZ 脑图维护

适用于用户明确要求整理、补充或修改 BRZ 脑图的任务。这里的 coding 表示通过代码维护脑图，不要求特定文件名。执行前阅读仓库 `README.md`、`docs/FORMAT.md` 和当前 `python3 -m brain_toolkit --help`；这些文件与本 skill 一起使用。

当前能力仅验证过 BRZ 离线结构。未经原应用人工导入、重新打开及再导出检查，不得声称已完成应用兼容性验证。

## 工作流程

1. 在仓库根目录运行工具。若输入是四卷原始包，使用仓库说明恢复到 `work/`；保留四卷和原 BRZ。不要执行包内授权或激活工具。
2. 运行 `python3 -m brain_toolkit inspect work/input.brz --list` 和 `python3 -m brain_toolkit validate work/input.brz`；使用 `inspect work/input.brz --id UUID --notes` 读取目标详情和 Markdown/HTML 笔记。笔记默认每份限制 20000 字符；若 `truncated=true`，用 `--note-limit` 增加上限或另读完整内容，不把截断文字当成全文。检查版本、节点和关系数量、UUID、类型/标签、附件、警告；读取目标及其父子、跳转、类型、标签和笔记上下文。
3. 以 UUID 选择已有节点。名称可能重复，不可仅凭文字自动选中第一个。能从上下文唯一定位时继续；存在影响编辑结果的歧义时向用户澄清，同时推进独立工作。
4. 根据用户目标创建 JSON 计划，必须含原 BRZ 的 `source_sha256`；列清具体操作、目标 UUID、新名称/笔记、关系端点、复用模板。把草稿、检查结果和中间文件置于 `work/`。用户已授权的日常可逆编辑可直接执行，无须额外确认。
5. 运行 `python3 -m brain_toolkit apply work/input.brz --plan work/edit.plan.json` 预演；**省略 `--output` 即为 dry-run，没有 `--dry-run` 参数**。检查修改清单后，增加 `--output work/edited.brz` 执行计划写副本，再运行 `python3 -m brain_toolkit validate work/edited.brz`。输入输出必须为不同路径，且输出文件不能已经存在。
6. 核对原件 SHA-256 未变、未涉及成员及记录未变、计划操作均完成、没有悬空引用；保留变更报告和新文件摘要。若发现原样本已有警告，单列为原有问题，不随意扩展修改范围。
7. 交付编辑副本、改动说明和验证状态。如果环境能运行正常可用的原应用，按 `docs/FORMAT.md` 完成测试脑图导入；否则明确报告“离线结构通过，原应用导入未验证”，保留后续人工检查步骤。

## 操作选择

| 需求 | 工具操作 | 必须维护的约束 |
| --- | --- | --- |
| 改名/标签文字/背景色 | `update_thought` | 仅允许 `Name`、`Label`、`BackgroundColor`，保留 UUID；前景色修改不支持 |
| 新增普通/类型/标签节点 | `add_thought` | `Kind` 仅 1、2、4；新 UUID 不重复；不创建系统节点 |
| 添加/移除普通父子或跳转 | `add_link` / `remove_link` | 父子 A=父 B=子；跳转为联系；不得使用普通移除误删类型/标签关系 |
| 改普通节点类型 | `set_type` | 仅 `Kind=1` 目标，同步 `TypeId` 与 `Meaning=2` link；已有 `Kind=2` 类型层级用 `Meaning=3`，保留且不编辑 |
| 添加/移除标签 | `set_tag` | 使用 `Kind=4` 标签与 `Meaning=5` link；不靠改 `TagIds` |
| 更新/新建 Markdown 笔记 | `set_note` | 同步附件元数据、真实 UTF-8 长度和文件成员；保留旧 HTML 与 `ThoughtIconInfo`，应用笔记标记可能需刷新 |
| 复用已有图标 | `reuse_icon` | 同 BRZ、同 `Kind` 的简单图标模板；目标无类型/标签/笔记/其他附件，已有图标不替换 |
| 修改已有属性值 | `update_field` | 仅更新已有字段实例的已有值字段；不推断完整属性类型枚举 |

计划顶层只允许 `source_sha256` 与 `operations`。各操作参数如下；未知键会被拒绝，创建记录的 `id` 可省略由工具生成，但需要在后续操作引用的新节点应预先分配 UUID v4。

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
| `update_field` | `id`（字段实例 UUID），`value`；保留已有标量 JSON 类型 |

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

## 原应用验证

在正常可用的 TheBrain 中先创建可丢弃的测试脑图，导入新 BRZ 副本，检查本次编辑与未改附件，关闭重开，再导出并比对。随包手册 p252–253 指出导入会修改当前打开的脑图，且不能撤销；首次验证不能合并进用户的日常脑图。

遇到未知版本、无效输入、缺失引用、冲突 UUID、计划摘要不匹配或超出已研究格式的需求时，停止对应写操作，报告具体原因和已完成工作。不要通过修改授权文件或猜测本地数据库来绕过阻碍。
