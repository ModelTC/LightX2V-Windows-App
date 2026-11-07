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


def inference_profile(backend, config, width, height, free_vram=None, plan=None):
    """Apply a model/task resource plan; hardware tiers are never UI modes."""
    if backend not in BACKENDS:
        raise ValueError('Unknown inference backend.')
    if plan is None:
        raise ValueError('Automatic inference requires a model resource plan.')
    from resource_plan import apply_resource_plan
    return apply_resource_plan(config, plan), plan['strategy']


def inference_environment(backend, parent, repository, large, plan=None):
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
    env['LIGHTX2V_PIN_HOST_WEIGHTS'] = '1' if plan is None or plan.get('pin_host_weights', True) else '0'
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
    # Native runtimes may write compiler diagnostics to stdout during GPU init.
    # Read the explicit JSON result instead of treating diagnostics as the result.
    return parse_runtime_output(result.stdout)


def parse_runtime_output(output):
    import json
    for line in reversed(output.splitlines()):
        try:
            value=json.loads(line)
        except (ValueError,TypeError):
            continue
        if isinstance(value,dict) and value.get('backend') in BACKENDS and all(k in value for k in ('free','total','ramAvailable','ramTotal')):
            return value
    raise RuntimeError('GPU runtime inspection did not return a valid resource result.')


if __name__ == '__main__':
    import json
    import sys
    import torch
    backend = detect_backend(torch, sys.argv[1])
    free, total = torch.cuda.mem_get_info(0)
    import psutil
    properties = torch.cuda.get_device_properties(0)
    memory = psutil.virtual_memory()
    print(json.dumps(dict(backend=backend, free=free, total=total,
                         ramAvailable=memory.available, ramTotal=memory.total,
                         shared_memory=bool(getattr(properties, 'is_integrated', False)),
                         device=properties.name,
                         capability=list(torch.cuda.get_device_capability(0)))))
