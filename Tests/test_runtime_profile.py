import importlib.util
import os
from pathlib import Path
import types
import unittest

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('runtime_profile',ROOT/'LightX2V.Windows/Backend/runtime_profile.py')
runtime=importlib.util.module_from_spec(spec);spec.loader.exec_module(runtime)


def torch(backend='cuda', version='2.11.0+cu128', capability=(8,9), available=True, bf16=True, archs=None):
    return types.SimpleNamespace(__version__=version,version=types.SimpleNamespace(hip='7.13.0' if backend=='amd_rocm' else None,cuda='12.8' if backend=='cuda' else None),cuda=types.SimpleNamespace(is_available=lambda:available,get_device_name=lambda i:'AMD Radeon 8060S' if backend=='amd_rocm' else 'NVIDIA GeForce RTX',get_device_capability=lambda i:capability,is_bf16_supported=lambda **kw:bf16,get_arch_list=lambda:archs or ['sm_80','sm_86','sm_90','sm_120','compute_120']))

class RuntimeProfileTests(unittest.TestCase):
    def test_auto_and_explicit_backend(self):
        self.assertEqual(runtime.detect_backend(torch()),'cuda')
        self.assertEqual(runtime.detect_backend(torch('amd_rocm','2.11.0+rocm7.13.0')),'amd_rocm')
        with self.assertRaisesRegex(RuntimeError,'selected'):runtime.detect_backend(torch(),'amd_rocm')
    def test_driver_cpu_wrong_wheel_and_unsupported_bf16(self):
        for value in [torch(available=False),torch('cpu'),torch(version='2.11.0+cpu'),torch(capability=(7,5)),torch(bf16=False),torch(capability=(13,0),archs=['sm_80'])]:
            with self.subTest(value=value),self.assertRaises(RuntimeError):runtime.detect_backend(value)
    def test_ampere_ada_blackwell(self):
        for cap in [(8,0),(8,6),(8,9),(9,0),(12,0)]:self.assertEqual(runtime.detect_backend(torch(capability=cap)),'cuda')
    def test_amd_config_preserved_for_all_sizes(self):
        import json
        base=json.loads((ROOT/'LightX2V.Windows/Resources/qwen_image_21_turbo.json').read_text())
        for width,height in [(512,512),(1376,768),(1024,1024),(2048,2048),(2752,1536)]:
            expected=dict(base)
            if width*height>1100000:expected.update(cpu_offload=True,offload_granularity='model',use_tiling_vae=True,vae_tile_size=1024)
            actual,profile=runtime.inference_profile('amd_rocm',base,width,height)
            self.assertEqual(actual,expected);self.assertEqual(base['cpu_offload'],False)
    def test_cuda_memory_policy(self):
        for memory in [None,8,12,16,24]:
            config,profile=runtime.inference_profile('cuda',{},1024,1024,None if memory is None else memory*runtime.GIB)
            self.assertEqual(config['offload_granularity'],'block');self.assertTrue(config['use_tiling_vae']);self.assertEqual(config['vae_tile_size'],512)
        self.assertEqual(runtime.inference_profile('cuda',{},512,512,32*runtime.GIB)[0]['offload_granularity'],'model')
    def test_backend_environment_isolation(self):
        parent={'PLATFORM':'amd_rocm','TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL':'1','RANK':'1','PYTORCH_NO_HIP_MEMORY_CACHING':'1','WORLD_SIZE':'2'}
        for backend in runtime.BACKENDS:
            for large in [False,True]:
                actual=runtime.inference_environment(backend,parent,r'C:\path with spaces\中文',large)
                self.assertEqual(actual['PLATFORM'],backend);self.assertNotIn('RANK',actual);self.assertNotIn('WORLD_SIZE',actual)
                self.assertEqual('TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL' in actual,backend=='amd_rocm' and large)
                self.assertNotIn('PYTORCH_NO_HIP_MEMORY_CACHING',actual)
        self.assertEqual(parent['RANK'],'1')

if __name__=='__main__':unittest.main()

class DownloadSpaceTests(unittest.TestCase):
    def test_remaining_bytes_resume_and_destination_guard(self):
        import hashlib
        import tempfile
        spec=importlib.util.spec_from_file_location('download_space',ROOT/'LightX2V.Windows/Backend/download_models.py')
        download=importlib.util.module_from_spec(spec);spec.loader.exec_module(download)
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve();item=dict(path='weight.bin',size=100,sha256='a',git_sha1=None)
            self.assertEqual(download.required_download_space(root,[item]),runtime.GIB+100)
            key=hashlib.sha256((item['path']+'a'+'None').encode()).hexdigest();cache=root/'.lightx2v-downloads';cache.mkdir();(cache/(key+'.partial')).write_bytes(b'x'*30)
            self.assertEqual(download.required_download_space(root,[item]),runtime.GIB+70)
            (root/'weight.bin').write_bytes(b'x'*100)
            self.assertEqual(download.required_download_space(root,[item]),runtime.GIB)
            with self.assertRaises(ValueError):download.required_download_space(root,[dict(item,path='../escape.bin')])
