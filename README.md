# 通过代码维护 TheBrain 脑图

本仓库现在包含一套 **实验性的 BRZ 离线编辑工具**。无需找回原软件源代码，即可读取现有导出包、按 UUID 修改节点及关系，输出新的 BRZ 副本。工具只用 Python 标准库，要求 Python 3.10 或更新版本。

已验证的是五份现有 BRZ 的文件结构及编辑后的内部一致性；**尚未验证原软件导入、重新打开和再导出**。随包官方手册明确警告手工修改归档可能破坏文件，因此首次使用应在独立测试脑图中导入副本。原始四卷和其中的原始 BRZ 均保持不变。

## 从哪里开始

- [操作 skill](skills/brz-edit/SKILL.md)：以后让助手整理资料时使用的工作流程。
- [格式分析](docs/FORMAT.md)：节点、关系、UUID、标签、类型、笔记、属性及图标的证据和边界。
- [输入清单与哈希](docs/INVENTORY.json)：四卷及五份 BRZ 的摘要和记录数量。
- [验证记录](docs/VALIDATION.md)：自动检查、示例变化、原应用测试状态。
- [演示计划](examples/demo.plan.json)、[演示 BRZ](examples/test2-code-edit-demo.brz)、[演示报告](examples/demo.report.json)。

这是对现有文件格式的研究与工具实现，没有恢复 TheBrain 的软件源码，也没有重建其桌面应用。

## 恢复原始材料

将四卷保持原名并放在同一目录。安装正常的 7-Zip 后，在仓库根目录执行：

```sh
7z x Brainmind.7z.001 -owork/extracted
```

也可用 7-Zip 图形界面打开 `.001` 并解压到 `work/extracted`。若解压工具要求拼接，应按 `.001`、`.002`、`.003`、`.004` 顺序二进制拼接，再按 7z 解压；不是分别解压四份文件。

相关 BRZ 位于 `work/extracted/TheBrain14 - v2/`。离线编辑工具不需要安装或执行包内的任何 EXE。

## 查看与修改

以下命令在仓库根目录执行；Windows 可将 `python3` 换为 `py -3`。

```sh
python3 -m brain_toolkit --help
python3 -m brain_toolkit inspect "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz" --list
python3 -m brain_toolkit validate "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz"
```

`inspect` 给出源文件 SHA-256，`--list` 给出名称与 UUID。用 `--id UUID` 查看某个节点及其连线、附件、属性，加 `--notes` 读取 Markdown/HTML 笔记原文。每份笔记默认最多显示 20000 字符，并明确标记是否截断；可用 `--note-limit` 调整。修改名称不会改变已有 UUID；同名节点需结合关系上下文判断。

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
python3 -m brain_toolkit apply "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz" --plan examples/demo.plan.json
python3 -m brain_toolkit apply "work/extracted/TheBrain14 - v2/test2 2025-03-21 (1).brz" --plan examples/demo.plan.json --output work/my-demo.brz > work/my-demo.report.json
python3 -m brain_toolkit validate work/my-demo.brz
```

工具不覆盖已有输出；若需重新生成，请另取一个新文件名。计划中的新节点采用预分配 UUID v4，后续操作可引用它。未指定 UUID 的新实体会自动生成，因此同一计划重复从原件生成的二进制摘要可以不同。

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
| `update_field` | 更新已有属性实例的已有标量值 |

精确参数和限制见 [skill](skills/brz-edit/SKILL.md)。工具保留未知字段、不相关成员、未改记录原始字节及日志；不会随意重建整份脑图。整个计划失败时不产生部分编辑结果，源摘要不匹配、引用错误或输出冲突会被拒绝。

## 使用边界

- 仅编辑 `ExchangeFormatVersion=5` 的已研究结构；父子关系允许多个父节点，不将图强行改为树。
- 图标内部状态串、同步协议及完整属性类型尚未恢复；图标操作有严格的模板限制，笔记编辑保留原有图标缓存，显示效果需应用检查。
- 不删除节点、不合并身份、不创建未知属性定义，不写事件、固定关系或权限。
- 仓库没有实际本地脑图存储目录，不能确定你提到的 `.dll` 数据文件格式；本工具不直接写本地数据库或同步目录。
- `validation.errors=[]` 只表示离线检查通过。`application_import_verified` 保持 `false`，直到实际完成原应用往返测试。

原应用导入检查步骤见 [格式分析中的验证流程](docs/FORMAT.md#分级验证及导入检查)。

## 以后如何调用

可以直接向助手提出：

> 按本仓库 `skills/brz-edit/SKILL.md`，读取这份 BRZ，把这些资料整理成父子节点并补充笔记，保留已有节点身份，交付新的 BRZ 和修改报告。

仓库的 `AGENTS.md` 也会引导后续代码助手读取这个 skill。工具是仓库内的可复用能力，未向你的电脑全局安装任何插件。

## 开发检查

```sh
python3 -m unittest discover -s tests -v
```

合成测试无需任何第三方库；恢复到上述路径的五份原始导出还会参加实样本检查。测试重点是引用一致性、事务回退、原始字节保留和文件写入边界。
