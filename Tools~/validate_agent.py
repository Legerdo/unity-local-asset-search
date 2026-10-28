"""Integration check against a running real Editor backend, without Computer Use."""
import argparse
import json
import math
from pathlib import Path
import subprocess
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--project", required=True)
parser.add_argument("--output")
args = parser.parse_args()
adapter = Path(__file__).parents[1] / "Agent~/mcp_server.py"
process = subprocess.Popen([sys.executable, str(adapter), "--project", args.project], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, text=True, encoding="utf-8")
report = {"transport": "MCP stdio", "checks": []}
try:
    def request(number, method, params=None):
        process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": number, "method": method, "params": params or {}}) + "\n"); process.stdin.flush()
        response = json.loads(process.stdout.readline())
        assert response["id"] == number and "error" not in response, response
        return response["result"]
    assert request(1, "initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "integration", "version": "1"}})["protocolVersion"] == "2025-11-25"
    process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); process.stdin.flush()
    assert len(request(2, "tools/list")["tools"]) == 2
    status = request(3, "tools/call", {"name": "unity_asset_status", "arguments": {}})
    assert not status["isError"] and status["structuredContent"]["model_loaded"]
    for n, (kind, query) in enumerate([("text", "a sharp metal sword"), ("image", "a red square"), ("audio", "an electronic tone")], 4):
        result = request(n, "tools/call", {"name": "unity_asset_search", "arguments": {"query": query, "kind": kind, "top": 3}})
        assert not result["isError"], result
        hits = result["structuredContent"]["results"]
        assert hits and all(hit["kind"] == kind and math.isfinite(hit["score"]) for hit in hits)
        report["checks"].append({"kind": kind, "top_path": hits[0]["path"], "score": hits[0]["score"]})
    filtered = request(7, "tools/call", {"name": "unity_asset_search", "arguments": {
        "query": "a red square", "kind": "image", "minScore": .7, "pathContains": "RedSquare", "extensions": "png", "sort": "path", "top": 1}})
    assert not filtered["isError"] and len(filtered["structuredContent"]["results"]) == 1
    assert filtered["structuredContent"]["results"][0]["score"] >= .7
    report["advanced_filters"] = True
    report["passed"] = True
    if args.output:
        Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), "utf-8")
    print(json.dumps(report, ensure_ascii=True))
finally:
    process.stdin.close()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill(); process.wait()
    assert process.returncode == 0, process.stderr.read()
