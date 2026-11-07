"""Crash reporting tests use fixture files and exit codes, never trigger WER or a GPU."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "bridge_diagnostics_test", ROOT / "LightX2V.Windows/Backend/bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class BridgeDiagnosticsTests(unittest.TestCase):
    def test_signed_and_unsigned_access_violations_are_readable(self):
        for code in (3221225477, -1073741819):
            with self.subTest(code=code), tempfile.TemporaryDirectory() as folder:
                details = bridge.worker_exit_details(
                    code, 29212, folder, "python.exe", time.time(), dump_roots=[])
                self.assertEqual(details["exit_code_hex"], "0xC0000005")
                self.assertEqual(details["exit_code_unsigned"], 3221225477)
                self.assertEqual(details["worker_pid"], 29212)
                message = bridge.worker_failure_message(details, ["unrelated size JSON"])
                self.assertIn("access violation", message)
                self.assertIn("worker PID 29212", message)
                self.assertIn("No matching existing crash dump", message)
                self.assertNotIn("unrelated size JSON", message)
                self.assertNotIn("retained dump", message)
                saved = json.loads((Path(folder) / "worker-exit.json").read_text())
                self.assertEqual(saved["exit_code"], code)

    def test_only_existing_matching_recent_nonempty_dumps_are_listed(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            started = time.time() - 10
            good = root / "python.exe.29212.dmp"
            good.write_bytes(b"fixture dump, not a real minidump")
            stale = root / "python.exe.29212.old.dmp"
            stale.write_bytes(b"old")
            os.utime(stale, (started - 60, started - 60))
            (root / "python.exe.292120.dmp").write_bytes(b"other pid")
            (root / "python.exe.29212.empty.dmp").write_bytes(b"")
            (root / "another.exe.29212.dmp").write_bytes(b"other executable")
            details = bridge.worker_exit_details(
                0xC0000005, 29212, root, "python.exe", started, dump_roots=[root])
            self.assertEqual([item["path"] for item in details["dumps"]], [str(good.resolve())])
            message = bridge.worker_failure_message(details, [])
            self.assertIn(str(good.resolve()), message)
            self.assertIn("Found existing crash dump", message)
            self.assertNotIn("No matching existing", message)

    def test_default_dump_location_is_checked_without_creating_it(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            task, local = root / "task", root / "local"
            task.mkdir()
            with patch.dict(os.environ, {"LOCALAPPDATA": str(local)}):
                self.assertEqual(bridge.find_existing_crash_dumps(
                    task, 1234, "python.exe", time.time()), [])
                self.assertFalse((local / "CrashDumps").exists())
                dumps = local / "CrashDumps"
                dumps.mkdir(parents=True)
                dump = dumps / "python.exe.1234.dmp"
                dump.write_bytes(b"fixture")
                found = bridge.find_existing_crash_dumps(task, 1234, "python.exe", time.time() - 1)
                self.assertEqual(found[0]["path"], str(dump.resolve()))

    def test_fault_log_is_only_reported_when_it_exists(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            details = bridge.worker_exit_details(0xC0000005, 123, root, "python.exe",
                                                  time.time(), dump_roots=[])
            self.assertNotIn("faulthandler_log", details)
            (root / "worker-fault.log").write_text("Worker PID: 123\n", encoding="utf-8")
            details = bridge.worker_exit_details(0xC0000005, 123, root, "python.exe",
                                                  time.time(), dump_roots=[])
            self.assertEqual(details["faulthandler_log"], str((root / "worker-fault.log").resolve()))

    def test_python_exit_keeps_traceback_tail_and_records_pid_hex(self):
        with tempfile.TemporaryDirectory() as folder:
            details = bridge.worker_exit_details(1, 456, folder, "python.exe",
                                                  time.time(), dump_roots=[])
            self.assertFalse(details["native_failure"])
            message = bridge.worker_failure_message(details, ["RuntimeError: original failure"])
            self.assertIn("0x00000001", message)
            self.assertIn("worker PID 456", message)
            self.assertIn("RuntimeError: original failure", message)

    def test_diagnostic_write_failure_does_not_replace_exit_information(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(
                bridge, "atomic_json", side_effect=OSError("read-only diagnostics")):
            details = bridge.worker_exit_details(0xC0000005, 789, folder, "python.exe",
                                                  time.time(), dump_roots=[])
        self.assertEqual(details["exit_code_hex"], "0xC0000005")
        self.assertIn("diagnostic_write_error", details)
        self.assertIn("access violation", bridge.worker_failure_message(details, []))


if __name__ == "__main__":
    unittest.main()
