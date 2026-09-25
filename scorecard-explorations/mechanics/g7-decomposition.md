# G7 Decomposition — damaging 3475 → death → corpse → loot → 4058 → credit

READ-ONLY planning. No repo file modified, nothing built or run (this doc is the
sole write). Branch `develop` @ 2026-09-20. G3–G6 frozen (untouched).
Incoming state (G6 freeze end): quest 251 ACTIVE, damaging-but-alive 3475 under a
live quest-owned AutoAttack loop; HP 286→259 trailing, prey alive, meat 0→0,
quest Progress (`scorecard-explorations/mechanics/g6-closeout-freeze.md:9-20`).
Canonical seam (locked): `BotActionCommandQueue → GameplayActor → engine`.
Mission: sustain to kill → observe death → loot the corpse → 4058×3 credit → Ready,
each as its own bounded gate. This note is sections H–K of the G7 report:
**(H) split recommendation, (I) ownership, (J) gates, (K) risks.**
Perception (§3), randomness (§4), diagnostics/taxonomy (§5), fixtures (§6),
traces + G8 handoff (§7) are the working detail those sections rest on.

Static chain (code-verified, not re-proven here): NPC template 3475 (Solzreed Boar)
→ loot pack 4530 → item 4058 (×3); credit-time identity is the item
(`QuestActObjItemGather.OnItemGather` matches act id + item 4058),
kill-time identity is the template
(`AAEmu.Game/Core/Managers/Bots/QuestObjectiveTargetSelector.cs:28-33`).
Progress act: `QuestActObjItemGather` item 4058 (expected act 10473, detail 616;
`QuestObjectiveTargetSelector.cs:49-53`); relevance = 251 active AND 4058 < 3
(`QuestObjectiveTargetSelector.cs:101-108`).

---

## H. DECOMPOSITION — verdict: four-gate split (G7a–d), not one gate

**Recommend G7a (kill only) / G7b (corpse observed) / G7c (loot → 4058 grant) /
G7d (acquisition → Ready credit).** Rationale from ownership boundaries (§I):
the four phases have four different owners and four disjoint failure vocabularies.
A single end-to-end gate from the G6 state to Ready would conflate them: one
`Starvation` or one meat-delta-zero tells the investigator nothing about whether
combat stalled, death was missed, the corpse was unlootable, or the pack simply
rolled no meat. The G3→G6 precedent is one predicate family per gate
(G4 SELECT-ONLY `G4QuestObjectiveTargetTests.cs:30-37`; G5 stops at range, never
attacks `G5QuestPursuitGateTests.cs:46-48`; G6 stops at first damage, no kill
`G6QuestCombatGateTests.cs:52-54`) — G7 continues that doctrine, it does not
break it.

Least-harness-manipulation argument: all four gates reuse the SAME staging ops
as G4–G6 (fresh bot, level 10, vitals refill, 251 pre-hold at true giver 3512,
`teleportToNpc(3475)` + `PollNpcObjId` proof, START snapshot, then observe-only;
`G4QuestObjectiveTargetTests.cs:24-28`, `G6QuestCombatGateTests.cs:23-28`). The
marginal cost of the split is one START snapshot + one PASS predicate per gate,
not new fixture machinery. Chaining is allowed (G7b may open on G7a's corpse,
G7c on G7b's observed corpse) but each gate re-takes its START snapshot, so a
chained corpse despawn reads as a named `HARNESS/corpse-gone` setup failure,
never as a loot failure (§5).

Single-gate variant (rejected except as a post-pass sweep): one run from the G6
state to Ready. Cheaper in wall-clock (one staging), but fault isolation is nil
and the randomness question (§4) forces either unbounded repeats inside one
verdict or a faked deterministic pack — both prohibited. Acceptable ONLY as a
final integration sweep after G7a–d pass, with no new claims attached.

No new owners, no new subsystems, no new queue kinds are proposed anywhere
below. GOAP (`BotWorldStateProvider.TargetDead/HasCorpseToLoot`,
`AAEmu.Game/Core/Managers/Bots/Goap/BotWorldStateProvider.cs:217-225`) stays
unwired and uncited as production evidence (production executor is
`BotRoamStepExecutor`, per `gap-observe-scale.md` §4); the scenario-library kill
seams (`IKillCreditSeam`, `LevelingLoopScenario.cs:429-435`) stay in the library
— production-lane kills are real cast/AutoAttack damage only (Q4 precedent,
`Q4LiveHuntLootE2eTests.cs:110-115`).

---

## I. OWNERSHIP — existing components only

| Phase | Owner (behavior) | Owner (capability / engine truth) |
|---|---|---|
| Combat continuation (sustain loop to death) | `QuestBehavior` — `CombatProposal` (`QuestBehavior.cs:677-755`) + `CombatGoal = "quest.objective-combat"` (`:80-81`) + priority 23 below pursuit 24 (`QuestDecisionScenario.cs:60-65`); quest-side teardown is G7's by explicit debt assignment (`g6-closeout-freeze.md:132`) | `GameplayActor.AutoAttack` (`GameplayActor.cs:779-848`) / `StopAutoAttack` (`:850-869`); engine owns damage + death (`Npc.DoDie`, `Npc.cs:839-842`) |
| Corpse detection (death observed, lootable named) | `QuestBehavior` — a `target-dead` withdrawal predicate already exists (`QuestBehavior.cs:707-711`, diag `validate=target-dead`); G7 promotes it from withdrawal-reason to detection event. Loot-once memory beside `LastStopHold`/`LastPursuitIssue` wake caches (`:89-96`) or the `ActorEffectLedger` (`ActorIdempotency.cs:52-60`); NOT `BotMemory.HasLootedCurrentTarget` (GOAP-context memory, `BotMemory.cs:43`) | Engine owns death truth (`Unit.DoDie` → `LootingContainer.GenerateLoot`, `Unit.cs:532-533`); killer target-clear as corroboration (roam precedent `:777-781`) |
| Loot execution | `QuestBehavior` dispatch (new goal-guarded arm beside the AutoAttack arm, `QuestBehavior.cs:816-822`) | `GameplayActor.Loot` (`GameplayActor.cs:1733-1762`, CSLootOpenBag/lootAll path `:1753-1761`); queue kind exists (`BotActionKind.Loot`, `BotActionCommandQueue.cs:30`, dispatch `:581-582`) but quest legs dispatch direct live-actor verbs (G6 precedent: no queue kind for AutoAttack "by design", `QuestBehavior.cs:816-821`) — either route is engine-true; stay on the direct live-actor route for consistency |
| Credit observation (4058 → objective → Ready) | `QuestBehavior` / `QuestObjectiveTargetSelector.IsRelevant` (bag 4058 < 3, `:101-108`; funnel have/need, `:122-125`) + Ready-guard (`QuestBehavior.cs:131-135`: Ready quests get turn-in proposals only, never Advance) | Engine owns credit: `LootingContainer.TryTakeLoot` → bag grant → `QuestManagerEvents.DoItemsAcquiredEvents` → `OnItemGather` (`QuestManagerEvents.cs:86-103`) → `QuestActObjItemGather.OnItemGather` → `SetObjective` from live bag count (`QuestActObjItemGather.cs:70-78`); `RunAct` bag-evaluated (`:25-29`) |

Non-owners (explicit): the roam wildlife hunt leg (`BotRoamStepExecutor.cs:734-860`)
owns loot+teardown for OPPORTUNISTIC wildlife (`:750-790`: `actor.Loot` triage
granted/no-op/rejected `:761-769`, `StopAutoAttack` `:772-775`, target-clear
`:777-781`, pending-move preempt `:786-790`) — but it is suppressed while the
quest leg is active (`:737`: hunt skipped while `QuestLegActive`), so it can never
own the 251 corpse. GOAP owns nothing in production (unwired). The bridge
`loot` op and `BotDriveBridge` are fixture-only (Q4 legs use them as the
harness hand, `Q4LiveHuntLootE2eTests.cs:121-122`; production behavior must not
cite them).

Teardown ordering (roam precedent, quest-side mirror): loot first, then
`StopAutoAttack`, then target-clear + state reset (`BotRoamStepExecutor.cs:750-790`).
A quest-side teardown that stops the loop BEFORE looting risks the corpse-despawn
race (§K); a teardown that never stops the loop leaves `IsAutoAttack` live on a
dead target (note `CombatProposal` withdraws with `already-live` while the loop
runs, `QuestBehavior.cs:723-727` — the loop itself must be stopped by the new
teardown arm, it does not self-cancel on death in any code found; [INFERENCE]
flagged as such).

---

## J. GATES

### J.1 PERCEPTION — minimum fields for G7: what exists vs missing

Snapshot today (`ActorObservation.cs:22-91`, copied 1:1 into `BotObservedContext`,
`BotDecisionProposal.cs:67-103`): self pose/HP/MP, `CurrentTargetObjId` (id ONLY),
nearby id lists (unbounded ids, no vitals), `ActiveQuestIds` (ids ONLY — no
Status/Step/objectives), `BagItemCounts` (4058 count ✓), money/labor/party.

| G7 need | Status | Source |
|---|---|---|
| target alive/dead | MISSING from snapshot — live recheck only | `npc.Hp > 0` via `ParentWorld.GetNpc` (`QuestBehavior.cs:697-703`; selector `:169-174`) |
| corpse / lootable flag | MISSING — no `HasCorpseToLoot` outside GOAP flags | GOAP-only (`BotWorldStateProvider.cs:217-225`); quest side must derive: dead + `LootingContainer.Items.Count > 0` (container pre-flight exists at `GameplayActor.cs:1745-1748`) |
| target objId + template + distance | objId in snapshot; template/distance live-only | `npc.TemplateId` + `MathUtil.CalculateDistance` flat (`QuestBehavior.cs:728-735`; selector `:168`) |
| bag 4058 (have/need) | EXISTS | `BagItemCounts` (`QuestObjectiveTargetSelector.cs:123-125`, have/need in funnel `:222-224`) |
| quest progress (Status/objectives/Ready) | MISSING from snapshot — live `Character.Quests` read | `ActiveQuests.GetValueOrDefault(questId).Status` (`QuestBehavior.cs:131`; scenario precedent `QuestDecisionScenario.cs:113` per gap audit) |
| despawn (corpse timer) | MISSING, no observer anywhere found (UNKNOWN for NPC corpses; doodad `Despawn` guard exists at `GameplayActor.cs:1580-1584` as Interact precedent) | Corpse expiry = spawner `DespawnTime` floor + 2 s post-loot minimum (`LootingContainer.cs:305-317`); see §K race |

**Recommendation: no snapshot extension, no new subsystem for G7a–c.** The
live-recheck pattern (resolve `GetNpc` at proposal time, never store/cache) is
the documented selector contract (`QuestObjectiveTargetSelector.cs:14-19`) and a
blessed "intentional live recheck" class (`brain-boundaries.md` §2.1). Extending
`ActorObservation` with per-target vitals would widen every `Observe()` (S2/S9
allocation pressure, `gap-observe-scale.md` §1.7) for one pinned target. If G7d
polling proves too chatty, the bounded additive option is a single
`PinnedTargetSnapshot{ObjId, TemplateId, Hp, Alive, DistanceM}` + `Quest251Progress
{Have, Need, Status}` on the observation — additive fields only (v2 precedent,
`ActorObservation.cs:15-20`), never a second world model. Decision: defer until
G7d asks for it with a measured wake-cost complaint.

### J.2 RANDOMNESS STRATEGY — assume worst case (probabilistic)

**Verdict: UNVERIFIED whether pack 4530 guarantees 4058.** Not read (read-only
constraint; the `compact.sqlite3` `loots`/`loot_groups` rows for pack 4530 were
not queried — no DB file in-repo; game-data access is code-shaped only):
`LootGameData` loads per-row `drop_rate/min_amount/max_amount/always_drop`
(`LootGameData.cs:40-55`) with a `drop_rate<=1 → 10000000` normalization (`:40`,
`:73` — scale unknown, [INFERENCE] reads as per-10M); generation rolls
`GeneratePackNewV2(lootDropRate, lootGoldRate, …)` with aggro/killer multipliers
(`LootingContainer.cs:118-208`). Q4's grant assertion is `meatDeltaOp >= 1`, NOT
`== 3` (`Q4LiveHuntLootE2eTests.cs:147-155`) — consistent with variable yield,
and one kill demonstrably does NOT have to satisfy ×3. `ResolveSource` proves
the LINK (3475→4530→4058 rows exist, `QuestObjectiveTargetSelector.cs:266-282`)
but says nothing about RATES.

Strategy (no fakes, no counter writes — `IKillCreditSeam` stays library-only):

1. **Split the credit gate (primary).** G7c proves loot→grant MECHANICS on
   whatever the pack yields (Q4 conservation idiom: granted == container
   before−after, every bag delta a gain, op↔test double-read,
   `Q4LiveHuntLootE2eTests.cs:145-161`); G7d proves acquisition→Ready over
   as many kills as the pack demands. A dry kill (no meat) is then a PASS of G7c
   mechanics with `meatDelta=0` + a G7d loop iteration, never a failure.
2. **Bounded repeated kills with strict budget (secondary).** Per-kill caps from
   precedent: Q4 `maxCasts = 60` (`:217`), spike `SustainMaxRounds = 30`
   (`AdventurerSpikeScenario.cs:181`), roam 30 s engagement cap
   (`BotRoamStepExecutor.cs:748`); hunt-level budget e.g. ≤ N kills or ≤ T
   minutes to reach 4058×3, then fail `CREDIT/budget-exhausted` honestly.
   No-progress exclusion (spike `NoProgressSkipRounds = 3`,
   `AdventurerSpikeScenario.cs:152-156`) ports as leash/undamageable handling.
3. **Deterministic fixture: REJECTED.** No drop-rate override, no seeded pack,
   no synthetic `DoOnMonsterHuntEvents` in the production lane — any of those
   would prove the fixture, not the chain (cf. Q4 "no quest-free substitution",
   `:24-26`, and G6 zero-side-effect doctrine, freeze §e.5).

### J.3 DIAGNOSTICS + TAXONOMY — bounded funnel, predicate-named failures

Extend the two existing Info lines, additively, values-without-spaces
(precedent `QuestBehavior.cs:766-789`, `LogCombatOutcome :885-897`):

- `QuestObjectiveTargetDiag` — append `death=[…]` + `loot=[…]` + `credit=[…]`
  segments (all `-` until that phase arms).
- `QuestObjectiveCombatDiag` — exists today with
  `cycle/char/actor/target/template/verb/trace/state/detail/hpBefore/hpAfter/
  delta/targetAlive` (`:891-896`); add `loopLive/isAutoAttack/pinned` (gate
  instant), `dead/corpseId/lootable/containerBefore/granted/itemBefore/
  itemAfter/progressBefore/progressAfter` as each phase lands, plus `trace`
  (already there) + `CycleId` (already `cycle=`).
- Full ordered funnel per wake (all bounded scalars, no lists):
  `loopLive → hp → dead → corpse → lootable → candidate → dispatched →
  completed → itemBefore/After → progressBefore/After + trace/CycleId`.
  Reuse `TruncateSweep` (`:921-922`) for details; Info (not Debug) so the lane
  file target records it (`:763-764` precedent).

Failure taxonomy — stage prefix × §17 reason (`ActorFailureReason`,
`IGameplayActor.cs:1115-1134`: WrongDecision/Navigation/RejectedAction/
StateTransition/Persistence/Starvation/FidelityError), in current code terms:

- `COMBAT/*` — existing diag verbs as names: `already-live`, `out-of-range`,
  `target-unassigned`, `target-not-hostile/attackable/visible`, `target-lost`,
  `no-selection/not-relevant` (`QuestBehavior.cs:685-735`); all WrongDecision
  at DECIDE, RejectedAction at dispatch (`GameplayActor.cs:790-791`,
  `unknown auto attack skill` `:827`).
- `DEATH/*` — `target-dead-withdrawn` (expected pre-loot observation, not a
  failure); `kill-timeout` (TimedOut Starvation on the sustain budget);
  `loop-still-live-after-death` (teardown omission — StateTransition family).
- `LOOT/*` — engine refusals verbatim: `owner-not-found`,
  `out-of-loot-range` (`MaxLootingRange = 200f`, `LootingContainer.cs:32` —
  effectively never fires at 3 m hold; corpse-loss reads as owner-not-found),
  `empty-or-already-looted` (`GameplayActor.cs:1741-1748`);
  `grant-zero` (Completed with 0 granted — Q4 `remaining==0` + `meatDelta==0`
  dry-kill shape, `:178-184` retry-empty idiom).
- `ITEM/*` — `caller-delta-mismatch` (Q4 conservation violation, `:148-155`);
  `foreign-take` (non-Completed terminal — rig-proven shape,
  `GameplayActorLootGrantTests` per Q4 doc `:22`).
- `CREDIT/*` — `objective-unchanged` (OnItemGather fired, `SetObjective` did
  not move — `QuestActObjItemGather.cs:70-78`); `not-ready-after-3`
  (`RunAct` false at have==3, `:25-29`); `advance-refused`
  (StateTransition quest-not-active, `IGameplayActor.cs:670-673`).
- `HARNESS/*` — `SETUP/*` (G4–G6 convention: `accept-251`, `boar-unresolved`,
  `stage-range`, `start-snapshot`), `corpse-gone` (chained corpse despawned
  before START), `CONFIG/bootstrap-off` (G3/G6 precedent), `leash-reset`
  (prey reset mid-sustain — debt-listed, freeze §g).

### J.4 FIXTURES + PASS shapes

Shared staging (all gates): fresh bot, level 10, vitals ≥0.95, 251 pre-held at
giver 3512, staged at a live 3475 spawner (`teleportToNpc(3475)` +
`PollNpcObjId`), ONE authoritative START snapshot (charId, quest state, target
objId/template/HP, snap↔actor identity, flat distance, activity, CycleId, meat
4058, money, turnIns — G5/G6 shape, `G6QuestCombatGateTests.cs:324-359`), then
**observe-only** (no casts/loots/moves post-START except the autonomy under
test; exclusion scan for `Cast`/`Loot` hits like G6 `ScanCombatExclusion`,
`G6QuestCombatGateTests.cs:549-551`).

| Gate | Starting state | PASS shape (all must hold) |
|---|---|---|
| G7a kill-only | G6 end state: 251 ACTIVE, live 3475, quest-owned AutoAttack loop live, meat<3 | prey dead (`Hp<=0` / `GetNpc` null + `PollNpcObjId` gone); loop torn down (`IsAutoAttack` false, `AutoAttackTask` null, target cleared — roam `:772-781` shape); **zeros held**: meat delta 0, money delta 0, quest still Progress, turnIns 0 (anti-leak, G6 `COMBAT-CREDIT-LEAK` precedent `:632-634`); funnel shows `dead` + combat trace Completed |
| G7b corpse observed | Fresh unlooted corpse (G7a chained, or re-staged kill; START re-taken; `HARNESS/corpse-gone` if despawned) | quest-side detection names corpse (diag `corpse={objId} lootable=true`, container non-empty — test-side `containerBefore>=1` like Q4 `:148-150`); **no loot dispatched yet** (grant zeros, meat unchanged); quest Progress |
| G7c loot → 4058 | Unlooted corpse + 251 ACTIVE + in loot range | `Loot` Completed; Q4 conservation: granted == container before−after, remaining==0, every bag delta a gain, op↔test double-read match (`:147-161`); meat after ≥ meat before (dry `meatDelta==0` allowed ONLY with container proof + counted as G7d iteration); retry-after-success grants nothing (Q4 `:178-184`); quest Progress-or-Ready (status read, not asserted — G7d owns it) |
| G7d acquisition → credit | Bag 4058 < 3, 251 ACTIVE, live 3475 ground | Bounded repeats (§J.2) until bag ≥ 3; objective counter == 3 (`SetObjective` from bag count, `QuestActObjItemGather.cs:77`); quest **Ready** (RunAct true, `:25-29`); turnIns 0 (turn-in is G8); relevance flips false (have≮3, `:101-108`) so combat proposals withdraw |

Single-gate variant: G6-state → Ready in one verdict. Rationale against: see §H
(conflation + randomness trap). Keep ONLY as a post-pass integration sweep.

---

## K. RISKS (bounded, code-backed)

1. **DPS/leash cap (known debt, freeze §g).** Boar leash-reset vs level-10 DPS
   may cap G7a at sustained-damage not death — the fixture concession (spike to
   Level 50) is pre-authorized but changes the fixture, not the gate; a
   `HARNESS/leash-reset` trip with HP-sawtooth evidence keeps it honest.
2. **Corpse-despawn race.** Corpse floor = spawner `DespawnTime`, min 2 s
   post-loot (`LootingContainer.cs:305-317`); spawner `DespawnTime` value for the
   3475 ground is UNKNOWN (not read). G7b/c budgets must sit well inside the
   observed despawn window; first observation calibrates, code does not promise.
3. **`OutOfCombatRecoveryModule` (priority 85) preempting post-fight wakes**
   (freeze §g) — observe, never disable; a wake stolen by recovery reads as
   `DEATH/loop-still-live-after-death` noise, diagnose via activity field.
4. **Tag/aggro credit shape.** `Npc.DoDie` credits eligible tag-team members in
   range, else the character-killer fallback (`Npc.cs:843-879`); contributors get
   `DoOnMonsterHuntEvents` (`:1013-1021`). Solo-bot lane is the fallback path —
   safe; any second damage dealer (another bot, guard) risks `ITEM/foreign-take`
   or missing kill credit. Single-bot lane only.
5. **Loot-range is a non-risk; busy-reject is the risk.** `MaxLootingRange =
   200f` (`LootingContainer.cs:32`) never binds at 3 m hold; the live risk is
   single-writer `Rejected(StateTransition, busy)` (`GameplayActor.cs:46-48`,
   queue `:446-448`) if teardown (loot→stop→clear) collides with a Running leg —
   teardown must sequence through one dispatch per wake (roam precedent issues
   them inline `:754-781`, but the quest leg only runs while the actor is idle,
   `:711` — a Running leg defers the whole teardown a wake).
6. **Audit retention under kill-loop volume.** Caps: sink 10 k drop-oldest /
   1 k flush (`PlayerBotAuditSink.cs:36-39`, `:71-98` per gap audit), queue
   history 1024, trace API 100. Bounded repeats (§J.2) stay orders of magnitude
   below; per-tick full-observation logging stays prohibited
   (`gap-observe-scale.md` §1.7).
7. **251 is an ItemGather quest, not a MonsterHunt quest.** Kill credit
   (`DoOnMonsterHuntEvents`, `QuestManagerEvents.cs:169-203`) is IRRELEVANT to
   251's Progress act — only the bag count via `OnItemGather` moves it
   (`QuestActObjItemGather.cs:70-78`). Any gate asserting hunt counters for 251
   is testing the wrong act. [INFERENCE] flagged only as emphasis: the code is
   explicit.

---

## TRACES — existing human/engine refs for select → attack → damage → kill → loot

- **select:** G4 funnel `QuestObjectiveTargetDiag` + `Target (targeting {id})`
  decide history (`g6-closeout-freeze.md:12-15`); selector
  (`QuestObjectiveTargetSelector.cs:211-220`); dispatch `SetTarget` only
  (`QuestBehavior.cs:804-806`).
- **attack:** G6 `QuestObjectiveCombatDiag` + `AutoAttack (auto-attack started
  (2) on {id})` terminal (`g6-closeout-freeze.md:12-16`); verb
  (`GameplayActor.cs:840-847`); role/skill choice engine-mediated
  (`:800-808`).
- **damage:** G6 trailing-wake HP 286→259 idiom (freeze §b: gate predicate is
  loop-live + pinned, damage is trailing observation); `combatHpBefore` proposal
  time → `hpAfter` post-dispatch (`QuestBehavior.cs:885-897`); Q4 per-cast
  hpBefore/hpAfter + `targetAlive` edge (`Q4LiveHuntLootE2eTests.cs:236-245`).
- **kill:** Q4 hunt-kill leg, real cast damage, bounded 60 casts
  (`:110-115`, `:207-276`); spike `HUNT-KILL` stages credited
  (`AdventurerSpikeScenario.cs:556-559`); engine `Npc.DoDie →
  QuestManager.DoOnMonsterHuntEvents` (`Npc.cs:839-878`,
  `QuestManagerEvents.cs:169-203`); leveling-loop hunt-leg contract, LIVE vs RIG
  paths (`LevelingLoopScenario.cs:121-129`).
- **loot:** Q4 grant conservation + retry-empty (`:117-184`); roam granted /
  no-op / rejected triage (`BotRoamStepExecutor.cs:754-769`); `GameplayActor.Loot`
  contract (`GameplayActor.cs:1733-1762`); foreign-take shape rig-proven
  (`GameplayActorLootGrantTests`, per Q4 doc `:22`); M1M2 251 contract row
  `(4058, 3)` expectations (`M1M2ReplayScenario.cs:94-95` — replay context, not
  autonomy evidence).

## G8 HANDOFF (one paragraph, no deep plan)

G7d ends with quest 251 **Ready**, bag 4058×3 (consumed at turn-in —
`QuestActEtcItemObtain.QuestCleanup` consumes on completion,
`QuestActEtcItemObtain.cs:50-56`, same family as the gather act), bot wherever
the last corpse fell. G8 (Ready → return → Talk → turn-in) is a NEW gate family
on the frozen G7: return travel to the report NPC (251's report flow; true giver
3512 ground is the G3-proven geography, `G3AutonomousQuestAccept3512Tests.cs:33-36`;
E2E lane precedent swaps report NPC 3512 at DATA level for defect-harness runs,
`E2eStack.cs:761-765` — G8 must pin the CANONICAL report target), Talk credit
through the real `DoTalkMadeEvents` path (`QuestManagerEvents.cs:130-162`) via
the `Talk` contract action (void-talk refused fail-closed,
`IGameplayActor.cs:753-772`), then `TurnInQuest` at the live NPC
(`:680-683`) with reward/persistence proof; return-travel ownership (quest
pursuit proposal vs route layer) and reporter-range preconditions are G8's
discovery pass, not decided here.
