"""The same image input limits are consumed by the desktop app and backend."""
import json
from pathlib import Path

POLICY = json.loads((Path(__file__).resolve().parents[1] / 'Resources' / 'image-input.json').read_text(encoding='utf-8'))
MAXIMUM_COUNT = POLICY['maximumCount']
MAXIMUM_FILE_BYTES = POLICY['maximumFileBytes']
MAXIMUM_PIXELS = POLICY['maximumPixels']
MAXIMUM_DIMENSION = POLICY['maximumDimension']


def validate_references(task, images):
    if task not in ('t2i', 'i2i') or not isinstance(images, list) or len(images) > MAXIMUM_COUNT:
        raise ValueError('Invalid image task or reference count.')
    if (task == 'i2i') != bool(images):
        raise ValueError('Image editing needs reference images.')
