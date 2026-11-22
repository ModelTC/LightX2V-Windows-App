# LightX2V <img src="docs/assets/AppIcon.png" alt="" width="36" height="36" align="absmiddle">

[English](README.md)

用于本地图像生成和编辑的 Windows 原生应用，支持 [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo)。图像推理使用 [LightX2V](https://github.com/ModelTC/LightX2V)，可选的提示词增强使用 [llama.cpp](https://github.com/ggml-org/llama.cpp)。已在 RTX 4070 笔记本显卡和 Radeon 8060S 上分别验证 CUDA 与 ROCm。

## 下载

| 通道 | Windows 11 · x64 | 提交 | 日期 (UTC+8) |
| --- | --- | --- | --- |
| Latest | [下载 Latest APP](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/latest/LightX2V-APP-Windows-x64.zip) | [`2abdff1`](https://github.com/ModelTC/LightX2V-Windows-App/commit/2abdff1c9dd90e7e701430b9c3ffc2ff0fd38d7c) | 2026-11-22 14:54 |
| Stable | 尚未提供 | — | — |

应用内置 LightX2V 源码、llama.cpp 和 .NET。模型权重与兼容的 Python 环境可在应用中单独准备。

每个通道保留一个安装包。更新 Stable 时替换原包，日常构建更新 Latest。

[将 Latest 晋升为 Stable](https://github.com/ModelTC/LightX2V-Windows-App/actions/workflows/promote-stable.yml)

**Windows 首次启动**：下载 ZIP，完整解压后运行 `LightX2V.exe`。安装包未签名。如果 Windows 提示风险，请在[发布页](https://github.com/ModelTC/LightX2V-Windows-App/releases/tag/latest)核对来源和 SHA-256。环境配置见[使用说明](docs/getting-started.md)。

### 支持的模型

- [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo)
- [Qwen-Image-2.1-PE-T2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-T2I-GGUF-Q8_0)
- [Qwen-Image-2.1-PE-I2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-I2I-GGUF-Q8_0)

## 从源码构建

在 Windows x64 安装 .NET SDK 10.0.403、Python 3.12，以及带 Windows SDK 的 Visual Studio C++ x64 Build Tools，然后运行：

```powershell
git clone https://github.com/ModelTC/LightX2V-Windows-App.git
cd LightX2V-Windows-App
.\build.ps1 -Destination .\dist\LightX2V
.\dist\LightX2V\LightX2V.exe
```
