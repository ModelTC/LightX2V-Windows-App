"""Port of the existing Mac app's scripts/make-icon.swift geometry."""
from PIL import Image, ImageDraw
import sys
im=Image.new('RGBA',(1024,1024));d=ImageDraw.Draw(im)
d.rounded_rectangle((64,64,960,960),radius=202,fill=(240,240,240,255))
d.rounded_rectangle((89,89,935,935),radius=180,fill=(20,20,20,255))
d.polygon([(579,212),(291,547),(471,547),(428,809),(742,459),(550,459)],fill=(250,250,250,255))
d.ellipse((729,719,807,797),fill=(140,140,140,255))
im.save(sys.argv[1],sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
