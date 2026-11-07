"""Run one task with early native-fault diagnostics and isolated resource adapters."""
import faulthandler
import json
import os
from pathlib import Path
import runpy
import sys
import time
import traceback

sys.path.insert(0, str(Path(__file__).resolve().parent))
_FAULT_FD = None


def _write_json(path, value):
    try:
        temporary = path.with_name(path.name + ".tmp")
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2, default=str),
                             encoding="utf-8")
        os.replace(temporary, path)
    except Exception:
        pass


def _diagnostic_error(folder, error):
    try:
        with (folder / "worker-diagnostics-error.log").open("a", encoding="utf-8") as output:
            output.write(str(error) + "\n")
    except Exception:
        pass


def enable_fault_logging(folder):
    """Keep an OS file descriptor alive until process exit, including native faults."""
    global _FAULT_FD
    path = folder / "worker-fault.log"
    result = {"worker_pid": os.getpid(), "parent_pid": os.getppid(),
              "started_at": time.time(), "task_id": folder.name,
              "run_id": os.environ.get("LIGHTX2V_TASK_RUN_ID")}
    try:
        folder.mkdir(parents=True, exist_ok=True)
        _FAULT_FD = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
        header = ("Worker PID: " + str(os.getpid()) + "; parent PID: " + str(os.getppid())
                  + "; started_at: " + str(result["started_at"]) + "\n")
        os.write(_FAULT_FD, header.encode("utf-8"))
        faulthandler.enable(file=_FAULT_FD, all_threads=True)
        result.update(faulthandler_enabled=True, faulthandler_log=str(path))
    except Exception as error:
        result.update(faulthandler_enabled=False, faulthandler_error=str(error))
        try:
            faulthandler.enable(all_threads=True)
            result["faulthandler_stderr_fallback"] = True
        except Exception:
            pass
    return result


def main():
    repo = Path(sys.argv[1])
    sys.path.insert(0, str(repo))
    arguments = sys.argv[2:]
    config_path = Path(arguments[arguments.index("--config_json") + 1])
    folder = config_path.parent
    startup = enable_fault_logging(folder)
    startup["status"] = "starting"
    _write_json(folder / "worker-start.json", startup)
    recorder = None
    try:
        # Install before any adapters, LightX2V, transformers or torch import.
        from checkpoint_io import install_windows_checkpoint_io
        startup["checkpoint_io"] = install_windows_checkpoint_io()
        _write_json(folder / "worker-start.json", startup)
        config = json.loads(config_path.read_text(encoding="utf-8-sig"))
        from adaptive_streaming import install as install_streaming, install_request_lifecycle
        install_streaming(config)
        install_request_lifecycle(config)
        from telemetry import install as install_telemetry
        recorder = install_telemetry(config, folder)
        startup["status"] = "running"
        _write_json(folder / "worker-start.json", startup)
        sys.argv = ["lightx2v.infer", *arguments]
        runpy.run_module("lightx2v.infer", run_name="__main__")
    except BaseException as error:
        failure = None if isinstance(error, SystemExit) and error.code in (None, 0) else error
        if recorder is not None:
            try:
                recorder.close(error=failure)
            except Exception as diagnostic_error:
                _diagnostic_error(folder, diagnostic_error)
        startup.update(status="failed" if failure is not None else "completed",
                       finished_at=time.time())
        if failure is not None:
            startup.update(error=str(error), error_type=type(error).__name__)
            try:
                with (folder / "worker-python-error.log").open("a", encoding="utf-8") as output:
                    traceback.print_exc(file=output)
            except Exception:
                pass
        _write_json(folder / "worker-start.json", startup)
        raise
    else:
        if recorder is not None:
            try:
                recorder.close()
            except Exception as diagnostic_error:
                _diagnostic_error(folder, diagnostic_error)
        startup.update(status="completed", finished_at=time.time())
        _write_json(folder / "worker-start.json", startup)


if __name__ == "__main__":
    main()
