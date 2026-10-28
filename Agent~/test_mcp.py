import json
from pathlib import Path
import subprocess
import sys
import unittest
import mcp_server


class McpTests(unittest.TestCase):
    def test_stdio_initialize_discover_and_disconnected_error(self):
        requests = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "unity_asset_search", "arguments": {"query": "한국어 검"}}}]
        run = subprocess.run([sys.executable, str(Path(__file__).with_name("mcp_server.py")), "--project", str(Path(__file__).parent)],
                             input="\n".join(json.dumps(r) for r in requests) + "\n", text=True, encoding="utf-8", capture_output=True)
        self.assertEqual(run.returncode, 0, run.stderr)
        responses = [json.loads(line) for line in run.stdout.splitlines()]
        self.assertEqual(len(responses), 3)
        self.assertEqual(responses[0]["result"]["protocolVersion"], "2025-06-18")
        self.assertEqual([t["name"] for t in responses[1]["result"]["tools"]], ["unity_asset_status", "unity_asset_search"])
        self.assertTrue(responses[2]["result"]["isError"])
        self.assertIn("Unity", responses[2]["result"]["content"][0]["text"])

    def test_reject_mutation_scope_override_and_invalid_schema(self):
        for args in ({"query": "sword", "roots": ["Assets"]}, {"query": "sword", "top": True}, {"query": "sword", "code": "yes"}, {"query": ""}, {"query": "sword", "minScore": 2}):
            with self.assertRaises(ValueError):
                mcp_server.dispatch(".", "tools/call", {"name": "unity_asset_search", "arguments": args})
        with self.assertRaises(ValueError):
            mcp_server.dispatch(".", "tools/call", {"name": "unity_asset_clear", "arguments": {}})


if __name__ == "__main__":
    unittest.main()
