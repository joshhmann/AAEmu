# Playerbot Technical Design — Consolidated Single Artifact

> Consolidated 2026-08-10 from ROADMAP.md (M5-M10, module architecture, standing
> rules, Gap-audit G0-G4), LIVING-WORLD.md, and card evidence (M2b pilot
> t_db550fca, gap audit t_0fda3cd3, parity audit t_98415169).
> This document is the canonical design reference for the playerbot track.
> Where it extends beyond already-recorded decisions, the extension is marked
> **[PROPOSED]** and awaits Josh review. Milestone traceability: Chapter 8.
>
> Status: draft — Rei accuracy gate PASS (t_ddb3aff3, 2026-08-10); awaiting Josh review.

---

## 1. Design thesis: the DB is the world, embodiment is rendering

**The core shift (Codex review, endorsed):** do not finish AAEmu before
building playerbots. Build ONE dependable classic-ArcheAge life loop, make
bots master that slice, and expand outward together. Bots continuously expose
real server defects while the world comes alive.

**The philosophy (project-wide, permanent):** *PlayerBots are not the
feature. The living world is the feature.* PlayerBots are the mechanism that
gives life to neighborhoods, farms, caravans, pirates, prisons, juries,
markets, villages, festivals — and all the weird stories that made 2014
ArcheAge memorable. If every decision passes that test, the architecture
stays right. (ROADMAP standing rules; LIVING-WORLD.md core philosophy.)

**The DB is the world; embodiment is rendering.** A citizen's authoritative
existence is a database row: character record, inventory, quest state,
schedule, personality metadata. Embodiment — a live `Character` in a region
with a tick — is an expensive *rendering* of that row, reserved for bots that
matter right now. This is the fidelity-tier model (M6.5): do NOT simulate
1000 full players; only nearby/relevant bots run expensive simulation.
(LIVING-WORLD.md Population Philosophy; ROADMAP M6.5.)

### 1.1 Fidelity tiers (M6.5, locked)

| Tier | Name | Simulation |
|------|------|-----------|
| 1 | Full PlayerBot | Combat, navigation, parties |
| 2 | Reduced simulation | Coarse movement, trade, farming |
| 3 | Scheduled simulation | Harvest timers, crafting, travel progress (DB-driven, tick-light) |
| 4 | Dormant | Loaded only when needed |

The Population Director (M7+) assigns fidelity by proximity, relevance, and
activity; a player walking into town "upgrades" nearby citizens from Tier 3/4
to Tier 1/2 without the world paying for 1000 full simulations at once.
Dormant = DB row + metadata only, no `Character` materialized, no region
presence, no per-second tick (A5, Chapter 3).

### 1.2 Three tiers of intelligence (locked — never mix them)

1. **Game AI (deterministic):** combat, farming, crafting, trade packs,
   navigation, prison, juries, schedules, economy, recovery, parties — the
   simulation itself. Local, predictable, always on.
2. **Social AI (mostly deterministic):** canned + contextual chatter,
   greetings, gossip, taunts, trade warnings, event reactions — no LLM.
3. **Narrative AI (optional):** long conversations, remembering a player,
   rumors, backstories, special events — lives entirely OUTSIDE the server
   thread; the API is only called when there's something worth saying.

Chatter tiering (locked): Layer 1 template chatter → Layer 2 procedural
chatter (templates filled with names/places/items/events) → Layer 3 LLM
chatter (rare personalized/narrative moments). Personality archetype files:
`chatter/{lawful,greedy,cheerful,paranoid,pirate,farmer,merchant,guard}/`.
**Living Village launches with ZERO LLM dependency.**

### 1.3 The world runs with zero external AI (permanent design principle)

Ollama dies → world keeps running. OpenRouter quota exhausted → bots still
farm. Internet down → caravans continue. LLM bridge crashes → juries still
work. **The LLM is flavor, not infrastructure.** LLM usage boundary (locked):
dialogue, personality, memories, rumors, contextual reactions, high-level
flavor choices ONLY. Movement, combat, farming, trade runs, crime, juries,
schedules, recovery = deterministic. Never "bot deciding every second → LLM
call." Rate-limit heavily; no ambient chatter in combat; per-bot cooldowns;
per-zone message budgets; shared summaries not full histories; cheap model
for routine lines, stronger model only for memorable events.

### 1.4 Validating references

- **Halo 1 AI design** (Bungie, GDC talk — https://youtu.be/kda7rz5qFtI)
  validates the deterministic-core + rich-reaction-surface thesis: perceived
  intelligence comes from *communicated state* — barks, reactions, emote
  signals — driven by FSM + stimulus perception and director-scripted
  encounters. Harvestable rule for the social/behavior chapters: **every
  goal/state transition emits a VISIBLE signal (line, emote, action);
  reaction coverage beats cognition.** Applied here: the Social AI layer
  (Chapter 4/7) is exactly that reaction surface — bots that *tell* the
  player what they're doing read as alive.
- **AzerothCore playerbots + module system** is the existence proof for the
  module pattern on emulators: bots as ordinary accounts/characters with a
  synthetic controller only, plus additive capability modules
  (mod-ah-bot-plus, mod-dungeon-clear, mod-llm-chatter). B3 goal arbitration
  and the module architecture (Chapter 4) model directly on it.

---

## 2. Actor contract + execution boundary (M5, A1)

**NOT autonomous bots — the contract first.** M5 normalizes, does not invent:
wrap existing capabilities behind ONE additive, inspectable contract.
Existing primitives to wrap: NPC AI movement (NpcAi), target selection, skill
execution, interaction, inventory/game services, normal player and unit
state. Administrator commands are for diagnostics and test setup ONLY — never
a production gameplay-action implementation.

**Explicit NON-GOALS (M5):** no autonomous planning · no LLM integration ·
no generalized navigation rewrite · no core gameplay interface replacement ·
no bot-only inventory or combat behavior.

### 2.1 Contract shape

- one unified observation snapshot
- one validated action request format
- lifecycle tracking: Requested → Accepted → Running → Completed |
  Rejected(reason) | Interrupted(reason) | TimedOut
- failure reasons · cancellation + timeout · diagnostics + trace IDs
- policy forbidding database shortcuts
- adapter implementations over existing systems

**Action surface tiers** (contract defines the FULL vocabulary;
implementations land in slices):

- **M5 required actions:** Observe · Move · Stop · Target · Cast · Interact ·
  Loot · UseItem · Mount/Dismount · AcceptQuest · TurnInQuest
- **M5.1 economic extension:** Plant · Harvest · Craft · PackPickup/PutDown ·
  BoardVehicle · Buy/Sell · Deposit/Withdraw

### 2.2 Execution boundary (threading rule — the non-negotiable)

A single execution boundary for world/character mutation: controllers may
enqueue requests but may NOT mutate a `Character` concurrently. **A1 (M6-exit-
blocking) marshals bot steps onto the game-loop thread; the scheduler stays a
pure wake producer.** Verification is a debug thread-affinity assertion
proving zero Character/world mutation off the tick thread — trace-based exit
tests alone do NOT satisfy this: the current bot layer (8 unsynchronized
worker threads, PlayerBotScheduler.cs:84) would pass trace tests while
violating the rule (L332-333). Acceptance (A1): thread-affinity assert proves
zero Transform writes off the tick thread; 25-bot 6h soak; tick invoke
p95 < 50ms; zero position-tear in wire capture.

### 2.3 Idempotency + audit trail

Idempotency/correlation rules so retries and timeouts cannot duplicate items,
currency, labor consumption, quest credit, or interactions. Every action
emits a structured trace record:

```
{trace_id, actor_id, action, target_id, requested_at, started_at,
 completed_at, result, state_changes}
```

supporting both debugging and the M8 economic audit. M5.1 exit: a scripted
actor completes the curated farm/craft/pack/vehicle/trade segment through the
economic actions; retry tests prove non-idempotent actions do not execute
twice.

### 2.4 Test boundary rule (actor vs playerbot)

- **Actor contract tests** prove the server can execute and observe a
  command correctly.
- **Playerbot behavior tests** prove a controller chooses the right command
  sequence.
Never blur them — every bot failure must be debuggable to one of: wrong
choice / navigation / action rejected / state transition / persistence.

---

## 3. Scale ladder A1-A6 + gate G1 (G2)

Verified order of failure (gap audit 2026-08-09): **threading → broadcast GC
→ density lock/scheduler ceiling → autosave wall → dormancy/fan-out/memory.**

| Item | Size | Work | Acceptance criteria |
|------|------|------|---------------------|
| **A1** | L | Execution boundary: bot steps marshalled onto the game-loop thread; scheduler stays a pure wake producer | Thread-affinity assert proves zero Transform writes off the tick thread; 25-bot 6h soak; tick invoke p95 < 50ms; zero position-tear in wire capture |
| **A2** | S | Broadcast economics: humans-nearby short-circuit + allocation-free GetAround overload (WorldManager.cs:1181) + kill Region.GetList array copies on the hot path | 100 bots 0 humans ⇒ zero bot-originated packets; gen0 GC < 1/min |
| **A3** | M | PopulationDirector O(1): incremental per-zone/per-activity counters; RefreshPressure on a 5s timer (never called today); human-proximity wake trigger | 1,000-bot wake storm transition p99 < 100ms |
| **A4** | M | Save scalability: per-character dirty tracking + batching | Autosave p95 < 2s at 250 characters; zero `_isSaving` skips |
| **A5** | L | **TRUE DORMANCY — the pivotal item:** Dormant = DB row + metadata only, no Character materialized, no region presence, no per-second tick; Tier 3 = DB-driven scheduled simulation (harvest/travel timers advance while nobody is embodied) | 1,000 registered / ≤50 embodied; RSS within 15% of the 50-only baseline; wake-to-visible p95 < 3s; dormant timers advance over 6h |
| **A6** | M | Manifest-driven mass provisioning: citizen manifest as data; replaces hardcoded CitizenNN + 10-bot clamp | Cold boot → 100 citizens on schedule < 60s |

**Gate G1:** 50-bot 6h soak with numeric budgets → 100 profiling → 250
staged. Numeric budgets (approved before any soak): p95/p99 world-tick time,
memory, database writes, action-queue backlog, recovery rate. Gate in
stages: one bot 30 min → 10 bots 1h → 10 bots 6h. A qualitative "no overrun"
is not sufficient evidence. Staged density ladder (resolved decision):
10 correctness → 25 village → 50 soak → 100 only after profiling. Soak-failure
semantics: any crash = automatic fail + RCA card; the 6h clock restarts only
after the fix lands. **M3b/M8 coupling:** the autosave budget at gate scale
is p95 < 2s with the two homesteads + 25 bots embodied (save path must not
kill M8 later).

---

## 4. Behavior architecture: goal arbitration + module contract (G3/B3)

Playerbots is the SUBSTRATE; modules are specialized layers around it
(AzerothCore-inspired, additive capability layers — copy the ARCHITECTURAL
ROLES, not the code). Two module categories:

- **Embodied modules** (control real bot characters — MUST use the M5
  Gameplay Actor Contract): adventuring, farming, hauling, dungeon clearing,
  party behavior, homesteading.
- **Ambient services** (affect the world but are NOT embodied players — must
  not masquerade as bot behavior): market liquidity (MarketMaker), population
  direction, LLM bridge, guide, test coordinator.

### 4.1 Module set

Each module declares: required observations, required actions, emitted
events, persisted metadata, commands/config, performance budget, failure
modes.

| Module | Role | Inspiration |
|--------|------|-------------|
| AAEmu.Bot.Core | runtime controller, headless sessions | Playerbots core |
| AAEmu.Bot.Population | Population Director — level/faction/zone/profession distribution, human-presence weighting, schedules | mod-player-bot-level-brackets |
| AAEmu.Bot.Adventure | quest route, combat, loot, death recovery | mod-dungeon-clear pattern |
| AAEmu.Bot.Party | group/assist/roles/rally | — |
| AAEmu.Bot.Homestead | farm cycle, harvest, replant | — |
| AAEmu.Bot.Trade | pack craft, vehicle load, route, sell | — |
| AAEmu.Bot.Dungeon | group dungeon clear with scenario harness | mod-dungeon-clear |
| AAEmu.Economy.MarketMaker | TWO MODES: bootstrap (capped synthetic supply, labeled internal) → living economy (bots list real produced goods; service only fills gaps) | mod-ah-bot-plus |
| AAEmu.Social.Chatter | async LLM bridge — game writes events to queue, NEVER blocks on Ollama; personality/memory/cooldowns | mod-llm-chatter |
| AAEmu.Guide | grounded Q&A — live server data only, NEVER model memory for mechanics | mod-llm-guide |
| AAEmu.Bot.TestHarness | deterministic seeded scenario runs (seed, replay, JSONL results) | mod-dungeon-clear harness |

`AAEmu.Bot.*` names are capability boundaries first, not a mandate to create
many assemblies immediately. Begin inside the existing Game project where
access to normal gameplay services is required; split projects only when a
stable API, independent test boundary, or deployment boundary justifies the
dependency cost.

### 4.2 Goal arbitration + IBotActivityModule (B3)

B3 (M): goal arbitration + module contract (`IBotActivityModule`):

- re-implement **roam as a module** with zero scheduler changes;
- **delete/absorb the dead PlayerBotBehaviorController stack**;
- new module = **one file + one config line**.

Acceptance: a new behavior (e.g. a fishing trip) ships as exactly one file
and one config line, registered through arbitration — no scheduler edits.
This is the mechanism that lets M9.5 activities and future modules
(fishing fleet, festival coordinator) slot in WITHOUT touching combat AI or
the bot framework.

### 4.3 Test harness pattern (copy wholesale)

`.bot test start golden-path seed=1234` → run → `{run_id, seed, activity,
result, failure, stage, elapsed_seconds}` → replay failed runs. Fits the
actor-vs-playerbot failure taxonomy exactly.

**Development loop (Hermes as prototype lab):** prototype behaviors in
Hermes → observe failures/edge cases → distill into deterministic game logic
→ deploy to thousands of bots. Hermes is the research environment, not the
per-bot runtime.

---

## 5. Persistence: playerbot_metadata store + bot-world restart test (B4)

**M6.0 embodiment (locked):** bots use ORDINARY AAEmu login accounts and
ordinary character records. Gameplay state lives in normal character,
inventory, quest, mount, mail, housing and economy systems. At runtime,
BotManager activates the character through a **trusted internal headless
game session** — no client login handshake emulation, no real game client.

```
ManagedBotAccount (account_type=HeadlessBot)
    └── ordinary Character (real account ownership, real character row)
            ↓
      HeadlessGameSession (internal, trusted)
            ↓
      normal Character loading pipeline
            ↓
      PlayerBotController (additive, temporary)
```

Account model: one managed bot account per bot character initially
(`bot_managed_000001`…); strong random credentials; accounts flagged
HeadlessBot and BLOCKED from public client login. (PlayerAltBot — humans
activating their own alts as companions — is a later category.) Core policy:
reuse standard character loading + gameplay services; permit ONLY narrowly
scoped lifecycle hooks (internal character loading, headless session
create/cleanup, world registration, distinguishing connected humans from
headless actors); no broad core rewrites, no parallel player persistence
model, no direct gameplay-state DB writes.

**Bot-specific persistence is limited to metadata with no normal character
equivalent:** personality profile, schedule, profession, home assignment,
behavior config, last planner state (the M6.0 list that has **no table
today**).

**B4 (S-M):** `playerbot_metadata` store (personality, schedule, profession,
home, planner state) + **audit-trace flush**; **2-checkpoint bot-world
restart test**. M8 exit couples to it: "auditable economy" = the M5 audit
trail flushed per B4, with an economy-ledger reconciliation assertion in the
2-checkpoint restart test; "multiple restarts" = ≥3, with state-fidelity
criteria (schedule phase, home, profession, inventory, ledger) asserted after
each. The M2b pilot already proves the pattern at quest level: 2/2
checkpoints byte-identical WriteData round-trip, no duplication, resume
completes through the real turn-in path. Additive-layer rule (refined):
composition, adapters, existing extension points first; narrow, reviewed core
hooks only when required to reuse the normal Character/session lifecycle;
NEVER a parallel character/inventory/quest/property/economy implementation.

---

## 6. Population distribution & activity model

> **Chapter status:** the fixed points below (6.1-6.3) are already recorded
> decisions. The distribution model itself (6.4-6.7) is **NEW design work —
> [PROPOSED] for Josh review.** Nothing in this chapter changes code today.

### 6.1 Fixed points (locked decisions, consolidated)

- **Fidelity assignment is the PopulationDirector's job** (v1 exists:
  `AAEmu.Game/Core/Managers/Bots/PopulationDirector.cs`). It is the ONLY
  fidelity authority: Dormant/Reduced/Full per bot, single-step ladder
  transitions, transition safety gate (never downgrade while in combat /
  attached to a Slave / carrying a trade pack / in trial / grouped with a
  human / saving), adaptive pressure control (HEALTHY/PRESSURE/HIGH/CRITICAL
  bands drive wake/sleep), density caps per-zone and per-activity.
- **The director does NOT own embodiment** — it consumes the
  `IPlayerBotManager` registry and `IPlayerBotScheduler` for wake decisions.
- **Population-distribution targets (activity mix by game-time/zone) land
  with the PopulationDirector work in G2/G3** (Lane D pairing rule, locked
  2026-08-10).
- **A3 (M):** PopulationDirector O(1) — incremental per-zone/per-activity
  counters; `RefreshPressure` on a 5s timer (**never called today**);
  human-proximity wake trigger. Acceptance: 1,000-bot wake storm transition
  p99 < 100ms.

### 6.2 The gap: activityResolver is null today

`PopulationDirector.cs:60`:

```csharp
_activityResolver = activityResolver ?? (_ => null);
```

The director's constructor defaults the activity resolver to a
never-null lambda, so `EmbodiedOnActivity(string)` exists in the interface
(IPopulationDirector.cs:65-66) but returns 0 for every activity today — no
bot carries an activity label, and no per-activity density cap can be
enforced. `_zoneResolver` defaults to `Transform.ZoneId` (works), but
activity is an unpopulated dimension. **A3's per-activity counters cannot be
built until an activity label exists per embodied bot** — this is the
ordering constraint that makes chapter 6.4 the prerequisite for A3's
per-activity half.

### 6.3 Bot-integration pairing rule (Lane D, locked 2026-08-10)

Every Lane D mechanic pairs with its playerbot surface in the same breath:
the mechanic card is not complete until (a) M5-stand-in bots exercised it as
the H/A evidence where applicable AND (b) the corresponding bot-activity
surface is carded. **Mechanic completeness and population behavior are one
track read twice: the fleet fixes the game, and the bots immediately become
its playerbase.**

| Mechanic works (Lane D) | → bot-activity surface |
|-------------------------|------------------------|
| AUCTION / MERCHANT | trader bots (market listings, buy/sell) |
| INDUN (dungeons) | dungeon-runner module (M7 party + AAEmu.Bot.Dungeon) |
| PVP / DUEL / arenas | PvP bots (arena squads, duel challengers) |
| FISH | contest participants (M9.5 fishing contest) |
| TRADE / PACK / SLAVE | hauler/trader bots (M8.3, AAEmu.Bot.Trade) |
| FARM / PROPERTY | farmer + homestead bots (M8.1/8.2, AAEmu.Bot.Homestead) |
| CHAT | social bots (M8.5a chatter, community surface) |

Corollary: an activity with no paired mechanic at evidence grade is **not
staffed** — bots cannot believably live an activity the engine cannot run.
This is the gate that keeps the population model honest.

### 6.4 [PROPOSED] Activity taxonomy and player-mix targets

Define one canonical activity label per embodied bot (the value
`activityResolver` returns). Proposal — aligned with the fidelity tiers and
the Lane D mechanic ledger:

- **questing** (PvE progression; adventurer archetype)
- **dungeons** (INDUN; party + dungeon module)
- **trade** (AH/merchant surface: listing, buying, selling)
- **economy** (farming / crafting / hauling — LABOR, CRAFT, PACK, SLAVE)
- **pvp** (PVP/DUEL/arena surfaces)
- **social/idle** (plaza presence, chatter, group activities — the
  "village feels alive" texture)
- **offline** (dormant or scheduled — Tier 3/4)

**Player-mix targets:** of N citizens, a distribution across these
activities. Proposal for a village-scale N (working example, not a
commitment — the M8 exit village is 2 farmers / 1 crafter / 2 haulers /
3 adventurers = 8 embodied + humans):

| Activity | Prime-time share (proposal) | Dead-hours share (proposal) |
|----------|-----------------------------|------------------------------|
| questing | 25-30% | 5-10% |
| dungeons | 10-15% (gated by INDUN grade) | 0% |
| trade/AH | 15-20% | 5-10% |
| economy | 20-25% | 10-15% (Tier 3 timers keep crops/labor advancing) |
| pvp | 5-10% (gated by PVP grade) | 0% |
| social/idle | 10-15% | 30-40% (the world still looks inhabited) |
| offline/dormant | remainder (density caps) | remainder (A5 keeps them DB-only) |

**Varying dimensions:**
- **Game-time:** prime time (evenings/weekends, human-presence weighted) vs
  dead hours (world keeps running; embodiment shrinks, Tier 3 timers carry
  the economy; social/idle share rises so towns don't look empty).
- **Zone:** starter zones skew questing/adventurer; towns skew trade/social;
  farm belts skew economy; dungeons only staffed when INDUN evidence exists
  (6.3); PvP zones only staffed when PVP evidence exists.
- **Season:** seasonal content (M9.5 events — fishing contest as the locked
  launch activity) shifts share toward the event's activity during its
  window; festival coordinator module (future) is the scheduler for this.

**Converge, don't cap:** the mix is a target distribution the director
converges toward as bots wake/sleep, not a hard quota. Per-activity density
caps (already in the interface) remain the hard bound; the mix guides which
activities get the wake budget.

### 6.5 [PROPOSED] How PopulationDirector consumes the model

- `activityResolver` is injected at construction (or via the director's
  module wiring in G3) and returns the bot's current canonical activity
  label — sourced from the scheduler/activity-module state (B3), NOT guessed.
- `EmbodiedOnActivity(activity)` + A3's incremental per-activity counters
  become the observability surface: the director sees the live mix vs the
  target mix.
- **Wake/sleep policy extension:** when pressure is HEALTHY and a wake
  budget is available, prefer waking a Dormant bot whose activity is
  under-represented relative to the current game-time/zone target. When
  pressure rises, the existing band sweep demotes — and demotion order
  prefers over-represented activities first (gate-respecting: never a bot in
  combat, etc.).
- **Human-presence weighting** (already in the module table's role): the mix
  table shifts toward "what a human nearby expects to see" — a player
  entering town upgrades Tier 3/4 citizens to Tier 1/2 (M6.5) and the
  activity mix in that zone moves toward the prime-time profile.
- `RefreshPressure` on its 5s timer (A3) becomes the heartbeat that recomputes
  mix deviation; the O(1) counters keep this free at 1,000 bots.

### 6.6 [PROPOSED] Tier 3 abstract simulation: believable statistics, not timers

Tier 3 (Scheduled simulation) is DB-driven: harvest timers, crafting, travel
progress advance while nobody is embodied. **Proposal:** Tier 3 must also
produce the *statistical footprint* of a living population — because a player
reading the AH, the census, or faction balance should see evidence of
activity, not just "timers advancing":

- **Economy ledger entries:** Tier 3 harvest/craft/travel completions settle
  into the same B4-ledger/audit-trail records as embodied actions (marked
  tier-3 source), so market depth and trade volume are continuous, not
  stepwise. MarketMaker's bootstrap→living transition depends on this: the
  synthetic-supply cap is only removable if real production (including
  scheduled) sustains the market.
- **Statistical surfaces:** per-zone "who's around" (from the resident
  manifest + fidelity map), AH depth (listings from Tier 3 production),
  faction/profession balance (from the citizen manifest) — all derived, all
  DB-queryable, all consistent with what embodiment would show if a player
  walked in.
- **Believability rule:** a Tier 3 result must be *indistinguishable in
  kind* from a Tier 1 result of the same activity (same services, same
  ledger, same events) — the difference is only when, not what. This keeps
  the "DB is the world" thesis intact: the row is the truth, the tick is
  rendering.

### 6.7 [PROPOSED] Acceptance criteria for the population model

- [ ] Every embodied bot carries a canonical activity label; `EmbodiedOnActivity` returns non-zero counts for at least 5 activities at 25+ bots.
- [ ] At 1,000 registered / ≤50 embodied (A5), mix deviation from the game-time target is computable and wake/sleep decisions prefer under-represented activities (A3 budgets hold: wake-storm p99 < 100ms).
- [ ] Dead-hours demo: 25 embodied bots show the dead-hours mix; Tier 3 timers keep farm/labor/ledger activity advancing; a player logging in sees a populated-but-quiet world that "was alive without them" (the LIVING-WORLD success test).
- [ ] No activity is staffed before its paired Lane D mechanic is at evidence grade (6.3 corollary, checked at gate).
- [ ] Tier 3 economy events appear in the B4 ledger with tier-3 source labels; reconciliation assertion in the 2-checkpoint restart test still passes.

---

## 7. Branch coverage map

Which roadmap branch owns which bot surface:

| Branch | Coverage | Bot surface |
|--------|----------|-------------|
| **PvE** | M5 core actions (B1), M7 adventurer/party bots, M8.1-8.4 village roles, M8.5 social | questing, combat, loot, leveling, parties; the M2b pilot (t_db550fca) is the quest-level proof-of-pattern: 3 bots × 10 cycles 30/30 green, 0 leaks, 2/2 restart checkpoints, seeded-regression proof |
| **Events + minigames** | M9.5 activities (fishing contest = locked launch activity; races, caravans, hunts, construction, festivals = candidates until carded) | an activity is a module (B3): one file + one config line; participants are staffed via the pairing rule (FISH → contest participants); auditable results (entries, scores, winner, rewards settled in the ledger); world outside the event keeps running within G1 budgets |
| **PvP** | Lane D mechanics → bot surfaces (PVP/DUEL/arenas); M10 siege (deferred, two slices) | PvP bots only after arena/duel mechanics at evidence grade; siege slice 2 = scripted bot squads with victory state + reward settlement in audit trail |
| **Economy** | M4 trade/craft/transport, M5.1 economic actions (B2), M8.3 hauler/trader, M9 trade-pack economy, MarketMaker bootstrap→living | farmer/crafter/hauler bots; audit-trail-backed ledger (B4); emergent market behavior (price → pack value → routes → escorts → pirates) is incentive-driven, not scripted |
| **Community** | M8.5a social v1 (greetings, task acks, canned dialogue), M8.5b async LLM bridge, M8.5c grounded guide, M9 rumors/gossip, guilds (Lane B/FC substrate), M9 politics/village identity | social bots with cooldown budgets; rumor propagation store (event → witness → hearsay, imperfect information, persisted per B4); LLM narrates only — never controls movement/combat/economy; server NEVER waits on an LLM |

M9 caveat: until each system has an exit test it is a vision section, not a
milestone. Required substrate before any M9 credit: needs/incentives layer
(the reason a bot farms or steals — does not exist in any form today),
crime/justice substrate (CRIME-01/TRIAL-01 W=1 stubs, PRISON-01 no
PrisonManager), rumor propagation store.

---

## 8. Milestone traceability

Which M5-M10 gate each chapter serves:

| Chapter | Serves | Gate / evidence |
|---------|--------|-----------------|
| 1. Design thesis | M6 (framework), M7-M8 (living village), all | Standing rules; M6.5 fidelity tiers; zero-external-AI principle is a permanent design constraint on every bot card |
| 2. Actor contract + execution boundary | **M5 exit** (B1/B2), **M6 exit** (A1 is M6-exit-blocking), M4 automated fallback, M8 auditable economy | M5 core + M5.1 economy exit tests; thread-affinity assertion; retry/idempotency tests; audit trail feeds the M8 economy audit |
| 3. Scale ladder A1-A6 + G1 | M6 (A1), M8 (A5 true dormancy + G1 budgets — M8 exit runs at 25 embodied within G1 budgets), M3b/M4 (A4, A2 couplings) | G2 ladder; 50-bot soak → 100 profiling → 250 staged; staged density gates (10→25→50→100) |
| 4. Behavior architecture (B3) | M7 (party/adventurer modules), **M8** (C1-C5 village modules), M8.5 (social module), M9.5 (activities as modules), M9 (emergent modules) | B3 exit: new module = one file + one config line; M8 exit = village of 8 villagers + humans, full day, ≥3 restarts, auditable economy |
| 5. Persistence (B4) | **M6 exit** (restart-persistence scenario per standing rule), **M8** (schedules/homes/professions survive restart), M8.5, M9 (traits/rumors persisted) | 2-checkpoint bot-world restart test; ledger reconciliation assertion; ≥3 restart cycles with state-fidelity criteria |
| 6. Population distribution & activity model | **M6.5** (fidelity assignment), **M8** (village composition), **G2/A3** (O(1) director), **Lane D** (mechanic→bot pairing), M9.5 (event staffing), M9 (village identity/specialization) | A3 acceptance (1,000-bot wake storm p99 < 100ms); pairing rule enforced at every mechanic card; [PROPOSED] criteria in 6.7 pending Josh review |
| 7. Branch coverage map | M5-M10 overall | Each branch's own exit tests (M7 group encounter, M8.5 social exit, M9.5 event exit, M10 slice exits) |
| 8. This chapter | — | Traceability audit |

Definition-of-done cross-cutting (every milestone): three scenarios (human,
automated, restart-persistence) · both scorecards updated · merged to
develop (G0-1) · Rei signoff · STATUS.md reflects the milestone (Nei) ·
deployed by Mai only at milestone/release-candidate boundaries · no branch
push or PR to upstream (upstream is intake-only).

---

## Appendix: sources and evidence

- ROADMAP.md — M5-M10, module architecture, standing rules, Gap audit
  G0-G4 (2026-08-09), Resolved planning decisions, Lane D (locked
  2026-08-10).
- LIVING-WORLD.md — philosophy + architecture reference (2026-08-03).
- scorecard-explorations/m2b-playerbot-pilot.md + m2b-pilot-metrics.md —
  M2b pilot evidence (t_db550fca, 2026-08-06).
- Gap audit card t_0fda3cd3; parity audit t_98415169 (M6.6, 2026-08-08).
- Code: `AAEmu.Game/Core/Managers/Bots/PopulationDirector.cs`,
  `IPopulationDirector.cs`, `PopulationDirectorOptions.cs`,
  `PopulationDirectorMetrics.cs`; `PlayerBotScheduler.cs` (threading
  violation noted at :84/:332-333); `WorldManager.cs:1181` (GetAround).
- Halo 1 AI design talk (https://youtu.be/kda7rz5qFtI); AzerothCore
  playerbots + module ecosystem (mod-ah-bot-plus, mod-llm-chatter,
  mod-player-bot-level-brackets, mod-dungeon-clear).
