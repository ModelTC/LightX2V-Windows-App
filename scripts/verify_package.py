"""Check download SHA-256, every payload file, and the exact release source commit."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import zipfile


def verify(package, version, source_commit=None, require_clean=False):
    expected_name = f'LightX2V-Windows-{version}-win-x64.zip'
    if package.name not in (expected_name,"LightX2V-APP-Windows-x64.zip") or not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Package filename and version differ')
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    checksum = package.with_suffix('.zip.sha256').read_text(encoding='ascii').strip().split()
    if checksum != [digest, package.name]:
        raise ValueError('Download checksum does not match the ZIP')
    prefix = f'LightX2V-Windows-{version}/'
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate package entries')
        for name in names:
            if not name.startswith(prefix) or '..' in PurePosixPath(name).parts or '\\' in name:
                raise ValueError('Unexpected package entry')
        manifest_name = 'app/build-manifest.json' if prefix+'app/build-manifest.json' in names else 'build-manifest.json'
        manifest = json.loads(archive.read(prefix + manifest_name))
        if manifest.get('layoutVersion', 1) == 2:
            if manifest_name != 'app/build-manifest.json' or manifest.get('entryPoint') != 'LightX2V.exe' or manifest.get('appExecutable') != 'app/LightX2V.Windows.exe':
                raise ValueError('Invalid package layout')
            if {n[len(prefix):].split('/')[0] for n in names} != {'LightX2V.exe', 'app', 'docs'}:
                raise ValueError('Unexpected top-level package contents')
        elif manifest.get('layoutVersion', 1) != 1:
            raise ValueError('Unsupported package layout')
        if manifest['version'] != version or manifest['target'] != 'win-x64' or manifest['channel'] not in ('internal-beta','latest'):
            raise ValueError('Package metadata differs from the requested beta')
        if source_commit and manifest.get('sourceCommit') != source_commit:
            raise ValueError('Package was built from a different source commit')
        if require_clean and (manifest.get('sourceDirty') is not False or not re.fullmatch(r'[0-9a-f]{40}', manifest.get('sourceCommit') or '')):
            raise ValueError('Release requires a clean, committed source checkout')
        hashes = manifest['sha256']
        if set(names) != {prefix + name for name in hashes} | {prefix + manifest_name}:
            raise ValueError('The manifest does not cover the complete ZIP')
        if manifest.get('layoutVersion') == 2 and 'inferenceSource' in manifest:
            for profile in ('manifest.json', 'manifest-cuda.json'):
                source = json.loads(archive.read(prefix + 'app/Resources/Setup/' + profile))['source']
                if source != manifest['inferenceSource']:
                    raise ValueError('Inference source provenance differs between profiles')
            source_name = prefix + 'app/Resources/Setup/' + source['archive']
            if hashlib.sha256(archive.read(source_name)).hexdigest() != source['sha256']:
                raise ValueError('Bundled inference source checksum differs')
        for name, expected in hashes.items():
            if hashlib.sha256(archive.read(prefix + name)).hexdigest() != expected:
                raise ValueError('Payload checksum mismatch: ' + name)
    return dict(version=version, sourceCommit=manifest.get('sourceCommit'), sha256=digest, files=len(hashes))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', type=Path)
    parser.add_argument('--version', required=True)
    parser.add_argument('--source-commit')
    parser.add_argument('--require-clean', action='store_true')
    args = parser.parse_args()
    print(json.dumps(verify(args.package, args.version, args.source_commit, args.require_clean)))
