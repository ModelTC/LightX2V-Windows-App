# LightX2V Windows App

[English](README.md) | 简体中文

在 Windows 上用文字或最多 3 张参考图片创作图像。使用 [LightX2V](https://github.com/ModelTC/LightX2V) 和 [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo) 本地推理，可选 [llama.cpp](https://github.com/ggml-org/llama.cpp) 提示词增强。

Windows 11 x64，无需 WSL。支持 AMD ROCm / NVIDIA CUDA（预览）；[实测设备与范围](docs/testing.md)。

## 下载

| 版本 | Windows x64 | 状态 |
| --- | --- | --- |
| v0.3.11 | [下载 App](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/v0.3.11/LightX2V-Windows-0.3.11-win-x64.zip) | 内测版 / Pre-release |

完整解压 ZIP，运行 `LightX2V.Windows.exe`，按向导准备运行环境并下载模型。

当前构建未进行代码签名。[使用指南](docs/getting-started.md) · [发布说明](docs/releases/v0.3.11.md)

## 支持的模型

- [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo)
- [Qwen-Image-2.1-PE-T2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-T2I-GGUF-Q8_0)
- [Qwen-Image-2.1-PE-I2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-I2I-GGUF-Q8_0)

## 从源码构建

在 Windows 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 和 Python 3.12，然后在仓库根目录运行：

```powershell
.\build.ps1
```

[构建与开发](docs/development.md) · [第三方组件](THIRD_PARTY_NOTICES.md)
