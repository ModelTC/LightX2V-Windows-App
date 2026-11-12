"""Public entry point, legacy downloads, and tampered package regressions."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('verify_package', Path(__file__).resolve().parents[1] / 'scripts/verify_package.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PackageLayoutTests(unittest.TestCase):
    def fixture(self, root, layout=2, extra=None, corrupt=False):
        files = {'LightX2V.exe': b'launcher', 'app/LightX2V.Windows.exe': b'app', 'docs/README.md': b'help'} if layout == 2 else {'LightX2V.Windows.exe': b'legacy'}
        files.update(extra or {})
        manifest = dict(version='0.4.1', target='win-x64', channel='latest', sourceCommit='a'*40, sourceDirty=False,
                        sha256={name: hashlib.sha256(data).hexdigest() for name, data in files.items()})
        name = 'build-manifest.json'
        if layout == 2:
            manifest.update(layoutVersion=2, entryPoint='LightX2V.exe', appExecutable='app/LightX2V.Windows.exe')
            name = 'app/' + name
        path = root / 'LightX2V-APP-Windows-x64.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            for file, data in files.items():
                archive.writestr('LightX2V-Windows-0.4.1/' + file, data + (b'changed' if corrupt else b''))
            archive.writestr('LightX2V-Windows-0.4.1/' + name, json.dumps(manifest))
        path.with_suffix('.zip.sha256').write_text(hashlib.sha256(path.read_bytes()).hexdigest() + '  ' + path.name)
        return path

    def test_public_layout_and_legacy_package_both_verify(self):
        with tempfile.TemporaryDirectory() as temporary:
            for layout in (1, 2):
                result = module.verify(self.fixture(Path(temporary), layout), '0.4.1', 'a'*40, True)
                self.assertEqual(result['sourceCommit'], 'a'*40)

    def test_flat_runtime_file_is_rejected_from_public_top_level(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, 'top-level'):
                module.verify(self.fixture(Path(temporary), extra={'coreclr.dll': b'runtime'}), '0.4.1')

    def test_payload_change_cannot_be_hidden_by_recomputing_download_checksum(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, 'Payload checksum'):
                module.verify(self.fixture(Path(temporary), corrupt=True), '0.4.1')

    def test_other_source_commit_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, 'different source'):
                module.verify(self.fixture(Path(temporary)), '0.4.1', 'b'*40, True)
