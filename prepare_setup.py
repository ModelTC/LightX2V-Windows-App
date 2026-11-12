"""Verify the committed, pinned first-run payload before publishing.

Pins are deliberately not resolved against the latest package versions at build time.
The source archive contains the recorded LightX2V revision plus the same native
Windows compatibility files used by the Radeon 8060S acceptance environment.
"""
import hashlib
import json
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parent / 'LightX2V.Windows/Resources/Setup'
manifest = json.loads((root / 'manifest.json').read_text(encoding='utf-8'))
if manifest['schema'] != 1 or manifest['profile'] != 'native-amd-gfx1151-v1':
    raise ValueError('Unknown native setup profile')
packages = manifest['packages']
if len({p['name'] for p in packages}) != len(packages):
    raise ValueError('Duplicate package names')
lock = [p for p in (root / 'requirements-windows.lock').read_text(encoding='utf-8').splitlines() if p and not p.startswith('#')]
if lock != [p['name'] + '==' + p['version'] for p in packages]:
    raise ValueError('Manifest and requirements lock differ')
for item in [manifest['python'], *packages]:
    if len(item['sha256']) != 64 or item['size'] <= 0 or not item['url'].startswith('https://'):
        raise ValueError('Invalid pinned artifact: ' + item['file'])
    if Path(item['file']).name != item['file']:
        raise ValueError('Invalid package name')
source = manifest['source']
if source.get('version') != '0.6.1' or source['revision'] != '43c6a11bb65b7357f06e9603fa92249bc16b484e':
    raise ValueError('Expected the fixed LightX2V 0.6.1 source')
provenance = json.loads((root.parents[2] / 'third_party/source-provenance.json').read_text(encoding='utf-8'))
for key in ('version', 'revision', 'sha256', 'upstreamArchiveSha256', 'patchSha256'):
    if provenance[key] != source[key]:
        raise ValueError('Source provenance differs: ' + key)
if hashlib.sha256((root.parents[2] / provenance['patch']).read_bytes()).hexdigest() != source['patchSha256']:
    raise ValueError('Windows compatibility patch checksum mismatch')
archive = root / source['archive']
if hashlib.sha256(archive.read_bytes()).hexdigest() != manifest['source']['sha256']:
    raise ValueError('Bundled native source checksum mismatch')
with zipfile.ZipFile(archive) as source:
    for entry in source.infolist():
        if entry.filename.startswith(('/', '\\')) or '..' in Path(entry.filename).parts:
            raise ValueError('Unsafe source path')
    if 'lightx2v/infer.py' not in source.namelist() or source.testzip():
        raise ValueError('Invalid native source archive')
print(f'First-run payload verified: {len(packages)} pinned packages; source {manifest["source"]["revision"]}')

# The CUDA runtime is separate; normal builds validate committed pins, never resolve latest.
cuda = json.loads((root/'manifest-cuda.json').read_text(encoding='utf-8'))
if cuda['profile'] != 'native-nvidia-cu128-v1' or cuda['python'] != manifest['python'] or cuda['source'] != manifest['source']:
    raise ValueError('CUDA profile must share the bundled Python and verified source')
if any(p['name'].startswith('rocm') for p in cuda['packages']): raise ValueError('ROCm packages in CUDA profile')
if len({p['name'] for p in cuda['packages']}) != len(cuda['packages']): raise ValueError('Duplicate CUDA packages')
if (root/'requirements-cuda.lock').read_text(encoding='utf-8').splitlines() != [p['name']+'=='+p['version'] for p in cuda['packages']]: raise ValueError('CUDA lock differs')
for p in cuda['packages']:
    if len(p['sha256'])!=64 or p['size']<=0 or not p['url'].startswith('https://'): raise ValueError('Invalid CUDA artifact')
print('CUDA payload verified:',len(cuda['packages']),'pinned packages; RTX image acceptance pending')

assistants = json.loads((root.parent/'driver-assistants.json').read_text(encoding='utf-8'))
if {a['backend'] for a in assistants} != {'amd_rocm','cuda'}: raise ValueError('Missing official graphics assistant')
for a in assistants:
    artifact=a['download']
    if len(artifact['sha256'])!=64 or artifact['size']<=0 or not artifact['url'].startswith('https://') or Path(artifact['file']).name!=artifact['file']:raise ValueError('Invalid official installer pin')
print('Official graphics assistant pins verified: AMD / NVIDIA')
