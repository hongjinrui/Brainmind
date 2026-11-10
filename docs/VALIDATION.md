# 验证范围与方法

当前支持已研究的 BRZ v5 结构，实测应用版本为 Windows 的 **TheBrain 14.0.102.0**。

## 已验证范围

- 无底稿创建的 [NIST 成品](../examples/nist-ai-rmf/nist-from-content.brz)完成独立导入、关闭重开和原生再导出。38 个名称、38 份笔记及来源引用、37 条父子方向、13 条跳转、节点身份、脑图名称和主页设置均保留，再导出校验无错误、无警告。
- 原生再导出文件可继续离线修改 Label；无关笔记、关系及成员字节不变。该二次编辑未再次导入应用。
- 小样例验证了中文来源笔记、黄色及浅色背景、跳转显示。已有 BRZ 的改名、类型、标签、图标复用和笔记编辑也完成过应用往返检查。
- 独立 skill ZIP 已在包外工作目录、隔离 Python 进程中完成创建和继续编辑，无需 checkout、原 BRZ 或第三方 Python 包。

上述结果仅覆盖已测试的输入和版本。`validation.errors=[]` 表示离线结构通过；CLI 的 `application_import_verified=false` 表示它没有操作桌面应用。

## 如何验证

1. 先预演，再写到未存在的新路径，运行 `validate` 和 `inspect --list`。核对笔记全文、关系方向及来源；编辑已有文件时用 `compare` 核对变更范围，并确认原件 SHA-256 不变。
2. 将新建成品导入 TheBrain，选择“创建新脑图”；编辑副本使用独立可丢弃目标，首次验证不要合并进日常脑图。
3. 检查目标节点、笔记、父子/跳转、样式与附件，关闭重开后再导出。按内容和关系核对；如果应用重分配 UUID，不把按 ID 的增删直接当作内容丢失。
4. 记录实际应用版本及已测范围。应用新增设置或规范化时间字符串，应与内容丢失区分；未知格式不通过删记录或猜测字段来强行放行。

维护检查：

```sh
python -X utf8 -m unittest discover -s tests -v
python -X utf8 tools/package_skill.py --check releases/brz-edit.zip
```

测试覆盖创建与编辑、引用一致性、事务回退、禁止覆盖、原字节保留、Unicode、严格输入校验及独立包使用。[跨平台 CI](../.github/workflows/tests.yml) 的当前结果以对应提交为准；发布包检查会拒绝源码更新后遗留的旧 ZIP。
