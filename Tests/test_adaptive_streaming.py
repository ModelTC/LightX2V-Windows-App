"""Numerical and bounded-lifetime checks; optional real native GPU operator tests."""
from pathlib import Path
import os
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'LightX2V.Windows' / 'Backend'))
import adaptive_streaming as streaming
try:
    import torch
except ImportError:
    torch = None


class MemoryCheckpoint:
    def __init__(self, tensors):
        self.tensors = tensors
        self.reads = []
        self.fail_on = None

    def tensor_metadata(self, name):
        value = self.tensors[name]
        return value.dtype, tuple(value.shape), 0, value.numel() * value.element_size()

    def load_tensors_into(self, destinations):
        self.reads.append(tuple(destinations))
        if self.fail_on and any(self.fail_on in name for name in destinations):
            raise OSError('simulated incomplete checkpoint read')
        for name, target in destinations.items():
            target.copy_(self.tensors[name])

    def load_tensors(self, names, device='cpu'):
        self.reads.append(tuple(names))
        return {name: self.tensors[name].to(device) for name in names}


class MatrixLeaf:
    def __init__(self):
        self.base_attrs = [('layers.0.weight', 'weight', True)]
        self.weight = None


class MatrixBlock:
    def __init__(self):
        self.linear = MatrixLeaf()
        self._modules = {'linear': self.linear}

    def load(self, weights):
        self.linear.weight = weights['layers.0.weight'].t()

    def forward(self, value):
        return value @ self.linear.weight


@unittest.skipIf(torch is None, 'PyTorch is not installed; tensor and storage tests require it')
class StreamingTests(unittest.TestCase):
    def make_stream(self, checkpoint=None, device='cpu'):
        checkpoint = checkpoint or MemoryCheckpoint({
            'layers.0.weight': torch.tensor([[1., 2.], [3., 4.]], dtype=torch.bfloat16),
            'layers.1.weight': torch.tensor([[2., 0.], [1., 2.]], dtype=torch.bfloat16),
            'layers.2.weight': torch.tensor([[1., 1.], [0., 1.]], dtype=torch.bfloat16),
        })
        return streaming.CheckpointBlockStream(checkpoint, MatrixBlock, 3, device), checkpoint

    def test_layers_match_resident_execution_using_one_block(self):
        blocks, checkpoint = self.make_stream()
        value = torch.tensor([[1., 2.]], dtype=torch.bfloat16)
        expected = value.clone()
        for index in range(3):
            expected = expected @ checkpoint.tensors[f'layers.{index}.weight'].t()
        identities = []
        for block in blocks:
            identities.append(id(block))
            self.assertEqual(len(blocks._cpu), 1)
            self.assertEqual(len(blocks._gpu), 1)
            value = block.forward(value)
        torch.testing.assert_close(value, expected, rtol=0, atol=0)
        self.assertEqual(len(set(identities)), 1)
        self.assertIsNone(blocks._block)
        self.assertEqual(blocks._cpu, {})
        self.assertEqual(blocks._gpu, {})
        self.assertEqual(len(checkpoint.reads), 3)
        self.assertIsNone(block.linear.weight)  # The last yielded object cannot retain weights.

    def test_partial_iteration_close_releases_and_can_restart(self):
        blocks, _ = self.make_stream()
        iterator = iter(blocks)
        held = next(iterator)
        iterator.close()
        self.assertIsNone(held.linear.weight)
        self.assertFalse(blocks._active)
        self.assertEqual(len(list(blocks)), 3)
        self.assertIsNone(blocks._block)

    def test_checkpoint_read_failure_releases_staging_and_device(self):
        blocks, checkpoint = self.make_stream()
        checkpoint.fail_on = 'layers.1.'
        with self.assertRaisesRegex(OSError, 'incomplete'):
            list(blocks)
        self.assertFalse(blocks._active)
        self.assertIsNone(blocks._block)
        self.assertEqual(blocks._gpu, {})
        self.assertEqual(blocks._cpu, {})
        checkpoint.fail_on = None
        self.assertEqual(len(list(blocks)), 3)

    def test_invalid_later_layer_rejected_before_allocation(self):
        checkpoint = MemoryCheckpoint({
            'layers.0.weight': torch.zeros((2, 2), dtype=torch.bfloat16),
            'layers.1.weight': torch.zeros((3, 2), dtype=torch.bfloat16),
            'layers.2.weight': torch.zeros((2, 2), dtype=torch.bfloat16),
        })
        with self.assertRaisesRegex(ValueError, 'Inconsistent'):
            self.make_stream(checkpoint)
        self.assertEqual(checkpoint.reads, [])

    def test_operator_copy_or_dtype_conversion_is_rejected(self):
        class CopyingBlock(MatrixBlock):
            def load(self, weights):
                self.linear.weight = weights['layers.0.weight'].t().clone()
        _, checkpoint = self.make_stream()
        blocks = streaming.CheckpointBlockStream(checkpoint, CopyingBlock, 3, 'cpu')
        with self.assertRaisesRegex(ValueError, 'retain checkpoint buffer'):
            list(blocks)
        self.assertEqual(blocks._gpu, {})
        self.assertEqual(blocks._cpu, {})
        self.assertIsNone(blocks._block)

    def test_reentrant_iterator_is_rejected(self):
        blocks, _ = self.make_stream()
        iterator = iter(blocks)
        next(iterator)
        with self.assertRaisesRegex(RuntimeError, 'Concurrent'):
            next(iter(blocks))
        iterator.close()

    def test_embedding_reads_only_vocabulary_and_preserves_selected_rows(self):
        weights = torch.arange(40, dtype=torch.bfloat16).reshape(10, 4)
        checkpoint = MemoryCheckpoint({'vocabulary': weights, 'unrelated': torch.zeros(3)})
        embedding = streaming.CheckpointEmbedding(checkpoint, 'vocabulary', 'cpu', torch.bfloat16)
        ids = torch.tensor([7, 2, 7])
        torch.testing.assert_close(embedding.apply(ids), weights[ids], rtol=0, atol=0)
        self.assertEqual(checkpoint.reads, [('vocabulary',)])
        self.assertFalse(hasattr(embedding, 'weight'))

    def test_vision_weights_are_released_on_forward_failure(self):
        instances = []
        class FailingVision(MatrixBlock):
            def __init__(self):
                super().__init__()
                instances.append(self)
            def forward(self, pixels, grid):
                raise RuntimeError('vision failure')
        checkpoint = MemoryCheckpoint({'layers.0.weight': torch.ones((2, 2), dtype=torch.bfloat16)})
        vision = streaming.CheckpointVision(checkpoint, FailingVision, 'cpu')
        with self.assertRaisesRegex(RuntimeError, 'vision failure'):
            vision.forward(None, None)
        self.assertIsNone(instances[0].linear.weight)

    def test_vision_layernorm_preserves_upstream_sensitive_dtype(self):
        class Norm:
            base_attrs = [('vision.norm.weight', 'weight', False)]
            sensitive_layer_dtype = torch.float32
            infer_dtype = torch.bfloat16
        class Vision:
            def __init__(self):
                self.norm = Norm()
                self._modules = {'norm': self.norm}
            def load(self, weights):
                self.norm.weight = weights['vision.norm.weight']
        checkpoint = MemoryCheckpoint({'vision.norm.weight': torch.ones(4, dtype=torch.bfloat16)})
        vision = Vision()
        streaming._load_static_weights(vision, checkpoint, 'cpu', Norm, torch)
        self.assertEqual(vision.norm.weight.dtype, torch.float32)

    def test_absent_or_false_plan_leaves_runtime_untouched(self):
        self.assertFalse(streaming.install({}))
        self.assertFalse(streaming.install({'_app_resource_plan': {'dit_streaming': False, 'text_encoder_streaming': False}}))
        self.assertFalse(streaming.install({'_app_resource_plan': {'dit_streaming': 'false'}}))

    def test_explicit_quantized_streaming_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'original BF16'):
            streaming.install({'dit_quantized': True, '_app_resource_plan': {'dit_streaming': True}})


@unittest.skipIf(torch is None, 'PyTorch is not installed; numerical lifecycle tests require it')
class OneShotLifecycleTests(unittest.TestCase):
    def make_runner(self, *, flag=True, warmup=False):
        from types import SimpleNamespace
        class Encoder:
            def to_cpu(self, *args, **kwargs):
                raise AssertionError('One-shot cleanup must not copy the encoder to CPU.')
        leaf = SimpleNamespace(weight=torch.tensor([[1., 2.], [3., 4.]], requires_grad=True))
        leaf.pin_weight = leaf.weight
        leaf.weight_diff = torch.tensor(0.)
        encoder = Encoder()
        encoder._modules = {'linear': leaf}
        runner = SimpleNamespace(config={'_app_one_shot_worker': flag, 'warmup': warmup},
                                 text_encoders=[encoder], model=object(), vae=object())
        cache_calls = []
        runtime = SimpleNamespace(inference_mode=torch.inference_mode,
                                  cuda=SimpleNamespace(empty_cache=lambda: cache_calls.append('empty_cache')))
        return runner, leaf, runtime, cache_calls

    def test_success_releases_te_before_vae_and_keeps_all_condition_outputs(self):
        import weakref
        runner, leaf, runtime, cache_calls = self.make_runner()
        weight_ref = weakref.ref(leaf.weight)
        model, vae = runner.model, runner.vae
        original_list = runner.text_encoders
        returned = {}
        layout, negative_layout = object(), object()
        reference_features = torch.tensor([[1., 2.]], requires_grad=True)
        def original(self, images, shapes):
            # Represents vision/text conditioning, including both CFG branches
            # and layouts, all completed before the original method returns.
            weight = self.text_encoders[0]._modules['linear'].weight
            returned['cond'] = {'prompt_embeds': images @ weight.t(), 'layout': layout}
            returned['uncond'] = {'prompt_embeds': torch.tensor([[0., 1.]]) @ weight.t(), 'layout': negative_layout}
            return returned
        wrapped = streaming._wrap_one_shot_text_encoder(original, runtime)
        result = wrapped(runner, reference_features, [(1, 1, 1)])
        self.assertIs(result, returned)
        torch.testing.assert_close(result['cond']['prompt_embeds'], torch.tensor([[5., 11.]]), rtol=0, atol=0)
        torch.testing.assert_close(result['uncond']['prompt_embeds'], torch.tensor([[2., 4.]]), rtol=0, atol=0)
        self.assertIs(result['cond']['layout'], layout)
        self.assertIs(result['uncond']['layout'], negative_layout)
        self.assertFalse(result['cond']['prompt_embeds'].requires_grad)
        self.assertFalse(result['uncond']['prompt_embeds'].requires_grad)
        self.assertIsNone(weight_ref())  # No graph or CPU pinned alias retains it.
        self.assertIsNone(leaf.weight)
        self.assertIsNone(leaf.pin_weight)
        self.assertIsNone(leaf.weight_diff)
        self.assertEqual(runner.text_encoders, [])
        self.assertEqual(original_list, [])
        self.assertEqual(cache_calls, ['empty_cache'])
        self.assertIs(runner.model, model)
        self.assertIs(runner.vae, vae)
        # The next native run_input_encoder action is run_vae_encoder(images).
        # Verify that this phase sees the encoder already gone while original
        # reference features, completed layouts and embeddings stay usable.
        with torch.inference_mode():
            self.assertEqual(runner.text_encoders, [])
            result['image_latents'] = reference_features.square().sum(dim=-1, keepdim=True)
            downstream = result['cond']['prompt_embeds'].sum() + result['image_latents'].sum()
        torch.testing.assert_close(result['image_latents'], torch.tensor([[5.]]), rtol=0, atol=0)
        self.assertEqual(downstream.item(), 21.)
        with self.assertRaisesRegex(RuntimeError, 'already completed'):
            wrapped(runner, reference_features, [(1, 1, 1)])
        self.assertEqual(cache_calls, ['empty_cache'])

    def test_stream_staging_and_encoder_cycles_are_released_before_cache(self):
        import weakref
        runner, leaf, runtime, cache_calls = self.make_runner()
        class TrackedStream(streaming.CheckpointBlockStream):
            def __init__(self):
                self.releases = 0
            def release(self):
                self.releases += 1
        blocks = TrackedStream()
        encoder = runner.text_encoders[0]
        encoder._modules['layers'] = blocks
        encoder.cycle = lambda own=encoder: own
        encoder_ref = weakref.ref(encoder)
        del encoder
        cache_checks = []
        runtime.cuda.empty_cache = lambda: cache_checks.append(encoder_ref() is None)
        wrapped = streaming._wrap_one_shot_text_encoder(lambda self: {'prompt_embeds': torch.tensor([1.])}, runtime)
        result = wrapped(runner)
        self.assertEqual(blocks.releases, 1)
        self.assertEqual(cache_checks, [True])
        self.assertEqual(result['prompt_embeds'].item(), 1.)

    def test_unflagged_upstream_reusable_runner_is_untouched(self):
        runner, leaf, runtime, cache_calls = self.make_runner(flag=False)
        original_list = runner.text_encoders
        def original(self):
            return torch.tensor([[1., 2.]], requires_grad=True) @ leaf.weight.t()
        wrapped = streaming._wrap_one_shot_text_encoder(original, runtime)
        self.assertTrue(wrapped(runner).requires_grad)
        self.assertTrue(wrapped(runner).requires_grad)  # Ordinary repeated requests remain valid.
        self.assertIs(runner.text_encoders, original_list)
        self.assertIsNotNone(leaf.weight)
        self.assertEqual(cache_calls, [])

    def test_no_flag_is_noop_and_warmup_profile_is_rejected(self):
        self.assertFalse(streaming.install_request_lifecycle({}))
        self.assertFalse(streaming.install_request_lifecycle({'_app_one_shot_worker': False, 'warmup': False}))
        with self.assertRaisesRegex(ValueError, 'warmup=false'):
            streaming.install_request_lifecycle({'_app_one_shot_worker': True, 'warmup': True})
        runner, leaf, runtime, cache_calls = self.make_runner(warmup=True)
        wrapped = streaming._wrap_one_shot_text_encoder(lambda self: self.fail('must not encode'), runtime)
        with self.assertRaisesRegex(ValueError, 'warmup=false'):
            wrapped(runner)
        self.assertIsNotNone(leaf.weight)
        self.assertEqual(cache_calls, [])

    def test_encoding_failure_does_not_claim_success_or_discard_weights(self):
        runner, leaf, runtime, cache_calls = self.make_runner()
        original_list = runner.text_encoders
        def original(self):
            raise RuntimeError('encoding failed')
        with self.assertRaisesRegex(RuntimeError, 'encoding failed'):
            streaming._wrap_one_shot_text_encoder(original, runtime)(runner)
        self.assertIs(runner.text_encoders, original_list)
        self.assertIsNotNone(leaf.weight)
        self.assertFalse(getattr(runner, '_app_text_encoding_completed', False))
        self.assertEqual(cache_calls, [])


@unittest.skipUnless(torch is not None and os.environ.get('LIGHTX2V_TEST_GPU') == '1' and os.environ.get('LIGHTX2V_TEST_MODEL'),
                     'requires PyTorch, native GPU opt-in and LIGHTX2V_TEST_MODEL with the official BF16 checkpoint')
class NativeGpuStreamingTests(unittest.TestCase):
    def test_official_qwen_language_layers_match_resident(self):
        """Use two actual official layers and actual dimensions, with seven tokens."""
        import json
        from lightx2v.common.offload.safetensors_checkpoint import SafetensorsCheckpoint
        from lightx2v.models.input_encoders.hf.qwen_image_21.qwen3vl import Qwen3VLTextLayer
        self.assertTrue(torch.cuda.is_available())
        model = Path(os.environ['LIGHTX2V_TEST_MODEL'])
        path = model / 'text_encoder'
        cfg = json.loads((path / 'config.json').read_text(encoding='utf-8'))['text_config']
        checkpoint = SafetensorsCheckpoint(path)
        runtime = {'text_encoder_quantized': False}
        generator = torch.Generator().manual_seed(42)
        x = torch.randn((7, cfg['hidden_size']), generator=generator).to('cuda', dtype=torch.bfloat16)
        cos = torch.ones((7, cfg['head_dim']), dtype=torch.bfloat16).to('cuda')
        sin = torch.zeros((7, cfg['head_dim']), dtype=torch.bfloat16).to('cuda')
        with torch.inference_mode():
            resident = x.clone()
            for index in range(2):
                layer = Qwen3VLTextLayer(index, cfg, runtime)
                layer.load(checkpoint.load_tensors(streaming._weight_names(layer), device='cuda'))
                resident = layer.forward(resident, cos, sin)
                torch.cuda.synchronize()
                del layer
            blocks = streaming.CheckpointBlockStream(checkpoint, lambda: Qwen3VLTextLayer(0, cfg, runtime), 2)
            streamed = x.clone()
            for block in blocks:
                streamed = block.forward(streamed, cos, sin)
            torch.cuda.synchronize()
        torch.testing.assert_close(streamed, resident, rtol=0, atol=0)
        self.assertEqual(blocks._cpu, {})
        self.assertEqual(blocks._gpu, {})
        self.assertIsNone(blocks._block)
        print('Official GPU BF16 Qwen3VL: two checkpoint layers exactly match resident layers; staging released.')

    def test_official_qwen_dit_layers_match_resident(self):
        """Exercise real DiT matrix transposes, RMSNorm, attention and FFN weights."""
        import json
        from lightx2v.common.offload.safetensors_checkpoint import SafetensorsCheckpoint
        from lightx2v.models.networks.qwen_image_21.weights.transformer_weights import QwenImage21BlockWeights
        from lightx2v.models.networks.qwen_image_21.infer.transformer_infer import QwenImage21TransformerInfer
        model = Path(os.environ['LIGHTX2V_TEST_MODEL'])
        path = model / 'transformer'
        cfg = json.loads((path / 'config.json').read_text(encoding='utf-8'))
        cfg.update(dit_quantized=False, tensor_parallel=False, seq_parallel=False,
                   attn_type='torch_sdpa', rope_type='torch_real_rope', layer_norm_type='torch',
                   modulate_type='torch', rms_norm_type='fp32_variance', fused_qk_rms_norm=False,
                   fused_block_ops=False, use_compile=False)
        checkpoint = SafetensorsCheckpoint(path)
        infer = QwenImage21TransformerInfer(cfg)
        dim = cfg['num_attention_heads'] * cfg['attention_head_dim']
        generator = torch.Generator().manual_seed(42)
        x = torch.randn((7, dim), generator=generator).to('cuda', dtype=torch.bfloat16)
        modulation = tuple(torch.ones((1, dim), dtype=torch.bfloat16).to('cuda') for _ in range(4))
        rotary = torch.cat((torch.ones((7, cfg['attention_head_dim'] // 2)),
                            torch.zeros((7, cfg['attention_head_dim'] // 2))), -1).to('cuda')
        cached = torch.empty((0, cfg['num_attention_heads'], cfg['attention_head_dim']), dtype=torch.bfloat16, device='cuda')
        with torch.inference_mode():
            resident = x.clone()
            for index in range(2):
                block = QwenImage21BlockWeights(index, cfg)
                block.load(checkpoint.load_tensors(streaming._weight_names(block), device='cuda'))
                resident = infer.infer_block(block, resident, modulation, rotary, None, cached, cached)
                torch.cuda.synchronize()
                del block
            blocks = streaming.CheckpointBlockStream(checkpoint, lambda: QwenImage21BlockWeights(0, cfg), 2)
            streamed = x.clone()
            for block in blocks:
                streamed = infer.infer_block(block, streamed, modulation, rotary, None, cached, cached)
            torch.cuda.synchronize()
        torch.testing.assert_close(streamed, resident, rtol=0, atol=0)
        self.assertIsNone(blocks._block)
        print('Official GPU BF16 Qwen DiT: two checkpoint layers exactly match resident layers; staging released.')


if __name__ == '__main__':
    unittest.main()
