"""Small read-only MCP stdio adapter. No SDK or additional environment required."""
import argparse
import json
import sys
from search import call

PROTOCOL = "2025-11-25"
TOOLS = [
    {"name": "unity_asset_status", "description": "Read local Unity asset index/model status.",
     "inputSchema": {"type": "object", "additionalProperties": False},
     "annotations": {"readOnlyHint": True, "destructiveHint": False, "openWorldHint": False}},
    {"name": "unity_asset_search", "description": "Search existing Unity text, image and audio assets by meaning within the Editor-selected scope. Returns GUID, path, cosine score and chunk offset; inspect actual assets before use.",
     "inputSchema": {"type": "object", "properties": {
         "query": {"type": "string", "minLength": 1, "maxLength": 4096},
         "kind": {"type": "string", "enum": ["all", "text", "image", "audio"], "default": "all"},
         "top": {"type": "integer", "minimum": 1, "maximum": 100, "default": 10},
         "code": {"type": "boolean", "default": False},
         "minScore": {"type": "number", "minimum": -1, "maximum": 1, "default": -1},
         "sort": {"type": "string", "enum": ["similarity", "name", "path"], "default": "similarity"},
         "pathContains": {"type": "string", "maxLength": 256},
         "extensions": {"type": "string", "maxLength": 256, "description": "Comma-separated extensions, e.g. png,jpg"}}, "required": ["query"], "additionalProperties": False},
     "annotations": {"readOnlyHint": True, "destructiveHint": False, "openWorldHint": False}}
]


def dispatch(project, method, params):
    if method == "initialize":
        supported = {"2024-11-05", "2025-03-26", "2025-06-18", PROTOCOL}
        return {"protocolVersion": params.get("protocolVersion") if params.get("protocolVersion") in supported else PROTOCOL, "capabilities": {"tools": {}},
                "serverInfo": {"name": "unity-local-asset-search", "version": "0.2.0"}}
    if method == "ping":
        return {}
    if method == "tools/list":
        return {"tools": TOOLS}
    if method != "tools/call":
        raise LookupError("Method not found")
    name, args = params.get("name"), params.get("arguments", {})
    if name not in {t["name"] for t in TOOLS}:
        raise ValueError("Unknown tool")
    if not isinstance(args, dict):
        raise ValueError("Expected arguments object")
    if name == "unity_asset_status":
        if args:
            raise ValueError("Status takes no arguments")
        endpoint, body = "/status", {}
    else:
        if set(args) - {"query", "kind", "top", "code", "minScore", "sort", "pathContains", "extensions"}:
            raise ValueError("Unknown search argument")
        query, kind, top, code = args.get("query"), args.get("kind", "all"), args.get("top", 10), args.get("code", False)
        if (not isinstance(query, str) or not 1 <= len(query.strip()) <= 4096
                or kind not in ("all", "text", "image", "audio") or type(top) is not int or not 1 <= top <= 100 or type(code) is not bool):
            raise ValueError("Invalid search arguments")
        minimum, order = args.get("minScore", -1), args.get("sort", "similarity")
        path, extensions = args.get("pathContains", ""), args.get("extensions", "")
        if (type(minimum) not in (int, float) or not -1 <= minimum <= 1 or order not in ("similarity", "name", "path")
                or not isinstance(path, str) or len(path) > 256 or not isinstance(extensions, str) or len(extensions) > 256):
            raise ValueError("Invalid filter arguments")
        endpoint, body = "/search", {"query": query, "kind": kind, "top": top, "code": code,
                                  "minScore": minimum, "sort": order, "pathContains": path, "extensions": extensions}
    try:
        result = call(project, endpoint, body)
        return {"content": [{"type": "text", "text": json.dumps(result, ensure_ascii=False)}], "structuredContent": result, "isError": False}
    except Exception as ex:
        return {"content": [{"type": "text", "text": str(ex)}], "isError": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True)
    project = parser.parse_args().project
    sys.stdin.reconfigure(encoding="utf-8"); sys.stdout.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        request_id = None
        try:
            request = json.loads(line)
            if not isinstance(request, dict) or request.get("jsonrpc") != "2.0" or not isinstance(request.get("method"), str):
                raise ValueError("Invalid JSON-RPC request")
            if "id" not in request:
                continue
            request_id = request["id"]
            result = dispatch(project, request["method"], request.get("params", {}))
            response = {"jsonrpc": "2.0", "id": request_id, "result": result}
        except Exception as ex:
            code = -32700 if isinstance(ex, json.JSONDecodeError) else -32601 if isinstance(ex, LookupError) else -32602
            response = {"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": str(ex)}}
        print(json.dumps(response, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
