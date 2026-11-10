# Brainmind：把资料做成 TheBrain 脑图

给 AI 一份 PDF 或文字资料，得到包含主题、关系和来源笔记的 `.brz` 文件，可在 TheBrain 14 中打开。也支持继续编辑已有 BRZ 副本。

生成只需 **Python 3.10+** 和能阅读资料、执行 Python 的 AI 助手。完整 skill 包自带运行代码，无需 Git、pip 依赖或已有脑图；TheBrain 用于打开成品。

## 开始使用

1. 下载[完整 brz-edit.zip](https://github.com/hongjinrui/Brainmind/raw/refs/heads/main/releases/brz-edit.zip)，解压得到 `brz-edit/`。
2. 将整个目录安装到助手的 skill 目录，或让助手读取其中的 `SKILL.md`。
3. 提供资料并提出：

> 用 brz-edit 把这些资料整理成 TheBrain 脑图。建立主题和跨主题关系，笔记保留来源页码与链接，交付 BRZ。

AI 负责阅读和组织内容；工具接收内容 JSON 并生成归档，不直接理解 PDF。PDF 通过笔记中的链接引用，当前不嵌入文件。

在 TheBrain 中导入生成的 BRZ，选择“创建新脑图”，无需提前建立空白脑图。首次使用请保持为独立脑图。

## 直接试跑

解压包内含一个 3 节点中文示例。将下面的 `brz-edit` 替换为实际目录；Windows 可将 `python` 换为 `py -3`。

```sh
python -X utf8 "brz-edit/scripts/brz.py" create --content "brz-edit/references/example.content.json"
python -X utf8 "brz-edit/scripts/brz.py" create --content "brz-edit/references/example.content.json" --output "work/reading.brz"
python -X utf8 "brz-edit/scripts/brz.py" validate "work/reading.brz"
```

不带 `--output` 只预演，带上才写出；输出文件必须未存在。仓库用户可将命令前缀替换为 `python -X utf8 -m brain_toolkit`，在仓库根执行。

新作品使用 `create`，已有作品使用 `inspect`、`apply` 和 `compare`，保留原件及节点身份。具体输入格式与编辑步骤见 [skill](skills/brz-edit/SKILL.md) 和[格式说明](docs/FORMAT.md)。

## 成品示例与支持范围

[NIST AI RMF 中文脑图](examples/nist-ai-rmf/README.md)提供可直接打开的成品与内容 JSON：38 个节点、37 条父子关系、13 条跳转和 38 份来源笔记。

该示例已在 Windows 的 **TheBrain 14.0.102.0** 中完成导入、关闭重开和再导出，内容核对通过。当前支持 BRZ v5 的已研究结构；其他版本和格式需单独验证。工具不编辑运行中的数据库或云同步数据。[验证范围与方法](docs/VALIDATION.md)

## 维护

核心仅依赖 Python 标准库。修改后运行测试；代码、skill 或随包文档改变时，重建发布包：

```sh
python -X utf8 -m unittest discover -s tests -v
python -X utf8 tools/package_skill.py --output work/brz-edit.next.zip
python -c "from pathlib import Path; import os; Path('releases').mkdir(exist_ok=True); os.replace('work/brz-edit.next.zip', 'releases/brz-edit.zip')"
python -X utf8 tools/package_skill.py --check releases/brz-edit.zip
```

临时输出路径须未存在。打包器从唯一核心源码生成可独立安装的 ZIP，附逐文件 SHA-256；`--check` 只读检查发布包与源码一致。[CI](.github/workflows/tests.yml) 在 Ubuntu、Windows 和 Python 3.10/3.13/3.14 上测试，并拒绝陈旧的发布包。
