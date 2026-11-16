# Brainmind for TheBrain 14

让 AI 在 TheBrain 原窗口里阅读资料、理解当前节点，并直接协作编辑脑图。

**Brainmind 0.3.0 · Windows x64 · 仅适配 TheBrain 14.0.102.0**

这是一个非官方的外置扩展，使用方式类似“带扩展的启动器”。它随原程序启动，在右侧增加 AI 聊天面板；不覆盖 TheBrain 文件，不升级软件，也不修改授权或订阅。平时使用不需要 Codex、ChatGPT 桌面应用或界面自动化工具。

本项目只提供扩展，**不附带 TheBrain 安装包、TheBrain.exe 或其他第三方程序集**。请先自行安装并按其许可使用对应版本。可参考 [TheBrain 官方历史版本页面](https://www.thebrain.com/products/thebrain/oldversions)；该页面提供的版本可能变化，安装前仍需核对确切版本号。

## 可以做什么

- 在软件内与自己配置的 AI 模型对话，读取当前脑图、活动节点、邻接关系和当前编辑器中的未保存笔记。
- 直接创建节点、改名、增加父子／跳转关系，以及修改当前节点笔记。
- 添加 PDF、TXT、Markdown、CSV 供 AI 阅读。
- 点击“生成脑图”，让模型自行归纳资料结构，在当前节点下面创建新分支，并定位到新根节点。

## 准备条件

1. Windows x64，已完整安装 **TheBrain 14.0.102.0**，且该程序能正常运行。
2. 一个支持 **OpenAI Chat Completions 工具调用**的模型服务，或兼容该接口的本地模型。只有文字问答能力的接口不能完成脑图编辑。

扩展严格检查目标程序及依赖的文件指纹。其他 14.x、15.x 和 macOS 不在适配范围内；相同版本号但文件不同也可能被拒绝。不会自动下载、安装或升级 TheBrain。

## 安装与启动

1. 从本仓库 **Releases** 下载 `Brainmind-0.3.0-launcher.zip`，解压到一个独立目录。也可以下载源码仓库的 ZIP 并完整解压，仓库内已包含编译好的扩展。
2. 保存工作并**正常退出所有 TheBrain 窗口**。扩展必须随程序启动，无法附加到已经运行的窗口。
3. 双击 `launch-brainmind.cmd`。启动器会自动查找已安装的 TheBrain；之后打开自己的脑图，右侧会出现 **Brainmind AI** 面板。

不要只复制一个 `.cmd` 文件；请保持解压后的目录结构完整。扩展目录无需放进 TheBrain 安装目录。

如果没有自动找到安装位置，在解压目录打开 PowerShell，指定**已安装主程序**的路径：

```powershell
.\launch-brainmind.cmd -AppPath "你的安装目录\TheBrain.exe"
```

这里指定的是安装后的 `TheBrain.exe`，不是 `Installer.exe`。运行已编译的启动器不需要 Python 或 .NET SDK，也不需要管理员权限；依赖由已有的 TheBrain 安装提供。

## 配置自己的模型

在右侧 **Brainmind AI → 模型设置** 中填写：

| 字段 | 填写内容 |
| --- | --- |
| API 地址 | 服务商提供的兼容地址，例如 `https://你的服务地址/v1`；也可填写完整的 `/chat/completions` 地址 |
| 模型 | 该服务支持工具调用的模型名称 |
| API 密钥 | 自己的密钥；无需认证的本地服务可留空 |

点击“保存设置”。项目不提供模型账号、额度或密钥；地址与模型名称以所用服务的说明为准。

配置保存在当前 Windows 用户的 `%LOCALAPPDATA%\Brainmind\model.json`，与扩展目录分开。**API 地址和模型名称为本地普通配置，密钥使用当前用户的 Windows DPAPI 加密保存。** 发行包不含作者的模型配置；更新扩展会继续使用本机已有配置。

## 日常使用

**对话与编辑：** 选中要处理的节点，在聊天框写清要求，点击“发送”或按 `Ctrl+Enter`。例如：“保留当前草稿，补充三个论点，并为它们建立子节点。” 普通“发送”也可以只用于讨论。

**从资料建图：** 先选中希望挂载新分支的节点，点击“添加文件”，等待资料就绪，再点击 **“生成脑图”**。输入框可以留空，让模型自行判断结构；也可以补充重点和组织要求。建图成功后会定位到新根节点，并显示程序确认的实际创建数，纯文字大纲不算生成完成。

**取消与切换：** 处理中可点击“取消”。切换脑图、节点或修改相关内容时，原请求的后续写入会被停止。已经完成的单项操作仍保留，请先核对结果再决定继续或撤销；整个批次不会自动回滚。

**面板：** 可收起或关闭；关闭面板后可从菜单栏 **Brainmind** 重新打开。窗口较小时，滚动聊天区可以访问下方按钮。

## 文件与功能边界

- 最多同时选择 5 个资料文件，单个不超过 20 MiB，总计不超过 40 MiB。PDF 最多读取 600 页／400,000 字符，超限会提示截断。
- PDF 通过本机提取文字供模型阅读；扫描件需要先 OCR，图片和复杂图表不会作为视觉内容发送。
- 添加的文件用于聊天阅读，**不会保存为 TheBrain 节点附件**。原文件变化后，需要移除并重新选择。
- 一次“生成脑图”最多创建 40 个节点、8 层和 60 条额外跳转关系；创建在当前节点下面，不自动替换已有脑图。
- 目前支持普通节点、有限新增关系与当前 Markdown 笔记；不提供删除节点、批量覆盖或节点附件导入。
- 仅当前节点能读取编辑器中的未保存笔记。云同步并发、复杂富文本和任意模型服务的兼容性不在已验证范围内。

## 数据与隐私

发送请求时，当前节点、邻接关系、当前笔记，以及已选择资料的文字预览或模型请求的资料段落，会发送到**你配置的模型服务**。使用前请确认这些内容可以交给该服务处理，具体留存规则由服务商决定。

聊天记录和资料文字快照保留在本次进程内存中，不作为发布文件保存。运行日志不记录密钥、提示词或笔记正文。会话发现文件位于 `%LOCALAPPDATA%\Brainmind\sessions`。请勿把本机模型配置、会话目录或含个人内容的诊断材料提交到公开仓库。

## 常见问题

| 现象 | 处理方法 |
| --- | --- |
| 双击后没有看到面板 | 确认已退出旧 TheBrain，再用本目录的启动器打开；普通 TheBrain 快捷方式不会加载扩展。可在 PowerShell 运行启动器查看错误。 |
| 提示 TheBrain 已运行 | 正常保存并退出后重试，启动器不会替你关闭现有窗口。 |
| 提示找不到支持的安装／文件指纹不匹配 | 核对确切版本和 `-AppPath`。不要通过改写指纹配置绕过检查；需要重新适配的版本不能直接使用。 |
| 有文字回复，却没有节点 | 确认资料已就绪并点击“生成脑图”，核对程序显示的实际创建数及错误；确认模型支持工具调用。 |
| 请求失败或只完成一部分 | 先检查已创建内容。重试会是新请求，可能再次创建分支。 |
| 模型返回认证／接口错误 | 核对自己填写的地址、模型与密钥，确认服务支持 Chat Completions 工具调用。 |
| 更换电脑后密钥不可用 | DPAPI 密钥绑定 Windows 用户，请在新电脑重新填写。 |

## 停用与卸载

正常退出 TheBrain 后，用原来的 TheBrain 快捷方式启动，即可不加载 Brainmind。扩展没有写入安装目录，也没有设置全局启动钩子。

不再使用时可以删除解压目录。模型配置和会话文件仍在 `%LOCALAPPDATA%\Brainmind`，需要彻底清理时再自行删除该目录；这不会卸载 TheBrain。

## 源码与构建

GitHub 仓库保留核心源码和构建工具；普通用户启动包只含运行必需文件与说明，不包含测试、样例、生成的脑图、研究材料或技能包。

```text
launch-brainmind.cmd               双击启动入口
tools/launch-brainmind.ps1          程序发现、指纹校验和启动
build/0.3.0/Brainmind.Runtime.dll   外置扩展运行文件
build/0.3.0/manifest.json           扩展版本、摘要及源码摘要
config/supported-build.json        目标程序与依赖指纹
README.md / LICENSE / SHA256SUMS   使用说明、许可证与文件摘要
```

源码仓库另含 `src/Brainmind.Runtime/*.cs`、`tools/brainmind.py`、`tools/package_release.py`、`.gitignore` 和 `.gitattributes`。

| 核心源码 | 职责 |
| --- | --- |
| `StartupHook.cs`、`RuntimeHost.cs` | 随原程序加载、窗口会话与工具调度 |
| `ChatPanel.cs` | 原生聊天、资料选择和生成按钮 |
| `TheBrainAdapter.cs` | 当前脑图／笔记读取与受会话版本检查的原生编辑 |
| `ModelConfig.cs`、`ModelClient.cs` | 本机配置、密钥保护及模型工具协议 |
| `MapPlan.cs`、`MapBuilder.cs` | 建图计划预检、逐项执行与部分成功回执 |
| `Attachment*.cs`、`PipeServer.cs` | 资料文字快照与当前用户命令行通信 |

重新构建需要 Windows、Python 3.10+、已安装的 .NET 6 运行时、Windows Framework64 C# 编译器，以及上述确切版本的 TheBrain。不需要下载第三方 Python 包，不分发或修改 TheBrain 的依赖。

```powershell
python -X utf8 tools\brainmind.py build --app "你的安装目录\TheBrain.exe"
python -X utf8 tools\package_release.py
```

打包命令使用文件白名单，生成 `publish/Brainmind/` 干净仓库目录，以及 `dist/Brainmind-0.3.0-launcher.zip` 和 `dist/Brainmind-0.3.0-source.zip`。源码目录的编译参数、编译日志、个人配置及其他未列入白名单的文件均不进入输出。`SHA256SUMS` 可用于核对各包文件完整性；`dist/github-release-manifest.json` 记录归档摘要。

可选命令行控制只在源码仓库提供，运行 `python -X utf8 tools\brainmind.py --help` 查看命令。存在多个会话时使用 `--pid` 明确指定；直接编辑必须携带刚读取的 `context_token`。

## 许可与声明

本项目新写源码和文档采用 [MIT License](LICENSE)。TheBrain 是独立产品，名称和商标归各自权利人所有。本项目与 TheBrain 官方无隶属或背书关系，也不提供授权绕过、功能解锁或第三方安装材料。

当前适配依赖固定版本的非公开接口。升级 TheBrain、修改依赖文件或更换平台后，需要重新研究和验证适配。
