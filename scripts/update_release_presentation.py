"""Review English release metadata and optionally apply it through GitHub CLI."""
import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
CHINESE = re.compile(r'[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U0002fa1f]')
VERSION = re.compile(r'v\d+\.\d+\.\d+\Z')
SHA256 = re.compile(r'sha256:[0-9a-f]{64}\Z')
BUILD_WORKFLOW = '.github/workflows/build-windows.yml'
ENVIRONMENT = 'windows-x64'


class GitHub:
    def __init__(self, repository, apply=False):
        if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
            raise ValueError('Expected an owner/repository name.')
        self.repository = repository
        self.apply = apply
        self.prefix = 'repos/' + repository
        if apply and not (os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')):
            raise ValueError('--apply requires GH_TOKEN or GITHUB_TOKEN.')

    def request(self, method, endpoint, payload=None):
        endpoint = self.prefix + '/' + endpoint
        if not self.apply:
            if method != 'GET':
                raise ValueError('Dry-run mode cannot write to GitHub.')
            request = urllib.request.Request('https://api.github.com/' + endpoint, headers={
                'Accept': 'application/vnd.github+json', 'User-Agent': 'LightX2V-release-presentation',
                'X-GitHub-Api-Version': '2022-11-28',
            })
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
        command = ['gh', 'api', '--method', method, endpoint]
        if payload is not None:
            command += ['--input', '-']
        result = subprocess.run(command, input=json.dumps(payload) if payload is not None else None,
                                capture_output=True, text=True, timeout=60)
        if result.returncode:
            diagnostic = result.stderr.strip()
            for variable in ('GH_TOKEN', 'GITHUB_TOKEN'):
                token = os.environ.get(variable)
                if token:
                    diagnostic = diagnostic.replace(token, '[redacted]')
            raise RuntimeError(f'GitHub {method} {endpoint} failed: {diagnostic[:1000]}')
        return json.loads(result.stdout)

    def pages(self, endpoint, key=None):
        items = []
        separator = '&' if '?' in endpoint else '?'
        page = 1
        while True:
            response = self.request('GET', f'{endpoint}{separator}per_page=100&page={page}')
            batch = response[key] if key else response
            if not isinstance(batch, list):
                raise ValueError('Unexpected GitHub collection response.')
            items.extend(batch)
            if len(batch) < 100:
                return items
            page += 1


def asset_snapshot(release):
    return sorted((asset['id'], asset['name'], asset['state'], asset['size'], asset.get('digest'),
                   asset['browser_download_url']) for asset in release['assets'])


def invariant_snapshot(release):
    return (release['id'], release['tag_name'], release['target_commitish'], release['draft'],
            release['prerelease'], release['published_at'], asset_snapshot(release))


def published_assets(release, require_digests=False):
    tag = release['tag_name']
    if not VERSION.fullmatch(tag) or release['draft'] or not release.get('published_at'):
        raise ValueError('Expected a published versioned release.')
    package = f'LightX2V-Windows-{tag[1:]}-win-x64.zip'
    assets = {asset['name']: asset for asset in release['assets']}
    for name in (package, package + '.sha256'):
        asset = assets.get(name)
        if not asset or asset['state'] != 'uploaded' or asset['size'] <= 0:
            raise ValueError(f'{tag} is missing a complete published asset: {name}')
        digest = asset.get('digest')
        if (require_digests or digest is not None) and not SHA256.fullmatch(digest or ''):
            raise ValueError(f'{tag} has no usable asset digest: {name}')


@dataclass
class ReleasePlan:
    original: dict
    changes: dict


def prepare_releases(client):
    releases = [release for release in client.pages('releases') if not release['draft']]
    if not releases:
        raise ValueError('No published releases were found.')
    plans = []
    seen = set()
    for release in releases:
        published_assets(release)
        tag = release['tag_name']
        if tag in seen:
            raise ValueError('Duplicate release version: ' + tag)
        seen.add(tag)
        notes = (ROOT/'docs'/'releases'/(tag + '.md')).read_text(encoding='utf-8')
        if not notes.startswith('# ' + tag + ' ') or CHINESE.search(notes):
            raise ValueError('Expected matching English source notes for ' + tag)
        name = release.get('name') or ''
        body = release.get('body') or ''
        replacement_name = tag + ' · Windows beta' if CHINESE.search(name) else name
        replacement_body = notes if CHINESE.search(body) else body
        replacement_body = re.sub(r'\]\(\.\./([^\s)]+)\)',
                                  lambda match: f'](https://github.com/{client.repository}/blob/main/docs/{match[1]})',
                                  replacement_body)
        if CHINESE.search(replacement_name) or CHINESE.search(replacement_body):
            raise ValueError('Release metadata still contains Chinese for ' + tag)
        changes = {}
        if replacement_name != name:
            changes['name'] = replacement_name
        if replacement_body != body:
            changes['body'] = replacement_body
        plans.append(ReleasePlan(release, changes))
    return plans


def apply_releases(client, plans):
    for plan in plans:
        original = plan.original
        if not plan.changes:
            continue
        current = client.request('GET', f'releases/{original["id"]}')
        if (invariant_snapshot(current) != invariant_snapshot(original) or
                current.get('name') != original.get('name') or current.get('body') != original.get('body')):
            raise ValueError('Release changed after preflight: ' + original['tag_name'])
        updated = client.request('PATCH', f'releases/{original["id"]}', plan.changes)
        if invariant_snapshot(updated) != invariant_snapshot(original):
            raise ValueError('Release version, flags or assets changed: ' + original['tag_name'])
        for field, expected in plan.changes.items():
            if updated.get(field) != expected:
                raise ValueError('GitHub did not retain the requested metadata: ' + original['tag_name'])


def tag_sha(client, tag):
    reference = client.request('GET', 'git/ref/tags/' + urllib.parse.quote(tag, safe=''))['object']
    for _ in range(5):
        if reference['type'] == 'commit':
            return reference['sha']
        if reference['type'] != 'tag':
            break
        reference = client.request('GET', 'git/tags/' + reference['sha'])['object']
    raise ValueError('Release tag does not resolve to a commit: ' + tag)


def successful_build(run, release, sha):
    return (run.get('path', '').split('@', 1)[0] == BUILD_WORKFLOW and
            run.get('head_branch') == release['tag_name'] and run.get('head_sha') == sha and
            run.get('status') == 'completed' and run.get('conclusion') == 'success')


def prepare_deployment(client, plans):
    release = max((plan.original for plan in plans), key=lambda item: item['published_at'])
    published_assets(release, require_digests=True)
    sha = tag_sha(client, release['tag_name'])
    runs = client.pages('actions/runs?head_sha=' + sha, key='workflow_runs')
    verified = [run for run in runs if successful_build(run, release, sha)]
    if not verified:
        raise ValueError('No successful tag build was found for ' + release['tag_name'])
    run = max(verified, key=lambda item: item['run_number'])
    return release, sha, run


def backfill_deployment(client, plan):
    release, sha, run = plan
    current = client.request('GET', f'releases/{release["id"]}')
    published_assets(current, require_digests=True)
    if invariant_snapshot(current) != invariant_snapshot(release) or tag_sha(client, release['tag_name']) != sha:
        raise ValueError('The published release or tag changed after deployment preflight.')
    run = client.request('GET', f'actions/runs/{run["id"]}')
    if not successful_build(run, release, sha):
        raise ValueError('The tag workflow is no longer a completed successful build.')
    query = urllib.parse.urlencode({'environment': ENVIRONMENT, 'sha': sha})
    deployments = client.pages('deployments?' + query)
    matching = [item for item in deployments if item['sha'] == sha and item['ref'] == release['tag_name'] and item['environment'] == ENVIRONMENT]
    deployment = max(matching, key=lambda item: item['id']) if matching else None
    if deployment:
        statuses = client.pages(f'deployments/{deployment["id"]}/statuses')
        if (statuses and statuses[0]['state'] == 'success' and
                statuses[0].get('environment_url') == release['html_url'] and statuses[0].get('log_url') == run['html_url']):
            return f'{release["tag_name"]} already has a successful {ENVIRONMENT} deployment.'
    else:
        deployment = client.request('POST', 'deployments', {
            'ref': release['tag_name'], 'task': 'deploy', 'environment': ENVIRONMENT,
            'auto_merge': False, 'required_contexts': [], 'transient_environment': False,
            'production_environment': False, 'description': 'Published Windows beta ' + release['tag_name'],
            'payload': {'release_id': release['id'], 'workflow_run_id': run['id'], 'backfilled': True},
        })
        if deployment.get('sha') != sha or deployment.get('ref') != release['tag_name']:
            raise ValueError('GitHub created a deployment for an unexpected ref or commit.')
    client.request('POST', f'deployments/{deployment["id"]}/statuses', {
        'state': 'success', 'environment': ENVIRONMENT, 'auto_inactive': False,
        'environment_url': release['html_url'], 'log_url': run['html_url'],
        'description': 'Published ' + release['tag_name'] + ' after a successful Windows tag build.',
    })
    return f'{release["tag_name"]} deployment records successful run {run["id"]}.'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', default=os.environ.get('GITHUB_REPOSITORY', 'ModelTC/LightX2V-Windows-App'))
    parser.add_argument('--apply', action='store_true', help='Apply metadata-only changes using authenticated GitHub CLI.')
    parser.add_argument('--backfill-deployment', action='store_true', help='Record the latest published release after verifying its successful tag build.')
    args = parser.parse_args()
    client = GitHub(args.repository, args.apply)
    plans = prepare_releases(client)  # Validate every release before the first write.
    for plan in plans:
        print(plan.original['tag_name'] + ': ' + (', '.join(plan.changes) or 'already English'))
    deployment = None
    deployment_result = 'Deployment backfill was not requested.'
    if args.backfill_deployment:
        try:
            deployment = prepare_deployment(client, plans)
            release, sha, run = deployment
            deployment_result = f'Eligible deployment: {release["tag_name"]}, commit {sha}, successful run {run["id"]}.'
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
            deployment_result = 'Deployment backfill skipped: ' + str(error)
    if args.apply:
        apply_releases(client, plans)
        if deployment:
            try:
                deployment_result = backfill_deployment(client, deployment)
            except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
                deployment_result = 'Deployment backfill failed after release metadata succeeded: ' + str(error)
    print(deployment_result)
    print(f'{len(plans)} published releases validated. Mode: {"apply" if args.apply else "dry-run"}.')
    if args.apply and os.environ.get('GITHUB_STEP_SUMMARY'):
        summary = f'English release presentation checked for {len(plans)} published releases.\n\n{deployment_result}\n'
        with Path(os.environ['GITHUB_STEP_SUMMARY']).open('a', encoding='utf-8') as stream:
            stream.write(summary)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print('Release presentation failed: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
