#!/usr/bin/env bash
set -euo pipefail

# Protocol-only smoke: no game server, token, or gameplay mutation is needed.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
responses="$(mktemp)"
trap 'rm -f "$responses"' EXIT

printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  | dotnet run --no-restore --project "$repo_root/AAEmu.BotControlMcp" --no-launch-profile >"$responses"

python3 - "$responses" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    replies = [json.loads(line) for line in stream if line.strip()]

assert len(replies) == 2, replies
assert replies[0]["id"] == 1
assert replies[0]["result"]["protocolVersion"] == "2025-03-26"
assert replies[1]["id"] == 2
tools = replies[1]["result"]["tools"]
assert len(tools) == 19, len(tools)
assert {tool["name"] for tool in tools} >= {"observe", "action_status", "trace"}
print(f"MCP stdio protocol smoke passed: {len(tools)} tools")
PY
