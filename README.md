# LightX2V Windows App

English | [简体中文](README.zh-CN.md)

A native Windows app for local text-to-image generation and editing with up to three reference images. Powered by [LightX2V](https://github.com/ModelTC/LightX2V) and [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo), with optional [llama.cpp](https://github.com/ggml-org/llama.cpp) prompt enhancement.

Windows 11 x64, no WSL required. AMD ROCm / NVIDIA CUDA (preview); see [tested hardware](docs/testing.md).

## Download

| Version | Windows x64 | Status |
| --- | --- | --- |
| v0.3.10 | [Download App](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/v0.3.10/LightX2V-Windows-0.3.10-win-x64.zip) | Pre-release |

Extract the entire ZIP, run `LightX2V.Windows.exe`, and follow the wizard to prepare the environment and download models.

Current builds are unsigned. [Setup guide](docs/getting-started.md) · [Release notes](docs/releases/v0.3.10.md)

## Supported models

- [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo)
- [Qwen-Image-2.1-PE-T2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-T2I-GGUF-Q8_0)
- [Qwen-Image-2.1-PE-I2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-I2I-GGUF-Q8_0)

## Build from source

On Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Python 3.12, then run from the repository root:

```powershell
.\build.ps1
```

[Development](docs/development.md) · [Third-party components](THIRD_PARTY_NOTICES.md)
