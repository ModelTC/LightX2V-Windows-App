import contextlib
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
from pathlib import Path
import sys
import subprocess
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import urllib.error

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'LightX2V.Windows/Backend'))
from download_models import parallel_download
import download_models as downloads


class SampleResponse(io.BytesIO):
    def __init__(self, data, delay=0):
        super().__init__(data)
        self.status = 206
        self.headers = {'Content-Range': f'bytes 0-{len(data)-1}/{len(data)}'}
        self.delay = delay

    def read(self, size=-1):
        if self.delay:
            time.sleep(self.delay)
        return super().read(size)

    def read1(self, size=-1):
        return self.read(size)


class SourceSelectionTests(unittest.TestCase):
    def test_sources_are_probed_in_parallel_and_weight_route_speed_selects_winner(self):
        barrier = threading.Barrier(2)
        requests = []
        data = b'model sample'

        def metadata(resource, source, **options):
            barrier.wait(timeout=1)
            return [dict(path='model.gguf', size=len(data), url=f'https://{source}.invalid/model')]

        def open_sample(request, **options):
            source = 'huggingFace' if 'huggingFace' in request.full_url else 'modelScope'
            requests.append((source, request.headers['Range']))
            return SampleResponse(data, .04 if source == 'huggingFace' else .001)

        with patch.object(downloads, 'metadata', side_effect=metadata), patch.object(downloads.urllib.request, 'urlopen', side_effect=open_sample):
            sources = downloads.select_sources('turbo', lambda: None)
        self.assertEqual([source for source, _ in sources], ['modelScope', 'huggingFace'])
        self.assertCountEqual(requests, [('modelScope', 'bytes=0-11'), ('huggingFace', 'bytes=0-11')])

    def test_metadata_success_without_reachable_weight_file_is_rejected(self):
        def metadata(resource, source, **options):
            return [dict(path='model.gguf', size=3, url=f'https://{source}.invalid/model')]

        def open_sample(request, **options):
            if 'huggingFace' in request.full_url:
                raise urllib.error.URLError('weight CDN unavailable')
            return SampleResponse(b'abc')

        with patch.object(downloads, 'metadata', side_effect=metadata), patch.object(downloads.urllib.request, 'urlopen', side_effect=open_sample):
            sources = downloads.select_sources('turbo', lambda: None)
        self.assertEqual([source for source, _ in sources], ['modelScope'])

    def test_two_slow_routes_are_usable_with_a_small_weight_sample(self):
        data = b'x'*(64*1024)
        requests = []

        def metadata(resource, source, **options):
            return [dict(path='model.gguf', size=len(data), url=f'https://{source}.invalid/model')]

        def open_sample(request, **options):
            requests.append(request.headers['Range'])
            return SampleResponse(data, .7 if 'huggingFace' in request.full_url else .6)

        with patch.object(downloads, 'metadata', side_effect=metadata), patch.object(downloads.urllib.request, 'urlopen', side_effect=open_sample):
            sources = downloads.select_sources('turbo', lambda: None)
        self.assertEqual([source for source, _ in sources], ['modelScope', 'huggingFace'])
        self.assertEqual(requests, ['bytes=0-65535', 'bytes=0-65535'])

    def test_stalled_source_does_not_hide_another_usable_source(self):
        release = threading.Event()
        data = b'abc'

        def metadata(resource, source, **options):
            self.assertIsNotNone(options.get('deadline'))
            if source == 'huggingFace':
                release.wait(timeout=1)
                raise TimeoutError()
            return [dict(path='model.gguf', size=len(data), url='https://modelScope.invalid/model')]

        try:
            with patch.object(downloads, 'SOURCE_PROBE_TIMEOUT', .05), patch.object(downloads, 'metadata', side_effect=metadata), patch.object(downloads.urllib.request, 'urlopen', return_value=SampleResponse(data)):
                sources = downloads.select_sources('turbo', lambda: None)
            self.assertEqual([source for source, _ in sources], ['modelScope'])
        finally:
            release.set()

    def test_probe_deadline_does_not_wait_for_stalled_source(self):
        release = threading.Event()

        def stalled_metadata(*args, **options):
            release.wait(timeout=1)
            raise TimeoutError()

        started = time.monotonic()
        try:
            with patch.object(downloads, 'SOURCE_PROBE_TIMEOUT', .03), patch.object(downloads, 'metadata', side_effect=stalled_metadata):
                with self.assertRaisesRegex(OSError, 'Neither model download source'):
                    downloads.select_sources('turbo', lambda: None)
            self.assertLess(time.monotonic()-started, .4)
        finally:
            release.set()

    def test_probe_can_be_cancelled_while_metadata_is_in_flight(self):
        probing = threading.Event()
        release = threading.Event()

        def stalled_metadata(*args, **options):
            probing.set()
            release.wait(timeout=1)
            raise TimeoutError()

        def check():
            if probing.is_set():
                raise downloads.Cancelled()

        try:
            with patch.object(downloads, 'metadata', side_effect=stalled_metadata):
                with self.assertRaises(downloads.Cancelled):
                    downloads.select_sources('turbo', check)
        finally:
            release.set()

    def test_slow_drip_metadata_and_weights_do_not_delay_process_exit(self):
        weights = b'x'*128
        scope_files = [dict(Type='blob', Path='model_index.json', Size=2, Sha256=hashlib.sha256(b'{}').hexdigest()),
                       dict(Type='blob', Path='transformer/model.safetensors', Size=len(weights), Sha256=hashlib.sha256(weights).hexdigest())]
        hf_files = [dict(type='file', path='model_index.json', size=2, oid=hashlib.sha1(b'blob 2\0{}').hexdigest()),
                    dict(type='file', path='transformer/model.safetensors', size=len(weights), lfs=dict(oid=hashlib.sha256(weights).hexdigest()))]

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                metadata = 'metadata' in self.path
                if metadata:
                    data = json.dumps(hf_files if 'slow' in self.path else dict(Code=200, Data=dict(Files=scope_files))).encode()
                    self.send_response(200)
                else:
                    data = weights
                    self.send_response(206)
                    self.send_header('Content-Range', f'bytes 0-{len(data)-1}/{len(data)}')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                try:
                    if self.path.startswith('/drip'):
                        for byte in data:
                            self.wfile.write(bytes([byte]))
                            self.wfile.flush()
                            time.sleep(.02)
                    else:
                        self.wfile.write(data)
                except (BrokenPipeError, ConnectionResetError):
                    pass

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            for phase in ('metadata', 'weights'):
                with self.subTest(phase=phase):
                    script = f'''
import sys, urllib.request
sys.path.insert(0, {str(Path(downloads.__file__).parent)!r})
import download_models as downloads
downloads.SOURCE_PROBE_TIMEOUT = .15
real_open = urllib.request.urlopen
def open_local(request, **options):
    original = request if isinstance(request, str) else request.full_url
    metadata = isinstance(request, str)
    kind = 'metadata' if metadata else 'weights'
    slow = 'huggingface.co' in original
    prefix = 'drip-slow' if slow and kind == {phase!r} else 'slow' if slow else 'fast'
    return real_open('http://127.0.0.1:{server.server_port}/' + prefix + '-' + kind, **options)
downloads.urllib.request.urlopen = open_local
sources = downloads.select_sources('turbo', lambda: None)
assert sources[0][0] == 'modelScope', sources
'''
                    started = time.monotonic()
                    result = subprocess.run([sys.executable, '-c', script], capture_output=True, text=True, timeout=1.5)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertLess(time.monotonic()-started, 1.2)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


class SourceFallbackTests(unittest.TestCase):
    def test_cancellation_does_not_start_fallback_source(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.ExitStack() as stack:
            root = Path(directory)
            stack.enter_context(patch.object(downloads, 'select_sources', return_value=[('huggingFace', []), ('modelScope', [])]))
            download = stack.enter_context(patch.object(downloads, 'download_files', side_effect=downloads.Cancelled()))
            stack.enter_context(patch.object(downloads, 'emit'))
            with self.assertRaises(downloads.Cancelled):
                downloads.run(dict(resource='turbo', source='auto', destination=str(root), cancelFile=str(root/'cancel')), 0)
            self.assertEqual(download.call_count, 1)

    def test_auto_fallback_resumes_identical_file_and_records_actual_source(self):
        self.check_fallback(b'abcdef', b'abcdef', 'bytes=2-')

    def test_auto_fallback_keeps_different_content_in_separate_partial_cache(self):
        self.check_fallback(b'abcdef', b'uvwxyz', None)

    def check_fallback(self, primary_data, fallback_data, expected_range):
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                requests.append(self.headers.get('Range'))
                first = int(self.headers['Range'].split('=')[1].split('-')[0]) if self.headers.get('Range') else 0
                self.send_response(206 if first else 200)
                self.send_header('Content-Length', str(len(fallback_data)-first))
                if first:
                    self.send_header('Content-Range', f'bytes {first}-{len(fallback_data)-1}/{len(fallback_data)}')
                self.end_headers()
                self.wfile.write(fallback_data[first:])

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                primary = dict(path='model.gguf', size=len(primary_data), sha256=hashlib.sha256(primary_data).hexdigest(), git_sha1=None, url='https://unreachable.invalid/model')
                fallback = dict(primary, sha256=hashlib.sha256(fallback_data).hexdigest(), size=len(fallback_data), url=f'http://127.0.0.1:{server.server_port}/model')
                cache = root/'.lightx2v-downloads'
                cache.mkdir()
                partial = cache/(downloads.download_key(primary)+'.partial')
                partial.write_bytes(primary_data[:2])
                events = []
                real_open = downloads.urllib.request.urlopen

                def open_file(request, **options):
                    if request.full_url == primary['url']:
                        raise urllib.error.URLError('connection lost')
                    return real_open(request, **options)

                with contextlib.ExitStack() as stack:
                    stack.enter_context(patch.object(downloads, 'select_sources', return_value=[('huggingFace', [primary]), ('modelScope', [fallback])]))
                    stack.enter_context(patch.object(downloads.urllib.request, 'urlopen', side_effect=open_file))
                    stack.enter_context(patch.object(downloads.time, 'sleep'))
                    stack.enter_context(patch.object(downloads, 'emit', side_effect=lambda kind, **values: events.append(dict(type=kind, **values))))
                    downloads.run(dict(resource='turbo', source='auto', destination=str(root), cancelFile=str(root/'cancel')), 0)
                self.assertEqual((root/'model.gguf').read_bytes(), fallback_data)
                self.assertEqual(requests, [expected_range])
                self.assertIn(dict(type='download_source', source='modelScope', fallback=True), events)
                self.assertEqual(json.loads((root/'.lightx2v-model.json').read_text())['source'], 'modelScope')
                if primary_data != fallback_data:
                    self.assertEqual(partial.read_bytes(), primary_data[:2])
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_manual_source_is_used_without_probe_or_automatic_fallback(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.ExitStack() as stack:
            root = Path(directory)
            metadata = stack.enter_context(patch.object(downloads, 'metadata', return_value=[]))
            select = stack.enter_context(patch.object(downloads, 'select_sources'))
            download = stack.enter_context(patch.object(downloads, 'download_files', side_effect=urllib.error.URLError('offline')))
            stack.enter_context(patch.object(downloads, 'emit'))
            with self.assertRaises(urllib.error.URLError):
                downloads.run(dict(resource='turbo', source='modelScope', destination=str(root), cancelFile=str(root/'cancel')), 0)
            select.assert_not_called()
            self.assertEqual(metadata.call_args.args, ('turbo', 'modelScope'))
            self.assertEqual(download.call_count, 1)


class DownloadProgressTests(unittest.TestCase):
    def test_eta_counts_only_remaining_bytes_across_resumed_files(self):
        mib = 1024**2
        clock = [0.0]
        data = b'a'*(4*mib)
        specs = [dict(path=f'weight-{index}.bin', size=len(data), sha256=hashlib.sha256(data).hexdigest(), url=f'https://fixture.invalid/{index}')
                 for index in range(2)]
        events = []

        class TimedResponse(io.BytesIO):
            def __init__(self, offset):
                super().__init__(data[offset:])
                self.status = 206
                self.headers = {'Content-Range': f'bytes {offset}-{len(data)-1}/{len(data)}'}

            def read(self, size=-1):
                result = super().read(size)
                if result:
                    clock[0] += 1
                return result

        def open_file(request, **options):
            offset = int(request.headers['Range'].split('=')[1].split('-')[0])
            return TimedResponse(offset)

        with tempfile.TemporaryDirectory() as directory, contextlib.ExitStack() as stack:
            root = Path(directory).resolve()
            cache = root/'.lightx2v-downloads'
            cache.mkdir()
            for index, spec in enumerate(specs):
                (cache/(downloads.download_key(spec)+'.partial')).write_bytes(data[:(index+1)*mib])
            stack.enter_context(patch.object(downloads.time, 'monotonic', side_effect=lambda: clock[0]))
            stack.enter_context(patch.object(downloads.urllib.request, 'urlopen', side_effect=open_file))
            stack.enter_context(patch.object(downloads, 'emit', side_effect=lambda kind, **values: events.append(dict(type=kind, **values))))
            self.assertEqual(downloads.download_files(root, specs, lambda: None), 8*mib)
            self.assertEqual((root/specs[0]['path']).read_bytes(), data)
            self.assertEqual((root/specs[1]['path']).read_bytes(), data)
        first_file = [event for event in events if event['file'] == specs[0]['path'] and event['phase'] == 'downloading']
        self.assertIsNone(first_file[0]['etaSeconds'])
        self.assertEqual(first_file[1]['completed'], 3*mib)
        self.assertEqual(first_file[1]['bytesPerSecond'], mib)
        self.assertEqual(first_file[1]['etaSeconds'], 3)
        self.assertTrue(all(event['etaSeconds'] is None for event in events if event['phase'] == 'checking'))
        self.assertTrue(all(event['etaSeconds'] != 0 for event in events))

    def run_parallel_progress(self, samples):
        clock = [0.0]
        events = []
        spec = dict(path='model.gguf', size=70*1024**2, sha256='fixture-hash', url='https://fixture.invalid/model')

        def parallel(spec, partial, check, progress, transferred):
            for now, received, current, phase in samples:
                clock[0] = now
                transferred[0] = received
                progress(spec['path'], current, phase)
            raise downloads.Cancelled()

        with tempfile.TemporaryDirectory() as directory, contextlib.ExitStack() as stack:
            stack.enter_context(patch.object(downloads.time, 'monotonic', side_effect=lambda: clock[0]))
            stack.enter_context(patch.object(downloads, 'parallel_download', side_effect=parallel))
            stack.enter_context(patch.object(downloads, 'emit', side_effect=lambda kind, **values: events.append(dict(type=kind, **values))))
            with self.assertRaises(downloads.Cancelled):
                downloads.download_files(Path(directory).resolve(), [spec], lambda: None)
        return events

    def test_recent_speed_smooths_changes_and_converges_to_new_rate(self):
        mib = 1024**2
        samples = [(1, mib, mib, 'downloading'), (2, 2*mib, 2*mib, 'downloading')]
        samples.extend((second, (2+4*(second-2))*mib, (2+4*(second-2))*mib, 'downloading') for second in range(3, 10))
        events = [event for event in self.run_parallel_progress(samples) if event['phase'] == 'downloading']
        self.assertEqual(events[1]['bytesPerSecond'], mib)
        self.assertGreater(events[2]['bytesPerSecond'], mib)
        self.assertLess(events[2]['bytesPerSecond'], 4*mib)
        self.assertEqual(events[-1]['bytesPerSecond'], 4*mib)

    def test_progress_rollback_increases_eta_and_checking_resets_sampling(self):
        mib = 1024**2
        samples = [(1, mib, 40*mib, 'downloading'), (2, 2*mib, 41*mib, 'downloading'),
                   (3, 3*mib, 8*mib, 'downloading'), (4, 3*mib, 0, 'checking'),
                   (5, 4*mib, 9*mib, 'downloading'), (6, 5*mib, 10*mib, 'downloading'),
                   (12, 5*mib, 10*mib, 'downloading')]
        events = self.run_parallel_progress(samples)[1:]
        self.assertEqual(events[1]['etaSeconds'], 29)
        self.assertEqual(events[2]['etaSeconds'], 62)
        self.assertIsNone(events[3]['etaSeconds'])
        self.assertIsNone(events[4]['etaSeconds'])
        self.assertEqual(events[5]['etaSeconds'], 60)
        self.assertIsNone(events[6]['etaSeconds'])
        self.assertEqual(events[6]['bytesPerSecond'], 0)

    def test_preallocated_range_cache_credits_only_recorded_chunks(self):
        mib = 1024**2
        spec = dict(path='model.gguf', size=70*mib, sha256='fixture-hash', url='https://fixture.invalid/model')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cache = root/'.lightx2v-downloads'
            cache.mkdir()
            partial = cache/(downloads.download_key(spec)+'.partial')
            with partial.open('wb') as output:
                output.truncate(spec['size'])
            state = dict(size=spec['size'], chunkSize=32*mib, completed=[0, 2, 2, -1, 99])
            partial.with_suffix('.ranges.json').write_text(json.dumps(state))
            self.assertEqual(downloads.cached_download_bytes(root, spec), 38*mib)

    def test_source_fallback_starts_a_fresh_speed_sample(self):
        mib = 1024**2
        clock = [0.0]
        spec = dict(path='model.gguf', size=70*mib, sha256='fixture-hash', url='https://primary.invalid/model')
        fallback = dict(spec, url='https://fallback.invalid/model')
        events = []

        def parallel(spec, partial, check, progress, transferred):
            if 'primary' in spec['url']:
                for second in (1, 2):
                    clock[0] = second
                    transferred[0] = second*mib
                    progress(spec['path'], second*mib)
                raise urllib.error.URLError('connection lost')
            clock[0] = 3
            transferred[0] = mib
            progress(spec['path'], 3*mib)
            raise downloads.Cancelled()

        with tempfile.TemporaryDirectory() as directory, contextlib.ExitStack() as stack:
            root = Path(directory)
            stack.enter_context(patch.object(downloads.time, 'monotonic', side_effect=lambda: clock[0]))
            stack.enter_context(patch.object(downloads, 'select_sources', return_value=[('huggingFace', [spec]), ('modelScope', [fallback])]))
            stack.enter_context(patch.object(downloads, 'parallel_download', side_effect=parallel))
            stack.enter_context(patch.object(downloads, 'emit', side_effect=lambda kind, **values: events.append(dict(type=kind, **values))))
            with self.assertRaises(downloads.Cancelled):
                downloads.run(dict(resource='turbo', source='auto', destination=str(root), cancelFile=str(root/'cancel')), 0)
        transfer_events = [event for event in events if event.get('phase') == 'downloading']
        self.assertIsNotNone(transfer_events[1]['etaSeconds'])
        self.assertIsNone(transfer_events[2]['etaSeconds'])
        self.assertEqual(transfer_events[2]['bytesPerSecond'], 0)


class RangeTests(unittest.TestCase):
    def test_resumes_verified_ranges_and_preserves_exact_model_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);size=70*1024**2;source=root/'source.bin'
            block=bytes(range(256))*4096
            with source.open('wb') as output:
                for _ in range(70):output.write(block)
            requests=[];bad=[False]
            class Handler(BaseHTTPRequestHandler):
                def do_GET(self):
                    first,last=map(int,self.headers['Range'].split('=')[1].split('-'))
                    requests.append((first,last));self.send_response(206)
                    self.send_header('Content-Length',str(last-first+1))
                    self.send_header('Content-Range',f'bytes {first}-{last+int(bad[0])}/{size}')
                    self.end_headers()
                    with source.open('rb') as stream:
                        stream.seek(first);remaining=last-first+1
                        while remaining:
                            data=stream.read(min(1024**2,remaining));self.wfile.write(data);remaining-=len(data)
                def log_message(self,*args):pass
            server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
            thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
            try:
                spec=dict(size=size,path='model.gguf',url=f'http://127.0.0.1:{server.server_port}/model')
                target=root/'partial'
                with target.open('wb') as output:
                    for _ in range(36):output.write(block)
                transferred=[0]
                self.assertTrue(parallel_download(spec,target,lambda:None,lambda *a:None,transferred))
                self.assertEqual(hashlib.sha256(target.read_bytes()).digest(),hashlib.sha256(source.read_bytes()).digest())
                self.assertNotIn((0,32*1024**2-1),requests)
                self.assertEqual(transferred[0],38*1024**2)
                # A second invocation uses the persisted coverage bitmap.
                requests.clear();self.assertTrue(parallel_download(spec,target,lambda:None,lambda *a:None,[0]))
                self.assertEqual(requests,[(0,0)])
                # An incompatible server must not preallocate over existing data.
                other=root/'other';other.write_bytes(b'preserve me');bad[0]=True
                self.assertFalse(parallel_download(spec,other,lambda:None,lambda *a:None,[0]))
                self.assertEqual(other.read_bytes(),b'preserve me')
            finally:server.shutdown();server.server_close();thread.join()


if __name__=='__main__':unittest.main()
