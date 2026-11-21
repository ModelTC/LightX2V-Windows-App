"""Run the required CPU-only numerical and checkpoint fixtures."""
import os
from pathlib import Path
import sys
import unittest

if __name__ == '__main__':
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Tests'))
    os.environ['LIGHTX2V_TEST_CHECKPOINT_IO_REAL'] = '1'
    suite = unittest.defaultTestLoader.loadTestsFromNames([
        'test_adaptive_streaming.StreamingTests',
        'test_adaptive_streaming.OneShotLifecycleTests',
        'test_checkpoint_reader.OwnedTensorReadTests',
        'test_checkpoint_io.RealSafetensorsPolicyTests.test_owned_cpu_tensor_survives_context_close_and_gc',
        'test_checkpoint_io.RealSafetensorsPolicyTests.test_repeated_chinese_path_reads_with_gc_and_memory_churn',
        'test_checkpoint_io.RealSafetensorsPolicyTests.test_cpu_pread_does_not_call_torch_whole_file_storage',
        'test_checkpoint_io.RealSafetensorsPolicyTests.test_real_numpy_framework_is_unchanged',
    ])
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if result.skipped:
        print('CPU regressions require every selected test to run.', file=sys.stderr)
    sys.exit(0 if result.wasSuccessful() and not result.skipped else 1)
