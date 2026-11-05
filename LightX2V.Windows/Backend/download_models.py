"""Public model repositories: verified, resumable downloads without extra SDKs."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import sys
import time
import urllib.request
import urllib.parse
import concurrent.futures
import threading

sys.path.insert(0,str(Path(__file__).resolve().parent))

REPOSITORIES = {
    'turbo': 'Qwen/Qwen-Image-2.1-Turbo',
    't2i': 'lightx2v/Qwen-Image-2.1-PE-T2I-GGUF-Q8_0',
    'i2i': 'lightx2v/Qwen-Image-2.1-PE-I2I-GGUF-Q8_0',
}


def emit(kind, **values):
    print(json.dumps(dict(type=kind, **values), ensure_ascii=False), flush=True)


def metadata(resource, source):
    repo = REPOSITORIES[resource]
    if source == 'huggingFace':
        url = f'https://huggingface.co/api/models/{repo}/tree/main?recursive=true'
        files = []
        while url:
            with urllib.request.urlopen(url, timeout=30) as response:
                files.extend(json.load(response))
                link = response.headers.get('Link', '')
                import re
                match = re.search(r'<([^>]+)>;\s*rel="next"', link)
                url = match[1] if match else None
                if url and not url.startswith('https://huggingface.co/api/models/'):
                    raise ValueError('Unexpected repository pagination URL.')
        result = [dict(path=f['path'], size=f['size'], sha256=f.get('lfs', {}).get('oid'),
                       git_sha1=None if f.get('lfs') else f.get('oid')) for f in files if f['type'] == 'file']
        base = f'https://huggingface.co/{repo}/resolve/main/'
    elif source == 'modelScope':
        url = f'https://modelscope.cn/api/v1/models/{repo}/repo/files?Revision=master&Recursive=true'
        with urllib.request.urlopen(url, timeout=30) as response:
            data = json.load(response)
        if data.get('Code') != 200: raise ValueError('ModelScope repository is unavailable.')
        result = [dict(path=f['Path'], size=f['Size'], sha256=f.get('Sha256'), git_sha1=None)
                  for f in data['Data']['Files'] if f['Type'] == 'blob']
        base = f'https://modelscope.cn/models/{repo}/resolve/master/'
    else: raise ValueError('Unknown model download source.')
    selected = []
    for file in result:
        path = file['path']; parts = PurePosixPath(path).parts
        if not parts or path.startswith('/') or '..' in parts or '\\' in path or ':' in path:
            raise ValueError('Invalid repository file path.')
        allowed = (parts[0] in ('transformer', 'text_encoder', 'vae', 'scheduler', 'processor') or
                   path in ('model_index.json', 'config.json', 'README.md', 'LICENSE')) if resource == 'turbo' else (
                       len(parts) == 1 and (path.endswith('.gguf') or path in ('system_prompt.txt', 'chat_template.jinja', 'README.md', 'LICENSE')))
        if allowed:
            file['url'] = base + urllib.parse.quote(path, safe='/')
            selected.append(file)
    required = ['model_index.json'] if resource == 'turbo' else ['system_prompt.txt', f'Qwen-Image-2.1-PE-{resource.upper()}-Q8_0.gguf']
    if resource == 'i2i': required.append('mmproj-Qwen-Image-2.1-PE-I2I-BF16.gguf')
    if not set(required) <= {f['path'] for f in selected}: raise ValueError('Repository is missing required model files.')
    return selected


class Cancelled(Exception): pass


def parallel_download(spec, part, check, progress, transferred):
    """Persist a bitmap for non-overlapping ranges; never infer coverage from size."""
    size=spec['size'];chunk_size=32*1024**2;count=(size+chunk_size-1)//chunk_size
    state_path=part.with_suffix('.ranges.json')
    with urllib.request.urlopen(urllib.request.Request(spec['url'],headers={'Range':'bytes=0-0'}),timeout=30) as probe:
        if probe.status!=206 or probe.headers.get('Content-Range','')!=f'bytes 0-0/{size}': return False
    completed=set();prefix=part.stat().st_size if part.exists() else 0
    if state_path.exists():
        state=json.loads(state_path.read_text())
        if state.get('size')==size and state.get('chunkSize')==chunk_size:
            completed={i for i in state['completed'] if type(i) is int and 0<=i<count}
    elif prefix<size: completed=set(range(prefix//chunk_size))
    def persist():
        temp=state_path.with_suffix('.tmp')
        temp.write_text(json.dumps(dict(size=size,chunkSize=chunk_size,completed=sorted(completed))))
        os.replace(temp,state_path)
    with part.open('r+b' if part.exists() else 'wb') as stream: stream.truncate(size)
    persist();lock=threading.Lock();stopped=threading.Event();inflight={}
    def fetch(index):
        first=index*chunk_size;last=min(size-1,first+chunk_size-1)
        for attempt in range(3):
            check()
            if stopped.is_set(): raise Cancelled()
            try:
                headers={'Range':f'bytes={first}-{last}','User-Agent':'LightX2V-Windows/0.1.5'}
                with urllib.request.urlopen(urllib.request.Request(spec['url'],headers=headers),timeout=30) as response:
                    if response.status!=206 or response.headers.get('Content-Range','')!=f'bytes {first}-{last}/{size}': raise ValueError('Invalid model byte range.')
                    with part.open('r+b',buffering=0) as output:
                        output.seek(first);received=0
                        while block:=response.read(1024**2):
                            check()
                            if stopped.is_set(): raise Cancelled()
                            received+=len(block)
                            if received>last-first+1: raise ValueError('Model byte range is too large.')
                            output.write(block)
                            with lock: inflight[index]=received;transferred[0]+=len(block)
                        if received!=last-first+1: raise ValueError('Incomplete model byte range.')
                        os.fsync(output.fileno())
                with lock:
                    completed.add(index);inflight.pop(index,None);persist()
                return
            except Cancelled: raise
            except Exception:
                with lock: inflight[index]=0
                if attempt==2: raise
                time.sleep(.5)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        futures={pool.submit(fetch,i) for i in range(count) if i not in completed}
        try:
            while futures:
                check();done,futures=concurrent.futures.wait(futures,timeout=.2,return_when=concurrent.futures.FIRST_COMPLETED)
                for future in done: future.result()
                with lock: current=sum(min(chunk_size,size-i*chunk_size) for i in completed)+sum(inflight.values())
                progress(spec['path'],current)
        finally: stopped.set()
    return True


def required_download_space(root, files):
    needed = 1024**3
    for spec in files:
        path = (root/spec['path']).resolve()
        if not path.is_relative_to(root): raise ValueError('Model destination points outside the selected directory.')
        if path.is_file() and path.stat().st_size == spec['size']: continue
        key = hashlib.sha256((spec['path']+str(spec.get('sha256'))+str(spec.get('git_sha1'))).encode()).hexdigest()
        partial = root/'.lightx2v-downloads'/(key+'.partial')
        allocated = min(spec['size'], partial.stat().st_size) if partial.is_file() else 0
        needed += spec['size'] - allocated
    return needed


def run(request, parent):
    from bridge import parent_alive
    resource, source = request['resource'], request['source']
    if resource not in REPOSITORIES: raise ValueError('Unknown model resource.')
    root = Path(request['destination'])
    if not root.is_absolute(): raise ValueError('Choose an absolute model directory.')
    root.mkdir(parents=True, exist_ok=True); root = root.resolve()
    cancel = Path(request['cancelFile'])
    def check():
        if cancel.exists() or not parent_alive(parent): raise Cancelled()
    check(); emit('download_status', message='Reading repository files')
    files = metadata(resource, source)
    import shutil
    needed = required_download_space(root, files)
    if shutil.disk_usage(root).free < needed:
        raise OSError(f'Not enough disk space for this model. Need {needed/1024**3:.1f} GiB free, including pending downloads and a 1 GiB reserve.')
    total = sum(f['size'] for f in files); completed = 0; transferred=[0];started = time.monotonic(); last = 0.0
    def progress(path, current=0, phase='downloading'):
        nonlocal last
        now = time.monotonic()
        if now-last >= .2 or phase != 'downloading':
            emit('download_progress', file=path, completed=completed+current, total=total,
                 bytesPerSecond=transferred[0]/max(now-started,.001), phase=phase)
            last = now
    def verified(path, spec):
        if not path.is_file() or path.stat().st_size != spec['size']: return False
        sha = hashlib.sha256(); git = hashlib.sha1(f'blob {spec["size"]}\0'.encode())
        with path.open('rb') as stream:
            while chunk := stream.read(8*1024**2): check(); sha.update(chunk); git.update(chunk)
        return ((not spec.get('sha256') or sha.hexdigest() == spec['sha256']) and
                (not spec.get('git_sha1') or git.hexdigest() == spec['git_sha1']))
    for spec in files:
        check(); path = (root/spec['path']).resolve()
        if not path.is_relative_to(root): raise ValueError('Model destination points outside the selected directory.')
        progress(spec['path'], phase='checking')
        if verified(path, spec): completed += spec['size']; continue
        path.parent.mkdir(parents=True, exist_ok=True)
        cache = root/'.lightx2v-downloads'; cache.mkdir(exist_ok=True)
        key = hashlib.sha256((spec['path']+str(spec.get('sha256'))+str(spec.get('git_sha1'))).encode()).hexdigest()
        part = cache/(key+'.partial')
        import shutil
        allocated = part.stat().st_size if part.exists() else 0
        needed = max(0, spec['size'] - allocated) + 1024**3
        if shutil.disk_usage(root).free < needed:
            raise OSError(f"Not enough disk space for {spec['path']}. Need {needed/1024**3:.1f} GiB more free space (including a 1 GiB reserve).")
        if spec['size']>64*1024**2 and parallel_download(spec,part,check,progress,transferred):
            progress(spec['path'],phase='checking')
            if not verified(part,spec):
                part.unlink(missing_ok=True);part.with_suffix('.ranges.json').unlink(missing_ok=True)
                raise ValueError('Downloaded model checksum does not match the repository.')
            os.replace(part,path);part.with_suffix('.ranges.json').unlink(missing_ok=True)
            completed+=spec['size'];continue
        for attempt in range(3):
            check()
            try:
                offset = part.stat().st_size if part.exists() else 0
                if offset >= spec['size']:
                    if verified(part,spec): os.replace(part,path); break
                    part.unlink(); offset = 0
                headers = {'User-Agent':'LightX2V-Windows/0.1.5'}
                if offset: headers['Range'] = f'bytes={offset}-'
                with urllib.request.urlopen(urllib.request.Request(spec['url'],headers=headers),timeout=30) as response:
                    resume = offset and response.status == 206
                    if resume and not response.headers.get('Content-Range','').startswith(f'bytes {offset}-'):
                        raise ValueError('Invalid download range response.')
                    if not resume: offset = 0
                    with part.open('ab' if resume else 'wb') as stream:
                        while chunk := response.read(1024**2):
                            check(); stream.write(chunk); offset += len(chunk);transferred[0]+=len(chunk)
                            if offset > spec['size']: raise ValueError('Downloaded file exceeds the repository size.')
                            progress(spec['path'],offset)
                progress(spec['path'],phase='checking')
                if not verified(part,spec):
                    part.unlink(missing_ok=True)
                    raise ValueError('Downloaded model checksum does not match the repository.')
                os.replace(part,path); break
            except Cancelled: raise
            except Exception:
                if attempt == 2: raise
                time.sleep(.5)
        completed += spec['size']
    check()
    (root/'.lightx2v-model.json').write_text(json.dumps(dict(repository=REPOSITORIES[resource],source=source,files=files),indent=2),encoding='utf-8')
    emit('download_done',status='completed',destination=str(root),completed=total,total=total)


if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('request');parser.add_argument('--parent',type=int,default=0);args=parser.parse_args()
    try: run(json.loads(Path(args.request).read_text(encoding='utf-8-sig')),args.parent)
    except Cancelled: emit('download_done',status='cancelled')
    except Exception as error: emit('download_done',status='failed',message=str(error));raise SystemExit(1)
