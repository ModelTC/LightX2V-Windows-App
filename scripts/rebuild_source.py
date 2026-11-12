"""Reproduce the bundled source from a hash-verified official ZIP and reviewed patch."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def rebuild(upstream, destination):
    provenance = json.loads((ROOT / 'third_party/source-provenance.json').read_text())
    if hashlib.sha256(upstream.read_bytes()).hexdigest() != provenance['upstreamArchiveSha256']:
        raise ValueError('Official archive checksum mismatch')
    patch = ROOT / provenance['patch']
    if hashlib.sha256(patch.read_bytes()).hexdigest() != provenance['patchSha256']:
        raise ValueError('Compatibility patch checksum mismatch')
    with tempfile.TemporaryDirectory() as temporary, zipfile.ZipFile(upstream) as archive:
        root = Path(temporary)
        prefix = archive.namelist()[0].split('/')[0] + '/'
        names = []
        for entry in archive.infolist():
            name = entry.filename.removeprefix(prefix)
            if not name or entry.is_dir():
                continue
            if not entry.filename.startswith(prefix) or PurePosixPath(name).is_absolute() or '..' in PurePosixPath(name).parts or '\\' in name:
                raise ValueError('Unsafe upstream archive path')
            target = root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(entry))
            names.append(name)
        subprocess.run(['git', 'apply', '--check', str(patch)], cwd=root, check=True)
        subprocess.run(['git', 'apply', str(patch)], cwd=root, check=True)
        with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as output:
            for name in sorted(names):
                entry = zipfile.ZipInfo(name, (2026, 11, 9, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.external_attr = 0o100644 << 16
                output.writestr(entry, (root / name).read_bytes())
    digest = hashlib.sha256(destination.read_bytes()).hexdigest()
    if digest != provenance['sha256']:
        raise ValueError('Rebuilt archive differs from the pinned payload')
    print(digest)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('upstream', type=Path)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    rebuild(args.upstream, args.destination)
