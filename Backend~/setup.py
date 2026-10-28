"""Create an isolated environment; never change the invoking/global interpreter."""
import argparse
from pathlib import Path
import subprocess
import sys
import venv

parser = argparse.ArgumentParser()
parser.add_argument("--venv", required=True)
parser.add_argument("--cuda", action="store_true")
args = parser.parse_args()
target = Path(args.venv).expanduser().resolve()
if target.exists() and any(target.iterdir()) and not (target / "pyvenv.cfg").is_file():
    raise ValueError("Select an empty directory or an existing virtual environment")
venv.create(target, with_pip=True)
python = target / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
if args.cuda:
    subprocess.run([str(python), "-m", "pip", "install", "torch==2.14.1+cu130", "torchvision==0.29.1+cu130", "--index-url", "https://download.pytorch.org/whl/cu130"], check=True)
subprocess.run([str(python), "-m", "pip", "install", "-r", str(Path(__file__).with_name("requirements.txt"))], check=True)
subprocess.run([str(python), "-c", "from transformers import EmbeddingGemma2Model; from sentence_transformers import SentenceTransformer; import soundfile,librosa; print('Backend dependencies ready')"], check=True)
print(str(python), flush=True)
