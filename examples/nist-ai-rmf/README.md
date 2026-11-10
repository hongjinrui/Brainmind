# NIST AI RMF 1.0 中文脑图

[下载成品 BRZ](nist-from-content.brz) · [查看内容 JSON](content.json)

“AI 风险管理：从框架到行动”包含 7 个主分支、38 个节点、37 条父子关系、13 条跨主题跳转及 38 份中文来源笔记。笔记标明 PDF 页码，区分资料概括与原创实践提示。PDF 以链接引用，没有嵌入归档。

在 TheBrain 中导入成品，选择“创建新脑图”。该文件已在 TheBrain 14.0.102.0 中通过导入、关闭重开和原生再导出的内容核对；[验证范围](../../docs/VALIDATION.md)。

## 重新生成

在仓库根使用 Python 3.10+ 执行，Windows 可将 `python` 换为 `py -3`：

```sh
python -X utf8 -m brain_toolkit create --content examples/nist-ai-rmf/content.json
python -X utf8 -m brain_toolkit create --content examples/nist-ai-rmf/content.json --output work/nist-new.brz
python -X utf8 -m brain_toolkit validate work/nist-new.brz
```

第一条预演，第二条写出新文件，不需要现成 BRZ。使用[完整 skill 包](../../releases/brz-edit.zip)时，将命令前缀替换为 `python -X utf8 "技能目录/scripts/brz.py"`，内容文件使用实际路径。每次创建分配新身份；已有作品继续维护应使用 `apply`。

可编辑 `content.json` 调整内容。此示例未指定背景色，采用应用默认样式；需要颜色时为节点添加 `background_color`。

## 来源

Elham Tabassi (2023), *Artificial Intelligence Risk Management Framework (AI RMF 1.0)*, NIST AI 100-1. [DOI](https://doi.org/10.6028/NIST.AI.100-1) · [官方 PDF](https://nvlpubs.nist.gov/nistpubs/ai/NIST.AI.100-1.pdf) · [NIST 使用与许可说明](https://www.nist.gov/open/copyright-fair-use-and-licensing-statements-srd-data-software-and-technical-series-publications)

Republished courtesy of the National Institute of Standards and Technology.

本图由 Codex 于 2026-11-10 整理为中文知识结构并加入原创实践提示，解释 2023 年 1.0 版，不是官方译文，也不表示 NIST 背书。引用为正文页码，PDF 阅读器页码比正文多 5。
