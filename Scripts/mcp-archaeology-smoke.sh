#!/usr/bin/env bash
set -euo pipefail

# trace_item / find_quest_objectives / search_physics / list_pak_entries /
# read_pak_entry (unconfigured deterministic errors when ARCHEAGE_PAK_PATH
# is unset).
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
  '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"read_file","arguments":{"path":"AAEmu.Game/Data/CharTemplates.json"}}}' \
  '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"search_files","arguments":{"pattern":"Patrashu","root":"scorecard-explorations","glob":"*.md"}}}' \
  '{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"trace_item","arguments":{"id":29040}}}' \
  '{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"find_quest_objectives","arguments":{"quest_id":1502,"family":"talks"}}}' \
  '{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"search_physics","arguments":{}}}' \
  '{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"list_pak_entries","arguments":{}}}' \
  '{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"read_pak_entry","arguments":{"name":"ui/questcontext/quest.lua"}}}' \
  '{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"lookup_row","arguments":{"table":"items","id":29040}}}' \
  | dotnet run --no-restore --project "$repo_root/AAEmu.ArchaeologyMcp" --no-launch-profile 2>/dev/null \
  | grep '^{' >"$responses"

python3 - "$responses" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    replies = [json.loads(line) for line in stream if line.strip()]

assert len(replies) == 14, replies
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
assert "compact.sqlite3" in ids and "data" in ids and "game-source" in ids

tables = json.loads(replies[3]["result"]["content"][0]["text"])
assert tables["ok"] is True
assert len(tables["data"]["tables"]) > 100  # canonical DB has 679 tables
assert "npcs" in tables["data"]["tables"] and "items" in tables["data"]["tables"]

query = json.loads(replies[4]["result"]["content"][0]["text"])
assert query["ok"] is True
assert query["data"]["rows"][0]["id"] == 29040
assert query["provenance"]["path"].endswith("compact.sqlite3")

rejected = json.loads(replies[5]["result"]["content"][0]["text"])
assert rejected["ok"] is False
assert "forbidden keyword" in rejected["error"]

read = json.loads(replies[6]["result"]["content"][0]["text"])
assert read["ok"] is True
assert read["data"]["size"] > 0 and read["data"]["truncated"] is False

search = json.loads(replies[7]["result"]["content"][0]["text"])
assert search["ok"] is True
assert search["data"]["match_count"] >= 1
assert search["provenance"]["tool"] == "search_files"

trace_item = json.loads(replies[8]["result"]["content"][0]["text"])
assert trace_item["ok"] is True
assert trace_item["data"]["supported"] is True
assert trace_item["data"]["rows"][0]["id"] == 29040
assert trace_item["data"]["evidence"] == "exact"

quest = json.loads(replies[9]["result"]["content"][0]["text"])
assert quest["ok"] is True
assert quest["data"]["supported"] is True
assert quest["data"]["row_count"] >= 1
assert quest["data"]["rows"][0]["quest_context_id"] == 1502
assert quest["data"]["rows"][0]["family"] == "quest_act_obj_talks"

# AAPak surface: deterministic unconfigured errors when ARCHEAGE_PAK_PATH is
# unset (the smoke never assumes local 24.8 GB assets).
pak_list = json.loads(replies[11]["result"]["content"][0]["text"])
assert pak_list["ok"] is False
assert "not configured" in pak_list["error"]
assert pak_list["provenance"]["tool"] == "list_pak_entries"

pak_read = json.loads(replies[12]["result"]["content"][0]["text"])
assert pak_read["ok"] is False
assert "not configured" in pak_read["error"]
assert pak_read["provenance"]["tool"] == "read_pak_entry"

lookup = json.loads(replies[13]["result"]["content"][0]["text"])
assert lookup["ok"] is True
assert lookup["data"]["supported"] is True
assert lookup["data"]["rows"][0]["id"] == 29040
assert lookup["provenance"]["tool"] == "lookup_row"

print(f"MCP archaeology stdio smoke passed: {len(tools)} tools, {len(tables['data']['tables'])} tables, read-only")
PY
