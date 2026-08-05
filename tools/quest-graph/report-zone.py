#!/usr/bin/env python3
"""
Per-zone world-readiness report for the golden-path pipeline.

For the given zone(s), produces a markdown report with:
  1. Quest chains (kind-31 quest-completion prerequisites)
  2. Level/race gates per quest
  3. Missing-data checklist: quest-referenced NPCs without spawner rows,
     quest-referenced items without templates, broken act detail refs
  4. Milestone/quest_idx structure

Usage: python3 tools/quest-graph/report-zone.py /tmp/compact.sqlite3 9 124 125 > scorecard-explorations/solzreed-zone-report.md
"""
import sqlite3
import sys

DB = sys.argv[1]
ZONES = [int(z) for z in sys.argv[2:]]
ZONE_NAMES = {9: "Solzreed", 124: "Solzreed (2)", 125: "Solzreed (3)"}

c = sqlite3.connect(DB)
zone_clause = ",".join(str(z) for z in ZONES)
zone_name = ", ".join(ZONE_NAMES.get(z, str(z)) for z in ZONES)

# ---- quests in zone ----
quests = c.execute(
    f"SELECT id, name, LEVEL, category_id, milestone_id, chapter_idx, quest_idx, score, let_it_done, selective, successive, REPEATABLE "
    f"FROM quest_contexts WHERE zone_id IN ({zone_clause}) ORDER BY id").fetchall()

# ---- requirements per quest ----
def reqs_for(qid):
    rows = c.execute(
        "SELECT u.kind_id, u.value1, u.value2 FROM unit_reqs u "
        "JOIN quest_components cmp ON u.owner_id = cmp.id AND u.owner_type='QuestComponent' "
        "WHERE cmp.quest_context_id = ? ORDER BY u.kind_id", (qid,)).fetchall()
    return rows

# ---- missing spawner check: quest-referenced npcs without npc_spawner_npcs ----
spawned_npcs = {r[0] for r in c.execute("SELECT DISTINCT member_id FROM npc_spawner_npcs WHERE member_type='Npc' AND member_id IS NOT NULL")}
npc_names = dict(c.execute("SELECT id, name FROM npcs"))

def quest_npcs(qid):
    comp_clause = "(SELECT id FROM quest_components WHERE quest_context_id = ?)"
    joins = [
        ("quest_act_con_accept_npcs", "QuestActConAcceptNpc", "npc_id"),
        ("quest_act_con_report_npcs", "QuestActConReportNpc", "npc_id"),
        ("quest_act_obj_talks", "QuestActObjTalk", "npc_id"),
        ("quest_act_check_guards", "QuestActCheckGuard", "npc_id"),
        ("quest_act_obj_monster_hunts", "QuestActObjMonsterHunt", "npc_id"),
        ("quest_act_obj_spheres", "QuestActObjSphere", "npc_id"),
    ]
    rows = []
    for table, atype, col in joins:
        rows += c.execute(
            f"SELECT DISTINCT a.{col} FROM quest_acts qa "
            f"JOIN {table} a ON qa.act_detail_id = a.id AND qa.act_detail_type=? "
            f"WHERE qa.quest_component_id IN {comp_clause} AND a.{col} IS NOT NULL", (atype, qid)).fetchall()
    return rows

# ---- missing item templates ----
item_ids = {r[0] for r in c.execute("SELECT id FROM items")}

def quest_items(qid):
    rows = c.execute(
        "SELECT DISTINCT a.item_id, a.count FROM quest_acts qa "
        "JOIN quest_act_obj_item_gathers a ON qa.act_detail_id = a.id AND qa.act_detail_type='QuestActObjItemGather' "
        "WHERE qa.quest_component_id IN (SELECT id FROM quest_components WHERE quest_context_id = ?) AND a.item_id IS NOT NULL", (qid,)).fetchall() + \
        c.execute(
        "SELECT DISTINCT a.item_id, a.count FROM quest_acts qa "
        "JOIN quest_act_supply_items a ON qa.act_detail_id = a.id AND qa.act_detail_type='QuestActSupplyItem' "
        "WHERE qa.quest_component_id IN (SELECT id FROM quest_components WHERE quest_context_id = ?) AND a.item_id IS NOT NULL", (qid,)).fetchall()
    return rows

out = []
out.append(f"# Zone Readiness Report — {zone_name}")
out.append("")
out.append(f"Generated from compact.sqlite3 ({sys.argv[1]}), zones {zone_clause}.")
out.append("")
out.append(f"**{len(quests)} quest contexts** in zone.")
out.append("")

# ---- chains ----
out.append("## Quest chains (kind-31 completion prerequisites)")
out.append("")
chain_rows = []
for (qid, qname, *_) in quests:
    for kind, v1, v2 in reqs_for(qid):
        if kind == 31:
            chain_rows.append((qid, qname, v1))
if chain_rows:
    out.append("| quest | name | requires completion of |")
    out.append("|---|---|---|")
    for qid, qname, prereq in sorted(chain_rows):
        out.append(f"| {qid} | {qname or ''} | {prereq} |")
else:
    out.append("_No kind-31 prerequisites in this zone._")
out.append("")

# ---- gates ----
out.append("## Requirement gates (all kinds)")
out.append("")
gate_rows = []
for (qid, qname, *_) in quests:
    for kind, v1, v2 in reqs_for(qid):
        gate_rows.append((qid, qname, kind, v1, v2))
if gate_rows:
    out.append("| quest | name | kind | v1 | v2 |")
    out.append("|---|---|---|---|---|")
    for qid, qname, kind, v1, v2 in sorted(gate_rows):
        out.append(f"| {qid} | {qname or ''} | {kind} | {v1} | {v2} |")
else:
    out.append("_No requirement records._")
out.append("")

# ---- missing spawners ----
out.append("## Missing-data checklist")
out.append("")
out.append("### Quest-referenced NPCs with NO spawner row")
out.append("")
missing_npc = []
for (qid, qname, *_) in quests:
    for (npc_id,) in quest_npcs(qid):
        if npc_id and npc_id not in spawned_npcs:
            missing_npc.append((qid, qname, npc_id, npc_names.get(npc_id, "")))
if missing_npc:
    out.append("| quest | name | npc | npc name |")
    out.append("|---|---|---|---|")
    for qid, qname, npc_id, npc_name in sorted(set(missing_npc)):
        out.append(f"| {qid} | {qname or ''} | {npc_id} | {npc_name or ''} |")
else:
    out.append("_None — all quest NPCs have spawner rows._")
out.append("")

out.append("### Quest-referenced items with NO template row")
out.append("")
missing_item = []
for (qid, qname, *_) in quests:
    for item_id, count in quest_items(qid):
        if item_id and item_id not in item_ids:
            missing_item.append((qid, qname, item_id, count))
if missing_item:
    out.append("| quest | name | item | count |")
    out.append("|---|---|---|---|")
    for qid, qname, item_id, count in sorted(set(missing_item)):
        out.append(f"| {qid} | {qname or ''} | {item_id} | {count} |")
else:
    out.append("_None — all quest items have templates._")
out.append("")

print("\n".join(out))
