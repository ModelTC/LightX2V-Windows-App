# LightX2V <img src="docs/assets/AppIcon.png" alt="" width="36" height="36" align="absmiddle">

[简体中文](README.zh-CN.md)

A native Windows app for local image generation and editing with [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo). It uses [LightX2V](https://github.com/ModelTC/LightX2V) for inference and [llama.cpp](https://github.com/ggml-org/llama.cpp) for optional prompt enhancement. CUDA and ROCm have been exercised on the RTX 4070 Laptop GPU and Radeon 8060S.

## Download

| Channel | Windows 11 · x64 | Commit | Date (UTC+8) |
| --- | --- | --- | --- |
| Latest | [Download Latest APP](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/latest/LightX2V-APP-Windows-x64.zip) | [`87ee88a`](https://github.com/ModelTC/LightX2V-Windows-App/commit/87ee88a931d0ad4819cf7397aeb05db608c20e45) | 2026-11-25 11:29 |
| Stable | Not available yet | — | — |

The app bundles LightX2V source, llama.cpp, and .NET. Model weights and a compatible Python environment are prepared separately in the app.

Each channel keeps one app package. Updating Stable replaces its previous package. Ordinary builds update Latest.

[Promote Latest to Stable](https://github.com/ModelTC/LightX2V-Windows-App/actions/workflows/promote-stable.yml)

**First launch on Windows:** Download the ZIP, extract the whole folder, and run `LightX2V.exe`. The package is unsigned. If Windows flags it, verify its origin and SHA-256 on the [release page](https://github.com/ModelTC/LightX2V-Windows-App/releases/tag/latest). See [Getting started](docs/getting-started.md) for setup.

### Supported models

- [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo)
- [Qwen-Image-2.1-PE-T2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-T2I-GGUF-Q8_0)
- [Qwen-Image-2.1-PE-I2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-I2I-GGUF-Q8_0)

## Build from source

On Windows x64, install .NET SDK 10.0.403, Python 3.12, and Visual Studio C++ x64 Build Tools with a Windows SDK, then run:

```powershell
git clone https://github.com/ModelTC/LightX2V-Windows-App.git
cd LightX2V-Windows-App
.\build.ps1 -Destination .\dist\LightX2V
.\dist\LightX2V\LightX2V.exe
```
