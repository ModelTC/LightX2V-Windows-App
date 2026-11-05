"""Run the packaged backend on a future RTX host without UI automation.

Use the environment and source prepared by the app. No environment is installed
or system setting changed by this script. Start with --tiers 512.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import shutil

p=argparse.ArgumentParser();p.add_argument('--app',type=Path,required=True);p.add_argument('--python',type=Path,required=True);p.add_argument('--repository',type=Path,required=True);p.add_argument('--model',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--tiers',default='512');p.add_argument('--edit',action='store_true');a=p.parse_args()
if a.output.exists():p.error('Use a new output directory to preserve earlier evidence.')
sizes={'512':512,'1K':1024,'2K':2048};tiers=a.tiers.split(',')
if any(t not in sizes for t in tiers):p.error('--tiers accepts 512,1K,2K')
a.output.mkdir(parents=True)
def run(mode,request,folder):
    folder.mkdir(parents=True,exist_ok=True);path=folder/'request.json';path.write_text(json.dumps(request,ensure_ascii=False),encoding='utf-8')
    command=[str(a.python.resolve()),'-X','utf8','-u',str((a.app/'Backend/bridge.py').resolve()),mode,str(path.resolve()),'--parent',str(os.getpid())]
    with (folder/'protocol.ndjson').open('w',encoding='utf-8') as log:
        process=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,encoding='utf-8')
        try:
            for line in process.stdout:log.write(line);log.flush();print(line,end='',flush=True)
            code=process.wait()
        finally:
            if process.poll() is None:process.terminate();process.wait()
    if code:raise RuntimeError(f'Backend failed (code {code}); see {folder}/protocol.ndjson')
run('check',dict(protocol=1,backend='cuda'),a.output/'environment')
results=[];reference=None
for tier in tiers:
    for task in (['t2i','i2i'] if a.edit else ['t2i']):
        folder=(a.output/(tier+'-'+task)).resolve();folder.mkdir()
        request=dict(protocol=1,backend='cuda',repository=str(a.repository.resolve()),model=str(a.model.resolve()),config=str((a.app/'Resources/qwen_image_21_turbo.json').resolve()),output=str(folder/'image.png'),prompt='A blue ceramic teapot on a wooden table beside a sunny window, warm natural light, realistic photography.',width=sizes[tier],height=sizes[tier],seed=42,task=task)
        if task=='i2i':
            inputs=folder/'inputs';inputs.mkdir();source=inputs/'reference.png';shutil.copy2(reference,source)
            request.update(prompt='Add a red apple beside the blue teapot.',inputImages=[dict(path=str(source))],referenceResolution=512)
        run('run',request,folder);image=folder/'image.png';header=image.read_bytes()[:24]
        assert header[:8]==b'\x89PNG\r\n\x1a\n' and int.from_bytes(header[16:20],'big')==int.from_bytes(header[20:24],'big')==sizes[tier]
        invocation=json.loads((folder/'invocation.json').read_text(encoding='utf-8-sig'));assert invocation['backend']=='cuda' and invocation['environment']['PLATFORM']=='cuda'
        assert 'TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL' not in invocation['environment']
        results.append(dict(tier=tier,task=task,invocation=invocation,image=str(image)));reference=image
(a.output/'rtx-acceptance.json').write_text(json.dumps(dict(completedAt=datetime.datetime.now().astimezone().isoformat(),backend='cuda',results=results),indent=2),encoding='utf-8')
print('RTX acceptance completed:',len(results),'actual images. Attach this folder for review.')
