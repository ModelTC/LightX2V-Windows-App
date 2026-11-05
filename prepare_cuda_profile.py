"""Explicitly refresh the pinned CUDA profile from official wheel metadata.

Not called during a normal build. Does not download the multi-GB torch wheel.
The installer verifies the complete file against the official index SHA256.
"""
import copy
import html
import json
import re
import urllib.parse
import urllib.request
from pathlib import Path

root = Path(__file__).resolve().parent/'LightX2V.Windows/Resources/Setup'
manifest = copy.deepcopy(json.loads((root/'manifest.json').read_text(encoding='utf-8')))
manifest['profile'] = 'native-nvidia-cu128-v1'
manifest['packages'] = [p for p in manifest['packages'] if not p['name'].startswith('rocm')]
for name, version in [('torch', '2.11.0'), ('torchvision', '0.26.0'), ('torchaudio', '2.11.0')]:
    index = f'https://download.pytorch.org/whl/cu128/{name}/'
    text = urllib.request.urlopen(index, timeout=30).read().decode()
    filename = f'{name}-{version}+cu128-cp312-cp312-win_amd64.whl'
    links = [html.unescape(x) for x in re.findall(r'<a[^>]+href="([^"]+)"', text) if urllib.parse.unquote(x).split('#')[0].endswith('/'+filename)]
    if len(links) != 1: raise ValueError('Expected exactly one official wheel: '+filename)
    url, fragment = links[0].split('#', 1)
    digest = urllib.parse.parse_qs(fragment)['sha256'][0]
    head = urllib.request.urlopen(urllib.request.Request(url, method='HEAD'), timeout=30)
    metadata = urllib.request.urlopen(url+'.metadata', timeout=30).read().decode()
    if f'Version: {version}+cu128' not in metadata: raise ValueError('Incorrect wheel version')
    artifact = dict(name=name, version=version+'+cu128', file=filename, url=url, sha256=digest, size=int(head.headers['Content-Length']), group='dependency')
    manifest['packages'] = [artifact if p['name']==name else p for p in manifest['packages']]
manifest['provenance'] = {'torch':'https://pytorch.org/get-started/previous-versions/#v2110','index':'https://download.pytorch.org/whl/cu128','status':'RTX hardware acceptance pending'}
(root/'manifest-cuda.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
(root/'requirements-cuda.lock').write_text('\n'.join(p['name']+'=='+p['version'] for p in manifest['packages'])+'\n', encoding='utf-8')
print('CUDA profile pinned:',len(manifest['packages']),'packages;',sum(p['size'] for p in manifest['packages']),'download bytes')
