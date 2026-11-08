# Automatic resource planning

v0.3.7 selects a runtime strategy in the background based on the current device, available memory, and model metadata. AMD ROCm and NVIDIA CUDA use the same planning logic. The existing interface still controls the GPU backend, selected dimensions, reference images, and Prompt enhancement switch; no performance modes or manual offload options are added.

This change does not mean that all 8 GB GPUs have passed acceptance testing. The RTX 4070 Laptop 8 GB + 32 GB system memory configuration still requires complete testing on the actual hardware. Passing the environment check only confirms that the runtime environment is available.

## Information used for planning

Each generation checks the GPU runtime environment and reads safetensors headers to determine the sizes of the DiT, text encoder, and VAE weights actually used, individual layers, and loading buffers. Planning does not load the complete model weights into memory.

Planning also considers currently available GPU memory, available physical system memory, output pixel count, and whether the task is text-to-image or reference image editing. Workspace overhead and safety margins are conservative estimates, not minimum requirements measured on the hardware. Other applications, drivers, and operators still affect actual peaks.

- **Discrete GPUs**: GPU memory and system memory are budgeted separately. Model weights on the CPU, transfer buffers, and active weights on the GPU count toward their respective budgets.
- **Shared-memory devices**: On devices such as the Radeon 8060S, the GPU and CPU use the same physical memory pool. Planning does not add nominal GPU memory to system memory, and accounts for the overhead of CPU source weights and GPU copies existing at the same time.

After Prompt enhancement finishes and its subprocess exits, resources are checked again before planning image inference. Runtime environment checks use short-lived subprocesses; the supervisor does not retain a PyTorch GPU context.

## Automatically selected paths

| Path | Behavior |
| --- | --- |
| Full residency | When capacity is sufficient, models remain on the GPU and use the normal inference path, avoiding unnecessary layer-by-layer reads and transfers. |
| Component offloading | Components such as the text encoder and DiT move to the GPU by stage, and their memory is released when the stage ends. |
| Block offloading | System memory holds the DiT weights; the GPU retains only the blocks and buffers needed for the current computation. |
| Disk streaming | When CPU or GPU capacity cannot hold an entire component, BF16 layers are read on demand. The text encoder can look up vocabulary entries on the CPU and transfer only the required embeddings. |

Text encoder streaming and DiT streaming are planned separately. The VAE selects tiled decoding and tile size according to the budget. Streaming depends on SSD throughput and can be substantially slower than full residency; a smaller loading memory footprint does not imply the same generation speed.

Models still use BF16, and Turbo still uses a fixed 8 steps with the corresponding sampling parameters. Automatic resource planning preserves the dimensions, reference images, and enhancement settings requested by the user. A 512 output needs a smaller image workspace than 1K/2K, but does not shrink model weights. Changing the dimensions to 512 alone cannot resolve every model-loading memory shortage.

## Prompt enhancement

Prompt enhancement continues to use local llama.cpp. The application estimates the number of GPU layers from GGUF metadata and current resources; the CPU handles the remaining layers. Context, output budget, and sampling parameters retain their existing settings. Multiple reference images retain their order, with a per-image token budget controlling the visual context.

The enhancement model and image model run in separate stages. Image inference starts only after the enhancement process exits and releases its resources. Supervision, metadata reads, and host resource monitoring do not require importing torch.

## Memory pressure during execution

Severe system memory shortages are checked before inference starts. Physical memory is also monitored during execution. Only sustained severe pressure stops the processes belonging to the task; brief loading fluctuations do not trigger immediate termination based on a fixed multi-GB reserve.

Diagnostic information is preserved when a task stops. Memory estimates cannot eliminate all OOM failures. For a GPU OOM, a stop caused by system memory pressure, or a driver error, use that task's logs to determine the actual cause.

## Diagnostic files in the task directory

Each task saves an independent record. Common files include:

| File | Contents |
| --- | --- |
| `resource-plan.json` | The selected strategy, current resources, weight statistics, budgets, workspace estimates, and reasons for the selection. |
| `config.json` | The configuration actually used for this inference run. |
| `invocation.json` | Launch arguments, working directory, and key runtime environment details. |
| `inference.log` | Raw LightX2V inference logs. |
| `telemetry.jsonl` | Events for stages within the worker, including module initialization, loading, encoding, DiT, and VAE. |
| `resource-usage.json` | Cumulative peaks, minimum available resources, and a status summary from worker stage telemetry. |
| `host-telemetry.jsonl` | Periodic supervisor samples of its child processes and host memory, plus memory protection events. |
| `host-resource-usage.json` | A summary of host samples. |
| `supervisor-error.log` | Error details captured by the supervisor, created when a relevant error occurs. |
| `worker-start.json` | Worker startup identity, read strategy, and runtime status of the normal Python execution path. |
| `worker-fault.log` | The faulthandler log enabled early in startup; it may contain only identity information during normal execution. |
| `worker-exit.json` | The actual exit code, worker PID, native exceptions, and any dump paths found, as recorded by the supervisor. |

Prompt enhancement plans, commands, raw output, and diagnostics are in the `enhancement` subdirectory, including `resource-plan.json`, `invocation.json`, and `llama.log`.

Stage events are `stage_begin`, `stage_end`, and `stage_error`; `stage_id` and `parent_stage_id` identify nesting. Stage durations are wall-clock time, not isolated GPU kernel time. Telemetry does not force GPU synchronization for every block or sampling step.

- GPU fields include `allocated_bytes`, `reserved_bytes`, `max_allocated_bytes`, `max_reserved_bytes`, `device_free_bytes`, and `device_total_bytes`. Allocator peaks are cumulative peaks for that worker, and reserved memory is not a complete reading of actual dedicated memory use on a discrete GPU.
- Host fields include `process_rss_bytes`, Windows `process_private_bytes`, `system_available_bytes`, and system commit/limit.
- Windows commit uses `GetPerformanceInfo`, with the source identified by `system_commit_source` and `system_commit_exact`. When the API is unavailable, the pagefile proxy has a separate name and should not be treated as exact commit.
- The summary's `peaks` and `minimums` are statistics from recorded samples. Events already written remain available if a process is forcibly stopped. Brief CPU peaks between samples may go unobserved.

Before submitting Issues, remove personal information from logs, including usernames, local paths, prompts, and reference images.

## Manual RTX 4070 Laptop acceptance testing

The 8 GB GPU memory and 32 GB system memory configuration remains an acceptance testing target. Planning unit tests cannot replace actual image generation. Acceptance records should include the App version, GPU and driver, GPU environment, system memory, and resources available before generation.

1. Configure the CUDA environment through the existing wizard, complete the GPU check, and reuse downloaded models. Connect AC power and close applications that use substantial GPU or system memory.
2. Use a prompt of your choice and a fixed aspect ratio to test 512, then 1K, followed by 2K as the hardware permits. Disable enhancement first, then separately test the complete workflow with enhancement enabled. Smaller dimensions help isolate image workspace overhead; they do not establish that loading issues are resolved.
3. Add reference images and test reference image editing and PE-I2I separately. Check reference image order, output dimensions, PNG decodability, and the fixed 8-step workflow.
4. Test stopping a task during enhancement and during image generation. Confirm that the interface becomes usable again and the task's processes exit before starting the next task.
5. Save the resource plan, stage and host logs, duration, and result for each task. Record successful installation, a passed environment check, successful generation, and acceptable speed separately.

Only a complete workflow with valid output on the relevant hardware can establish that a configuration has passed acceptance testing. CUDA support remains in preview.

## Component lifecycle within a single task

Each generation task uses an independent worker with warmup disabled. After text encoding succeeds, it retains the conditioning embeddings and layout, and immediately releases text encoder weights and buffers that the task no longer needs. Reference image VAE encoding follows that release. DiT and VAE continue to use the selected resident or streaming paths. This stage-based release does not require copying GPU weights back to the CPU and does not enable disk streaming on devices with ample resources.

The resource plan estimates peaks separately for these stages, avoiding permanently adding an already released encoder to all later workspaces. This lifecycle is enabled only for explicitly marked App single-request workers; it does not change the behavior of reusable upstream runners.

On Windows, workers also record their process's DXGI LOCAL/NON_LOCAL budgets at the start and end. These are diagnostic values, not GPU allocator peaks or a fixed fast-pool capacity. All results are retained if adapters with the same name cannot be matched uniquely. Each task has its own writable temporary directory for native runtime compilers and subprocesses.

Prompt enhancement reads the complete GGUF attention and recurrent layer layout, budgeting KV and fixed state separately. Image editing also includes the visual projection model and image workspace. With sufficient resources, all layers stay on the GPU. When resources are constrained, the number of GPU layers is reduced automatically while preserving context, output budget, reference images, and sampling settings.

## v0.3.8 Windows read fix

Normal loading on Windows now uses pread. The streaming vocabulary explicitly reads only the required rows, and streamed layers use owned buffers. This fix does not change automatic resource path selection. See [Windows weight loading](windows-checkpoint-io.md).

A native crash may prevent the worker from writing `stage_end` or a final status. In that case, use `worker-exit.json` to determine the actual exit. In v0.3.8 telemetry, `historical_peaks` separately records historical metrics such as the commit peak since system boot, while `peaks` summarizes only samples successfully written to the log during that session. The fields `run_id`, `session_id`, `producer_pid`, and `observed_pid` distinguish the task and processes involved.
