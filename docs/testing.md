# Beta Testing and Acceptance Scope

The first internal beta was v0.3.5. The initial repository cleanup did not modify the WPF or Python backend code. Release assets should correspond to a specific source commit and SHA-256 hash.

## Existing Validation

- Radeon 8060S / Windows 11 x64: real native installation, 512/1K/2K image generation, reference-image editing, and PE-T2I / PE-I2I previously passed acceptance.
- v0.3.5: 113 Windows checks and 14 Python tests passed. The Windows checks covered hardware fallback, driver status, wizard integration, and the completion page using off-screen rendering. That round did not repeat the full model download or inference at every size.
- Historical inference evidence is reused only in association with the relevant backend, runtime, configuration, and payload hashes. It does not mean every UI version has repeated all inference tests.
- RTX 4070 Laptop / 621.01 detection regression checks used reported data and injected fixtures. **They do not represent access to a real RTX laptop or acceptance of a complete CUDA installation or image generation.**

Raw logs, paths, generated work, preferences, and complete QA bundles from personal machines are not uploaded to the public source repository. The statements above describe the validation scope. Public CI build artifacts record their own source commit and do not inherit these results as new GPU hardware validation.

## Checks That Do Not Require a GPU

Run `python -m unittest discover -s Tests -v` from the repository root. These tests cover download resume/verification, protocol and enhancement parsing, CUDA/ROCm profile selection, and VRAM strategy. They do not download or install the complete models.

`python prepare_setup.py` validates pinned setup payloads. `python scripts/check_repository.py` checks the public commit scope and source provenance. CI compiles the app and the three C# check projects, then publishes and executes `SetupUiChecks.exe <new-output-directory> core`. This mode runs startup, readiness, history recovery, bridge and model-download checks with injected services and small fixtures using off-screen rendering.

The `cpu-tests` CI job installs PyTorch 2.11.0 from the official CPU wheel index, with NumPy 2.5.4 and safetensors 0.8.0. It explicitly runs the CPU streaming and one-shot lifecycle classes, owned checkpoint reads, checkpoint policy checks, and four real safetensors CPU methods. Selected cases must pass without skips. This exercises real BF16 tensors and storage lifetimes with generated checkpoint fixtures. The versioned prerelease job waits for both the Windows build and this CPU job. The CPU installation command follows the official PyTorch 2.11.0 instructions. [PyTorch installation reference](https://pytorch.org/get-started/previous-versions/#v2110)

`HeadlessChecks` is compiled in CI and retains tests that depend on a working default browser. Run those tests explicitly on a suitable Windows test machine. GPU execution and comparisons against the complete official model checkpoints remain separate hardware acceptance work.

To run the core off-screen checks locally on Windows, use a fresh report directory.

```powershell
dotnet publish SetupUiChecks/SetupUiChecks.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o artifacts/core-checks
.\artifacts\core-checks\SetupUiChecks.exe C:\LightX2V-QA\core-run core
```

## AMD Off-Screen Checks

Publish and run on a Windows test machine with an AMD driver:

```powershell
dotnet publish SetupUiChecks/SetupUiChecks.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o artifacts/setup-checks
.\artifacts\setup-checks\SetupUiChecks.exe C:\LightX2V-QA\new-hardware-run hardware
```

Use a fresh output path. This mode does not display test windows, operate the real desktop, or install drivers. It asserts that a real AMD device is present, so it cannot be used directly as a generic test for GPU-free CI or NVIDIA machines. The default full mode also validates an existing AMD Python environment, which must be prepared beforehand.

Native check modes accept `LIGHTX2V_QA_PYTHON` as an absolute Python executable path for validation with a dedicated runtime and existing model files.

## RTX Hardware Acceptance

First complete CUDA environment setup in the app and record the GPU model, driver, VRAM, and system memory. At a minimum, verify:

1. Automatic NVIDIA GPU detection without requiring an already working driver to be reinstalled.
2. One-click setup and model download, stop, retry, and continued usability after restarting the app.
3. Text-to-image at 512 / 1:1 with enhancement disabled, checking the real PNG and complete logs.
4. After success, test 1K, 2K, reference-image editing, prompt enhancement, cancellation, and history restoration in sequence.
5. Actual errors and recovery when VRAM/system memory is insufficient, recording elapsed time. There is no promise that every size will run.

Developers can use `rtx_acceptance.py` to validate real images with the same backend as the release package:

```powershell
python rtx_acceptance.py --app C:\Apps\LightX2V --python C:\LightX2V\environments\native-nvidia-cu128-v1\python.exe --repository C:\LightX2V\runtimes\lightx2v-native-HASH --model C:\LightX2V\models\Qwen\Qwen-Image-2.1-Turbo --output C:\LightX2V-RTX-acceptance --tiers 512
```

Use the paths from the app's actual configuration. After confirming that resources are sufficient, use `--tiers 512,1K,2K --edit` to generate multiple real images and save requests, configurations, and logs. CLI checks do not replace testing the new-user wizard and creation interactions.

The `Checks` directory retains early interactive acceptance programs that open windows and run real inference; some modes download/install components. Run them explicitly on a dedicated test machine only, not by default on a desktop where a user is working.

## v0.3.6 First-Time Setup Regression

The added `SetupUiChecks.exe <new-output-directory> startup` mode uses only injected environment/driver services and small model-file fixtures. It checks list population after installation, selection, a single GPU check, failure and cancellation, a separate initial wizard window, minimizing during downloads, and completion/exit paths using off-screen rendering. It does not perform a real CUDA installation or open test windows. The `hardware` mode continues with native AMD and existing wizard regression checks after these checks.

The development machine passed 143 off-screen/native checks in `hardware` mode during this round, and 14 Python tests passed. The new first-time setup checks also run separately in CI with GPU-free installation fixtures.

## v0.3.8 Weight-Loading Regression

The AMD development machine completed 135 Python checks (including real GPU / official checkpoint checks), compilation of four C# projects, 30 off-screen startup checks, three streamed 512 runs with new workers, resident 512, streamed 1K / reference-image editing, and some chained GPU enhancement runs. See [v0.3.8](releases/v0.3.8.md) for output consistency, per-session telemetry checks, and the actual acceptance boundaries.

Reports of intermittent native crashes on an RTX 4070 Laptop in v0.3.7 and evidence of a single image generated by a standalone worker had been received. This did not establish stable CUDA acceptance for the app. v0.3.8 required confirmation through repeated tasks with the same parameters and actual tasks after restarting the app; AMD results or GPU-free CI could not substitute for this validation.

## RTX 4070 Laptop v0.3.8 Hardware Feedback

User test-machine records show 4/4 successful image generations: 512 with enhancement off / on, and 1K and 2K with enhancement on. All used the pread streaming path, and the previous native access violation did not recur. See [RTX Hardware Results](rtx-4070-laptop-results.md) for the model, timings, resource metrics, and the restart / I2I scenarios that remain untested. This hardware feedback was received later and does not retroactively change the development-machine acceptance record at the time of the v0.3.8 release.
