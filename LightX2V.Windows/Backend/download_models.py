"""Public model repositories: verified, resumable downloads without extra SDKs."""
import argparse
from collections import deque
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import queue
import sys
import time
import urllib.request
import urllib.parse
import concurrent.futures
import threading

sys.path.insert(0,str(Path(__file__).resolve().parent))

CATALOG_PATH = Path(__file__).resolve().parent.parent/'Resources'/'model-catalog.json'
MODEL_CATALOG = json.loads(CATALOG_PATH.read_text(encoding='utf-8'))
if MODEL_CATALOG.get('schemaVersion') != 1:
    raise ValueError('Unsupported model catalog version.')
MODELS = MODEL_CATALOG['models']
REPOSITORIES = {key: model['repository'] for key, model in MODELS.items()}
SOURCE_PROBE_TIMEOUT = 8.0
SOURCE_PROBE_BYTES = 64*1024


def emit(kind, **values):
    print(json.dumps(dict(type=kind, **values), ensure_ascii=False), flush=True)


def metadata(resource, source, timeout=30, check=lambda: None, deadline=None):
    def open_repository(url):
        check()
        request_timeout = timeout if deadline is None else min(timeout, deadline-time.monotonic())
        if request_timeout <= 0:
            raise TimeoutError('Model source check timed out.')
        return urllib.request.urlopen(url, timeout=request_timeout)

    def read_repository(response):
        content = bytearray()
        read = getattr(response, 'read1', response.read)
        while True:
            check()
            if deadline is not None and time.monotonic() >= deadline:
                raise TimeoutError('Model source check timed out.')
            block = read(64*1024)
            if not block:
                break
            content.extend(block)
        return json.loads(content)

    repo = REPOSITORIES[resource]
    if source == 'huggingFace':
        url = f'https://huggingface.co/api/models/{repo}/tree/main?recursive=true'
        files = []
        while url:
            with open_repository(url) as response:
                files.extend(read_repository(response))
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
        with open_repository(url) as response:
            data = read_repository(response)
        if data.get('Code') != 200: raise ValueError('ModelScope repository is unavailable.')
        result = [dict(path=f['Path'], size=f['Size'], sha256=f.get('Sha256'), git_sha1=None)
                  for f in data['Data']['Files'] if f['Type'] == 'blob']
        base = f'https://modelscope.cn/models/{repo}/resolve/master/'
    else: raise ValueError('Unknown model download source.')
    check()
    model = MODELS[resource]
    selected = []
    for file in result:
        path = file['path']; parts = PurePosixPath(path).parts
        if not parts or path.startswith('/') or '..' in parts or '\\' in path or ':' in path:
            raise ValueError('Invalid repository file path.')
        allowed = parts[0] in model.get('downloadDirectories', []) or path in model['downloadFiles']
        if allowed:
            file['url'] = base + urllib.parse.quote(path, safe='/')
            selected.append(file)
    available = {file['path']: file for file in selected}
    if not set(model['requiredFiles']) <= available.keys():
        raise ValueError('Repository is missing required model files.')
    for directory, layout in model.get('weightComponents', {}).items():
        if not any(f'{directory}/{layout[name]}' in available for name in ('file', 'index')):
            raise ValueError(f'Repository is missing weights for {directory}.')
    projectors = model.get('projectorFiles', [])
    if projectors and not any(path in available for path in projectors):
        raise ValueError('Repository is missing the required vision projector.')
    for path, expected in model.get('expectedGgufSizes', {}).items():
        if available[path]['size'] != expected:
            raise ValueError('Repository GGUF files do not match the supported model catalog.')
    return selected


class Cancelled(Exception): pass


def select_sources(resource, check):
    """Measure the actual weight route, including connection and first-byte time."""
    deadline = time.monotonic() + SOURCE_PROBE_TIMEOUT
    stopped = threading.Event()

    def probe(source):
        def check_probe():
            check()
            if stopped.is_set():
                raise Cancelled()
            if time.monotonic() >= deadline:
                raise TimeoutError('Model source check timed out.')

        files = metadata(resource, source, timeout=SOURCE_PROBE_TIMEOUT, check=check_probe, deadline=deadline)
        weights = [spec for spec in files if spec['path'].endswith(('.safetensors', '.gguf'))]
        if not weights:
            raise ValueError('The model source has no weight files.')
        sample = max(weights, key=lambda spec: spec['size'])
        size = min(SOURCE_PROBE_BYTES, sample['size'])
        check_probe()
        started = time.monotonic()
        request = urllib.request.Request(sample['url'], headers={
            'Range': f'bytes=0-{size-1}', 'User-Agent': 'LightX2V-Windows',
        })
        with urllib.request.urlopen(request, timeout=max(.1, deadline-started)) as response:
            if response.status == 206:
                if response.headers.get('Content-Range') != f"bytes 0-{size-1}/{sample['size']}":
                    raise ValueError('Invalid model source byte range.')
            elif response.status != 200:
                raise ValueError('Model source is unavailable.')
            received = 0
            read = getattr(response, 'read1', response.read)
            while received < size:
                check_probe()
                block = read(min(64*1024, size-received))
                if not block:
                    raise ValueError('Incomplete model source sample.')
                received += len(block)
        check_probe()
        return source, files, received / max(time.monotonic()-started, .001)

    check()
    def probe_worker(source, future):
        if not future.set_running_or_notify_cancel():
            return
        try:
            future.set_result(probe(source))
        except Exception as error:
            future.set_exception(error)

    pending = {}
    for source in ('modelScope', 'huggingFace'):
        future = concurrent.futures.Future()
        pending[future] = source
        # Probes are read-only. An unresponsive HTTP peer must not delay process exit.
        threading.Thread(target=probe_worker, args=(source, future), daemon=True).start()
    available = []
    try:
        while pending:
            check()
            remaining = max(0, deadline-time.monotonic())
            done, _ = concurrent.futures.wait(pending, timeout=min(.1, remaining), return_when=concurrent.futures.FIRST_COMPLETED)
            for future in done:
                pending.pop(future)
                try:
                    available.append(future.result())
                except Cancelled:
                    raise
                except Exception:
                    pass
            if remaining <= 0:
                break
        check()
    finally:
        stopped.set()
        for future in pending:
            future.cancel()
    if not available:
        raise OSError('Neither model download source is reachable. Check your connection and retry.')
    return [(source, files) for source, files, speed in sorted(available, key=lambda item: item[2], reverse=True)]


def download_key(spec):
    identity = spec['path'] + str(spec.get('sha256')) + str(spec.get('git_sha1'))
    # Legacy cache names stay valid. Unhashed entries cannot share partial data across sources.
    if not (spec.get('sha256') or spec.get('git_sha1')):
        identity += str(spec.get('url', ''))
    return hashlib.sha256(identity.encode()).hexdigest()


class DownloadResponse:
    """Keep cancellable file writers separate from potentially blocked HTTP reads."""
    def __init__(self, request, check):
        self.request = request
        self.check = check
        self.events = queue.Queue(maxsize=2)
        self.stopped = threading.Event()
        self.ended = False

    def publish(self, event):
        while not self.stopped.is_set():
            try:
                self.events.put(event, timeout=.1)
                return True
            except queue.Full:
                continue
        return False

    def receive(self):
        try:
            with urllib.request.urlopen(self.request, timeout=30) as response:
                if not self.publish(('headers', (response.status, response.headers))):
                    return
                read = getattr(response, 'read1', response.read)
                while not self.stopped.is_set():
                    block = read(1024**2)
                    if not block:
                        self.publish(('end', None))
                        return
                    if not self.publish(('data', block)):
                        return
        except Exception as error:
            self.publish(('error', error))

    def next_event(self):
        started = time.monotonic()
        while True:
            self.check()
            remaining = 30-(time.monotonic()-started)
            if remaining <= 0:
                raise TimeoutError('The model download stopped receiving data.')
            try:
                kind, value = self.events.get(timeout=min(.1, remaining))
            except queue.Empty:
                continue
            if kind == 'error':
                raise value
            return kind, value

    def __enter__(self):
        threading.Thread(target=self.receive, name='LightX2V-download-reader', daemon=True).start()
        try:
            kind, value = self.next_event()
            if kind != 'headers':
                raise ValueError('Missing model download response headers.')
            self.status, self.headers = value
            return self
        except BaseException:
            self.stopped.set()
            raise

    def read(self):
        if self.ended:
            return b''
        kind, value = self.next_event()
        if kind == 'end':
            self.ended = True
            return b''
        return value

    def __exit__(self, kind, error, traceback):
        self.stopped.set()


def parallel_download(spec, part, check, progress, transferred):
    """Persist a bitmap for non-overlapping ranges; never infer coverage from size."""
    size=spec['size'];chunk_size=32*1024**2;count=(size+chunk_size-1)//chunk_size
    state_path=part.with_suffix('.ranges.json')
    with DownloadResponse(urllib.request.Request(spec['url'], headers={'Range':'bytes=0-0'}), check) as probe:
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
    def check_transfer():
        check()
        if stopped.is_set():
            raise Cancelled()
    def fetch(index):
        first=index*chunk_size;last=min(size-1,first+chunk_size-1)
        for attempt in range(3):
            check_transfer()
            try:
                headers={'Range':f'bytes={first}-{last}','User-Agent':'LightX2V-Windows'}
                with DownloadResponse(urllib.request.Request(spec['url'], headers=headers), check_transfer) as response:
                    if response.status!=206 or response.headers.get('Content-Range','')!=f'bytes {first}-{last}/{size}': raise ValueError('Invalid model byte range.')
                    with part.open('r+b',buffering=0) as output:
                        output.seek(first);received=0
                        while block:=response.read():
                            check_transfer()
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
                if stopped.wait(.5):
                    raise Cancelled()
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
        key = download_key(spec)
        partial = root/'.lightx2v-downloads'/(key+'.partial')
        allocated = min(spec['size'], partial.stat().st_size) if partial.is_file() else 0
        needed += spec['size'] - allocated
    return needed


def cached_download_bytes(root, spec):
    path = root/spec['path']
    if path.is_file() and path.stat().st_size == spec['size']:
        return spec['size']
    partial = root/'.lightx2v-downloads'/(download_key(spec)+'.partial')
    if not partial.is_file():
        return 0
    size = min(spec['size'], partial.stat().st_size)
    chunk_size = 32*1024**2
    ranges = partial.with_suffix('.ranges.json')
    if not ranges.exists():
        if spec['size'] > 64*1024**2:
            return size//chunk_size*chunk_size if size < spec['size'] else 0
        return size
    try:
        state = json.loads(ranges.read_text())
        if state.get('size') != spec['size'] or state.get('chunkSize') != chunk_size:
            return 0
        count = (spec['size']+chunk_size-1)//chunk_size
        completed = {index for index in state['completed'] if type(index) is int and 0 <= index < count}
        return sum(min(chunk_size, spec['size']-index*chunk_size) for index in completed
                   if min(spec['size'], (index+1)*chunk_size) <= size)
    except (OSError, ValueError, KeyError, TypeError):
        return 0


def download_files(root, files, check):
    import shutil
    needed = required_download_space(root, files)
    if shutil.disk_usage(root).free < needed:
        raise OSError(f'Not enough disk space for this model. Need {needed/1024**3:.1f} GiB free, including pending downloads and a 1 GiB reserve.')
    total = sum(f['size'] for f in files)
    completed = 0
    transferred = [0]
    last = 0.0
    samples = deque()
    cached = {spec['path']: cached_download_bytes(root, spec) for spec in files}
    pending_cached = sum(cached.values())

    def progress(path, current=0, phase='downloading'):
        nonlocal last
        now = time.monotonic()
        if now-last >= .2 or phase != 'downloading':
            speed = 0.0
            eta = None
            if phase == 'downloading':
                if samples and transferred[0] < samples[-1][1]:
                    samples.clear()
                samples.append((now, transferred[0]))
                while len(samples) > 2 and samples[1][0] <= now-5:
                    samples.popleft()
                duration = now-samples[0][0]
                if duration >= 1:
                    speed = max(0, transferred[0]-samples[0][1])/duration
                remaining = max(0, total-completed-current-pending_cached)
                if speed > 0 and remaining > 0:
                    eta = remaining/speed
            else:
                samples.clear()
            emit('download_progress', file=path, completed=completed+current, total=total,
                 bytesPerSecond=speed, etaSeconds=eta, phase=phase)
            last = now
    def verified(path, spec):
        if not path.is_file() or path.stat().st_size != spec['size']: return False
        sha = hashlib.sha256(); git = hashlib.sha1(f'blob {spec["size"]}\0'.encode())
        with path.open('rb') as stream:
            while chunk := stream.read(8*1024**2): check(); sha.update(chunk); git.update(chunk)
        return ((not spec.get('sha256') or sha.hexdigest() == spec['sha256']) and
                (not spec.get('git_sha1') or git.hexdigest() == spec['git_sha1']))
    for spec in files:
        pending_cached -= cached[spec['path']]
        check(); path = (root/spec['path']).resolve()
        if not path.is_relative_to(root): raise ValueError('Model destination points outside the selected directory.')
        progress(spec['path'], phase='checking')
        if verified(path, spec): completed += spec['size']; continue
        path.parent.mkdir(parents=True, exist_ok=True)
        cache = root/'.lightx2v-downloads'; cache.mkdir(exist_ok=True)
        key = download_key(spec)
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
                headers = {'User-Agent':'LightX2V-Windows'}
                if offset: headers['Range'] = f'bytes={offset}-'
                with DownloadResponse(urllib.request.Request(spec['url'], headers=headers), check) as response:
                    resume = offset and response.status == 206
                    if resume and not response.headers.get('Content-Range','').startswith(f'bytes {offset}-'):
                        raise ValueError('Invalid download range response.')
                    if not resume: offset = 0
                    with part.open('ab' if resume else 'wb') as stream:
                        while chunk := response.read():
                            check(); stream.write(chunk); offset += len(chunk);transferred[0]+=len(chunk)
                            if offset > spec['size']: raise ValueError('Downloaded file exceeds the repository size.')
                            progress(spec['path'],offset)
                if offset < spec['size']:
                    raise OSError('The model download ended before the complete file arrived. Retrying from the saved position.')
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
    return total


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
    if source == 'auto':
        emit('download_source', source='auto', fallback=False)
        sources = select_sources(resource, check)
    else:
        sources = [(source, metadata(resource, source, check=check))]
    for index, (source, files) in enumerate(sources):
        check()
        emit('download_source', source=source, fallback=index > 0)
        try:
            total = download_files(root, files, check)
            break
        except Cancelled:
            raise
        except Exception:
            if index == len(sources)-1:
                raise
    (root/'.lightx2v-model.json').write_text(json.dumps(dict(repository=REPOSITORIES[resource],source=source,files=files),indent=2),encoding='utf-8')
    emit('download_done',status='completed',destination=str(root),completed=total,total=total)


if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('request');parser.add_argument('--parent',type=int,default=0);args=parser.parse_args()
    try: run(json.loads(Path(args.request).read_text(encoding='utf-8-sig')),args.parent)
    except Cancelled: emit('download_done',status='cancelled')
    except Exception as error: emit('download_done',status='failed',message=str(error));raise SystemExit(1)
