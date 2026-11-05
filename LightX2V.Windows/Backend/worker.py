"""Select the requested source even when using isolated/embedded CPython."""
from pathlib import Path
import runpy
import sys
repo = Path(sys.argv[1])
sys.path.insert(0, str(repo))
sys.argv = ['lightx2v.infer', *sys.argv[2:]]
runpy.run_module('lightx2v.infer', run_name='__main__')
