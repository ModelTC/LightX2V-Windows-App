# RTX 4070 Laptop CUDA acceptance

On 2026-11-12, the user confirmed that CUDA had passed acceptance on the RTX 4070 Laptop. This is a user-reported hardware acceptance result, not a CUDA simulation on the AMD host. The confirmation did not include a new build identifier or additional raw measurements; the detailed v0.3.8 records below retain their original scope. It does not imply acceptance of every NVIDIA GPU or automatically authorize Stable promotion.

## Detailed v0.3.8 records

This section summarizes user-provided records from a Windows test machine, using source commit `c5c1f713be99aa3ab1e59e50425c91025f84e866`. These are not results from simulating CUDA on the AMD development machine. The raw logs and generated images are not distributed with the public source.

## Test machine and results

Intel i9-14900HX, 31.80 GiB physical memory, RTX 4070 Laptop with 8188 MiB GPU memory, Windows 11 25H2, and driver 621.01. The environment uses pinned PyTorch 2.11.0+cu128, safetensors 0.8.0, and BF16 Qwen-Image-2.1-Turbo.

| Output | Prompt enhancement | Total task time | Image pipeline | Peak GPU allocated / reserved |
| --- | --- | --- | --- | --- |
| 512×512 | Off | 82.69 s | 47.04 s | 1.57 / 2.05 GiB |
| 512×512 | On | 372.27 s | 50.00 s | 1.75 / 2.05 GiB |
| 1248×832 | On | 426.05 s | 68.97 s | 2.68 / 3.85 GiB |
| 2496×1664 | On | 554.89 s | 174.74 s | 3.73 / 4.74 GiB |

All four runs completed 8 steps, produced decodable PNGs, and had worker exit code 0, using `ssd_streaming` and the Windows `pread` strategy. The v0.3.7 `torch_cpu.dll / 0xC0000005` failure did not recur in this round.

1K and 2K automatically used tiled VAE decoding with a tile size of 512; 512 used untiled decoding. PE automatically selected 17 GPU layers, with the remaining layers running on the CPU. PE took approximately 301–358 seconds and accounted for most of the time with enhancement enabled. Context was not shortened and the output budget was not changed.

## Metric definitions and limits

- These samples demonstrate that this machine with 8 GB GPU memory / 32 GB system memory can complete the listed text-to-image tasks. They do not establish acceptance for all 8 GB devices, prompts, aspect ratios, or concurrent workloads.
- The peak allocator reserved value does not represent total dedicated memory use across the discrete GPU.
- The four runs were consecutive tasks. Full exits and relaunches before each run were not recorded, and the tests did not cover reference image editing, PE-I2I, cancellation, or consecutive 2K runs.
- The single 2K run had a commit peak of 34.61 / 35.31 GiB, leaving approximately 0.70 GiB of headroom. Substantial physical memory was still available; the two metrics must not be conflated.
- The first worker startup / import was slower than later samples. However, this report did not measure the time from App launch to an interactive interface, so it cannot directly serve as a timing baseline for this round of startup interface changes.

These limitations describe the archived v0.3.8 measurements, not the current CUDA acceptance status confirmed above. v0.3.8 supported at most three references; the current app supports eight. Keep future regression evidence scoped to its tested build and workflows, including relaunches, reference images and recovery after stopping a task.
