"""Download and verify an existing draft's assets, then publish without replacing them."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

from verify_package import verify as verify_package
from verify_release_assets import verify as verify_assets


def api(*args):
    return json.loads(subprocess.check_output(['gh', 'api', *args], text=True, encoding='utf-8'))


def publish(tag, commit, directory):
    if not re.fullmatch(r'v\d+\.\d+\.\d+', tag) or not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Expected an exact version tag and source commit')
    repo = os.environ['GH_REPO']
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
        raise ValueError('Invalid repository')
    # The tag endpoint does not return unpublished drafts. Authenticated list
    # requests include drafts; select exactly one matching tag from all pages.
    pages = api('--paginate', '--slurp', f'repos/{repo}/releases?per_page=100')
    matches = [r for page in pages for r in page if r['tag_name'] == tag]
    if len(matches) != 1:
        raise ValueError('Expected exactly one existing release draft')
    release = matches[0]
    if release['draft'] is not True or release['prerelease'] is not True:
        raise ValueError('Refusing to modify an already published release or a stable release')
    expected = {f'LightX2V-Windows-{tag[1:]}-win-x64.zip', f'LightX2V-Windows-{tag[1:]}-win-x64.zip.sha256'}
    assets = release['assets']
    if len(assets) != 2 or {a['name'] for a in assets} != expected or any(a['state'] != 'uploaded' for a in assets):
        raise ValueError('Expected two completely uploaded beta assets')
    directory.mkdir(parents=True, exist_ok=False)
    for asset in assets:
        path = directory / asset['name']
        # gh's HTTP client handles authenticated asset redirects; no token is
        # exported or forwarded by custom urllib redirect handling.
        with path.open('wb') as output:
            subprocess.run(['gh', 'api', f'repos/{repo}/releases/assets/{int(asset["id"])}', '-H', 'Accept: application/octet-stream'], stdout=output, check=True)
    report = directory / 'release.json'
    report.write_text(json.dumps(release), encoding='utf-8')
    package = directory / f'LightX2V-Windows-{tag[1:]}-win-x64.zip'
    verify_assets(report, package, tag)
    verified = verify_package(package, tag[1:], commit, True)
    print(json.dumps(verified), flush=True)
    result = api('--method', 'PATCH', f'repos/{repo}/releases/{int(release["id"])}', '-F', 'draft=false', '-F', 'prerelease=true', '-f', 'make_latest=false')
    if result['draft'] is not False or result['prerelease'] is not True or result['tag_name'] != tag:
        raise ValueError('Unexpected publication result')
    print(result['html_url'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('tag')
    parser.add_argument('source_commit')
    parser.add_argument('--directory', required=True, type=Path)
    args = parser.parse_args()
    publish(args.tag, args.source_commit, args.directory)
