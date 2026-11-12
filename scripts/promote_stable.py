#!/usr/bin/env python3
"""Promote the existing Latest package without rebuilding or changing Latest."""
import hashlib
import os
from pathlib import Path
import re
import tempfile
import urllib.request
import zipfile

from publish_latest import ASSET_NAME, GitHub, publish


def promote(client, run_number, run_id, attempt):
    release = client.api("GET", "releases/tags/latest")
    if release.get("draft"):
        raise RuntimeError("Latest is not published")
    asset = next(a for a in release["assets"] if a["name"] == ASSET_NAME)
    match = re.search(r"commit:([0-9a-f]{40})", asset.get("label") or "")
    if not match:
        raise RuntimeError("Latest package has no verified source commit")
    sha = match[1]
    reference = client.api("GET", "git/ref/tags/latest")
    if reference["object"]["sha"] != sha:
        raise RuntimeError("Latest tag does not match its package")
    request = urllib.request.Request(asset["browser_download_url"], headers={"User-Agent": "LightX2V-stable-promotion"})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read()
    if len(data) != asset["size"] or "sha256:" + hashlib.sha256(data).hexdigest() != asset["digest"]:
        raise RuntimeError("Latest download does not match the released package digest")
    with tempfile.TemporaryDirectory(prefix="lightx2v-stable-") as directory:
        archive = Path(directory) / ASSET_NAME
        archive.write_bytes(data)
        from verify_package import verify
        with zipfile.ZipFile(archive) as package:
            name=next(n for n in package.namelist() if n.endswith('/build-manifest.json'))
            version=__import__('json').loads(package.read(name))['version']
        archive.with_suffix('.zip.sha256').write_text(hashlib.sha256(data).hexdigest()+'  '+ASSET_NAME+'\n',encoding='ascii')
        verify(archive,version,sha,True)
        publish(client, archive, sha, run_number, run_id, attempt, tag="stable")



if __name__ == "__main__":
    promote(GitHub(os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_TOKEN"]),
            int(os.environ["GITHUB_RUN_NUMBER"]), os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])
