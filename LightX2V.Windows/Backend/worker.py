"""Run the selected runtime with automatic resource adapters and in-process metrics."""
import json
from pathlib import Path
import runpy
import sys

sys.path.insert(0,str(Path(__file__).resolve().parent))
repo = Path(sys.argv[1])
sys.path.insert(0, str(repo))
arguments = sys.argv[2:]
config_path = Path(arguments[arguments.index('--config_json') + 1])
config = json.loads(config_path.read_text(encoding='utf-8-sig'))
from adaptive_streaming import install as install_streaming, install_request_lifecycle
install_streaming(config)
install_request_lifecycle(config)
from telemetry import install as install_telemetry
recorder = install_telemetry(config, config_path.parent)
sys.argv = ['lightx2v.infer', *arguments]
try:
    runpy.run_module('lightx2v.infer', run_name='__main__')
except BaseException as error:
    recorder.close(error=error)
    raise
else:
    recorder.close()
