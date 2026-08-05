# unit_reqs layer audit — the 20 missing quest contexts and the 13 quests they gate

**Author:** Tai (evidence: hx-researcher, t_c87c5deb) · **Date:** 2026-08-05
**Data:** prod `compact.sqlite3` (md5 `78b3bdbf038db3b927056106efdf91af` = canonical 1.2,
verified on 192.168.0.165) · **Scope:** NO code changes — classification (fix-vs-drop)
of the 20 missing quest contexts referenced by QuestComponent-owned `unit_reqs` rows.
Cross-checks the same surfaces as the reachability audit (t_5600bbac,
`scorecard-explorations/quest-reachability.md` §4) and extends the M1 data-defect
methodology (`scorecard-explorations/data-defects.md`).

## 0. One-question answer (TL;DR)

**Of the 20 missing contexts, 7 are id-space collisions (the number is a live
sphere/npc/doodad/ai_event/item id, never a quest) and 13 are orphaned quest templates
(components survive, context row deleted) — all 13 belonging to chains already ruled
dead by data-defects.md. Zero restore-from-data candidates: no context survives for
any of the 20. Recommendation: PRUNE the gate rows that make real quests unreachable
(5 endgame quests gated on 6586), LEAVE the ExceptComplete rows (vacuous, no player
impact), and DO NOT treat any of the 13 gated quests as route content — none is on the
golden route or its excluded list.**

| Class | Count | Missing ids | Verdict |
|---|---|---|---|
| (c) id-space collision | 7 | 1882, 1921, 1832, 2053, 1848, 14, 6586 | PRUNE gate rows (5 endgame quests unblocked); row 20455 (→14) harmless, leave |
| (b) orphaned template, dropped chain | 13 | 1955–1958, 1961, 2140–2143, 3233, 3235, 6014, 6015 | PRUNE gate rows — chain already ruled drop in data-defects.md |
| (a) real quest that should exist | 0 | — | none restorable from data (no context row survives) |

## 1. The 20 missing contexts — full table

Legend: **collision** = the id is a live non-quest entity and there is NO quest body at
all (0 `quest_components`, 0 `quest_context_texts` rows); **orphan** = a full quest body
survives under a `quest_context_id` with no context row (same shape as the 28 orphans in
data-defects.md §7).

| # | missing context | body | owner unit_reqs (row → owner quest) | gated quest(s) | classification | verdict + recommendation |
|---|---|---|---|---|---|---|
| 1 | 1882 | 0 comps | 16832 → 1883 | 1883 (구 불볕황야)불길한 결말 | **collision** — id = sphere 1882 (피 묻은 손 포로를 감시하는 눈), npc 1882 (촌장 제프리), ai_event 1882, doodad 1882 (네손가락 천막) | PRUNE row 16832 (impossible gate on a cat-1 dummy shell; see §4) |
| 2 | 1921 | 0 comps | 16853 → 1922 | 1922 (구 불볕황야)장로의 지혜 | **collision** — id = sphere 1921 (파괴의 요람 종유석), npc 1921 (포비드네), ai_event 1921, skill-14553 kind-15 doodad-range ref (doodad 1921) | PRUNE row 16853 (dead shell) |
| 3 | 1832 | 0 comps | 18500 → 1836 | 1836 미사용 ("UNUSED") | **collision** — id = sphere 1832 (숲지기 알터의 사랑), npc 1832 (피 묻은 손 암살자), ai_event 1832, doodad 1832 (매사냥긴풀) | PRUNE row 18500 — owner is literally named 미사용 |
| 4 | 2053 | 0 comps | 18576 → 2054 | 2054 (구 불볕황야)가려움의 원인 | **collision** — id = sphere 2053 (가루다 서식지 뾰족 바위), npc 2053 (마빈), ai_event 2053, doodad 2053 (불화살지역) | PRUNE row 18576 (see §4: 2054 has a live loot edge) |
| 5 | 1848 | 0 comps | 18578 → 2056 | 2056 (구 불볕황야)보답의 마음 | **collision** — id = sphere 1848 (매사냥고원 바람돌), npc 1848 (먼지 바람 정령), ai_event 1848, doodad 1848 (빛나는동굴약초) | PRUNE row 18578 (see §4: 2056 has a live loot edge) |
| 6 | 14 | 0 comps | 20455 → 38 | 38 엘프의 셈법 | **collision** — id = npc 14 (Coward Villager), ai_event 14, npc_group 14, **sphere_quests row 280** (sphere triggers quest 14); id 14 is also used as a LEVEL value in 45 kind-1 unit_reqs rows | **NO ACTION** — gate is kind 36 (ExceptComplete): 14 can never complete ⇒ vacuously passable. 38 is fully wired (accept NPC 5931, report NPC 1464, loot item 24325) and NOT actually blocked |
| 7 | 6586 | 0 comps | 46598/46603/46609/46613/46619 → 6587/6589/6592/6594/6597 | **5 real endgame quests** (cat 114 God's Shield chain, zone 193, lvl 51) | **collision** — id = npc 6586 (토벌대장 캐치미), doodad 6586 (핏물먹이 주술사의 격류방출) | **PRUNE the 5 rows — the only gate with real blast radius.** 6586's context and body are gone; id reused by npc/doodad. Pruning unblocks the 5 stage-1 quests (they have accept-NPC wiring: 사우락/자일/그렌델) |
| 8 | 1955 | 4 comps | 19197 → 1956 (orphan) | 1956 (orphan) | **orphan** — cat-34 crafting chain link; qac rows 56/69 | PRUNE row 19197 (chain dropped, data-defects §4) |
| 9 | 1956 | 3 comps | 19198 → 1957 (orphan) | 1957 (orphan) | **orphan** — chain link; qac 58/70; sphere_accept_quest_quests row 3 (sphere accepts 1956 — dangling) | PRUNE row 19198 |
| 10 | 1957 | 3 comps | 19205 → 1958 (orphan) | 1958 (orphan) | **orphan** — chain link; qac 60/71 | PRUNE row 19205 |
| 11 | 1958 | 3 comps | 19201 → 1959 | **1959 장작을 모아보세요** (real chain quest) | **orphan** — chain link; qac 61/72; gates 1959 which is a live-but-unreachable chain quest | PRUNE row 19201 (1959 is itself only reachable via the dropped chain) |
| 12 | 1961 | 3 comps | 19206 → 2140 (orphan) | 2140 (orphan) | **orphan** — chain link; qac 75/76 | PRUNE row 19206 |
| 13 | 2140 | 3 comps | 19207 → 2141 (orphan) | 2141 (orphan) | **orphan** — chain link; qac 77/78; **also** sphere 2140 (신기루 섬 제작대 안내) + doodad 2140 (핏자국) + **quest_components.id 2140 = comp of real quest 541** | PRUNE row 19207 — note the component-id reuse (745/12913 pattern, §6) |
| 14 | 2141 | 3 comps | 19208 → 2142 (orphan) | 2142 (orphan) | **orphan** — chain link; qac 79/80; also sphere/doodad 2141 | PRUNE row 19208 |
| 15 | 2142 | 3 comps | 19209 → 2143 (orphan) | 2143 (orphan) | **orphan** — chain link; qac 81/82; also item 2142 (인도자의 열매), npc 2142 (바스렐리), sphere 2142, doodad 2142 (UCC 액자), comp of quest 541, kind-10 OwnItem row 45718 (legit item ref) | PRUNE row 19209 (kind-10 row 45718 is a valid item gate — DO NOT touch) |
| 16 | 2143 | 3 comps | 19210 → 2144 | **2144 거미줄을 모아보세요** (real chain quest) | **orphan** — chain link; qac 83/84; also sphere 2143 (제국선인의 대사), doodad 2143 (두근두근나무), comp of quest 541 | PRUNE row 19210 (2144 reachable only via dropped chain) |
| 17 | 3233 | 4 comps | 27692 → 3234 (orphan) | 3234 (orphan) | **orphan** — isolated 2-quest chain; comp of real quest 842 (여우 인간의 변신) | PRUNE row 27692 (both sides dead) |
| 18 | 3235 | 4 comps | 27695 → 3236 (orphan) | 3236 (orphan) | **orphan** — isolated chain; comp of real quest 843 (연인을 잃은 남자) | PRUNE row 27695 (both sides dead) |
| 19 | 6014 | 4 comps | 44930 (kind 36, →6015), 44931 (kind 36, →6014, self) on comp 25900 of quest 6014 (orphan) | 6014 (orphan, self) | **orphan** — mutual ExceptComplete pair; npc 6014 (하카레레); comp of real quest 1180 | NO ACTION needed — owner quest never loads (no context row); rows inert. Prune only if cleaning orphans |
| 20 | 6015 | 4 comps | 44930 (kind 36 →6015) | 6015 (orphan) | **orphan** — same pair; npc 6015 (요하르); comp of real quest 1180 | NO ACTION needed (same as #19) |

Count check: 24 QuestComponent-owned rows → 20 distinct missing contexts → 13 distinct
REAL (context-having) owner quests: 1883, 1922, 1836, 2054, 2056, 1959, 2144, 38,
6587, 6589, 6592, 6594, 6597. The remaining 11 rows' owners are themselves orphaned
contexts (1956, 1957, 1958, 2140–2143, 3234, 3236, 6014) — dead-to-dead edges.

## 2. Classification evidence — the collision class (7)

All seven have **zero** `quest_components`, **zero** `quest_context_texts`, **zero**
`quest_acts` — there is no quest residue of any kind. Their ids are provably owned by
other entity tables (id-space collision, exactly the 745/12913 class documented in
quest-reachability.md §4 caveat):

| missing id | spheres.id | npcs.id | ai_events.id | doodad_almighties.id | items.id | notes |
|---|---|---|---|---|---|---|
| 1882 | 피 묻은 손 포로를 감시하는 눈 | 촌장 제프리 | OnFriendSeen | 네손가락 천막 | — | 4-way collision |
| 1921 | 파괴의 요람 떨어지는 종유석 | 포비드네 | OnFriendNearSeen | — | — | + skill 14553 kind-15 DoodadRange → doodad 1921 |
| 1832 | 숲지기 알터의 사랑 | 피 묻은 손 암살자 | OnFriendSeen | 매사냥긴풀 | — | 4-way |
| 2053 | 가루다 서식지의 뾰족 바위 | 마빈 | OnClientGreeting | 불화살지역 | — | 4-way |
| 1848 | 매사냥고원_바람돌 | 먼지 바람 정령 | OnEnemySeen | 빛나는동굴약초 | — | 4-way |
| 14 | — | Coward Villager | OnClientGreeting | — | — | + npc_group 14, sphere_quests row 280, **45 kind-1 LEVEL rows** (min-level 14) |
| 6586 | — | 토벌대장 캐치미 | — | 핏물먹이 주술사의 격류방출 | — | the only collision with real quest blast radius |

## 3. Classification evidence — the orphan class (13)

Thirteen ids carry full component bodies (3–10 comps each) with no context row and no
texts — the same signature as the 28 orphaned contexts in data-defects.md §7:

- **cat-34 crafting chain (9):** 1955, 1956, 1957, 1958, 1961, 2140, 2141, 2142, 2143 —
  the chain `1954 → 1955 → 1956 → 1957 → 1958 →(1959)→ 1960 → 1961 → 2140 → 2141 →
  2142 → 2143 →(2144)→ 2145 → 2146` (arrows = qac auto-accept; Start comps gate on the
  previous link via kind 31). data-defects.md §4 verdict stands: **drop the chain**
  (recoverable-if-wanted: bodies intact, re-INSERT contexts). The two "real" gated
  quests 1959/2144 are mid-chain links — only reachable through the dropped chain, so
  pruning their gates changes nothing for players.
- **isolated 2-quest chains (4):** 3233→3234, 3235→3236 (data-defects §7: drop),
  6014⇄6015 mutual ExceptComplete pair (data-defects §7: drop; owner never loads).

## 4. The 13 gated real quests — zone / kind / route status

| Quest | Name | cat (name) | zone (key) | lvl | accept wiring | on any route? |
|---|---|---|---|---|---|---|
| 1883 | (구 불볕황야)불길한 결말 | 1 (dummy) | 1 w_gweonid_forest_1 (129) | 0 | **none — 0 acts** | NO |
| 1922 | (구 불볕황야)장로의 지혜 | 1 (dummy) | 1 (129) | 0 | **none — 0 acts** | NO |
| 1836 | 미사용 (UNUSED) | 1 (dummy) | 1 (129) | 0 | **none — 0 acts** | NO |
| 2054 | (구 불볕황야)가려움의 원인 | 1 (dummy) | 22 e_sunny_wilderness_1 (157) | 10 | 1 act (ObjItemGather) + **live loot edge item 15887 하피 깃털 → loot_quest_id 2054** | NO |
| 2056 | (구 불볕황야)보답의 마음 | 1 (dummy) | 22 (157) | 10 | SupplyItem+ObjItemGather + **live loot edge item 15889 불꽃서슬 부족의 요리 → loot_quest_id 2056** | NO |
| 1959 | 장작을 모아보세요 | 34 (오늘 할 일) | 1 (129) | 1 | full acts; chain link | NO |
| 2144 | 거미줄을 모아보세요 | 34 (오늘 할 일) | 1 (129) | 1 | full acts; chain link | NO |
| 38 | 엘프의 셈법 | 9 (그위오니드 숲) | 1 (129) | 4 | **fully wired**: accept npc 5931 기억술사 셀레스트 (1 spawner), report npc 1464 수련사 토라린 (1 spawner), **loot item 24325 채무 증서 → loot_quest_id 38**, gates: lvl≥2, complete 64 (real), complete 52 (real), ExceptComplete 14 (vacuous) | NO — zone 1 legacy Gweonid, not Solzreed |
| 6587 | 강인한 정신력을 얻기 위해 | 114 (신의 방패가 되는 길) | 193 o_land_of_sunlights (275) | 51 | accept npc 14927 훈련교관 사우락; kill 10× npc 14909 황혼의 그리핀 | NO — endgame |
| 6589 | 들녘뿌리 퇴치 1단계 | 114 | 193 (275) | 51 | AcceptNpcKill 1299/1300 (npcs 12668/12669) | NO |
| 6592 | 들녘을 떠도는 에페리움 망령 전사 | 114 | 193 (275) | 51 | accept npc 14928 훈련교관 자일; kill 10× npc 14910 | NO |
| 6594 | 이끼 슬라임 퇴치 1단계 | 114 | 193 (275) | 51 | AcceptNpcKill 1301/1302 (npcs 12666/12667) | NO |
| 6597 | 바다에서 떠오르는 시체 1단계 | 114 | 193 (275) | 51 | accept npc 14929 훈련교관 그렌델 | NO |

**Route status: none of the 13 is on the Solzreed golden route** (zones 9/124/125,
Golden-Route-Solzreed.md) or its excluded list. 1883/1922/1836/1959/2144/38 live in the
legacy Gweonid starter (zone 1) — superseded by the 1.2 Solzreed opening; 2054/2056 in
Sunny Wilderness (zone 22); 6587–6597 are the lvl-51 "God's Shield" (cat 114) chain in
the Land of Sunlights (zone 193) — the same chain family as T2 quest 6615 (신의 방패
정식 대원이 되다), which the harness already PASSes. The cat-114 chain is the only
group with future content value (see §7 recommendation).

## 5. Cross-surface audit (same surfaces as the reachability audit)

For all 20 ids, checked every quest-id reference surface in compact.sqlite3:

| Surface | Hits | Notes |
|---|---|---|
| `quest_contexts` | 0/20 | definition of "missing" |
| `quest_context_texts` | 0/20 | names gone with contexts |
| `quest_act_con_accept_components` | 18 (chain ghosts 1955–1958/1961/2140–2143, 2 each) | dangling accept links inside the dropped chain |
| `item_accept_quests` | 0/20 | no item grants a missing quest |
| `doodad_func_require_quests` | 0/20 | no doodad requires one |
| `doodad_func_quests` | 0/20 | — |
| `sphere_quests` | 1 (row 280 → quest_id **14**) | a live sphere triggers the collision id 14 — dangling |
| `sphere_accept_quest_quests` | 1 (row 3 → **1956**) | a sphere-accept grants orphan 1956 — dangling |
| `npcs.engage_combat_give_quest_id` | 0/20 | — |
| `items.loot_quest_id` | 0/20 (missing ids) | BUT 3 of the **gated** quests have live loot edges: 38 ← item 24325, 2054 ← 15887, 2056 ← 15889 |
| `quest_mail_sends` / `quest_task_quests` / `game_schedule_quests` / `accept_quest_effects` | 0/20 | — |
| `unit_reqs` (all kinds, value1 = any of the 20) | see §6 | — |

## 6. Cross-layer check — Skill / Sphere / AiEvent rows (67 / 31 / 29)

The audit's counts reproduce exactly: 151 total quest-prereq-kind rows (31,32,33,36,37,
72,73) reference missing contexts — **24 QuestComponent (20 distinct) + 67 Skill (57
distinct) + 31 Sphere (25 distinct) + 29 AiEvent (21 distinct)**.

**None of the Skill/Sphere/AiEvent rows references any of our 20 ids** — the three
other layers target a disjoint set of missing contexts (out of scope for this card;
same follow-up as the audit: candidate verifier finding ACT_REF_MISSING_QUEST scoped to
unit_reqs).

Non-quest-prereq rows whose value1 happens to be one of the 20 (must NOT be confused
with quest deps):
- **kind-1 Level rows, value1=14 — 65 rows.** These are min-level-14 gates, not quest
  refs. The strongest proof that id 14 is collision-owned: pruning a "quest-14" finding
  here would break level gates.
- kind-43 LaborPower rows value1=14 (3 rows) — labor-margin gates, same.
- kind-15 DoodadRange row 22921 (Skill 14553 → doodad 1921) — doodad ref, same pattern.
- kind-10 OwnItem row 45718 (comp 529 → item 2142) — legit item gate on a real item.

**745/12913-pattern component-id reuse:** ids 2140/2142/2143 exist as
`quest_components.id` of real quest 541 (부두 앞의 소란); 3233/3235 as comps of
842/843; 6014/6015 as comps of 1180 (코볼트 동굴의 점박이 코볼트 처치). A verifier
joining `unit_reqs.value1` to `quest_components.id` would silently PASS these; the
join target must be `quest_contexts.id` only.

## 7. Bottom line + fix-card shape

1. **No restore/accept candidates.** None of the 20 has a surviving context row; the 13
   bodies belong to chains already ruled dead. Nothing here is "a real 1.2 quest that
   should exist" in the restore-from-data sense.
2. **The only fix with real player impact: PRUNE the 5 gate rows on 6586** (ids
   46598/46603/46609/46613/46619). That unblocks the 5 lvl-51 God's Shield stage-1
   quests, which are otherwise fully wired (accept NPCs + kill objectives). Recommended
   shape: `DELETE FROM unit_reqs WHERE id IN (46598,46603,46609,46613,46619);` — same
   mechanism as the 745/2951 fix in data-defects.md (delete the impossible gate row).
   Note: the God's Shield chain's own root (6586's hypothetical quest) is unrecoverable
   from data — pruning accepts the chain starts with stage 1.
3. **PRUNE the 11 chain/orphan gate rows** (16832, 16853, 18500, 18576, 18578, 19197,
   19198, 19201, 19205, 19206, 19207, 19208, 19209, 19210, 27692, 27695) — dead-to-dead
   or dead-chain-to-dead-shell edges. Optional-but-clean; zero player impact either way.
   Do NOT touch: kind-1 rows with value1=14 (level gates), row 22921 (doodad ref),
   row 45718 (item ref).
4. **LEAVE the ExceptComplete rows** (20455 → 14, 44930/44931 → 6014/6015): kind 36
   "must not have completed" against a never-completable quest is vacuously true —
   no player impact, no boot error (they resolve at runtime, not load). Quest 38 in
   particular is NOT blocked and is playable today (fully wired Gweonid quest).
5. **Verifier follow-up (BUG-007, engine-side, NOT this card):** if ACT_REF_MISSING_QUEST
   is extended to unit_reqs, resolve `value1` against `quest_contexts.id` ONLY, and
   classify collisions (id present in spheres/npcs/items/doodads/ai_events) as a
   separate severity from orphans — otherwise 7 of these 20 will be false "missing
   quest" findings and the 45 level-14 rows will look like quest deps.
6. **Overlay rule stands:** compact.sqlite3 is read-only reference (upstream alignment
   rule 3) — the PRUNE rows land as an additive overlay (SQL/updates or a startup
   sanitizer), never by editing the reference file.
7. **No upstream PR** (lane gate). This report lands on branch `unit-reqs-layer-audit`
   (fork only); STATUS.md/SCORECARD flow via Nei.

## Appendix — reproducible queries (prod box, compact.sqlite3)

```sql
-- the 24 QuestComponent-owned rows (this card's universe)
SELECT r.id, r.owner_id, r.owner_type, r.kind_id, r.value1
FROM unit_reqs r
WHERE r.owner_type='QuestComponent'
  AND r.kind_id IN (31,32,33,36,37,72,73)
  AND r.value1 NOT IN (SELECT id FROM quest_contexts);

-- collision proof: is the missing id a live entity of another type?
SELECT 1882 AS qid, (SELECT COUNT(*) FROM spheres WHERE id=1882) AS spheres,
       (SELECT COUNT(*) FROM npcs WHERE id=1882) AS npcs,
       (SELECT COUNT(*) FROM ai_events WHERE id=1882) AS ai_events,
       (SELECT COUNT(*) FROM doodad_almighties WHERE id=1882) AS doodads;

-- the 45 level-14 rows that must NOT be mistaken for quest deps
SELECT COUNT(*) FROM unit_reqs WHERE kind_id=1 AND value1=14;

-- chain accept links (qac) held by the ghost bodies
SELECT qac.quest_context_id, COUNT(*) FROM quest_act_con_accept_components qac
WHERE qac.quest_context_id IN (1955,1956,1957,1958,1961,2140,2141,2142,2143)
GROUP BY 1;

-- live loot edges INTO the gated quests
SELECT id, name, loot_quest_id FROM items WHERE loot_quest_id IN (38,2054,2056);

-- gate rows to prune for the God's Shield unblock
SELECT id FROM unit_reqs WHERE value1=6586 AND kind_id=31 AND owner_type='QuestComponent';
```
