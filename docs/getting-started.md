# Getting started

## Prepare the app

Download the Windows x64 ZIP from this repository's Releases page and choose **Extract All**. Open the extracted folder and run `LightX2V.exe`. Keep the entire folder: extracting only the EXE or running it directly inside the ZIP is not supported.

This is an unsigned preview build. A SHA-256 file is provided for checking download integrity. If Windows blocks the app, first confirm that it came from this project's Releases page. You do not need to disable Windows protection or change system security settings.

## Complete first-time setup

The Windows four-step assistant is retained: choose a workspace, prepare the computer, download required image files, then start creating. Hardware scanning, official driver guidance, checked environment selection and automatic installation keep their existing flow. Only the base image model is required; no sample generation is required. Finishing preparation selects Standard.

For later changes, **General settings** uses three compact cards: language, workspace and PyTorch environment. Environment search and preview preparation do not switch your active workspace until you save. Environment search and installation open in a separate dialog. WSL and a system CUDA Toolkit are not required; the portable package includes .NET.

## Create your first image

Enter a scene description, choose a size and aspect ratio, and generate. Use the plus button to add reference images. The existing Prompt enhancement switch controls enhancement. For laptops, connect power and close applications using substantial GPU or system memory.

Starting with v0.3.7, the app automatically chooses model residency, offloading, or streaming from currently available resources. No manual performance mode is needed. Machines with sufficient capacity keep the ordinary inference path; constrained devices may spend more time reading from the CPU and SSD, including on first load.

512 output requires less image working memory than 1K or 2K, but does not shrink model weights. Choosing 512 alone does not establish that a model-loading memory failure is resolved. Check the task's resource plan and logs to distinguish loading, image computation, enhancement, and runtime failures.

The documented RTX 4070 Laptop with 8 GB VRAM and 32 GB RAM completed four v0.3.8 text-to-image tasks, including 512, 1K, 2K, and prompt enhancement. Repeated app restarts and reference-image editing on that test machine still need verification. These samples do not guarantee every size or 8 GB configuration; passing an environment check alone is not proof that all generation paths work. See [RTX hardware results](rtx-4070-laptop-results.md) and [Automatic resource planning](automatic-resources.md).

## Data and upgrades

Creations and parameters are saved under the workspace's `outputs` folder; `workspace.json` indexes the history. Preferences are normally stored at `%LOCALAPPDATA%\LightX2V\configuration\preferences.json`. To upgrade, close the old app, fully extract the new version into a new program folder, and continue using the existing workspace. Complete models and downloaded installation packages can be reused.

Stopped downloads can be retried. Model status buttons recheck local completeness; downloads verify file hashes. If enhancement files are missing, use the appropriate model card to download them or turn off prompt enhancement. The send button tooltip explains missing requirements.

Damaged preferences and history indexes are preserved alongside their originals with a `.corrupt-*` suffix. The app can restore valid `.bak` settings or rebuild history from saved task records. An unsupported newer workspace format is preserved and requires a compatible app version.

For generation failures, inspect the task logs. Starting with v0.3.7, tasks also save `resource-plan.json`, stage telemetry, and host-memory samples. For GPU detection problems, copy the detection information under **More help and computer information**, or inspect `setup/hardware-detection.json` in the workspace. Review usernames, local paths, prompts, and reference images before sharing diagnostic data.

v0.3.8 reuses v0.3.7 models, Python environments, and workspaces without clearing directories. It fixes Windows weight-reading paths and improves crash diagnostics; see [Windows checkpoint I/O](windows-checkpoint-io.md).

Starting with v0.3.9, select a sent prompt and use Ctrl+C or the context menu to copy it. The image-as-reference button below a generated result appends that image to the draft. Enter editing instructions and generate; existing draft text and parameters are not restored from the old task. A configured workspace is interactive on launch. Necessary environment checks run on submission, support cancellation, and preserve the draft on failure.

## Input and maintenance

Enter sends, Shift+Enter inserts a newline, and Ctrl+Enter remains available. An IME candidate confirmation does not submit. Add up to eight references using the file picker, drag and drop, clipboard image paste, or a generated image. Imports use a shared 100 MB / 40 megapixel policy; a failed batch preserves the previous draft.

Each creation remembers its size, aspect ratio and enhancement preference. Reusing a task selects its original creation and prepares the input without submitting it. Generation uses a new random seed.

General settings includes **Uninstall** with model weights, workspace data, and app/settings groups. Review the exact paths before confirming. External resources are unchecked by default; overlapping retained resources and directory links are rejected. Application removal follows the packaged file manifest and keeps other files placed in the application folder. A packaged application is needed to launch its maintenance helper. A failed maintenance job leaves a diagnostic under `%LOCALAPPDATA%\LightX2V-maintenance`. In-app updates and INT8 remain deferred.

With no saved current-user configuration, the four-step first-run assistant opens. Old development folders are never imported automatically. Entering the computer step automatically searches and selects a compatible checked environment; confirmation is required before reuse or installation. Entering the model step automatically searches existing locations and then local fixed disks. Folder buttons can select locations excluded from automatic search. Existing saved user preferences continue to restore across upgrades.

The package keeps runtime files in `app` and documentation/licenses in `docs`. Keep both beside the public launcher. Models and workspaces are separate. Inference uses the verified bundled LightX2V 0.6.1 source; an external checkout is never silently used for generation or overwritten.
