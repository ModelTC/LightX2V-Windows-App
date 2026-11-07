"""Owned-storage safetensors reads for bounded App checkpoint streaming.

No mmap, safe_open, torch.from_file or borrowed file-backed tensors are used.
Headers are validated before allocations. Tensor payloads and selected vocabulary
rows are read directly into PyTorch-owned CPU storage with bounded readinto calls.
"""
from collections import defaultdict
from contextlib import contextmanager
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import re
import struct
import sys

MAX_HEADER_BYTES = 100_000_000
READ_CHUNK_BYTES = 8 * 1024 * 1024
_DTYPES = {
    'BOOL': ('bool', 1), 'U8': ('uint8', 1), 'I8': ('int8', 1),
    'I16': ('int16', 2), 'I32': ('int32', 4), 'I64': ('int64', 8),
    'F16': ('float16', 2), 'BF16': ('bfloat16', 2),
    'F32': ('float32', 4), 'F64': ('float64', 8),
    'F8_E4M3': ('float8_e4m3fn', 1), 'F8_E5M2': ('float8_e5m2', 1),
}
_BLOCK_KEY = re.compile(r'^transformer_blocks\.(\d+)\.')


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f'Duplicate safetensors header key: {key}')
        value[key] = item
    return value


def _invalid_json_constant(value):
    raise ValueError(f'Invalid safetensors JSON constant: {value}')


def _signature(stat):
    return stat.st_size, stat.st_mtime_ns, stat.st_dev, stat.st_ino


def _read_exact_into(source, target, *, description):
    """Handle ordinary short reads without allocating an additional payload copy."""
    completed = 0
    while completed < len(target):
        end = min(len(target), completed + READ_CHUNK_BYTES)
        try:
            count = source.readinto(target[completed:end])
        except InterruptedError:
            continue
        if count is None or count <= 0:
            raise EOFError(f'Incomplete checkpoint payload for {description}: {completed}/{len(target)} bytes')
        if count > end - completed:
            raise OSError(f'Invalid checkpoint read length for {description}')
        completed += count


@dataclass(frozen=True)
class TensorSpec:
    shard: str
    dtype: str
    shape: tuple
    offset: int
    nbytes: int


class BoundedSafetensorsCheckpoint:
    """Header index plus explicit file reads, compatible with the App adapters."""
    def __init__(self, checkpoint, *, torch_module=None):
        checkpoint = Path(checkpoint)
        self.checkpoint_dir = checkpoint if checkpoint.is_dir() else checkpoint.parent
        self._torch_module = torch_module
        self._specs = {}
        self._signatures = {}
        self.weight_map = {}
        files = sorted(checkpoint.glob('*.safetensors')) if checkpoint.is_dir() else [checkpoint]
        if not files or any(not path.is_file() for path in files):
            raise FileNotFoundError(f'Safetensors checkpoint not found: {checkpoint}')
        for path in files:
            self._index(path)

    @property
    def torch(self):
        if self._torch_module is None:
            import torch
            self._torch_module = torch
        return self._torch_module

    def _index(self, path):
        with path.open('rb', buffering=0) as source:
            stat = os.fstat(source.fileno())
            prefix = source.read(8)
            if len(prefix) != 8:
                raise ValueError(f'Incomplete safetensors header length: {path.name}')
            header_size = struct.unpack('<Q', prefix)[0]
            if header_size > MAX_HEADER_BYTES or header_size < 2 or header_size > stat.st_size - 8:
                raise ValueError(f'Invalid safetensors header size: {path.name}')
            raw = bytearray(header_size)
            _read_exact_into(source, memoryview(raw), description=f'{path.name} header')
            if raw[:1] != b'{':
                raise ValueError(f'Safetensors header must begin with a JSON object: {path.name}')
            try:
                header = json.loads(raw.decode('utf-8'), object_pairs_hook=_unique_object,
                                    parse_constant=_invalid_json_constant)
            except (UnicodeDecodeError, json.JSONDecodeError) as error:
                raise ValueError(f'Invalid safetensors header JSON: {path.name}') from error
            if not isinstance(header, dict):
                raise ValueError(f'Safetensors header must be an object: {path.name}')
            payload_size = stat.st_size - 8 - header_size
            intervals = []
            for name, entry in header.items():
                if name == '__metadata__':
                    if not isinstance(entry, dict) or any(not isinstance(k, str) or not isinstance(v, str) for k, v in entry.items()):
                        raise ValueError(f'Invalid safetensors string metadata: {path.name}')
                    continue
                if name in self._specs:
                    raise ValueError(f'Duplicate checkpoint tensor across shards: {name}')
                if not isinstance(entry, dict) or set(entry) != {'dtype', 'shape', 'data_offsets'}:
                    raise ValueError(f'Invalid safetensors tensor metadata: {name}')
                dtype, shape, offsets = entry['dtype'], entry['shape'], entry['data_offsets']
                if not isinstance(dtype, str) or dtype not in _DTYPES:
                    raise ValueError(f'Unsupported safetensors dtype for {name}: {dtype!r}')
                if not isinstance(shape, list) or any(type(dim) is not int or dim < 0 or dim > sys.maxsize for dim in shape):
                    raise ValueError(f'Invalid safetensors tensor shape: {name}')
                if not isinstance(offsets, list) or len(offsets) != 2 or any(type(x) is not int for x in offsets):
                    raise ValueError(f'Invalid safetensors tensor offsets: {name}')
                begin, end = offsets
                nbytes = math.prod(shape) * _DTYPES[dtype][1]
                if begin < 0 or end < begin or end > payload_size or nbytes > sys.maxsize or end - begin != nbytes:
                    raise ValueError(f'Invalid safetensors tensor byte range: {name}')
                self._specs[name] = TensorSpec(path.name, dtype, tuple(shape), 8 + header_size + begin, nbytes)
                self.weight_map[name] = path.name
                intervals.append((begin, end, name))
            cursor = 0
            for begin, end, name in sorted(intervals):
                if begin != cursor:
                    raise ValueError(f'Overlapping or non-contiguous checkpoint tensor range: {name}')
                cursor = end
            if cursor != payload_size:
                raise ValueError(f'Unindexed checkpoint payload bytes: {path.name}')
            if _signature(os.fstat(source.fileno())) != _signature(stat):
                raise RuntimeError(f'Checkpoint changed while indexing: {path.name}')
            self._signatures[path.name] = _signature(stat)

    def tensor_spec(self, name):
        try:
            return self._specs[name]
        except KeyError as error:
            raise KeyError(f'Safetensors checkpoint is missing requested tensor: {name}') from error

    def tensor_metadata(self, name):
        spec = self.tensor_spec(name)
        return getattr(self.torch, _DTYPES[spec.dtype][0]), spec.shape, spec.offset, spec.nbytes

    @property
    def block_indices(self):
        return tuple(sorted({int(match.group(1)) for name in self.weight_map if (match := _BLOCK_KEY.match(name))}))

    @contextmanager
    def _open_checked(self, shard):
        path = self.checkpoint_dir / shard
        with path.open('rb', buffering=0) as source:
            if _signature(os.fstat(source.fileno())) != self._signatures[shard]:
                raise RuntimeError(f'Checkpoint changed after indexing: {shard}')
            yield source
            if _signature(os.fstat(source.fileno())) != self._signatures[shard]:
                raise RuntimeError(f'Checkpoint changed while reading: {shard}')

    def _target_bytes(self, name, tensor):
        dtype, shape, _, _ = self.tensor_metadata(name)
        if tensor.device.type != 'cpu' or not tensor.is_contiguous() or tensor.dtype != dtype or tuple(tensor.shape) != shape:
            raise ValueError(f'Checkpoint destination must be a contiguous CPU {dtype} tensor of shape {shape}: {name}')
        # The tensor owns this storage and remains live through the complete read.
        return memoryview(tensor.reshape(-1).view(self.torch.uint8).numpy()).cast('B')

    def load_tensors_into(self, destinations):
        groups = defaultdict(list)
        for name, tensor in destinations.items():
            spec = self.tensor_spec(name)
            groups[spec.shard].append((spec.offset, name, self._target_bytes(name, tensor)))
        for shard, entries in sorted(groups.items()):
            with self._open_checked(shard) as source:
                for offset, name, target in sorted(entries, key=lambda item: item[0]):
                    source.seek(offset)
                    _read_exact_into(source, target, description=f'{shard}:{name}')

    def load_tensors(self, names, device='cpu'):
        """Load requested tensors into owned storage; GPU loads stage one at a time."""
        groups = defaultdict(list)
        for name in names:
            spec = self.tensor_spec(name)
            groups[spec.shard].append((spec.offset, name))
        result = {}
        for shard, entries in sorted(groups.items()):
            with self._open_checked(shard) as source:
                for offset, name in sorted(entries):
                    dtype, shape, _, _ = self.tensor_metadata(name)
                    tensor = self.torch.empty(shape, dtype=dtype, device='cpu')
                    source.seek(offset)
                    _read_exact_into(source, self._target_bytes(name, tensor), description=f'{shard}:{name}')
                    result[name] = tensor.to(device=device, non_blocking=False)
                    del tensor
        return result

    def read_rows(self, name, ids):
        """Read only unique vocabulary rows, then restore input order and shape."""
        dtype, shape, offset, _ = self.tensor_metadata(name)
        if len(shape) != 2:
            raise ValueError(f'Embedding checkpoint tensor must have two dimensions: {name}')
        if ids.device.type != 'cpu' or ids.dtype not in (self.torch.int32, self.torch.int64):
            raise TypeError('Embedding token IDs must be CPU int32 or int64 tensors.')
        rows, width = shape
        output_shape = tuple(ids.shape) + (width,)
        if ids.numel() == 0:
            return self.torch.empty(output_shape, dtype=dtype, device='cpu')
        unique, inverse = self.torch.unique(ids.reshape(-1), sorted=True, return_inverse=True)
        row_ids = unique.tolist()
        if row_ids[0] < 0 or row_ids[-1] >= rows:
            raise IndexError(f'Embedding token ID is outside checkpoint vocabulary [0, {rows}): {name}')
        selected = self.torch.empty((len(row_ids), width), dtype=dtype, device='cpu')
        target = memoryview(selected.reshape(-1).view(self.torch.uint8).numpy()).cast('B')
        row_bytes = width * _DTYPES[self.tensor_spec(name).dtype][1]
        begin = 0
        with self._open_checked(self.tensor_spec(name).shard) as source:
            for end in range(1, len(row_ids) + 1):
                if end == len(row_ids) or row_ids[end] != row_ids[end - 1] + 1:
                    source.seek(offset + row_ids[begin] * row_bytes)
                    _read_exact_into(source, target[begin * row_bytes:end * row_bytes], description=f'{name} rows {row_ids[begin]}:{row_ids[end - 1] + 1}')
                    begin = end
        return selected.index_select(0, inverse).reshape(output_shape)
