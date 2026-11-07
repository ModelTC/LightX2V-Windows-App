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
def install(config, output_dir):
    event("telemetry_install", config=config, output_dir=str(output_dir))
    return Recorder()
''', encoding="utf-8")
            (package / "infer.py").write_text(PRELUDE + r'''
import sys
from lightx2v.entry_helper import IMPORT_MARKER
config_path = Path(sys.argv[sys.argv.index("--config_json") + 1])
config = json.loads(config_path.read_text(encoding="utf-8-sig"))
event("infer_begin", argv=sys.argv, isolated=sys.flags.isolated,
      helper=IMPORT_MARKER, source=str(Path(__file__).resolve()))
if config["fixture_mode"] == "runtime_error":
    raise RuntimeError("original inference failure")
if config["fixture_mode"] == "interrupt":
    raise KeyboardInterrupt("original inference interruption")
if config["fixture_mode"] == "system_exit":
    raise SystemExit(7)
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
                "source": str((package / "infer.py").resolve())}

    def assert_installed_in_order(self, entries, expected):
        self.assertGreaterEqual(len(entries), 4)
        self.assertEqual([entry['event'] for entry in entries[:4]],
                         ['stream_install','lifecycle_install','telemetry_install','infer_begin'])
        for entry in entries[:3]:
            self.assertEqual(entry['config'],expected['config'])
        self.assertEqual(entries[2]['output_dir'],expected['folder'])
        self.assertEqual(entries[3]['isolated'],1)
        self.assertEqual(entries[3]['argv'],expected['argv'])
        self.assertEqual(entries[3]['source'],expected['source'])
        self.assertEqual(entries[3]['helper'],'selected repository helper')

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


if __name__ == "__main__":
    unittest.main()
