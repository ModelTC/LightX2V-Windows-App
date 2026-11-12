#!/usr/bin/env python3
"""Maintain one non-expiring APP per release channel; called under workflow concurrency control."""
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

ASSET_NAME = "LightX2V-APP-Windows-x64.zip"
PREFIX = "LightX2V-APP-Windows-x64-"
DISPLAY_TIMEZONE = timezone(timedelta(hours=8))
DATE_FORMAT = "%Y-%m-%d %H:%M"


class GitHub:
    def __init__(self, repository, token):
        self.repository = repository
        self.token = token

    def _request(self, method, host, path, data=None, content_type="application/json", missing_ok=False):
        request = urllib.request.Request(
            f"https://{host}/repos/{self.repository}/{path}", data=data, method=method,
            headers={"Authorization": "Bearer " + self.token, "Accept": "application/vnd.github+json",
                     "X-GitHub-Api-Version": "2022-11-28", "Content-Type": content_type})
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                body = response.read()
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            if error.code == 404 and missing_ok:
                return None
            raise

    def api(self, method, path, payload=None, missing_ok=False):
        data = json.dumps(payload).encode() if payload is not None else None
        return self._request(method, "api.github.com", path, data, missing_ok=missing_ok)

    def upload(self, release_id, name, data):
        path = f"releases/{release_id}/assets?" + urllib.parse.urlencode({"name": name})
        return self._request("POST", "uploads.github.com", path, data, "application/zip")


def list_all(client, path, key=None):
    items = []
    for page in range(1, 1001):
        response = client.api("GET", f"{path}?per_page=100&page={page}")
        batch = response[key] if key else response
        items.extend(batch)
        if len(batch) < 100:
            return items
    raise RuntimeError("Too many results; refusing incomplete cleanup")


def publish(client, archive, sha, run_number, run_id, attempt, tag="latest"):
    if tag not in {"latest", "stable"}:
        raise ValueError("Unknown release channel")
    title = "Latest build" if tag == "latest" else "Stable"
    blob = archive.read_bytes()
    digest = hashlib.sha256(blob).hexdigest()
    release = client.api("GET", f"releases/tags/{tag}", missing_ok=True)
    if release is None:
        drafts=[item for item in list_all(client,"releases") if item.get("tag_name")==tag]
        if len(drafts)>1: raise RuntimeError("Multiple releases for one channel; refusing ambiguous recovery")
        release=drafts[0] if drafts else None
    old = None
    if release:
        if release.get("immutable"):
            raise RuntimeError("The release is immutable and cannot be replaced")
        old = next((a for a in release["assets"] if a["name"] == ASSET_NAME), None)
        previous_run = re.search(r"lightx2v-run:(\d+)", (old or {}).get("label") or "")
        if tag == "latest" and previous_run and int(previous_run[1]) > run_number:
            print("A newer successful build is already published; leaving it unchanged.")
            return False
    else:
        release = client.api("POST", "releases", {
            "tag_name": tag, "target_commitish": sha, "draft": True,
            "name": f"LightX2V · {title}", "body": "Preparing the first build."})

    # Stage and verify the new upload before changing the working download.
    staged = client.upload(release["id"], f"{PREFIX}{run_id}-{attempt}.zip", blob)
    if (staged.get("state") != "uploaded" or staged.get("size") != len(blob)
            or staged.get("digest") != "sha256:" + digest):
        raise RuntimeError("Uploaded asset verification failed; previous download retained")

    old_path = f"releases/assets/{old['id']}" if old else None
    reference=client.api("GET",f"git/ref/tags/{tag}",missing_ok=True)
    renamed=False
    ref_changed=False
    try:
        if old:
            client.api("PATCH", old_path, {"name": f"{PREFIX}backup-{old['id']}.zip"})
        client.api("PATCH", f"releases/assets/{staged['id']}", {
            "name": ASSET_NAME, "label": f"lightx2v-run:{run_number}; commit:{sha}"})
        renamed=True
        if reference:
            client.api("PATCH", f"git/refs/tags/{tag}", {"sha": sha, "force": True})
        else:
            client.api("POST", "git/refs", {"ref": f"refs/tags/{tag}", "sha": sha})
        ref_changed=True
        url = f"https://github.com/{client.repository}"
        retention = ("Updated after every successful build; only one Latest APP is kept."
                     if tag == "latest" else "Updated only by manual promotion; only one Stable APP is kept.")
        notes = (
            f"[Download {tag.title()} APP]({url}/releases/download/{tag}/{ASSET_NAME})\n\n"
            f"{retention} Downloads do not expire automatically.\n\n"
            f"- Commit: `{sha}`\n- [Publication workflow]({url}/actions/runs/{run_id})\n"
            "- Windows 11 / x64\n- Unsigned portable package\n"
            "- Includes .NET and pinned inference source; prepare models and Python in the app\n\n"
            f"SHA-256:\n```text\n{digest}  {ASSET_NAME}\n```\n")
        client.api("PATCH", f"releases/{release['id']}", {
            "name": f"LightX2V · {title}", "target_commitish": sha,
            "body": notes, "draft": False, "prerelease": False, "make_latest": "true" if tag == "latest" else "false"})
    except Exception as error:
        rollback_errors=[]
        def restore(method,path,payload=None):
            try: client.api(method,path,payload)
            except Exception as failure: rollback_errors.append(str(failure))
        if renamed:
            restore("PATCH",f"releases/assets/{staged['id']}",{"name":f"{PREFIX}failed-{staged['id']}.zip"})
        if old:
            restore("PATCH",old_path,{"name":ASSET_NAME})
        if ref_changed:
            if reference: restore("PATCH",f"git/refs/tags/{tag}",{"sha":reference['object']['sha'],"force":True})
            else: restore("DELETE",f"git/refs/tags/{tag}")
        # A failed first release remains a recoverable draft.
        metadata={key:release[key] for key in ('name','target_commitish','body','draft','prerelease') if key in release}
        if metadata: restore("PATCH",f"releases/{release['id']}",metadata)
        if rollback_errors:
            raise RuntimeError("Publication failed and rollback needs manual recovery: "+"; ".join(rollback_errors)) from error
        raise

    # Only retire APP assets managed by this workflow, after successful publication.
    assets = list_all(client, f"releases/{release['id']}/assets")
    for asset in assets:
        if asset["id"] != staged["id"] and asset["name"].startswith(PREFIX) and asset["name"].endswith(".zip"):
            client.api("DELETE", f"releases/assets/{asset['id']}")
    print(f"Published {url}/releases/download/{tag}/{ASSET_NAME}\nSHA-256: {digest}")
    return True



if __name__ == "__main__":
    from verify_package import verify
    archive=Path(sys.argv[1])
    import zipfile
    with zipfile.ZipFile(archive) as package:
        name=next(n for n in package.namelist() if n.endswith('/build-manifest.json'))
        version=json.loads(package.read(name))['version']
    verify(archive,version,os.environ['GITHUB_SHA'],True)
    publish(GitHub(os.environ['GITHUB_REPOSITORY'],os.environ['GITHUB_TOKEN']),archive,
            os.environ['GITHUB_SHA'],int(os.environ['GITHUB_RUN_NUMBER']),os.environ['GITHUB_RUN_ID'],os.environ['GITHUB_RUN_ATTEMPT'])
