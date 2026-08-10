# WI-6 — Band 41-50 ltd triage: quests 3419 / 4967 / 6069

**Card:** t_6f950108 (triage) · **Prepared by:** Nei · **Date:** 2026-08-09
**Decision authority:** Josh (G0-5) — this packet is evidence + recommendation, NOT a decision.

**Data provenance:** canonical `compact.sqlite3` (md5 `78b3bdbf038db3b927056106efdf91af`),
READ-ONLY access throughout. Merged develop @ 726b574c7 (post-WI-2 CrimePoint closure; register
history unchanged since 38f4e36f1). Engine citations verified on that tree.

---

## TL;DR (decision asks — one line each)

| Quest | Shape | Reachable in live play? | Engine completion path? | Recommendation | ASK |
|---|---|---|---|---|---|
| **3419** 의논할 수 없는 고민 | ltd, score 0, Start→Progress→Reward, no report act | **YES** — accept NPC 9581 exists + spawner; gates 3370/3372 completable | **YES via client packet** (0x0dd → TryCompleteQuestAsLetItDone) once 2/3 kills credit | **NO-GO drop** — keep; not M2a-shaped (that cluster was unreachable) | **NO-GO — keep 3419?** |
| **4967** 황금비늘의 후손 해방 | ltd, score 0, Start→Progress→Reward, no report act | **YES** — accept NPC 10089 exists + spawner; doodad 5892 exists | **YES via client packet** once 1/1 interaction credits | **NO-GO drop** — keep; same reasoning | **NO-GO — keep 4967?** |
| **6069** 거침없이 춤추는 격투의 칼날 | ltd, score 0, Start(no acts)→Progress→Ready(no acts)→Reward | **NO** — zero accept surfaces of any kind (Start comp 26119 has no acts; 0 item/doodad/effect/sphere/con-accept) | **NO** — unreachable, so no path can ever be entered; objective also never credits (ability-level act has no event hookup) | **GO drop** — consistent with M2a cluster A precedent | **GO — drop 6069?** |

---

## 1. Shape summary (from compact.sqlite3)

### 3419 — 의논할 수 없는 고민 ("An Undiscussable Worry")
- cat 59, lvl 46, milestone 5, **ltd='t'**, zone 20 (`e_hasla_1`), score 0, degree 3
- Start comp 14674: `QuestActConAcceptNpc` 2812 → **NPC 9581** (감독관 히치 — exists, 1 spawner row)
  - unit_reqs on Start comp: kind 1 (level) value 42; **kind 31 gates → quests 3372 + 3370** must be complete
  - Gates 3370/3372: both alive, both ltd WITH `QuestActConReportNpc` (2999→NPC 9580, 3001→NPC 9581 — exist) → **gates are completable** in live play
- Progress comp 21285: `QuestActObjMonsterGroupHunt` 515 → group 469 (코산의 아이들 하부 조직원), count 3; members 4567/4569/9752 — all exist, all have spawner rows; `OnMonsterGroupHunt` wired (QuestManagerEvents.cs:184-194)
- Reward comp 14677: `QuestActConAutoComplete` 1375 (always-true)
- **No Ready step. No report acts.**

### 4967 — 황금비늘의 후손 해방 ("Freeing the Golden Scale Descendant")
- cat 60, lvl 45, milestone 5, **ltd='t'**, zone 1 (`w_gweonid_forest_1`), score 0, degree 3
- Start comp 21553: `QuestActConAcceptNpc` 3924 → **NPC 10089** (고고학자 수이 — exists, 1 spawner row); unit_reqs kind 1 value 41 (level gate)
- Progress comp 21554: `QuestActObjInteraction` 638 → wi 19, doodad 5892 (속박 마법진 — exists in doodad_almighties), count 1; `OnInteraction` wired (QuestActObjInteraction.cs:36)
- Reward comp 21555: `QuestActConAutoComplete` 1186 (always-true)
- **No Ready step. No report acts.**

### 6069 — 거침없이 춤추는 격투의 칼날 ("The Uninhibited Dancing Blade of Combat")
- cat 55, lvl 50, **milestone 14**, **ltd='t'**, zone 1, score 0, degree 3
- Start comp 26119: **NO acts at all**
- Progress comp 26120: `QuestActObjAbilityLevel` 7 (ability 1, level 50) — engine-loadable (QuestManager.cs:1056) but **no event hookup → objective counter never credits**
- Ready comp 26121 (kind 6): **no acts**
- Reward comp 26122: `QuestActSupplyItem` 4002 (item 30757)
- **Zero accept surfaces:** 0 item_accept_quests / 0 doodad_func_quests / 0 accept_quest_effects / 0 sphere_quests / 0 quest_act_con_accept_components. Start comp act-less → no NPC offers it.
- Already flagged: runnability.md:245 SKIP "let-it-done quest with no report act"; SCORECARD.md:162 "1 let-it-done-without-report-act (6069)".

## 2. Completion-path analysis (can the engine EVER leave Progress?)

Verified on the fork engine (all three quests are ltd + score=0):

1. **Server auto-advance — impossible.** `QuestStep.cs:127-129`: ltd Progress step is forced
   `res = false` ("always forced forward using the Report Acts"). RunComponents can never return
   true on a ltd Progress step.
2. **HackFix — does not fire.** `NewQuestCode.cs:69-78` requires `Score > 0`; all three have score=0.
3. **Report acts — absent.** None of the three carries QuestActConReportNpc/Doodad/Journal, so the
   server-side force-advance (QuestActConReportNpc.cs:59-60 sets Step=Ready) never triggers.
4. **Client packet — LIVE for 3419/4967, moot for 6069.** `CSTryQuestCompleteAsLetItDonePacket`
   (0x0dd) is registered (GameNetwork.cs:221) → `CharacterQuests.TryCompleteQuestAsLetItDone`
   (CharacterQuests.cs:647-661): if quest active + LetItDone + objective status ≥ CanEarlyComplete,
   jumps straight to Reward. 3419: 2 kills → CanEarlyComplete (monster group 469 credits).
   4967: 1 interaction → QuestComplete (wi 19 / doodad 5892 credits). Reward comps are
   always-true auto-completes → quest completes. **6069: quest can never be accepted, so this
   path can never be entered — and its objective never credits regardless** (ability-level act has
   no event subscription; 0 ≥ Count*1/2 only via integer-division accident on an uncreditable counter).

> **Why this differs from the M2a note:** the M2a cluster-A register line ("zero report acts →
> engine can never leave Progress") listed only paths 1-3. The packet path (4) is a real, registered
> engine exit that M2a's quests could never use anyway (they were unreachable — zero accept
> surfaces). **3419 and 4967 ARE reachable, so the packet path applies to them.** Their only
> reliance is on the real 1.2 client sending 0x0dd (the standard let-it-done mechanic) — worth a
> 2-minute live verify if Josh keeps them.

## 3. Accept-surface & dependency scan (all three)

- unit_reqs kind 31 with value1 ∈ {3419,4967,6069}: **none** — no other quest gates on these → drop orphans nothing
- items.loot_quest_id → these quests: **none** (no dangling loot rows)
- sphere_quests → these quests: **none**
- act-detail rows are **SHARED with other quests** (ability 7 → 15 quests incl. 1533/1833/1944/2279/5465/6049…; supply 4002 → 5106; autocomplete 1375/1186 → many; group-hunt 515 → 10 quests; interaction 638 → 8 quests; accept-npc 2812/3924 → 6 quests). **A drop must delete only quest_acts rows + comps + context — never the shared act-detail rows** (M2a collision pitfall).

## 4. Register-format records (draft — PENDING Josh)

### 3419 — reachable ltd quest (recommendation: keep)

| Field | Value |
|---|---|
| Shape | ltd='t', score 0, zone 20, ms 5, cat 59, lvl 46; Start(accept NPC 9581, gates 3370+3372) → Progress(monster group 469 ×3) → Reward(auto-complete); no report act, no Ready step |
| Verdict recommendation | **NO-GO drop** — reachable + packet-completable; not M2a-shaped |
| Drop action sketch *(only if Josh overrides)* | quest_contexts −1, quest_components −3 (14674/21285/14677), quest_acts −3 (20419/29614/32791), unit_reqs −4 (37860/37861/37862/35004), texts −1, bubbles −4; **act-detail rows 2812/515/1375 SHARED — keep** |
| Restore pointer | Full body preserved (comps/acts/texts/bubbles) — restore only if the Hasla quest line is rebuilt |
| Risk | Removes a live-offerable quest from NPC 9581; players mid-progress would lose it. Gates 3370/3372 unaffected (they don't gate on 3419) |

### 4967 — reachable ltd quest (recommendation: keep)

| Field | Value |
|---|---|
| Shape | ltd='t', score 0, zone 1, ms 5, cat 60, lvl 45; Start(accept NPC 10089) → Progress(interact doodad 5892) → Reward(auto-complete); no report act, no Ready step |
| Verdict recommendation | **NO-GO drop** — reachable + packet-completable |
| Drop action sketch *(only if Josh overrides)* | quest_contexts −1, quest_components −3 (21553/21554/21555), quest_acts −3 (30115/30123/30122), unit_reqs −2 (38226/40394), texts −1, bubbles −4; **act-detail rows 3924/638/1186 SHARED — keep** |
| Restore pointer | Full body preserved — restore only if the Gweonid/scale line is rebuilt |
| Risk | Removes a live-offerable quest from NPC 10089; players mid-progress lose it |

### 6069 — unreachable ltd quest (recommendation: drop)

| Field | Value |
|---|---|
| Shape | ltd='t', score 0, zone 1, ms 14, cat 55, lvl 50; Start(no acts) → Progress(ability-level 1@50) → Ready(no acts) → Reward(supply item 30757); zero accept surfaces |
| Verdict recommendation | **GO drop** — unreachable + no completion path; matches M2a cluster-A precedent and existing SKIP flag |
| Drop action sketch | quest_contexts −1, quest_components −4 (26119/26120/26121/26122), quest_acts −2 (35730/35732), unit_reqs −1 (45196), texts −3, bubbles −4; **act-detail rows 7/4002 SHARED — keep**; remove 6069 from `T3_PINNED_QUESTS` (gen-manifests.py:766) + runnability.md SKIP row + SCORECARD.md line at census regen |
| Restore pointer | Full body preserved — restore only if a 1.2-era ability-gate quest is rebuilt |
| Risk | Low: already SKIP-documented; no accept surface means no live players can ever hold it. Harness-gap queue keeps 5967 as the remaining ability-level example |

## 5. Verification notes

- All queries `-readonly` / `mode=ro` on the canonical sqlite; zero writes.
- Headline numbers cross-checked against runnability.md + SCORECARD.md (6069 SKIP line confirmed current on merged develop).
- Precedent chain (M2a): t_f105ee3b → t_e5deb128 → t_656ed5fe — drop execution pattern documented in `aaemu-quest-data-triage` skill + dropped-content-register.md §6/§7.
