"""Strict header and owned-buffer row/block I/O regressions, without mmap."""
from contextlib import contextmanager
import gc
import io
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'LightX2V.Windows' / 'Backend'))
import checkpoint_reader as reader
from checkpoint_reader import BoundedSafetensorsCheckpoint
try:
    import torch
except ImportError:
    torch = None


def write_blob(path, header, payload=b''):
    raw = json.dumps(header, separators=(',', ':')).encode('utf-8') if isinstance(header, dict) else header
    raw += b' ' * (-len(raw) % 8)
    path.write_bytes(struct.pack('<Q', len(raw)) + raw + payload)
    return 8 + len(raw)


def write_tensors(path, tensors):
    header, payload, offset = {}, [], 0
    for name, dtype, shape, raw in tensors:
        header[name] = {'dtype': dtype, 'shape': list(shape), 'data_offsets': [offset, offset + len(raw)]}
        payload.append(raw)
        offset += len(raw)
    return write_blob(path, header, b''.join(payload))


class HeaderAndReadTests(unittest.TestCase):
    def test_headers_and_block_index_need_no_torch_or_safetensors(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'model.safetensors'
            write_tensors(path, [('transformer_blocks.0.weight', 'BF16', (2,), b'1234'),
                                 ('transformer_blocks.1.weight', 'BF16', (2,), b'5678')])
            with mock.patch.dict(sys.modules, {'safetensors': None}), mock.patch('mmap.mmap', side_effect=AssertionError('mmap forbidden')):
                checkpoint = BoundedSafetensorsCheckpoint(path)
                self.assertEqual(checkpoint.block_indices, (0, 1))
                self.assertEqual(checkpoint.tensor_spec('transformer_blocks.1.weight').nbytes, 4)
                self.assertIsNone(checkpoint._torch_module)

    def test_shape_dtype_offsets_are_strict_including_bool_and_float(self):
        entries = [
            {'dtype': 'BF16', 'shape': [True], 'data_offsets': [0, 4]},
            {'dtype': 'BF16', 'shape': [2.0], 'data_offsets': [0, 4]},
            {'dtype': 'BF16', 'shape': [-2], 'data_offsets': [0, 4]},
            {'dtype': 'BF16', 'shape': [2**64], 'data_offsets': [0, 4]},
            {'dtype': 'BF16', 'shape': [2], 'data_offsets': [False, 4]},
            {'dtype': 'BF16', 'shape': [2], 'data_offsets': [0, 4.0]},
            {'dtype': 'BF16', 'shape': [2], 'data_offsets': [-1, 3]},
            {'dtype': 'BF16', 'shape': [2], 'data_offsets': [4, 0]},
            {'dtype': 'BF16', 'shape': [2], 'data_offsets': [0, 5]},
            {'dtype': 'BF16', 'shape': [2], 'data_offsets': [0, 2]},
            {'dtype': 'unknown', 'shape': [2], 'data_offsets': [0, 4]},
            {'dtype': True, 'shape': [2], 'data_offsets': [0, 4]},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'bad.safetensors'
            for entry in entries:
                with self.subTest(entry=entry):
                    write_blob(path, {'weight': entry}, b'1234')
                    with self.assertRaises(ValueError):
                        BoundedSafetensorsCheckpoint(path)

    def test_duplicate_json_keys_nan_and_invalid_metadata_are_rejected(self):
        headers = [b'{"weight":{"dtype":"BF16","dtype":"F32","shape":[1],"data_offsets":[0,4]}}',
                   b'{"weight":{"dtype":"BF16","shape":[NaN],"data_offsets":[0,4]}}',
                   {'__metadata__': {'invalid': 2}, 'weight': {'dtype': 'BF16', 'shape': [2], 'data_offsets': [0, 4]}}]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'bad.safetensors'
            for header in headers:
                with self.subTest(header=header):
                    write_blob(path, header, b'1234')
                    with self.assertRaises(ValueError):
                        BoundedSafetensorsCheckpoint(path)

    def test_header_length_truncation_and_limits_are_checked_before_allocation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'bad.safetensors'
            for raw in [b'1234567', struct.pack('<Q', reader.MAX_HEADER_BYTES + 1), struct.pack('<Q', 1024) + b'{}']:
                path.write_bytes(raw)
                with self.assertRaises(ValueError):
                    BoundedSafetensorsCheckpoint(path)

    def test_holes_overlaps_and_unindexed_bytes_are_rejected(self):
        cases = [({'a': {'dtype': 'U8', 'shape': [2], 'data_offsets': [1, 3]}}, b'123'),
                 ({'a': {'dtype': 'U8', 'shape': [2], 'data_offsets': [0, 2]},
                   'b': {'dtype': 'U8', 'shape': [2], 'data_offsets': [1, 3]}}, b'123'),
                 ({'a': {'dtype': 'U8', 'shape': [2], 'data_offsets': [0, 2]}}, b'123')]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'bad.safetensors'
            for header, payload in cases:
                write_blob(path, header, payload)
                with self.assertRaises(ValueError):
                    BoundedSafetensorsCheckpoint(path)

    def test_duplicate_names_across_shards_and_changed_payload_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first, second = root / 'one.safetensors', root / 'two.safetensors'
            write_tensors(first, [('weight', 'U8', (2,), b'12')])
            write_tensors(second, [('weight', 'U8', (2,), b'34')])
            with self.assertRaisesRegex(ValueError, 'Duplicate checkpoint tensor'):
                BoundedSafetensorsCheckpoint(root)
            second.unlink()
            checkpoint = BoundedSafetensorsCheckpoint(root)
            with first.open('ab') as file:
                file.write(b'x')
            with self.assertRaisesRegex(RuntimeError, 'changed after indexing'):
                with checkpoint._open_checked(first.name):
                    self.fail('must reject before yielding a changed file')

    def test_short_reads_interruptions_and_chunk_bound_are_handled(self):
        class ShortReader(io.BytesIO):
            def __init__(self, data):
                super().__init__(data)
                self.sizes = []
                self.interrupted = False
            def readinto(self, target):
                self.sizes.append(len(target))
                if not self.interrupted:
                    self.interrupted = True
                    raise InterruptedError()
                return super().readinto(target[:2])
        source = ShortReader(b'123456789')
        target = bytearray(9)
        with mock.patch.object(reader, 'READ_CHUNK_BYTES', 4):
            reader._read_exact_into(source, memoryview(target), description='unit fixture')
        self.assertEqual(target, b'123456789')
        self.assertLessEqual(max(source.sizes), 4)
        with self.assertRaisesRegex(EOFError, 'Incomplete checkpoint payload'):
            reader._read_exact_into(io.BytesIO(b'12'), memoryview(bytearray(3)), description='truncated')


@unittest.skipIf(torch is None, 'PyTorch is not installed; owned tensor reads require it')
class OwnedTensorReadTests(unittest.TestCase):
    def make_table(self, path):
        table = torch.arange(48, dtype=torch.bfloat16).reshape(12, 4)
        write_tensors(path, [('lm_head.weight', 'BF16', (2, 4), b'\0' * 16),
                             ('embedding.weight', 'BF16', table.shape, table.view(torch.uint8).numpy().tobytes())])
        return table

    def test_row_reads_preserve_duplicate_order_shape_and_read_only_unique_rows(self):
        with tempfile.TemporaryDirectory(prefix='中文 首次读取 ') as directory:
            path = Path(directory) / '模型.safetensors'
            table = self.make_table(path)
            checkpoint = BoundedSafetensorsCheckpoint(path)
            original = checkpoint._open_checked
            counts = {'bytes': 0, 'reads': 0, 'seeks': []}
            class Counted:
                def __init__(self, source): self.source = source
                def seek(self, offset): counts['seeks'].append(offset); return self.source.seek(offset)
                def readinto(self, target):
                    n = self.source.readinto(target)
                    counts['bytes'] += n
                    counts['reads'] += 1
                    return n
            @contextmanager
            def counted(shard):
                with original(shard) as source:
                    yield Counted(source)
            ids = torch.tensor([[7, 2, 7, 1], [3, 11, 0, 2]], dtype=torch.int64)
            with mock.patch.object(checkpoint, '_open_checked', counted), mock.patch('mmap.mmap', side_effect=AssertionError('mmap forbidden')):
                output = checkpoint.read_rows('embedding.weight', ids)
            torch.testing.assert_close(output, torch.nn.functional.embedding(ids, table), rtol=0, atol=0)
            self.assertEqual(counts['bytes'], 6 * 4 * 2)  # six unique rows, not 12-row vocabulary
            self.assertEqual(counts['reads'], 3)  # adjacent IDs 0..3 are coalesced
            self.assertIsNone(output.untyped_storage().filename)
            path.unlink()
            gc.collect()
            torch.testing.assert_close(output, table[ids], rtol=0, atol=0)

    def test_headers_rows_and_blocks_never_enter_native_mapping_apis(self):
        # Mock all native mapping entry points, not only Python mmap (the Rust
        # safetensors backend bypasses the latter entirely).
        import types
        try:
            import safetensors
        except ImportError:
            safetensors = types.SimpleNamespace(safe_open=lambda *a, **kw: None)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'model.safetensors'
            table = self.make_table(path)
            with mock.patch.dict(sys.modules, {'safetensors': safetensors}), \
                    mock.patch.object(safetensors, 'safe_open', side_effect=AssertionError('native safe_open forbidden')) as safe_open, \
                    mock.patch.object(torch.UntypedStorage, 'from_file', side_effect=AssertionError('native whole-file storage forbidden')) as storage_open, \
                    mock.patch.object(torch, 'from_file', side_effect=AssertionError('native tensor mapping forbidden')) as tensor_open, \
                    mock.patch('mmap.mmap', side_effect=AssertionError('Python mapping forbidden')):
                checkpoint = BoundedSafetensorsCheckpoint(path)
                ids = torch.tensor([7, 2, 7])
                rows = checkpoint.read_rows('embedding.weight', ids)
                target = torch.empty_like(table)
                checkpoint.load_tensors_into({'embedding.weight': target})
                loaded = checkpoint.load_tensors(['embedding.weight'])['embedding.weight']
                safe_open.assert_not_called()
                storage_open.assert_not_called()
                tensor_open.assert_not_called()
            torch.testing.assert_close(rows, table[ids], rtol=0, atol=0)
            torch.testing.assert_close(target, table, rtol=0, atol=0)
            torch.testing.assert_close(loaded, table, rtol=0, atol=0)

    def test_scalar_int32_empty_ids_and_bounds_match_embedding_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'model.safetensors'
            table = self.make_table(path)
            checkpoint = BoundedSafetensorsCheckpoint(path)
            scalar = checkpoint.read_rows('embedding.weight', torch.tensor(3, dtype=torch.int32))
            torch.testing.assert_close(scalar, table[3], rtol=0, atol=0)
            self.assertEqual(checkpoint.read_rows('embedding.weight', torch.empty((2, 0), dtype=torch.int64)).shape, (2, 0, 4))
            for ids in [torch.tensor([-1]), torch.tensor([12])]:
                with self.assertRaises(IndexError): checkpoint.read_rows('embedding.weight', ids)
            for ids in [torch.tensor([1.]), torch.tensor([True])]:
                with self.assertRaises(TypeError): checkpoint.read_rows('embedding.weight', ids)

    def test_block_reads_use_owned_destinations_and_validate_layout(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'model.safetensors'
            expected = torch.arange(6, dtype=torch.bfloat16).reshape(2, 3)
            write_tensors(path, [('weight', 'BF16', expected.shape, expected.view(torch.uint8).numpy().tobytes())])
            checkpoint = BoundedSafetensorsCheckpoint(path)
            target = torch.empty_like(expected)
            with mock.patch('mmap.mmap', side_effect=AssertionError('mmap forbidden')):
                checkpoint.load_tensors_into({'weight': target})
                loaded = checkpoint.load_tensors(['weight'])['weight']
            torch.testing.assert_close(target, expected, rtol=0, atol=0)
            torch.testing.assert_close(loaded, expected, rtol=0, atol=0)
            self.assertIsNone(loaded.untyped_storage().filename)
            with self.assertRaises(ValueError):
                checkpoint.load_tensors_into({'weight': torch.empty((3, 2), dtype=torch.bfloat16).t()})
            with self.assertRaises(ValueError):
                checkpoint.load_tensors_into({'weight': torch.empty((2, 3), dtype=torch.float32)})
            with path.open('r+b') as file: file.truncate(path.stat().st_size - 1)
            with self.assertRaisesRegex(RuntimeError, 'changed after indexing'):
                checkpoint.load_tensors_into({'weight': target})

    def test_repeated_owned_embedding_reads_survive_gc_churn_and_do_not_call_load_tensors(self):
        from adaptive_streaming import CheckpointEmbedding
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'model.safetensors'
            table = self.make_table(path)
            checkpoint = BoundedSafetensorsCheckpoint(path)
            embedding = CheckpointEmbedding(checkpoint, 'embedding.weight', 'cpu', torch.bfloat16)
            ids = torch.tensor([11, 3, 3, 0, 5])
            with mock.patch.object(checkpoint, 'load_tensors', side_effect=AssertionError('whole-table load forbidden')):
                for _ in range(20):
                    value = embedding.apply(ids)
                    churn = bytearray(2 * 1024 * 1024)
                    gc.collect()
                    torch.testing.assert_close(value, table[ids], rtol=0, atol=0)
                    del churn


@unittest.skipUnless(torch is not None and os.environ.get('LIGHTX2V_TEST_MODEL'),
                     'requires PyTorch and LIGHTX2V_TEST_MODEL for official CPU row-read validation')
class OfficialCheckpointRowTests(unittest.TestCase):
    def test_actual_bf16_embedding_offset_and_repeated_rows(self):
        path = Path(os.environ['LIGHTX2V_TEST_MODEL']) / 'text_encoder' / 'model.safetensors'
        checkpoint = BoundedSafetensorsCheckpoint(path)
        name = 'model.language_model.embed_tokens.weight'
        spec = checkpoint.tensor_spec(name)
        self.assertEqual(spec.shape, (151936, 4096))
        self.assertEqual(spec.dtype, 'BF16')
        ids = torch.tensor([0, 1, 2, 151643, 151655, 151643, 151935, 7, 42], dtype=torch.int64)
        expected = []
        with path.open('rb', buffering=0) as source:
            for row in ids.tolist():
                source.seek(spec.offset + row * spec.shape[1] * 2)
                raw = bytearray(source.read(spec.shape[1] * 2))
                self.assertEqual(len(raw), spec.shape[1] * 2)
                expected.append(torch.frombuffer(raw, dtype=torch.bfloat16).clone())
        expected = torch.stack(expected)
        for _ in range(10):
            output = checkpoint.read_rows(name, ids)
            churn = bytearray(16 * 1024 * 1024)
            gc.collect()
            self.assertTrue(torch.equal(output.view(torch.uint8), expected.view(torch.uint8)))
            self.assertIsNone(output.untyped_storage().filename)
            self.assertNotEqual(output.data_ptr(), spec.offset)
            del churn
        print(f'Official CPU owned-row validation passed: offset=0x{spec.offset:X}, 9 requested/8 unique rows, 10 GC/churn iterations; no full vocabulary load.')


if __name__ == '__main__':
    unittest.main()
