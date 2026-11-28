#!/usr/bin/env python3
"""Promote the existing Latest package without rebuilding or changing Latest."""
import hashlib
import os
from pathlib import Path
import re
import tempfile
import urllib.request

from publish_latest import ASSET_NAME, INSTALLER, GitHub, publish, record_download
from verify_package import verify


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
        archive.with_suffix('.zip.sha256').write_text(hashlib.sha256(data).hexdigest()+'  '+ASSET_NAME+'\n',encoding='ascii')
        verify(archive, source_commit=sha, require_clean=True)
        setup = next(a for a in release["assets"] if a["name"] == INSTALLER)
        if setup["label"] != asset["label"]:
            raise RuntimeError("Latest installer and update package have different provenance")
        with urllib.request.urlopen(setup["browser_download_url"], timeout=120) as response:
            installer = response.read()
        if len(installer) != setup["size"] or "sha256:" + hashlib.sha256(installer).hexdigest() != setup["digest"]:
            raise RuntimeError("Latest installer digest mismatch")
        archive.with_name(INSTALLER).write_bytes(installer)
        date = publish(client, archive, sha, run_number, run_id, attempt,
                       tag="stable", original_upload_time=asset["created_at"])
        record_download(client, "stable", sha, date)



if __name__ == "__main__":
    promote(GitHub(os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_TOKEN"]),
            int(os.environ["GITHUB_RUN_NUMBER"]), os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"])
