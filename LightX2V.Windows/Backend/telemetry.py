"""Stage and host resource telemetry without importing torch in the supervisor.

GPU allocator peaks are cumulative for this worker. Durations measure wall time,
not synchronized kernel execution. Windows commit is read with GetPerformanceInfo;
a pagefile proxy, when used, is explicitly labelled and never called exact commit.
"""
from contextlib import contextmanager
import atexit
import ctypes
import functools
import importlib
import json
import os
from pathlib import Path
import sys
import threading
import time
import uuid

_ACTIVE = None


def _windows_commit():
    if sys.platform != "win32":
        return {}
    class PerformanceInfo(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), *[(name, ctypes.c_size_t) for name in
            ("CommitTotal", "CommitLimit", "CommitPeak", "PhysicalTotal",
             "PhysicalAvailable", "SystemCache", "KernelTotal", "KernelPaged",
             "KernelNonpaged", "PageSize")], ("HandleCount", ctypes.c_ulong),
            ("ProcessCount", ctypes.c_ulong), ("ThreadCount", ctypes.c_ulong)]
    try:
        info = PerformanceInfo()
        info.cb = ctypes.sizeof(info)
        fn = ctypes.WinDLL("psapi", use_last_error=True).GetPerformanceInfo
        fn.argtypes = [ctypes.POINTER(PerformanceInfo), ctypes.c_ulong]
        fn.restype = ctypes.c_int
        if not fn(ctypes.byref(info), ctypes.sizeof(info)):
            raise ctypes.WinError(ctypes.get_last_error())
        page = int(info.PageSize)
        return {"system_commit_bytes": int(info.CommitTotal) * page,
                "system_commit_limit_bytes": int(info.CommitLimit) * page,
                "system_commit_peak_bytes": int(info.CommitPeak) * page,
                "system_commit_peak_scope": "system_since_boot",
                "system_commit_source": "GetPerformanceInfo",
                "system_commit_exact": True}
    except Exception as error:
        return {"system_commit_error": str(error)}


def sample_host(pid=None):
    """Sample one process and system RAM. This function never imports torch."""
    result = {"timestamp": time.time(), "pid": os.getpid() if pid is None else int(pid)}
    try:
        import psutil
    except Exception as error:
        result["host_sampler_error"] = str(error)
        result.update(_windows_commit())
        return result
    try:
        memory = psutil.virtual_memory()
        result.update(system_total_bytes=int(memory.total),
                      system_available_bytes=int(memory.available))
    except Exception as error:
        result["system_memory_error"] = str(error)
    try:
        process = psutil.Process(result["pid"])
        created = process.create_time() if callable(getattr(process, "create_time", None)) else None
        memory = process.memory_info()
        if callable(getattr(process, "is_running", None)) and not process.is_running():
            raise RuntimeError("Observed process exited or its PID was reused during sampling")
        if created is not None:
            result["process_create_time"] = created
        result["process_rss_bytes"] = int(memory.rss)
        private = getattr(memory, "private", None)
        if private is not None:
            result["process_private_bytes"] = int(private)
            result["process_private_source"] = "psutil.memory_info.private"
        if getattr(memory, "peak_wset", None) is not None:
            result["process_peak_rss_bytes"] = int(memory.peak_wset)
    except Exception as error:
        result["process_memory_error"] = str(error)
    commit = _windows_commit()
    result.update(commit)
    if sys.platform == "win32" and "system_commit_bytes" not in commit:
        try:
            swap = psutil.swap_memory()
            result.update(pagefile_proxy_used_bytes=int(swap.used),
                          pagefile_proxy_total_bytes=int(swap.total),
                          system_commit_source="psutil.swap_memory_pagefile_proxy",
                          system_commit_exact=False)
        except Exception as error:
            result["pagefile_proxy_error"] = str(error)
    return result


def _gpu_snapshot(torch_module):
    cuda = getattr(torch_module, "cuda", None)
    if cuda is None:
        return {"available": False, "reason": "No CUDA-compatible device API"}
    try:
        if not cuda.is_available():
            return {"available": False}
        if hasattr(cuda, "is_initialized") and not cuda.is_initialized():
            return {"available": True, "initialized": False}
        device = cuda.current_device()
        free, total = cuda.mem_get_info(device)
        return {"available": True, "initialized": True, "device": int(device),
                "allocated_bytes": int(cuda.memory_allocated(device)),
                "reserved_bytes": int(cuda.memory_reserved(device)),
                "max_allocated_bytes": int(cuda.max_memory_allocated(device)),
                "max_reserved_bytes": int(cuda.max_memory_reserved(device)),
                "device_free_bytes": int(free), "device_total_bytes": int(total),
                "peak_scope": "worker_cumulative"}
    except Exception as error:
        return {"available": True, "sampling_error": str(error)}


def _atomic_json(path, value):
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2, default=str),
                         encoding="utf-8")
    os.replace(temporary, path)


class _Journal:
    def __init__(self, output_dir, filename, summary_name, run_id=None):
        self.directory = Path(output_dir)
        self.directory.mkdir(parents=True, exist_ok=True)
        self.path = self.directory / filename
        self.summary_path = self.directory / summary_name
        self.lock = threading.RLock()
        self.started = time.monotonic()
        self.session_id = uuid.uuid4().hex
        self.run_id = run_id or os.environ.get("LIGHTX2V_TASK_RUN_ID") or uuid.uuid4().hex
        self.task_id = os.environ.get("LIGHTX2V_TASK_ID") or (
            self.directory.parent.name if self.directory.name == "enhancement" else self.directory.name)
        self._event_sequence = 0
        self.summary = {"schema": 2, "status": "running", "pid": os.getpid(),
                        "pid_scope": "producer", "producer_pid": os.getpid(),
                        "observed_pid": os.getpid(), "observed_pids": [os.getpid()],
                        "task_id": self.task_id, "run_id": self.run_id,
                        "session_id": self.session_id,
                        "metrics_scope": "successfully_written_events_in_this_session",
                        "started_at": time.time(), "sample_count": 0,
                        "peaks": {}, "minimums": {}, "historical_peaks": {},
                        "historical_peak_scopes": {}, "processes": {}, "errors": []}
        self.closed = False

    def _observe(self, snapshot, prefix=""):
        for name, value in snapshot.items():
            if isinstance(value, dict):
                self._observe(value, prefix + name + ".")
            elif isinstance(value, (int, float)) and not isinstance(value, bool) and name.endswith("_bytes"):
                key = prefix + name
                if name in ("system_commit_peak_bytes", "process_peak_rss_bytes"):
                    scope = "system_since_boot" if name == "system_commit_peak_bytes" else "observed_process_lifetime"
                    self.summary["historical_peaks"][key] = max(
                        self.summary["historical_peaks"].get(key, 0), value)
                    self.summary["historical_peak_scopes"][key] = scope
                elif "available" in name or "free" in name:
                    self.summary["minimums"][key] = min(
                        self.summary["minimums"].get(key, value), value)
                else:
                    self.summary["peaks"][key] = max(self.summary["peaks"].get(key, 0), value)

    def record(self, event, **fields):
        with self.lock:
            if self.closed:
                return
            sequence = self._event_sequence + 1
            item = {**fields, "schema": 2, "event": event, "timestamp": time.time(),
                    "elapsed_seconds": time.monotonic() - self.started,
                    "sequence": sequence, "session_id": self.session_id,
                    "run_id": self.run_id, "task_id": self.task_id,
                    "producer_pid": os.getpid(),
                    "observed_pid": self.summary["observed_pid"]}
            # Aggregate only after the event was written and flushed. A failed
            # append must never contribute an otherwise unobservable peak.
            with self.path.open("a", encoding="utf-8") as output:
                output.write(json.dumps(item, ensure_ascii=False, default=str) + "\n")
                output.flush()
            self._event_sequence = sequence
            self._observe(fields)
            self.summary["sample_count"] += 1
            self.summary["last_sequence"] = sequence
            self.summary["last_event"] = event
            self.summary["last_timestamp"] = item["timestamp"]
            host = fields.get("host", {})
            if isinstance(host, dict) and host.get("pid") is not None and not host.get("process_identity_mismatch"):
                identity = str(host["pid"]) + ":" + str(host.get("process_create_time", "unknown"))
                process = self.summary["processes"].setdefault(identity,
                    {"pid": host["pid"], "create_time": host.get("process_create_time"), "peaks": {}})
                for key, value in host.items():
                    if key.startswith("process_") and key.endswith("_bytes") and isinstance(value, (int, float)):
                        process["peaks"][key] = max(process["peaks"].get(key, 0), value)
            if event == "stage_begin":
                counts = self.summary.setdefault("stage_counts", {})
                counts[fields["stage"]] = counts.get(fields["stage"], 0) + 1
            if event in ("stage_error", "guard_trigger", "instrumentation_error"):
                self.summary["errors"].append({key: value for key, value in item.items()
                    if key not in ("host", "gpu")})
                self.summary["errors"] = self.summary["errors"][-50:]
            _atomic_json(self.summary_path, self.summary)

    def safe_record(self, event, **fields):
        try:
            self.record(event, **fields)
        except Exception as error:
            # Observability must never replace the original inference exception.
            self.summary["diagnostic_write_errors"] = self.summary.get("diagnostic_write_errors", 0) + 1
            self.summary["last_diagnostic_write_error"] = str(error)

    def close(self, error=None):
        with self.lock:
            if self.closed:
                return
            self.summary.update(status="failed" if error is not None else "completed",
                finished_at=time.time(), duration_seconds=time.monotonic() - self.started)
            if error is not None:
                self.summary["error"] = str(error)
                self.summary["error_type"] = type(error).__name__
            self.safe_record("session_end", error=None if error is None else str(error))
            self.closed = True


class StageRecorder(_Journal):
    def __init__(self, output_dir, torch_module, config=None):
        super().__init__(output_dir, "telemetry.jsonl", "resource-usage.json")
        self.torch = torch_module
        self.sequence = 0
        self.patches = []
        self.local = threading.local()
        self.summary["duration_definition"] = "wall_time_without_forced_gpu_synchronization"
        self.summary["gpu_peak_scope"] = "worker_cumulative"
        self.summary["resource_plan"] = (config or {}).get("_app_resource_plan", {})
        self.summary["stage_counts"] = {}
        self.video_memory_device = self.summary['resource_plan'].get('runtime',{}).get('device')
        if self.video_memory_device:
            self.summary['windows_video_memory_begin'] = self.video_memory_snapshot()
        self.safe_record("session_begin", **self.snapshot())

    def video_memory_snapshot(self):
        try:
            from windows_memory import query_video_memory
            return query_video_memory(device_name=self.video_memory_device)
        except Exception as error:
            return {'available':False,'sampling_error':str(error),'scope':'current_process'}

    def snapshot(self):
        try:
            host = sample_host()
        except Exception as error:
            host = {"sampling_error": str(error)}
        return {"host": host, "gpu": _gpu_snapshot(self.torch)}

    @contextmanager
    def stage(self, name):
        stack = getattr(self.local, "stack", [])
        self.local.stack = stack
        with self.lock:
            self.sequence += 1
            sequence = self.sequence
        parent = stack[-1] if stack else None
        stack.append(sequence)
        started = time.monotonic()
        self.safe_record("stage_begin", stage=name, stage_id=sequence,
            parent_stage_id=parent, **self.snapshot())
        try:
            yield
        except BaseException as error:
            self.safe_record("stage_error", stage=name, stage_id=sequence,
                parent_stage_id=parent, duration_seconds=time.monotonic() - started,
                error=str(error), error_type=type(error).__name__,
                **self.snapshot())
            raise
        else:
            self.safe_record("stage_end", stage=name, stage_id=sequence,
                parent_stage_id=parent, duration_seconds=time.monotonic() - started,
                **self.snapshot())
        finally:
            stack.pop()

    def instrument(self, runner_class):
        names = ("init_modules", "load_model", "load_transformer",
                 "load_text_encoder", "load_vae", "run_input_encoder",
                 "run_text_encoder", "run_vae_encoder", "init_run", "run_main",
                 "run_vae_decoder", "process_images_after_vae_decoder", "run_pipeline")
        for name in names:
            original = getattr(runner_class, name, None)
            if not callable(original):
                continue
            owned = name in runner_class.__dict__
            @functools.wraps(original)
            def wrapped(instance, *args, _method=original, _stage=name, **kwargs):
                with self.stage(_stage):
                    return _method(instance, *args, **kwargs)
            setattr(runner_class, name, wrapped)
            self.patches.append((runner_class, name, original, wrapped, owned))

    def close(self, error=None):
        if self.closed:
            return
        if error is None and any(item.get("event") == "stage_error"
                                 for item in self.summary["errors"]):
            error = RuntimeError("One or more inference stages failed; see telemetry.jsonl")
        if self.video_memory_device:
            self.summary['windows_video_memory_end'] = self.video_memory_snapshot()
        self.safe_record("worker_final_sample", **self.snapshot())
        super().close(error)
        for cls, name, original, wrapped, owned in reversed(self.patches):
            if getattr(cls, name, None) is wrapped:
                if owned:
                    setattr(cls, name, original)
                else:
                    delattr(cls, name)
        self.patches.clear()


def install(config, output_dir):
    """Instrument Qwen runner in this worker after device/streaming setup.

    Returns an idempotent recorder with close(error=None). No extra process or
    per-block/step synchronization is used. Import errors are recorded and raised.
    """
    global _ACTIVE
    if _ACTIVE is not None and not _ACTIVE.closed:
        if _ACTIVE.directory.resolve() == Path(output_dir).resolve():
            return _ACTIVE
        _ACTIVE.close()
    torch_module = importlib.import_module("torch")
    recorder = StageRecorder(output_dir, torch_module, config)
    try:
        module = importlib.import_module(
            "lightx2v.models.runners.qwen_image_21.qwen_image_21_runner")
        recorder.instrument(module.QwenImage21Runner)
    except BaseException as error:
        recorder.safe_record("instrumentation_error", error=str(error),
                             error_type=type(error).__name__)
        recorder.close(error)
        raise
    _ACTIVE = recorder
    atexit.register(recorder.close)
    return recorder


class ResourceMonitor(_Journal):
    """Sample an owned child without torch; guards are recorded, never kill it.

    Set pid or use set_pid() when moving from PE to Qwen. on_guard(event) is
    called once after guard_consecutive low-memory samples. Default thresholds
    are disabled so the supervisor retains its termination policy.
    """
    def __init__(self, output_dir, pid=None, interval=1.0, on_guard=None,
                 min_available_bytes=0, min_commit_headroom_bytes=0,
                 guard_consecutive=3, run_id=None):
        super().__init__(output_dir, "host-telemetry.jsonl", "host-resource-usage.json", run_id=run_id)
        self.pid = os.getpid() if pid is None else int(pid)
        self._expected_create_time = None
        self.summary["observed_pid"] = self.pid
        self.summary["observed_pids"] = [self.pid]
        self.interval = max(0.1, float(interval))
        self.on_guard = on_guard
        self.min_available_bytes = max(0, int(min_available_bytes))
        self.min_commit_headroom_bytes = max(0, int(min_commit_headroom_bytes))
        self.guard_consecutive = max(1, int(guard_consecutive))
        self._low_count = 0
        self._guarded = False
        self._stop = threading.Event()
        self._thread = None

    def set_pid(self, pid):
        self.pid = int(pid)
        self._expected_create_time = None
        self.summary["observed_pid"] = self.pid
        if self.pid not in self.summary["observed_pids"]:
            self.summary["observed_pids"].append(self.pid)
        self._low_count = 0

    def sample(self):
        host = sample_host(self.pid)
        actual_pid = host.get("pid", self.pid)
        created = host.get("process_create_time")
        if actual_pid != self.pid or (created is not None and self._expected_create_time is not None
                                     and created != self._expected_create_time):
            host = dict(host)
            for name in tuple(host):
                if name.startswith("process_") and name.endswith("_bytes"):
                    host.pop(name)
            host["process_identity_mismatch"] = {
                "expected_pid": self.pid, "sample_pid": actual_pid,
                "expected_create_time": self._expected_create_time,
                "sample_create_time": created}
        elif created is not None:
            self._expected_create_time = created
            self.summary["observed_process_create_time"] = created
        self.safe_record("host_sample", host=host)
        reasons = []
        available = host.get("system_available_bytes")
        if self.min_available_bytes and available is not None and available < self.min_available_bytes:
            reasons.append("physical_memory_reserve")
        commit, limit = host.get("system_commit_bytes"), host.get("system_commit_limit_bytes")
        if self.min_commit_headroom_bytes and commit is not None and limit is not None and limit - commit < self.min_commit_headroom_bytes:
            reasons.append("commit_headroom")
        self._low_count = self._low_count + 1 if reasons else 0
        if self._low_count >= self.guard_consecutive and not self._guarded:
            self._guarded = True
            self.record_guard(",".join(reasons), host=host,
                consecutive_samples=self._low_count)
        return host

    def record_guard(self, reason, **fields):
        event = {"reason": str(reason), **fields}
        self.safe_record("guard_trigger", **event)
        if self.on_guard is not None:
            try:
                self.on_guard(event)
            except Exception as error:
                self.safe_record("observer_error", error=str(error))

    def start(self):
        if self._thread is not None or self.closed:
            return self
        self.sample()
        def work():
            while not self._stop.wait(self.interval):
                try:
                    self.sample()
                except Exception as error:
                    self.safe_record("sampling_error", error=str(error))
        self._thread = threading.Thread(target=work, name="LightX2V-resource-monitor",
                                        daemon=True)
        self._thread.start()
        return self

    def stop(self, error=None):
        self._stop.set()
        if self._thread is not None and self._thread is not threading.current_thread():
            self._thread.join(timeout=self.interval + 1)
        try:
            self.sample()
        except Exception:
            pass
        self.close(error)

    def __enter__(self):
        return self.start()

    def __exit__(self, kind, error, traceback):
        self.stop(error)
        return False
