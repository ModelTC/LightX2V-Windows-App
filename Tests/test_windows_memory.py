import ctypes
import importlib.util
import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('windows_memory',ROOT/'LightX2V.Windows/Backend/windows_memory.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)


def adapter(name='AMD Radeon(TM) Example Graphics',vendor=0x1002,luid='00000000:00000001',software=False):
    return dict(name=name,vendor_id=vendor,device_id=123,luid=luid,software=software,
                local=dict(budget_bytes=100,current_usage_bytes=40,budget_headroom_bytes=60),
                **{'nonlocal':dict(budget_bytes=0,current_usage_bytes=0,budget_headroom_bytes=0)})


class WindowsMemoryTests(unittest.TestCase):
    def test_name_normalization_and_vendor_filter(self):
        chosen=adapter()
        others=[adapter('Microsoft Basic Render Driver',0x1414,software=True),
                adapter('NVIDIA GeForce Example GPU',0x10de),chosen]
        self.assertIs(m.select_adapter(others,'AMD Radeon Example',0x1002),chosen)
        with self.assertRaises(m.WindowsMemoryError):
            m.select_adapter(others,'AMD Radeon Example',0x10de)
    def test_hybrid_and_identical_adapters_require_identity(self):
        one,two=adapter(),adapter(luid='00000000:00000002')
        with self.assertRaisesRegex(m.WindowsMemoryError,'ambiguous'):
            m.select_adapter([one,two],one['name'],0x1002)
        self.assertIs(m.select_adapter([one,two],one['name'],0x1002,luid=two['luid']),two)
    def test_query_keeps_all_raw_adapters_on_ambiguous_match(self):
        rows=[adapter(),adapter(luid='00000000:00000002')]
        with patch.object(m,'_query_dxgi_adapters',return_value=rows):
            result=m.query_video_memory('AMD Radeon Example',0x1002)
        self.assertFalse(result['available'])
        self.assertEqual(result['adapters'],rows)
        self.assertEqual(result['scope'],'current_process')
        self.assertEqual(result['process_id'],os.getpid())
        json.dumps(result)
    def test_success_reports_process_budget_not_dedicated_descriptor(self):
        row=adapter();row['dedicated_video_bytes']=10
        with patch.object(m,'_query_dxgi_adapters',return_value=[row]):
            result=m.query_video_memory('AMD Radeon Example',0x1002)
        self.assertTrue(result['available'])
        self.assertEqual(result['working_set_budget_bytes'],100)
        self.assertEqual(result['working_set_headroom_bytes'],60)
    def test_unavailable_api_or_zero_budget_is_explicit(self):
        with patch.object(m,'_query_dxgi_adapters',side_effect=OSError('unavailable')):
            result=m.query_video_memory()
        self.assertFalse(result['available'])
        self.assertIn('unavailable',result['reason'])
        row=adapter();row['local']['budget_bytes']=0
        with patch.object(m,'_query_dxgi_adapters',return_value=[row]):
            result=m.query_video_memory()
        self.assertFalse(result['available'])
        self.assertIn('zero',result['reason'])
    def test_fixed_width_layout_and_memory_arithmetic(self):
        self.assertEqual(ctypes.sizeof(m._GUID),16)
        self.assertEqual(ctypes.sizeof(m._LUID),8)
        self.assertEqual(ctypes.sizeof(m._VideoMemoryInfo),32)
        info=m._VideoMemoryInfo(100,140,50,0)
        self.assertEqual(m._memory_dict(info)['over_budget_bytes'],40)
        self.assertEqual(m._memory_dict(info)['budget_headroom_bytes'],0)
        if sys.platform=='win32' and ctypes.sizeof(ctypes.c_void_p)==8:
            self.assertEqual(ctypes.sizeof(m._AdapterDesc1),312)


@unittest.skipUnless(sys.platform=='win32' and os.environ.get('LIGHTX2V_TEST_DXGI')=='1',
                     'Enable the read-only real DXGI API check explicitly on Windows')
class RealWindowsMemoryTests(unittest.TestCase):
    def test_enumeration_and_query_by_actual_luid(self):
        rows=m._query_dxgi_adapters()
        hardware=[r for r in rows if not r['software'] and r.get('local') and r['local']['budget_bytes']>0]
        self.assertTrue(hardware,'No DXGI hardware budget could be read')
        for row in hardware:
            result=m.query_video_memory(row['name'],row['vendor_id'],row['device_id'],row['luid'])
            self.assertTrue(result['available'],result)
            self.assertEqual(result['adapter']['luid'],row['luid'])
            self.assertGreater(result['working_set_budget_bytes'],0)
            self.assertGreaterEqual(result['adapter']['local']['current_usage_bytes'],0)
            self.assertEqual(result['scope'],'current_process')


if __name__=='__main__':unittest.main()
