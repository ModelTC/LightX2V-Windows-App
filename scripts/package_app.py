"""Package an independent checkout without embedding private acceptance reports."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

from verify_package import ASSET_NAME, extraction_folder

ROOT = Path(__file__).resolve().parents[1]
RUNTIME_SPEC = json.loads((ROOT / 'scripts/llama-runtime.json').read_text(encoding='utf-8'))


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def package(app):
    root = app.resolve()
    app = root / "app"
    if not (root / "LightX2V.exe").is_file():
        raise ValueError("Missing native launcher")
    required = [
        'LightX2V.Windows.exe', 'LightX2V.Windows.dll', 'LightX2V.Windows.runtimeconfig.json',
        'coreclr.dll', 'PresentationFramework.dll', 'LICENSE.txt', 'ThirdPartyNotices.txt',
        'Resources/qwen_image_21_turbo.json', 'Resources/driver-assistants.json', 'Resources/model-catalog.json',
    ]
    for name in required:
        if not (app / name).is_file():
            raise ValueError('Incomplete publish directory: ' + name)
    for folder, pattern in [('Backend', '*.py'), ('Resources', '*.json'), ('Resources/Setup', '*')]:
        for source in sorted((ROOT / 'LightX2V.Windows' / folder).glob(pattern)):
            if source.is_file():
                target = app / folder / source.name
                if not target.is_file() or sha(source) != sha(target):
                    raise ValueError('Published source/payload differs: ' + str(target.relative_to(app)))
    # Every shipped llama runtime binary must match the pinned official ZIP.
    runtime_version = RUNTIME_SPEC['version']
    archive = ROOT / f'llama-{runtime_version}-bin-win-vulkan-x64.zip'
    if not archive.is_file() or sha(archive) != RUNTIME_SPEC['archiveSha256']:
        raise ValueError('Run prepare_runtime.py to obtain the verified runtime archive')
    runtime = app / 'Runtime' / f'llama-{runtime_version}-vulkan'
    expected = set(RUNTIME_SPEC['files'])
    with zipfile.ZipFile(archive) as official:
        for name in expected:
            if not (runtime / name).is_file() or sha(runtime / name) != hashlib.sha256(official.read(name)).hexdigest():
                raise ValueError('Runtime component differs: ' + name)
    license = 'LICENSE-llama.cpp.txt'
    if not (runtime / license).is_file() or sha(runtime / license) != sha(ROOT / 'LightX2V.Windows/Resources' / license):
        raise ValueError('Runtime license differs: ' + license)
    metadata = runtime / 'runtime.json'
    if not metadata.is_file() or json.loads(metadata.read_text(encoding='utf-8')) != dict(
        version=runtime_version, backend='Vulkan', archiveSha256=RUNTIME_SPEC['archiveSha256']
    ):
        raise ValueError('Runtime metadata differs from the pinned archive')
    if {p.name for p in runtime.iterdir()} != expected | {license, metadata.name}:
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
    docs = root / 'docs'
    shutil.copytree(ROOT / 'docs', docs, dirs_exist_ok=True)
    shutil.copy2(ROOT / 'THIRD_PARTY_NOTICES.md', docs)
    shutil.copytree(ROOT / 'third_party', docs / 'third_party', dirs_exist_ok=True)
    for name in ['LICENSE.txt', 'ThirdPartyNotices.txt']:
        shutil.move(str(app / name), docs / name)
    if {p.name for p in root.iterdir()} != {'LightX2V.exe', 'app', 'docs'}:
        raise ValueError('Unexpected top-level package files')
    try:
        commit = subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', '--verify', 'HEAD'], stderr=subprocess.DEVNULL, text=True).strip()
        dirty = bool(subprocess.check_output(['git', '-C', str(ROOT), 'status', '--porcelain'], text=True).strip())
    except (OSError, subprocess.CalledProcessError):
        commit, dirty = None, None
    payload = sorted(p for p in root.rglob('*') if p.is_file() and p != app / 'build-manifest.json')
    manifest = dict(
        target='win-x64',
        layoutVersion=2, entryPoint='LightX2V.exe', appExecutable='app/LightX2V.Windows.exe',
        sourceCommit=commit, sourceDirty=dirty,
        inferenceSource=json.loads((app/'Resources/Setup/manifest.json').read_text(encoding='utf-8'))['source'],
        backends=['amd_rocm', 'cuda'],
        verification='Compilation and package integrity only. GPU acceptance scope is documented in docs/testing.md.',
        sha256={p.relative_to(root).as_posix(): sha(p) for p in payload},
    )
    (app / 'build-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    result = root.parent / ASSET_NAME
    prefix = extraction_folder(commit)
    with zipfile.ZipFile(result, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as output:
        for path in sorted(root.rglob('*')):
            if path.is_file():
                output.write(path, prefix + '/' + path.relative_to(root).as_posix())
    result.with_suffix('.zip.sha256').write_text(sha(result) + '  ' + result.name + '\n', encoding='ascii')
    with zipfile.ZipFile(result) as output:
        if output.testzip():
            raise ValueError('ZIP integrity check failed')
    print(json.dumps(dict(package=str(result), sourceCommit=commit, sha256=sha(result), files=len(manifest['sha256']))))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    package(parser.parse_args().app)
