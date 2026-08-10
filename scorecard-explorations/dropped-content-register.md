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
`78b3bdbf038db3b927056106efdf91af`) — READ-ONLY reference; drops are applied
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

## 8. WI-6 band 41-50 ltd triage — DRAFT (PENDING Josh decision, 2026-08-09)

Evidence: `scorecard-explorations/band-41-50-ltd-triage-wi6.md` (card t_6f950108). Josh rules
per G0-5; Nei prepared evidence only. Status: **BLOCKED awaiting decision** — this section is a
draft; verdicts below are recommendations until Josh answers on the card.

| Quest | Shape | Accept surface | Completion path | Recommendation | Josh's ruling |
|---|---|---|---|---|---|
| **3419** 의논할 수 없는 고민 | ltd, score 0, Start→Progress→Reward, no report act; zone 20, ms 5, cat 59 | **NPC 9581** (live, spawner) + gates 3370/3372 (completable) | packet path 0x0dd (reachable: 2/3 group-469 kills → CanEarlyComplete) | NO-GO drop (keep) | _pending_ |
| **4967** 황금비늘의 후손 해방 | ltd, score 0, Start→Progress→Reward, no report act; zone 1, ms 5, cat 60 | **NPC 10089** (live, spawner) | packet path 0x0dd (1 interaction credits) | NO-GO drop (keep) | _pending_ |
| **6069** 거침없이 춤추는 격투의 칼날 | ltd, score 0, Start(no acts)→Progress→Ready(no acts)→Reward; zone 1, ms 14, cat 55 | **NONE** (0 across all 5 accept surfaces) | none (unreachable; objective never credits) | GO drop | _pending_ |

## 9. WI-11a band-0/null — 155 dropped; 4 KEPT; 56 deferred to WI-11b (2026-08-09)

**Decision (G0-5): Josh, 2026-08-09 ~21:15 PDT (delegated to Kimi nightwatch —
comment on t_724ccab2):** Q1 **GO** / Q2 **NO-GO** / Q3 **GO** / Q4 **GO** /
Q5 **GO** / Q6 **GO** / Q7-Q10 **DEFER** to WI-11b (t_8ec705f0).
"Execute per M2a playbook: guarded patches, register §8 [sic — this §9] with
provenance per set." Execution card: **t_267a3279** (impl, hyrax-os) → Rei
gate (filed on impl block, t_656ed5fe pattern). Reversible via restore
pointers below. Evidence packet:
`scorecard-explorations/wi-11a-band0-null-triage.md` (t_724ccab2, Nei).
Population: 231 LEVEL-0/NULL contexts; 16 already in this register (§1/§2,
excluded); 215 live candidates → 155 dropped (this section) + 56 deferred
(§9g) + 4 kept (Q2, §9b).

### 9a. A1 tutorial stubs — 88 (ask: GO)

| Field | Value |
|---|---|
| Quests | **2584, 2586, 2589–2606, 2609, 2612, 2614, 2616, 2620–2683** (튜토리얼 1–100, cat 45, w_gweonid_forest_1) |
| Shape | zero quest_components / zero acts / zero accept surfaces / zero refs; same legacy tutorial stub family as §2 |
| Verdict | Josh 2026-08-09 (delegated Kimi nightwatch, t_724ccab2 comment) — **GO drop** (M2a §7 zero-component-shell pattern) |
| Drop action | delete 88 quest_contexts (0 comps, 0 acts — no downstream rows); remove nothing from allowlist (never allowlisted) |
| Restore pointer | legacy tutorial skeleton — rebuild target if a 1.2-era tutorial line is re-added |
| Risk | LOW; sole ref = skill 12586 kind-27 → 2640 (inert after drop) |

### 9b. A2 unit-req/dummy specials — 4 (ask: NO-GO keep)

| Field | Value |
|---|---|
| Quests | **315** (스킬 연결용 — "do not delete"), **1728** (두다드 스킬 사용전용 — "do not delete"), **2046** (Unit Req Dummy), **1576** (dummy) |
| Shape | zero components; allowlist-masked (QuestSanityVerifier.cs:87-93 documents 315/1728 as client-side skill/doodad link hooks); 1728 ← sphere 567; 2046 ← spheres 595/721/770/780/1096/1172 |
| Verdict | Josh 2026-08-09 — **NO-GO (KEEP)** — allowlist hooks + live sphere accepts |
| If dropped anyway | +7 sphere_quests rows pruned; 4 ids removed from allowlist (regressions re-report at WARN) |
| Restore pointer | n/a if kept |

### 9c. B1+D1 Dwarf main-story skeleton — 60 (ask: GO, one block)

| Field | Value |
|---|---|
| Quests | B1 (ltd, engine-stuck): **5040, 5773, 5781–5811** · D1 (placeholders): **3484–3490, 3492–3502, 3562–3563, 3565–3568, 3992, 4408, 5980** — all cat 93 메인스토리_dummy, w_gweonid_forest_1 |
| Shape | one self-contained linear chain 5980→3484→…→5811 (59 kind-31 gates, all owners in-set); B1 half = ltd + zero report acts + Score=0 → can never leave Progress (QuestStep.cs:127-129; HackFix ineligible) |
| Verdict | Josh 2026-08-09 — **GO drop as ONE block** (B1+D1, 60 quests) |
| Drop action | −60 quest_contexts / −242 quest_components / −18 acts; prune 59 kind-31 unit_reqs + ~149 owned unit_reqs + skill gate (5806 ← skill 12050) |
| Restore pointer | chain topology is the blueprint for a future real Dwarf main story (numbered dummy slots 201–605) |
| Risk | MEDIUM — the block is self-contained, but the impl must prune the full gate set (drift-checked) |

### 9d. B2 title quests — 3 (ask: GO)

| Field | Value |
|---|---|
| Quests | **8000001 설립자 / 8000002 여행자 / 8000003 선구자** (cat 82, ms 8000001) |
| Shape | ltd='t', Score=0, 6 comps / 15 Supply-family acts, zero report acts → engine-stuck; titles unobtainable |
| Verdict | Josh 2026-08-09 — **GO drop** |
| Restore pointer | title grants must be rebuilt via another surface if ever wanted |
| Risk | LOW |

### 9e. B3 cat-1 test/unused — 3 (ask: GO)

| Field | Value |
|---|---|
| Quests | **1835 (테스트), 1836 미사용, 1895 미사용** |
| Shape | ltd, Score=0, 8 comps / 0 acts; 1836 gates on 1832 (external) — dependent, not depended-on |
| Verdict | Josh 2026-08-09 — **GO drop** |
| Risk | LOW |

### 9f. B4 Cradle act-less — 1 (ask: GO)

| Field | Value |
|---|---|
| Quest | **5678** 골치 아픈 주문서 (cat 27, 태초의 요람) |
| Shape | Start/Progress/Ready comps, zero acts, ltd, Score=0 → engine-stuck (M2a §6 A2 shape) |
| Verdict | Josh 2026-08-09 — **GO drop** |
| Risk | LOW |

### 9g. NOT dropped — WI-11b sweep population (56)

D1 was folded into the Q3 drop (§9c), so the deferred sweep population is
**56** (was 83 pre-ruling). D4 must NOT be dropped (live external gate
1405→1404; 8 loot_quest_id refs).

- **D2 old-Sunny (13):** 1883–1884, 1886–1887, 1912–1919, 1922
- **D3 tutorial sphere steps (12):** 2585, 2587–2588, 2607–2608, 2610–2611, 2613, 2615, 2617–2619
- **D4 real content (22):** 1394, 1397, 1401–1402, 1404, 1485, 5307–5308, 5313–5314, 5459, 5698–5699, 5999, 6222–6223, 6229, 6250–6251, 6314, 6355, 8000004
- **D5 test/dummy (9):** 1097, 1101, 1128, 1132, 1148, 1204, 2971, 4897, 5649

Sweep card: t_8ec705f0 (Tai). D2/D3/D5 = drop candidates pending WI-11b checks.

## How to check if an id is in this register

```bash
grep -n "745\|1421\|1391\|1533\|2140\|1954\|1867\|2148\|3748\|5575" scorecard-explorations/dropped-content-register.md
```

Before filing any future quest-defect card, check this file — a "missing"
quest may be here by decision, not by accident.
