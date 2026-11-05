"""Audit source candidates (or the Git index) without printing sensitive values."""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {'.git', 'bin', 'obj', '__pycache__', 'dist', 'artifacts', 'Runtime', '.venv'}
FORBIDDEN = {'qa', 'models', 'environments', 'outputs', 'UserData', '.env', '.DS_Store', 'source.zip'}
ARCHIVE = 'LightX2V.Windows/Resources/Setup/lightx2v-native.zip'
SECRET = re.compile(r'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|-----BEGIN (?:OPENSSH|RSA|EC|DSA) PRIVATE KEY-----|AKIA[0-9A-Z]{16})')


def candidates():
    try:
        git_root = Path(subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', '--show-toplevel'], stderr=subprocess.DEVNULL, text=True).strip()).resolve()
        if git_root == ROOT:
            entries = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files', '-z']).decode().split('\0')
            if any(entries):
                return [ROOT / name for name in entries if name]
    except (OSError, subprocess.CalledProcessError):
        pass
    return [p for p in ROOT.rglob('*') if p.is_file() and not set(p.relative_to(ROOT).parts) & EXCLUDED and not p.name.startswith('llama-')]


def main():
    files = candidates()
    for path in files:
        relative = path.relative_to(ROOT)
        if path.is_symlink() or not path.is_file():
            raise ValueError('Non-regular source candidate: ' + str(relative))
        if set(relative.parts) & (EXCLUDED | FORBIDDEN) or path.suffix.lower() in ('.exe', '.dll', '.safetensors', '.gguf', '.whl', '.log', '.pyc', '.dmp'):
            raise ValueError('Generated or private file in source candidates: ' + str(relative))
        if path.suffix == '.zip' and relative.as_posix() != ARCHIVE:
            raise ValueError('Unexpected source archive: ' + str(relative))
        if path.stat().st_size > 2 * 1024 * 1024 and relative.as_posix() != ARCHIVE:
            raise ValueError('Unexpected large file: ' + str(relative))
        if path.suffix not in ('.zip', '.ico', '.png'):
            if SECRET.search(path.read_text(encoding='utf-8-sig')):
                raise ValueError('Potential credential in: ' + str(relative))
    provenance = json.loads((ROOT / 'third_party/source-provenance.json').read_text(encoding='utf-8'))
    archive = ROOT / ARCHIVE
    if hashlib.sha256(archive.read_bytes()).hexdigest() != provenance['sha256']:
        raise ValueError('Source provenance and archive do not match')
    with zipfile.ZipFile(archive) as bundled:
        for entry in bundled.infolist():
            path = Path(entry.filename)
            if path.is_absolute() or '..' in path.parts or set(path.parts) & {'__pycache__', '.git', '.env'}:
                raise ValueError('Unexpected bundled source path: ' + entry.filename)
            if path.suffix in ('.py', '.json', '.yaml', '.yml', '.sh', '.md', '.txt') and SECRET.search(bundled.read(entry).decode('utf-8', errors='replace')):
                raise ValueError('Potential credential in bundled source: ' + entry.filename)
        if bundled.read('LICENSE') != (ROOT / 'third_party/licenses/LightX2V-Apache-2.0.txt').read_bytes():
            raise ValueError('Bundled license differs')
    print(f'Repository audit passed: {len(files)} source files; fixed source archive and retained license verified.')


if __name__ == '__main__':
    main()
