# PlayerBot GOAP Planning Architecture & Scaling Guide

Goal-Oriented Action Planning (GOAP) for ArcheAge 1.2 PlayerBots in AAEmu.

- Audience: Contributors
- Latest native evidence: segmented82a11f6dc101 failed after25.902 seconds on GetByte(farm_type), while actual SQL schema is INT. ColdDb1 identity/maturity and native Harvest3 were observed; a second paid75 seed and native cropDb2 followed. Saved850LP99/native14+earnedraw3/Db2 and all prior failed receipts are preserved; cleanupErrors0 and sequential Game/Login/DB0. All owned resources are stopped. One-line typed-reader proposal is unapplied; full online growth, food/healing/refill/325SQL acceptance remains unproved. Temporary adapter removal remains required after success.

## Current use and onboarding

Begin with the [new-session bootstrap](../../START-HERE.md#new-session-bootstrap),
[current session state](../../.omp/state/CURRENT.md#session-resume) and
[E132 current developed capability baseline](../../ROADMAP.md#current-developed-capability-baseline).
The user-approved [integration direction](../../PROJECT-CONTROL.md#playerbot-integration-direction)
and [extension roadmap](../../ROADMAP.md#playerbot-extension-roadmap) reuse existing
PlayerBot foundations to prove supported goal-to-goal autonomy. No new brain,
exhaustive human parity or module-by-module AzerothCore port is required.
Necessary shared-core repairs follow the [core authorization](../../PROJECT-CONTROL.md#server-core-integration-authorization).

Current wiring is **production activity arbiter + roam**, a **quest-only behavior
runtime**, and a **separate hosted homestead GOAP** path—not universal GOAP ownership
of every activity. The [current ownership contract](../../PROJECT-CONTROL.md#current-playerbot-ownership-contract)
is authoritative; this page does not choose a replacement design. Reuse the existing
planner where appropriate rather than introducing another brain. E132 distinguishes
actual source capability, wired opt-in policy and historical live proof: farm seed
buy/surplus sell are already coded; provisioned housing claim → construct/restart has
historical proof; commerce/group scenarios are not autonomous policy.

The [supported-capability contract](../../PROJECT-CONTROL.md#playerbot-supported-capability-contract)
retains all seven criteria, including recovery from interruption **or** failed action.
The [current checkpoint](../../STATUS.md#current-playerbot-policy-checkpoint) promotes
no grade or H verdict; historical A5 closure stays scoped, and a farm/private plot
is not a universal quest gate. The next [bounded priority](../../ROADMAP.md#next-bounded-playerbot-priority)
is finite existing-owner A → eligible B, including the same NPC, preserving the
`AdvanceEnter` guard/counter and repairing only observed failure.
E130 recovery branch `review/branch-recovery-2026-10-10` at
`8024926a718a11f754abd2d92716e1c421fc22ce` is unmerged pending the user's decision.
Future implementation/gates are not completed or allocated here; the detailed
Activity-Spine campaign remains unratified.

## Owned healing, ordinary rest and returning to a quest

Verified locally in [E287](../../.omp/state/EVIDENCE.md#e287--d111-owned-healingrestresume-qualified-locally-acquisition-remains-separate):
native recovery 19/19, actual native-thread callback smoke, full repository and
archaeology gates. Fixtures provide initial supplies/metadata; this is not live
or human/client recovery acceptance.

D111 keeps the bot on its existing actor and scheduler. Recovery starts below
75% HP or 60% MP, and finishes only after both reach 95%; those thresholds are
unchanged. Repeated wakes continue recovery instead of starting unrelated patrol
or wildlife work.

The bot may use an already-owned item whose ordinary self-targeted effects are
supported HP healing. Item category or name is not evidence: a MaxMana bonus is
not an MP refill. Quest-protected items, unsupported effects and native refusals
do not grant recovery or silently consume another item. Acquisition and new
merchant stock are outside this card.

A delayed item remains pending until its native cast/effects finish. Applying a
healing buff does not mean the bot has recovered; its ordinary timed ticks must
actually change HP. Rest changes stance, not points: the normal world updates own
HP/MP regeneration, including the normal post-cast MP pause. Thirty seconds
without observed progress produces a named block, not a promised recovery rate.
Changed relevant facts can make the bot eligible again.

Recovery waits for a valid foreign request to reach a safe boundary. Combat,
death, supersession, logout or generation replacement release only recovery-owned
work and posture. The chosen quest is retained, but its old execution plan is not
replayed: after recovery, ordinary quest planning chooses fresh work.

The evidence is isolated native-runtime/fixture proof, not deployment, supply
self-acquisition or human/client acceptance. Conservatively selected healing
excludes plot, channelled, area, conditional and mixed-effect item uses. The
general asynchronous item lifecycle does not establish those gameplay variants
as recovery support. Current receipts and verification state remain in
[CURRENT](../../.omp/state/CURRENT.md#session-resume) and the
[evidence ledger](../../.omp/state/EVIDENCE.md).

**D112 / [E290 owned-craft prerequisite](../../.omp/state/EVIDENCE.md#e290--owned-craft-cancellation-qualified-supplied-input-production-only), accepted locally:** the server now cancels the exact bot-owned native craft before publishing interruption or timeout, including external queue expiry. Only that unique Skill/Doodad/queue is released; a foreign replacement, even the same recipe, and foreign generic skills survive. Native effects already committed are reported Completed. Delayed observed ApplyEffects and EndSkill share the existing execution lock; ordinary unobserved human crafting keeps its original path.

Actual native supplied-input processing/craft/bread healing passes8/8; focused62/62, full4688/4687pass0fail1namedliveoptinskip/compiler0/0/MCP44+24 and archaeology163/full24 qualify exact source68. Independent review was SOURCE ONLY. Fixtures supply grain/knife/labor, world/bench/vitals; MySQL setup refusals and absent Cooking-group/native fallback are disclosed in E290. Bread's observed HP gain40 is distinct from its catalog base600; passive world regeneration was not subscribed. Persistence/restart and live/client/H remain unproved.

**D113 food acquisition policy is implemented, opt-in and default OFF; end-to-end earned supply is not accepted yet.** `ProduceHardBread` on the existing shared farm options selects barley for canonical hardbread6751 and admits work only when usable unprotected hardbread is below the bounded reserve5. Default potato behavior/counting stays unchanged. The same actor, activity arbiter, route and scheduler buy seeds, seek ordinary public-farm soil, track their crop, harvest and reuse ordinary knife processing/crafting. Exact pending food work holds its owner against lower-priority wildlife; real safety/recovery/death/generation boundaries remain. An unavailable detour releases execution and retries only on relevant changed resource/world/permission/protection/readiness facts. CharacterCraft's ordinary native refusal now ends its exact submitted queue truthfully; foreign replacement and committed effects survive.

[E298](../../.omp/state/EVIDENCE.md#e298--first-full-food-attempt-failed-before-start-native-http-caller-correction-qualified) retains all06/07/08 histories and actual FULL09 failure: MTP1fail/exit2, primary542 fresh-HarvestCompleted same-poll assumption and separate bad cleanup DELETE404648. Native two naturally matured crops yieldedraw7/LP98; ordinary finally bridge deactivate ran, but policy subsequently paid thirdseed/current775 and created actual persistedcropDb3. Clean owned Game/Login/DB0/0/0 by00:30:51 leaves SQLwallet850/raw7/crop3: genuine75c conservation/durability mismatch, not an accepted baseline or fix. ExactnativeSTART23:21:51.1238909Z. Preserve all earnedprogress/source84/c77/f11/raw, no reset/grant/debit/strip/cropdelete/budgetrelabel/restart. Offline exactoracle/cleanup/lifecycle diagnosis and Main-reviewed boundedrepair/preservation decision precede any continuation; laterfood/heal/refill/325s/H/parentcompletion unproved.

The user personally watched the full functional potato public-farm cycle; preserve that human observation without a confirmation rerun. It is separate from E106's supplied-seed/Growth10 automated receipt and E294's supplied mature barley fixture. Animation roughness is separate. Current food acceptance requires ordinary paid barley on the same farm path, same-identity naturally scheduled growth at the explicitly declared and validated development rate, at least10 earned raw, paid knife, processing12, hardbread, native source-aware bread healing and a **distinct real reserve-refill Craft** after actual consumption drops usable bread below5. Legitimate processed leftovers are conserved; no two-upstream-batch mandate. An initially placed NONBOT network observer is test apparatus, not H, and remains fixed/passive after START. Controlled native NPC stimulus is disclosed, not autonomous combat or an HP override.

**[D114 sustained development profile](../../.omp/state/DECISIONS.md#d114--sustained-faster-development-rates-and-bounded-gameplay-waits):** use existing private `World.GrowthRate=10` and retain Loot1/economy/labor/combat/action ordering. Required gameplay waits in development acceptance should be≤10min; inspect the relevant existing per-mechanic setting first, change only a wait actually blocking the current test, and disclose declared/effective rates and ACCELERATED evidence. Barley's matching-climate source minimum is188.34s atGrowth10, not a live proof or manual maturity. `World__GrowthRate` is the native environment override; `E2E_GROWTH_RATE` is the existing private stack-config generator input, not native Game's direct key. Never call destructive EnsureUp for this profile or silently accept its Loot100/AutoSave.2 defaults. Owned stopped private config/native binding is already10/1/5; the food test's Growth1 assertions/minimum require a reviewed declared-rate change before a new run. Playable187 config changes need exact ownership and no restart/reload is implied; .165/shared production defaults and security/network/lease clocks are unchanged. No reset, forced callbacks, rewards or financial reconciliation is authorized. Db3's prior Growth1 birth and FULL09 native775/SQL850 failure remain explicit.


## Ordinary native harvesting and its limits

The bot starts harvesting through the same ordinary native skill path as a player,
keeps its request Running during the real cast, and observes crop/inventory changes
before reporting completion. Native crop interaction checks the existing adjusted
labor cost before effects, including labor lost during casting; insufficient labor
or protected foreign crops leave the crop/output unchanged. This is a crop-path
correction, not a new global skill-cost system. The existing labor formula is reused
through a pure static helper, avoiding a throwaway Skill allocation.

Interruption, death or timeout cancels only that exact bot-owned execution.
Foreign replacement work survives, and a native result already committed wins a
late interruption/backstop. Existing callers retain the same actor/request through
terminal observation. The common actor tick clears a held harvest even when farming
is no longer selected; it does not force replanting or add a second artificial
four-second pause. Unsupported tree target binding fails explicitly.

[E294](../../.omp/state/EVIDENCE.md#e294--ordinary-native-harvest-qualified-no-seed-acquisition-loop)
records actual canonical4000ms native9/9, unique142 focused controls and
full4706total/4705pass/0fail/1live-onlyskip/compiler0/0/MCP44+24 plus
archaeology163/full24 for source79/79d2c913. Independent review was SOURCE ONLY.
Permanent timed-native fixtures use explicit150ms metadata; neither those timings
nor supplied initially mature crops prove natural growth. Supplied-mature barley
actually yielded2 with labor100→99/vocation0→1. Supplied-seed Plant reached startup
then persistence refusal/Interrupted, not a durable growing crop. No human/client,
live, deployment or complete seed→grain→food→repeat acceptance follows.

**FOOD LOOP INCOMPLETE — the human-observed potato farming cycle is accepted separately; current changed-path paid barley acquisition/natural growth on its planted identity, earned grain-to-food healing and real reserve-refill/persistence remain to be exercised end-to-end.** Implemented policy and local qualification do not upgrade that runtime outcome.

## Historical/design planner model

The diagram and strategic model below describe planner design, not current universal
wiring or live autonomy proof. The former 2026-09-15 integration-WIP/near-term-queue
guidance is historical, not today's dispatch.

```
                  ┌─────────────────────────────────────┐
                  │          High-Level Goal            │
                  │   (e.g., SecretWildFarm, Recover)   │
                  └──────────────────┬──────────────────┘
                                     │
                                     ▼
                  ┌─────────────────────────────────────┐
                  │       GoapPlanner (A* Search)       │
                  │   Bit-Hamming Heuristic (PopCount)  │
                  │      Compact 64-Bit WorldState      │
                  └──────────────────┬──────────────────┘
                                     │
                                     ▼
                  ┌─────────────────────────────────────┐
                  │          Plan Action Queue          │
                  │   [Travel] ──> [Buy] ──> [Plant]    │
                  └──────────────────┬──────────────────┘
                                     │
                                     ▼
                  ┌─────────────────────────────────────┐
                  │          Execution Boundary         │
                  │         (IGameplayActor Core)       │
                  └─────────────────────────────────────┘
```

---

## 1. Overview & Architectural Role

The historical intended integration separates three responsibilities; these are design
roles, not a claim that production currently routes all activities through GOAP:
1. **Strategic Planning (GOAP)**: Evaluates high-level goals (`GoapGoal`), world beliefs (`BotWorldState`), and synthesizes dynamic multi-step action chains (`IGoapAction`) without hardcoded state machines.
2. **Tactical Combat (CombatDecisionTree)**: Handles instant, reactive, sub-second combat decisions (melee vs ranged kiting, class combo triggers, Battlerage/Vitalism rotations).
3. **Physical Execution (IGameplayActor & BotRoamStepExecutor)**: Server-authoritative movement, interaction, casting, and inventory operations respecting native ArcheAge 1.2 rules.

> **Upstream Alignment Invariant**: The GOAP planner is strictly deliberative and **emits `ActorRequest` / `DesiredAction` objects only**. It never directly modifies database tables, never teleports entities, and never bypasses the server's single execution boundary.

---

## 2. Core Components (`AAEmu.Game/Core/Managers/Bots/Goap/`)

| Type | Role | Implementation Notes |
| :--- | :--- | :--- |
| **`BotWorldState`** | Compact 64-bit world state | Value-type (`readonly struct`) with bitmask flags (`InCombat`, `LowHealth`, `NearSeedMerchant`, `HasTreeSaplings`, `AtWildFarm`, etc.) + resource constraints (`Labor`, `Gold`). Single-cycle bitwise operations. |
| **`IGoapAction`** | Atomic action interface | Defines `Preconditions`, `Effects`, and dynamic `CalculateCost`. |
| **`GoapActionBase`** | Action builder base | Fluent builder for authoring actions (`WithPrecondition`, `WithEffect`, `WithLaborCost`, `WithGoldCost`). |
| **`GoapGoal`** | High-level objective | Encapsulates `DesiredState` and dynamic priority evaluation (`CalculatePriority`). |
| **`GoapPlanner`** | A* search engine | Forward A* graph search with bit-Hamming heuristic (`BitOperations.PopCount`), cycle pruning, and an expansion cap (`MaxExpansions = 500`). |
| **`GoapPlanResult`** | Delivery envelope | Reports success status, total cost, node count, and the ordered action sequence. |

---

## 3. Scaling Characteristics & Safeguards

As the action library expands, establish these correctness and performance boundaries
before claiming fleet-scale readiness:

### Measured budgets, not inferred guarantees

- Bitmask checks can be cheap, but action pruning percentages and latency require
  benchmarks against a named action library and world snapshot.
- A bit-Hamming heuristic is not automatically admissible: one action may satisfy
  several bits, and action costs vary. Prove the assumption or document non-optimal
  bounded search. Resource values affecting affordability belong in search identity.
- An expansion cap bounds expansion count, not wall-clock time or allocation. Measure
  planning latency, allocation, replanning frequency, and scheduler impact under load.
- Value-type states do not make the planner allocation-free: collections and plan
  nodes still matter. Caching/background planning need a measured bottleneck and
  thread-safe snapshots; neither is an automatic next step.
- Adding actions expands both search and verification obligations. New actions need
  real target binding, authoritative preconditions, execution/refusal semantics, and
  observed postconditions. Predicted effects never establish actual success.

---

## 4. Revisit Triggers & Historical Context

The architecture originally deferred graph planning until execution was reliable.
Planner foundations have since landed; blanket deferral no longer describes source
inventory. Retain that work; bounded integration follows the current ownership contract
and approved roadmap above, not the historical parity-first queue.

The previously cited 25-bot / 8-hour roaming observation, even if corroborated by its
original artifacts, demonstrates only its measured movement/presence scope. It cannot
establish farming, purchasing, action parity, or autonomous livelihood closure.
Runtime and human acceptance must be obtained for the named loop.

Expansion gate: reliable ordinary action → truthful observation and concrete target
→ resource-correct plan → observed execution/recovery → repeatable live loop.
Keep one lifecycle and scheduler; do not add parallel rules or speculative fleet caches.

---

## 5. Example: 4-Step Wild Farming Plan Discovery

This is a synthetic planner illustration, not a wired activity policy, current dispatch,
or live farming proof. Existing farming code and historical evidence must be assessed
separately through the E132 baseline; the flag plan below does not replace either.

When a bot with an empty initial state (`BotWorldState.Empty`) activates the goal `SecretWildFarm`:
```csharp
var goal = new GoapGoal("SecretWildFarm")
    .WithCondition(BotWorldState.SecretGrovePlanted);

var actions = new IGoapAction[]
{
    new GoapActionBase("TravelToSeedMerchant", 1.5f)
        .WithEffect(BotWorldState.NearSeedMerchant),

    new GoapActionBase("BuyTreeSaplings", 1.0f)
        .WithPrecondition(BotWorldState.NearSeedMerchant)
        .WithEffect(BotWorldState.HasTreeSaplings),

    new GoapActionBase("HikeToSecretPlateau", 3.0f)
        .WithEffect(BotWorldState.AtWildFarm),

    new GoapActionBase("PlantSecretGrove", 2.0f)
        .WithPrecondition(BotWorldState.HasTreeSaplings)
        .WithPrecondition(BotWorldState.AtWildFarm)
        .WithEffect(BotWorldState.SecretGrovePlanted)
};

var plan = planner.Plan(bot, BotWorldState.Empty, goal, actions);
```

**Discovered Sequence**:
1. `TravelToSeedMerchant` (Cost: 1.5)
2. `BuyTreeSaplings` (Cost: 1.0)
3. `HikeToSecretPlateau` (Cost: 3.0)
4. `PlantSecretGrove` (Cost: 2.0)  
*Illustrative declared cost: 7.5. This synthetic flag-only example is not an
execution trace or a current latency benchmark; production acceptance must include
resources, actual targets, and observed outcomes.*
