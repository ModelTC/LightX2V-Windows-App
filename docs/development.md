# Building and Repository Layout

This repository is independent of the machine-validation workspace and can be cloned and built directly. The initial cleanup was based on v0.3.5; see the corresponding release notes for later first-time setup fixes.

```text
LightX2V.Windows/       WPF app, Python bridge backend, styles, and pinned setup manifests
Checks/                Early full desktop/inference acceptance checks; manual execution only
HeadlessChecks/        Windows Shell distribution checks
SetupUiChecks/         Wizard, download, hardware, and off-screen UI checks
Tests/                 Python unit tests that do not require a GPU
scripts/               Release packaging, repository checks, and local validation entry points
third_party/           Pinned inference source provenance, compatibility patches, and licenses
docs/                  Usage, testing, and release notes
build.ps1              Windows build and ZIP entry point
prepare_setup.py       Offline validation of pinned setup manifests and source archives
prepare_runtime.py     Fetch and validate the pinned llama.cpp Vulkan runtime
prepare_cuda_profile.py Explicit maintainer tool for refreshing CUDA manifests; not used by normal builds
rtx_acceptance.py      Real RTX image-generation acceptance using the environment prepared by the app
```

## Local Build

Use Windows x64, .NET SDK 10.0.403 (`global.json` allows later patches in that feature band), and Python 3.12. Normal builds do not require a GPU inference environment.

```powershell
.\build.ps1 -Dotnet dotnet -Python python
```

The build validates the pinned manifests for both backends, the source ZIP, and the graphics helper download manifest in `Resources/Setup`. It then validates and extracts the pinned llama.cpp archive and produces a self-contained win-x64 application and ZIP. Network access is mainly for NuGet and the official llama.cpp release; this process does not download tens of gigabytes of models.

Use `-Destination C:\Builds\LightX2V-Windows-0.3.6` to specify a fresh output directory. The directory must not exist or must be empty to avoid mixing in files from an older version. The build also copies user documentation, component notices, and the .NET license, and generates a per-file SHA-256 manifest. The version is read from the project file.

`scripts/package_app.py` can also package a complete, already-published application folder on its own. It validates the pinned payloads and actual llama.cpp files and checks for missing components and version mismatches. A successful build is not reported as successful hardware acceptance. Existing private acceptance data is excluded from public packages.

## Testing

```powershell
python prepare_setup.py
python scripts/check_repository.py
python -m unittest discover -s Tests -v
dotnet build SetupUiChecks/SetupUiChecks.csproj -c Release
dotnet build HeadlessChecks/HeadlessChecks.csproj -c Release
dotnet build Checks/Checks.csproj -c Release
```

GitHub Actions runs these checks and builds the package, saving the output as a workflow artifact. Ordinary branches do not modify Releases. Pushing a `v*` tag that matches the project version triggers a separate publishing job, which verifies the source commit, all file hashes, and uploaded assets again before publishing a pre-release. CI does not claim GPU acceptance. See [Testing Scope](testing.md).

## Setup Payloads and Source Provenance

The two environment manifests pin the versions, sizes, and SHA-256 hashes of Python, PyTorch, and every wheel. Builds do not resolve the "latest version." `prepare_cuda_profile.py` changes the pinned manifests; run it only when deliberately updating dependencies, followed by renewed acceptance testing.

`LightX2V.Windows/Resources/Setup/lightx2v-native.zip` contains the inference source required at runtime. At about 13 MB, it is the only large pinned source archive committed to the repository and does not use Git LFS. It is based on LightX2V commit `58b8e2592519711457e9fface5a804d7c337a1ae`. Changes to the 16 native Windows compatibility files are in `third_party/native-windows.patch`; the exact archive hash and changed files are recorded in `third_party/source-provenance.json`. The archive retains upstream licenses.

Do not commit models, environments, download caches, generated images, personal settings, QA logs, development SSH configuration, or historical packages. Raw development acceptance records remain outside the repository. Compatibility logic for reading user preferences and existing environments is retained; the initial repository cleanup did not change inference behavior.

## Initial Window and Environment State

Recovery, first-use guidance, and model compatibility now have separate boundaries:

- `StateStore` owns versioned preferences and workspace indexes, last-valid backups, damaged-file preservation, and task-record recovery. A valid index reconciles newer known task records and tasks created after the index; older records removed from history remain removed. Unsupported schema versions are never rewritten.
- `LegacyWorkspaceImporter` contains the historical baseline adapter. Invalid legacy configuration falls back to fresh setup with a diagnostic instead of escaping before startup error handling.
- `BridgeClient` owns subprocess and redirected-stream lifetime. `BackendEvent` validates the protocol envelope and event field types; ordinary JSON remains a text log. A callback failure terminates the child and cancels the peer reader. Failure to write a cancellation signal still reaches timed process termination.
- `MainViewModel.Workspace`, `.Generation`, and `.Guidance` contain persistence coordination, generation events/cleanup, and actionable availability explanations. The main view model keeps the orchestration entry points.
- `Resources/model-catalog.json` is shared by C# and Python. It defines supported repositories, required files, component layouts, and compatibility constraints. It deliberately documents the current compatible `main`/`master` policy rather than claiming immutable model revisions. Dependency/runtime pins remain reproducible; a future model revision change still needs explicit compatibility review.

Model startup checks read small configuration files and weight headers. Successful tokenizer format checks are cached by path, size, and modification time. Explicit **Check and repair** invokes the downloader's full source hash verification and can repair an already-ready model. Do not hash every multi-gigabyte weight at each launch.

First-use environment discovery prefers the selected and managed executables, publishes candidates incrementally, and runs independently of the blocking preparation state. The wizard can select or prepare a verified candidate before the remaining search finishes. Search has a 45-second overall budget; filesystem discovery also runs off the UI thread with a bounded outer wait. Reusing a candidate checks its backend and executable fingerprint, and never persists GPU readiness across app launches.

`StartupCoordinator` opens a separate wizard first when Python or required models are missing. `WpfStartupHost` uses ordinary `Show()`, a taskbar entry, and explicit exit control, and creates the workspace only after setup finishes. If workspace initialization finds that an existing user's configuration is unavailable, it hides the workspace before restoring the wizard. Do not change the initial wizard back to a `ShowDialog()` over the main window.

In v0.3.9, a configured workspace opens without an automatic GPU probe. The first generation request performs cancellable preflight checks and can reuse an existing session. Configuration snapshots and epoch guards prevent stale asynchronous results from being applied after the configuration changes. Explicit first-time setup can still probe the GPU.

Automatic installation performs one real GPU check through the environment service. After it succeeds, the candidate environment is added to the list and selected, and that result is used to connect the workspace without a second duplicate check. The first check for a newly installed environment has a 180-second timeout, and an explicit manual check uses 90 seconds. Background discovery caps each candidate at 30 seconds within the 45-second overall search budget. All checks are cancellable. Injected test services cover installation, checks, failure, and cancellation; fixtures do not validate real CUDA installation or image generation.
