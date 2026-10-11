# PlayerBot GOAP Planning Architecture & Scaling Guide

Goal-Oriented Action Planning (GOAP) for ArcheAge 1.2 PlayerBots in AAEmu.

- Audience: Contributors
- Last verified against: documentation-reviewed 2026-10-11 at `develop` / `aa057ffc3bb4596f4a6972eb4f012994970e163a` plus inherited projection/E131 docUI dirty work; no fresh gameplay verification

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
