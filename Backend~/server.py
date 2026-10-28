"""Editor-owned loopback service. No asset bytes leave this process.

Only --download accesses Hugging Face; normal inference is strictly local.
The protocol is documented in Documentation~/README.md.
"""
from __future__ import annotations

import argparse
import gc
import hashlib
import hmac
import json
import os
from pathlib import Path
import sqlite3
import threading
import time
from contextlib import contextmanager
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODEL_ID = "google/embeddinggemma-2"
MODEL_REVISION = "914f7f89142e33e77833254d9c9b90c3cef7303b"
RECIPE = "asset-search-v2-fp32-l2:text1800-overlap200,image1024,audio20s-overlap2-16k"
TEXT_EXTS = {".txt", ".md", ".json", ".csv", ".xml", ".yaml", ".yml", ".cs", ".shader", ".compute", ".uss", ".uxml", ".lua"}
MAX_BODY = 8 * 1024 * 1024
# Native inference pools are tied to the executing thread. HTTP requests have
# short-lived threads; reuse one lazy model worker across loading and inference.
MODEL_WORKER = ThreadPoolExecutor(max_workers=1, thread_name_prefix="asset-inference")


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


def normalized_vectors(vectors, count, dimension):
    import numpy as np
    vectors = np.asarray(vectors, dtype="<f4").reshape(count, -1)
    norms = np.linalg.norm(vectors, axis=1)
    if vectors.shape != (count, dimension) or not np.isfinite(vectors).all() or not ((norms > .99) & (norms < 1.01)).all():
        raise ValueError("Model returned an invalid embedding")
    return vectors / norms[:, None]


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
        self.batch_profile = getattr(args, "batch_profile", "balanced")
        self.model = None
        self.query_cache = OrderedDict()
        self.load_seconds = 0
        self.hash_seconds = 0
        self.job_started = None
        self.elapsed_seconds = 0
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
        started = time.perf_counter()
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
        self.hash_seconds = time.perf_counter() - started
        with self.db() as db:
            row = db.execute("SELECT value FROM meta WHERE key='fingerprint'").fetchone()
            if row and row[0] != fingerprint:
                raise ValueError("Model/preprocessing changed. Clear the generated index before reindexing.")
        def construct():
            model = SentenceTransformer(str(self.model_dir), device=device, local_files_only=True,
                                        trust_remote_code=False, model_kwargs={"torch_dtype": dtype})
            for kind in ("image", "audio"):
                if not model.supports(kind):
                    raise ValueError("Installed backend does not support " + kind)
            return model
        model = MODEL_WORKER.submit(construct).result()
        self.model = model
        self.device = device
        self.fingerprint = fingerprint
        with self.db() as db:
            db.execute("INSERT OR REPLACE INTO meta VALUES ('fingerprint',?)", (fingerprint,))
        self.load_seconds = time.perf_counter() - started

    def status(self):
        with self.db() as db:
            count = db.execute("SELECT count(*) FROM assets").fetchone()[0]
        values = dict(self.snapshot(), count=count, device=self.device, dimensions=self.dim,
                      model_loaded=self.model is not None, load_seconds=self.load_seconds,
                      hash_seconds=self.hash_seconds, elapsed_seconds=self.elapsed_seconds,
                      query_cache_entries=len(self.query_cache))
        if values["running"] and self.job_started is not None:
            values["elapsed_seconds"] = time.perf_counter() - self.job_started
        if self.device == "cuda":
            import torch
            if torch.cuda.is_initialized():
                values["gpu_allocated_mb"] = torch.cuda.memory_allocated() / 1048576
                values["gpu_reserved_mb"] = torch.cuda.memory_reserved() / 1048576
                values["gpu_peak_mb"] = torch.cuda.max_memory_allocated() / 1048576
        return values

    def encode_many(self, data, prompt="", batch_size=16):
        self.load()
        return MODEL_WORKER.submit(self._encode_many, data, prompt, batch_size).result()

    def _encode_many(self, data, prompt, batch_size):
        import numpy as np
        import torch
        while True:
            try:
                vectors = np.asarray(self.model.encode(data, prompt=prompt, batch_size=batch_size,
                    normalize_embeddings=True, truncate_dim=self.dim, show_progress_bar=False), dtype="<f4").reshape(len(data), -1)
            except torch.OutOfMemoryError:
                if batch_size <= 1:
                    raise
                batch_size = max(1, batch_size // 2)
            else:
                return normalized_vectors(vectors, len(data), self.dim)
            # Leave the exception block before retrying: its traceback can own GPU
            # temporaries that empty_cache cannot release while the traceback is alive.
            gc.collect()
            if self.device == "cuda":
                torch.cuda.empty_cache()

    def encode(self, data, prompt=""):
        return self.encode_many([data], prompt, 1)[0]

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

    def inputs(self, item, source):
        if item["kind"] == "text":
            if source.suffix.lower() not in TEXT_EXTS or source.stat().st_size > 2 * 1024 * 1024:
                raise ValueError("Unsupported text extension or text exceeds 2 MB")
            text = source.read_text("utf-8-sig")
            title = Path(item["path"]).name
            for offset, content in chunks(text):
                yield offset, "title: " + title + " | text: " + content
        elif item["kind"] == "image":
            from PIL import Image
            Image.MAX_IMAGE_PIXELS = 32_000_000
            with Image.open(source) as image:
                image = image.convert("RGB")
                image.thumbnail((1024, 1024))
                yield 0, {"image": image.copy()}
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
                    yield offset, {"audio": {"array": np.asarray(samples), "sampling_rate": 16000}}
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
            self.job_started = time.perf_counter()
            self.job = {"running": True, "done": 0, "total": len(items), "updated": 0, "skipped": 0,
                        "failed": 0, "cancelled": False, "current": "Loading model", "errors": []}
        threading.Thread(target=self.index, args=(items,), daemon=True).start()
        return self.snapshot()

    def record_error(self, item, ex):
        with self.job_lock:
            self.job["failed"] += 1
            self.job["done"] += 1
            self.job["errors"].append(item["path"] + ": " + str(ex))

    def write_asset(self, item, checksum, vectors):
        if self.cancel.is_set():
            raise InterruptedError("Cancelled")
        with self.db() as db:
            db.execute("DELETE FROM vectors WHERE guid=?", (item["guid"],))
            db.execute("INSERT OR REPLACE INTO assets VALUES (?,?,?,?)", (item["guid"], item["path"], item["kind"], checksum))
            db.executemany("INSERT INTO vectors VALUES (?,?,?,?)", [(item["guid"], n, offset, vector.tobytes()) for n, (offset, vector) in enumerate(vectors)])
        with self.job_lock:
            self.job["updated"] += 1
            self.job["done"] += 1

    def index(self, items):
        try:
            with self.lock:
                self.load()
                changed = {"text": [], "image": [], "audio": []}
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
                            with self.job_lock:
                                self.job["skipped"] += 1
                                self.job["done"] += 1
                        else:
                            changed[item["kind"]].append((item, source, checksum))
                    except InterruptedError:
                        break
                    except Exception as ex:
                        self.record_error(item, ex)
                text_batch = 64 if self.device == "cuda" else 8
                for kind, batch_size in (("text", text_batch), ("image", 4), ("audio", 2)):
                    if self.batch_profile == "single":
                        batch_size = 1
                    for start in range(0, len(changed[kind]), batch_size):
                        if self.cancel.is_set():
                            raise InterruptedError("Cancelled")
                        prepared, documents = [], []
                        for item, source, checksum in changed[kind][start:start + batch_size]:
                            try:
                                parts = list(self.inputs(item, source))
                                prepared.append((item, checksum, parts, len(documents)))
                                documents.extend(data for _, data in parts)
                            except InterruptedError:
                                raise
                            except Exception as ex:
                                self.record_error(item, ex)
                        if not documents:
                            continue
                        with self.job_lock:
                            self.job["current"] = prepared[0][0]["path"]
                        batch_failed = False
                        try:
                            vectors = self.encode_many(documents, batch_size=batch_size)
                        except Exception:
                            batch_failed = True
                        if batch_failed:
                            # Isolate an invalid item instead of failing unrelated assets in its batch.
                            # The failed batch's traceback must be gone before allocating again.
                            gc.collect()
                            if self.device == "cuda":
                                import torch
                                torch.cuda.empty_cache()
                            for item, checksum, parts, _ in prepared:
                                try:
                                    single = self.encode_many([data for _, data in parts], batch_size=1)
                                    self.write_asset(item, checksum, zip((o for o, _ in parts), single))
                                except InterruptedError:
                                    raise
                                except Exception as ex:
                                    self.record_error(item, ex)
                        else:
                            for item, checksum, parts, offset in prepared:
                                self.write_asset(item, checksum, zip((o for o, _ in parts), vectors[offset:offset + len(parts)]))
        except InterruptedError:
            pass
        except Exception as ex:
            with self.job_lock:
                self.job["errors"].append(str(ex))
        finally:
            with self.job_lock:
                self.job["cancelled"] = self.cancel.is_set()
                self.job["running"] = False
                self.job["current"] = ""
                self.elapsed_seconds = time.perf_counter() - self.job_started

    def search(self, body):
        import numpy as np
        query = body.get("query", "").strip()
        if not query or len(query) > 4096:
            raise ValueError("Query must contain 1-4096 characters")
        kind = body.get("kind", "all")
        if kind not in ("all", "text", "image", "audio"):
            raise ValueError("Invalid kind")
        top = max(1, min(int(body.get("top", 20)), 100))
        minimum = float(body.get("minScore", -1))
        order = body.get("sort", "similarity")
        path_contains = body.get("pathContains", "").strip()
        extensions = body.get("extensions", "").strip()
        if not -1 <= minimum <= 1 or order not in ("similarity", "name", "path"):
            raise ValueError("Invalid similarity threshold or sort")
        if len(path_contains) > 256 or len(extensions) > 256:
            raise ValueError("Path/extension filter exceeds 256 characters")
        suffixes = ["." + value.strip().lower().lstrip(".") for value in extensions.split(",") if value.strip()]
        if any(not suffix[1:].isalnum() for suffix in suffixes) or len(suffixes) > 16:
            raise ValueError("Use up to 16 comma-separated file extensions, such as png,jpg")
        if self.snapshot()["running"]:
            raise ValueError("Indexing is running; wait or cancel first")
        with self.lock:
            prefix = "task: code retrieval | query: " if body.get("code", False) else "task: search result | query: "
            self.load()
            cache_key = (prefix, query)
            cached = cache_key in self.query_cache
            if not cached:
                self.query_cache[cache_key] = self.encode(query, prompt=prefix)
                if len(self.query_cache) > 64:
                    self.query_cache.popitem(last=False)
            self.query_cache.move_to_end(cache_key)
            vec = self.query_cache[cache_key]
            best = {}
            roots = body.get("roots", [])
            excluded = body.get("excluded", [])
            conditions, parameters = [], []
            if kind != "all":
                conditions.append("a.kind=?")
                parameters.append(kind)
            if path_contains:
                conditions.append("instr(lower(a.path),lower(?))>0")
                parameters.append(path_contains)
            if suffixes:
                conditions.append("(" + " OR ".join("substr(lower(a.path),-?)=?" for _ in suffixes) + ")")
                for suffix in suffixes:
                    parameters.extend((len(suffix), suffix))
            def scoped(paths):
                clauses = []
                for root in paths:
                    prefix_path = root.rstrip("/") + "/"
                    clauses.append("(a.path=? OR substr(a.path,1,?)=?)")
                    parameters.extend((root, len(prefix_path), prefix_path))
                return " OR ".join(clauses)
            if roots:
                conditions.append("(" + scoped(roots) + ")")
            if excluded:
                conditions.append("NOT (" + scoped(excluded) + ")")
            # Filter before reading vector blobs, then stream bounded chunks for exact ranking.
            with self.db() as db:
                cursor = db.execute("SELECT a.guid,a.path,a.kind,v.offset,v.vector FROM assets a JOIN vectors v ON a.guid=v.guid"
                                    + (" WHERE " + " AND ".join(conditions) if conditions else ""), parameters)
                while block := cursor.fetchmany(2048):
                    scores = np.stack([np.frombuffer(r[4], dtype="<f4") for r in block]) @ vec
                    for row, score in zip(block, scores):
                        if score < minimum:
                            continue
                        guid, path, item_kind, offset, _ = row
                        if not (self.project / path).is_file():
                            continue
                        if guid not in best or score > best[guid]["score"]:
                            best[guid] = {"guid": guid, "path": path, "kind": item_kind, "offset": offset, "score": float(score)}
            if order == "similarity":
                ranked = sorted(best.values(), key=lambda r: (-r["score"], r["path"]))
            else:
                ranked = sorted(best.values(), key=lambda r: ((Path(r["path"]).name if order == "name" else r["path"]).casefold(), -r["score"]))
            return {"results": ranked[:top], "matched": len(ranked),
                    "fingerprint": self.fingerprint, "cache_hit": cached}

    def clear(self):
        if self.snapshot()["running"]:
            raise ValueError("Cancel indexing and wait before clearing the index")
        with self.lock, self.db() as db:
            db.execute("DELETE FROM vectors")
            db.execute("DELETE FROM assets")
            db.execute("DELETE FROM meta")
            self.fingerprint = None
            self.unload()
        return {"ok": True}

    def unload(self):
        """Caller holds self.lock. Release model cycles without deleting the index."""
        self.query_cache.clear()
        # SentenceTransformers' model-card metadata owns a back-reference. Break
        # it explicitly: collecting the module tree alone leaves native weights
        # reachable in the validated backend, even after a full GC collection.
        card = getattr(self.model, "model_card_data", None)
        if card is not None and getattr(card, "model", None) is self.model:
            card.model = None
        self.model = None
        gc.collect()
        if self.device == "cuda":
            import torch
            if torch.cuda.is_initialized():
                torch.cuda.synchronize()
                torch.cuda.empty_cache()


def make_handler(engine, token, agent_token=""):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_POST(self):
            if self.headers.get("Origin") or self.headers.get("Host") != f"127.0.0.1:{self.server.server_port}":
                self.reply(403, {"error": "Local editor requests only"})
                return
            authorization = self.headers.get("Authorization", "")
            is_agent = bool(agent_token) and hmac.compare_digest(authorization, "Bearer " + agent_token)
            if not is_agent and not hmac.compare_digest(authorization, "Bearer " + token):
                self.reply(401, {"error": "Unauthorized"})
                return
            if is_agent and self.path not in ("/status", "/search"):
                self.reply(403, {"error": "Agent credentials allow search and status only"})
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
                    result = engine.status()
                elif self.path == "/warmup":
                    if engine.snapshot()["running"]:
                        raise ValueError("Indexing is running")
                    with engine.lock:
                        engine.load()
                    result = engine.status()
                elif self.path == "/index":
                    result = engine.start(body)
                elif self.path == "/cancel":
                    engine.cancel.set()
                    result = {"ok": True}
                elif self.path == "/search":
                    if is_agent:
                        scope_file = engine.state / "agent-scope.json"
                        scope = json.loads(scope_file.read_text("utf-8")) if scope_file.is_file() else {}
                        if not scope.get("roots"):
                            raise ValueError("Choose an asset scope in the Unity Editor first")
                        body["roots"] = scope["roots"]
                        body["excluded"] = scope.get("excluded", [])
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
    parser.add_argument("--batch-profile", choices=["balanced", "single"], default="balanced")
    args = parser.parse_args()
    if args.download:
        download_model(args.model_dir)
        return
    token = os.environ.get("UNITY_EMBEDDING_TOKEN", "")
    if len(token) < 32:
        raise ValueError("Editor session token is required")
    engine = Engine(args)
    ready = engine.state / "ready.json"
    agent_file = engine.state / "agent-connection.json"

    def remove_owned_connection_files():
        for path in (ready, agent_file):
            try:
                if json.loads(path.read_text("utf-8")).get("pid") == os.getpid():
                    path.unlink(missing_ok=True)
            except (OSError, ValueError):
                pass

    def parent_watch():
        try:
            if os.name == "nt":
                # Blocking CRT/buffered reads can lock stdin while native imports
                # inspect it. Probe the Editor's pipe without reading or locking
                # the Python stream, including during a cold import/startup.
                import ctypes
                from ctypes import wintypes
                kernel = ctypes.WinDLL("kernel32", use_last_error=True)
                kernel.GetStdHandle.restype = wintypes.HANDLE
                kernel.PeekNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                                                 ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
                kernel.PeekNamedPipe.restype = wintypes.BOOL
                handle = kernel.GetStdHandle(-10)  # STD_INPUT_HANDLE
                while kernel.PeekNamedPipe(handle, None, 0, None, None, None):
                    time.sleep(.1)
                if ctypes.get_last_error() not in (109, 232):  # broken/closing pipe
                    raise ctypes.WinError(ctypes.get_last_error())
            else:
                while os.read(0, 1):
                    pass
        finally:
            # Native inference/destructors can block interpreter teardown. This
            # process is exclusively Editor-owned; exit on its pipe closing and
            # let the OS reclaim CUDA/native allocations. SQLite rolls back any
            # incomplete per-asset transaction without changing source assets.
            remove_owned_connection_files()
            os._exit(0)

    threading.Thread(target=parent_watch, daemon=True).start()

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
    agent_token = os.environ.get("UNITY_EMBEDDING_AGENT_TOKEN", "")
    server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(engine, token, agent_token))
    server.daemon_threads = True
    server.timeout = 1
    pending_ready = ready.with_suffix(".tmp")
    pending_ready.write_text(json.dumps({"port": server.server_port, "pid": os.getpid(), "session": args.session_id}), "utf-8")
    pending_ready.replace(ready)
    if agent_token:
        agent_file.write_text(json.dumps({"protocol": 1, "host": "127.0.0.1", "port": server.server_port,
            "token": agent_token, "pid": os.getpid()}), "utf-8")
        if os.name != "nt":
            agent_file.chmod(0o600)
    try:
        server.serve_forever()
    finally:
        server.server_close()
        with engine.lock:
            engine.unload()
        remove_owned_connection_files()


if __name__ == "__main__":
    main()
