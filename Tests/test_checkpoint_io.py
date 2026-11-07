import gc
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('checkpoint_io',ROOT/'LightX2V.Windows/Backend/checkpoint_io.py')
io=importlib.util.module_from_spec(spec);spec.loader.exec_module(io)


class CheckpointPolicyTests(unittest.TestCase):
    def setUp(self):
        self.calls=[]
        class Handle:
            def __enter__(self):return self
            def __exit__(self,*args):return False
            def metadata(self):return {'fixture':'preserved'}
            def get_tensor(self,name):return ('tensor',name)
            def get_slice(self,name):return ('slice',name)
        self.handle=Handle()
        def native(*args,**kwargs):
            if kwargs.get('backend') not in ('mmap','pread',None):
                raise ValueError('native invalid backend')
            self.calls.append((args,kwargs));return self.handle
        self.native=native
        self.package=types.ModuleType('safetensors');self.package.safe_open=native;self.package.__version__='0.8.0'
        self.extension=types.ModuleType('safetensors._safetensors_rust');self.extension.safe_open=native
        self.framework=types.ModuleType('safetensors.numpy');self.framework.safe_open=native
        self.modules={'safetensors':self.package,'safetensors._safetensors_rust':self.extension,
                      'safetensors.numpy':self.framework}
        self.context=patch.dict(sys.modules,self.modules);self.context.start();self.addCleanup(self.context.stop)
    def test_windows_default_and_explicit_backends_are_pread_for_all_frameworks(self):
        with patch.object(io.sys,'platform','win32'):
            policy=io.install_windows_checkpoint_io()
            for framework in ('pt','pytorch','np','numpy','tf','flax','paddle','mlx'):
                for backend in ({},{'backend':'mmap'},{'backend':'pread'}):
                    self.package.safe_open('checkpoint',framework,device='cpu',**backend)
        self.assertTrue(policy['enabled'])
        self.assertTrue(all(kwargs['backend']=='pread' for _,kwargs in self.calls))
        self.assertEqual({args[1] for args,_ in self.calls},{'pt','pytorch','np','numpy','tf','flax','paddle','mlx'})
    def test_aliases_context_and_metadata_are_unchanged(self):
        with patch.object(io.sys,'platform','win32'):
            io.install_windows_checkpoint_io()
            self.assertIs(self.package.safe_open,self.extension.safe_open)
            self.assertIs(self.package.safe_open,self.framework.safe_open)
            with self.extension.safe_open(filename='model',framework='pt',device='cuda:2') as handle:
                self.assertIs(handle,self.handle)
                self.assertEqual(handle.metadata(),{'fixture':'preserved'})
                self.assertEqual(handle.get_slice('w'),('slice','w'))
            self.assertEqual(self.calls[-1][1],dict(filename='model',framework='pt',device='cuda:2',backend='pread'))
    def test_install_is_idempotent_and_status_is_json_serializable(self):
        with patch.object(io.sys,'platform','win32'):
            first=io.install_windows_checkpoint_io();wrapper=self.package.safe_open
            second=io.install_windows_checkpoint_io()
            self.assertIs(wrapper,self.package.safe_open)
            self.package.safe_open('model','pt')
            status=io.checkpoint_io_status()
        self.assertEqual(first,second)
        self.assertEqual(status['open_calls'],1)
        self.assertEqual(status['redirected_mmap_calls'],1)
        self.assertIs(getattr(wrapper,io._ORIGINAL),self.native)
        json.dumps(status)
    def test_non_windows_is_untouched_and_does_not_import_safetensors(self):
        with patch.object(io.sys,'platform','linux'),patch.object(io.importlib,'import_module') as loader:
            policy=io.install_windows_checkpoint_io()
        loader.assert_not_called()
        self.assertFalse(policy['enabled'])
        self.assertIs(self.package.safe_open,self.native)
    def test_install_imports_only_safetensors_not_torch_or_upstream(self):
        with patch.object(io.sys,'platform','win32'),patch.object(io.importlib,'import_module',return_value=self.package) as loader:
            io.install_windows_checkpoint_io()
        loader.assert_called_once_with('safetensors')
    def test_invalid_backend_keeps_native_validation(self):
        with patch.object(io.sys,'platform','win32'):
            io.install_windows_checkpoint_io()
            with self.assertRaisesRegex(ValueError,'native invalid backend'):
                self.package.safe_open('model','pt',backend='invalid')


@unittest.skipUnless(os.environ.get('LIGHTX2V_TEST_CHECKPOINT_IO_REAL')=='1',
                     'Enable real safetensors CPU checks explicitly in the pinned environment')
class RealSafetensorsPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import torch
        import safetensors
        import safetensors.torch
        cls.torch,cls.safetensors=torch,safetensors
        cls.save_file=staticmethod(safetensors.torch.save_file)
    def setUp(self):
        self.saved=[(module,module.safe_open) for name,module in tuple(sys.modules.items())
                    if name=='safetensors' or name.startswith('safetensors.')
                    if module is not None and hasattr(module,'safe_open')]
        self.addCleanup(self.restore)
        io.install_windows_checkpoint_io()
        self.temp=tempfile.TemporaryDirectory(prefix='checkpoint-io-');self.addCleanup(self.temp.cleanup)
        self.path=Path(self.temp.name)/'中文路径'/ '权重.safetensors';self.path.parent.mkdir()
        self.expected={'bf16':self.torch.arange(128,dtype=self.torch.float32).reshape(16,8).to(self.torch.bfloat16),
                       'float':self.torch.arange(24,dtype=self.torch.float32).reshape(6,4)}
        self.save_file(self.expected,str(self.path),metadata={'fixture':'中文元数据'})
    def restore(self):
        for module,original in self.saved:module.safe_open=original
    def test_owned_cpu_tensor_survives_context_close_and_gc(self):
        with self.safetensors.safe_open(self.path,framework='pt',device='cpu') as handle:
            tensor=handle.get_tensor('bf16')
            self.assertEqual(handle.metadata(),{'fixture':'中文元数据'})
            saved_slice=handle.get_slice('float')
            self.assertEqual(saved_slice.get_shape(),[6,4])
        gc.collect()
        self.torch.testing.assert_close(tensor,self.expected['bf16'],rtol=0,atol=0)
        sliced=saved_slice[1:5,1:3]
        self.torch.testing.assert_close(sliced,self.expected['float'][1:5,1:3],rtol=0,atol=0)
        tensor.add_(1)
        with self.safetensors.safe_open(self.path,framework='pt',device='cpu') as handle:
            self.torch.testing.assert_close(handle.get_tensor('bf16'),self.expected['bf16'],rtol=0,atol=0)
    def test_repeated_chinese_path_reads_with_gc_and_memory_churn(self):
        for index in range(24):
            with self.safetensors.safe_open(self.path,framework='pt',device='cpu',backend='mmap') as handle:
                actual=handle.get_tensor('bf16')
            churn=bytearray(1024*1024)
            gc.collect()
            self.torch.testing.assert_close(actual,self.expected['bf16'],rtol=0,atol=0)
            del actual,churn
    def test_cpu_pread_does_not_call_torch_whole_file_storage(self):
        with patch.object(self.torch.UntypedStorage,'from_file',side_effect=AssertionError('unsafe whole-file storage')):
            with self.safetensors.safe_open(self.path,framework='pt',device='cpu') as handle:
                actual=handle.get_tensor('float')
        self.torch.testing.assert_close(actual,self.expected['float'],rtol=0,atol=0)
    def test_real_numpy_framework_is_unchanged(self):
        import numpy as np
        with self.safetensors.safe_open(self.path,framework='np') as handle:
            actual=handle.get_tensor('float')
        gc.collect()
        np.testing.assert_array_equal(actual,self.expected['float'].numpy())
    @unittest.skipUnless(os.environ.get('LIGHTX2V_TEST_CHECKPOINT_IO_GPU')=='1',
                         'GPU checks are serialized by the root validation run')
    def test_gpu_pread_tensor_matches_original_bf16(self):
        self.assertTrue(self.torch.cuda.is_available())
        with patch.object(self.torch.UntypedStorage,'from_file',side_effect=AssertionError('unsafe whole-file storage')):
            with self.safetensors.safe_open(self.path,framework='pt',device='cuda:0') as handle:
                actual=handle.get_tensor('bf16')
        self.torch.cuda.synchronize()
        self.torch.testing.assert_close(actual.cpu(),self.expected['bf16'],rtol=0,atol=0)


if __name__=='__main__':unittest.main()
