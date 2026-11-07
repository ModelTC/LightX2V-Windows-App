"""Worker-local safetensors I/O policy for Windows.

Install before importing LightX2V, transformers or other safetensors consumers.
Windows workers force the supported ``pread`` backend for every framework and
both default and explicit mmap requests. The returned native handle retains its
normal context, metadata, get_tensor and get_slice APIs. Precision, placement,
streaming strategy and checkpoint files are unchanged.

This avoids safetensors 0.8's PyTorch whole-file COW storage mapping. That version
can still make a checked Rust metadata mapping before selecting pread; this
policy is not a promise that every metadata access is completely mmap-free.
CPU pread get_slice in 0.8 reads its entire source tensor before slicing. Bounded
embedding/block streaming must keep using the application's direct file reader.
"""
from functools import wraps
import importlib
import sys

_MARKER = '_lightx2v_windows_checkpoint_io'
_ORIGINAL = '_lightx2v_original_safe_open'
_STATE = '_lightx2v_checkpoint_io_state'


def _make_safe_open(original, state):
    @wraps(original)
    def safe_open(*args, **kwargs):
        requested = kwargs.get('backend', 'mmap')
        # Preserve the native validation/error for unknown backend objects.
        # Recognized mmap/pread calls alone are redirected by the worker policy.
        if requested not in ('mmap', 'pread'):
            return original(*args, **kwargs)
        selected = dict(kwargs)
        selected['backend'] = 'pread'
        state['open_calls'] += 1
        if requested == 'mmap':
            state['redirected_mmap_calls'] += 1
        return original(*args, **selected)
    setattr(safe_open, _MARKER, True)
    setattr(safe_open, _ORIGINAL, original)
    setattr(safe_open, _STATE, state)
    return safe_open


def install_windows_checkpoint_io():
    """Install once in this process and return JSON-serializable policy metadata.

    No torch or upstream application module is imported. Non-Windows processes
    are untouched, including not importing safetensors merely to report a no-op.
    """
    if sys.platform != 'win32':
        return {'enabled': False, 'policy': 'unchanged', 'platform': sys.platform,
                'backend': None, 'reason': 'not_windows'}
    module = importlib.import_module('safetensors')
    current = module.safe_open
    if getattr(current, _MARKER, False):
        wrapper = current
        original = getattr(current, _ORIGINAL)
        state = getattr(current, _STATE)
    else:
        original = current
        state = {'enabled': True, 'policy': 'windows_pread', 'platform': 'win32',
                 'backend': 'pread', 'safetensors_version': str(getattr(module, '__version__', 'unknown')),
                 'open_calls': 0, 'redirected_mmap_calls': 0, 'patched_modules': []}
        wrapper = _make_safe_open(original, state)
    # The package alias is the normal upstream entry point. Patch the extension
    # alias too, and already-loaded safetensors framework aliases without
    # importing any framework. Future ``from safetensors import safe_open``
    # statements will naturally receive the wrapper.
    candidates = [('safetensors', module)]
    candidates.extend((name, loaded) for name, loaded in tuple(sys.modules.items())
                      if name.startswith('safetensors.') and loaded is not None)
    for name, loaded in candidates:
        value = getattr(loaded, 'safe_open', None)
        if value is original or value is wrapper:
            setattr(loaded, 'safe_open', wrapper)
            if name not in state['patched_modules']:
                state['patched_modules'].append(name)
    return {**state, 'patched_modules': list(state['patched_modules'])}


def checkpoint_io_status():
    """Snapshot diagnostics without importing safetensors or changing the policy."""
    module = sys.modules.get('safetensors')
    current = getattr(module, 'safe_open', None)
    state = getattr(current, _STATE, None)
    return {**state, 'patched_modules': list(state['patched_modules'])} if state else {
        'enabled': False, 'policy': 'unchanged', 'platform': sys.platform, 'backend': None}
