# LightX2V Windows App v0.3.9

[English](README.md) | 简体中文

在 Windows 上用文字或参考图片创作图像。界面与 [LightX2V Mac App](https://github.com/ModelTC/LightX2V-Mac-App) 保持一致，使用 [LightX2V](https://github.com/ModelTC/LightX2V) 进行本地推理，支持 Qwen-Image-2.1-Turbo 和可选的 llama.cpp 提示词增强。

**当前内测版本：v0.3.9。** 原生 Windows x64，无需 WSL。本版让已配置工作区启动后即可操作，发送时再做必要检查；支持复制已发送的提示词，并可将生成结果一键添加为图生图参考，移除复用参数按钮。保留自动资源规划与 v0.3.8 的读取稳定性修复。AMD Radeon 8060S 为已有实测平台，本版验收结果见发布说明；NVIDIA CUDA 仍为预览支持；已收到 RTX 4070 Laptop 的 512 / 1K / 2K 文生图成功记录，完整验收范围见文档。

## 下载与开始使用

| 版本 | Windows x64 下载 | 状态 |
| --- | --- | --- |
| v0.3.9 | [下载 App](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/v0.3.9/LightX2V-Windows-0.3.9-win-x64.zip) | 内测版 / Pre-release |

[发布说明与已知限制](https://github.com/ModelTC/LightX2V-Windows-App/releases/tag/v0.3.9)。请下载上面的 App ZIP；GitHub 的源码下载按钮不包含可运行的 App。

1. 完整解压 ZIP，运行文件夹内的 `LightX2V.Windows.exe`。
2. 按向导选择保存位置、检查电脑并准备组件、下载模型。
3. 配置完成后点击“开始创作”，输入自己的提示词即可。

便携包包含 .NET 桌面运行时和固定版本推理源码。向导可自动安装 Python 与推理依赖、复用本机环境，并一键下载模型。**普通用户无需打开终端、安装开发工具或输入下载命令。** 显卡驱动需要安装时，向导提供官方入口及操作指引；Windows 可能要求确认权限或重启。

详细步骤见 [首次使用](docs/getting-started.md)。当前构建未进行代码签名；请确认安装包来自本仓库的 Release 发布页，不要关闭系统安全保护。

## 设备与内测范围

| 后端 | 当前范围 | 验证状态 |
| --- | --- | --- |
| AMD ROCm | Radeon 8060S 自动配置；其他 AMD 显卡需兼容的已有原生环境 | 历史版本已实测文生图、参考图编辑、2K 与提示词增强；本版结果见发布说明 |
| NVIDIA CUDA | 支持原生 BF16 且与固定 PyTorch CUDA wheel 兼容的 GPU，主要面向 RTX 30/40/50 系列 | 预览，不能视为这些型号均已实测 |

Windows 11 x64 是当前验证平台。CPU-only、Intel GPU、Windows ARM64、RTX 20 系列的 FP16/量化回退尚未支持。

当前使用 BF16 大模型。App 按实际模型与空闲资源自动选择驻留、卸载或流式读取，容量充足时保留普通推理路径，不增加手选模式。**RTX 4070 Laptop 8 GB + 32 GB 内存已有 4 次文生图成功样本**，覆盖 512 / 1K / 2K 与提示词增强；逐次重启和参考图编辑仍待补测。详见 [RTX 实机记录](docs/rtx-4070-laptop-results.md)。 512 会减少图像工作区开销，但不会缩小模型权重，不能作为加载内存不足的通用解决方法。策略与日志说明见 [自动资源规划](docs/automatic-resources.md)。请预留约 100 GB 磁盘空间，避免安装和下载中途空间不足；这不是所有机器的固定最低占用。

## 创作功能

- Qwen-Image-2.1-Turbo，固定 8 步，文生图及最多 3 张参考图编辑。
- 512 / 1K / 2K、八种比例预设、自适应尺寸。
- 可选 PE-T2I / PE-I2I 提示词增强，使用本地 Vulkan 运行时。
- 生成进度、停止任务、查看日志、创作历史、提示词选择复制、结果图用作参考、图片复制与导出。
- 中文 / English；`Ctrl+N` 新建，`Ctrl+Enter` 生成或停止。

模型权重不包含在安装包中。支持 Hugging Face / ModelScope 下载、续传与校验。H3 视频和多模型选择留待后续版本。

## 从源码构建

开发者在 Windows 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 和 Python 3.12 后，在仓库根目录运行：

```powershell
.\build.ps1
```

输出为 `dist/LightX2V-Windows-0.3.9-win-x64.zip` 及 `.sha256`。构建校验提交的依赖清单，并从官方 GitHub Release 获取固定版本 llama.cpp；不会自动更换已锁定依赖。无需 GPU 即可编译，实际推理需要支持的 GPU 与对应环境。

参阅 [构建与测试](docs/development.md)、[验收范围](docs/testing.md)、[版本发布说明](docs/releases/v0.3.9.md) 和 [第三方组件](THIRD_PARTY_NOTICES.md)。

## 反馈问题

请通过 Issues 提交 App 版本、显卡、显存、系统内存、复现步骤和错误信息。识别显卡失败时，可在向导的“更多帮助与电脑信息”中复制检测信息。日志可能包含本机路径及提示词，提交前请检查并删去个人信息。CUDA 相关反馈请明确是安装、环境检查还是生成阶段失败。
