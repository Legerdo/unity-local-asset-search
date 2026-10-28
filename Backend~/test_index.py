import argparse
from pathlib import Path
import sqlite3
import tempfile
import time
import unittest
import numpy as np
import server


class DeterministicEngine(server.Engine):
    """Algorithm-only fixture. Real model validation is a separate Unity entry point."""
    def load(self):
        self.model = object()
        self.fingerprint = "algorithm-test"
    def encode_many(self, data, prompt="", batch_size=16):
        self.calls.append((len(data), batch_size))
        return np.tile(np.array([1, 0, 0, 0], dtype="<f4"), (len(data), 1))


class IndexTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "Assets/Selected").mkdir(parents=True)
        self.model = self.root / "model"
        self.model.mkdir()
        self.old_validate = server.validate_model
        server.validate_model = lambda _: self.model
        self.engine = DeterministicEngine(argparse.Namespace(project_root=str(self.root), state_dir=str(self.root / "Library/index"), model_dir=str(self.model), dimensions=4, device="cpu"))
        server.validate_model = self.old_validate
        self.engine.calls = []
    def tearDown(self):
        server.validate_model = self.old_validate
        self.temp.cleanup()
    def items(self, count=20):
        result = []
        for n in range(count):
            path = self.root / f"Assets/Selected/{n}.txt"
            path.write_text("A metal sword", "utf-8")
            result.append({"guid": f"{n:032x}", "path": path.relative_to(self.root).as_posix(), "source": str(path), "kind": "text"})
        return result
    def run_index(self, items):
        self.engine.start({"items": items})
        until = time.monotonic() + 5
        while self.engine.snapshot()["running"]:
            self.assertLess(time.monotonic(), until)
            time.sleep(.01)
        return self.engine.snapshot()
    def test_batching_incremental_and_one_changed_asset(self):
        items = self.items()
        result = self.run_index(items)
        self.assertEqual((result["updated"], result["failed"]), (20, 0))
        self.assertEqual(self.engine.calls, [(8, 8), (8, 8), (4, 8)])
        self.engine.calls.clear()
        self.assertEqual(self.run_index(items)["skipped"], 20)
        self.assertEqual(self.engine.calls, [])
        Path(items[0]["source"]).write_text("A blue potion", "utf-8")
        self.assertEqual(self.run_index(items)["updated"], 1)
    def test_invalid_text_does_not_fail_other_batch_members(self):
        items = self.items(3)
        Path(items[0]["source"]).write_bytes(b"\xff\xfe\x80")
        result = self.run_index(items)
        self.assertEqual((result["done"], result["updated"], result["failed"]), (3, 2, 1))
    def test_scope_filter_precedes_vector_decoding_and_query_cache(self):
        items = self.items(2)
        self.run_index(items)
        with self.engine.db() as db:
            db.execute("UPDATE vectors SET vector=? WHERE guid=?", (b"invalid", items[1]["guid"]))
        query = {"query": "a sword", "kind": "text", "roots": [items[0]["path"]]}
        self.engine.calls.clear()
        self.assertEqual(self.engine.search(query)["results"][0]["guid"], items[0]["guid"])
        self.assertFalse(self.engine.search(dict(query, query="a potion"))["cache_hit"])
        self.assertTrue(self.engine.search(query)["cache_hit"])
        self.assertEqual(len(self.engine.calls), 2)
    def test_cancellation_keeps_existing_asset_transaction(self):
        item = self.items(1)[0]
        self.run_index([item])
        with self.engine.db() as db:
            before = db.execute("SELECT hash FROM assets").fetchone()[0]
        self.engine.cancel.set()
        with self.assertRaises(InterruptedError):
            self.engine.write_asset(item, "changed", [(0, np.ones(4, dtype="<f4"))])
        with self.engine.db() as db:
            self.assertEqual(db.execute("SELECT hash FROM assets").fetchone()[0], before)

    def test_bfloat16_rounding_is_renormalized_in_float32(self):
        vectors = np.array([[.998, 0, 0, 0], [0, 1.002, 0, 0]], dtype="<f4")
        result = server.normalized_vectors(vectors, 2, 4)
        np.testing.assert_allclose(np.linalg.norm(result, axis=1), 1, atol=1e-6)
        for invalid in (np.zeros((1, 4)), np.full((1, 4), np.nan)):
            with self.assertRaisesRegex(ValueError, "invalid embedding"):
                server.normalized_vectors(invalid, 1, 4)

    def test_score_sort_path_and_extension_filters(self):
        items = self.items(3); self.run_index(items)
        with self.engine.db() as db:
            for item, score in zip(items, (.5, .9, .7)):
                vector = np.array([score, np.sqrt(1-score*score), 0, 0], dtype="<f4")
                db.execute("UPDATE vectors SET vector=? WHERE guid=?", (vector.tobytes(), item["guid"]))
        query = {"query": "a sword", "minScore": .8}
        self.assertEqual([r["guid"] for r in self.engine.search(query)["results"]], [items[1]["guid"]])
        self.assertEqual(self.engine.search(dict(query, minScore=1.0))["results"], [])
        named = self.engine.search(dict(query, minScore=-1, sort="name", top=2))
        self.assertEqual(named["matched"], 3)
        self.assertEqual([r["guid"] for r in named["results"]], [i["guid"] for i in items[:2]])
        self.assertEqual(self.engine.search(dict(query, minScore=-1, extensions=".txt", pathContains="/2.txt"))["results"][0]["guid"], items[2]["guid"])
        self.assertEqual(self.engine.search(dict(query, extensions="png,jpg"))["results"], [])
        with self.assertRaises(ValueError):
            self.engine.search(dict(query, minScore=2))
        with self.engine.db() as db:
            db.execute("UPDATE vectors SET vector=? WHERE guid!=?", (b"invalid", items[1]["guid"]))
        self.assertEqual(len(self.engine.search(dict(query, pathContains="1.txt"))["results"]), 1)


if __name__ == "__main__":
    unittest.main()
