"""Package an independent checkout without embedding private acceptance reports."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def package(app):
    app = app.resolve()
    version = ET.parse(ROOT / 'LightX2V.Windows/LightX2V.Windows.csproj').findtext('./PropertyGroup/Version')
    if not version or not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Invalid project version')
    required = [
        'LightX2V.Windows.exe', 'LightX2V.Windows.dll', 'LightX2V.Windows.runtimeconfig.json',
        'coreclr.dll', 'PresentationFramework.dll', 'LICENSE.txt', 'ThirdPartyNotices.txt',
        'Resources/qwen_image_21_turbo.json', 'Resources/driver-assistants.json',
        'Runtime/llama-b11564-vulkan/llama-completion.exe',
        'Runtime/llama-b11564-vulkan/llama-mtmd-cli.exe',
        'Runtime/llama-b11564-vulkan/LICENSE-llama.cpp.txt',
        'Runtime/llama-b11564-vulkan/LICENSE-LLVM-OpenMP',
    ]
    for name in required:
        if not (app / name).is_file():
            raise ValueError('Incomplete publish directory: ' + name)
    for folder, pattern in [('Backend', '*.py'), ('Resources/Setup', '*')]:
        for source in sorted((ROOT / 'LightX2V.Windows' / folder).glob(pattern)):
            if source.is_file():
                target = app / folder / source.name
                if not target.is_file() or sha(source) != sha(target):
                    raise ValueError('Published source/payload differs: ' + str(target.relative_to(app)))
    # Every shipped llama runtime binary must match the pinned official ZIP.
    archive = ROOT / 'llama-b11564-bin-win-vulkan-x64.zip'
    if not archive.is_file() or sha(archive) != '1a1b260a2240a12cac7a94590fa88d06ae25d349db9d556e9c2503f7d22e29a2':
        raise ValueError('Run prepare_runtime.py to obtain the verified runtime archive')
    runtime = app / 'Runtime/llama-b11564-vulkan'
    with zipfile.ZipFile(archive) as official:
        expected = {n for n in official.namelist() if n.endswith('.dll') or n in (
            'llama-completion.exe', 'llama-mtmd-cli.exe', 'LICENSE-LLVM-OpenMP')}
        for name in expected:
            if not (runtime / name).is_file() or sha(runtime / name) != hashlib.sha256(official.read(name)).hexdigest():
                raise ValueError('Runtime component differs: ' + name)
        if {p.name for p in runtime.iterdir()} != expected | {'LICENSE-llama.cpp.txt', 'runtime.json'}:
            raise ValueError('Unexpected runtime contents')
    # Reject evidence, caches and settings; do not silently publish or discard them.
    for path in app.rglob('*'):
        relative = path.relative_to(app)
        if path.is_symlink():
            raise ValueError('Symlinks cannot be packaged: ' + str(relative))
        if set(relative.parts) & {'UserData', '__pycache__', 'qa', 'outputs', 'models', 'environments'} or path.name in (
            'preferences.json', 'workspace.json', 'hardware-detection.json') or path.suffix.lower() in (
                '.log', '.pdb', '.gguf', '.safetensors', '.whl', '.partial'):
            raise ValueError('Unexpected private/generated file in publish directory: ' + str(relative))
    for name in ['README.md', 'README.zh-CN.md', 'THIRD_PARTY_NOTICES.md']:
        shutil.copy2(ROOT / name, app / name)
    shutil.copytree(ROOT / 'docs', app / 'docs', dirs_exist_ok=True)
    shutil.copytree(ROOT / 'third_party', app / 'third_party', dirs_exist_ok=True)
    try:
        commit = subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', '--verify', 'HEAD'], stderr=subprocess.DEVNULL, text=True).strip()
        dirty = bool(subprocess.check_output(['git', '-C', str(ROOT), 'status', '--porcelain'], text=True).strip())
    except (OSError, subprocess.CalledProcessError):
        commit, dirty = None, None
    payload = sorted(p for p in app.rglob('*') if p.is_file() and p.name != 'build-manifest.json')
    manifest = dict(
        version=version, channel='internal-beta', target='win-x64',
        sourceCommit=commit, sourceDirty=dirty,
        backends=['amd_rocm', 'cuda'], cudaHardwareAcceptance='pending',
        verification='Compilation and package integrity only. GPU acceptance scope is documented in docs/testing.md.',
        sha256={p.relative_to(app).as_posix(): sha(p) for p in payload},
    )
    (app / 'build-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    result = app.parent / f'LightX2V-Windows-{version}-win-x64.zip'
    prefix = f'LightX2V-Windows-{version}'
    with zipfile.ZipFile(result, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as output:
        for path in sorted(app.rglob('*')):
            if path.is_file():
                output.write(path, prefix + '/' + path.relative_to(app).as_posix())
    result.with_suffix('.zip.sha256').write_text(sha(result) + '  ' + result.name + '\n', encoding='ascii')
    with zipfile.ZipFile(result) as output:
        if output.testzip():
            raise ValueError('ZIP integrity check failed')
    print(json.dumps(dict(package=str(result), sha256=sha(result), files=len(manifest['sha256']), version=version)))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    package(parser.parse_args().app)
