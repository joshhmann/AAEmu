# G7 human trace — quest 251 (Angry boars): verdict + substitute evidence

> READ-ONLY trace audit, repo `/root/aaemu-dev`, branch `develop`, 2026-09-20.
> Nothing modified, built, or run. Trace is used for **ordering/timing validation ONLY**;
> server code/data stays authoritative for legality/credit (quest chain per
> `scorecard-explorations/mechanics/g4-objective-credit.md` §A:
> 251 → component 633 → act 10473 `QuestActObjItemGather` item **4058 ×3** →
> NPC template **3475** via loot pack **4530** → accept/report NPC **3512**).

## 1. Verdict: NO human trace covering quest 251 exists in the repo

Searched (read-only `grep`/`glob`/`read`) for `251`, `3475`, `4058`, `3512`,
`boar/Boar/meat`, quest/loot/death/turn-in markers:

- `scorecard-explorations/` (incl. `mechanics/g7-*`, `generated/`)
- `playertrace-coverage/` (`summary.md`, `packet-coverage.csv`, `event-coverage.csv`, `sequence-patterns.csv`, …)
- `Scripts/playertrace-coverage/` (parser, coverage engine, dashboard)
- `Docs/` and `docs/`
- `traces/player-actions/` (14 human captures, char `Dingus`, 2026-09-14) and `traces/bot-trajectories/`
- `soak-artifacts/`

Every `251` hit outside code/docs is an elapsed-ms false positive (`elapsed_ms: 2516`
etc.). The only genuine `251` references are **code/docs, never a human packet
capture**: G3/G4 freeze + discovery docs, `Golden-Route-Solzreed.md` (251 = step 2,
gather 3× item 4058 @ NPC 3512, reward 18791 ×1), m2b pilot notes,
`Q4LiveHuntLootE2eTests.cs`. No file in the repo contains a human
accept → target → combat → death → loot → 4058 → progress → turn-in sequence
for quest 251 (or for boar 3475 / meat 4058 at all).

Two structural reasons no such attribution is possible even in principle:

1. **Trace schema carries no quest/item/NPC-template IDs.** `trace_parser.py`
   records only `{Packet, Opcode}` for packets and `{SkillId, TargetObjId
   (runtime objId), X/Y/Z/Yaw/Range}` for skill events. Quest IDs, item
   template IDs, and NPC template IDs never appear in any record, so the
   `quest_accept_and_turnin` capture cannot be tied to 251 / 3512 / 4058 / 3475.
2. **Corpus gap is self-reported.** `playertrace-coverage/summary.md` §7 lists
   "Quest Acceptance, Objective Progression & Turn-In" and "Basic Combat &
   Auto-Attack Lifecycle" as `[UNTRACED ACTION FAMILY]`; §8 ranks
   `combat_basic_melee` and `quest_accept_and_turnin` as the HIGH-priority
   traces still to collect. (Caveat: `packet-coverage.csv` claims 1×
   `CSCompleteQuestContextPacket` + 1× `SCQuestContextCompletedPacket`, but a
   full-file grep over `traces/` finds **zero** records with those names — the
   CSV is stale relative to the current corpus, or the rows describe a removed
   file. No human turn-in packet exists in the corpus as it stands.)

## 2. Closest HUMAN evidence (real player, quest-agnostic): `quest_accept_and_turnin__Dingus__20260914_194206.jsonl`

22,690 records, `elapsed_ms` 0 → 157,316 (≈157 s, 19:42:06 → 19:44:44 UTC).
Character `Dingus` (id 2). Selected event timeline (movement/keepalive collapsed):

| # | elapsed_ms | timestamp (UTC) | direction | event / packet (opcode) | IDs / notes |
|---|---|---|---|---|---|
| 1 | 0 | 19:42:06.891 | — | `lifecycle:trace_started` | Scenario=`quest_accept_and_turnin`, CharacterId=2 |
| 2 | 2018–2056 | 19:42:08.909–.947 | out/in | `SCTargetChangedPacket` (0x084) → `CSChangeTargetPacket` (0x02C) | human picks a target before talking |
| 3 | 4540 | 19:42:11.431 | in | `CSInteractNPCPacket` (0x065) | accept conversation opened (human keypress) |
| 4 | 6946 | 19:42:13.837 | out | `SCQuestContextStartedPacket` (0x147) | accept landed; ~2.4 s after interact (dialogue time) |
| 5 | 6977 | 19:42:13.869 | out+in | `SCQuestContextUpdatedPacket` (0x149) + `CSStartQuestContextPacket` (0x0D5) **same ms** | client echo + server update in one tick |
| 6 | 7017–7018 | 19:42:13.908–.909 | out | `SCItemTaskSuccessPacket` (0x090) + `SCQuestContextUpdatedPacket` ×2 | supply-item grant bundled with accept (~70 ms after Started) |
| 7 | 9000 | 19:42:15.891 | in | `CSInteractNPCEndPacket` (0x066) | human closes dialogue (~2 s after grant) |
| 8 | 22309–22314 | 19:42:29.200–.205 | — | `skill_requested` → `instant_cast` (SkillId **10602**, TargetObjId 44662 Npc, Range 33.05) → `SCUnitPointsPacket` → `SCSkillFiredPacket` → `SCItemTaskSuccessPacket` → `SCMateSpawnedPacket` | item-caster summon (CasterType=Item); mount/pet out ~15 s after dialogue end |
| 9 | 37366 | 19:42:44.257 | — | `skill_requested` → `instant_cast` (SkillId **16064**, TargetObjId **44656** Npc, Range 17.36) → `SCUnitPointsPacket` → `SCSkillFiredPacket`; `CSStartSkillPacket` (0x052) client echo same ms | first combat cast on an NPC |
| 10 | 38120–38161 | 19:42:45.011–.052 | out | `SCAiAggroPacket` → `SCCombatFirstHitPacket` (0x089) → `SCAggroTargetChangedPacket` → `SCTargetChangedPacket` | aggro → first-hit ordering |
| 11 | 38805 | 19:42:45.696 | — | SkillId 16064 again vs same Npc 44656, Range 9.85 (was 17.36) | human closed distance between casts |
| 12 | 39714 | 19:42:46.605 | — | `skill_requested` SkillId **11918** (no `instant_cast` row; `CSStartSkillPacket` echo only) | cast-with-travel/channeled shape differs from 16064 |
| 13 | 55864 | 19:43:02.755 | out | `SCBuffRemovedPacket` → **`SCUnitDeathPacket` (0x071)** → `SCAiAggroPacket` → `SCCombatClearedPacket` (0x086) → `SCTargetChangedPacket` | kill #1: death → combat-cleared → target-changed, same ms |
| 14 | 66688 | 19:43:13.579 | out | same death burst | kill #2, no quest packet (non-objective kill) |
| 15 | **88176** | **19:43:35.067** | out | `SCUnitPointsPacket` → `SCCombatClearedPacket` → `SCBuffRemovedPacket` → **`SCUnitDeathPacket`** → **`SCQuestContextUpdatedPacket` (+0.03 ms)** → `SCAiAggroPacket` → `SCCombatClearedPacket` → **`SCQuestContextUpdatedPacket` (+0.16 ms)** → `SCTargetChangedPacket` | **kill #3: death and quest-progress emitted in the same server tick** — the corpus's only human kill-credit ordering proof |
| 16 | 103311, 128889 | 19:43:50.202, 19:44:15.780 | out | same death burst, no quest packets | kills #4–5, non-objective |
| 17 | 157316 | 19:44:44.207 | — | `lifecycle:trace_stopped` (TotalEvents=22690) | ends on movement; **no turn-in, no loot packet anywhere in file** |

Supporting human ordering from `combat_basic_melee` / `combat_basic_ranged`
(same player, same day): `skill_requested` (SkillId 16064, TargetType Npc,
Ranges 16.78 → 9.98 as the player approaches) → `instant_cast` →
`SCUnitPointsPacket` → `SCSkillFiredPacket`, with `CSStartSkillPacket` as the
client-side echo; deaths (`SCUnitDeathPacket` 19:39:38.893 / 19:40:37.699) follow
the identical BuffRemoved → Death → Aggro → CombatCleared → TargetChanged burst.
**Zero loot records and zero turn-in records exist in any human trace.**

## 3. Substitute — NOT human (BotScenarioRunner rigs + bot E2E). Marked as such.

### 3a. `scorecard-explorations/generated/m7-adventurer-spike.jsonl` — quest 250 fox cull (closest full hunt/loot loop; KILL-objective, not 251's GATHER-objective)

`AdventurerSpikeScenario` via `RunAsScenario ← BotScenarioRunner ←
BotDriveBridge` (scenario-only, rig-faked damage through REAL
`DoOnMonsterHuntEvents`; all 33 records same-ms timestamps, deterministic rig):

`AcceptQuest(250 via Doodad/5047)` → `Observe` → `Move` → **`Target(24576)` →
`Cast(skill 90001)` → `Loot(24576, "looted 1 item(s)", bag entries 1)` →
`AdvanceQuest(250, Progress)`** → repeat ×3 (objIds 24576/24578/24577) →
`AdvanceQuest(250, Reward/Completed)` → `AdvanceQuest(250, Drop/Dropped)`.
One run extends to `quest 330 accepted (Npc/3597)` → `quest 330 completed by
turn-in` (return/turn-in shape). Failure shapes also recorded: `RejectedAction:
nothing to loot (empty or already looted)` and `no attackable npc template 3492
visible` (WrongDecision).

### 3b. `AAEmu.IntegrationTests/E2e/Q4LiveHuntLootE2eTests.cs` — quest 251 bot E2E (the ONLY 251 kill→loot evidence; bot, not human)

Legs: `enter-world` → `quest-hold` (setLevel 10, teleportToNpc, `accept` 251 —
note header still cites acceptor NPC **2425**, superseded by true giver
**3512** per G3 freeze) → `hunt-setup` (teleportToNpc **3475**, pin live boar
objId, meatBefore=InvCount(**4058**)) → `hunt-kill` (cast rotation skills
**18131**→fallback **18134**, `maxCasts=60`, death observed on hpBefore==0 /
hpAfter==0 / !targetAlive edge) → `grant` (`loot` op; conservation proof:
`granted == containerBefore − remaining`, `remaining==0`, `meatDelta>=1`,
op↔test double-read `meatAfter−meatMid == meatDeltaOp`, all bag deltas gains)
→ `retry-empty` (second loot grants nothing). Foreign-take shape is rig-proven
(`GameplayActorLootGrantTests`), not live.

## 4. Per-transition classification (trace = ordering/timing only)

Applies to the §2 human timeline first; substitutes (§3) inherit the same
labels, flagged `[SUBSTITUTE]`.

| # | Transition | Class | Rationale |
|---|---|---|---|
| 2 | target-select before talking (`CSChangeTargetPacket` → `SCTargetChangedPacket`) | HUMAN BEHAVIOR | player choice of whom to approach; engine only echoes |
| 3 | opening dialogue (`CSInteractNPCPacket`) | HUMAN BEHAVIOR | when/whom to hail is player-driven |
| 4–6 | accept burst: Started → (Updated + client echo, same ms) → ItemTaskSuccess + Updated ×2 within ~70 ms | ENGINE REQUIREMENT | server-gated packet bundle and tick-coalescing; bot MUST tolerate same-ms Started/Updated/ItemGrant in any sub-order within the tick |
| 7 | closing dialogue promptly (`CSInteractNPCEndPacket` ~2 s post-grant) | HUMAN BEHAVIOR | pacing choice, no server gate |
| 8 | summon-then-fight (mount/pet via item skill 10602 before combat) | LIKELY POLICY CHOICE | sensible bot default (travel/companions first); not mandatory — engine never requires it |
| 9/11 | combat casts interleaved with movement; range shrinks 17.36 → 9.85 between casts on same objId | HUMAN BEHAVIOR (movement timing) + ENGINE REQUIREMENT (range legality) | *when* to step closer is choice; that a cast only lands in range is server-gated |
| 10 | aggro → CombatFirstHit ordering | ENGINE REQUIREMENT | server-emitted combat state machine |
| 13–16 | death burst: BuffRemoved → `SCUnitDeathPacket` → Aggro → CombatCleared → TargetChanged, same ms | ENGINE REQUIREMENT | fixed server emission order; bot MUST treat death+cleared+target-changed as one atomic tick |
| 15 | death + `SCQuestContextUpdatedPacket` in the same tick (objective kill) vs death alone (non-objective kills) | ENGINE REQUIREMENT | kill-credit fanout (`DoOnMonsterHuntEvents`) is server-side; bot MUST NOT assume every death advances the quest, and MUST re-read quest state after each kill |
| 17 | no loot / no turn-in observed; trace ends on movement | HUMAN BEHAVIOR (session cut) + corpus gap | nothing engine-mandated; loot-after-kill and return-to-giver are **[SUBSTITUTE]**-only (Q4 grant legs; spike 330 turn-in) and stay unvalidated for human timing |
| S1 | `[SUBSTITUTE]` spike: Target → Cast → Loot → Advance per corpse, ×3, then Completed → Dropped | LIKELY POLICY CHOICE (loop shape) + ENGINE REQUIREMENT (credit/advance/drop transitions) | per-corpse loot-before-advance is the sane bot default; that 251 needs ×3 meats and drops after completion is data/server fact |
| S2 | `[SUBSTITUTE]` Q4: pin-one-boar, bounded cast rotation (≤60), caller-delta conservation, retry-empty grants nothing | LIKELY POLICY CHOICE (budgets/re-anchor) + ENGINE REQUIREMENT (grant conservation, no-double-grant) | retry-after-success and double-read cross-checks are mandatory bot policy; cast cap is policy |

Counts: HUMAN BEHAVIOR 5 (rows 2, 3, 7, 9-part, 17) · ENGINE REQUIREMENT 7
(rows 4–6, 9-part, 10, 13–16, 15, S1-part, S2-part) · LIKELY POLICY CHOICE 4
(rows 8, S1-part, S2-part, 9-movement-timing shared).

## 5. Ordering/timing facts the trace validates (and what stays authoritative)

- Accept bundle collapses into one tick (Started/Updated/ItemGrant ≤ ~70 ms):
  bots MUST NOT sequence accept-steps across ticks.
- Objective-kill credit is same-tick as death; non-objective deaths emit no
  quest packet: bots MUST re-observe quest state per kill, never count corpses.
- Death handling is an atomic burst (death → cleared → target-changed):
  re-targeting before the burst completes races the engine.
- NOT validated by any human trace: corpse-loot timing/packets, 4058 grant
  conservation, Ready transition at 3× meat, return travel to 3512, turn-in
  dialogue, reward 18791. For those, Q4 + spike are ordering hints only —
  legality/credit stays with `QuestActObjItemGather.OnItemGather`
  (item-4058 identity, bag-count credit) and `Npc.DoDie` attribution.
