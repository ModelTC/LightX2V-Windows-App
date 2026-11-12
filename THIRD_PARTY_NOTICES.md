# Third-party components

This application uses the following projects. Their licenses apply to their respective components; model weights and software licenses are separate. The repository currently provides no separate license grant for the App's original code.

| Component | Source / fixed version | License information |
| --- | --- | --- |
| LightX2V inference source | [ModelTC/LightX2V](https://github.com/ModelTC/LightX2V), release `0.6.1`, revision `43c6a11bb65b7357f06e9603fa92249bc16b484e`, with native Windows compatibility changes | Apache-2.0; [retained license](third_party/licenses/LightX2V-Apache-2.0.txt); additional component licenses remain inside the bundled source |
| llama.cpp | [ggml-org/llama.cpp](https://github.com/ggml-org/llama.cpp), official Windows Vulkan release `b11564` | MIT; retained at `LightX2V.Windows/Resources/LICENSE-llama.cpp.txt` and copied into the runtime |
| LLVM OpenMP runtime | Included by the pinned llama.cpp release | The release's `LICENSE-LLVM-OpenMP` is copied alongside its binaries |
| .NET / WPF | Microsoft .NET 10, self-contained Windows x64 publish | SDK `LICENSE.txt` and `ThirdPartyNotices.txt` are retained in the portable package |
| Python and inference dependencies | Downloaded into the user's workspace using the committed AMD / CUDA manifests | Licenses distributed in the official Python archive and wheel metadata remain with the installed environment |

The exact LightX2V archive SHA-256 and modified files are recorded in [source-provenance.json](third_party/source-provenance.json). The reviewable changes are in [windows-compat-0.6.1.patch](third_party/windows-compat-0.6.1.patch). The archive is rebuilt from the fixed upstream ZIP with these reviewable patches; both upstream and patched hashes are recorded.

Qwen-Image-2.1-Turbo, PE-T2I and PE-I2I weights are downloaded separately from their model repositories. Consult the model cards and their licenses before use or redistribution; this repository does not include their weights. GPU driver assistants are downloaded from the vendors when requested; this repository does not redistribute the driver installers.
