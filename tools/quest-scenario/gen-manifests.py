#!/usr/bin/env python3
"""
M1-5b manifest generator: prod compact.sqlite3 -> scenario manifests.

Reads the canonical 1.2 quest data and emits one JSON manifest per quest into
  AAEmu.UnitTests/Game/Quests/Scenario/Manifests/t1/ and .../t2/
(one file per quest; format documented in QuestScenarioManifest.cs header).

Tiers:
  T1 = all Solzreed golden-zone quests (zone_id IN (9, 124, 125)) - 97 quests
  T2 = 20-quest sample of the kill-accept family + live CheckGuard + live
       ItemGroup contexts

Quests whose shapes the harness cannot synthesize are emitted with a "skip"
block (broken refs, unsupported act types) - reported, never faked.
"""
import json
import os
import sqlite3
import sys

DB = sys.argv[1] if len(sys.argv) > 1 else "/tmp/compact.sqlite3"


def parse_bool(v):
    """SQLite 't'/'f' (or 1/0) -> bool. bool('f') is True in Python - never do that."""
    if isinstance(v, bool):
        return v
    if isinstance(v, int):
        return v != 0
    return str(v).strip().lower() in ("1", "t", "true", "y", "yes")
OUT_ROOT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..",
    "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests")

T1_ZONES = (9, 124, 125)
KILL_ACCEPT_SAMPLE_SIZE = 20

# ---- act type -> (detail table, param map {jsonField: column}) ----
ACT_TABLES = {
    "QuestActConAcceptNpc": ("quest_act_con_accept_npcs", {"npcId": "npc_id"}),
    "QuestActConAcceptNpcKill": ("quest_act_con_accept_npc_kills", {"npcId": "npc_id"}),
    "QuestActConAcceptDoodad": ("quest_act_con_accept_doodads", {"doodadId": "doodad_id"}),
    "QuestActConAcceptItem": ("quest_act_con_accept_items", {"itemId": "item_id"}),
    "QuestActConAcceptSphere": ("quest_act_con_accept_spheres", {"sphereId": "sphere_id"}),
    "QuestActConAcceptLevelUp": ("quest_act_con_accept_level_ups", {"level": "LEVEL"}),
    "QuestActConAcceptComponent": ("quest_act_con_accept_components", {"questContextId": "quest_context_id"}),
    "QuestActConAutoComplete": ("quest_act_con_auto_completes", {}),
    "QuestActConReportNpc": ("quest_act_con_report_npcs", {"npcId": "npc_id"}),
    "QuestActConReportDoodad": ("quest_act_con_report_doodads", {"doodadId": "doodad_id"}),
    "QuestActConReportJournal": ("quest_act_con_report_journals", {}),
    "QuestActObjMonsterHunt": ("quest_act_obj_monster_hunts", {"npcId": "npc_id", "count": "count"}),
    "QuestActObjMonsterGroupHunt": ("quest_act_obj_monster_group_hunts", {"monsterGroupId": "quest_monster_group_id", "count": "count"}),
    "QuestActObjItemGather": ("quest_act_obj_item_gathers", {"itemId": "item_id", "count": "count"}),
    "QuestActObjItemUse": ("quest_act_obj_item_uses", {"itemId": "item_id", "count": "count"}),
    "QuestActObjItemGroupGather": ("quest_act_obj_item_group_gathers", {"itemGroupId": "item_group_id", "count": "count"}),
    "QuestActObjItemGroupUse": ("quest_act_obj_item_group_uses", {"itemGroupId": "item_group_id", "count": "count"}),
    "QuestActObjTalk": ("quest_act_obj_talks", {"npcId": "npc_id"}),
    "QuestActObjTalkNpcGroup": ("quest_act_obj_talk_npc_groups", {"npcGroupId": "npc_group_id"}),
    "QuestActObjInteraction": ("quest_act_obj_interactions", {"doodadId": "doodad_id", "count": "count", "wiId": "wi_id", "phase": "phase"}),
    "QuestActObjSphere": ("quest_act_obj_spheres", {"sphereId": "sphere_id", "npcId": "npc_id"}),
    "QuestActObjCraft": ("quest_act_obj_crafts", {"craftId": "craft_id", "count": "count"}),
    "QuestActObjLevel": ("quest_act_obj_levels", {"level": "LEVEL"}),
    "QuestActObjZoneMonsterHunt": ("quest_act_obj_zone_monster_hunts", {"zoneId": "zone_id", "count": "count"}),
    "QuestActObjExpressFire": ("quest_act_obj_express_fires", {"expressKeyId": "express_key_id", "npcGroupId": "npc_group_id", "count": "count"}),
    "QuestActCheckGuard": ("quest_act_check_guards", {"npcId": "npc_id"}),
    "QuestActCheckSphere": ("quest_act_check_spheres", {"sphereId": "sphere_id"}),
    "QuestActCheckTimer": ("quest_act_check_timers", {"limitTime": "limit_time", "nextComponent": "next_component"}),
    "QuestActSupplyItem": ("quest_act_supply_items", {"itemId": "item_id", "gradeId": "grade_id", "count": "count"}),
    "QuestActSupplyCopper": ("quest_act_supply_coppers", {"amount": "amount"}),
    "QuestActSupplyExp": ("quest_act_supply_exps", {"exp": "exp"}),
    "QuestActSupplyJuryPoint": ("quest_act_supply_jury_points", {"point": "point"}),
    "QuestActSupplyAppellation": ("quest_act_supply_appellations", {"appellationId": "appellation_id"}),
    "QuestActSupplyRemoveItem": ("quest_act_supply_remove_items", {"itemId": "item_id", "count": "count"}),
    "QuestActSupplySelectiveItem": ("quest_act_supply_selective_items", {"itemId": "item_id", "gradeId": "grade_id", "count": "count"}),
}

# Act types that need no synthetic event but whose RunAct is drivable by quest state.
NO_EVENT_TYPES = {
    "QuestActConAcceptNpc", "QuestActConAcceptNpcKill", "QuestActConAcceptDoodad",
    "QuestActConAcceptItem", "QuestActConAcceptSphere", "QuestActConAcceptLevelUp",
    "QuestActConAcceptComponent", "QuestActConAutoComplete", "QuestActCheckGuard",
    "QuestActCheckSphere", "QuestActCheckTimer",
    "QuestActSupplyItem", "QuestActSupplyCopper", "QuestActSupplyExp",
    "QuestActSupplyJuryPoint", "QuestActSupplyAppellation", "QuestActSupplyRemoveItem",
    "QuestActSupplySelectiveItem",
}

# Act types -> synthetic event shape builder. Returns None when the shape is
# not synthesizable (quest must be SKIPPED with reason).
def event_shape(act_type, params, component_id, group_members):
    if act_type == "QuestActObjMonsterHunt":
        return {"type": "MonsterHunt", "npcId": params.get("npcId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjMonsterGroupHunt":
        return {"type": "MonsterGroupHunt", "npcId": params.get("monsterGroupId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjItemGather":
        return {"type": "ItemGather", "itemId": params.get("itemId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjItemUse":
        return {"type": "ItemUse", "itemId": params.get("itemId", 0)}
    if act_type == "QuestActObjItemGroupGather":
        members = group_members.get(params.get("itemGroupId", 0), [])
        if not members:
            return None
        return {"type": "ItemGroupGather", "itemId": members[0], "itemGroupId": params.get("itemGroupId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjItemGroupUse":
        return {"type": "ItemGroupUse", "itemGroupId": params.get("itemGroupId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjTalk":
        return {"type": "Talk", "npcId": params.get("npcId", 0)}
    if act_type == "QuestActObjTalkNpcGroup":
        return None  # npc-group member mapping not synthesizable -> skip quest
    if act_type == "QuestActObjInteraction":
        return {"type": "Interaction", "doodadId": params.get("doodadId", 0)}
    if act_type == "QuestActObjSphere":
        return {"type": "EnterSphere", "componentId": component_id}
    if act_type == "QuestActObjCraft":
        return {"type": "Craft", "craftId": params.get("craftId", 0)}
    if act_type == "QuestActObjLevel":
        return {"type": "LevelUp"}
    if act_type == "QuestActObjZoneMonsterHunt":
        return None  # zone->zone-group mapping unverified -> skip quest
    if act_type == "QuestActObjExpressFire":
        return None  # npc-group member mapping unverified -> skip quest
    if act_type == "QuestActConReportNpc":
        return {"type": "ReportNpc", "npcId": params.get("npcId", 0), "selected": 0}
    if act_type == "QuestActConReportDoodad":
        return {"type": "ReportDoodad", "doodadId": params.get("doodadId", 0), "selected": 0}
    if act_type == "QuestActConReportJournal":
        return {"type": "ReportJournal"}
    return None


def load_item_groups(c):
    groups = {}
    for row in c.execute("SELECT quest_item_group_id, item_id FROM quest_item_group_items").fetchall():
        groups.setdefault(row[0], []).append(row[1])
    return groups


def load_quest_acts(c, quest_id):
    """Returns {component_id: [act dicts]} for one quest, act dicts already materialized."""
    rows = c.execute("""
        SELECT a.id, a.quest_component_id, a.act_detail_type, a.act_detail_id
        FROM quest_acts a
        JOIN quest_components cmp ON a.quest_component_id = cmp.id
        WHERE cmp.quest_context_id = ?
        ORDER BY a.id
    """, (quest_id,)).fetchall()
    acts = {}
    for act_id, comp_id, act_type, detail_id in rows:
        if act_type not in ACT_TABLES:
            acts.setdefault(comp_id, []).append(
                {"actId": act_id, "type": act_type, "detailId": detail_id, "_unsupported": True})
            continue
        table, params_map = ACT_TABLES[act_type]
        try:
            row = c.execute(f"SELECT * FROM {table} WHERE id = ?", (detail_id,)).fetchone()
        except Exception:
            row = None
        if row is None:
            acts.setdefault(comp_id, []).append(
                {"actId": act_id, "type": act_type, "detailId": detail_id, "_brokenRef": True})
            continue
        cols = [r[1] for r in c.execute(f"PRAGMA table_info({table})").fetchall()]
        values = dict(zip(cols, row))
        act = {"actId": act_id, "type": act_type, "detailId": detail_id}
        for json_field, column in params_map.items():
            if column in values and values[column] is not None:
                act[json_field] = values[column]
        acts.setdefault(comp_id, []).append(act)
    return acts


def build_manifest(c, quest_id, family, item_groups):
    ctx = c.execute("SELECT id, name, category_id, LEVEL, zone_id, let_it_done, selective, score FROM quest_contexts WHERE id = ?",
                    (quest_id,)).fetchone()
    if ctx is None:
        return {"questId": quest_id, "name": "", "family": family,
                "skip": {"reason": "orphaned context (no quest_contexts row)"}}
    qid, name, category_id, level, zone_id, let_it_done, selective, score = ctx

    comps = c.execute("SELECT id, component_kind_id, next_component FROM quest_components WHERE quest_context_id = ? ORDER BY id",
                      (quest_id,)).fetchall()
    if not comps:
        return {"questId": qid, "name": name, "family": family, "skip": {"reason": "no components"}}
    acts = load_quest_acts(c, quest_id)

    comp_by_id = {cid: (kind, nxt) for cid, kind, nxt in comps}
    kind_names = {2: "Start", 3: "Supply", 4: "Progress", 5: "Fail", 6: "Ready", 7: "Drop", 8: "Reward"}
    kind_ids = {v: k for k, v in kind_names.items()}

    # ---- components with materialized acts ----
    components = []
    skip_reasons = []
    has_timer_or_fail = False
    guard_npc_id = None

    for cid, kind, nxt in comps:
        comp_acts = acts.get(cid, [])
        kind_name = kind_names.get(kind, "Unknown")
        for act in comp_acts:
            if act.get("_unsupported"):
                skip_reasons.append(f"unsupported act type {act['type']}")
            if act.get("_brokenRef"):
                skip_reasons.append(f"broken act ref {act['type']}#{act['detailId']}")
        if kind_name == "Fail":
            has_timer_or_fail = True
        if act_type_in(comp_acts, "QuestActCheckTimer"):
            has_timer_or_fail = True
        if kind_name == "Start":
            for act in comp_acts:
                if act["type"] == "QuestActCheckGuard":
                    guard_npc_id = act.get("npcId")

        # Drop-only act shapes: any act whose event shape is None and is not a
        # NO_EVENT type means this quest cannot be driven -> skip.
        for act in comp_acts:
            if act["type"] not in NO_EVENT_TYPES and act["type"] not in ACT_TABLES:
                continue
            if act["type"] in NO_EVENT_TYPES or act["type"] in ("QuestActObjSphere", "QuestActObjMonsterGroupHunt", "QuestActObjItemGroupUse", "QuestActConReportNpc", "QuestActConReportDoodad", "QuestActConReportJournal", "QuestActObjMonsterHunt", "QuestActObjItemGather", "QuestActObjItemUse", "QuestActObjTalk", "QuestActObjInteraction", "QuestActObjCraft", "QuestActObjLevel", "QuestActObjItemGroupGather"):
                continue
            if event_shape(act["type"], act, cid, item_groups) is None:
                skip_reasons.append(f"unsynthesizable event shape for {act['type']}")

        components.append({
            "kind": kind_name,
            "id": cid,
            "nextComponent": nxt,
            "acts": [{k: v for k, v in act.items() if not k.startswith("_")} for act in comp_acts],
        })

    if not any(comp["kind"] == "Start" for comp in components):
        skip_reasons.append("no Start component")

    # ---- acceptor from the Start component ----
    acceptor = {"type": "Npc", "id": 0}
    inventory = []
    for comp in components:
        if comp["kind"] != "Start":
            continue
        for act in comp["acts"]:
            t = act["type"]
            if t == "QuestActConAcceptNpc":
                acceptor = {"type": "Npc", "id": act.get("npcId", 0)}
            elif t == "QuestActConAcceptNpcKill":
                acceptor = {"type": "Kill", "id": act.get("npcId", 0)}
            elif t == "QuestActConAcceptDoodad":
                acceptor = {"type": "Doodad", "id": act.get("doodadId", 0)}
            elif t == "QuestActConAcceptItem":
                acceptor = {"type": "Item", "id": act.get("itemId", 0)}
                inventory.append({"itemId": act.get("itemId", 0), "count": 1})
            elif t == "QuestActConAcceptSphere":
                acceptor = {"type": "Sphere", "id": act.get("sphereId", 0)}
            break  # first accept act wins
        break

    # ---- stage plan: the engine walks KINDS (GoToNextStep) and the driver calls
    # RunCurrentStep once at accept + once per stage. Each call advances past the
    # current kind when its components pass, resting at the next present kind.
    # Auto-pass kinds (accept/supply/timer/auto-complete acts, empty comps) are
    # passed through by the START stage's second call.
    kind_order = ["Supply", "Progress", "Ready", "Reward"]
    AUTO_PASS_TYPES = {
        "QuestActConAcceptNpc", "QuestActConAcceptNpcKill", "QuestActConAcceptDoodad",
        "QuestActConAcceptItem", "QuestActConAcceptSphere", "QuestActConAcceptLevelUp",
        "QuestActConAcceptComponent", "QuestActConAutoComplete", "QuestActConReportJournal",
        "QuestActCheckTimer", "QuestActCheckGuard",
        "QuestActSupplyItem", "QuestActSupplyCopper", "QuestActSupplyExp",
        "QuestActSupplyJuryPoint", "QuestActSupplyAppellation", "QuestActSupplyRemoveItem",
        "QuestActSupplySelectiveItem",
    }

    # Acts that pass without events because the generator pre-stocks the inventory
    # (gather acts hydrate their objective from actual inventory contents).
    HYDRATED_TYPES = {"QuestActObjItemGather", "QuestActObjItemGroupGather"}

    present = [k for k in kind_order if any(comp["kind"] == k for comp in components)]

    events_by_kind = {}
    reward_items = []
    for comp in components:
        kind = comp["kind"]
        for act in comp["acts"]:
            if act.get("_unsupported") or act.get("_brokenRef"):
                continue
            if act["type"] in NO_EVENT_TYPES:
                continue
            shape = event_shape(act["type"], act, comp["id"], item_groups)
            if shape is not None:
                events_by_kind.setdefault(kind, []).append(shape)
            else:
                skip_reasons.append(f"unsynthesizable event shape for {act['type']} (comp {comp['id']})")
        if kind == "Reward":
            for act in comp["acts"]:
                if act["type"] == "QuestActSupplyItem" and not act.get("_unsupported"):
                    reward_items.append({"itemId": act["itemId"], "count": act.get("count", 1)})

    def kind_is_auto(kind_name):
        acts = [a for comp in components if comp["kind"] == kind_name for a in comp["acts"]]
        if not acts:
            return True  # empty components pass vacuously
        if selective:
            # Selective quests pass the Progress step when ANY active component passes
            return any(a["type"] in AUTO_PASS_TYPES or a["type"] in HYDRATED_TYPES for a in acts)
        return all(a["type"] in AUTO_PASS_TYPES or a["type"] in HYDRATED_TYPES for a in acts)

    def advance(pos):
        """One GoToNextStep walk: the first present kind after pos, or None = completed."""
        if pos is None:
            return None
        idx = kind_order.index(pos) if pos in kind_order else -1
        for nxt in kind_order[idx + 1:]:
            if nxt in present:
                return nxt
        return None

    STATUS_AT = {"Progress": "Progress", "Ready": "Ready", "Reward": "Completed"}

    def expect_for(pos):
        """Expectation dict for a resting position."""
        if pos is None:
            return {"completed": True}
        expect = {"step": pos}
        if pos in STATUS_AT:
            expect["status"] = STATUS_AT[pos]
        return expect

    stages = []
    if "Start" not in {comp["kind"] for comp in components}:
        skip_reasons.append("no Start component")
    else:
        # ---- START stage = 2 RunCurrentStep calls (accept + stage). ----
        # call 1 advances Start -> first present kind. call 2 advances further
        # ONLY if that kind's components pass without events (auto-pass).
        first = advance("Start")
        pos = advance(first) if (first is not None and kind_is_auto(first)) else first
        stages.append({"name": "START", "events": [], "expect": expect_for(pos)})

        # ---- one stage per present kind (Supply/Progress/Ready) ----
        for kind in kind_order:
            if kind not in present or kind == "Reward":
                continue
            if pos is None:
                # quest already completed - the stage's call cannot move it
                stages.append({"name": {"Supply": "SUPPLY", "Progress": "PROGRESS", "Ready": "READY"}[kind],
                               "events": events_by_kind.get(kind, []), "expect": {"completed": True}})
                continue
            if pos == kind:
                # resting at this stage's kind: events make its comps pass -> advance
                pos = advance(pos)
            elif kind_is_auto(pos):
                # resting ahead at an auto-pass kind (selective/hydrated advance) -> advance
                pos = advance(pos)
            # else: resting ahead at a non-auto kind - its events come later -> stays
            stages.append({"name": {"Supply": "SUPPLY", "Progress": "PROGRESS", "Ready": "READY"}[kind],
                           "events": events_by_kind.get(kind, []), "expect": expect_for(pos)})

        if "Reward" in present:
            expect = {"completed": True}
            if reward_items:
                expect["rewardItems"] = reward_items
            stages.append({"name": "REWARD", "events": [], "expect": expect})

    stages.append({"name": "PERSIST", "events": [], "expect": {"persistRoundTrip": True}})
    if has_timer_or_fail:
        for stage in stages:
            if stage["name"] == "PERSIST":
                stage["expect"]["failPathWired"] = True

    manifest = {
        "questId": qid,
        "name": (name or "").strip(),
        "zoneId": zone_id,
        "categoryId": category_id,
        "level": level,
        "letItDone": parse_bool(let_it_done),
        "selective": parse_bool(selective),
        "score": score,
        "family": family,
        "acceptor": acceptor,
        "template": {"level": level, "components": components},
        "stages": stages,
        "selectedRewardIndex": 1 if any(
            a["type"] == "QuestActSupplySelectiveItem" for comp in components for a in comp["acts"]) else 0,
    }
    if inventory:
        manifest["inventory"] = inventory
    # Stock gather-objective items: QuestActObjItemGather hydrates its objective
    # from the actual inventory (SetObjective(GetItemsCount)) - the harness must
    # pre-place the items for the quest to progress.
    for comp in components:
        for a in comp["acts"]:
            if a["type"] == "QuestActObjItemGather":
                inventory.append({"itemId": a.get("itemId", 0), "count": a.get("count", 1)})
            elif a["type"] == "QuestActObjItemGroupGather":
                gid = a.get("itemGroupId", 0)
                if gid in item_groups:
                    inventory.append({"itemId": item_groups[gid][0], "count": a.get("count", 1)})
    if inventory:
        manifest["inventory"] = inventory
    if guard_npc_id:
        manifest["guard"] = {"npcId": guard_npc_id, "alive": True}

    groups = {}
    for comp in components:
        for a in comp["acts"]:
            if a["type"] in ("QuestActObjItemGroupGather", "QuestActObjItemGroupUse"):
                gid = a.get("itemGroupId", 0)
                if gid and gid in item_groups:
                    groups.setdefault("itemGroups", {})[str(gid)] = item_groups[gid]
    if groups:
        manifest["groups"] = groups

    if skip_reasons:
        manifest["skip"] = {"reason": "; ".join(sorted(set(skip_reasons)))}

    return manifest


def act_type_in(comp_acts, act_type):
    return any(a["type"] == act_type for a in comp_acts)


def main():
    c = sqlite3.connect(DB)
    c.row_factory = sqlite3.Row
    item_groups = load_item_groups(c)

    # ---- T1: Solzreed golden zone ----
    t1_ids = [r[0] for r in c.execute(
        "SELECT id FROM quest_contexts WHERE zone_id IN (?,?,?) ORDER BY id", T1_ZONES).fetchall()]

    # ---- T2: kill-accept family (381), sample of 20 supported shapes ----
    family_ids = [r[0] for r in c.execute("""
        SELECT DISTINCT cmp.quest_context_id
        FROM quest_acts a
        JOIN quest_components cmp ON a.quest_component_id = cmp.id
        WHERE a.act_detail_type = 'QuestActConAcceptNpcKill'
        ORDER BY cmp.quest_context_id""").fetchall()]
    t2_ids = family_ids[:KILL_ACCEPT_SAMPLE_SIZE]
    t2_ids += [r[0] for r in c.execute(
        "SELECT quest_context_id FROM quest_components WHERE id IN "
        "(SELECT quest_component_id FROM quest_acts WHERE act_detail_type='QuestActCheckGuard') "
        "AND quest_context_id IN (1033,1313,1897,3656,745,1421)").fetchall()]
    t2_ids += [r[0] for r in c.execute(
        "SELECT quest_context_id FROM quest_components WHERE id IN "
        "(SELECT quest_component_id FROM quest_acts WHERE act_detail_type IN "
        "('QuestActObjItemGroupGather','QuestActObjItemGroupUse')) "
        "AND quest_context_id IN (5489,5490,6578,6600,6615,1955,1957,1958,2140)").fetchall()]
    t2_ids = sorted(set(t2_ids))

    counts = {"t1": 0, "t2": 0}
    for tier, ids in (("t1", t1_ids), ("t2", t2_ids)):
        out_dir = os.path.join(OUT_ROOT, tier)
        os.makedirs(out_dir, exist_ok=True)
        family = "golden-zone" if tier == "t1" else "mixed-families"
        for qid in ids:
            manifest = build_manifest(c, qid, family, item_groups)
            if manifest is None:
                continue
            with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
                json.dump(manifest, f, ensure_ascii=False, indent=1)
            counts[tier] += 1

    print(json.dumps({"generated": counts, "out": OUT_ROOT,
                      "t1_total": len(t1_ids), "t2_total": len(t2_ids)}, indent=1))


if __name__ == "__main__":
    main()
