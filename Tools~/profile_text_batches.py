"""Optional encoder-only microbenchmark; excludes Unity export, audio and SQLite writes."""
import argparse
import json
from pathlib import Path
import sys
import time
sys.path.insert(0, str(Path(__file__).parents[1] / "Backend~"))
import numpy as np
import torch
from server import Engine

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--project", required=True)
parser.add_argument("--model", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
project = Path(args.project).resolve()
engine = Engine(argparse.Namespace(project_root=str(project), state_dir=str(project / "Library/LocalAssetSearchBatchProfile"),
                                   model_dir=args.model, dimensions=768, device="cuda"))
engine.clear(); engine.load()
data = ["title: " + p.name + " | text: " + p.read_text("utf-8") for p in sorted((project / "Assets/LocalAssetSearchBenchmark").glob("Description*.txt"))]
assert len(data) == 128, "Run AssetSearchValidation first to create the owned fixture"
engine.encode(data[0])
reference, report = None, []
for batch in (1, 16, 32, 64):
    torch.cuda.reset_peak_memory_stats()
    started = time.perf_counter(); vectors = engine.encode_many(data, batch_size=batch); elapsed = time.perf_counter() - started
    if reference is None:
        reference = vectors
    cosine = np.sum(reference * vectors, axis=1) / (np.linalg.norm(reference, axis=1) * np.linalg.norm(vectors, axis=1))
    assert float(cosine.min()) > .999, "Batch output differs from the single-item reference"
    report.append({"batch": batch, "seconds": elapsed, "texts": len(data), "peak_mb": torch.cuda.max_memory_allocated() / 1048576,
                   "min_cosine": float(cosine.min())})
Path(args.output).write_text(json.dumps(report, indent=2), "utf-8")
print(json.dumps(report))
