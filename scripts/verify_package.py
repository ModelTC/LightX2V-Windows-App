"""Check download SHA-256, every payload file, and the exact release source commit."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import zipfile


ASSET_NAME = 'LightX2V-APP-Windows-x64.zip'
EXTRACTION_FOLDER = 'LightX2V-Windows'


def valid_commit(value):
    return isinstance(value, str) and re.fullmatch(r'[0-9a-f]{40}', value) is not None


def extraction_folder(source_commit):
    return f'{EXTRACTION_FOLDER}-{source_commit[:7]}' if valid_commit(source_commit) else EXTRACTION_FOLDER


def verify(package, version=None, source_commit=None, require_clean=False):
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    checksum = package.with_suffix('.zip.sha256').read_text(encoding='ascii').strip().split()
    if checksum != [digest, package.name]:
        raise ValueError('Download checksum does not match the ZIP')
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate package entries')
        if not names or '/' not in names[0]:
            raise ValueError('Missing extraction folder')
        folder = names[0].split('/', 1)[0]
        prefix = folder + '/'
        for name in names:
            if not name.startswith(prefix) or '..' in PurePosixPath(name).parts or '\\' in name:
                raise ValueError('Unexpected package entry')
        manifest_name = 'app/build-manifest.json' if prefix+'app/build-manifest.json' in names else 'build-manifest.json'
        manifest = json.loads(archive.read(prefix + manifest_name))
        commit = manifest.get('sourceCommit')
        layout = manifest.get('layoutVersion', 1)
        legacy_version = manifest.get('version')
        if 'version' in manifest:
            if not isinstance(legacy_version, str) or not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', legacy_version):
                raise ValueError('Invalid legacy package version')
            if version is not None and version != legacy_version:
                raise ValueError('Package version differs from the requested legacy version')
            legacy_folder = f'{EXTRACTION_FOLDER}-{legacy_version}'
            folders = {legacy_folder}
            if valid_commit(commit):
                folders.add(f'{legacy_folder}-{commit[:7]}')
            if package.name not in (ASSET_NAME, f'{legacy_folder}-win-x64.zip') or manifest.get('channel') not in ('internal-beta', 'latest'):
                raise ValueError('Invalid legacy package metadata')
        else:
            folders = {extraction_folder(commit)}
            if version is not None or layout != 2 or package.name != ASSET_NAME or 'channel' in manifest:
                raise ValueError('Invalid package metadata')
        if folder not in folders:
            raise ValueError('Extraction folder does not match the package version or source commit')
        if layout == 2:
            if manifest_name != 'app/build-manifest.json' or manifest.get('entryPoint') != 'LightX2V.exe' or manifest.get('appExecutable') != 'app/LightX2V.Windows.exe':
                raise ValueError('Invalid package layout')
            if {n[len(prefix):].split('/')[0] for n in names} != {'LightX2V.exe', 'app', 'docs'}:
                raise ValueError('Unexpected top-level package contents')
        elif layout != 1:
            raise ValueError('Unsupported package layout')
        if manifest.get('target') != 'win-x64':
            raise ValueError('Invalid package target')
        if source_commit and commit != source_commit:
            raise ValueError('Package was built from a different source commit')
        if require_clean and (manifest.get('sourceDirty') is not False or not valid_commit(commit)):
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
    result = dict(sourceCommit=commit, sha256=digest, files=len(hashes))
    if legacy_version is not None:
        result['version'] = legacy_version
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('package', type=Path)
    parser.add_argument('--version', help='Expected version for a legacy numbered package')
    parser.add_argument('--source-commit')
    parser.add_argument('--require-clean', action='store_true')
    args = parser.parse_args()
    print(json.dumps(verify(args.package, args.version, args.source_commit, args.require_clean)))
