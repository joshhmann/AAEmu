# Golden Route — Solzreed (Nuian)

- Audience: Players, testers, contributors
- Last verified against: `develop` on 2026-08-05 (canonical `compact.sqlite3` r208022, zones 9/124/125; M1-5 scenario-harness census)
- Scope: M1 golden-route document — the curated opening progression for a new Nuian character in Solzreed, plus intentionally excluded quests
- Sources: `scorecard-explorations/solzreed-zone-report.md` (97 quests, kind-31 chains, gates), `scorecard-explorations/runnability.md` (88/97 T1 pass), `tools/quest-graph/README.md`

Solzreed is the LOCKED golden zone (ROADMAP.md — locked-shape, Josh 2026-08-03). This page is the
curated route: which quests a fresh Nuian character follows, in what order, what each
step gates on, what is currently broken (with quest ids), and what we are deliberately
leaving out and why.

## TL;DR — the route in one paragraph

Create a Nuian character, spawn in Solzreed, and follow the village breadcrumbs: the
notice-board kills (250, 251, 329, 330) and village errands (2239, 252, 324, 325, 2531,
2532) get you to level 3-4; then the main village chain **254 → 255 → 256 → 257 → 259**
(plus the **260 → 261** fan) carries the story to level 5-6; the shepherd/pickaxe arc
(**265 → 266 → 354**) unlocks the **mount chain 4292 → 4294 → 4295**, which ends with the
first mount (item 18649). In parallel, the prophet line (**2531 → 2532**) feeds the big
Bloody Hand investigation (**2255 → 2256 → 2257 → 2258 → 2259 → 2260 → 1525 → 2263 →
2261 → 3503 → 2262 → 2264 → 2265 → 2266**) for levels 4-10. Everything else in the zone
is either a bounty-board kill quest (optional, good exp) or intentionally excluded
(level 31-50 content, jury/library arcs, or currently broken quests).

## 1. The curated opening chain (recommended order)

Step order is derived from the kind-31 completion prerequisites in the zone report.
"Lv gate" is the acceptance requirement (unit_reqs kind 1 = Level); quests without a
kind-1 row are gated by race (kind 3, value 1 = Nuian) or mother faction (kind 42,
value 148) instead. "Harness" is the M1-5 scenario verdict (PASS/FAIL); FAIL rows are
explained in section 4 — most are harness/manifest artifacts, not gameplay blockers.

### 1a. Arrival — notice board and first kills (level 1-3)

| Step | Quest | Name | Lv gate | Accept from | Objective | Turn in | Reward | Harness |
|---|---|---|---|---|---|---|---|---|
| 1 | 250 | 솔즈리드 여우 처치 (Solzreed fox hunt) | 1 | Doodad 5047 | Kill 3× fox (npc 3492) | auto | 110 exp | **FAIL** (rig artifact, see §4) |
| 2 | 251 | 화난 멧돼지들 (Angry boars) | 1 | Npc 3512 | Gather 3× item 4058 | Npc 3512 | item 18791 ×1 | PASS |
| 3 | 330 | 나를 찾는 사람 (Someone looking for me) | 1 | Npc 3597 | — | Npc 3511 | — | PASS |
| 4 | 329 | 불곰을 조심해! (Beware the flame bears) | 2 | Doodad 5048 | Kill 3× bear group 153 | auto | — | PASS |
| 5 | 252 | 숲 되살리기 (Restoring the forest) | 2 (req 251) | Npc 7653 | Use item 7738 | auto | item 18791 ×1 | PASS |
| 6 | 324 | 앨런의 도움 (Alan's help) | 2 (req 251) | Npc 3513 | Gather 3× item 2726 | Npc 3596 | — | PASS |
| 7 | 325 | 로나의 약 (Rona's medicine) | 2 (req 324) | Npc 3596 | Gather 1× item 4043 | Npc 3515 | item 18792 ×1 | PASS |
| 8 | 2239 | 지붕 위로 날아간 닭 (The chicken on the roof) | — (own item 33381) | Npc 7653 | Gather 1× item 33381 | auto | — | PASS |
| 9 | 2531 | 시골에 도착한 예언자 (The prophet arrives) | 1 | Npc 11541 | — | Npc 10580 | item 23633 ×1 | PASS |
| 10 | 2532 | 낯선 소녀의 연락 (Word from the strange girl) | 4 (req 2531) | Npc 11542 | — | Npc 10581 | item 23633 ×1 | PASS |

### 1b. Main village chain (level 3-6) — the story spine

| Step | Quest | Name | Lv gate | Accept from | Objective | Turn in | Reward | Harness |
|---|---|---|---|---|---|---|---|---|
| 11 | 254 | 엄마의 걱정 (Mother's worry) | 2 | Npc 3515 | — | Npc 3516 | — | PASS |
| 12 | 255 | 제니의 부탁 (Jenny's request) | 3 (req 254) | Npc 3516 | Gather 1× item 13713 | Npc 3516 | — | PASS |
| 13 | 256 | 고집쟁이 제니 (Stubborn Jenny) | 2 (req 255) | Npc 3516 | — | Npc 7651 | — | PASS |
| 14 | 257 | 선돌 연구자의 행방 (The standing-stone researcher) | 3 (req 256) | Npc 7651 | — | Npc 3517 | item 18791 ×2 | PASS |
| 15 | 259 | 위대한 유산 (The great heritage) | 3 (req 257) | Npc 3517 | Gather 1× item 24786 | Npc 5329 | item 18792 ×2 | PASS |
| 16 | 260 | 정체 모를 빛 (A mysterious light) | 3 | Npc 3593 | Gather 3× item 8128 | Npc 3593 | items 32481-3 | PASS |
| 17 | 261 | 원혼 달래기 (Soothing the spirits) | 3 (req 260) | Npc 3593 | Use item 8129 | Npc 3593 | items 32484-6, 35823 | PASS |

### 1c. Shepherd / pickaxe arc → the MOUNT chain (level 6, the M1 exit goal)

| Step | Quest | Name | Lv gate | Accept from | Objective | Turn in | Reward | Harness |
|---|---|---|---|---|---|---|---|---|
| 18 | 265 | 솔즈리언의 문을 위하여 (For the gate of Solzreed) | 3 | Npc 7657 | Gather 3× item 16247 | Npc 7657 | item 18791 ×2 | **FAIL** (manifest artifact, §4) |
| 19 | 266 | 양치기의 부탁 (The shepherd's request) | 3 | Npc 3520 | Kill 20× group 435 + gather 10× item 8130 | Npc 3520 | item 18791 ×2 | **FAIL** (manifest artifact, §4) |
| 20 | 354 | 미안한 이야기 (An awkward story) | 3 (req 266) | Npc 3523 | — | Npc 3605 | — | PASS |
| 21 | 4292 | 망아지 운반 (Carrying the foal) | 3 (req 354) | Npc 3636 | Deliver foal (1 h timer) | Npc 10666 | item 23635 | **FAIL** (persistence, §4) |
| 22 | 4294 | 망아지의 먹이 (The foal's feed) | 3 (req 4292) | Npc 10666 | Use item 23635 + gather 1× item 21850 | Npc 10666 | item 23680-2 (choose 1) | PASS |
| 23 | 4295 | 여행의 동반자를 얻다! (Gain a travel companion!) | 3 (req 4294) | Npc 10666 | Gather 1× each item 8159/8160/8161 | Npc 10666 | **item 18649 — FIRST MOUNT** | PASS |

> **M1 exit-test note:** "reaches first-mount prerequisite" maps to completing 4292→4294→4295.
> 4292's harness PERSIST failure (WriteData→ReadData byte mismatch, timed quest) is the
> one real blocker on this path — see §4, blocker class D.

### 1d. Bloody Hand investigation (levels 4-10, parallel arc)

| Step | Quest | Name | Lv gate | Accept from | Objective | Turn in | Reward | Harness |
|---|---|---|---|---|---|---|---|---|
| 24 | 2255 | 금빛 표지 (The golden mark) | — (race, req 2532) | Npc 10581 | Use item 16280 | Npc 10581 | — | PASS |
| 25 | 2256 | 루키우스의 흔적 (Traces of Lucius) | — (req 2255) | Npc 10581 | — | Npc 10646 | — | PASS |
| 26 | 2257 | 심상치 않은 시체들 (Suspicious corpses) | — (req 2256) | Npc 10646 | Gather 1× item 16287 | Npc 3630 | item 23633 ×1 | PASS |
| 27 | 2258 | 피 묻은 손의 침입 (The Bloody Hand invasion) | — (req 2257) | Npc 3630 | Gather 1× item 16288 | Npc 3611 | item 23633 ×1 | PASS |
| 28 | 2259 | 엄중한 경계령 (Strict alert order) | — (req 2258) | Npc 3611 | Gather 1× item 16259 | Npc 10582 | — | PASS |
| 29 | 2260 | 특명! 백월만 (Special order! Baekwol Bay) | — (req 2259) | Npc 10582 | Gather 1× item 16260 | Npc 10583 | item 23633 ×1 | PASS |
| 30 | 1525 | 널린 희생자들 (Scattered victims) | — (race, req 2260) | Npc 10583 | Enter sphere 1415 | auto | — | PASS |
| 31 | 2263 | 살인마에게 자비를 (Mercy for the murderer) | — (req 1525) | Npc 6984 | Gather 1× item 24126 | Npc 10583 | item 23633 ×1 | PASS |
| 32 | 2261 | 피 묻은 손이 쫓는 것 (What the Bloody Hand chases) | — (req 2263) | Npc 10583 | Use item 16293 | Npc 10583 | — | PASS |
| 33 | 3503 | 희생자를 줄일 기회 (A chance to save lives) | — (req 2261) | Npc 10583 | — | Npc 10585 | item 23633 ×1 | PASS |
| 34 | 2262 | 스콧을 찾아서 (Finding Scott) | — (req 3503) | Npc 10585 | — | Npc 10644 | item 23633 ×1 | PASS |
| 35 | 2264 | 남겨진 실마리들 (Left-behind clues) | — (req 2262) | Npc 10644 | Gather 1× item 24967 | Npc 10585 | — | PASS |
| 36 | 2265 | 꽃의 처녀 플로라 (Flora, flower maiden) | — (req 2264) | Npc 10585 | Deliver item 21604 | Npc 12022 | item 23633 ×1 | PASS |
| 37 | 2266 | 범인을 찾아서 (Finding the culprit) | — (req 2265) | Doodad 1086 | — | Npc 10986 | item 23633 ×1 | PASS |

### 1e. Bounty board & side errands (optional, good exp, all PASS)

Kill quests accepted on kill (Kill acceptor, BUG-006 family) and turned in via the
journal: 2374 (Lv 3, group 155 ×5), 2370 (Lv 4, group 154 ×15), 2413 (Lv 4, npc 7669
×1 → Npc 7662, item 23092), 2570 (Lv 4, group 62 ×20), 2569 (Lv 5, group 57 ×12),
2578 (Lv 5, group 204 ×8), 2573 (Lv 5, group 201 ×15), 2575 (Lv 6, group 229 ×20),
2579 (Lv 6, npc 3452 ×8).

Doodad/item-accepted side quests: 1652 (Lv 3, doodad 8055, group 448-adjacent npc 7673
×3), 1725 (Lv 3, Npc 3529 → Npc 3593, item 18792 ×2; mutually exclusive with 260),
1650 (Lv 5, item 14786, enter sphere 650), 2576 (Lv 6, doodad 3028, npc 8145 ×1, 10
copper), 2404 (Lv 5, doodad 2854, gather item 23611 → Npc 7656), 2762 (Lv 3, Npc 8143,
enter sphere 1142), 2393 (Lv 3, Npc 7661, gather item 17863 — the Solzreed ferry),
2400 (Lv 3, req 263, Npc 3605, gather item 16393 → Npc 3618, item 18792 ×3),
6161 (Lv 3, req 350, Npc 3601, kill npc 14667 ×5).

## 2. Chain map (kind-31 prerequisites at a glance)

```
250 ─ 251 ─┬─ 252 ── 324 ── 325            (village)
           └─ (329, 330, 2239 standalone)
254 ─ 255 ─ 256 ─ 257 ─ 259                (main story)
260 ─ 261                                   (fan-out)
265 ─ 263 ─ 2400                            (pickaxe branch)
266 ─ 354 ─ 4292 ─ 4294 ─ 4295 ─► MOUNT 18649
269 ─┬─ 270                                 (doodle fan-out)
     └─ 271
273 ─ 2249
299 ─┬─ 300 ─┬─ 298                         (level 6-9 arc)
     │       ├─ 303 ─ 304
     │       └─ 5146
     └─ 290 ─ 291
345 ─ 347,  346 standalone
350 ─ 6161
2393 ─ 2246;  2245, 2248, 2251 standalone
2531 ─ 2532 ─ 2255 ─ 2256 ─ 2257 ─ 2258 ─ 2259 ─ 2260 ─ 1525 ─ 2263 ─ 2261 ─ 3503 ─ 2262 ─ 2264 ─ 2265 ─ 2266
4292/4294/4295 race-gated (kind 3 = Nuian); Bloody Hand chain race-gated too.
```

## 3. What is gated on what (acceptance gates)

- **Level gates (unit_reqs kind 1):** the Lv gate column above. Notable: the whole
  mount chain (4292/4294/4295) only requires level 3 — a fresh character can reach the
  first mount very early if the shepherd arc (266/354) is done.
- **Race gate (kind 3, value 1 = Nuian):** 1525, 2255-2266, 2531, 2532, 3503,
  4292, 4294, 4295 — the Bloody Hand chain and the mount chain are Nuian-only.
- **Mother-faction gate (kind 42, value 148):** 250, 329, 1650, 1652, 2370, 2374,
  2404, 2413, 2569, 2570, 2573, 2575, 2576, 2578, 2579, 5059, 5095-5098, 5167,
  5168, 5267, 6161, 6249, 6280 — consistent with the Nuian golden faction.
- **Item gates:** 2239 (own item 33381), 5095 (own item 27378), 5167 (own item 27572),
  5719 (equip item 28800).
- **Completion prerequisites (kind 31):** all arrows in the chain map.
- **Mutual exclusion:** 1725 must NOT be completed together with 260
  (except-completed gate on 1725); 330 is except-completed vs 6198.

## 4. Known blockers (the 11 T1 harness FAILs)

Verdict semantics: **FAIL** = a stage assertion or engine exception in the M1-5
scenario harness. All 11 are listed with quest ids. Classification matters — most are
harness/manifest artifacts, not things a player will hit:

| Quest | Name | Failing stage(s) | Class | What it actually means |
|---|---|---|---|---|
| 250 | 솔즈리드 여우 처치 | REWARD | **B — rig artifact** | Reward exp crashes in the harness because the rigged character has no ability trees assigned (`CharacterAbilities.AddActiveExp` KeyNotFound 'General'). Real characters always have abilities from creation; live play unaffected. Latent engine fragility: `AddActiveExp` should guard `Ability1 == General`. |
| 265 | 솔즈리언의 문을 위하여 | START, PROGRESS | **A — manifest artifact** | LetItDone quest: engine holds the quest at Progress until the report act; manifest expected auto-advance to Ready. READY+REWARD pass — quest completes in-game via report. Manifest expectation needs fixing, not the engine. |
| 266 | 양치기의 부탁 | START, PROGRESS | **A — manifest artifact** | Same LetItDone pattern as 265; completes via report (READY/REWARD pass). |
| 269 | 두들링을 두들겨라 | PROGRESS | **A — manifest artifact** | Same LetItDone pattern; completes via report. |
| 294 | 타락한 요정의 이름, 밴시 | PROGRESS | **A — manifest artifact** | Same LetItDone pattern; level-35 content, outside the opening route anyway. |
| 295 | 밴시 정화 | PROGRESS, READY, REWARD | **C — harness event limitation** | Objective is ItemUse ×3 but the harness fires a single ItemUse event (count not supported for that event type) → 1/3 objectives, cascade fail. Level-35 content; engine path unverified, no engine defect identified. |
| 299 | 네손가락 도적단의 위협 | START, PROGRESS | **A — manifest artifact** | LetItDone pattern; completes via report. |
| 303 | 영혼을 가르는 무기 | START, PROGRESS | **A — manifest artifact** | LetItDone pattern; completes via report. |
| 2248 | 피 묻은 손을 물리쳐라 | PROGRESS | **A — manifest artifact** | LetItDone pattern; completes via report. |
| 350 | 일손 부족 | PERSIST | **D — real persistence defect** | WriteData→ReadData round-trip byte mismatch on a TIMED quest (QuestActCheckTimer 1 h). All three PERSIST failures (350, 4292, and T2 1313) are timed quests — 100% correlation. Suspicion: the timer/Time serialization path. Needs an engine fix (Tai) before the restart-persistence exit test is trustworthy on timed quests. |
| 4292 | 망아지 운반 | PERSIST | **D — real persistence defect** | Same timed-quest byte mismatch. **This one sits on the mount chain — the M1 exit goal.** The quest completes, but its state does not round-trip byte-identically; a restart mid-quest is the risk. Same fix as 350. |

**Missing-data checklist (zone report):** none — every quest-referenced NPC has a
spawner row and every quest-referenced item has a template.

## 5. Intentionally excluded quests (with reasons)

These quests exist in Solzreed's data but are deliberately NOT part of the golden
route. They remain playable targets for later milestones or Lane B.

| Quest(s) | Name | Reason excluded |
|---|---|---|
| 293, 294, 295 | 요정의 정수 / 타락한 요정의 이름, 밴시 / 밴시 정화 | Level 31-35 banshee chain — far beyond the M1 opening route; 294/295 also carry harness FAILs (classes A/C). Revisit for later milestones. |
| 6249 | 가라앉은 만의 공포 | Level 31 doodad-accept kill quest (group 507 ×15) — out of M1 range. |
| 6280 | 고난을 부르는 발걸음 | Level 35 level-up-accept quest — out of M1 range; also an exp-reward quest (same class-B rig caveat). |
| 5059 | 배심원 자격, 놓치지 않을 거예요 | Level 45 jury quest (requires 4784; jury/crime gates kind 46/47) — depends on the jury system, not the opening loop. |
| 4901, 4902, 4903 | 마일즈가 연구 중인 선돌 / 선돌 연구자의 집 방문 / 아이들의 비밀장소 | Level 50 sphere-accept quests (spheres 2021-2023) — endgame content. |
| 5095 → 5096 → 5166 → 5097 → 5098 | 초승달 왕좌의 도서관 chain | Level 50 library arc (cat 85), including two cross-zone prerequisites (5105, 5104). Endgame content. |
| 5106, 5107 | 세상에서 가장 위대했던 도서관 이야기 / 보관될 수 없는 지식에 관하여 | Level 50 library quests with out-of-zone prerequisites (5105, 5104). |
| 5167 → 5168 → 5267 | 낯익은 얼굴 / 특사의 자격 / 이니스테르의 속사정 | Level 50 library continuation; 5167 requires out-of-zone 5304. |
| 5719 | 비틀린 역사의 진실 | Level 50 quest requiring 5718 (out of zone) + equip item 28800. |
| 350 | 일손 부족 | In-zone and level-appropriate, but excluded from the *curated* route until blocker class D (timed-quest persistence) is fixed — it is a timed quest and the harness cannot yet certify its restart round-trip. Still available for players. |
| (T2 set, informational) | 1033, 1313, 1897, 3656, 5489, 6578, 6600, 6615 | Not Solzreed zone quests — included here only so contributors know the T2 family census (21 PASS / 8 FAIL / 6 SKIP, orphaned contexts 745/1421/1955/1957/1958/2140) is tracked in runnability.md, not in this route. |

## 6. Playtest checklist (M1 exit tests)

- [ ] New Nuian character completes 1a + 1b without GM intervention (expect: levels 1 → 5-6, quest rewards received).
- [ ] Character completes the shepherd arc (265/266 — watch LetItDone report behavior live) and 354.
- [ ] Mount chain 4292 → 4294 → 4295 completes; character receives item 18649 (first mount) and can summon it.
- [ ] Mid-route logout/restart: quest log state resumes (pay special attention to timed quests 350/4292 — blocker class D).
- [ ] Bloody Hand arc 2255 → 2266 completes end to end (levels 4 → 10).
- [ ] Bounty-board kills credit and journal turn-in works (kill-acceptor family, BUG-006).

## Related

- [Home](Home)
- [Server](Server)
- [Developer Notes](Developer-Notes)
- [Code Terminology](Code-Terminology)
- Zone data: `scorecard-explorations/solzreed-zone-report.md`
- Harness census: `scorecard-explorations/runnability.md`
- Quest engine deep-dive: `scorecard-explorations/quests.md`
