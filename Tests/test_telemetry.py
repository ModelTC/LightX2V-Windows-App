"""Tests for real host readings, stage failure preservation and bounded monitoring."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
PATH = ROOT / "LightX2V.Windows" / "Backend" / "telemetry.py"
spec = importlib.util.spec_from_file_location("app_telemetry_test", PATH)
telemetry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(telemetry)


def fake_torch():
    return types.SimpleNamespace(cuda=types.SimpleNamespace(
        is_available=lambda: True, is_initialized=lambda: True,
        current_device=lambda: 0, mem_get_info=lambda device: (123, 456),
        memory_allocated=lambda device: 12, memory_reserved=lambda device: 24,
        max_memory_allocated=lambda device: 36, max_memory_reserved=lambda device: 48))


def events(folder, filename="telemetry.jsonl"):
    return [json.loads(line) for line in (Path(folder) / filename).read_text().splitlines()]


HAVE_PSUTIL = importlib.util.find_spec('psutil') is not None


class TelemetryTests(unittest.TestCase):
    def test_supervisor_module_import_uses_standard_library_only(self):
        script = ("import importlib.util,sys; s=importlib.util.spec_from_file_location('t',"
                  + repr(str(PATH)) + "); m=importlib.util.module_from_spec(s); "
                  "s.loader.exec_module(m); assert 'torch' not in sys.modules; "
                  "assert 'psutil' not in sys.modules")
        result = subprocess.run([sys.executable, "-S", "-X", "utf8", "-c", script],
                                text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_optional_sampler_returns_explicit_error(self):
        with patch.dict(sys.modules, {"psutil": None}), patch.object(
                telemetry, "_windows_commit", return_value={}):
            result = telemetry.sample_host()
        self.assertIn("host_sampler_error", result)
        self.assertNotIn("process_rss_bytes", result)

    @unittest.skipUnless(HAVE_PSUTIL, 'Real host sampling requires optional psutil')
    def test_host_sampling_never_imports_torch(self):
        script = ("import importlib.util,sys; s=importlib.util.spec_from_file_location('t',"
                  + repr(str(PATH)) + "); m=importlib.util.module_from_spec(s); "
                  "s.loader.exec_module(m); assert 'torch' not in sys.modules; "
                  "v=m.sample_host(); assert v['process_rss_bytes']>0; "
                  "assert 'torch' not in sys.modules")
        child = subprocess.run([sys.executable, "-X", "utf8", "-c", script],
                               text=True, capture_output=True)
        self.assertEqual(child.returncode, 0, child.stderr)

    @unittest.skipUnless(HAVE_PSUTIL, 'Real host sampling requires optional psutil')
    def test_real_windows_commit_and_process_memory(self):
        result = telemetry.sample_host()
        self.assertGreater(result["process_rss_bytes"], 0)
        self.assertGreater(result["system_available_bytes"], 0)
        if sys.platform == "win32":
            self.assertEqual(result["system_commit_source"], "GetPerformanceInfo")
            self.assertTrue(result["system_commit_exact"])
            self.assertGreater(result["system_commit_limit_bytes"], result["system_commit_bytes"])
            self.assertGreater(result["process_private_bytes"], 0)

    def test_failure_is_recorded_and_original_exception_survives(self):
        with tempfile.TemporaryDirectory() as folder:
            recorder = telemetry.StageRecorder(folder, fake_torch())
            original = ValueError("original stage failure")
            with self.assertRaises(ValueError) as captured:
                with recorder.stage("run_main"):
                    raise original
            self.assertIs(captured.exception, original)
            recorder.close(original)
            log = events(folder)
            failure = next(item for item in log if item["event"] == "stage_error")
            self.assertEqual(failure["error_type"], "ValueError")
            self.assertEqual(failure["gpu"]["max_allocated_bytes"], 36)
            summary = json.loads((Path(folder) / "resource-usage.json").read_text())
            self.assertEqual(summary["status"], "failed")
            self.assertEqual(summary["peaks"]["gpu.max_allocated_bytes"], 36)

    def test_instrumentation_preserves_nested_calls_and_restores_inheritance(self):
        class Base:
            def init_modules(self):
                return self.load_model()
        class Runner(Base):
            def load_model(self):
                return 42
        with tempfile.TemporaryDirectory() as folder:
            original = Runner.load_model
            recorder = telemetry.StageRecorder(folder, fake_torch())
            recorder.instrument(Runner)
            self.assertEqual(Runner().init_modules(), 42)
            recorder.close()
            self.assertIs(Runner.load_model, original)
            self.assertNotIn("init_modules", Runner.__dict__)
            log = events(folder)
            begins = [item for item in log if item["event"] == "stage_begin"]
            self.assertEqual([item["stage"] for item in begins], ["init_modules", "load_model"])
            self.assertEqual(begins[1]["parent_stage_id"], begins[0]["stage_id"])

    def test_logging_failure_does_not_mask_inference_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            recorder = telemetry.StageRecorder(folder, fake_torch())
            with patch.object(recorder, "record", side_effect=OSError("disk failure")):
                with self.assertRaisesRegex(RuntimeError, "inference"):
                    with recorder.stage("run_main"):
                        raise RuntimeError("inference")
            recorder.close()

    def test_snapshot_failure_does_not_mask_stage_result_or_error(self):
        with tempfile.TemporaryDirectory() as folder:
            recorder = telemetry.StageRecorder(folder, fake_torch())
            with patch.object(telemetry, "sample_host", side_effect=OSError("sampler unavailable")):
                with recorder.stage("successful"):
                    pass
                with self.assertRaisesRegex(ValueError, "original"):
                    with recorder.stage("failed"):
                        raise ValueError("original")
                recorder.close()
            self.assertTrue(any(item["event"] == "stage_error" for item in events(folder)))

    @unittest.skipUnless(HAVE_PSUTIL, 'Real host sampling requires optional psutil')
    def test_missing_process_still_records_system_usage(self):
        result = telemetry.sample_host(2147483647)
        self.assertIn("process_memory_error", result)
        self.assertGreater(result["system_available_bytes"], 0)

    def test_guard_requires_consecutive_samples_and_does_not_kill_process(self):
        low = {"system_available_bytes": 10, "system_commit_bytes": 90,
               "system_commit_limit_bytes": 100, "process_rss_bytes": 5}
        good = dict(low, system_available_bytes=90, system_commit_bytes=10)
        with tempfile.TemporaryDirectory() as folder:
            calls = []
            monitor = telemetry.ResourceMonitor(folder, pid=os.getpid(),
                min_available_bytes=20, min_commit_headroom_bytes=20,
                guard_consecutive=2, on_guard=calls.append)
            with patch.object(telemetry, "sample_host", side_effect=[low, good, low, low, low]):
                for _ in range(5):
                    monitor.sample()
            monitor.close()
            self.assertEqual(len(calls), 1)
            self.assertIn("commit_headroom", calls[0]["reason"])
            log = events(folder, "host-telemetry.jsonl")
            self.assertEqual(sum(item["event"] == "guard_trigger" for item in log), 1)
            self.assertTrue(os.getpid())

    def test_monitor_stop_flushes_partial_summary(self):
        with tempfile.TemporaryDirectory() as folder:
            host = {"process_rss_bytes": 123, "system_available_bytes": 456}
            with patch.object(telemetry, "sample_host", return_value=host):
                monitor = telemetry.ResourceMonitor(folder, interval=0.1).start()
                monitor.stop(RuntimeError("cancelled by supervisor"))
                monitor.stop()
            summary = json.loads((Path(folder) / "host-resource-usage.json").read_text())
            self.assertEqual(summary["status"], "failed")
            self.assertIn("process_rss_bytes", str(summary["peaks"]))
            self.assertFalse(monitor._thread.is_alive())

    def test_install_is_idempotent_and_hooks_real_class_only_once(self):
        class Runner:
            def init_modules(self):
                return "loaded"
        module = types.SimpleNamespace(QwenImage21Runner=Runner)
        def imports(name):
            return fake_torch() if name == "torch" else module
        with tempfile.TemporaryDirectory() as folder:
            with patch.object(telemetry.importlib, "import_module", side_effect=imports):
                recorder = telemetry.install({"_app_resource_plan": {"dit_streaming": True}}, folder)
                self.assertIs(telemetry.install({}, folder), recorder)
                self.assertEqual(Runner().init_modules(), "loaded")
                recorder.close()
            summary = json.loads((Path(folder) / "resource-usage.json").read_text())
            self.assertTrue(summary["resource_plan"]["dit_streaming"])
            self.assertEqual(summary["stage_counts"]["init_modules"], 1)

    def test_proxy_is_never_claimed_to_be_exact_commit(self):
        fake_psutil = types.SimpleNamespace(
            virtual_memory=lambda: types.SimpleNamespace(total=100, available=50),
            Process=lambda pid: types.SimpleNamespace(
                memory_info=lambda: types.SimpleNamespace(rss=10, private=20)),
            swap_memory=lambda: types.SimpleNamespace(used=30, total=200))
        with patch.dict(sys.modules, {"psutil": fake_psutil}), patch.object(
                telemetry, "_windows_commit", return_value={}), patch.object(
                telemetry.sys, "platform", "win32"):
            result = telemetry.sample_host()
        self.assertFalse(result["system_commit_exact"])
        self.assertNotIn("system_commit_bytes", result)
        self.assertIn("pagefile_proxy", result["system_commit_source"])


if __name__ == "__main__":
    unittest.main()


class TelemetryOwnershipTests(unittest.TestCase):
    def test_producer_and_observed_process_are_explicit_and_journal_matches_summary(self):
        with tempfile.TemporaryDirectory() as folder:
            monitor = telemetry.ResourceMonitor(folder, pid=29212, run_id="task-run-fixture")
            sample = {"pid": 29212, "process_create_time": 100.0,
                      "process_private_bytes": 10, "system_commit_bytes": 20}
            with patch.object(telemetry, "sample_host", return_value=sample):
                monitor.sample()
            monitor.close()
            summary = json.loads((Path(folder) / "host-resource-usage.json").read_text())
            rows = events(folder, "host-telemetry.jsonl")
            self.assertEqual(summary["producer_pid"], os.getpid())
            self.assertEqual(summary["pid_scope"], "producer")
            self.assertEqual(summary["observed_pid"], 29212)
            self.assertEqual(summary["run_id"], "task-run-fixture")
            self.assertEqual(summary["sample_count"], len(rows))
            self.assertEqual(summary["last_sequence"], rows[-1]["sequence"])
            for row in rows:
                self.assertEqual(row["session_id"], summary["session_id"])
                self.assertEqual(row["observed_pid"], 29212)
                self.assertEqual(row["producer_pid"], os.getpid())

    def test_failed_journal_append_cannot_pollute_summary_peak(self):
        with tempfile.TemporaryDirectory() as folder:
            journal = telemetry._Journal(folder, "events.jsonl", "summary.json")
            journal.record("baseline", host={"process_private_bytes": 10})
            original_open = Path.open
            def failing_open(path, *args, **kwargs):
                if path == journal.path:
                    raise OSError("simulated append failure")
                return original_open(path, *args, **kwargs)
            with patch.object(Path, "open", new=failing_open):
                journal.safe_record("not_written", host={"process_private_bytes": 999999})
            journal.record("after", host={"process_private_bytes": 20})
            journal.close()
            summary = json.loads(journal.summary_path.read_text())
            rows = events(folder, "events.jsonl")
            self.assertEqual(summary["peaks"]["host.process_private_bytes"], 20)
            self.assertEqual(summary["sample_count"], len(rows))
            self.assertEqual(summary["diagnostic_write_errors"], 1)
            self.assertNotIn("not_written", [item["event"] for item in rows])

    def test_reused_directory_starts_new_session_without_old_peak(self):
        with tempfile.TemporaryDirectory() as folder:
            first = telemetry._Journal(folder, "events.jsonl", "summary.json")
            first.record("sample", host={"process_private_bytes": 999})
            first.close()
            second = telemetry._Journal(folder, "events.jsonl", "summary.json")
            second.record("sample", host={"process_private_bytes": 10})
            second.close()
            rows = events(folder, "events.jsonl")
            summary = json.loads(second.summary_path.read_text())
            self.assertNotEqual(first.session_id, second.session_id)
            current = [row for row in rows if row["session_id"] == summary["session_id"]]
            self.assertEqual(summary["sample_count"], len(current))
            self.assertEqual(summary["peaks"]["host.process_private_bytes"], 10)
            self.assertGreater(len(rows), len(current))

    def test_system_history_peak_is_not_an_observed_task_commit_peak(self):
        with tempfile.TemporaryDirectory() as folder:
            journal = telemetry._Journal(folder, "events.jsonl", "summary.json")
            journal.record("sample", host={"system_commit_bytes": 20,
                                          "system_commit_peak_bytes": 900})
            journal.close()
            summary = json.loads(journal.summary_path.read_text())
            self.assertEqual(summary["peaks"]["host.system_commit_bytes"], 20)
            self.assertNotIn("host.system_commit_peak_bytes", summary["peaks"])
            self.assertEqual(summary["historical_peaks"]["host.system_commit_peak_bytes"], 900)
            self.assertEqual(summary["historical_peak_scopes"]["host.system_commit_peak_bytes"],
                             "system_since_boot")

    def test_pid_reuse_sample_does_not_contribute_new_process_memory(self):
        with tempfile.TemporaryDirectory() as folder:
            monitor = telemetry.ResourceMonitor(folder, pid=29212)
            samples = [
                {"pid": 29212, "process_create_time": 100.0, "process_private_bytes": 10},
                {"pid": 29212, "process_create_time": 200.0, "process_private_bytes": 999999},
            ]
            with patch.object(telemetry, "sample_host", side_effect=samples):
                monitor.sample()
                monitor.sample()
            monitor.close()
            summary = json.loads(monitor.summary_path.read_text())
            rows = events(folder, "host-telemetry.jsonl")
            self.assertEqual(summary["peaks"]["host.process_private_bytes"], 10)
            mismatched = [row["host"] for row in rows if row.get("host", {}).get("process_identity_mismatch")]
            self.assertEqual(len(mismatched), 1)
            self.assertNotIn("process_private_bytes", mismatched[0])
            self.assertNotIn("29212:200.0", summary["processes"])

    def test_summary_recovers_after_atomic_summary_write_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            journal = telemetry._Journal(folder, "events.jsonl", "summary.json")
            with patch.object(telemetry, "_atomic_json", side_effect=OSError("summary busy")):
                journal.safe_record("persisted_sample", host={"process_private_bytes": 10})
            journal.record("after", host={"process_private_bytes": 30})
            journal.close()
            summary = json.loads(journal.summary_path.read_text())
            rows = events(folder, "events.jsonl")
            self.assertEqual(summary["sample_count"], len(rows))
            self.assertEqual(summary["last_sequence"], len(rows))
            replay = max(row.get("host", {}).get("process_private_bytes", 0) for row in rows)
            self.assertEqual(summary["peaks"]["host.process_private_bytes"], replay)
