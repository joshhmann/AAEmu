#!/usr/bin/env python3
"""Refined feature completeness scorecard — TWO dimensions:
A) Data layer: is the canonical sqlite table referenced anywhere in .cs?
B) Logic layer: does a Manager class exist for the domain? tests exist?

Output: markdown scorecard for the repo.
"""
import os
import re
from collections import defaultdict

REPO = "/root/aaemu-dev"
CODE_DIRS = ["AAEmu.Game", "AAEmu.Login", "AAEmu.Commons"]

with open("/tmp/tables.txt") as f:
    tables = [line.strip() for line in f if line.strip()]

blobs = {}
for d in CODE_DIRS:
    path = os.path.join(REPO, d)
    texts = []
    for root, dirs, files in os.walk(path):
        for fn in files:
            if fn.endswith(".cs"):
                with open(os.path.join(root, fn), errors="ignore") as fh:
                    texts.append(fh.read())
    blobs[d] = "\n".join(texts)

# manager inventory
managers = set()
for d in CODE_DIRS:
    path = os.path.join(REPO, d)
    for root, dirs, files in os.walk(path):
        for fn in files:
            if fn.endswith("Manager.cs") and not fn.startswith("I"):
                managers.add(fn[:-3])

def domain_of(table):
    if table.startswith("quest_act_"):
        return "quests"
    if table.startswith("doodad_func_"):
        return "doodads"
    if table.startswith("indun_"):
        return "instances"
    if table.startswith("fx_"):
        return "fx-visuals"
    if table.startswith("item_"):
        return "items"
    if table.startswith("npc_"):
        return "npcs"
    if table.startswith("slave_"):
        return "slaves"
    if table.startswith("mate_"):
        return "mates"
    if table.startswith("buff_"):
        return "buffs"
    if table.startswith("skill_"):
        return "skills"
    if table.startswith("housing_"):
        return "housing"
    if table.startswith("sphere_"):
        return "spheres"
    if table.startswith("plot_"):
        return "plots"
    if table.startswith("zone_"):
        return "zones"
    if table.startswith("auction_"):
        return "auction"
    if table.startswith("craft_"):
        return "crafting"
    if table.startswith("mail_"):
        return "mail"
    if table.startswith("music_"):
        return "music"
    if table.startswith("model_"):
        return "models"
    if table.startswith("sound_"):
        return "sounds"
    if table.startswith("premium_"):
        return "premium"
    if table.startswith("quest_"):
        return "quests"
    if table.startswith("tower_def"):
        return "towerdefense"
    if table.startswith("specialt"):
        return "specialty-trade"
    if table.startswith("shipyard"):
        return "shipyards"
    if table.startswith("transfer"):
        return "transfers"
    if table.startswith("world_"):
        return "world"
    if table.startswith("character_"):
        return "characters"
    if table.startswith("equip_"):
        return "equipment"
    if table.startswith("gimmick"):
        return "gimmicks"
    if table.startswith("rank_"):
        return "ranks"
    if table.startswith("achievement"):
        return "achievements"
    if table.startswith("battle_field"):
        return "battlefields"
    if table.startswith("crime"):
        return "crime"
    if table.startswith("taxation"):
        return "taxation"
    if table.startswith("siege"):
        return "siege"
    if table.startswith("race_track"):
        return "race-tracks"
    if table.startswith("fish"):
        return "fishing"
    if table.startswith("merchant"):
        return "merchants"
    if table.startswith("loot"):
        return "loot"
    if table.startswith("mould"):
        return "moulds"
    if table.startswith("appellation"):
        return "appellations"
    if table.startswith("bubble"):
        return "bubbles"
    if table.startswith("express"):
        return "express-text"
    if table.startswith("combat"):
        return "combat"
    if table.startswith("expedition"):
        return "expeditions"
    if table.startswith("family"):
        return "families"
    if table.startswith("slave"):
        return "slaves"
    return "misc"

def referenced(table):
    pat = re.compile(r"\b" + re.escape(table) + r"\b")
    return any(pat.search(blob) for blob in blobs.values())

domains = defaultdict(lambda: {"total": 0, "ref": 0, "tables": []})
for t in tables:
    dom = domain_of(t)
    domains[dom]["total"] += 1
    domains[dom]["tables"].append(t)
    if referenced(t):
        domains[dom]["ref"] += 1

# manager presence per domain (singular-match: doodads->DoodadManager etc.)
def singular(dom):
    for suf in ("s", "es"):
        if dom.endswith(suf) and len(dom) > len(suf) + 2:
            return dom[: -len(suf)]
    return dom

manager_hits = defaultdict(list)
for m in sorted(managers):
    ml = m.lower()
    for dom in domains:
        s = singular(dom)
        if s == "misc" or s == "fx-visuals":
            continue
        if s in ml and ml.startswith(s):
            manager_hits[dom].append(m)
            break

print("# ArcheAge 1.2 Feature Completeness Scorecard")
print()
print(f"Generated from: compact.sqlite3 r208022 ({len(tables)} tables) vs AAEmu develop ({len(managers)} managers).")
print()
print("## Legend")
print("- **Tables**: canonical sqlite tables in the domain")
print("- **Data-wired**: tables referenced by any .cs (server reads this data)")
print("- **Managers**: game systems present in code")
print()
print("## Domain scorecard")
print()
print("| Domain | Tables | Data-wired | % | Managers |")
print("|--------|--------|-----------|------|----------|")
for dom in sorted(domains, key=lambda d: -domains[d]["total"]):
    s = domains[dom]
    pct = 100 * s["ref"] / s["total"] if s["total"] else 0
    ms = ", ".join(manager_hits.get(dom, [])[:4]) or "—"
    print(f"| {dom} | {s['total']} | {s['ref']} | {pct:.0f}% | {ms} |")

print()
print("## Zero-data-wired domains (data exists, server ignores it)")
print()
for dom in sorted(domains, key=lambda d: -domains[d]["total"]):
    s = domains[dom]
    if s["ref"] == 0 and s["total"] >= 3:
        print(f"- **{dom}** ({s['total']} tables): {', '.join(s['tables'][:6])}{'...' if s['total'] > 6 else ''}")
