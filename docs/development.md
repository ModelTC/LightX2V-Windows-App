# Building and Repository Layout

The app uses WPF for the Windows interface and a Python inference backend.

```text
LightX2V.Windows/       WPF app, Python bridge backend, styles, and pinned setup manifests
SetupUiChecks/         Maintained Windows regression suite executed locally
Tests/                 Python backend regressions and opt-in model/GPU checks
scripts/               Build/release integrity checks and manual package startup check
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

Use `-Destination C:\Builds\LightX2V-Windows` to specify a fresh output directory. The directory must not exist or must be empty to avoid mixing in files from an older version. The build also copies user documentation, component notices, and the .NET license, and generates a per-file SHA-256 manifest. The version is read from the project file.

`scripts/package_app.py` can also package a complete, already-published application folder on its own. It validates the pinned payloads and actual llama.cpp files and checks for missing components and version mismatches. A successful build is not reported as successful hardware acceptance. Existing private acceptance data is excluded from public packages.

## Testing

```powershell
python prepare_setup.py
python scripts/check_repository.py
python -m unittest discover -s Tests -v
dotnet build SetupUiChecks/SetupUiChecks.csproj -c Release
```

GitHub Actions compiles, verifies the package and publishes Latest. Behavioral tests run locally or on the AMD Windows host; see [Testing](testing.md). Stable is promoted manually from verified Latest bytes after AMD and RTX acceptance. See [Releasing](releasing.md).

## Setup Payloads and Source Provenance

The two environment manifests pin the versions, sizes, and SHA-256 hashes of Python, PyTorch, and every wheel. Builds do not resolve the "latest version." `prepare_cuda_profile.py` changes the pinned manifests; run it only when deliberately updating dependencies, followed by renewed acceptance testing.

`LightX2V.Windows/Resources/Setup/lightx2v-native.zip` contains the inference source required at runtime. It is the only large pinned source archive committed to the repository and does not use Git LFS. It is based on LightX2V 0.6.1 commit `43c6a11bb65b7357f06e9603fa92249bc16b484e`. Changes to the 11 common/Qwen native Windows compatibility files are in `third_party/windows-compat-0.6.1.patch`; the exact archive hash and changed files are recorded in `third_party/source-provenance.json`. The archive retains upstream licenses.

Do not commit models, environments, download caches, generated images, personal settings, QA logs, development SSH configuration, or historical packages. Raw development acceptance records remain outside the repository.

## Initial Window and Environment State

Recovery, first-use guidance, and model compatibility now have separate boundaries:

- `StateStore` owns versioned preferences and workspace indexes, last-valid backups, damaged-file preservation, and task-record recovery. A valid index reconciles newer known task records and tasks created after the index; older records removed from history remain removed. Unsupported schema versions are never rewritten.
- `LegacyWorkspaceImporter` contains the historical baseline adapter. Invalid legacy configuration falls back to fresh setup with a diagnostic instead of escaping before startup error handling.
- `BridgeClient` owns subprocess and redirected-stream lifetime. `BackendEvent` validates the protocol envelope and event field types; ordinary JSON remains a text log. A callback failure terminates the child and cancels the peer reader. Failure to write a cancellation signal still reaches timed process termination.
- `MainViewModel.Workspace`, `.Generation`, and `.Guidance` contain persistence coordination, generation events/cleanup, and actionable availability explanations. The main view model keeps the orchestration entry points.
- `Resources/model-catalog.json` is shared by C# and Python. It defines supported repositories, required files, component layouts, and compatibility constraints. It deliberately documents the current compatible `main`/`master` policy rather than claiming immutable model revisions. Dependency/runtime pins remain reproducible; a future model revision change still needs explicit compatibility review.

Model startup checks read small configuration files and weight headers. Successful tokenizer format checks are cached by path, size, and modification time. The repair service invokes the downloader's full source hash verification and can repair an already-ready model; the compact download guide only exposes command copy and the source link. Do not hash every multi-gigabyte weight at each launch.

First-use environment discovery prefers the selected and managed executables, publishes candidates incrementally, and runs independently of the blocking preparation state. The wizard can select or prepare a verified candidate before the remaining search finishes. Search has a 45-second overall budget; filesystem discovery also runs off the UI thread with a bounded outer wait. Reusing a candidate checks its backend and executable fingerprint, and never persists GPU readiness across app launches.

`StartupCoordinator` retains the four-step Windows setup surface when the environment or base model is missing, or a driver installation is pending. General settings uses the compact three-card dialog shown in the Mac references. The initial surface is shown with ordinary `Show()` and a taskbar entry, and the workspace is created only after it finishes. Configured workspaces open without an automatic GPU probe.

`SetupController` supports preview preparation without committing the edited workspace. Settings commit a prepared, matched environment only when saved. Existing setup, driver, download and first-send verification services remain authoritative.

Reference limits live in `Resources/image-input.json`, consumed by both C# and Python. Workspace indexes also carry backward-compatible per-creation parameters and the active creation ID. Immutable generation requests remain separate from draft settings.

The native launcher is built with Visual Studio C++ x64 Build Tools (MSVC and Windows SDK), using a static CRT. The public ZIP contains only `LightX2V.exe`, `app`, and `docs` at its top level. The layout-v2 manifest is `app/build-manifest.json`, with paths relative to the package root. Runtime asset paths remain relative to the WPF executable inside `app`. Layout-v1 manifests remain readable for verification and uninstall.

To reproduce the pinned source ZIP, download the exact `upstreamArchiveUrl` in `third_party/source-provenance.json`, then run `python scripts/rebuild_source.py <official-zip> <output-zip>`. The script verifies the upstream and patch hashes, applies the reviewed patch and requires the deterministic output hash to match the committed payload. Builds only verify these committed pins; they never resolve a moving tag or branch.


## v0.4.2 discovery and progress ownership

`ModelDiscovery` owns read-only traversal and `ModelValidation` remains the single completeness policy. `SetupController` owns page lifetime, cancellation, manual-selection revisions and committing results. Its background model and environment searches do not occupy installation busy state. Simulation suites inject isolated discovery roots; native acceptance uses real searches.

`AppSettings.AtWorkspace` owns managed-path relocation. Settings verifies a separate draft and checks cancellation before committing; closing does not alter the active backend, model paths or workspace. The historical baseline adapter has no automatic startup or wizard entry point.

`SetupInstaller` aggregates actual received bytes separately from completed/cache/resume bytes. `DownloadRate` owns the short rate window, and `DownloadDisplay` formats stage-specific speed/ETA for setup and model cards. Source sampling uses only manifest URLs; final artifact hashes remain mandatory.

The supervisor serializes protocol emission and complete UTF-8 task-log writes under one lock. Enhancement stderr is drained concurrently into its stage log and the unified log stream. The viewer limits the combined legacy fallback as well as new logs to 90,000 bytes. File reveal uses a background STA shell selection request, respecting the existing integrity boundary.
