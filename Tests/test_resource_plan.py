import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('resource_plan', ROOT / 'LightX2V.Windows/Backend/resource_plan.py')
planner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(planner)
G = planner.GIB


def inventory():
    def component(size, block, static, largest, vision=0):
        return dict(bytes=int(size*G), largest_block_bytes=int(block*G), static_bytes=int(static*G),
                    largest_tensor_bytes=int(largest*G), vision_bytes=int(vision*G), embedding_bytes=0)
    return dict(transformer=component(13.25,.42,.18,.24),
                text_encoder=component(15.25,.35,1.1,1.1,.6),
                vae=component(.63,.05,.3,.04))


def runtime(gpu=8, free=7, ram=32, available=18, shared=False, backend='cuda'):
    return dict(backend=backend,free=int(free*G),total=int(gpu*G),ramTotal=int(ram*G),
                ramAvailable=int(available*G),shared_memory=shared)


def checkpoint(path, tensors):
    path.parent.mkdir(parents=True, exist_ok=True)
    offset, header = 0, {}
    for name, count in tensors.items():
        header[name] = dict(dtype='BF16', shape=[count], data_offsets=[offset,offset+count*2])
        offset += count*2
    encoded=json.dumps(header).encode('utf-8')
    path.write_bytes(struct.pack('<Q',len(encoded))+encoded+b'\0'*offset)


class HeaderTests(unittest.TestCase):
    def test_counts_effective_weights_and_excludes_only_final_text_norm(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            checkpoint(root/'transformer/a.safetensors', {'transformer_blocks.0.attn.weight':10, 'img_in.weight':3})
            checkpoint(root/'text_encoder/a.safetensors', {'lm_head.weight':20,
                'model.language_model.norm.weight':2, 'model.language_model.layers.0.input_layernorm.weight':3,
                'model.language_model.layers.0.self_attn.q_proj.weight':10,'model.visual.merger.norm.weight':4})
            checkpoint(root/'vae/a.safetensors', {'decoder.weight':5})
            actual=planner.model_inventory(root)
            self.assertEqual(actual['transformer']['bytes'],26)
            self.assertEqual(actual['text_encoder']['bytes'],34)
            self.assertEqual(actual['text_encoder']['excluded_bytes'],44)
            self.assertEqual(actual['text_encoder']['vision_bytes'],8)
            self.assertEqual(actual['text_encoder']['largest_block_bytes'],26)
            self.assertEqual(actual['transformer']['static_bytes'],6)
    def test_missing_component_has_actionable_path(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaisesRegex(planner.ModelMetadataError,'transformer'):
                planner.model_inventory(temp)
    def test_corrupt_header_and_truncated_payload_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'bad.safetensors'
            for content in (b'broken',struct.pack('<Q',100)+b'{}'):
                path.write_bytes(content)
                with self.assertRaisesRegex(planner.ModelMetadataError,'bad.safetensors'):
                    planner.read_safetensors_header(path)
            checkpoint(path,{'weight':10})
            path.write_bytes(path.read_bytes()[:-1])
            with self.assertRaisesRegex(planner.ModelMetadataError,'truncated payload'):
                planner.read_safetensors_header(path)
    def test_missing_index_shard_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            checkpoint(root/'transformer/a.safetensors',{'weight':10})
            (root/'transformer/model.index.json').write_text(json.dumps({'weight_map':{'weight':'missing.safetensors'}}))
            with self.assertRaisesRegex(planner.ModelMetadataError,'shard'):
                planner.model_inventory(root)


class ResourcePlanTests(unittest.TestCase):
    def plan(self, resource, width=512,height=512,task='t2i',policy=None,**request):
        with patch.object(planner,'model_inventory',return_value=inventory()):
            return planner.plan_resources('unused',resource,width,height,task,policy,**request)
    def test_8gb_32gb_uses_ssd_for_both_and_retains_bf16_size(self):
        plan=self.plan(runtime())
        self.assertEqual(plan['strategy'],'ssd_streaming')
        self.assertTrue(plan['dit_streaming'])
        self.assertTrue(plan['text_encoder_streaming'])
        self.assertFalse(plan['pin_host_weights'])
        self.assertTrue(plan['feasible'])
        result=planner.apply_resource_plan({'infer_steps':8,'attn_type':'torch_sdpa'},plan)
        self.assertEqual(result['infer_steps'],8)
        self.assertEqual((plan['width'],plan['height']),(512,512))
        self.assertEqual(result['_app_resource_plan'],plan)
        self.assertNotIn('dit_disk_streaming',result)
        self.assertNotIn('text_encoder_disk_streaming',result)
        json.dumps(plan)
    def test_almost_empty_32gb_host_still_budgets_cpu_load_source_overlap(self):
        result=self.plan(runtime(available=29))
        self.assertTrue(result['dit_streaming'])
        self.assertTrue(result['text_encoder_streaming'])
        self.assertEqual(result['estimates']['source_mapping_overlap_bytes'],0)
    def test_32gb_accelerator_does_not_keep_unused_text_weights_in_later_phases(self):
        # Encoding barely fits 32 GiB, while denoise/decode free the ~15 GiB TE.
        # The old all-weights-plus-VAE-workspace formula unnecessarily offloaded.
        result=self.plan(runtime(gpu=32,free=31.9,ram=32,available=18),1024,1024)
        self.assertEqual(result['strategy'],'full_resident')
        self.assertTrue(result['feasible'])
        self.assertTrue(result['single_request_text_release'])
        e=result['estimates'];weights=inventory()
        expected=max(e['effective_weight_bytes']+e['text_workspace_bytes'],
                     weights['transformer']['bytes']+weights['vae']['bytes']+e['dit_workspace_bytes']+e['condition_kv_bytes'],
                     weights['transformer']['bytes']+weights['vae']['bytes']+e['vae_workspace_bytes'])
        self.assertEqual(e['gpu_peak_bytes'],expected)
        self.assertLessEqual(e['gpu_peak_bytes'],result['gpu_budget_bytes'])
        applied=planner.apply_resource_plan({},result)
        self.assertTrue(applied['_app_one_shot_worker'])
        self.assertFalse(applied['cpu_offload'])
        self.assertFalse(applied['text_encoder_cpu_offload'])
    def test_large_discrete_gpu_keeps_full_resident_fast_path(self):
        plan=self.plan(runtime(gpu=80,free=75,ram=64,available=48),1024,1024)
        self.assertEqual(plan['strategy'],'full_resident')
        self.assertFalse(plan['config_changes']['cpu_offload'])
        self.assertFalse(plan['dit_streaming'])
        self.assertFalse(plan['text_encoder_streaming'])
    def test_component_and_cpu_block_choices_follow_actual_budgets(self):
        medium=self.plan(runtime(gpu=24,free=22,ram=96,available=72),1024,1024)
        self.assertEqual(medium['strategy'],'component_offload')
        smaller=self.plan(runtime(gpu=8,free=7,ram=64,available=48))
        self.assertEqual(smaller['strategy'],'block_offload')
        self.assertFalse(smaller['dit_streaming'])
        self.assertTrue(smaller['text_encoder_streaming'])
    def test_shared_memory_is_minimum_of_views_never_sum(self):
        abundant=self.plan(runtime(gpu=90,free=85,ram=96,available=75,shared=True,backend='amd_rocm'))
        self.assertEqual(abundant['strategy'],'full_resident')
        pressure=self.plan(runtime(gpu=90,free=85,ram=96,available=20,shared=True,backend='amd_rocm'))
        self.assertTrue(pressure['dit_streaming'])
        self.assertTrue(pressure['text_encoder_streaming'])
        self.assertLess(pressure['gpu_budget_bytes'],20*G)
        self.assertEqual(pressure['host_budget_bytes'],pressure['gpu_budget_bytes'])
        self.assertFalse(pressure['pin_host_weights'])
    def test_backend_name_does_not_imply_shared_memory(self):
        amd=self.plan(runtime(gpu=24,free=22,ram=64,available=48,backend='amd_rocm'))
        cuda=self.plan(runtime(gpu=24,free=22,ram=64,available=48))
        self.assertEqual(amd['strategy'],cuda['strategy'])
        self.assertFalse(amd['shared_memory'])
    def test_dynamic_gpu_and_host_samples_change_plan(self):
        high=self.plan(runtime(gpu=48,free=45,ram=64,available=48))
        low=self.plan(runtime(gpu=48,free=7,ram=64,available=18))
        self.assertEqual(high['strategy'],'full_resident')
        self.assertEqual(low['strategy'],'ssd_streaming')
        self.assertNotEqual(high['gpu_budget_bytes'],low['gpu_budget_bytes'])
    def test_larger_and_edit_requests_have_larger_workspace_estimates(self):
        small=self.plan(runtime())
        large=self.plan(runtime(),2048,2048,'i2i')
        self.assertGreater(large['estimates']['dit_workspace_bytes'],small['estimates']['dit_workspace_bytes'])
        self.assertGreater(large['estimates']['text_workspace_bytes'],small['estimates']['text_workspace_bytes'])
        self.assertTrue(large['config_changes']['use_tiling_vae'])
    def test_impossible_budget_explained_without_silent_downscale(self):
        plan=self.plan(runtime(free=.25,available=.5),2048,2048)
        self.assertFalse(plan['feasible'])
        self.assertIn('exceeds',plan['reasons'][-1])
        self.assertEqual(plan['width'],2048)
    def test_critical_pressure_is_dynamic_and_not_the_planning_reserve(self):
        sample=planner.memory_pressure(2*G,32*G)
        self.assertFalse(sample['severe'])
        self.assertLess(sample['reserve_bytes'],G)
        self.assertTrue(planner.memory_pressure(.1*G,32*G)['severe'])
        low=planner.memory_pressure(.2*G,8*G)
        self.assertFalse(low['severe'])
        self.assertLess(low['reserve_bytes'],sample['reserve_bytes'])

    def test_actual_reference_count_increases_resident_condition_kv(self):
        one=self.plan(runtime(),task='i2i',reference_count=1)
        three=self.plan(runtime(),task='i2i',reference_count=3)
        self.assertGreater(three['estimates']['condition_kv_bytes'],one['estimates']['condition_kv_bytes'])
        self.assertGreater(three['estimates']['text_workspace_bytes'],one['estimates']['text_workspace_bytes'])
        expected=2*32*4096*three['estimates']['prefix_tokens']*2
        self.assertEqual(three['estimates']['condition_kv_bytes'],expected)
    def test_long_prompt_cannot_hide_behind_the_image_pixel_budget(self):
        short=self.plan(runtime(),prompt='cat')
        long=self.plan(runtime(),prompt='long prompt '*2000)
        self.assertGreater(long['estimates']['condition_kv_bytes'],8*G)
        self.assertGreater(long['estimates']['text_workspace_bytes'],short['estimates']['text_workspace_bytes'])
        self.assertFalse(long['feasible'])
        self.assertEqual(long['estimates']['token_estimator'],'utf8_byte_upper_bound')
    def test_cpu_tokenizer_is_used_when_available(self):
        import types
        encoder=types.SimpleNamespace(encode=lambda text,add_special_tokens:types.SimpleNamespace(ids=list(range(7))))
        fake=types.SimpleNamespace(Tokenizer=types.SimpleNamespace(from_file=lambda path:encoder))
        with patch.dict('sys.modules',{'tokenizers':fake}):
            result=self.plan(runtime(),prompt='any prompt')
        self.assertEqual(result['estimates']['token_estimator'],'tokenizer_json')
        self.assertEqual(result['estimates']['prompt_tokens'],71)
    def test_stream_gpu_peak_includes_vae_and_prepost_while_encoding(self):
        result=self.plan(runtime())
        e=result['estimates']
        self.assertGreater(e['gpu_peak_bytes'],e['vae_workspace_bytes'])
        self.assertEqual(e['persistent_vae_bytes'],inventory()['vae']['bytes'])
        self.assertEqual(e['persistent_dit_static_bytes'],inventory()['transformer']['static_bytes'])
    def test_vae_tiling_follows_available_budget_at_same_output_size(self):
        high=self.plan(runtime(gpu=160,free=150,ram=96,available=80),2048,2048)
        lower=self.plan(runtime(gpu=80,free=75,ram=96,available=80),2048,2048)
        self.assertEqual(high['strategy'],'full_resident')
        self.assertEqual(lower['strategy'],'full_resident')
        self.assertFalse(high['config_changes']['use_tiling_vae'])
        self.assertTrue(lower['config_changes']['use_tiling_vae'])
    def test_shared_peak_accounts_for_cpu_and_gpu_copies(self):
        result=self.plan(runtime(gpu=40,free=38,ram=64,available=35,shared=True))
        e=result['estimates']
        self.assertEqual(e['shared_peak_bytes'],e['host_peak_bytes']+e['gpu_peak_bytes'])
        self.assertLessEqual(e['shared_peak_bytes'],result['shared_budget_bytes'])

    def test_policy_override_and_invalid_runtime(self):
        plan=self.plan(runtime(),policy={'host_reserve_fraction':.05})
        self.assertAlmostEqual(plan['estimates']['host_reserve_bytes']/G,1.6)
        with self.assertRaisesRegex(ValueError,'policy'):
            self.plan(runtime(),policy={'misspelled':1})
        with self.assertRaisesRegex(ValueError,'inconsistent'):
            self.plan(runtime(gpu=8,free=9))
        with self.assertRaisesRegex(ValueError,'Missing runtime resource'):
            self.plan({'backend':'cuda'})


if __name__=='__main__':
    unittest.main()
