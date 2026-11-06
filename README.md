# LightX2V Windows App

在 Windows 上用文字或参考图片创作图像。界面与 [LightX2V Mac App](https://github.com/ModelTC/LightX2V-Mac-App) 保持一致，使用 [LightX2V](https://github.com/ModelTC/LightX2V) 进行本地推理，支持 Qwen-Image-2.1-Turbo 和可选的 llama.cpp 提示词增强。

**首个内测版本：v0.3.5。** 原生 Windows x64，无需 WSL。AMD Radeon 8060S 已完成实机验证；NVIDIA CUDA 为预览支持，完整安装与出图仍在 RTX 实机内测中。

## 下载与开始使用

| 版本 | Windows x64 下载 | 状态 |
| --- | --- | --- |
| v0.3.5 | [下载 App](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/v0.3.5/LightX2V-Windows-0.3.5-win-x64.zip) | 首个内测版 / Pre-release |

[发布说明与已知限制](https://github.com/ModelTC/LightX2V-Windows-App/releases/tag/v0.3.5)。请下载上面的 App ZIP；GitHub 的源码下载按钮不包含可运行的 App。

1. 完整解压 ZIP，运行文件夹内的 `LightX2V.Windows.exe`。
2. 按向导选择保存位置、检查电脑并准备组件、下载模型。
3. 配置完成后点击“开始创作”，输入自己的提示词即可。

便携包包含 .NET 桌面运行时和固定版本推理源码。向导可自动安装 Python 与推理依赖、复用本机环境，并一键下载模型。**普通用户无需打开终端、安装开发工具或输入下载命令。** 显卡驱动需要安装时，向导提供官方入口及操作指引；Windows 可能要求确认权限或重启。

详细步骤见 [首次使用](docs/getting-started.md)。当前构建未进行代码签名；请确认安装包来自本仓库的 Release 发布页，不要关闭系统安全保护。

## 设备与内测范围

| 后端 | 当前范围 | 验证状态 |
| --- | --- | --- |
| AMD ROCm | Radeon 8060S 自动配置；其他 AMD 显卡需兼容的已有原生环境 | 8060S 已实测文生图、参考图编辑、2K 与提示词增强 |
| NVIDIA CUDA | 支持原生 BF16 且与固定 PyTorch CUDA wheel 兼容的 GPU，主要面向 RTX 30/40/50 系列 | 预览，不能视为这些型号均已实测 |

Windows 11 x64 是当前验证平台。CPU-only、Intel GPU、Windows ARM64、RTX 20 系列的 FP16/量化回退尚未支持。

当前使用 BF16 大模型，显存不足时会把部分模型放到系统内存。**RTX 4070 Laptop 8 GB + 32 GB 内存仍需实测，不能保证流畅或完成出图。** 建议首次从 512、1:1、关闭 Prompt 增强开始，成功后逐步尝试 1K/2K。请预留约 100 GB 磁盘空间，避免安装和下载中途空间不足；这不是所有机器的固定最低占用。

## 创作功能

- Qwen-Image-2.1-Turbo，固定 8 步，文生图及最多 3 张参考图编辑。
- 512 / 1K / 2K、八种比例预设、自适应尺寸。
- 可选 PE-T2I / PE-I2I 提示词增强，使用本地 Vulkan 运行时。
- 生成进度、停止任务、查看日志、创作历史、参数复用、图片复制与导出。
- 中文 / English；`Ctrl+N` 新建，`Ctrl+Enter` 生成或停止。

模型权重不包含在安装包中。支持 Hugging Face / ModelScope 下载、续传与校验。H3 视频和多模型选择留待后续版本。

## 从源码构建

开发者在 Windows 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 和 Python 3.12 后，在仓库根目录运行：

```powershell
.\build.ps1
```

输出为 `dist/LightX2V-Windows-0.3.5-win-x64.zip` 及 `.sha256`。构建校验提交的依赖清单，并从官方 GitHub Release 获取固定版本 llama.cpp；不会自动更换已锁定依赖。无需 GPU 即可编译，实际推理需要支持的 GPU 与对应环境。

参阅 [构建与测试](docs/development.md)、[验收范围](docs/testing.md)、[首版发布说明](docs/releases/v0.3.5.md) 和 [第三方组件](THIRD_PARTY_NOTICES.md)。

## 反馈问题

请通过 Issues 提交 App 版本、显卡、显存、系统内存、复现步骤和错误信息。识别显卡失败时，可在向导的“更多帮助与电脑信息”中复制检测信息。日志可能包含本机路径及提示词，提交前请检查并删去个人信息。CUDA 相关反馈请明确是安装、环境检查还是生成阶段失败。
