# Getting started

## Prepare the app

Download the Windows x64 ZIP from this repository's Releases page and choose **Extract All**. Open the extracted folder and run `LightX2V.Windows.exe`. Keep the entire folder: extracting only the EXE or running it directly inside the ZIP is not supported.

This is an unsigned preview build. A SHA-256 file is provided for checking download integrity. If Windows blocks the app, first confirm that it came from this project's Releases page. You do not need to disable Windows protection or change system security settings.

## Complete setup in four steps

1. **Workspace:** Choose a folder you can read and write, with enough space for the environment, models, and creations. Allow about 100 GB of free space. If a long path causes installation problems, choose a shorter location such as `D:\LightX2V`.
2. **Prepare your computer:** The app scans your graphics hardware and recommends a backend. Graphics software, also called a driver, lets Windows use the GPU. Keep a working installed driver; if one is missing, follow the official installation assistant. Save other work before a requested restart. Return to the app, check again, and choose a compatible environment or use automatic preparation.
3. **Download model files:** Click **Download required files automatically**. The Qwen image model is about 32 GB; prompt enhancement requires additional model files and can be left disabled initially. Choose Hugging Face or ModelScope. Interrupted downloads can be retried and completed data reused.
4. **Finish setup:** Click **Start creating** to enter the workspace. Generating a sample image is not required.

The app includes the .NET runtime. Automatic preparation installs its own Python in the selected workspace without replacing system Python. WSL and a system CUDA Toolkit are not required. NVIDIA needs a compatible graphics driver; the current CUDA profile requires a driver exposing CUDA 12.8 or newer and must pass the actual GPU environment check.

## Create your first image

Enter a scene description, choose a size and aspect ratio, and generate. Use the plus button to add reference images. The existing Prompt enhancement switch controls enhancement. For laptops, connect power and close applications using substantial GPU or system memory.

Starting with v0.3.7, the app automatically chooses model residency, offloading, or streaming from currently available resources. No manual performance mode is needed. Machines with sufficient capacity keep the ordinary inference path; constrained devices may spend more time reading from the CPU and SSD, including on first load.

512 output requires less image working memory than 1K or 2K, but does not shrink model weights. Choosing 512 alone does not establish that a model-loading memory failure is resolved. Check the task's resource plan and logs to distinguish loading, image computation, enhancement, and runtime failures.

The documented RTX 4070 Laptop with 8 GB VRAM and 32 GB RAM completed four v0.3.8 text-to-image tasks, including 512, 1K, 2K, and prompt enhancement. Repeated app restarts and reference-image editing on that test machine still need verification. These samples do not guarantee every size or 8 GB configuration; passing an environment check alone is not proof that all generation paths work. See [RTX hardware results](rtx-4070-laptop-results.md) and [Automatic resource planning](automatic-resources.md).

## Data and upgrades

Creations and parameters are saved under the workspace's `outputs` folder; `workspace.json` indexes the history. Preferences are normally stored at `%LOCALAPPDATA%\LightX2V\configuration\preferences.json`. To upgrade, close the old app, fully extract the new version into a new program folder, and continue using the existing workspace. Complete models and downloaded installation packages can be reused.

Stopped downloads can be retried. For generation failures, inspect the task logs. Starting with v0.3.7, tasks also save `resource-plan.json`, stage telemetry, and host-memory samples. For GPU detection problems, copy the detection information under **More help and computer information**, or inspect `setup/hardware-detection.json` in the workspace. Review usernames, local paths, prompts, and reference images before sharing diagnostic data.

v0.3.8 reuses v0.3.7 models, Python environments, and workspaces without clearing directories. It fixes Windows weight-reading paths and improves crash diagnostics; see [Windows checkpoint I/O](windows-checkpoint-io.md).

Starting with v0.3.9, select a sent prompt and use Ctrl+C or the context menu to copy it. The image-as-reference button below a generated result appends that image to the draft. Enter editing instructions and generate; existing draft text and parameters are not restored from the old task. A configured workspace is interactive on launch. Necessary environment checks run on submission, support cancellation, and preserve the draft on failure.
