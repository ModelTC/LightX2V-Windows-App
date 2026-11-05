"""Ensure both uploaded draft assets are present before making a beta public."""
import argparse
import hashlib
import json
from pathlib import Path


def verify(report, package, tag):
    data = json.loads(report.read_text(encoding='utf-8'))
    if data['tag_name'] != tag or data['draft'] is not True or data['prerelease'] is not True:
        raise ValueError('Expected a draft prerelease for the requested version')
    expected = {p.name: p for p in [package, package.with_suffix('.zip.sha256')]}
    assets = data['assets']
    if len(assets) != len(expected) or {a['name'] for a in assets} != set(expected):
        raise ValueError('Draft assets do not match the two release files')
    for asset in assets:
        path = expected[asset['name']]
        if asset['state'] != 'uploaded' or asset['size'] != path.stat().st_size:
            raise ValueError('Incomplete release upload: ' + path.name)
        if asset.get('digest') and asset['digest'] != 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest():
            raise ValueError('Uploaded asset checksum differs: ' + path.name)
    print('Draft prerelease assets verified; ready to publish.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path)
    parser.add_argument('package', type=Path)
    parser.add_argument('--tag', required=True)
    args = parser.parse_args()
    verify(args.report, args.package, args.tag)
