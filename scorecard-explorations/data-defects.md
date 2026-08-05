# M1 Data-Defect Backlog — classification of verifier census findings (fix vs drop)

**Author:** Tai (evidence: hx-researcher, t_7416ea48) · **Date:** 2026-08-04
**Data:** prod `compact.sqlite3` (md5 `78b3bdbf038db3b927056106efdf91af`, identical on
192.168.0.165 and /tmp copies) · **Census source:** prod boot 2026-08-04 03:22:41
`[QuestSanity] SUMMARY: 5 ERRORS, 128 warnings, 4 info across 4775 quests / 17720
components / 19047 acts` (BUG-007 verifier, `AAEmu.Game/Core/Managers/QuestSanityVerifier.cs`).
**Scope:** NO code changes — evidence + fix-vs-drop recommendations per row.

## 0. Task body vs actual census (corrections)

The card enumerated a subset from an earlier read. The live census + DB cross-check show:

| Card said | Census actually says |
|---|---|
| COMPONENT_NEXT_MISSING: 776, 777 | **3 quests: 330, 776, 777** (card missed 330 — golden-route quest) |
| ACT_REF_MISSING_QUEST: 2145 → 2146 | **2 quests: 1960 → 1961, 2145 → 2146** (card missed 1960) |
| QUEST_NO_START: 1533–1548 (16 quests) | **23 quests**: 1533, 1535–1549, 1551–1554, 1640, 1830, 1831 (1534/1550 have no context row at all; 1549 + 1551–1554 + 1640/1830/1831 were missed) |
| QUEST_NO_COMPONENTS: 1391 | **96 quests**: 315, 1391, 1576, 1728, 2046, 2148–2229 (82), 3748, 3750–3757 (10) |
| Orphaned quest_contexts: 8 (745, 1421, 1954–1958, 2140) | **28 orphaned quest_context_ids** (116 orphan `quest_components` rows). The 8 listed are all verified; 20 more exist (1697, 1961, 2141–2143, 2146, 3233–3236, 5133, 5765, 6014, 6015, 6019, 6230, 6350, 6371, 6420, 6635) |

## 1. Verdict legend

- **(a) fixable data** — concrete rows (table + id) to UPDATE/DELETE/INSERT; no engine change.
- **(b) engine gap** — missing/incorrect act-handler or verifier behavior.
- **(c) drop** — dead content; delete or explicitly ignore. Reason given per row.

## 2. Summary table

| Finding (severity) | Quest(s) | What the 1.2 data says | Verdict | Action |
|---|---|---|---|---|
| COMPONENT_NEXT_MISSING (Error) | 330, 776, 777 | `next_component` refs an id that exists in no quest | **(a)** cosmetic; engine never reads the field | 3 UPDATEs (below) or ignore; verifier severity should be Warn |
| ACT_REF_MISSING_QUEST (Error) | 1960 → 1961, 2145 → 2146 | targets are orphaned contexts (components intact) of an abandoned cat-34 crafting chain; chain roots gated on orphans (1958/2143) → nothing in the chain is reachable | **(c)** drop chain; targets unrecoverable-by-use | delete dangling acts + orphan rows, or restore 6 contexts if chain ever wanted |
| QUEST_NO_START (Warn) | 23 (see §5) | single Reward comp (kind 8) with SupplyCopper+SupplyExp; legacy 1.0-era tutorial shells, zone 1 Gweonid, no accept path, no live deps | **(c)** drop | delete 23 contexts + their comps |
| QUEST_NO_COMPONENTS (Warn) | 96 (see §6) | 82 "하다보니(reserve)" placeholders; 5 named shells (315 do-not-delete, 1391, 1576 dummy, 1728 do-not-delete, 2046 dummy); 10 Hadir-farm cutscene (instance 169) | **(c)** drop (315/1728: keep if respecting the do-not-delete label; 2148–2229: keep-as-reserved acceptable) | delete or verifier allowlist |
| Orphaned quest_contexts (Info count: 116 rows / 28 ids) | 745, 1421, 1697, 1954–1958, 1961, 2140–2143, 2146, 3233–3236, 5133, 5765, 6014, 6015, 6019, 6230, 6350, 6371, 6420, 6635 | full component/act shapes survive; context row (and texts) deleted. Real cross-impact: **745 blocks quest 2951's Supply step**; **5133/6420 have `item_accept_quests` rows** (items grant quests that don't exist) | **(c)** drop all 28; two cleanup fixes in live rows | delete orphan comps/acts/unit_reqs + 2 item_accept rows; repoint-or-delete 2951's kind-32 gate |

**Golden route check:** none of the census quests are on the Solzreed golden route or its
excluded list, with one exception: **quest 330 IS on the golden route (step 3, PASS)** — its
COMPONENT_NEXT_MISSING is the only census finding touching route content, and it has zero
runtime impact (harness PASS; field unused by the engine). 1533–1554 are **not** Solzreed
(kind 31 chain? no): zone 1 `w_gweonid_forest_1` (legacy Nuian starter), category 28
(tutorial); not in zones 9/124/125, not in the golden route, not on its excluded list.

## 3. COMPONENT_NEXT_MISSING — 330, 776, 777 (5 ERR total = 3 here + 2 in §4)

| Quest | Name | Zone / Lvl / Cat | Component | next_component | Correct target |
|---|---|---|---|---|---|
| 330 | 나를 찾는 사람 (Someone looking for me) | 125 `w_solzreed_3` / 1 / 4 | 1520 (Start, kind 2) | 3543 (exists in no quest) | 1521 (Ready) |
| 776 | 해적과 오크 (Pirates and Orcs) | 8 `e_sunrise_peninsula_1` / 23 / 14 | 3480 (Start) | 4370 (exists in no quest) | 3482 (Progress) |
| 777 | 오크의 그늘 아래 (Under the Orc's shadow) | 8 / 23 / 14 | 3488 (Progress, kind 4) | 3487 (exists in no quest) | 11591 (Ready) or 0 |

Evidence this is cosmetic, not an engine gap:
- `QuestComponentTemplate.cs:17` — "NextComponent feels like it is a deprecated field in
  the compact.sqlite3, the only 3 references doesn't seem to make any sense".
- `NextComponent` is read only by the loaders (`QuestManager.cs:516,683`); it is never used
  in progression. `QuestStep.cs` organizes steps by `QuestComponentKind` and runs the acts
  of the current step's components (`RunComponents`), advancing on act results — no
  next_component traversal anywhere.
- Golden-route harness: quest 330 = **PASS** (START/READY/REWARD) despite the defect.

Cross-references (all satisfiable today, so no player impact):
- 1688 해적의 소굴로 (zone 8, lvl 23, cat 14) Start comp 8060: kind-31 (must have completed **776**) — fine, 776 completes via acts.
- 2522 그라일렌트 경 (zone 141, lvl 12) Supply comp 16453: kind-37 (777 not completed + active) — fine.
- 3600 사라진 사람들 (zone 26, lvl 37) Ready comp 14852: kind-32 (776 in progress) — data quirk, satisfiable.

Fix (a): `UPDATE quest_components SET next_component=1521 WHERE id=1520;`
`UPDATE quest_components SET next_component=3482 WHERE id=3480;`
`UPDATE quest_components SET next_component=11591 WHERE id=3488;`
Recommended: yes — 3 rows, silences 3 boot Errors. Verifier follow-up (BUG-007, not this
card): downgrade COMPONENT_NEXT_MISSING to Warn; the field is deprecated.

**Mechanism decision (Tai, 2026-08-04, t_25744130):** quest template data is loaded from
compact.sqlite3 at startup (QuestManager.Load, `SQLite.CreateConnection`), so a MySQL
`SQL/updates` file cannot correct the census. The 3 fixes landed as a **startup sanitizer**
— `AAEmu.Game/Core/Managers/QuestDataOverlay.cs` (additive in-memory overlay applied by
QuestManager.Load right after `LoadQuestComponents`; drift rows Warn, never throw) —
branch `fix/verifier-data-overlay`. SQL/updates stays the mechanism for MySQL-hosted data.

## 4. ACT_REF_MISSING_QUEST — 1960 → 1961, 2145 → 2146

| Quest | Name | Zone / Lvl / Cat | Accept path | Reward comp's ConAcceptComponent |
|---|---|---|---|---|
| 1960 | 여행자의 조잡한 공구상자를 설치해보세요 (install the traveler's crude toolbox) | 1 `w_gweonid_forest_1` / 1 / 34 | Start comp 9792: self-ConAccept + AcceptItemGain item 15589 장작 (firewood) | comp 9794 act 75 → **1961 (no context row)** |
| 2145 | 다용도 옷감을 만들어보세요 (make versatile fabric) | 1 / 1 / 34 | Start comp 9925: self-ConAccept + AcceptItemGain item 16234 거미줄 (spider web) ×10 | comp 9927 act 89 → **2146 (no context row)** |

These are links in an abandoned cat-34 crafting chain (zone 1 Gweonid):

```
1954 → 1955 → 1956 → 1957 → 1958 →(1959 live)→ 1960 → 1961 → 2140 → 2141 → 2142 → 2143 →(2144 live)→ 2145 → 2146
```
(arrows = Reward comp's ConAcceptComponent auto-accept; Start comps gate on the previous
quest via unit_reqs kind 31 = CompleteQuestContext.)

Why the whole chain is dead (the deciding evidence):
- **1959's** Start comp 9789 gate: kind 31 → complete **1958** (orphan) → 1959 can never be accepted.
- **1960's** Start comp 9792 gate: kind 31 → complete **1959** → unreachable (1959 dead).
- **2144's** Start comp 9922 gate: kind 31 → complete **2143** (orphan) → 2144 can never be accepted. Its accept item 16240 also has no `items` row.
- **2145's** Start comp 9925 gate: kind 31 → complete **2144** → unreachable (2144 dead).
- Sibling block 2148–2229 is all "하다보니(reserve)" empty templates — the ids were reserved for a content block that was never filled; the orphaned mid-chain contexts are the same abandoned block.

Runtime behavior if the acts ever fired: `QuestActConAcceptComponent.RunAct` returns true
unconditionally (M1-2 watch item) → no crash; the missing target is silently skipped.

Verdict: **(c) drop the chain** — 1961, 2146, 1954–1958, 2140–2143 (13 orphan contexts) +
the dangling acts 75/89 on the live quests. The components/acts of every orphan are intact,
so the block is **recoverable** if the crafting chain is ever wanted (re-INSERT the 13
`quest_contexts` rows + texts; gates are satisfiable once the chain roots exist). Alternative
minimal action: delete only the two dangling acts (`quest_act_con_accept_components` ids 75,
89 + their `quest_acts` rows) — silences the 2 ERR while leaving the dead data in place.

## 5. QUEST_NO_START — 23 quests (all legacy tutorial shells)

1533, 1535–1549, 1551–1554, 1640, 1830, 1831. Data: every one has exactly one component of
kind 8 (Reward) carrying `QuestActSupplyCopper` + `QuestActSupplyExp` (1830/1831 미사용
"UNUSED" have empty components; 1831 additionally has Progress+Ready comps, all act-less).
Names are the Korean 1.0-era tutorial sequence: 튜토리얼_아이템_획득 … 14. 메인퀘저널 plus
1640 "10. 죽음" (death) — a numbered 1–14 tutorial step list.

- Zone 1 `w_gweonid_forest_1` — the legacy Nuian starter; the 1.2 opening uses the Solzreed
  arrival quests (250/251/330/329/…, golden route) instead. Not zones 9/124/125.
- **No accept path**: no `item_accept_quests`, `doodad_func_quests`, `accept_quest_effects`,
  or accept-component references; no kind-31 prereq refs from live quests (the kind-30/23/35
  unit_reqs pointing at these ids are NoBuffTag/TargetBuffTag/AreaSphere refs to
  buff_tags/spheres that exist — id collisions, not quest deps; the one kind-35 owner
  (comp 18182) has no `quest_components` row).
- 1534 and 1550 have no context row AND no components (pure id gaps — nothing to drop).

Verdict: **(c) drop** — dead legacy content. Delete the 23 contexts + their components/acts
(safe: nothing references them). If a 1.2-era tutorial is ever rebuilt, these shells are the
skeleton to reuse.

## 6. QUEST_NO_COMPONENTS — 96 quests

| Group | Ids | Data / intent | Verdict |
|---|---|---|---|
| Reserve block | 2148–2229 (82) | all named 하다보니(reserve), cat 34, zone 1, lvl 1, zero components | **(c)** drop, or keep-as-reserved — zero refs (the kind-35 unit_reqs from lvl-50 Ostera quests are AreaSphere gates on real spheres 2148–2189) |
| Do-not-delete shells | 315 (스킬 연결용 퀘스트), 1728 (두다드 스킬 사용전용) | cat 37, zone 1, lvl 0, zero components, zero refs; names warn "do not delete" | **(c)** drop is safe; **recommend keep** (respect the original devs' label, zero cost, and 315/1728 may be client-side skill/doodad link hooks) |
| Dummy shells | 1391 마을을 지켜라 (cat 27, zone 0, lvl 0), 1576 "dummy" (cat 1), 2046 "Unit Req Dummy" (cat 37) | no refs (1391's kind-35 ref is sphere 1391, which exists) | **(c)** drop |
| Hadir farm cutscene | 3748, 3750–3757 (10) | 하디르의 농장 인던연출 3–10, zone 169 `instance_hadir_farm`, cat 63, repeatable, zero components, zero refs | **(c)** drop (instance content, unreachable in M1; cutscene quests are driven by dungeon scripts we don't run) |

## 7. Orphaned quest_contexts — 28 ids (116 orphan `quest_components` rows)

All orphans: no `quest_contexts` row AND no `quest_context_texts` row (names deleted with
the templates); components + acts survive. None are referenced by any accept path except as
noted. Full inventory:

| Orphan | Components | Shape | External refs | Verdict |
|---|---|---|---|---|
| **745** | 10 | full quest (Start, Supply, 5×Progress, Ready ReportNpc, Fail, Reward) | **quest 2951 윈란드의 연애편지 (zone 24 `e_ancient_forest`, lvl 29, cat 26) Supply comp 12913: kind-32 gate (745 must be in progress)** — 745 can never be in progress → **2951's Supply step stalls forever** | **(c)** drop 745 + **delete/repoint unit_reqs row** (owner 12913) to unblock 2951 |
| **1421** | 8 | full quest (Start, 4×Progress w/ CheckTimer+ItemUse+Talk+Sphere, Supply, Ready, Fail) | none (kind-35 ref = sphere 1421, exists) | **(c)** drop |
| **1954–1958** | 4/4/3/3/3 | crafting-chain links (cat 34 block, §4) | intra-chain only; 1959's gate → 1958 | **(c)** drop (or restore with the chain) |
| **1961, 2140–2143, 2146** | 3 each | crafting-chain links | 1960→1961, 2145→2146 dangling acts; 2140's gate → 1961; 2141→2140; 2142→2141; 2143→2142; 2146's gate → 2145 | **(c)** drop (or restore with the chain) |
| **1697** | 5 | full quest | none | **(c)** drop |
| **3233–3236** | 4 each | full quests (Start/Progress/Ready/Reward) | intra-chain kind-31: 3234→3233, 3236→3235 | **(c)** drop |
| **5133** | 3 | full quest | **item_accept_quests: item 26756 수습 곡예사의 증표 (apprentice acrobat's token) grants quest 5133** → using the item silently does nothing | **(c)** drop + delete the `item_accept_quests` row |
| **5765** | 3 | full quest | kind-32 owners 1973/1974 have no `quest_components` rows (dead) | **(c)** drop |
| **6014, 6015** | 4/4 | full quests | mutual kind-36 (ExceptComplete) between the two; both orphaned | **(c)** drop |
| **6019, 6350** | 3 each | Start/Progress/Ready | none | **(c)** drop |
| **6230** | 5 | full quest | none | **(c)** drop |
| **6371** | 10 | big quest (Start, 7×Progress, Ready, Reward) | none | **(c)** drop |
| **6420** | 4 | full quest | **item_accept_quests: item 34820 (which itself has no `items` row) grants quest 6420** | **(c)** drop + delete the `item_accept_quests` row |
| **6635** | 3 | full quest | none | **(c)** drop |

Cleanup SQL shape (for the fix card, if approved):
```sql
-- 1) delete orphan component rows (116) + their orphan act rows (verify each side first)
DELETE FROM quest_acts WHERE quest_component_id IN
  (SELECT id FROM quest_components qc LEFT JOIN quest_contexts q ON q.id=qc.quest_context_id WHERE q.id IS NULL);
DELETE FROM quest_components WHERE quest_context_id NOT IN (SELECT id FROM quest_contexts);
DELETE FROM unit_reqs WHERE owner_type='QuestComponent' AND owner_id NOT IN (SELECT id FROM quest_components);
-- 2) dangling cat-34 accept acts
DELETE FROM quest_acts WHERE id IN (14072, 14121);        -- act rows 75 / 89 (verified 2026-08-04)
DELETE FROM quest_act_con_accept_components WHERE id IN (75, 89);
-- 3) item-accept rows for orphans
DELETE FROM item_accept_quests WHERE quest_id IN (5133, 6420);
-- 4) unblock quest 2951 (remove the impossible gate; row id 16000 verified)
DELETE FROM unit_reqs WHERE id = 16000;
-- 5) optional: drop the 23 tutorial + 96 empty contexts
-- DELETE FROM quest_acts WHERE quest_component_id IN (SELECT id FROM quest_components WHERE quest_context_id IN (...));
-- DELETE FROM quest_components WHERE quest_context_id IN (...);
-- DELETE FROM quest_context_texts WHERE quest_context_id IN (...);
-- DELETE FROM quest_contexts WHERE id IN (...);
```
Caution: `compact.sqlite3` is a READ-ONLY reference (upstream alignment rule 3) — these
fixes land as an **additive overlay**, never by editing the reference file. Mechanism
decision (t_25744130): sqlite-sourced data (quest templates) → startup sanitizer
(`QuestDataOverlay`, see §3); MySQL-hosted data → `SQL/updates` file.

## 8. Verifier (BUG-007) follow-up suggestions — engine-side, not this card

1. COMPONENT_NEXT_MISSING: severity Error → **Warn** (field deprecated, engine never reads it).
2. DATA_ORPHAN_COMPONENTS (Info): emit the orphan **quest_context_id list** so the census is
   self-documenting (this report had to reconstruct the 28 ids by hand).
3. Consider an allowlist for intentionally-empty contexts (the 82 "reserve" + do-not-delete
   shells) so QUEST_NO_COMPONENTS Warns are actionable by exception rather than by count.

## 9. Bottom line

- **Fixable data (cheap, do it):** 3 `next_component` UPDATEs (330/776/777); delete/repoint
  2951's kind-32 gate; delete 2 `item_accept_quests` rows (5133/6420).
- **Dead content (drop):** the whole cat-34 crafting chain block (13 orphans + 4 unreachable
  live shells + dangling acts), 23 tutorial shells, 96 empty contexts (or allowlist the
  reserve/do-not-delete ones), and all 28 orphans.
- **Engine gaps (b): none identified** — every finding traces to data, not handler code.
- **No upstream PR** (lane gate). This report lands on branch `data-defect-classification`
  (fork only); STATUS.md/SCORECARD flow via Nei.

## Appendix — reproducible queries (prod box)

```sql
-- 5 errors, verbatim (2026-08-04 boot):
--   COMPONENT_NEXT_MISSING: 330 c1520->3543 · 776 c3480->4370 · 777 c3488->3487
--   ACT_REF_MISSING_QUEST: 1960 act75->1961 · 2145 act89->2146
-- orphaned quest_context_ids (28):
SELECT qc.quest_context_id, COUNT(*) FROM quest_components qc
  LEFT JOIN quest_contexts q ON q.id=qc.quest_context_id WHERE q.id IS NULL GROUP BY 1;
-- kind-31 completion gates referencing census quests:
SELECT owner_id, value1 FROM unit_reqs WHERE kind_id=31 AND owner_type='QuestComponent'
  AND value1 IN (776,1955,1956,1957,1958,1960,1961,2140,2145);
-- item grants for orphan quests:
SELECT * FROM item_accept_quests WHERE quest_id IN (5133,6420);
```
