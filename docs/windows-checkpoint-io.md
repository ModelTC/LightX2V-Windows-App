# Windows weight loading and native crash diagnostics

v0.3.8 fixes the read path to address the risks of mapping large safetensors files on Windows. It does not change device resource planning, BF16 weights, Turbo's 8-step sampling, user-selected dimensions, or the interface.

## Failure sample and limits of the evidence

In the v0.3.7 report from an RTX 4070 Laptop with 8 GB GPU memory / 32 GB system memory, a 512 text-to-image run with enhancement disabled had selected disk streaming for both components and successfully completed model initialization. The process exited with the native exception `0xC0000005` during text encoding, with `torch_cpu.dll` as the faulting module. The invalid read address matched the embedding's absolute offset within the safetensors file. A successful isolated rerun with the same configuration does not establish that consecutive App runs have passed acceptance testing.

This supports a diagnosis of an invalid mapping base address. The report alone does not establish that closing a `safe_open` context necessarily leaves dangling tensors: normal safetensors storage references allow returned tensors to remain alive. A complete dump for this failure sample is not included in the repository, so the specific Windows mapping error code and complete native call stack remain unverified.

Pinned upstream source versions and a related fix provide supporting evidence:

- The mmap / PyTorch path in [safetensors v0.8.0](https://github.com/huggingface/safetensors/blob/v0.8.0/bindings/python/src/lib.rs) uses a storage mapping of the entire file.
- [PyTorch v2.11.0 MapAllocator](https://github.com/pytorch/pytorch/blob/v2.11.0/aten/src/ATen/MapAllocator.cpp) uses copy-on-write mappings in its Windows regular-file branch. Unlike the shared-memory branch, that branch does not check the return value of `MapViewOfFile`.
- [Transformers PR #48341](https://github.com/huggingface/transformers/pull/48341) merged a change to use pread on Windows, indicating that whole-file copy-on-write mappings can exhaust commit.

Available physical memory and commit headroom are different metrics. Whole-file mappings incur commit overhead, and brief peaks can occur between periodic samples. Free physical memory and the absence of a CUDA OOM therefore do not fully rule out a memory resource problem during mapping.

## Read strategy

- Before importing LightX2V/Transformers, the Windows inference worker selects `pread` for safetensors, covering normal model and VAE loading. This policy is independent of GPU vendor or memory size and does not switch high-capacity devices to disk streaming.
- The disk streaming adapter uses a header index and explicit file I/O to read required layers into owned CPU tensors. It continues to reuse a single CPU staging block and a single GPU block.
- Streaming embeddings deduplicate token IDs, read only the required vocabulary rows, and then restore the original order. Vocabulary lookup does not map the entire roughly 16.3 GiB encoder file or copy the entire roughly 1.16 GiB vocabulary.
- Invalid headers, shapes, dtypes, offsets, and short reads raise errors at the Python level, preserving the original error. The application does not blindly retry generation after a native access violation or fall back to the full encoder with its larger memory footprint.

## Diagnostics and retesting

Each generation still uses a separate task directory. Native exceptions preserve the hexadecimal exit code, worker PID, and faulthandler records. A matching dump that Windows has already generated and that is accessible can be recorded. The App does not modify WER registry settings, guarantee that the system will generate a dump, or copy or upload dumps.

Host logs distinguish the process recording events from the process being sampled, and identify the session and sampling scope. The historical system commit peak from `GetPerformanceInfo` is not the commit peak for the current task. The task peak can only be calculated from actual samples in that session, and transients between samples may still be missed.

Manual RTX retesting should start with enhancement disabled, using the same prompt and seed for consecutive generations at 512 / 1:1, as well as multiple full App exits and relaunches. Record at least 5 actual tasks. Once those succeed, test 1K, reference images, and enhancement. Preserve task directories; do not delete models or reinstall the environment to mask a read issue.

AMD development machine regression testing and acceptance testing on actual CUDA hardware are recorded separately in [this release's notes](releases/v0.3.8.md). Resource budget fixture validation on the development machine does not establish capacity acceptance on a discrete 8 GB GPU with 32 GB physical memory, nor does it constitute cold-cache testing.
