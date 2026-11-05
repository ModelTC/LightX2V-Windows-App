"""Backend selection and conservative inference policy shared by probe and runner."""
import os

GIB = 1024 ** 3
BACKENDS = ('amd_rocm', 'cuda')


def detect_backend(torch, expected='auto'):
    if expected not in ('auto', *BACKENDS):
        raise ValueError('Unknown backend. Choose AMD ROCm or NVIDIA CUDA.')
    backend = 'amd_rocm' if getattr(torch.version, 'hip', None) else 'cuda' if getattr(torch.version, 'cuda', None) else ''
    if not backend or not torch.cuda.is_available():
        raise RuntimeError('No working GPU runtime. Install a compatible GPU driver and GPU-enabled PyTorch; CPU-only PyTorch cannot generate images in this app.')
    if expected != 'auto' and backend != expected:
        raise RuntimeError(f'This Python environment uses {backend}, but {expected} was selected. Choose the matching environment.')
    name = torch.cuda.get_device_name(0)
    if backend == 'amd_rocm':
        if not str(torch.__version__).startswith('2.11.0+rocm7.13.0') or not any(n in name.lower() for n in ('amd', 'radeon')):
            raise RuntimeError('Use the AMD PyTorch 2.11.0 / ROCm 7.13.0 Windows environment and a compatible AMD GPU.')
    else:
        if str(torch.__version__) != '2.11.0+cu128':
            raise RuntimeError('Use the pinned NVIDIA PyTorch 2.11.0+cu128 environment (CUDA 12.8).')
        major, minor = torch.cuda.get_device_capability(0)
        if major < 8 or not torch.cuda.is_bf16_supported(including_emulation=False):
            raise RuntimeError('This BF16 profile requires an NVIDIA GPU with native BF16 support (compute capability 8.0 or newer).')
        archs = torch.cuda.get_arch_list()
        if not any(a.startswith('sm_') and a[3:].isdigit() and int(a[3:])//10 == major and int(a[3:])%10 <= minor or a.startswith('compute_') and a[8:].isdigit() and int(a[8:]) <= major * 10 + minor for a in archs):
            raise RuntimeError(f'This CUDA wheel does not support GPU architecture sm_{major}{minor}. A different runtime profile is required.')
    return backend


def inference_profile(backend, config, width, height, free_vram=None):
    if backend not in BACKENDS:
        raise ValueError('Unknown inference backend.')
    config = dict(config)
    large = width * height > 1100000
    if backend == 'amd_rocm':
        if large:
            config.update(cpu_offload=True, offload_granularity='model', use_tiling_vae=True, vae_tile_size=1024)
        return config, 'qwen-2k' if large else 'standard'
    # BF16 weights must not assume the large shared-memory budget of the AMD test host.
    block = free_vram is None or free_vram < 28 * GIB
    config.update(cpu_offload=True, offload_granularity='block' if block else 'model',
                  text_encoder_cpu_offload=True, use_tiling_vae=True, vae_tile_size=512 if block else 1024)
    return config, 'cuda-block-offload' if block else 'cuda-model-offload'


def inference_environment(backend, parent, repository, large):
    env = dict(parent)
    env.update(PLATFORM=backend, DTYPE='BF16', SENSITIVE_LAYER_DTYPE='None', PYTHONUTF8='1',
               PYTHONIOENCODING='utf-8', PYTHONUNBUFFERED='1', PYTHONDONTWRITEBYTECODE='1',
               PYTHONPATH=repository, HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1',
               TOKENIZERS_PARALLELISM='false', LOGURU_COLORIZE='NO', PROFILING_DEBUG_LEVEL='2')
    for key in ('PYTORCH_NO_HIP_MEMORY_CACHING', 'TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL',
                'WORLD_SIZE', 'RANK', 'LOCAL_RANK', 'MASTER_ADDR', 'MASTER_PORT'):
        env.pop(key, None)
    if backend == 'amd_rocm' and large:
        env['TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL'] = '1'
    env['LIGHTX2V_PIN_HOST_WEIGHTS'] = '1'
    return env


def inspect_runtime(expected='auto'):
    """Query in a short-lived process; the image supervisor holds no GPU context."""
    import json
    import subprocess
    import sys
    from pathlib import Path
    result = subprocess.run([sys.executable, '-X', 'utf8', str(Path(__file__).resolve()), expected],
                            capture_output=True, text=True, encoding='utf-8', timeout=60,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    if result.returncode:
        raise RuntimeError(result.stderr.strip()[-2000:] or 'GPU runtime inspection failed.')
    return json.loads(result.stdout)


if __name__ == '__main__':
    import json
    import sys
    import torch
    backend = detect_backend(torch, sys.argv[1])
    free, total = torch.cuda.mem_get_info(0)
    print(json.dumps(dict(backend=backend, free=free, total=total)))
