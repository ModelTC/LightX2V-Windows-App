"""Reproduce the bundled source from a hash-verified official ZIP."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def build_trimmed_archive(upstream, destination, excluded):
    with zipfile.ZipFile(upstream) as archive:
        prefix = archive.namelist()[0].split('/')[0] + '/'
        retained = {}
        for entry in archive.infolist():
            name = entry.filename.removeprefix(prefix)
            if not name or entry.is_dir():
                continue
            if not entry.filename.startswith(prefix) or PurePosixPath(name).is_absolute() or '..' in PurePosixPath(name).parts or '\\' in name:
                raise ValueError('Unsafe upstream archive path: ' + entry.filename)
            if any(name == path or name.startswith(path + '/') for path in excluded):
                continue
            if name in retained:
                raise ValueError('Duplicate upstream archive path: ' + name)
            retained[name] = entry
        with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED) as output:
            for name, source_entry in sorted(retained.items()):
                entry = zipfile.ZipInfo(name, (2026, 11, 9, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.create_system = 3
                entry.external_attr = 0o100644 << 16
                output.writestr(entry, archive.read(source_entry), compresslevel=9)


def rebuild(upstream, destination):
    provenance = json.loads((ROOT / 'third_party/source-provenance.json').read_text())
    if hashlib.sha256(upstream.read_bytes()).hexdigest() != provenance['upstreamArchiveSha256']:
        raise ValueError('Official archive checksum mismatch')
    with tempfile.TemporaryDirectory(dir=destination.parent) as temporary:
        candidate = Path(temporary) / 'source.zip'
        build_trimmed_archive(upstream, candidate, provenance['excludedFromBundle'])
        digest = hashlib.sha256(candidate.read_bytes()).hexdigest()
        if digest != provenance['sha256']:
            raise ValueError('Rebuilt archive differs from the pinned payload')
        candidate.replace(destination)
    print(digest)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('upstream', type=Path)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    rebuild(args.upstream, args.destination)
