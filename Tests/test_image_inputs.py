"""Regression checks for the public reference contract and real image imports."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

BACKEND = Path(__file__).resolve().parents[1] / 'LightX2V.Windows' / 'Backend'
sys.path.insert(0, str(BACKEND))
from image_policy import validate_references, MAXIMUM_COUNT


class ReferenceContractTests(unittest.TestCase):
    def test_eight_references_supported_ninth_rejected(self):
        for count in (1, 3, 8):
            validate_references('i2i', [{} for _ in range(count)])
        with self.assertRaises(ValueError):
            validate_references('i2i', [{} for _ in range(9)])
        self.assertEqual(MAXIMUM_COUNT, 8)

    def test_task_and_reference_list_must_agree(self):
        validate_references('t2i', [])
        for task, images in [('i2i', []), ('t2i', [{}]), ('other', []), ('i2i', {})]:
            with self.subTest(task=task, images=images), self.assertRaises(ValueError):
                validate_references(task, images)


@unittest.skipUnless(importlib.util.find_spec('PIL'), 'Pillow needed for real import checks')
class ImageImportTests(unittest.TestCase):
    def run_import(self, source, target):
        process = subprocess.run([sys.executable, "-X", "utf8", str(BACKEND / 'import_image.py'), str(source), str(target)], capture_output=True, text=True, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        self.assertTrue(process.stdout, f'Importer exited {process.returncode}: {process.stderr}')
        return process.returncode, json.loads(process.stdout)

    def test_orientation_and_normalized_png(self):
        from PIL import Image
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            image = Image.new('RGB', (24, 16), 'red')
            exif = Image.Exif(); exif[274] = 6
            source = root / '旋转 reference.jpg'; image.save(source, exif=exif)
            target = root / 'normalized.png'
            code, result = self.run_import(source, target)
            self.assertEqual(code, 0, result)
            with Image.open(target) as normalized:
                self.assertEqual(normalized.size, (16, 24))
                self.assertEqual(normalized.format, 'PNG')

    def test_damaged_file_does_not_create_an_import(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root / 'damaged.png'; source.write_text('broken image')
            target = root / 'normalized.png'
            code, result = self.run_import(source, target)
            self.assertNotEqual(code, 0)
            self.assertEqual(result['code'], 'invalid_image')
            self.assertFalse(target.exists())
