"""Real-model HTTP memory audit in a separate validation project's Library.

Requires psutil (available in the validated backend environment). Never uses the
project's normal index or changes source assets. No model downloads are performed.
"""
import argparse
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import time
from datetime import datetime, timezone
import psutil

ROOT = Path(__file__).resolve().parents[1]


class Session:
    def __init__(self, project, model, label):
        self.state = project / "Library/LocalAssetSearchMemoryAudit" / (label + "-" + secrets.token_hex(4))
        self.state.mkdir(parents=True)
        self.token, self.session = secrets.token_hex(32), secrets.token_hex(16)
        env = dict(os.environ, UNITY_EMBEDDING_TOKEN=self.token, PYTHONUTF8="1", PYTHONIOENCODING="utf-8", HF_HUB_DISABLE_TELEMETRY="1")
        self.log = (self.state / "process.log").open("wb")
        self.process = subprocess.Popen([sys.executable, str(ROOT / "Backend~/server.py"), "--project-root", str(project),
            "--model-dir", str(model), "--state-dir", str(self.state), "--device", "cuda", "--dimensions", "768",
            "--session-id", self.session], env=env, stdin=subprocess.PIPE, stdout=self.log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        self.owner = psutil.Process(self.process.pid)
        self.children = {}
        self.worker = None

    def remember(self):
        if self.owner.is_running():
            try:
                for child in self.owner.children(recursive=True):
                    self.children[child.pid] = child
            except psutil.NoSuchProcess:
                pass

    def ready(self):
        deadline = time.monotonic() + 120
        while not (self.state / "ready.json").is_file():
            self.remember()
            if self.process.poll() is not None:
                raise RuntimeError("Backend exited before ready: " + (self.state / "process.log").read_text("utf-8", errors="replace")[-2000:])
            if time.monotonic() > deadline:
                raise TimeoutError("Backend startup")
            time.sleep(.05)
        self.remember()
        info = json.loads((self.state / "ready.json").read_text("utf-8"))
        assert info["session"] == self.session
        self.port = info["port"]
        self.worker = psutil.Process(info["pid"])
        return self

    def call(self, endpoint, body=None):
        client = http.client.HTTPConnection("127.0.0.1", self.port, timeout=180)
        try:
            client.request("POST", endpoint, json.dumps(body or {}), {"Authorization": "Bearer " + self.token})
            response = client.getresponse(); data = json.loads(response.read())
            if response.status != 200:
                raise RuntimeError(str(data))
            return data
        finally:
            client.close()

    def memory(self, phase):
        status = self.call("/status")
        time.sleep(.02)
        mem = self.worker.memory_info()
        return dict(phase=phase, private_mb=getattr(mem, "private", mem.rss) / 1048576, rss_mb=mem.rss / 1048576,
                    handles=self.worker.num_handles() if os.name == "nt" else self.worker.num_fds(), threads=self.worker.num_threads(),
                    allocated_mb=status.get("gpu_allocated_mb", 0), reserved_mb=status.get("gpu_reserved_mb", 0),
                    query_cache=status["query_cache_entries"], model_loaded=status["model_loaded"])

    def close(self):
        self.remember()
        if self.process.stdin and not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=1.5)
            forced = False
        except subprocess.TimeoutExpired:
            self.process.kill(); self.process.wait(timeout=5); forced = True
        time.sleep(.1)
        remaining = [p for p in self.children.values() if p.is_running()]
        result = {"forced_launcher_kill": forced, "children_remaining_after_stop": len(remaining),
                  "observed_children": len(self.children), "remaining_pids": [p.pid for p in remaining]}
        # Test cleanup only: these exact children were captured from our own Popen.
        for child in remaining:
            if child.is_running():
                child.kill()
        for child in remaining:
            try:
                child.wait(timeout=5)
            except psutil.TimeoutExpired:
                pass
        self.log.close()
        return result


def fixture(project):
    folder = project / "Assets/LocalAssetSearchBenchmark"
    items = []
    for path in sorted(folder.iterdir()):
        if path.suffix.lower() not in (".txt", ".png", ".wav"):
            continue
        guid = re.search(r"^guid: ([a-f0-9]{32})$", path.with_name(path.name + ".meta").read_text("utf-8"), re.M)[1]
        items.append({"guid": guid, "path": path.relative_to(project).as_posix(), "source": str(path),
                      "kind": {".txt": "text", ".png": "image", ".wav": "audio"}[path.suffix.lower()]})
    assert len(items) == 134, "Run AssetSearchValidation in this separate project first"
    return items


def wait_index(session, items):
    session.call("/index", {"items": items})
    deadline = time.monotonic() + 180
    while True:
        status = session.call("/status")
        if not status["running"]:
            assert not status["errors"] and status["failed"] == 0, status
            return status
        assert time.monotonic() < deadline, "Index timeout"
        time.sleep(.05)


def validate_report(report):
    """Bounds for this warmed 134-asset fixture, not universal memory limits."""
    if report["rounds"]:
        baseline = report["baseline"]
        assert all(s["query_cache"] == 64 for s in report["rounds"])
        assert max(s["private_mb"] for s in report["rounds"]) <= baseline["private_mb"] + 128, "Query RAM growth exceeds 128 MiB"
        assert all(s["allocated_mb"] <= baseline["allocated_mb"] + 16 for s in report["rounds"] + report["reloads"]), "Live GPU tensors accumulate"
        assert all(s["allocated_mb"] <= 64 and not s["model_loaded"] for s in report["clears"]), "Unloaded model retains GPU tensors"
        assert all(s["threads"] <= baseline["threads"] + 2 for s in report["reloads"]), "Native loader threads accumulate"
        assert all(s["handles"] <= baseline["handles"] + 8 for s in report["reloads"]), "Native loader handles accumulate"
    assert not report["lifetime"]["forced_launcher_kill"], "Backend requires forced launcher termination"
    assert report["lifetime"]["children_remaining_after_stop"] == 0, "Owned descendants remain after stop"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--early-stop-only", action="store_true")
    parser.add_argument("--rounds", type=int, default=8)
    parser.add_argument("--queries-per-round", type=int, default=96)
    parser.add_argument("--reloads", type=int, default=3)
    args = parser.parse_args()
    project, model = args.project.resolve(), args.model.resolve()
    started = time.monotonic()
    report = {"rounds": [], "clears": [], "reloads": [], "passed": False,
              "started_utc": datetime.now(timezone.utc).isoformat(),
              "configuration": {"rounds": args.rounds, "queries_per_round": args.queries_per_round,
                                "reloads": args.reloads, "early_stop": args.early_stop_only}}
    session = Session(project, model, "early" if args.early_stop_only else "soak")
    try:
        if args.early_stop_only:
            deadline = time.monotonic() + .3
            while time.monotonic() < deadline:
                session.remember(); time.sleep(.01)
        else:
            session.ready(); session.call("/warmup")
            items = fixture(project); wait_index(session, items)
            def query(n):
                kind = ("text", "image", "audio")[n % 3]
                text = ("a metal sword, blue circle and electronic tone " * (1 + n % 4)) + str(n)
                result = session.call("/search", {"query": text, "kind": kind, "top": 10, "roots": ["Assets/LocalAssetSearchBenchmark"]})
                assert result["results"]
            for n in range(96):
                query(n)
            report["baseline"] = session.memory("warmed-baseline")
            for block in range(args.rounds):
                for n in range(args.queries_per_round):
                    query(1000 + block * args.queries_per_round + n)
                for _ in range(3):
                    status = wait_index(session, items)
                    assert status["skipped"] == 134 and status["updated"] == 0
                sample = session.memory("round-" + str(block + 1))
                assert sample["query_cache"] == 64
                report["rounds"].append(sample)
                print(json.dumps(sample), flush=True)
            for n in range(args.reloads):
                session.call("/clear"); time.sleep(.1)
                report["clears"].append(session.memory("clear-" + str(n + 1)))
                session.call("/warmup"); wait_index(session, items)
                for k in range(3): query(5000 + n * 3 + k)
                report["reloads"].append(session.memory("reload-" + str(n + 1)))
                print(json.dumps(report["clears"][-1]), flush=True)
    except Exception as exc:
        report["error"] = type(exc).__name__ + ": " + str(exc)
        raise
    finally:
        report["lifetime"] = session.close()
        report["elapsed_s"] = time.monotonic() - started
        try:
            if "error" not in report:
                validate_report(report)
                report["passed"] = True
        finally:
            args.output.write_text(json.dumps(report, indent=2), "utf-8")
            print(json.dumps({"lifetime": report["lifetime"], "passed": report["passed"]}), flush=True)


if __name__ == "__main__":
    main()
