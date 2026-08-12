# WI-11a — Band 0/null triage: classification + drop-decision evidence

**Card:** t_724ccab2 (G1 gate: every live quest context PASS or registered-drop)
**Date:** 2026-08-09 | **Author:** Nei (evidence only — no drops decided)
**Data source:** canonical `compact.sqlite3` (md5 `78b3bdbf038db3b927056106efdf91af`, READ-ONLY; all queries `mode=ro`)
**Precedent:** M2a purge playbook (t_f105ee3b triage → t_e5deb128 impl → t_656ed5fe gate → t_8b64ac7d merge; register §4/§6/§7 verdict shapes)

---

## 1. Scope and method

Population = every `quest_contexts` row with `LEVEL = 0` (228) or `LEVEL IS NULL` (3) = **231 contexts**. They sit OUTSIDE the named census bands (t6/t7/t8 = LEVEL 1-10/11-20/21-30).

**Census classification of band 0/null: UNCLASSIFIED — invisible to the census entirely.**
- Band sweeps select `LEVEL BETWEEN lo AND hi` (gen-manifests.py:881); census-meta.json `bands` = exactly {1-10, 11-20, 21-30}.
- No tier manifest references any band-0/null id; runnability.md has zero band-0 rows (verified: 0 grep hits for representative ids, no "band 0" text).
- Consequence: the G1 gate's "every live quest context PASS or registered-drop" has a blind spot of 231 contexts — the census cannot see them.

**Already registered-dropped (16, excluded from decisions):** 1391 (register §1), 1542-1549, 1551-1554, 1640, 1830-1831 (register §2 no-start cluster). Present in the canonical DB (drops are overlay/patches, never reference edits) but already decided.

**Live decision candidates: 215.**

Classification rules (per context, precedence order):
1. **no-components** — zero `quest_components` rows → StartQuest fails (NewQuestCode.cs:44-48), nothing runs.
2. **ltd-no-report** — ≥1 comp, `let_it_done='t'`, zero report acts (ConReportNpc/Doodad/Journal), `score=0`. Completion-path analysis: QuestStep.cs:127-129 forces `res=false` at Progress for ltd quests — they can NEVER leave Progress; the HackFix (NewQuestCode.cs:69-78) needs `Score>0` AND no Ready step — all 40 have Score=0 → no exit path. Engine-stuck.
3. **no-Start** — ≥1 comp, no Start-kind comp, no accept acts, no accept surface (item/doodad/effect/sphere), no `successive` — can never be acquired. (0 live members — see §5.)
4. **other** — everything else; real scrutiny, potentially alive → NOT drop candidates here.

Verified features per context: comps by kind, acts by type, accept surfaces (item_accept_quests / doodad_func_quests / accept_quest_effects / sphere_accept_quest_quests), sphere_quests refs, unit_reqs kind-31 inbound gates, unit_reqs owned by the context's comps (reverse gates), loot_quest_id refs, NPC givers.

## 2. Headline

| Class | Count | Recommendation | Josh decision |
|---|---|---|---|
| A. no-components | 92 | A1 (88): DROP · A2 (4): KEEP | **GO/NO-GO below** |
| B. ltd-no-report | 40 | DROP (engine-stuck) | **GO/NO-GO below** |
| C. no-Start | 0 | — (all 15 canonical members already dropped) | — |
| D. other | 83 | NO DROP here → WI-11b sweep (Tai) | — |
| already-dropped | 16 | excluded | — |
| **Total** | **231** | | |

## 3. Class A — no-components (92)

### A1 — tutorial stubs (88): `2584, 2586, 2589-2606, 2609, 2612, 2614, 2616, 2620-2683`
- **Shape:** cat 45 (튜토리얼), zone `w_gweonid_forest_1`, ms 5, LEVEL 0. Zero comps / zero acts / zero accept surfaces / zero sphere_quests refs / zero inbound gates.
- **Completion path:** none exists — no components means no Start step (StartQuest returns false + Warn, NewQuestCode.cs:44-48). Not startable, not completeable, not acquirable.
- **Dependency scan:** single exception — skill 12586 carries unit_reqs kind-27 → value1 **2640** (skill-gate dangle after drop; inert, same class as M2a loot_quest_id residue — the skill just never grants via that path). No kind-31 quest gates, no sphere refs, no owned unit_reqs.
- **Drop recommendation:** **DROP** (M2a §7 zero-component-shell pattern). Family note: same legacy numbered tutorial stub line as the already-dropped 1533-cluster (register §2) — cat differs (45 vs 28) but naming/zone/ms identical.
- **Restore pointer:** legacy tutorial skeleton — the natural rebuild target if a 1.2-era tutorial line is ever re-added.
- **Risk:** LOW. 88 contexts / 0 comps / 0 acts; one inert skill gate (2640).

### A2 — unit-req / dummy specials (4): `315, 1576, 1728, 2046`
- **Shape:** 315 "(삭제 금지) 스킬 연결용 퀘스트" (do-not-delete skill-link), 1728 "두다드 스킬 사용전용(삭제하지마시오)" (do-not-delete doodad-skill-use), 2046 "Unit Req Dummy", 1576 "dummy". All zero components. **Currently allowlist-masked** — QuestSanityVerifier.cs:87-93 comments them explicitly: *"315/1728 carry a 'do not delete' label (client-side skill/doodad link hooks), 1576/2046 are dummies"*.
- **Accept surfaces:** 1728 ← sphere_quests 567 (trigger 1); 2046 ← sphere_quests 595/721/770/780/1096/1172 (6 accept triggers). Live sphere wiring — the accept fires if the sphere is placed.
- **Dependency scan:** zero unit_reqs reference them (kind 31 or otherwise); zero item/doodad/effect surfaces.
- **Drop recommendation:** **KEEP (default NO-GO)** — do-not-delete labels are a deliberate dev marker, the allowlist documents them as client-side skill/doodad hooks, and 1728/2046 carry live sphere accept rows. Dropping without sphere-prune analysis risks breaking doodad/sphere interactions in the tutorial net. If Josh wants them gone anyway, the drop must prune 7 sphere_quests rows + remove 4 ids from the allowlist (then regressions re-report at WARN — that's the design).
- **Restore pointer:** n/a if kept.
- **Risk:** LOW if kept; MEDIUM if dropped (sphere surface pruning).

## 4. Class B — ltd-no-report (40)

Engine-stuck rule (all 40): ltd + zero report acts + Score=0 → QuestStep.cs:127-129 forces Progress back forever; HackFix ineligible (Score=0). None can ever complete in live play.

### B1 — Dwarf main-story dummy chain (33): `5040, 5773, 5781-5811`
- **Shape:** cat 93 ([종족 퀘스트] 드워프), zone Gweonid, ms 5, ltd='t', score=0. 134 comps, 0 acts. Names: 메인스토리_dummy206-213, 301-310, 401-403, 501-506, 601-605.
- **Completion path:** blocked at Progress by the ltd force (no report acts anywhere; HackFix Score=0 → ineligible).
- **Dependency scan:** 32 of 33 carry kind-31 gates, forming a **linear chain 5781→5782→…→5811** (each gates on its predecessor). Chain root 5040 gates on 4408 (class D1) — the full 60-quest main-story skeleton spans B1+D1 (§6.1). All gate owners are in-set (self-contained). One skill ref: 5806 ← skill 12050 (kind 27) → inert after drop.
- **Drop recommendation:** **DROP** (M2a §6 engine-stuck pattern) — decision coupled with D1 (§6.1): drop the skeleton as one block or keep both.
- **Restore pointer:** placeholder skeleton for the Dwarf race main story — the slots (dummy301…605) are numbered for a future real storyline; chain topology is the blueprint.
- **Risk:** MEDIUM — drop surface 33 quests / 134 comps / 0 acts / 32 kind-31 gates + 93 owned unit_reqs to prune; skill gate 12050 goes inert.

### B2 — title quests (3): `8000001-8000003` (설립자/여행자/선구자 — Founder/Traveler/Pioneer)
- **Shape:** cat 82 (칭호), ms 8000001, ltd='t', score=0. 6 comps, 15 acts — all Supply family (copper/exp/appellation); zero report acts.
- **Completion path:** blocked at Progress by the ltd force; the titles are unobtainable via quest completion.
- **Dependency scan:** zero inbound/owned refs. Zero surfaces.
- **Drop recommendation:** **DROP** (engine-stuck; the title-grant surface is dead anyway).
- **Restore pointer:** if the Founder/Traveler/Pioneer titles are ever wanted, grant them via a different surface (item/effect) — this shape can't deliver.
- **Risk:** LOW.

### B3 — cat-1 test/unused (3): `1835 (테스트), 1836 미사용, 1895 미사용`
- **Shape:** cat 1 (dummy), Gweonid, ltd='t', score=0. 8 comps, 0 acts.
- **Dependency scan:** 1836's comp owns unit_reqs 18500 (kind 31 → value1 **1832**, external) — 1836 is a chain DEPENDENT (needs 1832 complete); nothing depends on 1836. Dropping 1836 leaves 1832 untouched (1832 is the prerequisite, not the dependent).
- **Drop recommendation:** **DROP** (test/unused, engine-stuck).
- **Risk:** LOW.

### B4 — Cradle of Genesis (1): `5678` (골치 아픈 주문서 — "The Troublesome Scroll")
- **Shape:** cat 27 (태초의 요람), ltd='t', score=0. Start/Progress/Ready comps, **zero acts** (act-less shell like M2a §6 A2).
- **Dependency scan:** zero refs either direction.
- **Drop recommendation:** **DROP** (M2a §6 A2 pattern — act-less ltd, engine-stuck).
- **Risk:** LOW.

## 5. Class C — no-Start (0 live)

The class exists in the model, but every canonical member is **already registered-dropped**: 1542-1549, 1551-1554, 1640, 1830-1831 = the 15-quest no-start tutorial cluster (register §2, dropped 2026-08-05). The only other no-start-shaped contexts (ltd, no-Ready, score=0) sort into class B by precedence. **Nothing to decide.**

## 6. Class D — other (83) — NOT modeled dead → WI-11b

Scrutiny verdict: none of these are engine-dead by the completion-path rules, but they are also not census-visible (LEVEL 0/NULL). They are the WI-11b sweep population. Per sub-class guidance:

### D1 — main-story dummy chain, cat 93 (27): `3484-3490, 3492-3502, 3562-3563, 3565-3568, 3992, 4408, 5980`
- Shapes: 3484-3490 + 5980 (dummy_start) carry accept-NPC + report-NPC acts (completeable if accepted); 3493+ are act-less (would pass vacuously). All are placeholders for the Dwarf race main story.
- **Chain topology (with B1, 60 quests total, fully self-contained):** `5980 → 3484 → 3485 → 3486 → 3487 → 3488 → 3489 → 3490 → 3492 → 3493 → 3494 → 3495 → 3496 → 3497 → 3498 → 3499 → 3500 → 3501 → 3502 → 3562 → 3563 → 3565 → 3566 → 3567 → 3568 → 3992 → 4408 → 5040 → 5773 → 5781 → 5782 → … → 5811` — every kind-31 gate owner is in the set; no live quest depends on any member. Root 5980 has no prerequisites.
- **Recommendation:** NO DROP in this card. Decide B1+D1 as ONE block (§7 Q3). If Josh drops the block, the impl prunes 59 kind-31 gates + the whole chain's owned unit_reqs + 1 skill gate (5806 ← skill 12050).

### D2 — old Sunny Wilderness, "(구 불볕황야)" (13): `1883-1884, 1886-1887, 1912-1919, 1922`
- Legacy pre-1.2 old-zone line, relocated to Gweonid in data; act-less except 1914 (ObjTalk×1). ltd='f' → NOT engine-stuck (would pass vacuously); superseded content.
- External neighbors: 1883 gates on 1882 (not in set); 1922 gates on 1921 (not in set) — dead-end tail both directions, nothing in-set is depended on.
- **Recommendation:** NO DROP here (not modeled dead) → WI-11b harness-run; **strong drop candidate** afterwards (M2a §6 restore-pointer pattern — old-zone reference content worth preserving as dialogue reference if dropped).

### D3 — tutorial sphere steps, cat 45 (12): `2585, 2587-2588, 2607-2608, 2610-2611, 2613, 2615, 2617-2619`
- 1 Start comp each; 33 accept-sphere acts + 33 sphere_quests rows (spheres 1098-1130, triggers 1/3). Acquirable IF the spheres are placed in-world; no Progress/Reward comps → cannot complete (stuck post-accept).
- **Recommendation:** NO DROP here → WI-11b **sphere-placement check** first. Placed → live-accept-incomplete wart (accept surfaces to shells); unplaced → drop candidates (the sphere_quests rows must be pruned with them).

### D4 — real content, potentially alive (22): `1394, 1397, 1401-1402, 1404, 1485, 5307-5308, 5313-5314, 5459, 5698-5699, 5999, 6222-6223, 6229, 6250-6251, 6314, 6355, 8000004`
- Cradle of Genesis line (1394-1485: 고르곤 진정시키기 / 뱀이 싫어하는 것 / 폭주한 골렘 / 거인이 되고 싶은 골렘 / 시선을 피해서 / 하얀 숲으로), Blue Salt Brotherhood (5307-5314 봉제 인형 낙원 / 작은 소녀의 소원), 5459 붉은 용의 망령 처치, Great Reeds dragon eggs (5698-5699), 5999 여왕 목걸이 파괴하기, anniversary/event (6222-6223, 6250-6251, 8000004 할로윈 축제 준비), title quests (6229, 6355), library instance (6314).
- Full shapes: 90 comps / 100 acts / 24 component_texts / 31 chat_bubbles / 9 item-gather detail rows. **8 loot_quest_id refs** (items 13969→1101, 27808-27811→5307/5313, 28778→5459, 32539→6229, 33392→6355). **Live external gate: quest 1405's comp owns unit_reqs 11148 (kind 31 → value1 1404)** — a real dependent quest requires 1404 complete. 1485 requires 1484 (external predecessor).
- **Recommendation: MUST NOT be dropped.** WI-11b verifies each via the harness (expected PASS or vacuous-PASS; the Cradle chain has live neighbors).

### D5 — test/dummy-named (9): `1097, 1101, 1128, 1132, 1148, 1204, 2971, 4897, 5649`
- "dummy"/"미사용" ×6 (cat 1), 2971 테스트던전_타워디펜스시작 (test dungeon), 4897 OBT_튜토리얼링크용, 5649 끝없는 여정_test. Act-less except 1101 (sphere 389 accept + AiEvent-398 ref + loot item 13969 — wired!) and 1132 (← skill 12568, kind 22).
- **Recommendation:** NO DROP here → WI-11b wiring check (1101/1132 especially); drop candidates after.

## 7. Decision ask — per-class GO/NO-GO (Josh)

**RULING RECEIVED 2026-08-09 ~21:15 PDT (josh, delegated to Kimi nightwatch — comment on t_724ccab2). Recorded in register §9. Q7-Q10 defer to WI-11b (t_8ec705f0).**

| # | Set | Count | Ask | **Josh ruling** |
|---|---|---|---|---|
| Q1 | A1 tutorial stubs (2584…2683) | 88 | GO — drop (zero-component shells, M2a §7 pattern) | **GO** |
| Q2 | A2 unit-req/dummy (315, 1576, 1728, 2046) | 4 | NO-GO — keep (do-not-delete labels, live sphere accepts, allowlist-documented) | **NO-GO (keep)** |
| Q3 | B1+D1 Dwarf main-story skeleton (5040, 5773-5811, 3484-3502, 3562-3568, 3992, 4408, 5980) | 60 | GO — drop both halves as one block (33 engine-stuck + 27 placeholder) | **GO (block)** |
| Q4 | B2 title quests (8000001-8000003) | 3 | GO — drop (unobtainable titles) | **GO** |
| Q5 | B3 cat-1 test/unused (1835, 1836, 1895) | 3 | GO — drop | **GO** |
| Q6 | B4 5678 (골치 아픈 주문서) | 1 | GO — drop (act-less ltd) | **GO** |
| Q7 | D2 old Sunny Wilderness (구 불볕황야) | 13 | DEFER — WI-11b harness first; strong drop candidate after | **defer → WI-11b** |
| Q8 | D3 tutorial sphere steps | 12 | DEFER — WI-11b sphere-placement check | **defer → WI-11b** |
| Q9 | D4 real content | 22 | NO-GO — keep, verify in WI-11b | **defer → WI-11b (must not drop)** |
| Q10 | D5 test/dummy | 9 | DEFER — WI-11b wiring check | **defer → WI-11b** |

Impl execution: card **t_267a3279** (hyrax-os, parents=[t_724ccab2]) — 155 contexts dropped per ruling; Rei gate on impl block (M2a chain).

## 8. WI-11b handoff — sweep population (56 contexts)

Explicit list for the follow-up sweep (Tai, t_8ec705f0, gated on this card).
**Corrected post-ruling:** D1 (27 main-story dummies) folded into the Q3 drop
(§7/register §9c) — sweep population is 56, not 83:

- **D2 (13):** 1883-1884, 1886-1887, 1912-1919, 1922
- **D3 (12):** 2585, 2587-2588, 2607-2608, 2610-2611, 2613, 2615, 2617-2619
- **D4 (22):** 1394, 1397, 1401-1402, 1404, 1485, 5307-5308, 5313-5314, 5459, 5698-5699, 5999, 6222-6223, 6229, 6250-6251, 6314, 6355, 8000004
- **D5 (9):** 1097, 1101, 1128, 1132, 1148, 1204, 2971, 4897, 5649

Sweep guidance: harness-drive each (band-0 quests need a LEVEL override in the manifest generator or a dedicated t9/t0 tier); check sphere placement for D3; check skill/AiEvent wiring for 1101/1132/2640/3487/3495/5806/6222/1401/1485.

## 9. Verification notes

- All numbers re-derived read-only on 2026-08-09 from canonical compact.sqlite3 (md5 `78b3bdbf038db3b927056106efdf91af` — matches all SQL patch headers; the register header's `78b3bdbf0383db3b...` is a typo, corrected in this pass).
- Classification scripts + raw dumps: kanban workspace t_724ccab2 (triage-data.json / final-classes.json).
- Drop-surface counts per class: A1 = 88 quests/0 comps/0 acts; A2 = 4/0/0 (+7 sphere_quests); B1 = 33/134/0 (+32 ur31 + 93 owned ur); B2 = 3/6/15; B3 = 3/8/0; B4 = 1/3/0; D1 = 27/108/18; D2 = 13/65/1; D3 = 12/12/33 (+33 sphere_quests); D4 = 22/90/100; D5 = 9/35/7.
- G1 gate note: after the Q1-Q6 drops (155 contexts), the census's blind spot shrinks to the 56 WI-11b contexts (all scrutinized, none accidentally live).

