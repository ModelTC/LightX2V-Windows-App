"""Native Windows Qwen supervisor. Stdout is versioned NDJSON; inference uses an owned Job Object."""
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import traceback
import uuid

sys.dont_write_bytecode = True
sys.path.insert(0,str(Path(__file__).resolve().parent))
SIGMAS = [1.0, .978453, .95418, .926626, .89508, .845148, .704534, .414568]


def emit(kind, **fields):
    print(json.dumps({'protocol': 1, 'type': kind, **fields}, ensure_ascii=False), flush=True)


def atomic_json(path, value):
    p = Path(path); tmp = p.with_name(p.name + '.tmp')
    tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8'); tmp.replace(p)


def validate(request):
    if sys.platform != 'win32': raise ValueError('Native Windows Python is required.')
    if request.get('protocol') != 1: raise ValueError('Unsupported request protocol.')
    for key in ('repository', 'model', 'config', 'output'):
        if not isinstance(request.get(key), str) or not Path(request[key]).is_absolute():
            raise ValueError(f'{key} must be an absolute Windows path.')
    repo, model = Path(request['repository']), Path(request['model'])
    if not (repo/'lightx2v/infer.py').is_file(): raise ValueError('LightX2V source directory is invalid.')
    for folder in ('transformer', 'text_encoder', 'vae', 'scheduler', 'processor'):
        if not (model/folder).is_dir(): raise ValueError(f'Model component is missing: {folder}')
    index = json.loads((model/'model_index.json').read_text(encoding='utf-8-sig'))
    config = json.loads(Path(request['config']).read_text(encoding='utf-8-sig'))
    if index.get('sample_sigmas') != SIGMAS or config.get('sigmas') != SIGMAS or config.get('infer_steps') != 8:
        raise ValueError('Qwen-Image-2.1-Turbo requires its fixed 8-step weights and sampling configuration.')
    if config.get('attn_type') != 'torch_sdpa' or config.get('do_time_shift') is not False or config.get('enable_cfg') is not False:
        raise ValueError('Use the bundled native Qwen Turbo configuration.')
    return config


def find_existing_crash_dumps(folder, worker_pid, executable, started_at, roots=None):
    """Locate accessible, existing PID-matched dumps; never configure WER or copy dumps."""
    if roots is None:
        roots = [Path(folder), Path(folder) / "crash"]
        local = os.environ.get("LOCALAPPDATA")
        if local:
            roots.append(Path(local) / "CrashDumps")
    executable_name = Path(executable).name
    pattern = re.compile(re.escape(executable_name) + r"\." + str(int(worker_pid))
                         + r"(?:\.[^.]+)?\.dmp$", re.IGNORECASE)
    found = []
    for directory in dict.fromkeys(Path(root) for root in roots):
        try:
            candidates = directory.glob(executable_name + "." + str(int(worker_pid)) + "*.dmp")
            for path in candidates:
                try:
                    info = path.stat()
                    if (pattern.fullmatch(path.name) and path.is_file() and info.st_size > 0
                            and info.st_mtime >= float(started_at) - 2):
                        found.append({"path": str(path.resolve()), "size_bytes": info.st_size,
                                      "modified_at": info.st_mtime})
                except OSError:
                    continue
        except OSError:
            continue
    return sorted(found, key=lambda item: item["modified_at"], reverse=True)


def worker_exit_details(code, worker_pid, folder, executable, started_at, dump_roots=None):
    unsigned = int(code) & 0xFFFFFFFF
    native = (unsigned & 0xC0000000) == 0xC0000000
    details = {"schema": 1, "worker_pid": int(worker_pid), "producer_pid": os.getpid(),
               "exit_code": int(code), "exit_code_unsigned": unsigned,
               "exit_code_hex": f"0x{unsigned:08X}", "native_failure": native,
               "worker_started_at": started_at, "worker_finished_at": time.time(),
               "task_id": Path(folder).name,
               "reason": "access violation" if unsigned == 0xC0000005 else
                         "native process failure" if native else "process exit"}
    details["dumps"] = (find_existing_crash_dumps(folder, worker_pid, executable,
                        started_at, roots=dump_roots) if native else [])
    details["dump_collection"] = "existing_files_only; WER_configuration_unchanged"
    fault = Path(folder) / "worker-fault.log"
    try:
        if fault.is_file() and fault.stat().st_size > 0:
            details["faulthandler_log"] = str(fault.resolve())
    except OSError:
        pass
    try:
        atomic_json(Path(folder) / "worker-exit.json", details)
    except Exception as error:
        details["diagnostic_write_error"] = str(error)
    return details


def worker_failure_message(details, tail):
    identity = details["exit_code_hex"] + "; worker PID " + str(details["worker_pid"])
    if details["native_failure"]:
        message = "Native inference process crashed (" + details["reason"] + ", " + identity + ")."
        if details.get("faulthandler_log"):
            message += "\nPython fault log: " + details["faulthandler_log"]
        if details["dumps"]:
            message += "\nFound existing crash dump: " + "\n".join(
                item["path"] for item in details["dumps"])
        else:
            message += "\nNo matching existing crash dump was found in the checked task and default CrashDumps locations."
        message += "\nSee worker-exit.json and inference.log in the task directory."
        return message
    return ("Inference exited with code " + str(details["exit_code"]) + " (" + identity
            + ")\n" + "\n".join(tail[-8:]))


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [(k, ctypes.c_ulonglong) for k in ('ReadOperationCount','WriteOperationCount','OtherOperationCount','ReadTransferCount','WriteTransferCount','OtherTransferCount')]
class BASIC_LIMIT(ctypes.Structure):
    _fields_ = [('PerProcessUserTimeLimit',ctypes.c_longlong),('PerJobUserTimeLimit',ctypes.c_longlong),('LimitFlags',wintypes.DWORD),('MinimumWorkingSetSize',ctypes.c_size_t),('MaximumWorkingSetSize',ctypes.c_size_t),('ActiveProcessLimit',wintypes.DWORD),('Affinity',ctypes.c_size_t),('PriorityClass',wintypes.DWORD),('SchedulingClass',wintypes.DWORD)]
class EXTENDED_LIMIT(ctypes.Structure):
    _fields_ = [('BasicLimitInformation',BASIC_LIMIT),('IoInfo',IO_COUNTERS),('ProcessMemoryLimit',ctypes.c_size_t),('JobMemoryLimit',ctypes.c_size_t),('PeakProcessMemoryUsed',ctypes.c_size_t),('PeakJobMemoryUsed',ctypes.c_size_t)]


class OwnedJob:
    def __init__(self):
        self.k = ctypes.WinDLL('kernel32', use_last_error=True)
        self.k.CreateJobObjectW.restype = wintypes.HANDLE
        self.k.CreateJobObjectW.argtypes = [ctypes.c_void_p,wintypes.LPCWSTR]
        self.k.SetInformationJobObject.argtypes = [wintypes.HANDLE,ctypes.c_int,ctypes.c_void_p,wintypes.DWORD]
        self.k.AssignProcessToJobObject.argtypes = [wintypes.HANDLE,wintypes.HANDLE]
        self.k.TerminateJobObject.argtypes = [wintypes.HANDLE,wintypes.UINT]
        self.k.CloseHandle.argtypes = [wintypes.HANDLE]
        self.handle = self.k.CreateJobObjectW(None, None)
        if not self.handle: raise ctypes.WinError(ctypes.get_last_error())
        limits = EXTENDED_LIMIT(); limits.BasicLimitInformation.LimitFlags = 0x2000
        if not self.k.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            self.close(); raise ctypes.WinError(ctypes.get_last_error())
    def assign(self, child):
        if not self.k.AssignProcessToJobObject(self.handle, wintypes.HANDLE(int(child._handle))):
            child.kill(); child.wait(); raise ctypes.WinError(ctypes.get_last_error())
    def terminate(self):
        if self.handle: self.k.TerminateJobObject(self.handle, 130)
    def close(self):
        if self.handle: self.k.CloseHandle(self.handle); self.handle = None


def parent_alive(pid):
    if not pid: return True
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]; k.OpenProcess.restype=wintypes.HANDLE
    k.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD]; k.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=k.OpenProcess(0x100000,False,pid)
    if not handle: return False
    try: return k.WaitForSingleObject(handle,0)==258
    finally: k.CloseHandle(handle)


def run(request, config, parent):
    import queue
    import psutil
    import threading
    output=Path(request['output']); folder=output.parent
    if output.suffix.lower() != '.png' or output.exists(): raise ValueError('Choose a new PNG output path.')
    prompt=request.get('prompt')
    if not isinstance(prompt,str) or not 1<=len(prompt.strip())<=20000: raise ValueError('Enter a prompt between 1 and 20000 characters.')
    width,height,seed=request.get('width'),request.get('height'),request.get('seed')
    if any(type(n) is not int or n%32 or not 256<=n<=2752 for n in (width,height)): raise ValueError('Image dimensions must be multiples of 32 between 256 and 2752.')
    if width*height > 4400000: raise ValueError('Image dimensions exceed the supported 2K image presets.')
    config=dict(config)
    if type(seed) is not int or not 0<=seed<=4294967295: raise ValueError('Seed must be between 0 and 4294967295.')
    task=request.get('task','t2i'); images=request.get('inputImages',[])
    if task not in ('t2i','i2i') or not isinstance(images,list) or len(images)>3: raise ValueError('Invalid image task or reference count.')
    if (task=='i2i') != bool(images): raise ValueError('Image editing needs reference images.')
    references=[]
    if images:
        from PIL import Image
        for item in images:
            path=Path(item['path']).resolve()
            if not path.is_relative_to((folder/'inputs').resolve()) or not path.is_file(): raise ValueError('Reference image must be a saved task input.')
            if path.stat().st_size>50*1024**2: raise ValueError('Reference image is too large.')
            with Image.open(path) as image: image.verify()
            references.append(path.relative_to(folder.resolve()).as_posix())
        if request.get('referenceResolution',512)!=512: raise ValueError('Use the accepted 512 reference resolution.')
    folder.mkdir(parents=True,exist_ok=True)
    scratch=folder/'tmp'
    scratch.mkdir(exist_ok=True)
    os.environ['TEMP']=os.environ['TMP']=str(scratch)
    started=time.monotonic()
    from runtime_profile import inspect_runtime, inference_profile, inference_environment
    runtime=inspect_runtime(request.get('backend','auto'))
    had_enhancement=request.get('promptEnhancement') is not None
    if had_enhancement:
        from prompt_enhancement import run_enhancement, WindowsSupervisor, EnhancementCancelled, ratio_value
        supervisor=WindowsSupervisor(folder,parent,emit)
        try:
            request=run_enhancement(request,folder,emit,supervisor,runtime=runtime)
        except EnhancementCancelled:
            emit('done',status='cancelled',seconds=time.monotonic()-started);return 130
        finally: supervisor.close()
        prompt=request['prompt']
        resolution=request.get('resolution')
        if resolution is not None:
            if type(resolution) is not int or resolution not in (512,1024,2048): raise ValueError('Invalid adaptive image resolution.')
            import math
            aspect=ratio_value(request['aspectRatio'])
            width=round(math.sqrt(resolution*resolution*aspect)/32)*32
            height=round(math.sqrt(resolution*resolution/aspect)/32)*32
            if not (256<=width<=2752 and 256<=height<=2752 and width*height<=4400000):
                raise ValueError('Enhanced aspect ratio exceeds supported dimensions. Choose a fixed aspect ratio and retry.')
        request.update(width=width,height=height)
        atomic_json(folder/'effective_request.json',request)
        emit('effective_request',prompt=prompt,width=width,height=height)
    if had_enhancement: runtime=inspect_runtime(request.get('backend','auto'))
    backend=runtime['backend']
    from resource_plan import plan_resources, memory_pressure
    pressure=memory_pressure(runtime['ramAvailable'],runtime['ramTotal'])
    if pressure['severe']:
        raise RuntimeError('System memory is critically low before loading the model. Close other applications and retry.')
    free_vram=runtime['free']
    large_image=width*height > 1100000
    plan=plan_resources(request['model'],runtime,width,height,task=task,prompt=prompt,reference_count=len(references),reference_resolution=512)
    config,profile=inference_profile(backend,config,width,height,free_vram,plan=plan)
    atomic_json(folder/'resource-plan.json',plan)
    if not plan['feasible']:
        raise RuntimeError('Current available memory is below the estimated budget even with streaming. Close other applications and retry. See resource-plan.json in the task directory.')
    emit('log',message='Automatic resource plan: '+profile)
    config['_app_one_shot_worker']=True
    frozen=folder/'config.json'; atomic_json(frozen,config)
    env=inference_environment(backend,os.environ,request['repository'],large_image,plan=plan)
    env['LIGHTX2V_TASK_RUN_ID']=uuid.uuid4().hex
    env['LIGHTX2V_TASK_ID']=folder.name
    cmd=[sys.executable,'-X','utf8','-u',str(Path(__file__).with_name('worker.py')),request['repository'],'--model_cls','qwen_image_21','--task',task,'--model_path',request['model'],'--config_json',str(frozen),'--prompt',prompt,'--size',str(height),str(width),'--seed',str(seed),'--save_result_path',str(output)]
    if references: cmd+=['--image_path',','.join(references),'--resolution','512']
    atomic_json(folder/'invocation.json',{'argv':cmd,'cwd':str(folder),'platform':sys.platform,
        'backend':backend,'profile':profile,
        'environment':{k:env[k] for k in ('PLATFORM','DTYPE','SENSITIVE_LAYER_DTYPE','LIGHTX2V_PIN_HOST_WEIGHTS','TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL') if k in env}})
    cancel=folder/'cancel.signal'; cancelled=False; memory_error=False; tail=[]; lines=queue.Queue()
    if cancel.exists() or not parent_alive(parent):
        emit('done',status='cancelled',seconds=time.monotonic()-started);return 130
    from telemetry import ResourceMonitor
    job=OwnedJob(); child=None; monitor=None; pressure_since=None; worker_started_at=None
    emit('status',phase='loading',message='Loading model')
    try:
        worker_started_at=time.time()
        child=subprocess.Popen(cmd,cwd=folder,env=env,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,encoding='utf-8',errors='replace',bufsize=1,creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
        job.assign(child)
        emit('child_started',pid=child.pid)
        monitor=ResourceMonitor(folder,pid=child.pid,run_id=env['LIGHTX2V_TASK_RUN_ID']).start()
        def read():
            try:
                for line in child.stdout: lines.put(line)
            finally: lines.put(None)
        reader=threading.Thread(target=read,daemon=True); reader.start(); ended=False
        with (folder/'inference.log').open('w',encoding='utf-8') as log:
            while not ended:
                memory=psutil.virtual_memory()
                pressure=memory_pressure(memory.available,memory.total)
                if pressure['severe']:
                    if pressure_since is None: pressure_since=time.monotonic()
                    if time.monotonic()-pressure_since >= 2 and not memory_error:
                        memory_error=True
                        if monitor is not None: monitor.record_guard('host_memory_pressure',**pressure)
                        job.terminate()
                else: pressure_since=None
                if cancel.exists() or not parent_alive(parent):
                    cancelled=True;job.terminate()
                try: raw=lines.get(timeout=.2)
                except queue.Empty:
                    if child.poll() is not None and not reader.is_alive(): break
                    continue
                if raw is None: ended=True;continue
                line=re.sub(r'\x1b\[[0-9;]*[a-zA-Z]','',raw).rstrip('\r\n')
                log.write(line+'\n');log.flush();tail=(tail+[line])[-25:];emit('log',message=line)
                step=re.search(r'step_index:\s*(\d+)\s*/\s*(\d+)',line)
                if step: emit('step',step=int(step[1]),total=int(step[2]),phase='sampling')
                elif 'Onload Text Encoder' in line: emit('status',phase='encoding',message='Encoding prompt')
                elif 'Run DiT cost' in line: emit('status',phase='decoding',message='Decoding image')
        code=child.wait()
        exit_details=worker_exit_details(code,child.pid,folder,sys.executable,worker_started_at)
        if monitor is not None:
            monitor.safe_record('worker_exit',worker_pid=child.pid,
                exit_code=code,exit_code_hex=exit_details['exit_code_hex'],
                native_failure=exit_details['native_failure'])
        if memory_error: raise RuntimeError('Generation stopped because system memory remained critically low. See host-telemetry.jsonl and resource-plan.json in the task directory.')
        if cancelled:
            emit('done',status='cancelled',seconds=time.monotonic()-started); return 130
        if code: raise RuntimeError(worker_failure_message(exit_details,tail))
        from PIL import Image
        with Image.open(output) as im:
            im.load()
            if im.format!='PNG' or im.size!=(width,height): raise RuntimeError('Generated image has an unexpected format or dimensions.')
        emit('done',status='completed',output=str(output),width=width,height=height,seconds=time.monotonic()-started)
        return 0
    finally:
        if monitor is not None:
            try:
                monitor.stop(error='host_memory_pressure' if memory_error else 'cancelled' if cancelled else sys.exc_info()[1])
            except Exception:
                # A monitor cleanup failure must not replace the inference error.
                pass
        job.close()
        if child is not None:
            if child.poll() is None: child.kill()
            child.wait()


def main():
    p=argparse.ArgumentParser();p.add_argument('mode',choices=('run','check'));p.add_argument('request');p.add_argument('--parent',type=int,default=0);args=p.parse_args()
    try:
        request=json.loads(Path(args.request).read_text(encoding='utf-8-sig'))
        if args.mode=='check':
            from environment_probe import probe
            return probe(request,emit)
        config=validate(request)
        return run(request,config,args.parent)
    except Exception as exc:
        detail=traceback.format_exc()
        try:
            if args.mode=='run' and isinstance(request,dict) and request.get('output'):
                folder=Path(request['output']).parent
                if folder.is_dir(): (folder/'supervisor-error.log').write_text(detail,encoding='utf-8')
        except Exception: pass
        emit('error',message=str(exc));emit('done',status='failed',message=str(exc));return 1

if __name__=='__main__': raise SystemExit(main())
