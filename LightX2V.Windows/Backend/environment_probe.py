"""Model-independent checks for the pinned native Windows AMD profile."""
import importlib
import importlib.metadata
import platform
import re
import sys

REQUIRED = {'transformers': '5.20.2', 'diffusers': '0.42.0', 'safetensors': '0.8.0',
            'accelerate': None, 'Pillow': None, 'numpy': None, 'psutil': None,
            'loguru': None, 'einops': None, 'imageio': None, 'imageio-ffmpeg': None,
            'opencv-python': None, 'ftfy': None, 'aiohttp': None, 'pydantic': None,
            'prometheus_client': None, 'gguf': None, 'scipy': None, 'sentencepiece': None}


def probe(request, emit):
    if request.get('protocol') != 1: raise ValueError('Unsupported request protocol.')
    if sys.platform != 'win32' or sys.maxsize <= 2**32: raise ValueError('Use native x64 Windows Python.')
    versions = {}; missing = []
    for name, minimum in REQUIRED.items():
        try:
            version = importlib.metadata.version(name); versions[name] = version
            if minimum and tuple(map(int, re.findall(r'\d+', version)[:3])) < tuple(map(int, minimum.split('.'))): missing.append(name + '>=' + minimum)
        except importlib.metadata.PackageNotFoundError: missing.append(name)
    if missing: raise RuntimeError('Missing or incompatible dependencies: ' + ', '.join(missing))
    # Imports catch broken binary extensions as well as missing metadata.
    for module in ('PIL.Image', 'numpy', 'safetensors.torch', 'transformers', 'diffusers', 'cv2', 'scipy', 'psutil'):
        emit('log', message='Checking ' + module)
        importlib.import_module(module)
    import torch
    import torch.nn.functional as F
    from runtime_profile import detect_backend
    backend = detect_backend(torch, request.get('backend', 'auto'))
    device = torch.cuda.get_device_name(0)
    emit('log', message='Checking GPU BF16 matrix multiplication and attention')
    torch.manual_seed(42)
    a = torch.randn(32,32,dtype=torch.bfloat16); expected = a.float() @ a.float()
    actual = (a.cuda() @ a.cuda()).float().cpu()
    torch.testing.assert_close(actual, expected, rtol=.03, atol=.3)
    q = torch.randn(1,2,32,32,dtype=torch.bfloat16)
    value = F.scaled_dot_product_attention(q.cuda(),q.cuda(),q.cuda()).float().cpu()
    if not bool(torch.isfinite(value).all()): raise RuntimeError('AMD attention check returned invalid values.')
    torch.cuda.synchronize()
    import psutil
    free_vram, total_vram = torch.cuda.mem_get_info(0)
    memory = psutil.virtual_memory()
    emit('ready', backend=backend, cuda=getattr(torch.version, 'cuda', None), gpuFree=free_vram, gpuTotal=total_vram, ramAvailable=memory.available, ramTotal=memory.total, device=device, python=platform.python_version(), torch=str(torch.__version__), hip=torch.version.hip, packages=versions, gpuTests='BF16 GEMM and SDPA passed')
    return 0
