"""Read DXGI process memory budgets without torch, GPU allocation or reservations.

QueryVideoMemoryInfo reports the calling process, not another inference worker.
LOCAL is the OS working-set target. On UMA it refers to system memory; descriptor
DedicatedVideoMemory alone must not be treated as a measured fast-memory budget.

Primary API references:
https://learn.microsoft.com/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo
https://learn.microsoft.com/windows/win32/api/dxgi1_4/ns-dxgi1_4-dxgi_query_video_memory_info
https://learn.microsoft.com/windows/win32/api/d3d12/ne-d3d12-d3d12_memory_pool
"""
import ctypes
import os
import re
import sys
import time
import uuid

_SOURCE = 'IDXGIAdapter3.QueryVideoMemoryInfo'
_HRESULT = ctypes.c_int32
_UINT = ctypes.c_uint32


class _GUID(ctypes.Structure):
    _fields_ = [('Data1', ctypes.c_uint32), ('Data2', ctypes.c_uint16),
                ('Data3', ctypes.c_uint16), ('Data4', ctypes.c_ubyte * 8)]

    @classmethod
    def parse(cls, value):
        return cls.from_buffer_copy(uuid.UUID(value).bytes_le)


class _LUID(ctypes.Structure):
    _fields_ = [('LowPart', ctypes.c_uint32), ('HighPart', ctypes.c_int32)]


class _AdapterDesc1(ctypes.Structure):
    _fields_ = [('Description', ctypes.c_wchar * 128), ('VendorId', _UINT),
                ('DeviceId', _UINT), ('SubSysId', _UINT), ('Revision', _UINT),
                ('DedicatedVideoMemory', ctypes.c_size_t), ('DedicatedSystemMemory', ctypes.c_size_t),
                ('SharedSystemMemory', ctypes.c_size_t), ('AdapterLuid', _LUID), ('Flags', _UINT)]


class _VideoMemoryInfo(ctypes.Structure):
    _fields_ = [('Budget', ctypes.c_uint64), ('CurrentUsage', ctypes.c_uint64),
                ('AvailableForReservation', ctypes.c_uint64), ('CurrentReservation', ctypes.c_uint64)]


class WindowsMemoryError(RuntimeError):
    pass


def _check(hr, operation):
    if hr < 0:
        raise WindowsMemoryError(f'{operation} failed (HRESULT 0x{hr & 0xffffffff:08X})')


def _method(pointer, index, restype, *argtypes):
    table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    return ctypes.WINFUNCTYPE(restype, ctypes.c_void_p, *argtypes)(table[index])


def _release(pointer):
    if pointer and pointer.value:
        _method(pointer, 2, _UINT)(pointer)


def _memory_dict(info):
    return {'budget_bytes': int(info.Budget), 'current_usage_bytes': int(info.CurrentUsage),
            'available_for_reservation_bytes': int(info.AvailableForReservation),
            'current_reservation_bytes': int(info.CurrentReservation),
            'budget_headroom_bytes': max(0, int(info.Budget) - int(info.CurrentUsage)),
            'over_budget_bytes': max(0, int(info.CurrentUsage) - int(info.Budget))}


def _query_dxgi_adapters():
    if sys.platform != 'win32':
        raise WindowsMemoryError('DXGI memory budgets require Windows.')
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        raise WindowsMemoryError('DXGI memory helper requires x64 Python.')
    dll = ctypes.WinDLL('dxgi.dll')
    create = dll.CreateDXGIFactory1
    create.argtypes = [ctypes.POINTER(_GUID), ctypes.POINTER(ctypes.c_void_p)]
    create.restype = _HRESULT
    iid_factory = _GUID.parse('770aae78-f26f-4dba-a829-253c83d1b387')
    iid_adapter3 = _GUID.parse('645967a4-1392-4310-a798-8053ce3e93fd')
    factory = ctypes.c_void_p()
    adapters = []
    try:
        _check(create(ctypes.byref(iid_factory), ctypes.byref(factory)), 'CreateDXGIFactory1')
        enum = _method(factory, 12, _HRESULT, _UINT, ctypes.POINTER(ctypes.c_void_p))
        for index in range(64):
            adapter, adapter3 = ctypes.c_void_p(), ctypes.c_void_p()
            hr = enum(factory, index, ctypes.byref(adapter))
            if hr & 0xffffffff == 0x887A0002:  # DXGI_ERROR_NOT_FOUND
                break
            _check(hr, 'EnumAdapters1')
            try:
                desc = _AdapterDesc1()
                _check(_method(adapter, 10, _HRESULT, ctypes.POINTER(_AdapterDesc1))(adapter, ctypes.byref(desc)), 'GetDesc1')
                item = {'index': index, 'name': desc.Description, 'vendor_id': int(desc.VendorId),
                        'device_id': int(desc.DeviceId), 'software': bool(desc.Flags & 2),
                        'dedicated_video_bytes': int(desc.DedicatedVideoMemory),
                        'dedicated_system_bytes': int(desc.DedicatedSystemMemory),
                        'shared_system_bytes': int(desc.SharedSystemMemory),
                        'luid': f'{desc.AdapterLuid.HighPart & 0xffffffff:08X}:{desc.AdapterLuid.LowPart:08X}',
                        'local': None, 'nonlocal': None}
                try:
                    qi = _method(adapter, 0, _HRESULT, ctypes.POINTER(_GUID), ctypes.POINTER(ctypes.c_void_p))
                    _check(qi(adapter, ctypes.byref(iid_adapter3), ctypes.byref(adapter3)), 'QueryInterface(IDXGIAdapter3)')
                    query = _method(adapter3, 14, _HRESULT, _UINT, _UINT, ctypes.POINTER(_VideoMemoryInfo))
                    for segment, name in ((0, 'local'), (1, 'nonlocal')):
                        info = _VideoMemoryInfo()
                        hr = query(adapter3, 0, segment, ctypes.byref(info))
                        if hr < 0:
                            item[name + '_error'] = f'QueryVideoMemoryInfo HRESULT 0x{hr & 0xffffffff:08X}'
                        else:
                            item[name] = _memory_dict(info)
                except WindowsMemoryError as exc:
                    item['query_error'] = str(exc)
                adapters.append(item)
            finally:
                _release(adapter3)
                _release(adapter)
        return adapters
    finally:
        _release(factory)


def _normalized_name(name):
    value = re.sub(r'\((?:tm|r)\)', '', str(name), flags=re.I)
    value = re.sub(r'\b(?:graphics|gpu)\b', '', value, flags=re.I)
    return re.sub(r'[^a-z0-9]', '', value.lower())


def select_adapter(adapters, device_name=None, vendor_id=None, device_id=None, luid=None):
    """Select unambiguously; never silently pick the first adapter in a hybrid PC."""
    candidates = [item for item in adapters if not item.get('software')]
    if vendor_id is not None:
        candidates = [item for item in candidates if item.get('vendor_id') == vendor_id]
    if device_id is not None:
        candidates = [item for item in candidates if item.get('device_id') == device_id]
    if luid is not None:
        candidates = [item for item in candidates if item.get('luid', '').lower() == str(luid).lower()]
    if device_name:
        name = _normalized_name(device_name)
        exact = [item for item in candidates if _normalized_name(item.get('name', '')) == name]
        if exact:
            candidates = exact
        else:
            candidates = [item for item in candidates if name and
                          (name in _normalized_name(item.get('name', '')) or
                           _normalized_name(item.get('name', '')) in name)]
    if len(candidates) != 1:
        raise WindowsMemoryError('DXGI adapter selection is ambiguous or unmatched; provide the GPU name, vendor/device ID or LUID.')
    return candidates[0]


def query_video_memory(device_name=None, vendor_id=None, device_id=None, luid=None):
    """Return JSON metadata. Query failure is explicit and does not block inference."""
    result = {'source': _SOURCE, 'scope': 'current_process', 'process_id': os.getpid(),
              'captured_at_unix': time.time(), 'available': False}
    try:
        adapters = _query_dxgi_adapters()
        result['adapters'] = adapters
        item = select_adapter(adapters, device_name, vendor_id, device_id, luid)
        result.update(adapter=item, adapters=adapters)
        if not item.get('local') or not item['local']['budget_bytes']:
            raise WindowsMemoryError(item.get('query_error') or item.get('local_error') or 'DXGI local budget is unavailable or zero.')
        result.update(available=True, working_set_budget_bytes=item['local']['budget_bytes'],
                      working_set_headroom_bytes=item['local']['budget_headroom_bytes'])
    except (OSError, ValueError, WindowsMemoryError) as exc:
        result['reason'] = str(exc)
    return result


if __name__ == '__main__':
    import argparse
    import json
    parser = argparse.ArgumentParser()
    parser.add_argument('--name')
    parser.add_argument('--vendor', type=lambda value: int(value, 0))
    args = parser.parse_args()
    print(json.dumps(query_video_memory(args.name, args.vendor), ensure_ascii=False, indent=2))
