"""Fetch the pinned official Windows Vulkan runtime used by the PE adapter."""
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

ROOT=Path(__file__).resolve().parent
VERSION='b11564'
ARCHIVE=f'llama-{VERSION}-bin-win-vulkan-x64.zip'
SHA256='1a1b260a2240a12cac7a94590fa88d06ae25d349db9d556e9c2503f7d22e29a2'
archive=ROOT/ARCHIVE
if not archive.exists():
    part=archive.with_suffix('.partial')
    urllib.request.urlretrieve(f'https://github.com/ggml-org/llama.cpp/releases/download/{VERSION}/{ARCHIVE}',part)
    if hashlib.sha256(part.read_bytes()).hexdigest()!=SHA256: raise RuntimeError('Runtime checksum mismatch.')
    part.replace(archive)
if hashlib.sha256(archive.read_bytes()).hexdigest()!=SHA256: raise RuntimeError('Runtime checksum mismatch; remove the archive and retry.')
destination=ROOT/'LightX2V.Windows/Runtime/llama-b11564-vulkan'
destination.mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(archive) as package:
    for name in package.namelist():
        if Path(name).name!=name: raise ValueError('Unexpected runtime archive path.')
        if name.endswith('.dll') or name in ('llama-completion.exe','llama-mtmd-cli.exe','LICENSE-LLVM-OpenMP'):
            (destination/name).write_bytes(package.read(name))
(destination/'LICENSE-llama.cpp.txt').write_bytes((ROOT/'LightX2V.Windows/Resources/LICENSE-llama.cpp.txt').read_bytes())
(destination/'runtime.json').write_text(json.dumps(dict(version=VERSION,backend='Vulkan',archiveSha256=SHA256),indent=2), encoding='utf-8')
print(destination)
