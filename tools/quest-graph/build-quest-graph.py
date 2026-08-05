#!/usr/bin/env python3
"""
Build a queryable knowledge graph of the quest DATA (compact.sqlite3) in
graphify's graph.json format -> graphify-out/quest-graph.json

Nodes: quests, components, acts, unit requirements, npcs, items, zones, milestones
Links: contains (quest->component->act), accepts/reports/guards (quest->npc),
       requires-item / rewards-item (quest->item), in-zone, in-milestone,
       precedes (chain ordering within a milestone), requires (component->unit_req),
       references (act->npc/item).

Query with:  graphify query "<question>" --graph graphify-out/quest-graph.json
             graphify path "Quest <A>" "Quest <B>" --graph graphify-out/quest-graph.json
Merge with the code graph: graphify merge-graphs graphify-out/graph.json graphify-out/quest-graph.json --out graphify-out/merged-graph.json
"""
import json
import os
import re
import sqlite3
import sys

DB = sys.argv[1] if len(sys.argv) > 1 else "/tmp/compact.sqlite3"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "graphify-out", "quest-graph.json")

# ---- act type -> (detail table, {jsonField: column}, refs) ----
# refs: 'npc' or 'item' links to emit from the act to the referenced entity.
ACT_TABLES = {
    "QuestActConAcceptNpc": ("quest_act_con_accept_npcs", {"npcId": "npc_id"}, "npc"),
    "QuestActConAcceptNpcKill": ("quest_act_con_accept_npc_kills", {"npcId": "npc_id"}, "npc"),
    "QuestActConAcceptDoodad": ("quest_act_con_accept_doodads", {"doodadId": "doodad_id"}, None),
    "QuestActConAcceptItem": ("quest_act_con_accept_items", {"itemId": "item_id"}, "item"),
    "QuestActConAcceptSphere": ("quest_act_con_accept_spheres", {"sphereId": "sphere_id"}, None),
    "QuestActConAcceptLevelUp": ("quest_act_con_accept_level_ups", {"level": "LEVEL"}, None),
    "QuestActConAcceptComponent": ("quest_act_con_accept_components", {}, None),
    "QuestActConAutoComplete": ("quest_act_con_auto_completes", {}, None),
    "QuestActConReportNpc": ("quest_act_con_report_npcs", {"npcId": "npc_id"}, "npc"),
    "QuestActConReportDoodad": ("quest_act_con_report_doodads", {"doodadId": "doodad_id"}, None),
    "QuestActConReportJournal": ("quest_act_con_report_journals", {}, None),
    "QuestActObjMonsterHunt": ("quest_act_obj_monster_hunts", {"npcId": "npc_id", "count": "count"}, "npc"),
    "QuestActObjMonsterGroupHunt": ("quest_act_obj_monster_group_hunts", {"monsterGroupId": "quest_monster_group_id", "count": "count"}, None),
    "QuestActObjItemGather": ("quest_act_obj_item_gathers", {"itemId": "item_id", "count": "count"}, "item"),
    "QuestActObjItemUse": ("quest_act_obj_item_uses", {"itemId": "item_id", "count": "count"}, "item"),
    "QuestActObjItemGroupGather": ("quest_act_obj_item_group_gathers", {"itemGroupId": "item_group_id", "count": "count"}, None),
    "QuestActObjItemGroupUse": ("quest_act_obj_item_group_uses", {"itemGroupId": "item_group_id", "count": "count"}, None),
    "QuestActObjTalk": ("quest_act_obj_talks", {"npcId": "npc_id"}, "npc"),
    "QuestActObjTalkNpcGroup": ("quest_act_obj_talk_npc_groups", {"npcGroupId": "npc_group_id"}, None),
    "QuestActObjInteraction": ("quest_act_obj_interactions", {"doodadId": "doodad_id", "count": "count"}, None),
    "QuestActObjSphere": ("quest_act_obj_spheres", {"sphereId": "sphere_id", "npcId": "npc_id"}, "npc"),
    "QuestActObjCraft": ("quest_act_obj_crafts", {"craftId": "craft_id", "count": "count"}, None),
    "QuestActObjLevel": ("quest_act_obj_levels", {"level": "LEVEL"}, None),
    "QuestActObjZoneMonsterHunt": ("quest_act_obj_zone_monster_hunts", {"zoneId": "zone_id", "count": "count"}, None),
    "QuestActObjExpressFire": ("quest_act_obj_express_fires", {"expressKeyId": "express_key_id", "npcGroupId": "npc_group_id", "count": "count"}, None),
    "QuestActCheckGuard": ("quest_act_check_guards", {"npcId": "npc_id"}, "npc"),
    "QuestActCheckSphere": ("quest_act_check_spheres", {"sphereId": "sphere_id"}, None),
    "QuestActCheckTimer": ("quest_act_check_timers", {"limitTime": "limit_time", "nextComponent": "next_component"}, None),
    "QuestActSupplyItem": ("quest_act_supply_items", {"itemId": "item_id", "gradeId": "grade_id", "count": "count"}, "item"),
    "QuestActSupplyCopper": ("quest_act_supply_coppers", {"amount": "amount"}, None),
    "QuestActSupplyExp": ("quest_act_supply_exps", {"exp": "exp"}, None),
    "QuestActSupplyJuryPoint": ("quest_act_supply_jury_points", {"point": "point"}, None),
    "QuestActSupplyAppellation": ("quest_act_supply_appellations", {"appellationId": "appellation_id"}, None),
    "QuestActSupplyRemoveItem": ("quest_act_supply_remove_items", {"itemId": "item_id", "count": "count"}, "item"),
    "QuestActSupplySelectiveItem": ("quest_act_supply_selective_items", {"itemId": "item_id", "gradeId": "grade_id", "count": "count"}, "item"),
}

# Parse UnitReqsKindType enum values from the source (robust vs hardcoding).
KIND_RE = re.compile(r"^\s*(\w+)\s*=\s*(\d+),?", re.M)
KIND_ORD = re.compile(r"^\s*(\w+),?", re.M)


def load_req_kinds(repo_root):
    path = os.path.join(repo_root, "AAEmu.Game", "Models", "Game", "Units", "UnitReqs.cs")
    try:
        text = open(path).read()
    except OSError:
        return {}
    m = re.search(r"enum UnitReqsKindType\s*\{(.*?)\}", text, re.S)
    if not m:
        return {}
    body = m.group(1)
    explicit = dict(KIND_RE.findall(body))
    if explicit:
        return {int(v): k for k, v in explicit.items()}
    return {i: k for i, k in enumerate(KIND_ORD.findall(body))}


def main():
    c = sqlite3.connect(DB)
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    req_kinds = load_req_kinds(repo_root)

    nodes = {}
    links = []

    def add_node(nid, label, loc, kind=None, extra=None):
        if nid in nodes:
            return
        nodes[nid] = {
            "label": label, "file_type": "data", "source_file": "compact.sqlite3",
            "source_location": loc, "_origin": "quest-data", "id": nid,
            "community": 0, "norm_label": label.lower(),
        }
        if kind:
            nodes[nid]["kind"] = kind
        if extra:
            nodes[nid].update(extra)

    def add_link(src, tgt, relation, weight=1.0, loc="quest_contexts"):
        links.append({"relation": relation, "confidence": "EXTRACTED",
                      "source_file": "compact.sqlite3", "source_location": loc,
                      "weight": weight, "source": src, "target": tgt, "confidence_score": 1.0})

    # ---- quests + components + acts ----
    quests = c.execute("SELECT id, name, category_id, REPEATABLE, LEVEL, selective, successive, chapter_idx, quest_idx, milestone_id, let_it_done, zone_id, score FROM quest_contexts").fetchall()
    act_rows = c.execute("""
        SELECT a.id, a.quest_component_id, a.act_detail_type, a.act_detail_id, cmp.quest_context_id
        FROM quest_acts a JOIN quest_components cmp ON a.quest_component_id = cmp.id""").fetchall()
    acts_by_comp = {}
    for aid, comp_id, atype, adetail, qid in act_rows:
        acts_by_comp.setdefault(comp_id, []).append((aid, atype, adetail, qid))

    zone_names = {}
    try:
        for zid, zname in c.execute("SELECT id, name FROM zone_templates"):
            zone_names[zid] = zname
    except Exception:
        pass

    for (qid, qname, cat, repeatable, level, selective, successive, chapter, qidx, milestone, let_it_done, zone, score) in quests:
        qid_n = f"quest:{qid}"
        add_node(qid_n, f"Quest {qid} {(qname or '').strip()[:60]}", "quest_contexts",
                 extra={"level": level, "zone": zone, "milestone": milestone,
                        "quest_idx": qidx, "chapter_idx": chapter,
                        "selective": bool(selective), "successive": bool(successive),
                        "score": score, "repeatable": bool(repeatable),
                        "let_it_done": bool(let_it_done), "category": cat})
        if zone:
            zid_n = f"zone:{zone}"
            add_node(zid_n, f"Zone {zone} {zone_names.get(zone, '')}", "zone_templates")
            add_link(qid_n, zid_n, "in-zone")
        if milestone:
            mid_n = f"milestone:{milestone}"
            add_node(mid_n, f"Milestone {milestone}", "quest_contexts")
            add_link(qid_n, mid_n, "in-milestone")

    for (qid, qname, *_) in quests:
        comps = c.execute("SELECT id, component_kind_id, or_unit_reqs FROM quest_components WHERE quest_context_id=? ORDER BY id", (qid,)).fetchall()
        qid_n = f"quest:{qid}"
        for cid, kind_id, or_reqs in comps:
            cid_n = f"component:{cid}"
            kind_name = {2: "Start", 3: "Supply", 4: "Progress", 5: "Fail", 6: "Ready", 7: "Drop", 8: "Reward"}.get(kind_id, f"kind{kind_id}")
            add_node(cid_n, f"Component {cid} {kind_name}", "quest_components")
            add_link(qid_n, cid_n, "contains", loc="quest_components")
            if or_reqs:
                nodes[cid_n]["or_unit_reqs"] = bool(or_reqs)

            for rid, rkind, rv1, rv2 in c.execute("SELECT id, kind_id, value1, value2 FROM unit_reqs WHERE owner_type='QuestComponent' AND owner_id=?", (cid,)).fetchall():
                rid_n = f"req:{rid}"
                kind_label = req_kinds.get(rkind, f"kind{rkind}")
                add_node(rid_n, f"Req {kind_label} v1={rv1} v2={rv2}", "unit_reqs")
                add_link(cid_n, rid_n, "requires", loc="unit_reqs")
                # kind 31 = quest-completion prerequisite (empirically: value1 is a
                # quest id whose completion gates this quest) - first-class edge.
                if rkind == 31 and rv1:
                    add_link(qid_n, f"quest:{rv1}", "requires-completion", loc="unit_reqs")
                # semantic links from the requirement to referenced entities
                if kind_label in ("Level", "Ability", "Race", "Gender", "TrainedSkill", "Combat", "Buff"):
                    continue
                if kind_label in ("EquipItem", "OwnItem", "NoBuff"):
                    iid_n = f"item:{rv1}"
                    add_node(iid_n, f"Item {rv1}", "unit_reqs")
                    add_link(rid_n, iid_n, "validates", loc="unit_reqs")

            for aid, atype, adetail, _ in acts_by_comp.get(cid, []):
                aid_n = f"act:{aid}"
                add_node(aid_n, f"Act {atype} {adetail}", "quest_acts")
                add_link(cid_n, aid_n, "contains", loc="quest_acts")
                entry = ACT_TABLES.get(atype)
                if not entry:
                    continue
                table, params_map, ref_kind = entry
                row = c.execute(f"SELECT * FROM {table} WHERE id=?", (adetail,)).fetchone()
                if row is None:
                    add_link(aid_n, qid_n, "broken-ref", loc="quest_acts")
                    continue
                cols = [r[1] for r in c.execute(f"PRAGMA table_info({table})").fetchall()]
                values = dict(zip(cols, row))
                if ref_kind == "npc":
                    nid = values.get("npc_id")
                    if nid:
                        nn = f"npc:{nid}"
                        add_node(nn, f"NPC {nid}", "quest_acts")
                        rel = {"QuestActConAcceptNpc": "accepts", "QuestActConAcceptNpcKill": "accepts-kill",
                               "QuestActConReportNpc": "reports-to", "QuestActObjMonsterHunt": "objective-npc",
                               "QuestActObjTalk": "talks-to", "QuestActCheckGuard": "guards",
                               "QuestActObjSphere": "sphere-npc"}.get(atype, "references")
                        add_link(qid_n if atype.startswith("QuestActConAccept") or atype.startswith("QuestActConReport") or atype == "QuestActCheckGuard" else aid_n, nn, rel, loc="quest_acts")
                elif ref_kind == "item":
                    iid = values.get("item_id")
                    if iid:
                        inn = f"item:{iid}"
                        add_node(inn, f"Item {iid}", "quest_acts")
                        rel = "rewards-item" if atype.startswith("QuestActSupply") else ("requires-item" if atype.startswith("QuestActObj") else "references")
                        add_link(qid_n, inn, rel, loc="quest_acts")

    # ---- chain edges: consecutive quest_idx within a milestone ----
    chains = {}
    for (qid, qname, cat, repeatable, level, selective, successive, chapter, qidx, milestone, let_it_done, zone, score) in quests:
        if milestone and qidx:
            chains.setdefault((milestone, chapter), []).append((qidx, qid, successive))
    for (milestone, chapter), lst in chains.items():
        lst.sort()
        for (qidx, qid, successive), (nqidx, nqid, _) in zip(lst, lst[1:]):
            add_link(f"quest:{qid}", f"quest:{nqid}", "precedes" if not successive else "precedes-successive", loc="quest_contexts")

    # ---- quest -> quest successive linkage (within same zone fallback) ----
    # (successive flag already captured in the precedes-successive edges above)

    graph = {
        "directed": True, "multigraph": False, "graph": {},
        "nodes": list(nodes.values()), "links": links,
        "hyperedges": [], "built_at_commit": "quest-data",
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump(graph, f)
    print(json.dumps({"nodes": len(nodes), "links": len(links), "out": OUT}))


if __name__ == "__main__":
    main()
