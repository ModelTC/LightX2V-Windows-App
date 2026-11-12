"""Exercise rolling publication without making network requests."""
import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[1] / 'scripts' / 'publish_latest.py'
spec = importlib.util.spec_from_file_location('rolling_publisher', SOURCE)
publisher = importlib.util.module_from_spec(spec); spec.loader.exec_module(publisher)


class GitHubFixture:
    repository = 'fixture/LightX2V'

    def __init__(self, bad_digest=False, previous_run=1, fail_rename=False, fail_ref=False, fail_metadata=False):
        self.calls = []
        self.bad_digest = bad_digest
        self.fail_rename = fail_rename
        self.fail_ref=fail_ref
        self.fail_metadata=fail_metadata
        self.old = {'id': 1, 'name': publisher.ASSET_NAME, 'label': f'lightx2v-run:{previous_run}; commit:' + 'a'*40}

    def api(self, method, path, payload=None, missing_ok=False):
        self.calls.append((method, path, payload))
        if path == 'releases/tags/latest':
            return {'id': 20, 'assets': [self.old], 'name':'Previous', 'body':'Previous notes', 'target_commitish':'a'*40, 'draft':False, 'prerelease':False}
        if path == 'git/ref/tags/latest':
            return {'object': {'sha': 'a'*40}}
        if method == 'GET' and path.startswith('releases/20/assets?'):
            return [self.old, {'id': 2, 'name': publisher.ASSET_NAME}]
        if method == 'PATCH' and path == 'releases/assets/1':
            self.old.update(payload)
        if self.fail_rename and method == 'PATCH' and path == 'releases/assets/2' and payload.get('name') == publisher.ASSET_NAME:
            raise RuntimeError('simulated rename failure')
        if self.fail_ref and method == 'PATCH' and path == 'git/refs/tags/latest':
            raise RuntimeError('simulated ref update failure')
        if self.fail_metadata and method=='PATCH' and path=='releases/20' and payload.get('target_commitish')=='b'*40:
            raise RuntimeError('simulated metadata update failure')
        return {}

    def upload(self, release, name, data):
        self.calls.append(('UPLOAD', name, None))
        return {'id': 2, 'state': 'uploaded', 'size': len(data), 'digest': 'sha256:' + ('0'*64 if self.bad_digest else hashlib.sha256(data).hexdigest())}


class PublicationTests(unittest.TestCase):
    def publish(self, fixture):
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / publisher.ASSET_NAME; archive.write_bytes(b'verified fixture package')
            return publisher.publish(fixture, archive, 'b'*40, 2, '3', '1')

    def test_failed_upload_keeps_working_asset_and_tag(self):
        fixture = GitHubFixture(bad_digest=True)
        with self.assertRaises(RuntimeError):
            self.publish(fixture)
        self.assertFalse(any(method in ('PATCH', 'DELETE') for method, _, _ in fixture.calls))

    def test_failed_rename_restores_previous_download_name(self):
        fixture = GitHubFixture(fail_rename=True)
        with self.assertRaises(RuntimeError):
            self.publish(fixture)
        self.assertIn(('PATCH', 'releases/assets/1', {'name': publisher.ASSET_NAME}), fixture.calls)
        self.assertFalse(any(method == 'DELETE' for method, _, _ in fixture.calls))

    def test_ref_failure_restores_old_download_after_asset_replacement(self):
        fixture=GitHubFixture(fail_ref=True)
        with self.assertRaises(RuntimeError):
            self.publish(fixture)
        self.assertEqual(fixture.old['name'],publisher.ASSET_NAME)
        self.assertIn(('PATCH','releases/assets/2',{'name':publisher.PREFIX+'failed-2.zip'}),fixture.calls)
        self.assertFalse(any(method=='DELETE' for method,_,_ in fixture.calls))

    def test_metadata_failure_restores_download_tag_and_release_notes(self):
        fixture=GitHubFixture(fail_metadata=True)
        with self.assertRaises(RuntimeError): self.publish(fixture)
        self.assertEqual(fixture.old['name'],publisher.ASSET_NAME)
        self.assertIn(('PATCH','git/refs/tags/latest',{'sha':'a'*40,'force':True}),fixture.calls)
        restored=[payload for method,path,payload in fixture.calls if method=='PATCH' and path=='releases/20'][-1]
        self.assertEqual(restored['body'],'Previous notes')

    def test_first_upload_failure_reuses_its_existing_draft(self):
        class DraftFixture(GitHubFixture):
            release=None
            created=0
            fail_upload=True
            ref=None
            staged=None
            def api(self,method,path,payload=None,missing_ok=False):
                self.calls.append((method,path,payload))
                if path=='releases/tags/latest': return self.release if self.release and not self.release['draft'] else None
                if method=='GET' and path.startswith('releases?'): return [self.release] if self.release else []
                if method=='POST' and path=='releases':
                    self.created+=1;self.release=dict(payload,id=20,assets=[]);return self.release
                if path=='git/ref/tags/latest': return self.ref
                if method=='POST' and path=='git/refs': self.ref={'object':{'sha':payload['sha']}};return self.ref
                if method=='PATCH' and path=='releases/assets/2': self.staged.update(payload);return self.staged
                if method=='PATCH' and path=='releases/20': self.release.update(payload);return self.release
                if method=='GET' and path.startswith('releases/20/assets?'): return [self.staged] if self.staged else []
                return {}
            def upload(self,release,name,data):
                if self.fail_upload: self.fail_upload=False;raise RuntimeError('interrupted initial upload')
                self.staged=dict(id=2,name=name,state='uploaded',size=len(data),digest='sha256:'+hashlib.sha256(data).hexdigest())
                self.release['assets']=[self.staged];return self.staged
        fixture=DraftFixture()
        with self.assertRaises(RuntimeError): self.publish(fixture)
        self.assertTrue(self.publish(fixture))
        self.assertEqual(fixture.created,1)
        self.assertFalse(fixture.release['draft'])

    def test_older_build_does_not_replace_a_newer_latest(self):
        fixture = GitHubFixture(previous_run=5)
        self.assertFalse(self.publish(fixture))
        self.assertFalse(any(method != 'GET' for method, _, _ in fixture.calls))

    def test_publication_uploads_before_retiring_old_asset(self):
        fixture = GitHubFixture()
        self.assertTrue(self.publish(fixture))
        methods = [method for method, _, _ in fixture.calls]
        self.assertLess(methods.index('UPLOAD'), methods.index('DELETE'))
        update = next(payload for method, path, payload in fixture.calls if method == 'PATCH' and path == 'releases/20')
        self.assertIn('Windows 11', update['body'])
        self.assertEqual(update['target_commitish'], 'b'*40)
