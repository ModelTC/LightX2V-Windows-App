import sys
from pathlib import Path
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'LightX2V.Windows/Backend'))
from prompt_enhancement import plan_enhancement
GIB = 1024**3


class EnhancementResourceTests(unittest.TestCase):
    def test_constrained_gpu_uses_partial_layers_without_changing_context(self):
        plan = plan_enhancement(9 * GIB, dict(free=7 * GIB),
                                dict(layers=32, kv_heads=8, head_dim=128))
        self.assertGreater(int(plan['gpu_layers']), 0)
        self.assertLess(int(plan['gpu_layers']), 32)
        self.assertEqual(plan['context'], 32768)

    def test_roomy_gpu_keeps_full_offload(self):
        self.assertEqual(plan_enhancement(9 * GIB, dict(free=22 * GIB),
                         dict(layers=32))['gpu_layers'], 'all')

    def test_shared_pool_is_not_added_to_system_memory(self):
        plan = plan_enhancement(9 * GIB,
            dict(free=64 * GIB, ramAvailable=5 * GIB, shared_memory=True), dict(layers=32))
        self.assertNotEqual(plan['gpu_layers'], 'all')
        self.assertEqual(plan['free_gpu_bytes'], 5 * GIB)

    def test_missing_topology_falls_back_to_cpu_when_capacity_is_unknown(self):
        self.assertEqual(plan_enhancement(9 * GIB, {}, {})['gpu_layers'], '0')


class _FakeGGUFArray:
    """The small array interface used by metadata parsing, without NumPy."""
    def __init__(self, values, dtype='uint32'):
        import types
        self.values = list(values)
        self.dtype = types.SimpleNamespace(kind='u' if dtype.startswith('u') else 'i',
                                           name=dtype)
        self.size = len(self.values)

    def __bytes__(self):
        return bytes(self.values)

    def __getitem__(self, index):
        return self.values[index]

    def reshape(self, *shape):
        if shape != (-1,):
            raise AssertionError('Unexpected metadata reshape')
        return self

    def tolist(self):
        return list(self.values)


class GGUFTopologyFieldTests(unittest.TestCase):
    """Strings and scalar numbers can both use unsigned GGUF storage."""
    def topology(self, numeric_dtype, include_numbers=True, mask=None, interval=None):
        import types
        from unittest.mock import patch
        from prompt_enhancement import _gguf_topology

        def field(values, dtype=numeric_dtype):
            return types.SimpleNamespace(parts=[_FakeGGUFArray(values, dtype)], data=[0])

        values = {'general.architecture': field(b'qwen35', 'uint8')}
        if include_numbers:
            values.update({
                'qwen35.block_count': field([32]),
                'qwen35.attention.head_count_kv': field([4]),
                'qwen35.attention.key_length': field([256]),
                'qwen35.attention.value_length': field([256]),
                'qwen35.ssm.inner_size': field([4096]),
                'qwen35.ssm.state_size': field([128]),
                'qwen35.ssm.conv_kernel': field([4]),
            })
        if mask is not None:
            # Real ReaderField ARRAY(BOOL) stores one data part per layer.
            values['qwen35.attention.recurrent_layers'] = types.SimpleNamespace(
                parts=[_FakeGGUFArray([item], 'bool') for item in mask],
                data=list(range(len(mask))))
        if interval is not None:
            values['qwen35.full_attention_interval'] = field([interval])
        fake_reader = types.SimpleNamespace(fields=values)
        fake_module = types.SimpleNamespace(GGUFReader=lambda *args, **kwargs: fake_reader)
        with patch.dict(sys.modules, {'gguf': fake_module}):
            with patch.object(fake_module, 'GGUFReader', return_value=fake_reader) as reader:
                result = _gguf_topology(Path('fixture.gguf'))
        reader.assert_called_once_with('fixture.gguf', mode='r')
        return result

    def test_uint8_architecture_is_text_and_resolves_numeric_field_names(self):
        actual = self.topology('uint32')
        self.assertEqual(actual['architecture'], 'qwen35')
        self.assertEqual({key: actual[key] for key in ('layers', 'kv_heads', 'head_dim')},
                         dict(layers=32, kv_heads=4, head_dim=256))
        self.assertTrue(all(type(actual[key]) is int for key in ('layers', 'kv_heads', 'head_dim')))
        self.assertEqual(actual['full_attention_layers'], 32)
        self.assertEqual(actual['cache_layout_source'], 'conservative_all_layers')

    def test_unsigned_and_signed_numeric_arrays_are_read_as_scalars(self):
        for dtype in ('uint16', 'uint32', 'uint64', 'int32'):
            with self.subTest(dtype=dtype):
                actual = self.topology(dtype)
                self.assertEqual([actual[key] for key in ('layers', 'kv_heads', 'head_dim')],
                                 [32, 4, 256])

    def test_missing_numeric_fields_keep_documented_defaults(self):
        actual = self.topology('uint32', include_numbers=False)
        self.assertEqual([actual[key] for key in ('layers', 'kv_heads', 'head_dim')], [0, 8, 128])
        self.assertIsNone(actual['recurrent_mask'])

    def test_actual_qwen35_recurrent_mask_counts_eight_full_attention_layers(self):
        mask = [True, True, True, False] * 8
        actual = self.topology('uint32', mask=mask, interval=4)
        self.assertEqual(actual['recurrent_mask'], mask)
        self.assertEqual(actual['full_attention_layers'], 8)
        self.assertEqual(actual['recurrent_layers'], 24)
        self.assertEqual(actual['cache_layout_source'], 'recurrent_mask')

    def test_mask_takes_precedence_over_interval_and_invalid_mask_falls_back(self):
        mask = [True] * 30 + [False] * 2
        self.assertEqual(self.topology('uint32', mask=mask, interval=4)['full_attention_layers'], 2)
        fallback = self.topology('uint32', mask=mask[:-1], interval=4)
        self.assertIsNone(fallback['recurrent_mask'])
        self.assertEqual(fallback['full_attention_layers'], 8)
        self.assertEqual(fallback['cache_layout_source'], 'full_attention_interval')

    def test_interval_without_mask_counts_full_attention_layers(self):
        actual = self.topology('uint32', interval=4)
        self.assertEqual(actual['full_attention_layers'], 8)
        self.assertEqual(actual['recurrent_layers'], 24)


class HybridCacheBudgetTests(unittest.TestCase):
    def topology(self):
        return dict(layers=32, kv_heads=4, key_dim=256, value_dim=256,
                    full_attention_interval=4,
                    recurrent_mask=[True, True, True, False] * 8,
                    ssm_inner_size=4096, ssm_state_size=128, ssm_conv_kernel=4)

    def test_actual_32k_hybrid_kv_is_one_gib_and_recurrent_state_is_separate(self):
        plan = plan_enhancement(9527501536, dict(free=14 * GIB), self.topology())
        self.assertEqual(plan['estimated_kv_bytes'], GIB)
        self.assertEqual(plan['full_attention_layers'], 8)
        self.assertEqual(plan['recurrent_layers'], 24)
        self.assertEqual(plan['estimated_recurrent_state_bytes'], 24 * 4096 * 128 * 4)
        self.assertGreater(plan['estimated_recurrent_conv_bytes'], 0)
        self.assertEqual(plan['estimated_recurrent_bytes'],
                         plan['estimated_recurrent_state_bytes'] + plan['estimated_recurrent_conv_bytes'])

    def test_sixteen_gib_card_with_fourteen_free_can_keep_actual_pe_and_projector_on_gpu(self):
        plan = plan_enhancement(9527501536, dict(free=14 * GIB), self.topology(),
                                auxiliary_gpu_bytes=921704608 + GIB)
        self.assertEqual(plan['gpu_layers'], 'all')
        self.assertEqual(plan['context'], 32768)
        self.assertEqual(plan['auxiliary_gpu_bytes'], 921704608 + GIB)

    def test_eight_gib_card_still_uses_partial_layers_and_keeps_context(self):
        plan = plan_enhancement(9527501536, dict(free=7 * GIB), self.topology(),
                                auxiliary_gpu_bytes=921704608 + GIB)
        self.assertGreater(int(plan['gpu_layers']), 0)
        self.assertLess(int(plan['gpu_layers']), 32)
        self.assertEqual(plan['context'], 32768)

    def test_unknown_layout_keeps_conservative_all_layer_kv(self):
        plan = plan_enhancement(9527501536, dict(free=14 * GIB),
                                dict(layers=32, kv_heads=4, head_dim=256))
        self.assertEqual(plan['estimated_kv_bytes'], 4 * GIB)
        self.assertEqual(plan['recurrent_layers'], 0)

    def test_interval_fallback_rounds_up_when_layer_count_is_not_divisible(self):
        topology = self.topology()
        topology.pop('recurrent_mask')
        topology['layers'] = 30
        plan = plan_enhancement(9527501536, dict(free=14 * GIB), topology)
        self.assertEqual(plan['full_attention_layers'], 8)
        self.assertEqual(plan['recurrent_layers'], 22)


class EnhancementVisionBudgetTests(unittest.TestCase):
    def test_projector_and_image_workspace_reduce_language_gpu_layers(self):
        runtime = {'free': 7 * GIB}
        topology = {'layers': 32, 'kv_heads': 4, 'head_dim': 256}
        text = plan_enhancement(9 * GIB, runtime, topology)
        image = plan_enhancement(9 * GIB, runtime, topology, auxiliary_gpu_bytes=3 * GIB)
        self.assertLess(int(image['gpu_layers']), int(text['gpu_layers']))
        self.assertEqual(image['context'], text['context'])
        self.assertEqual(image['auxiliary_gpu_bytes'], 3 * GIB)
