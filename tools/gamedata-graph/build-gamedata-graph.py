#!/usr/bin/env python3
"""
Build a queryable knowledge graph of ALL AAEmu 1.2 game data (compact.sqlite3)
in graphify's graph.json format -> graphify-out/gamedata-graph.json

Every content row becomes a node; every resolvable `*_id` column becomes an
edge (validated against the target table's id set). Entity tables:

  npcs, items, skills, buffs, doodads, zone_templates, quest_contexts,
  quest_components, quest_acts, unit_reqs, races, crafts, loots, plot_events,
  plot_next_events, npc_spawners, events, item_groups, npc_groups, doodad_groups

Query:  graphify query "..." --graph graphify-out/gamedata-graph.json
Merge:  graphify merge-graphs <code-graph> <quest-graph> <this> --out graphify-out/merged-graph.json
"""
import json
import os
import sqlite3
import sys

DB = sys.argv[1] if len(sys.argv) > 1 else "/tmp/compact.sqlite3"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "graphify-out", "gamedata-graph.json")

# table -> node id prefix (consistent with the quest graph so merges dedupe)
ENTITY_TABLES = {
    "npcs": "npc", "items": "item", "skills": "skill", "buffs": "buff",
    "doodads": "doodad", "zone_templates": "zone", "quest_contexts": "quest",
    "quest_components": "component", "quest_acts": "act", "unit_reqs": "req",
    "races": "race", "crafts": "craft", "loots": "loot", "plot_events": "plot_event",
    "plot_next_events": "plot_next", "npc_spawners": "spawner", "events": "event",
    "item_groups": "item_group", "npc_groups": "npc_group", "doodad_groups": "doodad_group",
    "loot_packs": "loot_pack",
}

# column prefix -> node id prefix (longest match wins; validated against id sets)
COLUMN_TARGETS = [
    ("npc_spawner_id", "spawner"), ("npc_group_id", "npc_group"), ("npc_id", "npc"),
    ("item_group_id", "item_group"), ("item_asset_id", "item"), ("item_id", "item"),
    ("skill_id", "skill"), ("buff_id", "buff"), ("buff_tag_id", "buff"),
    ("doodad_group_id", "doodad_group"), ("doodad_id", "doodad"), ("zone_id", "zone"),
    ("quest_context_id", "quest"), ("race_id", "race"), ("craft_id", "craft"),
    ("loot_id", "loot"), ("loot_pack_id", "loot_pack"), ("plot_event_id", "plot_event"),
    ("effect_id", "effect"), ("event_id", "event"), ("spawner_id", "spawner"),
    ("appellation_id", "appellation"), ("grade_id", "grade"), ("body_id", "body"),
    ("member_id", "npc"),
]

NAME_COLUMNS = ["name", "name_en", "title", "label", "name_kr", "text", "localized"]


def main():
    c = sqlite3.connect(DB)
    existing = {r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    entity_tables = {t: p for t, p in ENTITY_TABLES.items() if t in existing}

    # ---- load id sets + name columns per entity table ----
    id_sets = {}
    names = {}
    for table, prefix in entity_tables.items():
        cols = [r[1] for r in c.execute(f"PRAGMA table_info({table})")]
        id_col = "id" if "id" in cols else None
        if not id_col:
            continue
        ids = {r[0] for r in c.execute(f"SELECT {id_col} FROM {table}")}
        id_sets[prefix] = ids
        name_col = next((col for col in NAME_COLUMNS if col in cols), None)
        if name_col:
            names[prefix] = {r[0]: (r[1] or "") for r in c.execute(f"SELECT {id_col}, {name_col} FROM {table}")}
        else:
            names[prefix] = {}

    nodes = {}
    links = []
    seen_links = set()

    def add_node(nid, label):
        if nid not in nodes:
            nodes[nid] = {"label": label, "file_type": "data", "source_file": "compact.sqlite3",
                          "source_location": "gamedata", "_origin": "gamedata", "id": nid,
                          "community": 0, "norm_label": label.lower()}

    def add_link(src, tgt, relation):
        key = (src, tgt, relation)
        if key in seen_links:
            return
        seen_links.add(key)
        links.append({"relation": relation, "confidence": "EXTRACTED", "source_file": "compact.sqlite3",
                      "source_location": "gamedata", "weight": 1.0, "source": src, "target": tgt,
                      "confidence_score": 1.0})

    # ---- entity nodes ----
    for table, prefix in entity_tables.items():
        cols = [r[1] for r in c.execute(f"PRAGMA table_info({table})")]
        if "id" not in cols:
            continue
        for row in c.execute(f"SELECT * FROM {table}"):
            values = dict(zip(cols, row))
            nid = f"{prefix}:{values['id']}"
            nm = names.get(prefix, {}).get(values["id"], "")
            label = f"{prefix.title().replace('_', ' ')} {values['id']} {str(nm)[:60]}".strip()
            add_node(nid, label)

    # ---- edge extraction: every table, every resolvable *_id column ----
    # longest-prefix match on the column name
    def resolve(col):
        best = None
        for cand, target in COLUMN_TARGETS:
            if col.endswith("_" + cand.split("_", 1)[1]) and cand in col:
                pass
        for cand, target in COLUMN_TARGETS:
            if col == cand or (col.endswith(cand)):
                if best is None or len(cand) > len(best[0]):
                    best = (cand, target)
        return best

    for table in sorted(existing):
        if table in entity_tables:
            continue
        cols = [r[1] for r in c.execute(f"PRAGMA table_info({table})")]
        id_col = "id" if "id" in cols else None
        if not id_col:
            continue
        refs = []
        for col in cols:
            if col == "id" or not col.endswith("_id"):
                continue
            resolved = resolve(col)
            if resolved and resolved[1] in id_sets:
                refs.append((col, resolved[1]))
        if not refs:
            continue
        for row in c.execute(f"SELECT {id_col}, {', '.join(r[0] for r in refs)} FROM {table}"):
            src = f"{table}:{row[0]}"
            if src not in nodes:
                add_node(src, f"{table.title().replace('_', ' ')} {row[0]}")
            for i, (col, target_prefix) in enumerate(refs):
                val = row[i + 1]
                if val is None:
                    continue
                # validate against the target id set (values can be cross-table)
                if val in id_sets[target_prefix]:
                    add_link(src, f"{target_prefix}:{val}", col[:-3] if col.endswith("_id") else col)

    graph = {
        "directed": True, "multigraph": False, "graph": {},
        "nodes": list(nodes.values()), "links": links,
        "hyperedges": [], "built_at_commit": "gamedata",
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump(graph, f)
    print(json.dumps({"nodes": len(nodes), "links": len(links), "out": OUT}))


if __name__ == "__main__":
    main()
