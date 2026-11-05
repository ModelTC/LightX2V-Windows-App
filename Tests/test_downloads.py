import contextlib
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'LightX2V.Windows/Backend'))
from download_models import parallel_download


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
