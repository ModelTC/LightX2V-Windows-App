"""Resource planning from checkpoint headers and current, shared or discrete budgets.

Workspace estimates are conservative policy inputs, not measured hardware minima.
No tensor is loaded and neither precision nor requested image size is changed here.
"""
import json
import math
from pathlib import Path
import re
import struct

GIB = 1024 ** 3
DEFAULT_POLICY = {
    'host_reserve_fraction': .08, 'host_reserve_min_bytes': .75 * GIB,
    'host_reserve_max_bytes': 3 * GIB,
    'gpu_reserve_fraction': .08, 'gpu_reserve_min_bytes': .5 * GIB,
    'gpu_reserve_max_bytes': 2 * GIB,
    'critical_host_fraction': .02, 'critical_host_min_bytes': .125 * GIB,
    'critical_host_max_bytes': .75 * GIB,
    'host_weight_overhead_fraction': .12, 'host_runtime_bytes': .5 * GIB,
    'source_mapping_overlap_fraction': 1.0,
    'block_transfer_overlap': 1.25,
    'dit_workspace_base_bytes': .5 * GIB, 'dit_workspace_per_megapixel_bytes': GIB,
    'text_workspace_bytes': .5 * GIB, 'edit_workspace_factor': 1.35,
    # A historical untiled 2K Windows run requested a single ~32 GiB buffer.
    # Allow overlap of two such convolution/work buffers, then calibrate from telemetry.
    'vae_workspace_base_bytes': .25 * GIB, 'vae_workspace_per_megapixel_bytes': 8 * GIB,
    'vae_workspace_overlap': 2, 'vae_output_per_megapixel_bytes': .125 * GIB,
    'vae_max_tile_size': 1024, 'vae_min_tile_size': 256,
    'default_prompt_tokens': 256, 'prompt_template_token_margin': 64,
    'default_reference_count': 3, 'reference_grid_rounding_factor': 1.1,
    'default_transformer_layers': 32, 'default_hidden_size': 4096,
    'default_vae_scale_factor': 16, 'text_activation_multiplier': 16,
    'prefix_activation_multiplier': 8, 'prefix_mask_bytes_per_token_pair': 1,
    'max_header_bytes': 64 * 1024 * 1024,
}
_EXCLUDED_TEXT = frozenset(('lm_head.weight', 'model.lm_head.weight',
    'model.language_model.norm.weight', 'language_model.norm.weight', 'model.norm.weight'))
_DTYPE_BITS = {'BOOL': 8, 'U8': 8, 'I8': 8, 'I16': 16, 'U16': 16,
    'I32': 32, 'U32': 32, 'I64': 64, 'U64': 64, 'F16': 16, 'BF16': 16,
    'F32': 32, 'F64': 64, 'F8_E4M3': 8, 'F8_E5M2': 8, 'F8_E4M3FN': 8}
_BLOCK = re.compile(r'^(?:transformer_blocks|model\.language_model\.layers|language_model\.layers|model\.layers)\.(\d+)\.')


class ModelMetadataError(ValueError):
    pass


def _policy(overrides=None):
    result = dict(DEFAULT_POLICY)
    if overrides:
        unknown = set(overrides) - result.keys()
        if unknown:
            raise ValueError('Unknown resource policy keys: ' + ', '.join(sorted(unknown)))
        result.update(overrides)
    for key, value in result.items():
        if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value) or value < 0:
            raise ValueError('Invalid nonnegative resource policy value: ' + key)
    for prefix in ('host_reserve', 'gpu_reserve', 'critical_host'):
        if result[prefix + '_min_bytes'] > result[prefix + '_max_bytes']:
            raise ValueError(prefix + ' minimum exceeds maximum')
        if result[prefix + '_fraction'] > 1:
            raise ValueError(prefix + ' fraction exceeds one')
    if result['block_transfer_overlap'] < 1:
        raise ValueError('block_transfer_overlap must be at least one')
    return result


def _reserve(total, policy, prefix):
    return int(min(policy[prefix + '_max_bytes'], max(policy[prefix + '_min_bytes'],
                                                     total * policy[prefix + '_fraction'])))


def memory_pressure(available_bytes, total_bytes, policy=None):
    """Sample classification only; the caller requires sustained pressure to stop.

    The critical floor is deliberately smaller than the planner's normal reserve.
    A brief loading spike must not recreate the old fixed 8 GiB termination rule.
    """
    p = _policy(policy)
    if total_bytes <= 0 or available_bytes < 0:
        raise ValueError('Invalid physical memory sample')
    critical = _reserve(total_bytes, p, 'critical_host')
    return {'severe': available_bytes < critical, 'reserve_bytes': critical,
            'critical_available_bytes': critical, 'available_bytes': int(available_bytes),
            'total_bytes': int(total_bytes),
            'reason': 'Physical memory is critically low' if available_bytes < critical else ''}


def read_safetensors_header(path, max_header_bytes=64 * 1024 * 1024):
    """Read and validate metadata without mapping or loading tensor payloads."""
    path = Path(path)
    try:
        size = path.stat().st_size
        with path.open('rb') as stream:
            prefix = stream.read(8)
            if len(prefix) != 8:
                raise ValueError('missing 8-byte header length')
            length = struct.unpack('<Q', prefix)[0]
            if not 2 <= length <= max_header_bytes or 8 + length > size:
                raise ValueError('invalid or truncated header length')
            header = json.loads(stream.read(length))
        if not isinstance(header, dict):
            raise ValueError('header must be an object')
        result, intervals = {}, []
        payload = size - 8 - length
        for name, tensor in header.items():
            if name == '__metadata__':
                continue
            if not isinstance(tensor, dict):
                raise ValueError('invalid tensor metadata for ' + name)
            shape, offsets, dtype = tensor.get('shape'), tensor.get('data_offsets'), tensor.get('dtype')
            if dtype not in _DTYPE_BITS or not isinstance(shape, list) or any(type(n) is not int or n < 0 for n in shape):
                raise ValueError('invalid dtype or shape for ' + name)
            if not isinstance(offsets, list) or len(offsets) != 2 or any(type(n) is not int for n in offsets):
                raise ValueError('invalid data offsets for ' + name)
            start, end = offsets
            expected = (math.prod(shape) * _DTYPE_BITS[dtype] + 7) // 8
            if not 0 <= start <= end <= payload or end - start != expected:
                raise ValueError('truncated payload or inconsistent tensor size for ' + name)
            result[name] = {'bytes': end - start, 'dtype': dtype, 'shape': shape}
            if end > start:
                intervals.append((start, end))
        intervals.sort()
        if any(current[0] < previous[1] for previous, current in zip(intervals, intervals[1:])):
            raise ValueError('overlapping tensor payloads')
        if not result:
            raise ValueError('checkpoint contains no tensors')
        return result
    except (OSError, ValueError, TypeError, OverflowError, struct.error) as exc:
        raise ModelMetadataError(f'Cannot read model metadata from {path}: {exc}') from exc


def model_inventory(model_path, policy=None):
    p = _policy(policy)
    root = Path(model_path)
    result = {}
    for component in ('transformer', 'text_encoder', 'vae'):
        folder = root / component
        shards = sorted(folder.glob('*.safetensors'))
        if not shards:
            raise ModelMetadataError(f'Model component has no safetensors checkpoint: {folder}')
        tensors, files = {}, []
        for shard in shards:
            header = read_safetensors_header(shard, p['max_header_bytes'])
            duplicate = tensors.keys() & header.keys()
            if duplicate:
                raise ModelMetadataError(f'Duplicate tensor in {folder}: {sorted(duplicate)[0]}')
            tensors.update(header)
            files.append({'name': shard.name, 'size_bytes': shard.stat().st_size})
        for index_path in sorted(folder.glob('*.index.json')):
            try:
                mapping = json.loads(index_path.read_text(encoding='utf-8-sig'))['weight_map']
                if not isinstance(mapping, dict):
                    raise ValueError('weight_map must be an object')
                for name, relative in mapping.items():
                    if not isinstance(relative, str) or Path(relative).name != relative or name not in tensors or not (folder / relative).is_file():
                        raise ValueError('missing or invalid shard for ' + name)
            except (OSError, ValueError, KeyError, TypeError) as exc:
                raise ModelMetadataError(f'Invalid model shard index {index_path}: {exc}') from exc
        excluded = {name: item for name, item in tensors.items()
                    if component == 'text_encoder' and name in _EXCLUDED_TEXT}
        included = {name: item for name, item in tensors.items() if name not in excluded}
        if not included:
            raise ModelMetadataError('No usable weights in ' + str(folder))
        blocks = {}
        for name, item in included.items():
            match = _BLOCK.match(name)
            if match:
                blocks[match[1]] = blocks.get(match[1], 0) + item['bytes']
        total = sum(item['bytes'] for item in included.values())
        largest_tensor = max(item['bytes'] for item in included.values())
        config_path = folder / 'config.json'
        try:
            architecture = json.loads(config_path.read_text(encoding='utf-8-sig')) if config_path.exists() else {}
            if not isinstance(architecture, dict):
                raise ValueError('config must be an object')
        except (OSError, ValueError) as exc:
            raise ModelMetadataError(f'Invalid component configuration {config_path}: {exc}') from exc
        text_arch = architecture.get('text_config', {})
        if not isinstance(text_arch, dict):
            raise ModelMetadataError(f'Invalid text_config in {config_path}')
        result[component] = {
            'layer_count': architecture.get('num_layers', text_arch.get('num_hidden_layers', len(blocks))),
            'hidden_size': (architecture.get('num_attention_heads', 0) * architecture.get('attention_head_dim', 0)
                            or text_arch.get('hidden_size', 0)),
            'spatial_scale': architecture.get('scale_factor_spatial', 0),
            'bytes': total, 'file_bytes': sum(f['size_bytes'] for f in files),
            'largest_shard_bytes': min(total, max(f['size_bytes'] for f in files)),
            'excluded_bytes': sum(item['bytes'] for item in excluded.values()),
            'excluded_tensors': sorted(excluded), 'tensor_count': len(included),
            'largest_tensor_bytes': largest_tensor,
            'largest_block_bytes': max(blocks.values(), default=largest_tensor),
            'static_bytes': total - sum(blocks.values()),
            'vision_bytes': sum(item['bytes'] for name, item in included.items() if name.startswith('model.visual.')),
            'embedding_bytes': sum(item['bytes'] for name, item in included.items() if name.endswith('embed_tokens.weight')),
            'files': files,
        }
    return result


def _runtime_number(runtime, primary, *aliases):
    for key in (primary, *aliases):
        value = runtime.get(key)
        if value is not None:
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
                raise ValueError('Invalid runtime resource: ' + key)
            return int(value)
    raise ValueError('Missing runtime resource: ' + primary)


def _prompt_tokens(model_path, prompt, reference_count, policy):
    """CPU tokenizer count, with a UTF-8 byte upper bound if unavailable."""
    margin = int(policy['prompt_template_token_margin'])
    if prompt is None:
        return int(policy['default_prompt_tokens']) + margin, 'policy_default', ''
    if not isinstance(prompt, str):
        raise ValueError('Resource planner prompt must be a string')
    image_prefix = ' '.join(f'<image{i+1}><|vision_start|><|image_pad|><|vision_end|>' for i in range(reference_count))
    text = ('<|im_start|>system\nComprehend and analyze the provided prompt.<|im_end|>\n'
            '<|im_start|>user\n' + image_prefix + prompt + '<|im_end|>\n<|im_start|>assistant\n')
    tokenizer_path = Path(model_path) / 'processor' / 'tokenizer.json'
    try:
        from tokenizers import Tokenizer
        tokenizer = Tokenizer.from_file(str(tokenizer_path))
        if hasattr(tokenizer, 'no_truncation'):
            tokenizer.no_truncation()
        if hasattr(tokenizer, 'no_padding'):
            tokenizer.no_padding()
        count = len(tokenizer.encode(text, add_special_tokens=False).ids)
        return count + margin, 'tokenizer_json', ''
    except Exception as exc:
        # Byte-level BPE cannot produce more tokens than UTF-8 input bytes;
        # extra margin covers control/template tokens and tokenizer differences.
        return len(text.encode('utf-8')) + margin, 'utf8_byte_upper_bound', str(exc)


def _positive_arch(component, key, fallback):
    value = component.get(key) or fallback
    if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
        raise ModelMetadataError('Invalid architecture resource dimension: ' + key)
    return int(value)


def plan_resources(model_path, runtime, width, height, task='t2i', policy=None, *,
                   prompt=None, reference_count=None, reference_resolution=512):
    """Return a JSON plan from the current resource sample and actual request.

    All adapters keep the VAE and DiT pre/post weights on the GPU. The complete
    per-layer condition KV cache also remains resident throughout denoising.
    """
    p = _policy(policy)
    if task not in ('t2i', 'i2i') or type(width) is not int or type(height) is not int or width <= 0 or height <= 0:
        raise ValueError('Invalid task or image dimensions for resource planning')
    if reference_count is None:
        reference_count = int(p['default_reference_count']) if task == 'i2i' else 0
    if type(reference_count) is not int or reference_count < 0 or (task == 't2i' and reference_count):
        raise ValueError('Invalid resource planner reference count')
    if task == 'i2i' and reference_count < 1:
        raise ValueError('Image editing resource planning needs a reference image')
    if type(reference_resolution) is not int or reference_resolution <= 0:
        raise ValueError('Invalid resource planner reference resolution')
    gpu_free = _runtime_number(runtime, 'free', 'gpuFree')
    gpu_total = _runtime_number(runtime, 'total', 'gpuTotal')
    host_free = _runtime_number(runtime, 'ramAvailable', 'ram_available', 'host_available_bytes')
    host_total = _runtime_number(runtime, 'ramTotal', 'ram_total', 'host_total_bytes')
    if not gpu_total or not host_total or gpu_free > gpu_total or host_free > host_total:
        raise ValueError('Runtime resource totals and available memory are inconsistent')
    shared = runtime.get('shared_memory', runtime.get('shared_memory_pool', False))
    if type(shared) is not bool:
        raise ValueError('shared_memory must be a boolean from device detection')
    inventory = model_inventory(model_path, p)
    dit, text, vae = (inventory[name] for name in ('transformer', 'text_encoder', 'vae'))
    layers = _positive_arch(dit, 'layer_count', p['default_transformer_layers'])
    hidden = _positive_arch(dit, 'hidden_size', p['default_hidden_size'])
    text_hidden = _positive_arch(text, 'hidden_size', p['default_hidden_size'])
    vae_scale = _positive_arch(vae, 'spatial_scale', p['default_vae_scale_factor'])
    prompt_tokens, token_source, tokenizer_note = _prompt_tokens(model_path, prompt, reference_count, p)
    reference_tokens = math.ceil(reference_count * reference_resolution ** 2 / vae_scale ** 2 * p['reference_grid_rounding_factor'])
    prefix_tokens = prompt_tokens + reference_tokens
    # BF16 K and V for every DiT layer. This cache is not block-streamed.
    condition_kv = int(2 * layers * hidden * prefix_tokens * 2)
    text_sequence_tokens = prompt_tokens + math.ceil(reference_tokens / 4)
    host_reserve = _reserve(host_total, p, 'host_reserve')
    gpu_reserve = _reserve(gpu_total, p, 'gpu_reserve')
    host_budget = max(0, host_free - host_reserve)
    gpu_budget = max(0, gpu_free - gpu_reserve)
    shared_budget = max(0, min(host_free, gpu_free) - max(host_reserve, gpu_reserve)) if shared else None
    if shared:
        host_budget = gpu_budget = shared_budget
    scale = width * height / (1024 * 1024)
    edit_factor = p['edit_workspace_factor'] if task == 'i2i' else 1
    prefix_work = (prefix_tokens * hidden * 2 * p['prefix_activation_multiplier'] +
                   prefix_tokens ** 2 * p['prefix_mask_bytes_per_token_pair'])
    dit_work = int((p['dit_workspace_base_bytes'] + p['dit_workspace_per_megapixel_bytes'] * scale) * edit_factor + prefix_work)
    text_work = int(p['text_workspace_bytes'] * edit_factor + text_sequence_tokens * text_hidden * 2 * p['text_activation_multiplier'])
    largest_load = max(c['largest_tensor_bytes'] for c in inventory.values())
    staging = int(p['host_runtime_bytes'] + largest_load)
    cpu_vae = int(vae['bytes'] * (1 + p['host_weight_overhead_fraction']))
    cpu_dit = int(dit['bytes'] * (1 + p['host_weight_overhead_fraction']))
    cpu_text = int(text['bytes'] * (1 + p['host_weight_overhead_fraction']))
    all_host = cpu_dit + cpu_text + cpu_vae + staging
    full_weights = sum(c['bytes'] for c in inventory.values())
    overlap = p['block_transfer_overlap']

    def source_overlap(strategy, dit_stream, text_stream):
        if strategy == 'full_resident':
            return 0
        # A copied CPU backing may coexist with touched safetensors source pages.
        # Budget the largest retained source shard during loading, not permanently.
        sources = ([dit.get('largest_shard_bytes', dit['bytes'])] if not dit_stream else [])
        if not text_stream:
            sources.append(text.get('largest_shard_bytes', text['bytes']))
        return int(max(sources, default=0) * p['source_mapping_overlap_fraction'])

    def vae_workspace(tile=None):
        processed_scale = scale if tile is None else min(scale, tile * tile / (1024 * 1024))
        return int(p['vae_workspace_base_bytes'] + p['vae_workspace_per_megapixel_bytes'] * p['vae_workspace_overlap'] * processed_scale +
                   p['vae_output_per_megapixel_bytes'] * scale)

    def estimate(strategy, dit_stream, text_stream, vae_work):
        if strategy == 'full_resident':
            host_peak = staging
            # The app worker serves one request. After successful input encoding,
            # its TE CPU/GPU references are dropped before denoising and decode.
            gpu_peak = max(full_weights + text_work,
                           dit['bytes'] + vae['bytes'] + dit_work + condition_kv,
                           dit['bytes'] + vae['bytes'] + vae_work)
        elif strategy == 'component_offload':
            host_peak = all_host + source_overlap(strategy, dit_stream, text_stream)
            gpu_peak = vae['bytes'] + max(dit['bytes'] + dit_work + condition_kv,
                                         text['bytes'] + text_work, vae_work)
        else:
            host_peak = cpu_vae + staging + (0 if dit_stream else cpu_dit) + (0 if text_stream else cpu_text) + source_overlap(strategy, dit_stream, text_stream)
            # Upstream CPU block offload retains two accelerator block buffers;
            # the checkpoint adapter owns one staging block and one GPU block.
            buffers = 0 if dit_stream else dit['largest_block_bytes'] * 2
            persistent = vae['bytes'] + dit['static_bytes'] + buffers
            dit_active = dit['largest_block_bytes'] * overlap if dit_stream else 0
            text_active = (text['largest_block_bytes'] * overlap + (text['vision_bytes'] if task == 'i2i' else 0)) if text_stream else text['bytes']
            gpu_peak = persistent + max(dit_active + dit_work + condition_kv,
                                        text_active + text_work, vae_work)
        shared_peak = host_peak + gpu_peak if shared else None
        fits = shared_peak <= shared_budget if shared else host_peak <= host_budget and gpu_peak <= gpu_budget
        return int(host_peak), int(gpu_peak), int(shared_peak) if shared else None, bool(fits)

    # Prefer fast residency. Within each storage tier, keep the largest VAE
    # operations that fit; dimensions alone never force a tiled or streamed path.
    tile_options = [None]
    candidate = int(p['vae_max_tile_size'])
    minimum = max(vae_scale, int(p['vae_min_tile_size']))
    while candidate >= minimum:
        if candidate < max(width, height) and candidate % vae_scale == 0:
            tile_options.append(candidate)
        candidate //= 2
    candidates = [('full_resident', False, False), ('component_offload', False, False),
                  ('block_offload', False, False), ('block_offload', False, True),
                  ('ssd_streaming', True, True)]
    chosen = None
    for strategy, dit_stream, text_stream in candidates:
        for tile in tile_options:
            vae_work = vae_workspace(tile)
            host_peak, gpu_peak, shared_peak, fits = estimate(strategy, dit_stream, text_stream, vae_work)
            chosen = (strategy, dit_stream, text_stream, tile, vae_work, host_peak, gpu_peak, shared_peak, fits)
            if fits:
                break
        if chosen[-1]:
            break
    strategy, dit_stream, text_stream, tile, vae_work, host_peak, gpu_peak, shared_peak, feasible = chosen
    cpu_offload = strategy != 'full_resident'
    granularity = 'block' if strategy in ('block_offload', 'ssd_streaming') else 'model'
    pin = bool(cpu_offload and not shared and not dit_stream and not text_stream and host_peak + largest_load <= host_budget * .7)
    reasons = ['Shared accelerator memory uses one physical pool, bounded by the smaller available view.' if shared else
               'Separate host and accelerator budgets use their current available memory.']
    reasons.append({'full_resident': 'All effective weights and estimated workspaces fit on the accelerator.',
                    'component_offload': 'Sequential component transfers fit; full simultaneous accelerator residency does not.',
                    'block_offload': 'Block offload avoids retaining the full DiT on the accelerator.',
                    'ssd_streaming': 'Full CPU weight backing exceeds current budgets; DiT and text layers will stream from SSD.'}[strategy])
    reasons.append('Single-request worker releases text-encoder CPU/GPU references after successful input encoding.')
    if text_stream:
        reasons.append('Text encoder uses layer streaming and row-wise embeddings; no complete encoder onload.')
    if tile is not None:
        reasons.append('Estimated untiled VAE workspace does not fit this storage tier; use the largest budgeted tile.')
    if token_source != 'tokenizer_json':
        reasons.append('Prompt token estimate uses ' + token_source + '; the budget is conservative.')
    if not feasible:
        reasons.append('Even streamed execution exceeds the estimated available budget; release memory before retrying.')
    config_changes = {'cpu_offload': cpu_offload, 'offload_granularity': granularity,
                      'text_encoder_cpu_offload': cpu_offload,
                      'use_tiling_vae': tile is not None,
                      'vae_tile_size': tile if tile is not None else int(p['vae_max_tile_size'])}
    return {
        'schema_version': 1, 'strategy': strategy, 'feasible': feasible,
        'backend': runtime.get('backend', ''), 'shared_memory': shared,
        'width': width, 'height': height, 'task': task,
        'single_request_text_release': True,
        'reference_count': reference_count, 'reference_resolution': reference_resolution,
        'dit_streaming': dit_stream, 'text_encoder_streaming': text_stream,
        'dit_storage': 'ssd' if dit_stream else 'cpu' if cpu_offload else 'resident',
        'text_encoder_storage': 'ssd' if text_stream else 'cpu' if cpu_offload else 'resident',
        'pin_host_weights': pin, 'config_changes': config_changes,
        'gpu_budget_bytes': gpu_budget, 'host_budget_bytes': host_budget,
        'shared_budget_bytes': shared_budget, 'stream_cache_bytes': 0,
        'runtime': dict(runtime), 'policy': p, 'model': inventory, 'reasons': reasons,
        'estimates': {'host_reserve_bytes': host_reserve, 'gpu_reserve_bytes': gpu_reserve,
            'effective_weight_bytes': full_weights, 'host_peak_bytes': host_peak,
            'gpu_peak_bytes': gpu_peak, 'shared_peak_bytes': shared_peak,
            'dit_workspace_bytes': dit_work, 'text_workspace_bytes': text_work,
            'untiled_vae_workspace_bytes': vae_workspace(), 'vae_workspace_bytes': vae_work,
            'largest_load_bytes': largest_load, 'condition_kv_bytes': condition_kv,
            'source_mapping_overlap_bytes': source_overlap(strategy, dit_stream, text_stream),
            'prefix_tokens': prefix_tokens, 'prompt_tokens': prompt_tokens,
            'reference_tokens': reference_tokens, 'token_estimator': token_source,
            'tokenizer_note': tokenizer_note,
            'persistent_vae_bytes': vae['bytes'], 'persistent_dit_static_bytes': dit['static_bytes'],
            'streamed_gpu_bytes': estimate('ssd_streaming', True, True, vae_work)[1]},
    }


def apply_resource_plan(config, plan):
    result = dict(config)
    result.update(plan['config_changes'])
    result['_app_one_shot_worker'] = bool(plan.get('single_request_text_release', False))
    # The Windows streaming adapter reads only these two booleans from this plan.
    # Do not enable upstream MPS-only *_disk_streaming flags.
    result['_app_resource_plan'] = plan
    return result
