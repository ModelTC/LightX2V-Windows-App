"""Run the actual worker entry point under isolated Python without loading a GPU."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKER = ROOT / "LightX2V.Windows" / "Backend" / "worker.py"
PRELUDE = r'''
import json
import os
from pathlib import Path
def event(kind, **fields):
    with Path(os.environ["WORKER_ENTRY_EVENTS"]).open("a", encoding="utf-8") as output:
        output.write(json.dumps({"event": kind, **fields}, ensure_ascii=False) + "\n")
'''


class WorkerEntryTests(unittest.TestCase):
    def run_worker(self, mode):
        """Copy only the entry script; collaborators and upstream are small fakes."""
        with tempfile.TemporaryDirectory(prefix="LightX2V worker entry ") as directory:
            root = Path(directory)
            backend = root / "portable app" / "Backend"
            repository = root / "LightX2V 源码"
            package = repository / "lightx2v"
            folder = root / "任务输出"
            for path in (backend, package, folder):
                path.mkdir(parents=True)
            shutil.copyfile(WORKER, backend / "worker.py")
            (package / "__init__.py").write_text("", encoding="utf-8")
            (package / "entry_helper.py").write_text(
                "IMPORT_MARKER = 'selected repository helper'\n", encoding="utf-8")
            (backend / "checkpoint_io.py").write_text(PRELUDE + r'''
import faulthandler
import sys
def install_windows_checkpoint_io():
    event("checkpoint_policy_install", fault_enabled=faulthandler.is_enabled(),
          torch_imported="torch" in sys.modules)
    if os.environ.get("WORKER_FIXTURE_MODE") == "bootstrap_error":
        raise RuntimeError("original bootstrap failure")
    return {"enabled": True, "backend": "test_pread"}
''', encoding="utf-8")
            (backend / "adaptive_streaming.py").write_text(PRELUDE + r'''
def install(config):
    event("stream_install", config=config)
def install_request_lifecycle(config):
    event("lifecycle_install", config=config)
''', encoding="utf-8")
            (backend / "telemetry.py").write_text(PRELUDE + r'''
class Recorder:
    def close(self, error=None):
        event("telemetry_close",
              error_type=None if error is None else type(error).__name__,
              error=None if error is None else str(error))
        if os.environ.get("WORKER_FIXTURE_MODE") == "runtime_error_with_close_failure":
            raise OSError("observer close failure")
def install(config, output_dir):
    event("telemetry_install", config=config, output_dir=str(output_dir))
    return Recorder()
''', encoding="utf-8")
            (package / "infer.py").write_text(PRELUDE + r'''
import sys
import __main__ as entry
import faulthandler
from lightx2v.entry_helper import IMPORT_MARKER
config_path = Path(sys.argv[sys.argv.index("--config_json") + 1])
config = json.loads(config_path.read_text(encoding="utf-8-sig"))
event("infer_begin", argv=sys.argv, isolated=sys.flags.isolated,
      helper=IMPORT_MARKER, source=str(Path(__file__).resolve()),
      fault_enabled=faulthandler.is_enabled())
faulthandler.dump_traceback(file=entry._FAULT_FD, all_threads=True)
if config["fixture_mode"] in ("runtime_error", "runtime_error_with_close_failure"):
    raise RuntimeError("original inference failure")
if config["fixture_mode"] == "interrupt":
    raise KeyboardInterrupt("original inference interruption")
if config["fixture_mode"] == "system_exit":
    raise SystemExit(7)
if config["fixture_mode"] == "system_exit_zero":
    raise SystemExit(0)
event("infer_return")
''', encoding="utf-8")
            # These would fail if the child used cwd/PYTHONPATH as module sources.
            (folder / "adaptive_streaming.py").write_text(
                "raise AssertionError('Imported an unrelated cwd adapter')\n",
                encoding="utf-8")
            hostile = root / "hostile Python path"
            hostile.mkdir()
            (hostile / "telemetry.py").write_text(
                "raise AssertionError('Imported an unrelated PYTHONPATH module')\n",
                encoding="utf-8")
            config = {"fixture_mode": mode,
                      "_app_resource_plan": {"dit_streaming": True,
                                             "text_encoder_streaming": True}}
            config_path = folder / "config.json"
            config_path.write_text(json.dumps(config), encoding="utf-8-sig")
            event_path = folder / "entry-events.jsonl"
            environment = os.environ.copy()
            environment.update(WORKER_ENTRY_EVENTS=str(event_path),
                               WORKER_FIXTURE_MODE=mode,
                               PYTHONPATH=str(hostile))
            prompt = "中文 prompt with spaces, commas\nand a second line"
            arguments = ["--model_cls", "qwen_image_21", "--config_json",
                         str(config_path), "--prompt", prompt]
            result = subprocess.run(
                [sys.executable, "-I", "-X", "utf8", str(backend / "worker.py"),
                 str(repository), *arguments],
                cwd=folder, env=environment, capture_output=True, text=True,
                encoding="utf-8", timeout=30)
            entries = ([json.loads(line) for line in event_path.read_text(
                encoding="utf-8").splitlines()] if event_path.exists() else [])
            return result, entries, {
                "config": config, "folder": str(folder),
                "argv": ["lightx2v.infer", *arguments],
                "source": str((package / "infer.py").resolve()),
                "startup": json.loads((folder / "worker-start.json").read_text()),
                "fault_log": (folder / "worker-fault.log").read_text(encoding="utf-8", errors="replace"),
                "python_error": ((folder / "worker-python-error.log").read_text(encoding="utf-8")
                                 if (folder / "worker-python-error.log").exists() else ""),
                "diagnostic_error": ((folder / "worker-diagnostics-error.log").read_text(encoding="utf-8")
                                     if (folder / "worker-diagnostics-error.log").exists() else "")}

    def assert_installed_in_order(self, entries, expected):
        self.assertGreaterEqual(len(entries), 5)
        self.assertEqual([entry['event'] for entry in entries[:5]],
                         ['checkpoint_policy_install','stream_install','lifecycle_install',
                          'telemetry_install','infer_begin'])
        self.assertTrue(entries[0]['fault_enabled'])
        self.assertFalse(entries[0]['torch_imported'])
        for event in entries[1:4]:
            self.assertEqual(event['config'], expected['config'])
        self.assertEqual(entries[3]['output_dir'], expected['folder'])
        self.assertEqual(entries[4]['isolated'], 1)
        self.assertEqual(entries[4]['argv'], expected['argv'])
        self.assertEqual(entries[4]['source'], expected['source'])
        self.assertTrue(entries[4]['fault_enabled'])
        self.assertEqual(entries[4]['helper'], 'selected repository helper')
        self.assertIn("Worker PID:", expected['fault_log'])
        self.assertIn("infer.py", expected['fault_log'])
        self.assertTrue(expected['startup']['faulthandler_enabled'])

    def test_isolated_worker_finds_backend_and_repository_then_closes(self):
        result, entries, expected = self.run_worker("normal")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assert_installed_in_order(entries, expected)
        self.assertEqual([entry["event"] for entry in entries[-2:]],
                         ["infer_return", "telemetry_close"])
        self.assertIsNone(entries[-1]["error_type"])
        self.assertIsNone(entries[-1]["error"])

    def test_inference_failure_is_closed_and_original_traceback_preserved(self):
        result, entries, expected = self.run_worker("runtime_error")
        self.assertNotEqual(result.returncode, 0)
        self.assert_installed_in_order(entries, expected)
        self.assertEqual(entries[-1]["event"], "telemetry_close")
        self.assertEqual(entries[-1]["error_type"], "RuntimeError")
        self.assertEqual(entries[-1]["error"], "original inference failure")
        self.assertIn("Traceback (most recent call last)", result.stderr)
        self.assertIn("RuntimeError: original inference failure", result.stderr)
        self.assertIn("infer.py", result.stderr)
        self.assertNotIn("ModuleNotFoundError", result.stderr)
        self.assertNotIn("infer_return", [entry["event"] for entry in entries])

    def test_base_exception_interruption_is_closed_and_reraised(self):
        result, entries, expected = self.run_worker("interrupt")
        self.assertNotEqual(result.returncode, 0)
        self.assert_installed_in_order(entries, expected)
        self.assertEqual(entries[-1]["error_type"], "KeyboardInterrupt")
        self.assertEqual(entries[-1]["error"], "original inference interruption")
        self.assertIn("KeyboardInterrupt: original inference interruption",
                      result.stderr)

    def test_nonzero_system_exit_keeps_exit_code_and_closes(self):
        result, entries, expected = self.run_worker("system_exit")
        self.assertEqual(result.returncode, 7)
        self.assert_installed_in_order(entries, expected)
        self.assertEqual(entries[-1]["error_type"], "SystemExit")
        self.assertEqual(entries[-1]["error"], "7")


    def test_bootstrap_failure_already_has_persistent_fault_and_python_logs(self):
        result, entries, expected = self.run_worker("bootstrap_error")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual([event["event"] for event in entries], ["checkpoint_policy_install"])
        self.assertTrue(entries[0]["fault_enabled"])
        self.assertIn("Worker PID:", expected["fault_log"])
        self.assertIn("original bootstrap failure", expected["python_error"])
        self.assertEqual(expected["startup"]["status"], "failed")

    def test_recorder_cleanup_cannot_replace_original_inference_error(self):
        result, entries, expected = self.run_worker("runtime_error_with_close_failure")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("RuntimeError: original inference failure", result.stderr)
        self.assertIn("observer close failure", expected["diagnostic_error"])
        self.assertEqual(expected["startup"]["error"], "original inference failure")

    def test_successful_system_exit_is_not_marked_failed(self):
        result, entries, expected = self.run_worker("system_exit_zero")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assert_installed_in_order(entries, expected)
        self.assertIsNone(entries[-1]["error_type"])
        self.assertEqual(expected["startup"]["status"], "completed")
        self.assertEqual(expected["python_error"], "")

if __name__ == "__main__":
    unittest.main()
