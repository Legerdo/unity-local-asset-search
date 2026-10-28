"""Editor-owned loopback service. No asset bytes leave this process.

Only --download accesses Hugging Face; normal inference is strictly local.
The protocol is documented in Documentation~/README.md.
"""
from __future__ import annotations

import argparse
import hashlib
import hmac
import json
import os
from pathlib import Path
import sqlite3
import threading
import time
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODEL_ID = "google/embeddinggemma-2"
MODEL_REVISION = "914f7f89142e33e77833254d9c9b90c3cef7303b"
RECIPE = "asset-search-v1:text1800-overlap200,image1024,audio20s-overlap2-16k"
TEXT_EXTS = {".txt", ".md", ".json", ".csv", ".xml", ".yaml", ".yml", ".cs", ".shader", ".compute", ".uss", ".uxml", ".lua"}
MAX_BODY = 8 * 1024 * 1024


def validate_model(folder):
    folder = Path(folder).expanduser().resolve()
    required = ["config.json", "tokenizer.json", "tokenizer_config.json", "modules.json",
                "config_sentence_transformers.json", "processor_config.json", "preprocessor_config.json",
                "1_Pooling/config.json"]
    missing = [p for p in required if not (folder / p).is_file()]
    if not (folder / "model.safetensors").is_file():
        index = folder / "model.safetensors.index.json"
        if index.is_file():
            shards = set(json.loads(index.read_text("utf-8"))["weight_map"].values())
            missing += [p for p in shards if not (folder / p).is_file()]
        else:
            missing.append("model.safetensors")
    if missing:
        raise ValueError("Incomplete model folder: " + ", ".join(missing))
    if json.loads((folder / "config.json").read_text("utf-8")).get("model_type") != "embedding_gemma2":
        raise ValueError("Expected an EmbeddingGemma 2 checkpoint")
    return folder


def download_model(folder):
    # Never overwrite an existing checkpoint. An interrupted partial download can resume.
    folder = Path(folder).expanduser().resolve()
    try:
        validate_model(folder)
        print("Model already complete; no download performed.", flush=True)
        return
    except ValueError:
        if (folder / "model.safetensors").exists():
            raise ValueError("Existing weights found in an incomplete folder. Select a new download directory.")
    from huggingface_hub import snapshot_download
    snapshot_download(MODEL_ID, revision=MODEL_REVISION, local_dir=str(folder),
                      allow_patterns=["*.safetensors", "*.json", "*.model", "*.jinja", "1_Pooling/*", "2_Normalize/*"],
                      ignore_patterns=["*.bin", "onnx/*", "*.md", ".gitattributes"])
    validate_model(folder)
    print("Download complete: " + str(folder), flush=True)


def digest_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(4 * 1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def chunks(text, size=1800, overlap=200):
    for start in range(0, max(len(text), 1), size - overlap):
        yield start, text[start:start + size]
        if start + size >= len(text):
            break


class Engine:
    def __init__(self, args):
        self.project = Path(args.project_root).resolve(strict=True)
        self.assets = (self.project / "Assets").resolve(strict=True)
        self.state = Path(args.state_dir).resolve()
        if not self.state.is_relative_to((self.project / "Library").resolve()):
            raise ValueError("State directory must be under this project's Library")
        self.state.mkdir(parents=True, exist_ok=True)
        self.previews = self.state / "Previews"
        self.previews.mkdir(exist_ok=True)
        self.model_dir = validate_model(args.model_dir)
        self.dim = args.dimensions
        self.device = args.device
        self.model = None
        self.lock = threading.Lock()
        self.job_lock = threading.Lock()
        self.cancel = threading.Event()
        self.job = {"running": False, "done": 0, "total": 0, "updated": 0, "skipped": 0,
                    "failed": 0, "cancelled": False, "current": "", "errors": []}
        self.fingerprint = None
        self.db_path = self.state / "index.sqlite3"
        with self.db() as db:
            db.executescript("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT);"
                             "CREATE TABLE IF NOT EXISTS assets(guid TEXT PRIMARY KEY,path TEXT,kind TEXT,hash TEXT);"
                             "CREATE TABLE IF NOT EXISTS vectors(guid TEXT,part INTEGER,offset REAL,vector BLOB,PRIMARY KEY(guid,part));")

    @contextmanager
    def db(self):
        db = sqlite3.connect(self.db_path, timeout=30)
        try:
            db.execute("PRAGMA foreign_keys=ON")
            with db:
                yield db
        finally:
            db.close()

    def load(self):
        if self.model is not None:
            return
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        import torch
        import transformers
        import sentence_transformers
        from sentence_transformers import SentenceTransformer
        device = self.device
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        if device == "cuda" and not torch.cuda.is_available():
            raise ValueError("CUDA was selected but this Python environment has no CUDA support")
        dtype = torch.bfloat16 if device == "cuda" and torch.cuda.is_bf16_supported() else torch.float32
        # Content hash prevents an index from silently mixing model revisions or weights.
        h = hashlib.sha256((RECIPE + str(self.dim) + str(dtype) + transformers.__version__ + sentence_transformers.__version__).encode())
        files = sorted(p for p in self.model_dir.rglob("*")
                       if p.is_file() and ".cache" not in p.parts and p.suffix in {".json", ".safetensors", ".model", ".jinja"})
        for p in files:
            h.update(p.relative_to(self.model_dir).as_posix().encode())
            h.update(digest_file(p).encode())
        fingerprint = h.hexdigest()
        with self.db() as db:
            row = db.execute("SELECT value FROM meta WHERE key='fingerprint'").fetchone()
            if row and row[0] != fingerprint:
                raise ValueError("Model/preprocessing changed. Clear the generated index before reindexing.")
        model = SentenceTransformer(str(self.model_dir), device=device, local_files_only=True,
                                    trust_remote_code=False, model_kwargs={"torch_dtype": dtype})
        for kind in ("image", "audio"):
            if not model.supports(kind):
                raise ValueError("Installed backend does not support " + kind)
        self.model = model
        self.device = device
        self.fingerprint = fingerprint
        with self.db() as db:
            db.execute("INSERT OR REPLACE INTO meta VALUES ('fingerprint',?)", (fingerprint,))

    def encode(self, data, prompt=""):
        import numpy as np
        self.load()
        vec = np.asarray(self.model.encode(data, prompt=prompt, normalize_embeddings=True,
                                          truncate_dim=self.dim, show_progress_bar=False), dtype="<f4").reshape(-1)
        norm = float(np.linalg.norm(vec))
        if len(vec) != self.dim or not np.isfinite(vec).all() or norm < 0.99 or norm > 1.01:
            raise ValueError("Model returned an invalid embedding")
        return vec

    def source(self, item):
        path = Path(item["source"]).resolve(strict=True)
        asset = (self.project / item["path"]).resolve()
        if not asset.is_relative_to(self.assets) or not asset.is_file():
            raise ValueError("Asset must be a file inside this project's Assets")
        if not (path.is_relative_to(self.assets) or path.is_relative_to(self.previews)):
            raise ValueError("Source outside Assets/preview directory")
        if not path.is_file() or path.stat().st_size > 256 * 1024 * 1024:
            raise ValueError("Source missing or exceeds 256 MB limit")
        guid = item["guid"]
        if len(guid) != 32 or any(c not in "0123456789abcdef" for c in guid):
            raise ValueError("Invalid Unity GUID")
        if item["kind"] not in ("text", "image", "audio"):
            raise ValueError("Unsupported kind")
        return path

    def parts(self, item, source):
        if item["kind"] == "text":
            if source.suffix.lower() not in TEXT_EXTS or source.stat().st_size > 2 * 1024 * 1024:
                raise ValueError("Unsupported text extension or text exceeds 2 MB")
            text = source.read_text("utf-8-sig")
            title = Path(item["path"]).name
            for offset, content in chunks(text):
                yield offset, self.encode("title: " + title + " | text: " + content)
        elif item["kind"] == "image":
            from PIL import Image
            Image.MAX_IMAGE_PIXELS = 32_000_000
            with Image.open(source) as image:
                image = image.convert("RGB")
                image.thumbnail((1024, 1024))
                yield 0, self.encode({"image": image})
        else:
            import numpy as np
            import soundfile as sf
            import librosa
            with sf.SoundFile(source) as audio:
                duration = len(audio) / audio.samplerate
                if duration > 300:
                    raise ValueError("Audio exceeds 300 seconds; split it into shorter assets first")
                if duration < 0.025:
                    raise ValueError("Audio is shorter than 25 ms")
                for offset in range(0, max(1, int(duration + 0.999)), 18):
                    if self.cancel.is_set():
                        raise InterruptedError("Cancelled")
                    audio.seek(int(offset * audio.samplerate))
                    samples = audio.read(int(20 * audio.samplerate), dtype="float32", always_2d=True).mean(axis=1)
                    if len(samples) < 0.025 * audio.samplerate:
                        break
                    if audio.samplerate != 16000:
                        samples = librosa.resample(samples, orig_sr=audio.samplerate, target_sr=16000)
                    yield offset, self.encode({"audio": {"array": np.asarray(samples), "sampling_rate": 16000}})
                    if offset + 20 >= duration:
                        break

    def snapshot(self):
        with self.job_lock:
            return dict(self.job, errors=list(self.job["errors"]))

    def start(self, body):
        items = body.get("items", [])
        if not isinstance(items, list) or not items or len(items) > 10000:
            raise ValueError("Select between 1 and 10000 supported assets")
        # Validate the entire request before starting or changing the index.
        for item in items:
            self.source(item)
        with self.job_lock:
            if self.job["running"]:
                raise ValueError("An indexing job is already running")
            self.cancel.clear()
            self.job = {"running": True, "done": 0, "total": len(items), "updated": 0, "skipped": 0,
                        "failed": 0, "cancelled": False, "current": "Loading model", "errors": []}
        threading.Thread(target=self.index, args=(items,), daemon=True).start()
        return self.snapshot()

    def index(self, items):
        try:
            with self.lock:
                self.load()
                for item in items:
                    if self.cancel.is_set():
                        break
                    with self.job_lock:
                        self.job["current"] = item["path"]
                    try:
                        source = self.source(item)
                        checksum = hashlib.sha256((digest_file(source) + json.dumps(item, sort_keys=True)).encode()).hexdigest()
                        with self.db() as db:
                            row = db.execute("SELECT hash FROM assets WHERE guid=?", (item["guid"],)).fetchone()
                        if row and row[0] == checksum:
                            field = "skipped"
                        else:
                            vectors = []
                            for offset, vec in self.parts(item, source):
                                if self.cancel.is_set():
                                    raise InterruptedError("Cancelled")
                                vectors.append((item["guid"], len(vectors), offset, vec.tobytes()))
                            with self.db() as db:
                                db.execute("DELETE FROM vectors WHERE guid=?", (item["guid"],))
                                db.execute("INSERT OR REPLACE INTO assets VALUES (?,?,?,?)", (item["guid"], item["path"], item["kind"], checksum))
                                db.executemany("INSERT INTO vectors VALUES (?,?,?,?)", vectors)
                            field = "updated"
                        with self.job_lock:
                            self.job[field] += 1
                    except InterruptedError:
                        break
                    except Exception as ex:
                        with self.job_lock:
                            self.job["failed"] += 1
                            self.job["errors"].append(item["path"] + ": " + str(ex))
                    with self.job_lock:
                        self.job["done"] += 1
        except Exception as ex:
            with self.job_lock:
                self.job["errors"].append(str(ex))
        finally:
            with self.job_lock:
                self.job["cancelled"] = self.cancel.is_set()
                self.job["running"] = False
                self.job["current"] = ""

    def search(self, body):
        import numpy as np
        query = body.get("query", "").strip()
        if not query or len(query) > 4096:
            raise ValueError("Query must contain 1-4096 characters")
        kind = body.get("kind", "all")
        if kind not in ("all", "text", "image", "audio"):
            raise ValueError("Invalid kind")
        top = max(1, min(int(body.get("top", 20)), 100))
        if self.snapshot()["running"]:
            raise ValueError("Indexing is running; wait or cancel first")
        with self.lock:
            prefix = "task: code retrieval | query: " if body.get("code", False) else "task: search result | query: "
            vec = self.encode(query, prompt=prefix)
            best = {}
            roots = body.get("roots", [])
            excluded = body.get("excluded", [])
            # Stream the SQLite vectors so many text/audio chunks cannot exhaust RAM.
            with self.db() as db:
                cursor = db.execute("SELECT a.guid,a.path,a.kind,v.offset,v.vector FROM assets a JOIN vectors v ON a.guid=v.guid"
                                    + (" WHERE a.kind=?" if kind != "all" else ""), (kind,) if kind != "all" else ())
                while block := cursor.fetchmany(2048):
                    scores = np.stack([np.frombuffer(r[4], dtype="<f4") for r in block]) @ vec
                    for row, score in zip(block, scores):
                        guid, path, item_kind, offset, _ = row
                        if roots and not any(path == r or path.startswith(r.rstrip("/") + "/") for r in roots):
                            continue
                        if any(path == r or path.startswith(r.rstrip("/") + "/") for r in excluded):
                            continue
                        if not (self.project / path).is_file():
                            continue
                        if guid not in best or score > best[guid]["score"]:
                            best[guid] = {"guid": guid, "path": path, "kind": item_kind, "offset": offset, "score": float(score)}
            return {"results": sorted(best.values(), key=lambda r: r["score"], reverse=True)[:top],
                    "fingerprint": self.fingerprint}

    def clear(self):
        if self.snapshot()["running"]:
            raise ValueError("Cancel indexing and wait before clearing the index")
        with self.lock, self.db() as db:
            db.execute("DELETE FROM vectors")
            db.execute("DELETE FROM assets")
            db.execute("DELETE FROM meta")
            self.model = None
            self.fingerprint = None
        return {"ok": True}


def make_handler(engine, token):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_POST(self):
            if self.headers.get("Origin") or self.headers.get("Host") != f"127.0.0.1:{self.server.server_port}":
                self.reply(403, {"error": "Local editor requests only"})
                return
            if not hmac.compare_digest(self.headers.get("Authorization", ""), "Bearer " + token):
                self.reply(401, {"error": "Unauthorized"})
                return
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if length < 2 or length > MAX_BODY:
                    self.reply(413, {"error": "Body size limit"})
                    return
                body = json.loads(self.rfile.read(length))
                if not isinstance(body, dict):
                    raise ValueError("Expected JSON object")
                if self.path == "/status":
                    with engine.db() as db:
                        count = db.execute("SELECT count(*) FROM assets").fetchone()[0]
                    result = dict(engine.snapshot(), count=count, device=engine.device, dimensions=engine.dim)
                elif self.path == "/index":
                    result = engine.start(body)
                elif self.path == "/cancel":
                    engine.cancel.set()
                    result = {"ok": True}
                elif self.path == "/search":
                    result = engine.search(body)
                elif self.path == "/clear":
                    result = engine.clear()
                else:
                    self.reply(404, {"error": "Unknown endpoint"})
                    return
                self.reply(200, result)
            except (ValueError, KeyError, FileNotFoundError) as ex:
                self.reply(400, {"error": str(ex)})
            except Exception as ex:
                self.reply(500, {"error": type(ex).__name__ + ": " + str(ex)})

        def reply(self, status, value):
            data = json.dumps(value, ensure_ascii=True).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)

    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--download", action="store_true")
    parser.add_argument("--project-root")
    parser.add_argument("--state-dir")
    parser.add_argument("--session-id", default="")
    parser.add_argument("--device", choices=["auto", "cpu", "cuda"], default="auto")
    parser.add_argument("--dimensions", type=int, choices=[256, 512, 768], default=768)
    args = parser.parse_args()
    if args.download:
        download_model(args.model_dir)
        return
    token = os.environ.get("UNITY_EMBEDDING_TOKEN", "")
    if len(token) < 32:
        raise ValueError("Editor session token is required")
    engine = Engine(args)
    if os.environ.get("UNITY_EMBEDDING_DIAGNOSTICS") == "1":
        import faulthandler
        trace = open(engine.state / "diagnostics.log", "w")
        faulthandler.dump_traceback_later(45, file=trace)
    # Initialize native extensions on the main thread before HTTP starts spawning workers.
    # On Windows, first importing NumPy/OpenBLAS in an inference worker can deadlock
    # against a concurrent ThreadingHTTPServer thread startup.
    import numpy
    import torch
    import torchvision
    from sentence_transformers import SentenceTransformer
    from PIL import Image
    import soundfile
    from librosa import resample
    server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(engine, token))
    server.daemon_threads = True
    server.timeout = 1
    ready = engine.state / "ready.json"
    pending_ready = ready.with_suffix(".tmp")
    pending_ready.write_text(json.dumps({"port": server.server_port, "pid": os.getpid(), "session": args.session_id}), "utf-8")
    pending_ready.replace(ready)
    # Lifetime belongs to the Editor. A reload/exit closes redirected stdin.
    def parent_watch():
        try:
            import sys
            sys.stdin.buffer.read()
        finally:
            server.shutdown()
    threading.Thread(target=parent_watch, daemon=True).start()
    try:
        server.serve_forever()
    finally:
        server.server_close()
        ready.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
