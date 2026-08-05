# Root cause — QUEST_NO_START cluster 1533–1548

**Card:** t_0a59cc6c (root cause + fail-before baseline) · **Branch:** `fix/no-start-1533` · **Mechanic:** QUEST-01
**Data provenance:** canonical 1.2 `compact.sqlite3`, md5 `78b3bdbf038db3b927056106efdf91af` (copied read-only to the card workspace for this analysis; the source DB on the aaemu box was never opened for writes).

## The defect

**23 quest templates load into the engine but can never be accepted by a player.**
Every one of them has components, but **zero Start-kind components** (`component_kind_id = 2`), and
**zero accept surfaces** anywhere in the data that could start them. The engine has no Start step
to run, so `Quest.StartQuest()` refuses every one of them.

Exact ids (cluster): **1533, 1535–1549, 1551–1554, 1640, 1830, 1831**
(1534 and 1550 are pure id gaps — no `quest_contexts` row, nothing is ever loaded for them.)

## Fail-before baseline (confirmed 2026-08-05)

Re-ran the census mirroring the verifier predicate against the canonical DB
(`Scripts/quest_no_start_census.sh`, read-only):

```
quest  category_id  zone_id  components  start_comps
-----  -----------  -------  ----------  -----------
1533   28           1        1           0
1535   28           1        1           0
... (through) ...
1830   1            22       1           0
1831   1            1        3           0
RESULT: FAIL — QUEST_NO_START quests above (can never be accepted)
EXIT=1
```

23 quests, exit 1 — exactly the documented cluster. Also captured by the M1 rig
(`QuestNoStartClusterTests`, b85f45e1, t_d5e088ed) and recorded in
`scorecard-explorations/no-start-cluster-1533-1548-evidence.md`.

## Why they can never be accepted (engine proof)

- `Quest.CreateQuestSteps()` (`AAEmu.Game/Models/Game/Quests/NewQuestCode.cs:16-36`) builds one
  `QuestStep` per component **kind** — no Start-kind component ⇒ no Start step.
- `Quest.StartQuest()` (`NewQuestCode.cs:42-56`) does `QuestSteps.TryGetValue(QuestComponentKind.Start)`,
  fails, logs *"Tried to start a quest without a starter component"*, returns `false`.
- `QuestSanityVerifier.VerifyLoadedState` (`AAEmu.Game/Core/Managers/QuestSanityVerifier.cs:188-194`)
  fires `QUEST_NO_START` for exactly this shape (loaded + ≥1 component + zero Start comps).
- The 23 ids were previously in the verifier allowlist (masked to Info at `:289-294`), which is why
  the startup census stayed green — **green ≠ runnable**.

## Component / act map (read-only census, canonical DB)

| quest | cat | zone | lvl | comps | kinds | Start comps | acts |
|---|---|---|---|---|---|---|---|
| 1533 | 28 | 1 | 1 | 1 | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1535–1541 | 28 | 1 | 1 | 1 each | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1542–1549, 1551–1554, 1640 | 28 | 1 | 0 | 1 each | 8 (Reward) | 0 | SupplyCopper + SupplyExp |
| 1830 | 1 | 22 | 0 | 1 | 8 (Reward) | 0 | (act-less) |
| 1831 | 1 | 1 | 0 | 3 | 4, 6, 8 (Progress/Ready/Reward) | 0 | (act-less) |
| 1534, 1550 | — | — | — | 0 | — (no context row) | — | id gaps |

- 25 component rows: 7738–7758 (one per cat-28 quest), 8492 (1830), 8494–8496 (1831).
- 42 `quest_acts` wiring rows (10867–10911 subset) — all `QuestActSupplyExp` / `QuestActSupplyCopper`
  reward acts on the kind-8 components. Shared act *detail* rows (small ids 6/7/8…) belong to many
  other quests and must not be deleted.
- Accept surfaces — **all zero**: `item_accept_quests`, `accept_quest_effects`,
  `doodad_func_quests`, `quest_act_con_accept_components` each have 0 rows referencing the cluster.
- `unit_reqs`: 9 rows with `value1` inside the cluster are **Skill/AiEvent-owned** (kinds 30/23/35 —
  buff-tag/sphere refs), not quest-accept gates (unit_reqs keys on owner_type+owner_id, so a quest id
  in `value1` is a collision, not a dependency).
- No `quest_context_texts` rows for the cluster — these 1.0-era tutorial shells carry no names in
  the canonical 1.2 reference.

## Verdict and fix location (as decided, Josh 2026-08-05)

Verdict: **drop as data, not code** — the cluster is dead legacy content (orphan/dummy shells), and
the fork's data rules keep `compact.sqlite3` read-only reference.

Recommended fix location (landed on this branch, t_5140fb35):

1. **Additive data patch** — `SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql` (guarded
   DELETEs for the 23 contexts, 25 components, 42 act-wiring rows; applied to a DB copy, never the
   canonical file). Drift verified: quest_contexts 4876→4853, quest_components 17851→17826,
   quest_acts 26886→26844.
2. **Verifier allowlist removal** — remove the 23 ids from `QuestSanityVerifier.BuildAllowlist`
   (`QuestSanityVerifier.cs`) so a regression that re-adds the rows re-reports `QUEST_NO_START`
   at **WARN** instead of being masked to Info (allowlist 132 → 109).
3. **Census script** — `Scripts/quest_no_start_census.sh` mirrors the predicate; `--apply-fix`
   demonstrates pass-after on a copy (fail-before 23 → 0).

Restore pointer if a 1.2-era tutorial is ever rebuilt:
`scorecard-explorations/dropped-content-register.md` §2.

## References

- `ROADMAP.md` §M1 — QUEST_NO_START cluster 1533–1548: DROP 2026-08-05 (Josh), t_5140fb35
- `scorecard-explorations/runnability.md` (M1-5c census) — fix-card queue points at the drop; 0 FAIL rows
- `scorecard-explorations/no-start-cluster-1533-1548-evidence.md` — rig + fail-before/pass-after evidence
- `scorecard-explorations/data-defects.md` §5 — classification verdict (c) drop
- `scorecard-explorations/dropped-content-register.md` §2 — drop register + restore pointer
