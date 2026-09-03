#!/usr/bin/env bash
set -euo pipefail

# Lightweight archaeology MCP availability/read-only smoke for the normal
# repository gate. Unlike Scripts/mcp-archaeology-smoke.sh (the full pre-merge
# check), this only exercises protocol/server availability, the expected
# 24-tool surface, the canonical repo-local compact.sqlite3 (679+ tables), a
# simple read-only SELECT, and read-only rejection of a DROP.
#
# It requires NO game_pak/client assets, NO MySQL, and NO expensive
# archaeology unit-test run. ARCHEAGE_PAK_PATH is intentionally unset, so the
# AAPak tools report their deterministic unconfigured errors (never assumed).
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
responses="$(mktemp)"
trap 'rm -f "$responses"' EXIT

printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_sources","arguments":{}}}' \
  '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_tables","arguments":{}}}' \
  '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"query_sql","arguments":{"sql":"SELECT id, name FROM items WHERE id = 29040"}}}' \
  '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"query_sql","arguments":{"sql":"DROP TABLE npcs"}}}' \
  | dotnet run --no-restore --project "$repo_root/AAEmu.ArchaeologyMcp" --no-launch-profile 2>/dev/null \
  | grep '^{' >"$responses"

python3 - "$responses" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    replies = [json.loads(line) for line in stream if line.strip()]

assert len(replies) == 6, replies
assert replies[0]["id"] == 1
assert replies[0]["result"]["protocolVersion"] == "2025-03-26"
assert replies[0]["result"]["serverInfo"]["name"] == "aaemu-archaeology"

tools = replies[1]["result"]["tools"]
assert len(tools) == 24, len(tools)
assert {tool["name"] for tool in tools} >= {
    "list_sources", "list_databases", "list_tables", "describe_table",
    "query_sql", "read_file", "search_files", "search_everything",
    "trace_references", "find_quest_objectives", "trace_skill", "trace_item",
    "trace_quest", "trace_npc", "trace_doodad", "trace_mate",
    "trace_vehicle", "trace_crafting", "trace_world_spawn", "search_physics",
    "compare_source_data", "list_pak_entries", "read_pak_entry", "lookup_row",
}

sources = json.loads(replies[2]["result"]["content"][0]["text"])
assert sources["ok"] is True
assert sources["provenance"]["tool"] == "list_sources"
ids = [s["source_id"] for s in sources["data"]["sources"]]
assert "compact.sqlite3" in ids

tables = json.loads(replies[3]["result"]["content"][0]["text"])
assert tables["ok"] is True
assert len(tables["data"]["tables"]) >= 679  # canonical DB has 679 tables/views
assert "npcs" in tables["data"]["tables"] and "items" in tables["data"]["tables"]

query = json.loads(replies[4]["result"]["content"][0]["text"])
assert query["ok"] is True
assert query["data"]["rows"][0]["id"] == 29040
assert query["provenance"]["path"].endswith("compact.sqlite3")

rejected = json.loads(replies[5]["result"]["content"][0]["text"])
assert rejected["ok"] is False
assert "forbidden keyword" in rejected["error"]

print(f"MCP archaeology gate smoke passed: {len(tools)} tools, {len(tables['data']['tables'])} tables, read-only")
PY
