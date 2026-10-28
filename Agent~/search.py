"""Read-only Unity asset search for local agents. Python standard library only."""
import argparse
import http.client
import json
from pathlib import Path
import re
import sys


def call(project, endpoint, body):
    path = Path(project).resolve(strict=True) / "Library/LocalAssetSearch/agent-connection.json"
    if not path.is_file():
        raise ValueError("Unity에서 Tools > Local Asset Search를 열고 모델을 연결하세요. / Connect the model in the Unity Editor first.")
    connection = json.loads(path.read_text("utf-8"))
    if (not isinstance(connection, dict) or connection.get("protocol") != 1 or connection.get("host") != "127.0.0.1"
            or type(connection.get("port")) is not int or not 1 <= connection["port"] <= 65535
            or not isinstance(connection.get("token"), str)
            or not re.fullmatch(r"[a-f0-9]{64}", connection.get("token", ""))):
        raise ValueError("Invalid local agent connection file; reconnect in the Unity Editor")
    client = http.client.HTTPConnection("127.0.0.1", connection["port"], timeout=120)
    try:
        client.request("POST", endpoint, json.dumps(body).encode("utf-8"), {
            "Authorization": "Bearer " + connection["token"], "Content-Type": "application/json"})
        response = client.getresponse()
        payload = json.loads(response.read(8 * 1024 * 1024))
        if response.status != 200:
            raise ValueError(payload.get("error", "Local search failed"))
        return payload
    finally:
        client.close()


def main():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True, help="Unity project folder containing Assets and Library")
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--query")
    action.add_argument("--status", action="store_true")
    parser.add_argument("--kind", choices=["all", "text", "image", "audio"], default="all")
    parser.add_argument("--top", type=int, choices=range(1, 101), metavar="1..100", default=10)
    parser.add_argument("--code", action="store_true", help="Use the code retrieval query prompt")
    parser.add_argument("--min-score", type=float, default=-1, help="Cosine threshold from -1 to 1")
    parser.add_argument("--sort", choices=["similarity", "name", "path"], default="similarity")
    parser.add_argument("--path-contains", default="")
    parser.add_argument("--extensions", default="", help="Comma-separated extensions, e.g. png,jpg")
    args = parser.parse_args()
    try:
        body = {} if args.status else {"query": args.query, "kind": args.kind, "top": args.top, "code": args.code,
            "minScore": args.min_score, "sort": args.sort, "pathContains": args.path_contains, "extensions": args.extensions}
        print(json.dumps(call(args.project, "/status" if args.status else "/search", body), ensure_ascii=False))
    except (OSError, ValueError, KeyError, http.client.HTTPException) as ex:
        print(json.dumps({"error": str(ex)}, ensure_ascii=False), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
