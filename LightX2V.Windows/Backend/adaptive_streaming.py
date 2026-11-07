"""Bounded BF16 Qwen-Image-2.1 checkpoint streaming for automatic resource plans.

Installed in the isolated inference worker, leaving the pinned upstream archive
unchanged. No weights are quantized. CUDA and ROCm both use torch's cuda device.
The ordinary LightX2V path is retained unless the plan requests streaming.
"""
import re

from checkpoint_reader import BoundedSafetensorsCheckpoint

_INSTALLED = False
_LIFECYCLE_INSTALLED = False


def _enabled(config, field):
    plan = config.get('_app_resource_plan', {})
    return isinstance(plan, dict) and plan.get(field) is True


def _weight_names(module):
    """Collect checkpoint leaves without traversing tensor storage or loading it."""
    names = set()
    attrs = getattr(module, 'base_attrs', None)
    if attrs is not None:
        names.update(name for name, _, _ in attrs if name is not None)
    else:
        for attr in ('weight_name', 'bias_name'):
            name = getattr(module, attr, None)
            if name:
                names.add(name)
    for child in (*getattr(module, '_modules', {}).values(),
                  *getattr(module, '_parameters', {}).values()):
        if child is not None:
            names.update(_weight_names(child))
    return names


def _weight_bindings(module):
    for name, attr, transpose in getattr(module, 'base_attrs', ()):
        if name is not None:
            yield module, name, attr, transpose
    for child in (*getattr(module, '_modules', {}).values(),
                  *getattr(module, '_parameters', {}).values()):
        if child is not None:
            yield from _weight_bindings(child)


def _assert_storage_bindings(block, sources):
    """A loader must retain buffer views, never silently clone/convert weights."""
    for operator, name, attr, transpose in _weight_bindings(block):
        actual = getattr(operator, attr, None)
        source = sources[name]
        if attr == 'weight':
            transpose = getattr(operator, 'weight_need_transpose', transpose)
        expected = source.t() if transpose else source
        if (actual is None or actual.dtype != expected.dtype or actual.device != expected.device
                or tuple(actual.shape) != tuple(expected.shape)
                or tuple(actual.stride()) != tuple(expected.stride())
                or actual.data_ptr() != expected.data_ptr()):
            raise ValueError(f'Streaming operator must retain checkpoint buffer storage/layout: {name}')


def _index_name(name, index):
    return re.sub(r'\.\d+(?=\.)', lambda match: '.' + str(index), name, count=1)


def _synchronize(torch, device):
    if str(device).split(':')[0] == 'cuda':
        torch.cuda.synchronize(device)


def _drop_weights(module):
    """Drop GPU views even when a caller still holds the yielded block object."""
    for attr in ('weight', 'weight_scale', 'bias', 'weight_cuda_buffer',
                 'bias_cuda_buffer', 'pin_weight', 'pin_weight_scale', 'pin_bias',
                 'weight_diff', 'bias_diff', 'lora_down', 'lora_up', 'lora_alpha'):
        if hasattr(module, attr):
            setattr(module, attr, None)
    for child in (*getattr(module, '_modules', {}).values(),
                  *getattr(module, '_parameters', {}).values()):
        if child is not None:
            _drop_weights(child)


class CheckpointBlockStream:
    """Reuse one CPU staging block and one GPU block; never retain all layers.

    A block is valid only until iteration advances. Synchronous transfers and
    synchronization before reuse make ownership explicit on CUDA and ROCm.
    CPU execution is supported for bounded-storage and numerical unit tests.
    """
    def __init__(self, checkpoint, block_factory, num_layers, device='cuda',
                 infer_dtype=None, torch_module=None):
        if torch_module is None:
            import torch as torch_module
        self.torch = torch_module
        self.checkpoint = checkpoint
        self.block_factory = block_factory
        self.num_layers = num_layers
        self.device = str(device)
        self.infer_dtype = infer_dtype or self.torch.bfloat16
        self._block = None
        self._cpu = {}
        self._gpu = {}
        self._active = False
        self._specs = {}
        if type(num_layers) is not int or num_layers < 1:
            raise ValueError('Checkpoint streaming requires at least one layer.')
        template = block_factory()
        names = sorted(_weight_names(template))
        if not names:
            raise ValueError('The streaming block has no checkpoint weights.')
        for name in names:
            dtype, shape, _, nbytes = checkpoint.tensor_metadata(name)
            if dtype != self.infer_dtype:
                raise ValueError(f'Streaming requires matching BF16 checkpoint/inference weights: {name}')
            for index in range(num_layers):
                layer_dtype, layer_shape, _, _ = checkpoint.tensor_metadata(_index_name(name, index))
                if layer_dtype != dtype or tuple(layer_shape) != tuple(shape):
                    raise ValueError(f'Inconsistent streaming layer shape/dtype: {_index_name(name, index)}')
            self._specs[name] = (dtype, tuple(shape), nbytes)
        self.block_bytes = sum(spec[2] for spec in self._specs.values())
        del template

    @property
    def blocks(self):
        return self

    def __len__(self):
        return self.num_layers

    def _allocate(self):
        if self._block is not None:
            return
        try:
            self._block = self.block_factory()
            for name, (dtype, shape, _) in self._specs.items():
                # Only this bounded staging block is committed to CPU memory.
                self._cpu[name] = self.torch.empty(shape, dtype=dtype, device='cpu')
                self._gpu[name] = self.torch.empty(shape, dtype=dtype, device=self.device)
            self._block.load(self._gpu)
            _assert_storage_bindings(self._block, self._gpu)
        except BaseException:
            self.release()
            raise

    def __iter__(self):
        if self._active:
            raise RuntimeError('Concurrent iteration over one streaming block is not supported.')
        self._active = True
        try:
            self._allocate()
            for index in range(self.num_layers):
                _synchronize(self.torch, self.device)
                destinations = {_index_name(name, index): tensor for name, tensor in self._cpu.items()}
                self.checkpoint.load_tensors_into(destinations)
                for name, tensor in self._cpu.items():
                    self._gpu[name].copy_(tensor, non_blocking=False)
                yield self._block
        finally:
            self._active = False
            self.release()

    def release(self):
        try:
            if self._block is not None:
                _synchronize(self.torch, self.device)
        finally:
            if self._block is not None:
                _drop_weights(self._block)
            self._block = None
            self._cpu.clear()
            self._gpu.clear()

    def to_cpu(self, non_blocking=False):
        self.release()
        return self

    def to_cuda(self, non_blocking=False):
        return self


class CheckpointEmbedding:
    """CPU vocabulary lookup; only requested embedding rows cross to the GPU."""
    def __init__(self, checkpoint, weight_name, device, dtype, torch_module=None):
        if torch_module is None:
            import torch as torch_module
        self.torch = torch_module
        self.checkpoint = checkpoint
        self.weight_name = weight_name
        self.device = device
        self.dtype = dtype

    def apply(self, ids):
        # Read only requested rows into owned CPU storage. A file offset can
        # never become a borrowed data pointer or require whole-file mapping.
        value = self.checkpoint.read_rows(self.weight_name, ids.cpu())
        return value.to(device=self.device, dtype=self.dtype)


def _load_static_weights(module, checkpoint, device, norm_type=(), torch_module=None):
    """Match the upstream vision LayerNorm dtype conversion, including FP32."""
    if torch_module is None:
        import torch as torch_module
    weights = checkpoint.load_tensors(_weight_names(module), device=device)
    def convert(root):
        if isinstance(root, norm_type):
            dtype = torch_module.float32 if root.sensitive_layer_dtype != root.infer_dtype else root.infer_dtype
            for name, _, _ in root.base_attrs:
                if name is not None:
                    weights[name] = weights[name].to(dtype)
        for child in (*getattr(root, '_modules', {}).values(),
                      *getattr(root, '_parameters', {}).values()):
            if child is not None:
                convert(child)
    convert(module)
    module.load(weights)


class CheckpointVision:
    """Load the vision tower only for an I2I request, then release its weights."""
    def __init__(self, checkpoint, factory, device, torch_module=None, norm_type=()):
        if torch_module is None:
            import torch as torch_module
        self.torch = torch_module
        self.checkpoint = checkpoint
        self.factory = factory
        self.device = device
        self.norm_type = norm_type

    def forward(self, pixels, grid):
        vision = self.factory()
        try:
            _load_static_weights(vision, self.checkpoint, self.device, self.norm_type, self.torch)
            return vision.forward(pixels, grid)
        finally:
            _synchronize(self.torch, self.device)
            _drop_weights(vision)
            del vision


def _validate_streaming(config):
    for key in ('tensor_parallel', 'text_encoder_tensor_parallel', 'seq_parallel',
                'shared_cpu_weights', 'lora_configs', 'lora_dynamic_apply', 'use_compile',
                'dit_disk_streaming', 'text_encoder_disk_streaming'):
        if config.get(key):
            raise ValueError(f'The automatic BF16 streaming profile does not support {key}.')
    if _enabled(config, 'dit_streaming') and config.get('dit_quantized'):
        raise ValueError('Automatic DiT checkpoint streaming requires original BF16 weights.')
    if _enabled(config, 'text_encoder_streaming') and config.get('text_encoder_quantized'):
        raise ValueError('Automatic text-encoder checkpoint streaming requires original BF16 weights.')


def install(config):
    """Install worker-local adapters selected by config['_app_resource_plan'].

    Only the two explicit boolean streaming fields enable adapters. An absent
    plan is a no-op, including no upstream imports or GPU initialization.
    """
    global _INSTALLED
    if not (_enabled(config, 'dit_streaming') or _enabled(config, 'text_encoder_streaming')):
        return False
    _validate_streaming(config)
    if _INSTALLED:
        return True

    import torch
    from loguru import logger
    from lightx2v.common.ops.norm.layer_norm_weight import LNWeightTemplate
    from lightx2v.models.input_encoders.hf.qwen_image_21.qwen3vl import (
        Qwen3VLTextLayer, Qwen3VLVision, QwenImage21TextEncoder,
    )
    from lightx2v.models.networks.qwen_image_21.model import QwenImage21TransformerModel
    from lightx2v.models.networks.qwen_image_21.weights.transformer_weights import QwenImage21BlockWeights
    from lightx2v.models.networks.qwen_image_21.infer.transformer_infer import QwenImage21TransformerInfer
    from lightx2v.models.runners.qwen_image_21.qwen_image_21_runner import QwenImage21Runner
    from lightx2v.utils.envs import GET_DTYPE
    from lightx2v_platform.base.global_var import AI_DEVICE
    if AI_DEVICE != 'cuda' or GET_DTYPE() != torch.bfloat16:
        raise ValueError('The automatic streaming adapter requires the CUDA/ROCm BF16 runtime.')

    class StreamingTextEncoder(QwenImage21TextEncoder):
        def _load_weights(self, path, runtime_config):
            _validate_streaming(runtime_config)
            checkpoint = BoundedSafetensorsCheckpoint(path)
            self.add_module('embedding', CheckpointEmbedding(checkpoint, self.embedding.weight_name, AI_DEVICE, GET_DTYPE(), torch))
            self.add_module('layers', CheckpointBlockStream(
                checkpoint, lambda: Qwen3VLTextLayer(0, self.text_config, runtime_config),
                self.text_config['num_hidden_layers'], AI_DEVICE, GET_DTYPE(), torch))
            self.add_module('vision', CheckpointVision(
                checkpoint, lambda: Qwen3VLVision(self.model_config['vision_config']), AI_DEVICE, torch, LNWeightTemplate))
            logger.info('[AdaptiveStreaming] Text encoder: checkpoint layers, CPU vocabulary lookup, on-demand vision; {:.3f} GiB per language block', self.layers.block_bytes / 1024**3)

        def to_cuda(self, non_blocking=False):
            return self

        def to_cpu(self, non_blocking=False):
            self.layers.release()
            return self

    class StreamingTransformerInfer(QwenImage21TransformerInfer):
        def get_compile_block_key(self, block_idx, block):
            return id(block)

    original_text_encoder = QwenImage21Runner.load_text_encoder
    original_weights = QwenImage21TransformerModel._init_weights
    original_infer_class = QwenImage21TransformerModel._init_infer_class
    original_init_run = QwenImage21Runner.init_run
    original_offload = QwenImage21Runner._offload_transformer
    original_end_run = QwenImage21Runner.end_run

    def load_text_encoder(self):
        if _enabled(self.config, 'text_encoder_streaming'):
            return [StreamingTextEncoder(self.config)]
        return original_text_encoder(self)

    def init_weights(self, weight_dict=None):
        if not _enabled(self.config, 'dit_streaming'):
            return original_weights(self, weight_dict)
        _validate_streaming(self.config)
        checkpoint = BoundedSafetensorsCheckpoint(self.model_path)
        if checkpoint.block_indices != tuple(range(self.config['num_layers'])):
            raise ValueError('The DiT checkpoint has unexpected block indices.')
        self.transformer_weights = CheckpointBlockStream(
            checkpoint, lambda: QwenImage21BlockWeights(0, self.config),
            self.config['num_layers'], AI_DEVICE, GET_DTYPE(), torch)
        self.pre_weight = self.pre_weight_class(self.config)
        self.post_weight = self.post_weight_class(self.config)
        names = _weight_names(self.pre_weight) | _weight_names(self.post_weight)
        weights = checkpoint.load_tensors(names, device=AI_DEVICE)
        self.pre_weight.load(weights)
        self.post_weight.load(weights)
        logger.info('[AdaptiveStreaming] DiT: one CPU staging block and one GPU block; {:.3f} GiB per block', self.transformer_weights.block_bytes / 1024**3)

    def init_infer_class(self):
        original_infer_class(self)
        if _enabled(self.config, 'dit_streaming'):
            self.transformer_infer_class = StreamingTransformerInfer

    def init_run(self):
        if not _enabled(self.config, 'dit_streaming'):
            return original_init_run(self)
        self.get_video_segment_num()
        self.scheduler.prepare(self.input_info)
        logger.info('[AdaptiveStreaming] Starting checkpoint-backed DiT execution')
        torch.cuda.synchronize()

    def offload_transformer(self):
        if not _enabled(self.config, 'dit_streaming'):
            return original_offload(self)
        try:
            self.model.transformer_weights.release()
        finally:
            self.model.clear_condition_kv()
            self.maybe_empty_cache(force=True, collect_garbage=True)

    def end_run(self):
        try:
            return original_end_run(self)
        finally:
            if _enabled(self.config, 'dit_streaming'):
                self.model.transformer_weights.release()
            if _enabled(self.config, 'text_encoder_streaming'):
                for encoder in getattr(self, 'text_encoders', ()):
                    encoder.to_cpu()

    QwenImage21Runner.load_text_encoder = load_text_encoder
    QwenImage21TransformerModel._init_weights = init_weights
    QwenImage21TransformerModel._init_infer_class = init_infer_class
    QwenImage21Runner.init_run = init_run
    QwenImage21Runner._offload_transformer = offload_transformer
    QwenImage21Runner.end_run = end_run
    _INSTALLED = True
    logger.info('[AdaptiveStreaming] Worker-local BF16 adapters installed; owned row/block reads without mmap')
    return True


def _discard_text_encoder_weights(encoders):
    """Drop weights without copying any GPU data back to the host."""
    def release_streams(module):
        if isinstance(module, CheckpointBlockStream):
            module.release()
            return
        for child in (*getattr(module, '_modules', {}).values(),
                      *getattr(module, '_parameters', {}).values()):
            if child is not None:
                release_streams(child)
    for encoder in encoders:
        release_streams(encoder)
        _drop_weights(encoder)
    # Clear an original mutable list too, so other list references cannot retain
    # the now-unused model. The caller has already installed a fresh empty list.
    if hasattr(encoders, 'clear'):
        encoders.clear()


def _wrap_one_shot_text_encoder(original, torch_module):
    """Wrap only explicitly flagged single-request, non-warmup App runners."""
    from functools import wraps
    @wraps(original)
    def run_text_encoder(self, *args, **kwargs):
        if self.config.get('_app_one_shot_worker') is not True:
            return original(self, *args, **kwargs)
        if self.config.get('warmup') is not False:
            raise ValueError('The App one-shot text-encoder lifecycle requires warmup=false.')
        if getattr(self, '_app_text_encoding_completed', False):
            raise RuntimeError('The App one-shot text encoder has already completed.')
        # All condition branches and layouts are now complete. Their embeddings are
        # independently computed tensors; release TE before later VAE encoding.
        # Inference mode prevents an autograd graph retaining model weights.
        with torch_module.inference_mode():
            outputs = original(self, *args, **kwargs)
        encoders = getattr(self, 'text_encoders', ())
        self.text_encoders = []
        self._app_text_encoding_completed = True
        _discard_text_encoder_weights(encoders)
        del encoders
        # Streaming factories can form small Python cycles referencing their
        # encoder. Collect them before the one phase-boundary cache release.
        import gc
        gc.collect()
        torch_module.cuda.empty_cache()
        return outputs
    return run_text_encoder


def install_request_lifecycle(config):
    """Opt this dedicated App worker into release-after-text-encoding ownership.

    Never enabled for ordinary upstream runners or warmup/reusable workers.
    DiT and VAE ownership are untouched, and full encoder.to_cpu() is not used.
    """
    global _LIFECYCLE_INSTALLED
    if config.get('_app_one_shot_worker') is not True:
        return False
    if config.get('warmup') is not False:
        raise ValueError('The App one-shot text-encoder lifecycle requires warmup=false.')
    if _LIFECYCLE_INSTALLED:
        return True
    import torch
    from lightx2v.models.runners.qwen_image_21.qwen_image_21_runner import QwenImage21Runner
    QwenImage21Runner.run_text_encoder = _wrap_one_shot_text_encoder(QwenImage21Runner.run_text_encoder, torch)
    _LIFECYCLE_INSTALLED = True
    return True
