"""Fetch the pinned official Windows Vulkan runtime used by the PE adapter."""
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

ROOT=Path(__file__).resolve().parent
spec = json.loads((ROOT / 'scripts/llama-runtime.json').read_text(encoding='utf-8'))
VERSION = spec['version']
ARCHIVE=f'llama-{VERSION}-bin-win-vulkan-x64.zip'
SHA256 = spec['archiveSha256']
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
    names = set(package.namelist())
    if any(Path(name).name != name or '/' in name or '\\' in name for name in names):
        raise ValueError('Unexpected runtime archive path.')
    # Preserve the CPU and Vulkan backends that ggml loads at runtime, in addition
    # to the DLLs imported by the two prompt-enhancement executables.
    expected = set(spec['files'])
    if not expected <= names:
        raise ValueError('The pinned archive is missing required runtime files.')
    for name in sorted(expected):
        (destination / name).write_bytes(package.read(name))
    # Remove former outputs on incremental builds only if they match the official archive.
    for name in sorted(names - expected):
        stale = destination / name
        if stale.is_file() and stale.read_bytes() == package.read(name):
            stale.unlink()
(destination/'LICENSE-llama.cpp.txt').write_bytes((ROOT/'LightX2V.Windows/Resources/LICENSE-llama.cpp.txt').read_bytes())
(destination/'runtime.json').write_text(json.dumps(dict(version=VERSION,backend='Vulkan',archiveSha256=SHA256),indent=2), encoding='utf-8')
print(destination)
