# Dropped Content Register — quest_contexts & dangling rows

**Purpose:** durable record of every quest context / data row DROPPED from the
fork's canonical 1.2 data handling, with decision provenance, execution cards,
and restore pointers. "In case we need to know" — grep this file before
re-adding, restoring, or re-flagging any id listed here.

**Decision authority:** Josh (2026-08-05 chat, msg 1534679020862701689):
*"Unblock granted, if they're orphans we prob don't need to code em in."*
Drop = data-level deletion / prune via SQL patches + verifier allowlist
removal. No code written to keep dead content alive.

All ids reference the canonical `compact.sqlite3` (md5
`78b3bdbf0383db3b927056106efdf91af`) — READ-ONLY reference; drops are applied
via `SQL/patches/compact/*.sql` guarded DELETEs + in-memory overlay where
needed, never by editing the reference file.

---

## 1. Dummy shells — 1391

| Field | Value |
|---|---|
| Quest | 1391 마을을 지켜라 ("Protect the Village") |
| Shape | 0 components / 0 acts; milestone_id=5, let_it_done='t', cat 27, zone 0, lvl 0 |
| Verdict | data-defects.md §6 (c) drop — deliberate dummy shell, allowlist-masked to INFO |
| Drop action | delete `quest_contexts` row 1391 + remove 1391 from verifier allowlist (QuestSanityVerifier.cs:93 "dummy shells" group) so a regression re-reports at WARN |
| Execution card | t_5a61cee3 (impl, ready) → Rei gate t_70ae1bba → census t_e239aa09 |
| Rig | fix/no-components-1391-rig @ 405e85b5 — flip to assert absence |
| Restore pointer | None — no canonical content exists (that's why it's a shell). Rebuild only if a real quest with this shape is sourced from client data. |

## 2. QUEST_NO_START cluster — 23 legacy tutorial shells

| Field | Value |
|---|---|
| Quests | **1533, 1535–1549, 1551–1554, 1640, 1830, 1831** (1534/1550 are pure id gaps — nothing to delete) |
| Shape | each has exactly one kind-8 (Reward) comp with QuestActSupplyCopper + QuestActSupplyExp; 1830/1831 "UNUSED" empty; zero Start comps, zero accept surfaces |
| Origin | legacy 1.0-era numbered tutorial step list (튜토리얼… 메인퀘저널), zone 1 `w_gweonid_forest_1` (old Nuian starter), cat 28 — superseded by the Solzreed opening (golden route) |
| Verdict | data-defects.md §5 (c) drop |
| Drop action | delete 23 quest_contexts + their quest_components/quest_acts rows via SQL patch; remove cluster ids from verifier allowlist (QuestSanityVerifier.cs:84-109) |
| Execution card | t_5140fb35 (impl, running) → Rei gate t_f884383f → census t_d5e7d11f |
| Rig | fix/no-start-1533-rig @ 9370e985 — flipped to post-drop contract (dropped-or-never-acceptable + allowlist-removed + patch-on-copy pass-after) on fix/no-start-1533 |
| Patch | `SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql` — guarded DELETEs: −23 quest_contexts / −25 quest_components / −42 quest_acts (drift 4876→4853, 17851→17826, 26886→26844; shared act-detail + unit_reqs collision rows untouched); census `Scripts/quest_no_start_census.sh --apply-fix` fail-before 23 → pass-after 0 |
| Restore pointer | **These shells are the skeleton to reuse if a 1.2-era tutorial is ever rebuilt** (data-defects.md §5). |

## 3. Orphaned quest_contexts — 8 (of 28 audited)

| Field | Value |
|---|---|
| Quests | **745, 1421, 1954, 1955, 1956, 1957, 1958, 2140** (full bodies survive: 3–10 comps each; context rows + texts deleted upstream) |
| Chain | cat-34 crafting chain: 1954→1955→1956→1957→1958→1959(live)→1960→1961→2140→2141→2142→2143→2144(live)→2145→2146 (data-defects.md §4) |
| Verdict | data-defects.md §7 (c) drop all 28 audited orphans; this card covers the 8 in the M1 widened backlog |
| Drop action | prune dangling unit_reqs gates **16064, 19197, 19198, 19201, 19205, 19207** (+ optional sphere_quests 418, sphere_accept_quest_quests 3) via `SQL/patches/compact/2026-08-05-drop-8-orphaned-contexts.sql` |
| Already pruned (do NOT re-prune) | unit_reqs 16000 + item_accept rows 5133/6420 — covered by `2026-08-04-fix-quest-data-defects.sql` on develop |
| Execution card | t_0ac25620 (ready) → Rei gate (to be filed on impl block) |
| Correction on record | data-defects.md's "745 blocks quest 2951's Supply" is an **id-collision misread**: unit_reqs 16000 is Skill-owned (gates skill 12913 가방 증기), engine keys by (owner_type, owner_id); 2951's real gates resolve — the prune is hygiene, NOT a 2951 unblock |
| Restore pointer | Chain is ruled dead (data-defects.md §4). Restoring any orphan requires the full chain context; do not re-add single contexts. |

## 4. Dangling accept-acts (chain B prune, not a context drop)

| Row | Details |
|---|---|
| quest 2145 Reward comp 9927 | accept-act `quest_act_con_accept_components` id 89 + `quest_acts` 14121 → 2146 (dropped orphan) |
| quest 1960 comp 9794 (sibling) | accept-act 75 → 1961 (dropped orphan) |
| Execution card | t_60a559ab (impl, running) → Rei gate t_53baa876 → census t_20b1bfb7 |

## 5. Related fix — NOT dropped (for contrast)

| Item | Status |
|---|---|
| quests 330/776/777 COMPONENT_NEXT_MISSING | **FIXED** via additive in-memory overlay QuestDataOverlay (1520→1521, 3480→3482, 3488→11591) — branch fix/next-missing-776-777 @ aa35a503, Rei gate t_d8a8c798. 330 is golden-route (step 3, zero runtime impact). |

## 6. M2a engine-stuck cluster — 26 templates (old Sunny Wilderness)

| Field | Value |
|---|---|
| Quests | **1867, 1898, 1904, 1908, 2054** (A1 — carry real NPC dialogue) + **5575, 5578, 5579, 5584, 5589, 5596, 5597, 5601, 5603, 5604, 5608, 5619, 5630, 5632, 5636, 5637, 5640, 5643, 5644, 5645** (A2 — act-less, cat 32) + **5641** (A3 — ltd='f' score=100, score never met) |
| Shape | zone 22 (old_e_sunny_wilderness), milestone 5, let_it_done='t' (except 5641). Zero report acts → engine can never leave Progress (QuestStep.cs:127-129 ltd force; NewQuestCode.cs:69-78 HackFix needs Score>0 AND no Ready step — none qualify). Zero accept surfaces (0 item_accept_quests / doodad_func_quests / accept_quest_effects / con_accept_components) → unreachable in live play |
| Verdict | Josh 2026-08-08 ("Option A is nice") — drop. Triage: t_f105ee3b; census SKIP guards (ltd-without-report-act / score-without-objectives) in the M2a band census |
| Drop action | `SQL/patches/compact/2026-08-06-drop-m2a-stuck-and-shells.sql`: −26 quest_contexts / −83 quest_components / −5 quest_acts / −5 act-detail rows / −10 quest_component_texts / −27 quest_chat_bubbles (A+B surfaces); 26 were never allowlisted (they have components — no mask to remove) |
| Execution card | t_e5deb128 (impl) → Rei gate t_656ed5fe |
| Restore pointer | **A1 dialogue preserved as reference**: 1867/1898/1904/1908/2054 carry real old-zone NPC dialogue — 27 quest_chat_bubbles on comps 8593/8594/8595/8804/8805/8806/8835/8836/8837/8851/8852/8853/9447/9448/9449 (NPCs 4917/4927/4922/4931/4938), 10 component_texts, 5 acts (item_gathers 848/878/885, monster_hunts 532/533). Superseded old-Sunny-Wilderness content worth preserving as reference — restore only if a 1.2-era Sunny Wilderness quest line is rebuilt. **Quest 1899** ("두려운 사실", same old cluster, census-PASS vacuous) is NOT dropped — flagged future-look: its Start gate unit_reqs 18563 (→1898) was pruned with this drop; re-check 1899's shape before any restore work |
| Residue (runtime-safe, verifier-silent) | 3 `items.loot_quest_id` rows dangle → dropped quests (15887→2054, 15985→1867, 16018→1904). Inert: item rows still load, loot just never fires. Leave unless rebuilding the Sunny Wilderness line |

## 7. M2a zero-component shells — 91 (reserve + Hadir cutscenes)

| Field | Value |
|---|---|
| Quests | **2148–2229** (82 "하다보니(reserve)", cat 34, zone 1, lvl 1, ms 5, ltd='f' — deliberate reserve block) + **3748, 3750–3757** (9 Hadir's Farm instance-cutscene shells, zone 169 instance_hadir_farm, cat 63) |
| Shape | zero quest_components each; census SKIP "no components" |
| Verdict | Josh 2026-08-08 ("Option A is nice") — drop. data-defects.md §6 already ruled reserve shells dead |
| Drop action | `SQL/patches/compact/2026-08-06-drop-m2a-stuck-and-shells.sql`: −91 quest_contexts / −9 sphere_quests rows (725→3748, 727–734→3750–3757 — accept triggers, MUST prune with the drop); removed all 91 from verifier allowlist (QuestSanityVerifier.cs — was 108 ids, now 17) so a regression re-reports at WARN |
| Execution card | t_e5deb128 (impl) → Rei gate t_656ed5fe |
| Restore pointer | None — deliberate reserve/cutscene shells with zero content. ⚠ Kind-35 Skill unit_reqs (38519/38559/38561/38563/38565/38955/38959/38968/38969/38973/38976/40175/40335/40383) reference spheres 2148–2189 (AreaSphere id collisions with the reserve ids) — **UNTOUCHED**; do not prune them when re-checking this drop |

---

## 8. WI-6 band 41-50 ltd triage — 6069 dropped; 3419/4967 ruled KEEP (2026-08-09)

Evidence: `scorecard-explorations/band-41-50-ltd-triage-wi6.md` + attached packet on card
t_6f950108. **Decision (G0-5): Josh, 2026-08-09 ~21:15 PDT (delegated to Kimi nightwatch —
comment on t_6f950108):** 3419 **NO-GO** / 4967 **NO-GO** / 6069 **GO** ("Execute per M2a
playbook: guarded SQL patch, quest-scoped rows only, shared act-detail rows untouched").
Execution card: **t_6810ebd4** → Rei gate (filed on impl block, t_656ed5fe pattern).

⚠ **3419 + 4967 are KEPT — do not re-flag as engine-stuck.** They have a live completion path
M2a's cluster-A register line predates: the registered client packet 0x0dd
(CSTryQuestCompleteAsLetItDone → CharacterQuests.TryCompleteQuestAsLetItDone, no report act
needed) once objectives credit. See evidence §2 on t_6f950108 for the full path analysis.

| Quest | Shape | Accept surface | Completion path | Josh's ruling |
|---|---|---|---|---|
| **3419** 의논할 수 없는 고민 | ltd, score 0, Start→Progress→Reward, no report act; zone 20, ms 5, cat 59 | **NPC 9581** (live, spawner) + gates 3370/3372 (completable) | packet 0x0dd (2/3 group-469 kills → CanEarlyComplete) | **NO-GO — KEEP** |
| **4967** 황금비늘의 후손 해방 | ltd, score 0, Start→Progress→Reward, no report act; zone 1, ms 5, cat 60 | **NPC 10089** (live, spawner) | packet 0x0dd (1 interaction credits) | **NO-GO — KEEP** |
| **6069** 거침없이 춤추는 격투의 칼날 | ltd, score 0, Start(no acts)→Progress→Ready(no acts)→Reward; zone 1, ms 14, cat 55 | **NONE** (0 across all 5 accept surfaces) | none — unreachable; objective never credits | **GO — DROP** |

### 6069 drop record

| Field | Value |
|---|---|
| Quest | 6069 거침없이 춤추는 격투의 칼날 ("The Uninhibited Dancing Blade of Combat") |
| Shape | ltd='t', score 0, zone 1, ms 14, cat 55, lvl 50; Start comp 26119 (NO acts) → Progress 26120 (QuestActObjAbilityLevel 7, ability 1 @ lvl 50 — no event hookup, objective never credits) → Ready 26121 (no acts) → Reward 26122 (QuestActSupplyItem 4002 → item 30757). **Zero accept surfaces** (0 item_accept_quests / doodad_func_quests / accept_quest_effects / sphere_quests / con_accept_components) → unreachable in live play |
| Verdict | Josh 2026-08-09 (t_6f950108 comment, delegated Kimi nightwatch) — **GO drop**. Consistent with M2a cluster-A precedent (register §6) + existing SKIP flags (runnability.md:245, SCORECARD.md:162) |
| Drop action | `SQL/patches/compact/2026-08-09-drop-wi6-6069.sql` (card t_6810ebd4): quest_contexts −1 (6069) / quest_components −4 (26119/26120/26121/26122) / quest_acts −2 (35730/35732) / unit_reqs −1 (45196) / quest_component_texts −3 / quest_chat_bubbles −4. ⚠ **Shared act-detail rows 7 + 4002 UNTOUCHED** (ability 7 → 15 quests; supply 4002 → 5106). Remove 6069 from `T3_PINNED_QUESTS` (tools/quest-scenario/gen-manifests.py:797) + DROPPED_QUESTS +1 + regenerate manifests/census-meta; update runnability.md SKIP row + SCORECARD.md line; check verifier allowlist (has components — expected not listed) |
| Execution card | t_6810ebd4 (impl) → Rei gate (filed on impl block) |
| Rig | No dedicated rig; 5967 (all-abilities branch) remains the canonical ability-level harness carrier — 6069's single-ability variant (ability 1) loses its example, acceptable at 0 live carriers |
| Restore pointer | Full body preserved (4 comps / 2 acts / 3 texts / 4 bubbles; act-detail rows 7 + 4002 remain shared-live). Restore only if a 1.2-era ability-gate ltd quest is rebuilt — re-add to T3_PINNED_QUESTS if restored |

---

## How to check if an id is in this register

```bash
grep -n "745\|1421\|1391\|1533\|2140\|1954\|1867\|2148\|3748\|5575\|6069" scorecard-explorations/dropped-content-register.md
```

Before filing any future quest-defect card, check this file — a "missing"
quest may be here by decision, not by accident.
