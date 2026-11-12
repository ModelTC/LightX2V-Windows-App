"""Full task logs and concurrent supervisor events use real streams, without a GPU."""
import importlib.util
import io
import json
from pathlib import Path
import threading
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('run_log_bridge', Path(__file__).resolve().parents[1]/'LightX2V.Windows/Backend/bridge.py')
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)

class RunLogTests(unittest.TestCase):
    def test_concurrent_diagnostics_preserve_protocol_and_full_log(self):
        class SlowOutput(io.StringIO):
            def write(self, value):
                # Release the GIL between print's text and newline writes.
                result=super().write(value)
                time.sleep(.0001)
                return result
        output=SlowOutput()
        saved=io.StringIO()
        with patch.object(bridge,'RUN_LOG',saved), patch.object(bridge.sys,'stdout',output):
            workers=[threading.Thread(target=lambda index=i:[bridge.emit('log',message=f'{index}:界:'+('x'*4096)) for _ in range(40)]) for i in range(4)]
            for worker in workers: worker.start()
            for worker in workers: worker.join()
        events=[json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(len(events),160)
        self.assertTrue(all(e['type']=='log' and e['protocol']==1 for e in events))
        self.assertEqual(saved.getvalue().splitlines(),[e['message'] for e in events])
        self.assertGreater(len(saved.getvalue().encode('utf-8')),90000)

    def test_status_and_model_output_are_kept_in_full_log(self):
        output,saved=io.StringIO(),io.StringIO()
        with patch.object(bridge,'RUN_LOG',saved), patch.object(bridge.sys,'stdout',output):
            bridge.emit('status',phase='loading',message='Loading model')
            bridge.emit('enhancement_result',prompt='一只蓝色的鸟')
            bridge.emit('done',status='completed')
        self.assertIn('Loading model',saved.getvalue())
        self.assertIn('一只蓝色的鸟',saved.getvalue())
        self.assertIn('completed',saved.getvalue())

    def test_enhancement_diagnostics_are_streamed_before_completion(self):
        import subprocess
        import sys
        import tempfile
        import prompt_enhancement as enhancement
        class Supervisor:
            def spawn(self,args,**kwargs): return subprocess.Popen(args,**kwargs)
            def release(self,child):
                if child.poll() is None: child.kill()
                child.wait()
            def check_cancelled(self): pass
        code='import sys,time; print("native model loading",file=sys.stderr,flush=True); time.sleep(.05); print(\'<think>done</think>{"rewritten_prompt":"a blue bird","wh_ratio":"1:1"}[end of text]\',flush=True)'
        events=[]
        with tempfile.TemporaryDirectory() as directory, patch.object(enhancement,'command',return_value=([sys.executable,'-u','-c',code],'t2i')), patch.object(enhancement,'inference_request',side_effect=lambda request,result:request):
            enhancement.run_enhancement({},Path(directory),lambda kind,**fields:events.append((kind,fields)),Supervisor())
            self.assertIn('native model loading',(Path(directory)/'enhancement/llama.log').read_text())
        diagnostic=next(i for i,(kind,fields) in enumerate(events) if kind=='log' and 'native model loading' in fields['message'])
        completion=next(i for i,(kind,fields) in enumerate(events) if kind=='enhancement_result')
        self.assertLess(diagnostic,completion)
