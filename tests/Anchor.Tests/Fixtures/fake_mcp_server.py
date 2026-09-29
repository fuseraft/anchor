"""Minimal MCP stdio server for tests: newline-delimited JSON-RPC 2.0."""
import json, os, sys

TOOLS = [
    {"name": "echo", "description": "Echo text back.", "annotations": {"readOnlyHint": True},
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}},
    {"name": "write_note", "description": "Pretend to save a note.",
     "inputSchema": {"type": "object", "properties": {"note": {"type": "string"}}}},
    {"name": "leak", "description": "Print an environment variable.", "annotations": {"readOnlyHint": True},
     "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}}}},
    {"name": "fail", "description": "Always fails.", "annotations": {"readOnlyHint": True},
     "inputSchema": {"type": "object", "properties": {}}},
]

def reply(id, result):
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": id, "result": result}) + "\n")
    sys.stdout.flush()

for line in sys.stdin:
    msg = json.loads(line)
    method, id, params = msg.get("method"), msg.get("id"), msg.get("params") or {}
    if id is None:
        continue
    if method == "initialize":
        reply(id, {"protocolVersion": params.get("protocolVersion"), "capabilities": {"tools": {}},
                   "serverInfo": {"name": "fake", "version": "1.0"}})
    elif method == "tools/list":
        reply(id, {"tools": TOOLS})
    elif method == "tools/call":
        name, args = params["name"], params.get("arguments") or {}
        if name == "echo":
            text, error = "echo: " + args.get("text", ""), False
        elif name == "write_note":
            text, error = "saved: " + args.get("note", ""), False
        elif name == "leak":
            text, error = os.environ.get(args.get("name", ""), "(unset)"), False
        else:
            text, error = "this tool always fails", True
        reply(id, {"content": [{"type": "text", "text": text}], "isError": error})
    else:
        sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": id, "error": {"code": -32601, "message": "unknown method"}}) + "\n")
        sys.stdout.flush()
