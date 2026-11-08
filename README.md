# LightX2V Windows App v0.3.9

English | [Simplified Chinese](README.zh-CN.md)

A native Windows app for local text-to-image generation and reference-image editing with [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo). It uses [LightX2V](https://github.com/ModelTC/LightX2V) for image inference and [llama.cpp](https://github.com/ggml-org/llama.cpp) for optional prompt enhancement. The interface follows the [LightX2V Mac App](https://github.com/ModelTC/LightX2V-Mac-App).

**Current preview release: v0.3.9.** Native Windows x64; no WSL required. Configured workspaces are interactive on launch, with necessary environment checks performed when a request is submitted. Sent prompts can be selected and copied. The result toolbar replaces **Reuse settings** with **Use as reference** to add a generated image to your current draft. Automatic resource planning and the Windows weight-reading fixes from v0.3.8 are retained.

## Download and get started

| Version | Windows x64 | Status |
| --- | --- | --- |
| v0.3.9 | [Download App](https://github.com/ModelTC/LightX2V-Windows-App/releases/download/v0.3.9/LightX2V-Windows-0.3.9-win-x64.zip) | Pre-release |

[Release notes and known limitations](docs/releases/v0.3.9.md). Download the App ZIP above; GitHub's source-code archives do not contain a runnable app.

1. Extract the entire ZIP and run `LightX2V.Windows.exe`.
2. Follow the setup wizard to choose a workspace, check your computer, prepare the required components, and download models.
3. Click **Start creating** when setup is complete, then enter a prompt.

The portable package includes the .NET desktop runtime and pinned LightX2V inference source. The wizard can install Python and inference dependencies, reuse a compatible existing environment, and download models. **Regular users do not need a terminal, developer tools, or download commands.** When a graphics driver is needed, the wizard provides official download links and guidance; Windows may request permission or a restart.

See [Getting started](docs/getting-started.md). Current builds are unsigned. Download them from this repository's Releases page and keep Windows security protections enabled.

## Hardware and validation scope

| Backend | Current scope | Validation |
| --- | --- | --- |
| AMD ROCm | Automatic setup for Radeon 8060S; other AMD GPUs require a compatible existing native Windows environment | Earlier versions were tested with text-to-image generation, reference-image editing, 2K output, and prompt enhancement. See the current release notes for this version's checks. |
| NVIDIA CUDA | Native BF16 support and compatibility with the pinned PyTorch CUDA wheel; primarily targets RTX 30/40/50 series | Preview support; these GPU families have not all been tested. |

Windows 11 x64 is the currently tested platform. CPU-only inference, Intel GPUs, Windows ARM64, and FP16/quantized fallbacks for RTX 20 series are not currently supported.

The image model uses BF16 weights. The app selects model residency, offloading, or SSD streaming from the actual model and available resources. Machines with sufficient resources retain the normal resident path; users do not need to select a performance mode.

**RTX 4070 Laptop 8 GB VRAM + 32 GB RAM has four documented successful text-to-image samples from v0.3.8**, covering 512 / 1K / 2K and prompt enhancement. Repeated app restarts and reference-image editing on that test machine still need verification. See the [RTX hardware results](docs/rtx-4070-laptop-results.md). These samples do not establish support for every 8 GB GPU or configuration.

512 output reduces image working memory, but does not reduce model weight size or resolve every loading-memory issue. See [Automatic resource planning](docs/automatic-resources.md). Allow about 100 GB of free disk space for setup and model downloads; this is suggested headroom, not a fixed minimum disk requirement.

## Features

- Qwen-Image-2.1-Turbo with the fixed 8-step sampling schedule; text-to-image generation and editing with up to three reference images.
- 512 / 1K / 2K output, eight aspect-ratio choices, and adaptive dimensions.
- Optional PE-T2I / PE-I2I prompt enhancement with a local Vulkan runtime.
- Generation progress, cancellation, task logs, creation history, selectable sent prompts, generated images as references, and image copy/export.
- Chinese and English interface; `Ctrl+N` for a new creation and `Ctrl+Enter` to generate or stop.

Model weights are downloaded separately. Downloads support Hugging Face / ModelScope, resume, and integrity checks. H3 video and multi-model selection are planned for later versions.

## Supported models

- [Qwen-Image-2.1-Turbo](https://huggingface.co/Qwen/Qwen-Image-2.1-Turbo)
- [Qwen-Image-2.1-PE-T2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-T2I-GGUF-Q8_0)
- [Qwen-Image-2.1-PE-I2I-GGUF-Q8_0](https://huggingface.co/lightx2v/Qwen-Image-2.1-PE-I2I-GGUF-Q8_0)

## Build from source

On Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Python 3.12, then run from the repository root:

```powershell
.\build.ps1
```

The build produces `dist/LightX2V-Windows-0.3.9-win-x64.zip` and its `.sha256` file. It verifies the committed dependency pins and retrieves the pinned llama.cpp version from its official GitHub Release. It does not automatically upgrade locked dependencies. A GPU is not required to compile the app; inference requires a supported GPU and compatible runtime.

Additional documentation: [Development](docs/development.md), [Testing scope](docs/testing.md), [Release notes](docs/releases/v0.3.9.md), and [Third-party components](THIRD_PARTY_NOTICES.md).

## Report an issue

Include the app version, GPU model, VRAM, system RAM, reproduction steps, and error message in a GitHub Issue. For GPU detection problems, use **More help and computer information** in the setup wizard to copy detection details. Logs can contain local paths, prompts, and reference-image information; review them before sharing. For CUDA issues, specify whether the failure happens during installation, environment checks, or generation.
