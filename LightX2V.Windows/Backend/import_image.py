"""Decode user-selected images to immutable PNG imports; never imports torch."""
import json
import sys
from pathlib import Path
from PIL import Image, ImageOps

source,target=map(Path,sys.argv[1:3])
try:
    if not source.is_absolute() or not source.is_file():raise ValueError('missing_image')
    if source.stat().st_size>50*1024**2:raise ValueError('image_too_large')
    Image.MAX_IMAGE_PIXELS=50_000_000
    with Image.open(source) as image:
        if image.format not in ('PNG','JPEG','WEBP','BMP','TIFF'):raise ValueError('unsupported_image')
        if image.width*image.height>50_000_000:raise ValueError('image_too_large')
        image=ImageOps.exif_transpose(image).convert('RGBA')
        target.parent.mkdir(parents=True,exist_ok=True)
        temporary=target.with_suffix('.tmp')
        image.save(temporary,format='PNG')
        if temporary.stat().st_size>50*1024**2:temporary.unlink();raise ValueError('image_too_large')
        temporary.replace(target)
        print(json.dumps({'ok':True,'path':str(target),'name':source.name,'width':image.width,'height':image.height},ensure_ascii=False))
except Exception as error:
    print(json.dumps({'ok':False,'code':str(error) if str(error) in ('missing_image','image_too_large','unsupported_image') else 'invalid_image'}))
    raise SystemExit(1)
