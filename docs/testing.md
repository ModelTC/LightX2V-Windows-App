# Testing

## Automated regression checks

Run the backend tests from the repository root:

```powershell
python prepare_setup.py
python scripts/check_repository.py
python -m unittest discover -s Tests -v
```

The Python suite covers downloads with local HTTP servers, checkpoint reads, tensor lifetimes, resource planning, protocol handling, and enhancement parsing. Tests that need complete model files or GPU execution are opt-in.

The `cpu-tests` CI job installs PyTorch 2.11.0 from the official CPU wheel index, with NumPy 2.5.4 and safetensors 0.8.0. Its selected CPU tensor/checkpoint cases must pass without skips. [PyTorch installation reference](https://pytorch.org/get-started/previous-versions/#v2110)

`SetupUiChecks` is the maintained Windows regression suite. CI publishes and runs it with the `core` mode. It covers setup, discovery and driver decisions with injected services, model readiness, recovery, subprocess handling, environment downloads through a local server, shell dispatch, logs, and saved UI state. Views are rendered offscreen.

To run it on Windows, use a fresh report directory:

```powershell
dotnet publish SetupUiChecks/SetupUiChecks.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o artifacts/core-checks
.\artifacts\core-checks\SetupUiChecks.exe C:\LightX2V-QA\core-run core
```

Omitting the mode also runs `core`. The optional `startup` mode runs only startup and readiness checks. Reports include assertions, failure details, binding diagnostics, and rendered views.

## Packaged application startup

From an interactive Windows session with the app closed, check an extracted package:

```powershell
.\scripts\check_packaged_app.ps1 -Executable C:\Apps\LightX2V\LightX2V.Windows.exe -Report C:\LightX2V-QA\package-startup
```

This check launches the packaged EXE using its bundled .NET runtime, accepts either the first-run wizard or a configured workspace, writes a UI startup report, and closes its own process. Validate image generation separately on the target hardware.

## Hardware acceptance

Complete setup in the app and record the app version, GPU model, driver, VRAM, system RAM, and runtime profile. Check these workflows on each target machine:

1. Fresh environment installation and model preparation, including cached files, cancellation, retry, and restart.
2. Text-to-image at 512 / 1:1 with enhancement disabled, followed by 1K and 2K where resources permit.
3. Reference-image editing, text-to-image and reference-image prompt enhancement, and repeated tasks after restarting the app.
4. Stop/retry, workspace restoration, model repair, and reported failures under resource pressure.

A CUDA host can exercise the packaged backend using the environment prepared by the app:

```powershell
python rtx_acceptance.py --app C:\Apps\LightX2V --python C:\LightX2V\environments\native-nvidia-cu128-v1\python.exe --repository C:\LightX2V\runtimes\lightx2v-native-HASH --model C:\LightX2V\models\Qwen\Qwen-Image-2.1-Turbo --output C:\LightX2V-RTX-acceptance --tiers 512
```

Use the paths from the app's configuration. `--tiers 512,1K,2K --edit` also exercises multiple image sizes and reference editing. Test installation and prompt enhancement through the app.

## Recorded results

Hardware results retain their version and configuration scope. [v0.3.12](releases/v0.3.12.md) records the AMD development-machine validation. [RTX 4070 Laptop results](rtx-4070-laptop-results.md) records the reported v0.3.8 text-to-image runs and remaining scenarios. Earlier release notes retain their corresponding acceptance results.

Keep raw QA data outside the source tree. Review local paths, prompts, reference images, and other personal information before sharing reports.
