"""Boundary/regression checks; real inference is tested separately in Unity."""
import argparse
import http.client
import json
from pathlib import Path
import tempfile
import threading
import subprocess
import sys
import unittest
import server


class BoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "Assets").mkdir()
        self.model = self.root / "model"
        self.model.mkdir()
        for name in ["config.json", "tokenizer.json", "tokenizer_config.json", "modules.json",
                     "config_sentence_transformers.json", "processor_config.json", "preprocessor_config.json", "1_Pooling/config.json"]:
            path = self.model / name
            path.parent.mkdir(exist_ok=True)
            path.write_text(json.dumps({"model_type": "embedding_gemma2"}), "utf-8")
        (self.model / "model.safetensors").write_bytes(b"unit-test-placeholder-not-a-model")
        self.engine = server.Engine(argparse.Namespace(project_root=str(self.root), state_dir=str(self.root / "Library" / "LocalAssetSearch"),
                                                     model_dir=str(self.model), dimensions=768, device="cpu"))

    def tearDown(self):
        self.temp.cleanup()

    def test_reject_incomplete_model(self):
        (self.model / "tokenizer.json").unlink()
        with self.assertRaisesRegex(ValueError, "tokenizer.json"):
            server.validate_model(self.model)

    def test_downloader_does_not_overwrite_existing_incomplete_weights(self):
        (self.model / "tokenizer.json").unlink()
        before = (self.model / "model.safetensors").read_bytes()
        with self.assertRaisesRegex(ValueError, "Existing weights"):
            server.download_model(self.model)
        self.assertEqual((self.model / "model.safetensors").read_bytes(), before)

    def test_refuse_outside_asset_or_preview_path(self):
        asset = self.root / "Assets" / "a.txt"
        asset.write_text("A sword", "utf-8")
        item = {"guid": "a" * 32, "path": "Assets/a.txt", "kind": "text", "source": str(self.root / "outside.txt")}
        (self.root / "outside.txt").write_text("not selected", "utf-8")
        with self.assertRaisesRegex(ValueError, "outside"):
            self.engine.source(item)
        item["source"] = str(asset)
        self.assertEqual(self.engine.source(item), asset.resolve())
        item["path"] = "Assets/../outside.txt"
        with self.assertRaisesRegex(ValueError, "inside"):
            self.engine.source(item)

    def test_no_state_outside_project_library(self):
        with self.assertRaisesRegex(ValueError, "Library"):
            server.Engine(argparse.Namespace(project_root=str(self.root), state_dir=str(self.root / "Assets" / "state"), model_dir=str(self.model), dimensions=768, device="cpu"))

    def test_auth_origin_and_host(self):
        token = "a" * 64
        http = server.ThreadingHTTPServer(("127.0.0.1", 0), server.make_handler(self.engine, token))
        thread = threading.Thread(target=http.serve_forever, daemon=True)
        thread.start()
        def request(headers):
            connection = http_client()
            connection.request("POST", "/status", "{}", headers)
            response = connection.getresponse()
            result = response.status
            response.read(); connection.close()
            return result
        def http_client():
            return http_client_class("127.0.0.1", http.server_port)
        http_client_class = __import__("http.client", fromlist=["HTTPConnection"]).HTTPConnection
        try:
            self.assertEqual(request({}), 401)
            auth = {"Authorization": "Bearer " + token}
            self.assertEqual(request(auth), 200)
            self.assertEqual(request(dict(auth, Origin="https://example.com")), 403)
            self.assertEqual(request(dict(auth, Host="evil.example")), 403)
        finally:
            http.shutdown(); http.server_close(); thread.join()

    def test_chunk_tail_coverage(self):
        text = "x" * 4500
        parts = list(server.chunks(text))
        self.assertEqual(parts[-1][0] + len(parts[-1][1]), len(text))
        self.assertTrue(all(len(value) <= 1800 for _, value in parts))

    def test_agent_read_only_scope_and_utf8_cli(self):
        token, agent = "a" * 64, "b" * 64
        received = []
        self.engine.search = lambda body: received.append(body) or {"results": [{"path": "Assets/선택/검.txt"}]}
        http = server.ThreadingHTTPServer(("127.0.0.1", 0), server.make_handler(self.engine, token, agent))
        thread = threading.Thread(target=http.serve_forever, daemon=True); thread.start()
        def request(endpoint, body):
            connection = http_client_class("127.0.0.1", http.server_port)
            connection.request("POST", endpoint, json.dumps(body), {"Authorization": "Bearer " + agent})
            response = connection.getresponse(); code = response.status; response.read(); connection.close()
            return code
        http_client_class = __import__("http.client", fromlist=["HTTPConnection"]).HTTPConnection
        try:
            self.assertEqual(request("/status", {}), 200)
            for endpoint in ("/index", "/clear", "/cancel", "/warmup"):
                self.assertEqual(request(endpoint, {}), 403)
            self.assertEqual(request("/search", {"query": "a sword"}), 400)
            scope = {"roots": ["Assets/선택"], "excluded": ["Assets/선택/제외"]}
            (self.engine.state / "agent-scope.json").write_text(json.dumps(scope), "utf-8")
            self.assertEqual(request("/search", {"query": "검", "roots": ["Assets"], "excluded": []}), 200)
            self.assertEqual(received[-1]["roots"], scope["roots"])
            self.assertEqual(received[-1]["excluded"], scope["excluded"])
            (self.engine.state / "agent-connection.json").write_text(json.dumps({"protocol": 1, "host": "127.0.0.1", "port": http.server_port, "token": agent}), "utf-8")
            run = subprocess.run([sys.executable, str(Path(__file__).parents[1] / "Agent~/search.py"),
                "--project", str(self.root), "--query", "한국어 검"], capture_output=True, encoding="utf-8")
            self.assertEqual(run.returncode, 0, run.stderr)
            self.assertIn("선택/검", run.stdout)
            self.assertEqual(received[-1]["query"], "한국어 검")
        finally:
            http.shutdown(); http.server_close(); thread.join()


if __name__ == "__main__":
    unittest.main()
