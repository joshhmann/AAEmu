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
  T3 = M1-5c stratified act-family census: greedy quota fill across the
       act_detail_type distribution (every family present in prod data gets
       >=2 example quests where available, more for the common families:
       ConReportNpc, AcceptNpc, SupplyItem, ObjItemGather, ObjTalk,
       ObjMonsterHunt). Quests already sampled in T1/T2 are excluded first so
       T3 widens coverage instead of repeating it. The M1-5c selection was
       FROZEN (T3_PINNED_QUESTS) on 2026-08-06: the greedy tie-break reads
       ACT_TABLES, so growing ACT_TABLES (M2a closures) would reshuffle the
       sample and churn the census - new act families get their own tiers
       (T4+) instead of re-sampling T3.
  T4 = M2a wave-1: band 1-20 quests carrying any of the four closed act
       families (QuestActObjCinema, QuestActEtcItemObtain,
       QuestActConAcceptItemGain, QuestActSupplyLp), minus dropped content,
       minus quests already sampled in T1/T2/T3 (each quest driven once).
  T5 = M2a wave-2: band 1-20 quests carrying any of the four wave-2 closed
       act families (QuestActObjExpressFire, QuestActObjAggro,
       QuestActCheckCompleteComponent, QuestActSupplyHonorPoint), minus
       already-sampled.
  T6 = M2a census: FULL band 1-10 sweep - every non-dropped quest with
       LEVEL 1-10, minus quests already sampled in T1-T5 (each quest driven
       exactly once across the census). Family = primary act family.
  T7 = M2a census: FULL band 11-20 sweep - every non-dropped quest with
       LEVEL 11-20, minus already-sampled (same rule).
  T8 = M2c census: FULL band 21-30 sweep - every non-dropped quest with
       LEVEL 21-30, minus already-sampled (same rule).

Also emits Manifests/census-meta.json (band denominators incl. dropped
ids per band + signature-zone map) so the tier test can render the M2a
band-census acceptance table and zone-coverage rows deterministically.

Quests whose shapes the harness cannot synthesize are emitted with a "skip"
block (broken refs, unsupported act types) - reported, never faked.
"""
import json
import math
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
    "QuestActObjZoneKill": ("quest_act_obj_zone_kills", {
        "zoneId": "zone_id",
        "countNpc": "count_npc",
        "countPk": "count_pk",
        "teamShare": "team_share",
        "useAlias": "use_alias",
        "questActObjAliasId": "quest_act_obj_alias_id",
        "lvMin": "lv_min",
        "lvMax": "lv_max",
        "isParty": "is_party",
        "lvMinNpc": "lv_min_npc",
        "lvMaxNpc": "lv_max_npc",
        "pcFactionId": "pc_faction_id",
        "pcFactionExclusive": "pc_faction_exclusive",
        "npcFactionId": "npc_faction_id",
        "npcFactionExclusive": "npc_faction_exclusive",
    }),
    "QuestActObjExpressFire": ("quest_act_obj_express_fires", {"expressKeyId": "express_key_id", "npcGroupId": "npc_group_id", "count": "count"}),
    "QuestActObjAggro": ("quest_act_obj_aggros", {"range": "RANGE", "rank1": "rank1", "rank2": "rank2", "rank3": "rank3", "rank1Ratio": "rank1_ratio", "rank2Ratio": "rank2_ratio", "rank3Ratio": "rank3_ratio", "rank1Item": "rank1_item", "rank2Item": "rank2_item", "rank3Item": "rank3_item", "useAlias": "use_alias", "questActObjAliasId": "quest_act_obj_alias_id"}),
    "QuestActCheckCompleteComponent": ("quest_act_check_complete_components", {"completeComponent": "complete_component"}),
    "QuestActSupplyHonorPoint": ("quest_act_supply_honor_points", {"point": "point"}),
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
    # ---- M2a wave-1 closures (2026-08-06) ----
    "QuestActObjCinema": ("quest_act_obj_cinemas", {"cinemaId": "cinema_id"}),
    "QuestActEtcItemObtain": ("quest_act_etc_item_obtains", {"itemId": "item_id", "count": "count"}),
    "QuestActConAcceptItemGain": ("quest_act_con_accept_item_gains", {"itemId": "item_id", "count": "count"}),
    "QuestActSupplyLp": ("quest_act_supply_lps", {"laborPower": "lp"}),
    # M2c wave-3 (t_64d13ee4): TRIVIAL supply act the expedition dailies carry
    # at Reward (5923/5924 LivingPoint; HonorPoint was already closed in
    # wave-2) - without this entry the dailies stay SKIP after the ZoneKill
    # closure.
    "QuestActSupplyLivingPoint": ("quest_act_supply_living_points", {"point": "point"}),
    # M2 WI-2 (t_f42b9ae3): CrimePoint supply act (7 live carriers:
    # 2916/2926/2935/2936/5197/5198/5494). Same shape as the JuryPoint/LivingPoint
    # closures - point-only detail table, RunAct->true.
    "QuestActSupplyCrimePoint": ("quest_act_supply_crime_points", {"point": "point"}),
    # M2 WI-3 (t_d5e802f5): AbilityLevel objective act (11 live carriers:
    # 5967 all-abilities + 6069/6070/6075-6082 single-ability, all level 50).
    # State-check objective: RunAct reads owner.Abilities exp (no event
    # subscription) - the driver's AbilityLevel event presees the exp.
    "QuestActObjAbilityLevel": ("quest_act_obj_ability_levels", {
        "abilityId": "ability_id",
        "level": "LEVEL",
        "useAlias": "use_alias",
        "questActObjAliasId": "quest_act_obj_alias_id",
    }),
    # M2 WI-4 (t_fe93e2d8): MateLevel objective act (7 carriers: 5430/5464/
    # 5465/5466/5812/5813 live + 6015 orphaned context, all level 50,
    # cleanup='t'). State-check objective: RunAct -> CalculateObjective scans
    # the owner's inventory for a SummonMate item with the act's ItemId whose
    # DetailLevel >= Level (QuestActObjMateLevel.cs:22-58) - the driver's
    # MateLevel event presees the summoned mate (cleanup acts consume it when
    # the objective is met).
    "QuestActObjMateLevel": ("quest_act_obj_mate_levels", {
        "itemId": "item_id",
        "level": "LEVEL",
        "cleanup": "cleanup",
        "useAlias": "use_alias",
        "questActObjAliasId": "quest_act_obj_alias_id",
    }),
    # M2 WI-5 (t_d6516324): CompleteQuest objective act (11 live carriers:
    # 5814-5821/5862/5868/5911, all level 50). State-check objective: RunAct
    # checks quest.Owner.Quests.HasQuestCompleted(QuestId)
    # (QuestActObjCompleteQuest.cs:26) - the driver's CompleteQuest event
    # pre-marks the referenced quest as completed (SetCompletedQuestFlag,
    # synthetic-block pattern) so the state check counts at Progress.
    "QuestActObjCompleteQuest": ("quest_act_obj_complete_quests", {
        "questId": "quest_id",
        "acceptWith": "accept_with",
        "useAlias": "use_alias",
        "questActObjAliasId": "quest_act_obj_alias_id",
    }),
}

# Act types that need no synthetic event but whose RunAct is drivable by quest state.
NO_EVENT_TYPES = {
    "QuestActConAcceptNpc", "QuestActConAcceptNpcKill", "QuestActConAcceptDoodad",
    "QuestActConAcceptItem", "QuestActConAcceptSphere", "QuestActConAcceptLevelUp",
    "QuestActConAcceptComponent", "QuestActConAutoComplete", "QuestActCheckGuard",
    "QuestActCheckSphere", "QuestActCheckTimer",
    "QuestActCheckCompleteComponent",
    "QuestActSupplyItem", "QuestActSupplyCopper", "QuestActSupplyExp",
    "QuestActSupplyJuryPoint", "QuestActSupplyAppellation", "QuestActSupplyRemoveItem",
    "QuestActSupplySelectiveItem",
    # M2a wave-1: pass-through / state-check acts with no synthetic event.
    "QuestActEtcItemObtain", "QuestActConAcceptItemGain", "QuestActSupplyLp",
    "QuestActSupplyHonorPoint",
    # M2c wave-3: supply acts RunAct->true (ChangeGamePoints), zero-wired domain.
    "QuestActSupplyLivingPoint",
    # M2 WI-2 (t_f42b9ae3): CrimePoint supply act - RunAct->true (AddCrime),
    # no synthetic event (mirrors JuryPoint/LivingPoint).
    "QuestActSupplyCrimePoint",
}

# Act types -> synthetic event shape builder. Returns None when the shape is
# not synthesizable (quest must be SKIPPED with reason).
def event_shape(act_type, params, component_id, group_members, npc_groups=None, acceptor_npc_id=0):
    if act_type == "QuestActObjMonsterHunt":
        return {"type": "MonsterHunt", "npcId": params.get("npcId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjMonsterGroupHunt":
        return {"type": "MonsterGroupHunt", "npcId": params.get("monsterGroupId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjItemGather":
        return {"type": "ItemGather", "itemId": params.get("itemId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjItemUse":
        # RC-4: QuestActObjItemUse.OnItemUse credits +1 per event and ignores any
        # count (QuestActObjItemUse.cs:46; OnItemUseArgs carries only ItemId) -
        # the driver must fire the event 'count' times, so carry the count.
        return {"type": "ItemUse", "itemId": params.get("itemId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjItemGroupGather":
        members = group_members.get(params.get("itemGroupId", 0), [])
        if not members:
            return None
        return {"type": "ItemGroupGather", "itemId": members[0], "itemGroupId": params.get("itemGroupId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjItemGroupUse":
        # RC-4: QuestActObjItemGroupUse subscribes OnItemUse (not OnItemGroupUse -
        # UnusedActs/QuestActObjItemGroupUse.cs:39) and credits +1 per use of any
        # group member (CheckGroupItem). The driver's ItemGroupUse event has no
        # subscriber; emit ItemUse with a group member itemId instead.
        members = group_members.get(params.get("itemGroupId", 0), [])
        if not members:
            return None
        return {"type": "ItemUse", "itemId": members[0], "itemGroupId": params.get("itemGroupId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjTalk":
        return {"type": "Talk", "npcId": params.get("npcId", 0)}
    if act_type == "QuestActObjTalkNpcGroup":
        # M2a wave-2 (t_41a14bab): npc-group member mapping now synthesizable -
        # the driver seeds QuestManager._groupNpcs from manifest groups
        # (quest_monster_npcs read path) and fires OnTalkNpcGroupMade.
        members = (npc_groups or {}).get(params.get("npcGroupId", 0), [])
        if not members:
            return None
        return {"type": "TalkNpcGroup", "npcGroupId": params.get("npcGroupId", 0), "npcId": members[0], "componentId": component_id}
    if act_type == "QuestActObjInteraction":
        # RC-4 pattern: OnInteraction credits +1 per event (AddObjective(1),
        # Interaction.cs:54) and RunAct requires currentObjectiveCount >= Count
        # (Interaction.cs:30) - the driver must fire the event 'count' times.
        return {"type": "Interaction", "doodadId": params.get("doodadId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjSphere":
        return {"type": "EnterSphere", "componentId": component_id}
    if act_type == "QuestActObjCraft":
        # RC-4 pattern (band-sweep finding, 2026-08-06): QuestActObjCraft.OnCraft
        # credits +1 per event (Craft.cs:47) and RunAct requires
        # currentObjectiveCount >= Count - the driver fires the event 'count'
        # times, so carry the count on the shape.
        return {"type": "Craft", "craftId": params.get("craftId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjLevel":
        return {"type": "LevelUp"}
    if act_type == "QuestActObjAbilityLevel":
        # M2 WI-3 (t_d5e802f5): state-check objective - RunAct reads ability
        # exp (no event subscription). The event's job is the RIG: the driver
        # presees ability exp so the state check counts at the Progress
        # stage. abilityId 0 = the all-abilities branch (every ability
        # 1..10 must meet the level; the driver saturates all of them).
        return {"type": "AbilityLevel", "abilityId": params.get("abilityId", 0), "level": params.get("level", 0)}
    if act_type == "QuestActObjMateLevel":
        # M2 WI-4 (t_fe93e2d8): state-check objective - RunAct ->
        # CalculateObjective scans the owner's inventory for a SummonMate
        # item with the act's ItemId whose DetailLevel >= Level. The event's
        # job is the RIG: the driver presees the summoned mate so the state
        # check counts at the Progress stage (cleanup acts consume it when
        # the objective is met).
        return {"type": "MateLevel", "itemId": params.get("itemId", 0), "level": params.get("level", 0)}
    if act_type == "QuestActObjCompleteQuest":
        # M2 WI-5 (t_d6516324): state-check objective - RunAct checks
        # HasQuestCompleted(QuestId) (QuestActObjCompleteQuest.cs:26). The
        # event's job is the RIG: the driver pre-marks the referenced quest
        # as completed (SetCompletedQuestFlag, synthetic-block pattern) so
        # the state check counts at the Progress stage. AcceptWith is
        # unused by the engine today (TODO in the act) - the questId alone
        # drives the objective.
        return {"type": "CompleteQuest", "questId": params.get("questId", 0)}
    if act_type == "QuestActObjCinema":
        # M2a wave-1: two-event drive. QuestActObjCinema.OnCinemaStarted sets
        # player.CurrentlyPlayingCinemaId = CinemaId; OnCinemaEnded credits the
        # objective only when CurrentlyPlayingCinemaId == CinemaId
        # (QuestActObjCinema.cs:48-78). Return BOTH events in order so the
        # stage fires started -> ended.
        return [
            {"type": "CinemaStarted", "cinemaId": params.get("cinemaId", 0)},
            {"type": "CinemaEnded", "cinemaId": params.get("cinemaId", 0)},
        ]
    if act_type == "QuestActObjZoneKill":
        # M2c wave-3 (t_64d13ee4): the act's OnZoneKill only credits when
        # victim != killer AND the victim satisfies the faction/level filters
        # (QuestActObjZoneKill.cs:70-96). FireEvent must deliver a NON-OWNER
        # victim built to satisfy the act's filter - carry the filter params
        # so the driver can construct it. count = max(NPC, PK) quota.
        # Engine state: faction-0 no-filter + 0..0 level bounds = "any level"
        # was fixed in t_497b51d8 (accepted; rides on this branch since the
        # base predates its merge). ZoneId itself is STILL unenforced (§2.4
        # watch item) - the zoneGroupId is syntactically valid but unchecked.
        return {
            "type": "ZoneKill",
            "zoneGroupId": params.get("zoneId", 0),
            "count": max(params.get("countNpc", 0), params.get("countPk", 0)) or 1,
            "countNpc": params.get("countNpc", 0),
            "countPk": params.get("countPk", 0),
            "npcFactionId": params.get("npcFactionId", 0),
            "npcFactionExclusive": parse_bool(params.get("npcFactionExclusive", "f")),
            "lvMinNpc": params.get("lvMinNpc", 0),
            "lvMaxNpc": params.get("lvMaxNpc", 0),
            "pcFactionId": params.get("pcFactionId", 0),
            "pcFactionExclusive": parse_bool(params.get("pcFactionExclusive", "f")),
            "lvMin": params.get("lvMin", 0),
            "lvMax": params.get("lvMax", 0),
        }
    if act_type == "QuestActObjExpressFire":
        # M2a wave-2 (t_41a14bab): ExpressFire credits when the owner expresses
        # an emotion at a member of the act's npc group. Fire the group's first
        # member with the act's express key (emotion id), 'count' times.
        members = (npc_groups or {}).get(params.get("npcGroupId", 0), [])
        if not members:
            return None
        return {"type": "ExpressFire", "npcId": members[0], "emotionId": params.get("expressKeyId", 0), "count": params.get("count", 1)}
    if act_type == "QuestActObjAggro":
        # M2a wave-2 (t_41a14bab): the act credits OnKill when the killed npc's
        # template id equals the quest acceptor npc id AND the owner holds aggro
        # on it (rating 0 = most aggro -> rank 1). Requires an Npc acceptor.
        if not acceptor_npc_id:
            return None
        return {"type": "Aggro", "npcId": acceptor_npc_id}
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


def load_npc_groups(c):
    """quest_monster_npcs -> {quest_monster_group_id: [npc_ids]}. The engine's
    QuestManager._groupNpcs is populated from this table (LoadQuestMonsterNpcs),
    so ExpressFire's CheckGroupNpc / TalkNpcGroup membership resolve against it."""
    groups = {}
    for row in c.execute("SELECT quest_monster_group_id, npc_id FROM quest_monster_npcs").fetchall():
        groups.setdefault(row[0], []).append(row[1])
    return groups


# Act types whose detail-table NUM columns hold 't'/'f' text (never bool() them).
BOOL_COLUMNS = {
    "QuestActObjAggro": {"rank1Item", "rank2Item", "rank3Item", "useAlias"},
    "QuestActObjAbilityLevel": {"useAlias"},
    "QuestActObjMateLevel": {"cleanup", "useAlias"},
    # M2 WI-5 (t_d6516324): acceptWith/useAlias are 't'/'f' text.
    "QuestActObjCompleteQuest": {"acceptWith", "useAlias"},
}


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
        bool_fields = BOOL_COLUMNS.get(act_type, set())
        for json_field, column in params_map.items():
            if column in values and values[column] is not None:
                act[json_field] = parse_bool(values[column]) if json_field in bool_fields else values[column]
        acts.setdefault(comp_id, []).append(act)
    return acts


def build_manifest(c, quest_id, family, item_groups, npc_groups=None):
    ctx = c.execute("SELECT id, name, category_id, LEVEL, zone_id, let_it_done, selective, score FROM quest_contexts WHERE id = ?",
                    (quest_id,)).fetchone()
    if ctx is None:
        return {"questId": quest_id, "name": "", "family": family,
                "skip": {"reason": "orphaned context (no quest_contexts row)"}}
    qid, name, category_id, level, zone_id, let_it_done, selective, score = ctx
    # Stage-model v4 (RC-1): the raw sqlite cells are 't'/'f' strings - bool('f')
    # is True in Python. Parse once here so EVERY use (kind_is_auto included) sees
    # the real value; the manifest fields were already parse_bool'd (14c78c94).
    let_it_done = parse_bool(let_it_done)
    selective = parse_bool(selective)
    # WI-8 (t_fc85a317): score can be NULL in sqlite (quest 3806 is the only
    # one in the corpus) - the engine reads GetInt32("score", 0)
    # (QuestManager.cs:578), so NULL == 0. Never pass None downstream.
    if score is None:
        score = 0

    comps = c.execute("SELECT id, component_kind_id, next_component FROM quest_components WHERE quest_context_id = ? ORDER BY id",
                      (quest_id,)).fetchall()
    if not comps:
        # Act-less shell: still a band quest - carry level + zoneId so the
        # band-census rollup and zone-coverage rows count the SKIP honestly.
        return {"questId": qid, "name": name, "family": family,
                "zoneId": zone_id, "template": {"level": level},
                "skip": {"reason": "no components"}}
    acts = load_quest_acts(c, quest_id)

    # ---- acceptor from the Start component ----
    # Computed BEFORE the component loop so the skip-check / event shapes can
    # resolve the Aggro event's target npc (M2a wave-2). ConAcceptComponent
    # (self-start) quests accept via engage-combat: the npc whose
    # engage_combat_give_quest_id == this quest id is the real acceptor.
    acceptor = {"type": "Npc", "id": 0}
    inventory = []
    for cid, kind, _nxt in comps:
        if kind != 2:  # Start
            continue
        for act in acts.get(cid, []):
            t = act["type"]
            if t == "QuestActConAcceptNpc":
                acceptor = {"type": "Npc", "id": act.get("npcId", 0)}
                break
            elif t == "QuestActConAcceptNpcKill":
                acceptor = {"type": "Kill", "id": act.get("npcId", 0)}
                break
            elif t == "QuestActConAcceptDoodad":
                acceptor = {"type": "Doodad", "id": act.get("doodadId", 0)}
                break
            elif t == "QuestActConAcceptItem":
                acceptor = {"type": "Item", "id": act.get("itemId", 0)}
                inventory.append({"itemId": act.get("itemId", 0), "count": 1})
                break
            elif t == "QuestActConAcceptItemGain":
                # M2a wave-1: mirrors ConAcceptItem (acceptor Item + inventory
                # preseed) but with the act's own Count - CAIG.RunAct checks
                # CheckItems(ItemId, Count) (QuestActConAcceptItemGain.cs:24).
                acceptor = {"type": "Item", "id": act.get("itemId", 0)}
                inventory.append({"itemId": act.get("itemId", 0), "count": act.get("count", 1)})
                break
            elif t == "QuestActConAcceptSphere":
                acceptor = {"type": "Sphere", "id": act.get("sphereId", 0)}
                break
            elif t == "QuestActConAcceptComponent":
                row = c.execute("SELECT id FROM npcs WHERE engage_combat_give_quest_id = ? LIMIT 1",
                                (quest_id,)).fetchone()
                if row:
                    acceptor = {"type": "Npc", "id": row[0]}
                    break
                # no engage-combat npc: self-start alone is not a usable acceptor
                # (cat-34 dailies carry ConAcceptComponent BEFORE CAIG - keep
                # scanning so the CAIG branch can claim the Item acceptor)
            break  # first accept act wins
        break
    acceptor_npc_id = acceptor["id"] if acceptor["type"] == "Npc" else 0

    comp_by_id = {cid: (kind, nxt) for cid, kind, nxt in comps}
    # kind_id 1 = None: the engine's legacy task-board step (NewQuestCode.cs
    # GoToNextStep walks Start -> None -> Supply; the None step runs its
    # components like any other and auto-passes when they pass). Only 5 quests
    # corpus-wide carry a None component (275/281/305/371/604, band 21-30).
    kind_names = {1: "None", 2: "Start", 3: "Supply", 4: "Progress", 5: "Fail",
                  6: "Ready", 7: "Drop", 8: "Reward"}
    kind_ids = {v: k for k, v in kind_names.items()}

    # ---- components with materialized acts ----
    components = []
    skip_reasons = []
    has_timer_or_fail = False
    guard_npc_ids = []  # RC-3: from ALL components' QuestActCheckGuard acts, deduped

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
        # RC-3: a QuestActCheckGuard in ANY component needs its NPC spawned -
        # QuestActCheckGuard.RunAct returns false when the guard is unresolvable
        # (CheckGuard.cs:26-33), so a guard in a non-Start component could never
        # pass. Collect every distinct guard npc id (first-seen order).
        for act in comp_acts:
            if act["type"] == "QuestActCheckGuard":
                npc_id = act.get("npcId")
                if npc_id and npc_id not in guard_npc_ids:
                    guard_npc_ids.append(npc_id)

        # Drop-only act shapes: any act whose event shape is None and is not a
        # NO_EVENT type means this quest cannot be driven -> skip.
        for act in comp_acts:
            if act["type"] not in NO_EVENT_TYPES and act["type"] not in ACT_TABLES:
                continue
            if act["type"] in NO_EVENT_TYPES or act["type"] in ("QuestActObjSphere", "QuestActObjMonsterGroupHunt", "QuestActObjItemGroupUse", "QuestActConReportNpc", "QuestActConReportDoodad", "QuestActConReportJournal", "QuestActObjMonsterHunt", "QuestActObjItemGather", "QuestActObjItemUse", "QuestActObjTalk", "QuestActObjInteraction", "QuestActObjCraft", "QuestActObjLevel", "QuestActObjAbilityLevel", "QuestActObjMateLevel", "QuestActObjCompleteQuest", "QuestActObjItemGroupGather"):
                continue
            if event_shape(act["type"], act, cid, item_groups, npc_groups, acceptor_npc_id) is None:
                skip_reasons.append(f"unsynthesizable event shape for {act['type']}")

        components.append({
            "kind": kind_name,
            "id": cid,
            "nextComponent": nxt,
            "acts": [{k: v for k, v in act.items() if not k.startswith("_")} for act in comp_acts],
        })

    if not any(comp["kind"] == "Start" for comp in components):
        skip_reasons.append("no Start component")

    # ---- stage plan: the engine walks KINDS (GoToNextStep) and the driver calls
    # RunCurrentStep once at accept + once per stage. Each call advances past the
    # current kind when its components pass, resting at the next present kind.
    # kind_order mirrors the engine's GoToNextStep chain: Start -> None -> Supply
    # -> Progress -> Ready -> Reward (None = legacy task-board step, kind_id 1).
    # Stage kinds (Supply/Progress/Ready) get their own manifest stage; None is
    # consumed by the START stage's auto-pass walk (its components are supply-
    # shaped and pass without events in every real carrier).
    kind_order = ["None", "Supply", "Progress", "Ready", "Reward"]
    STAGE_KINDS = ["Supply", "Progress", "Ready"]
    AUTO_PASS_TYPES = {
        "QuestActConAcceptNpc", "QuestActConAcceptNpcKill", "QuestActConAcceptDoodad",
        "QuestActConAcceptItem", "QuestActConAcceptSphere", "QuestActConAcceptLevelUp",
        "QuestActConAcceptComponent", "QuestActConAutoComplete", "QuestActConReportJournal",
        "QuestActCheckTimer", "QuestActCheckGuard",
        "QuestActSupplyItem", "QuestActSupplyCopper", "QuestActSupplyExp",
        "QuestActSupplyJuryPoint", "QuestActSupplyAppellation", "QuestActSupplyRemoveItem",
        "QuestActSupplySelectiveItem",
        # M2a wave-1: pass-through / state-check acts (RunAct->true once the
        # rig satisfies the condition; cinema needs events and is NOT here).
        "QuestActEtcItemObtain", "QuestActConAcceptItemGain", "QuestActSupplyLp",
        "QuestActSupplyHonorPoint",
        # M2 WI-2 (t_f42b9ae3): CrimePoint supply act - auto-pass like the other
        # point-supply acts.
        "QuestActSupplyCrimePoint",
    }

    # Act types that pass without events because the generator pre-stocks the inventory
    # (gather acts hydrate their objective from actual inventory contents).
    HYDRATED_TYPES = {"QuestActObjItemGather", "QuestActObjItemGroupGather"}

    # Acts the engine counts as objectives (CountsAsAnObjective => true in the act
    # classes; CheckGuard/CheckTimer/CheckSphere/report acts do NOT count). Used for
    # the let-it-done status model (mirrors Quest.GetQuestObjectiveStatus).
    OBJECTIVE_TYPES = {
        "QuestActObjMonsterHunt", "QuestActObjMonsterGroupHunt", "QuestActObjItemGather",
        "QuestActObjItemUse", "QuestActObjItemGroupGather", "QuestActObjItemGroupUse",
        "QuestActObjTalk", "QuestActObjTalkNpcGroup", "QuestActObjInteraction",
        "QuestActObjSphere", "QuestActObjCraft", "QuestActObjLevel",
        "QuestActObjZoneKill", "QuestActObjExpressFire",
        # M2a wave-1: QuestActObjCinema overrides CountsAsAnObjective => true.
        "QuestActObjCinema", "QuestActObjAggro",
        # M2 WI-3 (t_d5e802f5): ability-level objective - CountsAsAnObjective
        # => true (QuestActObjAbilityLevel.cs:9); credited via the rig preseed.
        "QuestActObjAbilityLevel",
        # M2 WI-4 (t_fe93e2d8): mate-level objective - CountsAsAnObjective
        # => true (QuestActObjMateLevel.cs:10); credited via the rig preseed
        # (SummonMate in inventory at DetailLevel >= Level).
        "QuestActObjMateLevel",
        # M2 WI-5 (t_d6516324): complete-quest objective - CountsAsAnObjective
        # => true (QuestActObjCompleteQuest.cs:7); credited via the rig preseed
        # (referenced quest pre-marked completed).
        "QuestActObjCompleteQuest",
    }

    present = [k for k in kind_order if any(comp["kind"] == k for comp in components)]

    # WI-8 (t_fc85a317): score-quest event scaling. The engine's score branch
    # (QuestStep.RunComponents, Score>0) passes when score >= Template.Score,
    # score = Σ Count×Objective over objective acts. MaxObjective() =
    # Score/Count+1 (ltd: ceil(×1.5)) shows the data intends objectives to
    # EXCEED the displayed count (each kill is worth Count score points).
    # Fire the first event-credited objective act enough times to close the
    # deficit, so the engine can actually reach the score target instead of
    # stalling at Progress (7 band-41-50 quests: 3076/3089/3625/4343/5062/
    # 5063/5064 - e.g. 3076 score=100 with 5×5+3×3=34 credited).
    scaled_events = {}
    if score > 0:
        s_now = sum((a.get("count", 1) ** 2) for comp in components
                    if comp["kind"] == "Progress"
                    for a in comp["acts"] if a["type"] in OBJECTIVE_TYPES)
        if s_now < score:
            deficit = score - s_now
            for comp in components:
                if comp["kind"] != "Progress":
                    continue
                for a in comp["acts"]:
                    if a["type"] not in OBJECTIVE_TYPES or a["type"] in HYDRATED_TYPES:
                        continue  # event acts only; hydrated acts credit from preseed
                    base = a.get("count", 1)
                    if base <= 0:
                        continue
                    scaled_events[a["actId"]] = base + math.ceil(deficit / base)
                    break
                if scaled_events:
                    break

    events_by_kind = {}
    reward_items = []
    for comp in components:
        kind = comp["kind"]
        for act in comp["acts"]:
            if act.get("_unsupported") or act.get("_brokenRef"):
                continue
            if act["type"] in NO_EVENT_TYPES:
                continue
            shape_act = act
            if act["actId"] in scaled_events:
                shape_act = dict(act)
                shape_act["count"] = scaled_events[act["actId"]]
            shape = event_shape(act["type"], shape_act, comp["id"], item_groups, npc_groups, acceptor_npc_id)
            if shape is not None:
                # Multi-event shapes (e.g. cinema started->ended) come as lists.
                shapes = shape if isinstance(shape, list) else [shape]
                events_by_kind.setdefault(kind, []).extend(shapes)
            else:
                skip_reasons.append(f"unsynthesizable event shape for {act['type']} (comp {comp['id']})")
        if kind == "Reward":
            for act in comp["acts"]:
                if act["type"] == "QuestActSupplyItem" and not act.get("_unsupported"):
                    reward_items.append({"itemId": act["itemId"], "count": act.get("count", 1)})

    # RC-2: the engine forces Progress res=false for let-it-done quests
    # (QuestStep.RunComponents, "LetItBeDone type of quests are always forced
    # forward using the Report Acts"), so RunCurrentStep NEVER advances a
    # let-it-done Progress step - completion only happens via the report
    # force-advance (QuestActConReportNpc.OnReportNpc sets Step=Ready).
    # Exception: the RunCurrentStep HackFix advances when Score > 0 AND the
    # template has no Ready step (NewQuestCode.RunCurrentStep).
    progress_forced_stuck = let_it_done and not (score > 0 and "Ready" not in present)

    # ---- engine completion-path guards (band-sweep findings, 2026-08-06) ----
    # (1) A let-it-done Progress is force-blocked; the ONLY exits are a report
    #     act force-advance (OnReportNpc/OnReportDoodad/OnReportJournal) or the
    #     HackFix. Quests with neither (old Sunny Wilderness cluster 1867/1898/
    #     1904/1908/2054 + act-less 5575-5645) can NEVER leave Progress - the
    #     template has no engine completion path. SKIP with reason, never fake-
    #     pass a quest the engine cannot complete.
    # (2) score>0 Progress evaluates score over OBJECTIVE acts; with no
    #     objective acts the score can never be met (stuck at Progress).
    has_report_act = any(
        a["type"] in ("QuestActConReportNpc", "QuestActConReportDoodad", "QuestActConReportJournal")
        for comp in components for a in comp["acts"])
    progress_obj_acts = [a for comp in components if comp["kind"] == "Progress"
                         for a in comp["acts"] if a["type"] in OBJECTIVE_TYPES]
    if progress_forced_stuck and not has_report_act:
        skip_reasons.append("let-it-done quest with no report act (engine has no completion path)")
    if score > 0 and "Progress" in present and not progress_obj_acts:
        skip_reasons.append("score quest with no Progress objectives (score can never be met)")

    def progress_score_met(events_credited):
        """Mimics the engine's Progress+Score>0 branch (QuestStep.RunComponents):
        res = score >= Template.Score where score = sum(Count * Objective) over
        objective acts; hydrated gather objectives carry their full count from
        accept time. WI-8: score quests fire scaled event counts (scaled_events)
        so the credited objective can exceed the displayed count (the engine's
        MaxObjective = Score/Count+1 proves the data intends that)."""
        def credited(a):
            if not (events_credited or a["type"] in HYDRATED_TYPES):
                return 0
            return scaled_events.get(a["actId"], a.get("count", 1))
        acts = [a for comp in components if comp["kind"] == "Progress" for a in comp["acts"]]
        obj_acts = [a for a in acts if a["type"] in OBJECTIVE_TYPES]
        s = sum(a.get("count", 1) * credited(a) for a in obj_acts)
        return s >= score

    def kind_is_auto(kind_name):
        if progress_forced_stuck and kind_name == "Progress":
            return False  # let-it-done Progress never auto-advances
        acts = [a for comp in components if comp["kind"] == kind_name for a in comp["acts"]]
        if not acts:
            # empty components pass vacuously - EXCEPT score Progress: the
            # engine still evaluates score >= Template.Score (score = 0 with
            # no acts -> never met -> stuck), so it never auto-advances.
            if kind_name == "Progress" and score > 0:
                return progress_score_met(False)
            return True
        if kind_name == "Progress" and score > 0:
            # engine: Progress + Score>0 -> res = score >= Score; hydrated
            # gather objectives credit at accept, event acts credit later
            return progress_score_met(False)
        if kind_name in ("Start", "Ready"):
            # Engine ORs Start/Ready acts (QuestComponent.RunComponent:
            # actsOrCheck = ThisStep is Start or Ready && Acts.Count > 0) -
            # ANY always-true act (e.g. SupplyRemoveItem) passes the step.
            # WI-8: 5174/5722 have Ready = ReportNpc + SupplyRemoveItem; the
            # supply act returns true unconditionally, so the engine advances
            # past Ready WITHOUT the report event (probe: START -> Reward).
            return any(a["type"] in AUTO_PASS_TYPES or a["type"] in HYDRATED_TYPES for a in acts)
        if selective:
            # Selective quests pass the Progress step when ANY active component passes
            return any(a["type"] in AUTO_PASS_TYPES or a["type"] in HYDRATED_TYPES for a in acts)
        return all(a["type"] in AUTO_PASS_TYPES or a["type"] in HYDRATED_TYPES for a in acts)

    def progress_status_ready(events_credited):
        """Mimics Quest.GetQuestObjectiveStatus() >= QuestComplete for the
        Progress step: per-act objective counts are credited either by the
        stage events (events_credited) or hydrated from the pre-stocked
        inventory (gather acts credit at accept time)."""
        acts = [a for comp in components if comp["kind"] == "Progress" for a in comp["acts"]]
        obj_acts = [a for a in acts if a["type"] in OBJECTIVE_TYPES]
        if not obj_acts:
            return True  # no objectives -> QuestComplete
        if score > 0:
            return progress_score_met(events_credited)
        # Per-act counts: min over acts (max when selective)
        per_act = []
        for a in obj_acts:
            cnt = a.get("count", 1)
            obj = cnt if (events_credited or a["type"] in HYDRATED_TYPES) else 0
            if let_it_done and obj >= cnt * 3 // 2:
                st = 4  # Overachieved
            elif let_it_done and obj > cnt:
                st = 3  # ExtraProgress
            elif obj >= cnt:
                st = 2  # QuestComplete
            elif let_it_done and obj >= cnt // 2:
                st = 1  # CanEarlyComplete
            else:
                st = 0  # NotReady
            per_act.append(st)
        best = max(per_act) if selective else min(per_act)
        return best >= 2

    def expect_for_rest(pos, stage_kind):
        """Expectation for a resting position; let-it-done quests stuck at
        Progress get the engine's actual status (Ready once objectives are
        credited, else Progress) instead of the fixed Progress->'Progress' map.
        The ltd status re-evaluation only happens when RunComponents executes
        AT Progress (QuestStep.RunComponents ltd branch); the START stage's
        call runs at the FIRST present kind (Supply when one precedes Progress),
        so its rest carries the transition status (Progress)."""
        expect: dict = dict(expect_for(pos))
        if progress_forced_stuck and pos == "Progress":
            ran_at_progress = stage_kind != "Start" or first == "Progress"
            if progress_status_ready(stage_kind == "Progress") and ran_at_progress:
                expect["status"] = "Ready"
            else:
                expect["status"] = "Progress"
        return expect

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
        stages.append({"name": "START", "events": [], "expect": expect_for_rest(pos, "Start")})

        # ---- one stage per present stage-kind (Supply/Progress/Ready) ----
        # (None is consumed by the START auto-pass walk; it never gets its own
        # manifest stage - its acts are supply-shaped in every real carrier.)
        for kind in STAGE_KINDS:
            if kind not in present:
                continue
            if pos is None:
                # quest already completed - the stage's call cannot move it
                stages.append({"name": {"Supply": "SUPPLY", "Progress": "PROGRESS", "Ready": "READY"}[kind],
                               "events": events_by_kind.get(kind, []), "expect": {"completed": True}})
                continue
            if pos == kind:
                # resting at this stage's kind: events make its comps pass -> advance.
                # RC-2: let-it-done Progress never advances (engine forces res=false).
                if not (progress_forced_stuck and kind == "Progress"):
                    pos = advance(pos)
            elif kind_is_auto(pos):
                # resting ahead at an auto-pass kind (selective/hydrated advance) -> advance
                pos = advance(pos)
            elif progress_forced_stuck and kind == "Ready" and pos == "Progress":
                # RC-2: the READY stage's report event force-advances the stuck
                # let-it-done quest Progress -> Ready (QuestActConReportNpc.cs:59-60),
                # then the Ready step passes and RunCurrentStep moves it onward.
                pos = advance("Ready")
            # else: resting ahead at a non-auto kind - its events come later -> stays
            stages.append({"name": {"Supply": "SUPPLY", "Progress": "PROGRESS", "Ready": "READY"}[kind],
                           "events": events_by_kind.get(kind, []), "expect": expect_for_rest(pos, kind)})

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
    if guard_npc_ids:
        manifest["guard"] = {"npcId": guard_npc_ids[0], "alive": True}
        manifest["guards"] = [{"npcId": npc_id, "alive": True} for npc_id in guard_npc_ids]

    groups = {}
    for comp in components:
        for a in comp["acts"]:
            if a["type"] in ("QuestActObjItemGroupGather", "QuestActObjItemGroupUse"):
                gid = a.get("itemGroupId", 0)
                if gid and gid in item_groups:
                    groups.setdefault("itemGroups", {})[str(gid)] = item_groups[gid]
            elif a["type"] in ("QuestActObjExpressFire", "QuestActObjTalkNpcGroup"):
                gid = a.get("npcGroupId", 0)
                if gid and npc_groups and gid in npc_groups:
                    groups.setdefault("npcGroups", {})[str(gid)] = npc_groups[gid]
    if groups:
        manifest["groups"] = groups

    if skip_reasons:
        manifest["skip"] = {"reason": "; ".join(sorted(set(skip_reasons)))}

    return manifest


def act_type_in(comp_acts, act_type):
    return any(a["type"] == act_type for a in comp_acts)


# ---- T3 (M1-5c): stratified act-family census ----
# The M1-5c quota-fill selection is FROZEN here (2026-08-06, M2a wave-1):
# the greedy tie-break counts "unsupported act types" against ACT_TABLES, so
# growing ACT_TABLES with new closures would silently reshuffle the T3 sample
# (an expectation-model change, not a quest regression). The committed T3
# manifests stay byte-stable; new families get their own tiers (T4+).
# Provenance: selected 2026-08-05 from the act_detail_type distribution on
# prod compact.sqlite3 (r208022) - 54 quests covering every quota family.
T3_PINNED_QUESTS = [
    117, 127, 179, 523, 650, 815, 1005, 1353, 1408, 1443, 1798, 1954, 1956,
    1998, 2008, 2017, 2102, 2108, 2301, 2717, 2916, 2926, 3006, 3026, 3569,
    3570, 4419, 4433, 4434, 4784, 4788, 4881, 5052, 5263, 5430, 5442, 5443,
    5464, 5552, 5650, 5814, 5815, 5900, 5923, 5924, 5967, 6003, 6037, 6040,
    6095, 6250, 6282, 6375,
]

# ---- T4 (M2a wave-1): band 1-20 quests carrying the four closed act families ----
# Deterministic: quests whose acts include any wave-1 family AND level 1-20,
# minus dropped content, minus quests already sampled in T1/T2/T3 (each quest
# driven exactly once across the census).
WAVE1_ACT_TYPES = (
    "QuestActObjCinema",
    "QuestActEtcItemObtain",
    "QuestActConAcceptItemGain",
    "QuestActSupplyLp",
)


def select_t4_quests(c, existing_ids):
    """Band 1-20 wave-1 act carriers, not already sampled, ordered by id."""
    placeholders = ",".join("?" * len(WAVE1_ACT_TYPES))
    rows = c.execute(f"""
        SELECT DISTINCT cmp.quest_context_id
        FROM quest_acts a
        JOIN quest_components cmp ON a.quest_component_id = cmp.id
        JOIN quest_contexts q ON q.id = cmp.quest_context_id
        WHERE a.act_detail_type IN ({placeholders})
          AND q.LEVEL BETWEEN 1 AND 20
        ORDER BY cmp.quest_context_id""", WAVE1_ACT_TYPES).fetchall()
    return [r[0] for r in rows if r[0] not in existing_ids]


# ---- T5 (M2a wave-2): band 1-20 quests carrying the four wave-2 act families ----
# Same deterministic selection as T4: quests whose acts include any wave-2 family
# AND level 1-20, minus dropped content, minus quests already sampled in
# T1/T2/T3/T4 (each quest driven exactly once across the census).
WAVE2_ACT_TYPES = (
    "QuestActObjExpressFire",
    "QuestActObjAggro",
    "QuestActCheckCompleteComponent",
    "QuestActSupplyHonorPoint",
)


def select_t5_quests(c, existing_ids):
    """Band 1-20 wave-2 act carriers, not already sampled, ordered by id."""
    placeholders = ",".join("?" * len(WAVE2_ACT_TYPES))
    rows = c.execute(f"""
        SELECT DISTINCT cmp.quest_context_id
        FROM quest_acts a
        JOIN quest_components cmp ON a.quest_component_id = cmp.id
        JOIN quest_contexts q ON q.id = cmp.quest_context_id
        WHERE a.act_detail_type IN ({placeholders})
          AND q.LEVEL BETWEEN 1 AND 20
        ORDER BY cmp.quest_context_id""", WAVE2_ACT_TYPES).fetchall()
    return [r[0] for r in rows if r[0] not in existing_ids]


# ---- T9 (M2 WI-2, t_f42b9ae3): CrimePoint supply act carriers ----
# All 7 live carriers (2916/2926/2935/2936/5197/5198/5494). 2916/2926 are
# already sampled in T3 (T3_PINNED_QUESTS); the other five are level 41-50,
# outside the t6/t7/t8 band sweeps - they need their own tier or they never
# reach the census. No level filter: the family's carriers are all high-level.
WAVE3_ACT_TYPES = (
    "QuestActSupplyCrimePoint",
)


def select_t9_quests(c, existing_ids):
    """All CrimePoint act carriers, not already sampled, ordered by id."""
    placeholders = ",".join("?" * len(WAVE3_ACT_TYPES))
    rows = c.execute(f"""
        SELECT DISTINCT cmp.quest_context_id
        FROM quest_acts a
        JOIN quest_components cmp ON a.quest_component_id = cmp.id
        WHERE a.act_detail_type IN ({placeholders})
        ORDER BY cmp.quest_context_id""", WAVE3_ACT_TYPES).fetchall()
    # Dropped quests stay out of every tier (same rule as select_band_quests).
    return [r[0] for r in rows if r[0] not in DROPPED_QUESTS and r[0] not in existing_ids]


# ---- T10 (M2 WI-3, t_d5e802f5): AbilityLevel objective act carriers ----
# All 11 live carriers. 5967 (all-abilities branch) is already sampled in T3
# (T3_PINNED_QUESTS); 6069 was DROPPED 2026-08-09 (register §8, t_6810ebd4 -
# unreachable ltd, zero accept surfaces); the other nine (6070/6075-6082,
# single-ability, all level 50) are outside the t6/t7/t8 band sweeps - they
# need their own tier or they never reach the census (same rule as t9 for
# CrimePoint).
WAVE4_ACT_TYPES = (
    "QuestActObjAbilityLevel",
)


def select_t10_quests(c, existing_ids):
    """All AbilityLevel act carriers, not already sampled, ordered by id."""
    placeholders = ",".join("?" * len(WAVE4_ACT_TYPES))
    rows = c.execute(f"""
        SELECT DISTINCT cmp.quest_context_id
        FROM quest_acts a
        JOIN quest_components cmp ON a.quest_component_id = cmp.id
        WHERE a.act_detail_type IN ({placeholders})
        ORDER BY cmp.quest_context_id""", WAVE4_ACT_TYPES).fetchall()
    # Dropped quests stay out of every tier (same rule as select_band_quests).
    # Required for the 6069 drop: it is still a live DB row (the overlay is
    # deploy-time), so without this filter t10 would re-sample it after the
    # stale t3 manifest is gone.
    return [r[0] for r in rows if r[0] not in DROPPED_QUESTS and r[0] not in existing_ids]


# ---- T11 (M2 WI-4, t_fe93e2d8): MateLevel objective act carriers ----
# 6 live carriers: 5430/5464 (already in T3_PINNED_QUESTS) + 5465/5466/5812/
# 5813 (level 50, outside the t6/t7/t8 band sweeps - they need their own
# tier or they never reach the census, same rule as t9/t10). 6015 also has a
# MateLevel act but NO quest_contexts row (orphaned context) - joining
# quest_contexts excludes it so the census does not gain a NEW orphan SKIP
# (zero-new-SKIP acceptance).
WAVE5_ACT_TYPES = (
    "QuestActObjMateLevel",
)


def select_t11_quests(c, existing_ids):
    """All LIVE MateLevel act carriers, not already sampled, ordered by id."""
    placeholders = ",".join("?" * len(WAVE5_ACT_TYPES))
    rows = c.execute(f"""
        SELECT DISTINCT cmp.quest_context_id
        FROM quest_acts a
        JOIN quest_components cmp ON a.quest_component_id = cmp.id
        JOIN quest_contexts q ON q.id = cmp.quest_context_id
        WHERE a.act_detail_type IN ({placeholders})
        ORDER BY cmp.quest_context_id""", WAVE5_ACT_TYPES).fetchall()
    return [r[0] for r in rows if r[0] not in existing_ids]


# ---- T12 (M2 WI-5, t_d6516324): CompleteQuest objective act carriers ----
# 11 live carriers: 5814/5815 (already in T3_PINNED_QUESTS) + 5816-5821/5862/
# 5868/5911 (level 50, outside the t6/t7/t8 band sweeps - they need their own
# tier or they never reach the census, same rule as t9/t10/t11). The selector
# joins quest_contexts so orphaned CompleteQuest act rows can't leak a new
# orphan SKIP into the census (zero-new-SKIP acceptance).
WAVE6_ACT_TYPES = (
    "QuestActObjCompleteQuest",
)


def select_t12_quests(c, existing_ids):
    """All LIVE CompleteQuest act carriers, not already sampled, ordered by id."""
    placeholders = ",".join("?" * len(WAVE6_ACT_TYPES))
    rows = c.execute(f"""
        SELECT DISTINCT cmp.quest_context_id
        FROM quest_acts a
        JOIN quest_components cmp ON a.quest_component_id = cmp.id
        JOIN quest_contexts q ON q.id = cmp.quest_context_id
        WHERE a.act_detail_type IN ({placeholders})
        ORDER BY cmp.quest_context_id""", WAVE6_ACT_TYPES).fetchall()
    return [r[0] for r in rows if r[0] not in existing_ids]


# ---- T6/T7/T8/T13 (M2a/M2c/WI-7 census): full band sweeps ----
# Every non-dropped quest in the band, minus quests already sampled in
# T1-T5/T9-T12 (each quest driven exactly once across the census).
BAND_TIERS = [
    ("t6", "band 1-10", 1, 10),
    ("t7", "band 11-20", 11, 20),
    ("t8", "band 21-30", 21, 30),
    ("t13", "band 31-40", 31, 40),
    ("t14", "band 41-50", 41, 50),
]

# Dropped content (scorecard-explorations/dropped-content-register.md):
# dummy shell 1391 + 23 no-start tutorial shells + 8 orphaned contexts
# (745/1421/1954-1958/2140 have no quest_contexts row - excluded by the
# JOIN already; listed for the census denominator bookkeeping). The
# level-1 tutorial shells (1533, 1535-1541) DO have rows and land in band
# 1-10 - exclude explicitly so sweep denominators match the register.
# M2a drop (2026-08-08, register §6/§7, t_e5deb128): 26 engine-stuck
# templates (1867/1898/1904/1908/2054 + 5575-5645 + 5641, zone 22) + 91
# zero-component shells (2148-2229 reserve + 3748/3750-3757 Hadir) - rows
# deleted by SQL/patches/compact/2026-08-06-drop-m2a-stuck-and-shells.sql.
DROPPED_QUESTS = {
    1391, 745, 1421, 1954, 1955, 1956, 1957, 1958, 2140,
    1533, 1535, 1536, 1537, 1538, 1539, 1540, 1541, 1542, 1543,
    1544, 1545, 1546, 1547, 1548, 1549, 1551, 1552, 1553, 1554,
    1640, 1830, 1831,
    # --- M2a drop cluster A (26 engine-stuck, zone 22 old Sunny Wilderness) ---
    1867, 1898, 1904, 1908, 2054,
    5575, 5578, 5579, 5584, 5589, 5596, 5597, 5601, 5603, 5604, 5608, 5619,
    5630, 5632, 5636, 5637, 5640, 5641, 5643, 5644, 5645,
    # --- M2a drop cluster B (91 zero-component shells) ---
    *range(2148, 2230),  # 2148-2229 reserve block
    3748, *range(3750, 3758),  # Hadir-farm cutscenes
    # --- WI-6 drop (2026-08-09, register §8, t_6810ebd4): 6069 unreachable ltd ---
    # quest (zero accept surfaces, no completion path) - rows deleted by
    # SQL/patches/compact/2026-08-09-drop-wi6-6069.sql.
    6069,
    # --- WI-11a drop (2026-08-09, register §9, t_267a3279): 155 band-0/null contexts ---
    # A1 tutorial stubs (88, cat 45): 2584, 2586, 2589-2606, 2609, 2612, 2614, 2616, 2620-2683
    2584, 2586, *range(2589, 2607), 2609, 2612, 2614, 2616, *range(2620, 2684),
    # B1+D1 Dwarf main-story skeleton (60, cat 93, one kind-31 chain 5980→…→5811):
    5040, 5773, *range(5781, 5812),  # B1 (33 ltd)
    *range(3484, 3491), *range(3492, 3503), 3562, 3563, *range(3565, 3569), 3992, 4408, 5980,  # D1 (27)
    # B2 title quests (3, cat 82): 8000001-8000003
    *range(8000001, 8000004),
    # B3 cat-1 test/unused (3) + B4 Cradle act-less (1)
    1835, 1836, 1895, 5678,
}

# Signature zones for the M2a/M2c zone-coverage rows (M2_PLAN.md zone map):
# REAL zone ids only - the catch-all w_gweonid_forest_1 (1) and the
# old_/test_/machinima_ variants carry meaningless attribution. Band 21-30
# sets match M2_PLAN.md per-zone counts exactly (Ancient Forest 113,
# Marionople 102, Two Crowns 91, White Forest 90, Singing Land 84,
# Sunrise Peninsula 80, Lilyut 49) - the secondary zone ids (132/25/137)
# are NOT folded in so the zone-coverage rows reproduce the plan's numbers.
SIGNATURE_ZONES = {
    "Gweonid": [127, 128],
    "Lilyut": [11, 141],
    "Mahadevi": [18, 142, 143],
    "Tiger Spine": [23, 179],
    "Falcony": [21, 130],
    "Sunny Wilderness": [22, 136],
    "Ancient Forest": [24],
    "Marionople": [2],
    "Two Crowns": [15],
    "White Forest": [10],
    "Singing Land": [140],
    "Sunrise Peninsula": [8],
}


def select_band_quests(c, lo, hi, existing_ids):
    """Full band sweep: non-dropped quest_contexts with LEVEL in [lo, hi],
    minus quests already sampled in earlier tiers, ordered by id."""
    rows = c.execute(
        "SELECT id FROM quest_contexts WHERE LEVEL BETWEEN ? AND ? ORDER BY id",
        (lo, hi)).fetchall()
    return [r[0] for r in rows if r[0] not in DROPPED_QUESTS and r[0] not in existing_ids]


def emit_census_meta(c, out_root):
    """Band denominators (total / dropped-in-band / non-dropped) + signature
    zone map -> Manifests/census-meta.json. The tier test reads this to
    render the M2a band-census acceptance table and zone-coverage rows.
    Deterministic: fixed key order, no wall-clock."""
    meta = {"bands": {}, "signatureZones": []}
    for tier, label, lo, hi in BAND_TIERS:
        ids = [r[0] for r in c.execute(
            "SELECT id FROM quest_contexts WHERE LEVEL BETWEEN ? AND ? ORDER BY id",
            (lo, hi)).fetchall()]
        dropped = sorted(q for q in ids if q in DROPPED_QUESTS)
        meta["bands"][f"{lo}-{hi}"] = {
            "label": label, "tier": tier, "total": len(ids),
            "dropped": dropped, "nonDropped": len(ids) - len(dropped),
        }
    for name, zone_ids in SIGNATURE_ZONES.items():
        meta["signatureZones"].append({"name": name, "zoneIds": zone_ids})
    with open(os.path.join(out_root, "census-meta.json"), "w") as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)
    return meta


def primary_family(acts):
    """Quest family label for the report: most frequent act type, preferring
    supported (generator-known) types so common families label drivable quests."""
    counts = {}
    for a in acts:
        counts[a] = counts.get(a, 0) + 1
    supported = sorted([a for a in counts if a in ACT_TABLES], key=lambda a: (-counts[a], a))
    unsupported = sorted([a for a in counts if a not in ACT_TABLES], key=lambda a: (-counts[a], a))
    if not (supported or unsupported):
        return "no-acts"  # act-less shell (band sweep includes empty contexts)
    return (supported or unsupported)[0]


def main():
    c = sqlite3.connect(DB)
    c.row_factory = sqlite3.Row
    item_groups = load_item_groups(c)
    npc_groups = load_npc_groups(c)

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

    counts = {"t1": 0, "t2": 0, "t3": 0, "t4": 0}
    for tier, ids in (("t1", t1_ids), ("t2", t2_ids)):
        out_dir = os.path.join(OUT_ROOT, tier)
        os.makedirs(out_dir, exist_ok=True)
        family = "golden-zone" if tier == "t1" else "mixed-families"
        for qid in ids:
            manifest = build_manifest(c, qid, family, item_groups, npc_groups)
            if manifest is None:
                continue
            with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
                json.dump(manifest, f, ensure_ascii=False, indent=1)
            counts[tier] += 1

    # ---- T3 (M1-5c): stratified act-family census (frozen sample) ----
    existing_ids = set()
    for tier in ("t1", "t2"):
        tier_dir = os.path.join(OUT_ROOT, tier)
        if os.path.isdir(tier_dir):
            existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(tier_dir) if f.endswith(".json")}
    t3_ids = T3_PINNED_QUESTS
    out_dir = os.path.join(OUT_ROOT, "t3")
    os.makedirs(out_dir, exist_ok=True)
    for qid in t3_ids:
        acts = set(r[0] for r in c.execute(
            """SELECT a.act_detail_type FROM quest_acts a
              JOIN quest_components cmp ON a.quest_component_id = cmp.id
              WHERE cmp.quest_context_id = ?""", (qid,)).fetchall())
        manifest = build_manifest(c, qid, primary_family(acts), item_groups, npc_groups)
        if manifest is None:
            continue
        with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=1)
        counts["t3"] += 1

    # ---- T4 (M2a wave-1): band 1-20 quests carrying the wave-1 act families ----
    existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(os.path.join(OUT_ROOT, "t3")) if f.endswith(".json")}
    t4_ids = select_t4_quests(c, existing_ids)
    out_dir = os.path.join(OUT_ROOT, "t4")
    os.makedirs(out_dir, exist_ok=True)
    for qid in t4_ids:
        acts = set(r[0] for r in c.execute(
            """SELECT a.act_detail_type FROM quest_acts a
              JOIN quest_components cmp ON a.quest_component_id = cmp.id
              WHERE cmp.quest_context_id = ?""", (qid,)).fetchall())
        manifest = build_manifest(c, qid, primary_family(acts), item_groups, npc_groups)
        if manifest is None:
            continue
        with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=1)
        counts["t4"] = counts.get("t4", 0) + 1

    # ---- T5 (M2a wave-2): band 1-20 quests carrying the wave-2 act families ----
    existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(os.path.join(OUT_ROOT, "t4")) if f.endswith(".json")}
    t5_ids = select_t5_quests(c, existing_ids)
    out_dir = os.path.join(OUT_ROOT, "t5")
    os.makedirs(out_dir, exist_ok=True)
    for qid in t5_ids:
        acts = set(r[0] for r in c.execute(
            """SELECT a.act_detail_type FROM quest_acts a
              JOIN quest_components cmp ON a.quest_component_id = cmp.id
              WHERE cmp.quest_context_id = ?""", (qid,)).fetchall())
        manifest = build_manifest(c, qid, primary_family(acts), item_groups, npc_groups)
        if manifest is None:
            continue
        with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=1)
        counts["t5"] = counts.get("t5", 0) + 1
    # Fold t5 into the sampled set so the band sweeps exclude wave-2 carriers
    # too (each quest driven exactly once across the census).
    existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(os.path.join(OUT_ROOT, "t5")) if f.endswith(".json")}

    # ---- T9 (M2 WI-2, t_f42b9ae3): CrimePoint supply act carriers ----
    # The five level-41-50 carriers not sampled anywhere else (2916/2926 are
    # already in T3). Folded into existing_ids before the band sweeps so they
    # stay driven exactly once.
    existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(os.path.join(OUT_ROOT, "t3")) if f.endswith(".json")}
    t9_ids = select_t9_quests(c, existing_ids)
    out_dir = os.path.join(OUT_ROOT, "t9")
    os.makedirs(out_dir, exist_ok=True)
    for qid in t9_ids:
        acts = set(r[0] for r in c.execute(
            """SELECT a.act_detail_type FROM quest_acts a
              JOIN quest_components cmp ON a.quest_component_id = cmp.id
              WHERE cmp.quest_context_id = ?""", (qid,)).fetchall())
        manifest = build_manifest(c, qid, primary_family(acts), item_groups, npc_groups)
        if manifest is None:
            continue
        with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=1)
        counts["t9"] = counts.get("t9", 0) + 1
    existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(out_dir) if f.endswith(".json")}

    # ---- T10 (M2 WI-3, t_d5e802f5): AbilityLevel objective carriers ----
    # The nine level-50 single-ability carriers (6070/6075-6082) not sampled
    # anywhere else (5967/6069 are in T3). Folded into existing_ids before
    # the band sweeps so they stay driven exactly once.
    t10_ids = select_t10_quests(c, existing_ids)
    out_dir = os.path.join(OUT_ROOT, "t10")
    os.makedirs(out_dir, exist_ok=True)
    for qid in t10_ids:
        acts = set(r[0] for r in c.execute(
            """SELECT a.act_detail_type FROM quest_acts a
              JOIN quest_components cmp ON a.quest_component_id = cmp.id
              WHERE cmp.quest_context_id = ?""", (qid,)).fetchall())
        manifest = build_manifest(c, qid, primary_family(acts), item_groups, npc_groups)
        if manifest is None:
            continue
        with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=1)
        counts["t10"] = counts.get("t10", 0) + 1
    existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(out_dir) if f.endswith(".json")}

    # ---- T11 (M2 WI-4, t_fe93e2d8): MateLevel objective carriers ----
    # The four level-50 carriers not sampled anywhere else (5465/5466/5812/
    # 5813; 5430/5464 are in T3). Folded into existing_ids before the band
    # sweeps so they stay driven exactly once.
    t11_ids = select_t11_quests(c, existing_ids)
    out_dir = os.path.join(OUT_ROOT, "t11")
    os.makedirs(out_dir, exist_ok=True)
    for qid in t11_ids:
        acts = set(r[0] for r in c.execute(
            """SELECT a.act_detail_type FROM quest_acts a
              JOIN quest_components cmp ON a.quest_component_id = cmp.id
              WHERE cmp.quest_context_id = ?""", (qid,)).fetchall())
        manifest = build_manifest(c, qid, primary_family(acts), item_groups, npc_groups)
        if manifest is None:
            continue
        with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=1)
        counts["t11"] = counts.get("t11", 0) + 1
    existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(out_dir) if f.endswith(".json")}

    # ---- T12 (M2 WI-5, t_d6516324): CompleteQuest objective carriers ----
    # The nine level-50 carriers not sampled anywhere else (5816-5821/5862/
    # 5868/5911; 5814/5815 are in T3). Folded into existing_ids before the
    # band sweeps so they stay driven exactly once.
    t12_ids = select_t12_quests(c, existing_ids)
    out_dir = os.path.join(OUT_ROOT, "t12")
    os.makedirs(out_dir, exist_ok=True)
    for qid in t12_ids:
        acts = set(r[0] for r in c.execute(
            """SELECT a.act_detail_type FROM quest_acts a
              JOIN quest_components cmp ON a.quest_component_id = cmp.id
              WHERE cmp.quest_context_id = ?""", (qid,)).fetchall())
        manifest = build_manifest(c, qid, primary_family(acts), item_groups, npc_groups)
        if manifest is None:
            continue
        with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
            json.dump(manifest, f, ensure_ascii=False, indent=1)
        counts["t12"] = counts.get("t12", 0) + 1
    existing_ids |= {int(os.path.splitext(f)[0]) for f in os.listdir(out_dir) if f.endswith(".json")}

    # ---- T6/T7 (M2a census): full band sweeps ----
    # The band denominators (incl. dropped ids per band) and the signature
    # zone map are emitted to Manifests/census-meta.json for the tier test's
    # acceptance table + zone-coverage rows.
    emit_census_meta(c, OUT_ROOT)
    band_counts = {}
    for tier, label, lo, hi in BAND_TIERS:
        band_ids = select_band_quests(c, lo, hi, existing_ids)
        out_dir = os.path.join(OUT_ROOT, tier)
        os.makedirs(out_dir, exist_ok=True)
        for qid in band_ids:
            acts = set(r[0] for r in c.execute(
                """SELECT a.act_detail_type FROM quest_acts a
                  JOIN quest_components cmp ON a.quest_component_id = cmp.id
                  WHERE cmp.quest_context_id = ?""", (qid,)).fetchall())
            manifest = build_manifest(c, qid, primary_family(acts), item_groups, npc_groups)
            if manifest is None:
                continue
            with open(os.path.join(out_dir, f"{qid}.json"), "w") as f:
                json.dump(manifest, f, ensure_ascii=False, indent=1)
            counts[tier] = counts.get(tier, 0) + 1
        band_counts[tier] = len(band_ids)

    print(json.dumps({"generated": counts, "out": OUT_ROOT,
                      "t1_total": len(t1_ids), "t2_total": len(t2_ids),
                      "t3_selected": len(t3_ids), "t4_selected": len(t4_ids),
                      "t5_selected": len(t5_ids),
                      "t9_selected": len(t9_ids),
                      "t10_selected": len(t10_ids),
                      "t11_selected": len(t11_ids),
                      "t12_selected": len(t12_ids),
                      "t6_selected": band_counts.get("t6", 0),
                      "t7_selected": band_counts.get("t7", 0),
                      "t8_selected": band_counts.get("t8", 0),
                      "t13_selected": band_counts.get("t13", 0),
                      "t14_selected": band_counts.get("t14", 0)}, indent=1))


if __name__ == "__main__":
    main()
