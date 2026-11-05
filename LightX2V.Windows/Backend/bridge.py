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
    started=time.monotonic()
    if request.get('promptEnhancement') is not None:
        from prompt_enhancement import run_enhancement, WindowsSupervisor, EnhancementCancelled, ratio_value
        supervisor=WindowsSupervisor(folder,parent,emit)
        try:
            request=run_enhancement(request,folder,emit,supervisor)
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
    from runtime_profile import inspect_runtime, inference_profile, inference_environment
    runtime=inspect_runtime(request.get('backend','auto'))
    backend=runtime['backend']
    if psutil.virtual_memory().available < 8*1024**3:
        raise RuntimeError('Less than 8 GiB of system memory is available. Close other applications before generating.')
    free_vram=runtime['free']
    large_image=width*height > 1100000
    config,profile=inference_profile(backend,config,width,height,free_vram)
    frozen=folder/'config.json'; atomic_json(frozen,config)
    env=inference_environment(backend,os.environ,request['repository'],large_image)
    cmd=[sys.executable,'-X','utf8','-u',str(Path(__file__).with_name('worker.py')),request['repository'],'--model_cls','qwen_image_21','--task',task,'--model_path',request['model'],'--config_json',str(frozen),'--prompt',prompt,'--size',str(height),str(width),'--seed',str(seed),'--save_result_path',str(output)]
    if references: cmd+=['--image_path',','.join(references),'--resolution','512']
    atomic_json(folder/'invocation.json',{'argv':cmd,'cwd':str(folder),'platform':sys.platform,
        'backend':backend,'profile':profile,
        'environment':{k:env[k] for k in ('PLATFORM','DTYPE','SENSITIVE_LAYER_DTYPE','LIGHTX2V_PIN_HOST_WEIGHTS','TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL') if k in env}})
    cancel=folder/'cancel.signal'; cancelled=False; memory_error=False; tail=[]; lines=queue.Queue()
    if cancel.exists() or not parent_alive(parent):
        emit('done',status='cancelled',seconds=time.monotonic()-started);return 130
    job=OwnedJob(); child=None
    emit('status',phase='loading',message='Loading model')
    try:
        child=subprocess.Popen(cmd,cwd=folder,env=env,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,encoding='utf-8',errors='replace',bufsize=1,creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
        job.assign(child)
        emit('child_started',pid=child.pid)
        def read():
            try:
                for line in child.stdout: lines.put(line)
            finally: lines.put(None)
        reader=threading.Thread(target=read,daemon=True); reader.start(); ended=False
        with (folder/'inference.log').open('w',encoding='utf-8') as log:
            while not ended:
                if psutil.virtual_memory().available < 8*1024**3:
                    memory_error=True;job.terminate()
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
        if memory_error: raise RuntimeError('Available system memory fell below 8 GiB. Try a smaller image or close other memory-intensive applications.')
        if cancelled:
            emit('done',status='cancelled',seconds=time.monotonic()-started); return 130
        if code: raise RuntimeError('Inference exited with code '+str(code)+'\n'+'\n'.join(tail[-8:]))
        from PIL import Image
        with Image.open(output) as im:
            im.load()
            if im.format!='PNG' or im.size!=(width,height): raise RuntimeError('Generated image has an unexpected format or dimensions.')
        emit('done',status='completed',output=str(output),width=width,height=height,seconds=time.monotonic()-started)
        return 0
    finally:
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
        emit('error',message=str(exc));emit('done',status='failed',message=str(exc));return 1

if __name__=='__main__': raise SystemExit(main())
