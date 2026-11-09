"""The packaged model catalog drives both download selection and local validation."""
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'LightX2V.Windows'/'Backend'))
import download_models as downloads


class RepositoryResponse(io.BytesIO):
    headers = {}


class ModelCatalogTests(unittest.TestCase):
    def repository_files(self, key):
        model = downloads.MODELS[key]
        files = {path: 2 for path in model['requiredFiles']}
        files.update(model.get('expectedGgufSizes', {}))
        files.update({f"{name}/{layout['file']}": 128 for name, layout in model.get('weightComponents', {}).items()})
        for path in model.get('projectorFiles', [])[:1]:
            files[path] = downloads.MODEL_CATALOG['minimumGgufBytes']
        return files

    def metadata(self, key, files, source='modelScope'):
        if source == 'modelScope':
            body = dict(Code=200, Data=dict(Files=[dict(Type='blob', Path=path, Size=size) for path, size in files.items()]))
        else:
            body = [dict(type='file', path=path, size=size, lfs=dict(oid='a'*64)) for path, size in files.items()]
        with patch.object(downloads.urllib.request, 'urlopen', return_value=RepositoryResponse(json.dumps(body).encode())):
            return downloads.metadata(key, source)

    def test_repository_identifiers_come_from_the_packaged_versioned_catalog(self):
        path = Path(downloads.__file__).parent.parent/'Resources'/'model-catalog.json'
        catalog = json.loads(path.read_text(encoding='utf-8'))
        self.assertEqual(catalog['schemaVersion'], 1)
        self.assertTrue(catalog['compatibilityVersion'])
        self.assertEqual(downloads.REPOSITORIES, {key: model['repository'] for key, model in catalog['models'].items()})

    def test_both_sources_require_tokenizer_processor_and_scheduler_files(self):
        for source in ('huggingFace', 'modelScope'):
            for missing in ('processor/tokenizer.json', 'processor/processor_config.json', 'scheduler/scheduler_config.json'):
                with self.subTest(source=source, missing=missing):
                    files = self.repository_files('turbo')
                    del files[missing]
                    with self.assertRaisesRegex(ValueError, 'missing required model files'):
                        self.metadata('turbo', files, source)

    def test_supported_single_and_sharded_layouts_can_be_downloaded(self):
        files = self.repository_files('turbo')
        self.assertEqual({file['path'] for file in self.metadata('turbo', files)}, set(files))
        layout = downloads.MODELS['turbo']['weightComponents']['transformer']
        del files['transformer/'+layout['file']]
        files['transformer/'+layout['index']] = 200
        files['transformer/shard-00001-of-00001.safetensors'] = 128
        self.assertEqual({file['path'] for file in self.metadata('turbo', files)}, set(files))
        del files['transformer/'+layout['index']]
        with self.assertRaisesRegex(ValueError, 'missing weights for transformer'):
            self.metadata('turbo', files)

    def test_optional_models_only_select_supported_gguf_files(self):
        files = self.repository_files('t2i')
        files['another-quantization.gguf'] = 1000
        selected = self.metadata('t2i', files)
        self.assertNotIn('another-quantization.gguf', {file['path'] for file in selected})
        model_file = downloads.MODELS['t2i']['modelFile']
        files[model_file] -= 1
        with self.assertRaisesRegex(ValueError, 'supported model catalog'):
            self.metadata('t2i', files)

    def test_both_supported_vision_projector_names_remain_compatible(self):
        for projector in downloads.MODELS['i2i']['projectorFiles']:
            with self.subTest(projector=projector):
                files = self.repository_files('i2i')
                for name in downloads.MODELS['i2i']['projectorFiles']:
                    files.pop(name, None)
                with self.assertRaisesRegex(ValueError, 'vision projector'):
                    self.metadata('i2i', files)
                files[projector] = downloads.MODEL_CATALOG['minimumGgufBytes']
                self.assertIn(projector, {file['path'] for file in self.metadata('i2i', files)})


if __name__ == '__main__':
    unittest.main()
