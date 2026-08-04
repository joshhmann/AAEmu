# ArcheAge Slums — Roadmap & Milestones (v4, locked-shape 2026-08-03)

> **🚫 THE RULE (Josh, permanent): NEVER push a PR to upstream AAEmu/AAEmu
> unless Josh explicitly approves it.** Everything stays in our own lane.
>
> **THE CORE SHIFT (Codex review, endorsed):** Do not finish AAEmu before
> building playerbots. Build ONE dependable classic-ArcheAge life loop, make
> bots master that slice, and expand outward together. Bots continuously
> expose real server defects while the world comes alive.
>
> **THE PHILOSOPHY (project-wide, permanent):** *PlayerBots are not the
> feature. The living world is the feature.* PlayerBots are just the
> mechanism that gives life to neighborhoods, farms, caravans, pirates,
> prisons, juries, markets, villages, festivals — and all the weird stories
> that made 2014 ArcheAge memorable. If every decision on this project
> passes that test, the architecture stays right.

## Three phases

1. **Playable Classic Loop** (M1-M4) — a dependable slice humans can enjoy
2. **Bot-Compatible Game Platform** (M5-M6) — the gameplay actor contract
   + deterministic bot framework
3. **Living Village** (M7-M8+) — bots populate, extend, and test the loop

## Standing rules (every milestone)

- **Every feature milestone defines THREE scenarios:** a human scenario, an
  automated scenario, and a restart-persistence scenario. These are the
  definition of done.
- Technical wiring scorecard AND experience scorecard both update per
  milestone (they measure different things).
- Bots must invoke normal AAEmu gameplay services — no direct DB
  manipulation, no bot-only resource creation.
- Additive layer rule: no core-interface rewrites; keeps upstream pulls clean.
- **Test boundary rule (actor vs playerbot):** *Actor contract tests* prove
  the server can execute and observe a command correctly. *Playerbot
  behavior tests* prove a controller chooses the right command sequence.
  Never blur them — every bot failure must be debuggable to one of:
  wrong choice / navigation / action rejected / state transition / persistence.
- **The golden path is the product.** All work is judged against:
  level with friends, get mounts, claim land, grow things, craft packs,
  ride carts, sail and trade.
- **THREE TIERS OF INTELLIGENCE (never mix them):**
  - **Game AI (deterministic):** combat, farming, crafting, trade packs,
    navigation, prison, juries, schedules, economy, recovery, parties —
    the simulation itself. Local, predictable, always on.
  - **Social AI (mostly deterministic):** canned + contextual chatter,
    greetings, gossip, taunts, trade warnings, event reactions — no LLM.
  - **Narrative AI (optional):** long conversations, remembering a player,
    rumors, backstories, special events — lives entirely OUTSIDE the server
    thread; the API is only called when there's something worth saying.
- **THE WORLD RUNS WITH ZERO EXTERNAL AI (permanent design principle):**
  Ollama dies → world keeps running. OpenRouter quota exhausted → bots
  still farm. Internet down → caravans continue. LLM bridge crashes →
  juries still work. **The LLM is flavor, not infrastructure.**
- **LLM usage boundary (locked):** dialogue, personality, memories, rumors,
  contextual reactions, high-level flavor choices ONLY. Movement, combat,
  farming, trade runs, crime, juries, schedules, recovery = deterministic.
  Never "bot deciding every second → LLM call." Rate-limit heavily: no
  ambient chatter in combat, per-bot cooldowns, per-zone message budgets,
  shared summaries not full histories, cheap model for routine lines,
  stronger model only for memorable events.
- **Chatter tiering:** Layer 1 template chatter (everyday reactions) → Layer
  2 procedural chatter (templates filled with names/places/items/events:
  "Someone paid {price} for {item_name}? Robbery.") → Layer 3 LLM chatter
  (rare personalized/narrative moments). Personality archetype files:
  `chatter/{lawful,greedy,cheerful,paranoid,pirate,farmer,merchant,guard}/`.
  **Living Village launches with ZERO LLM dependency** — canned + procedural
  chatter must feel good first; the API is an enhancement, not a requirement.

---

## M0 — Foundation ✅ COMPLETE

Workflow v3 (lane gate), community guidelines, kanban templates, gate.sh
verified, scorecard + 3 exploration reports, graphify graph (17.6k nodes),
shared division skill enabled on all 4 profiles, ROADMAP v1.
BUG-006 (kill-acceptor, 380 quests, 1082/1082 tests) parked awaiting Josh's
merge/deploy decision.

---

## M1 — Quest and progression spine (Track 1)

Trimmed, not exhaustive. Fix shared engine defects + the selected golden
route. Individual peripheral quest bugs → Lane B (maintenance).

**Work:**
- Merge/reconcile the parked kill-acceptor fix (BUG-006)
- Load + validate quest_act_obj_aliases (2,746 dangling rows)
- Audit stub acts (silent auto-complete/stall)
- Quest sanity verifier (startup cross-check)
- Fix common doodad phase/interaction objectives (quests 922/3889/3447)
- Select ONE faction + starting progression route; document intentionally
  excluded quests

**Priority order:** shared engine defects → golden-route blockers → silent
corruption → peripheral quests.

**Exit tests (pre-M5 automation distinction — NO accidental M5 dependency):**
- **Human:** new character enters world, completes curated opening chain,
  gains levels, receives rewards, logs out and continues, reaches
  first-mount prerequisite.
- **Automated (pre-M5):** engine-level scenario tests verify shared quest
  transitions, aliases, rewards, persistence, and known blockers — using
  existing test facilities, NOT the gameplay actor contract (that arrives
  in M5). After M5, the golden route is replayed THROUGH the actor contract
  as a contract-level regression scenario.
- **Restart-persistence:** the character resumes the route after server
  restart.

---

## M2 — Golden-path release gate

The repeatable playable journey — the first real definition of "playable."
**Classified lightweight + parallelizable**: M3 investigation may begin while
M2 documentation and test tooling are finalized.

**Golden path:** create character → starter progression → unlock mount →
acquire farm → plant & harvest → build house → craft trade pack → transport
pack → sell → return home.

**Primary outputs:** curated route · human playtest checklist · scenario
manifest · restart checkpoints · known-blocker registry · structured logging
expectations · clean database snapshot. (Selected race/faction, zones,
quest chain, skill builds, mount, housing zone, crop chain, crafting chain,
trade-pack recipe, land route, cart/hauler, short sea route if viable.)

**Exit tests (two tiers — don't let scheduling block readiness):**
- **Required correctness run:** two players complete the full loop twice,
  including a server restart, without GM repair.
- **Release validation:** four players complete one integrated session
  before the milestone is marked production-validated.
**This is the first real playable release.**

---

## M3 — Homestead integrity (two gates, M3a then M3b)

Prevents "housing is playable" from being blocked by every persistence edge
case, while refusing to declare the milestone complete prematurely.

### M3a — Homestead shell (visible, interactive loop)

Housing placement-zone validation · decoration-limit enforcement ·
housing-group/UI data · ownership + permissions · construction · crop
placement · growth + harvest · selected storage and furniture interactions.

**Exit condition:** two players establish adjacent homesteads and use the
curated objects during ONE uninterrupted session.

### M3b — Property persistence and recovery (engineering-heavy)

Furniture + bound doodad persistence · door/window phase state · crop +
livestock recovery · rotation/attachment integrity · storage persistence ·
server restart restoration · disconnect + logout cleanup · orphan/duplicate
prevention · administrative repair tooling.

**Exit condition:** the same two homesteads survive repeated logout,
restart, crash-recovery, and re-entry tests WITHOUT state loss or
duplication.

**Scorecard targets:** housing ~75-85% usable for curated loop, farming
~70-80%, property persistence green for tested items.

---

## M4 — Trade, crafting and transport integrity

The connective tissue of classic ArcheAge — ahead of music/contests/siege.

**Crafting:** recipe prerequisites, material + labor consumption, output
correctness, workstation range/ownership, inventory-full handling.
**Trade packs:** creation, backpack occupancy, placement/pickup, ownership,
storage on property, maturation, sale + reward correctness.
**Vehicles/ships:** summon/despawn, passenger + cargo attachment, death/
disconnect cleanup, portal/instance behavior, restart recovery, stuck
recovery.

**Exit test:** group harvests real materials → crafts pack → loads vehicle →
travels defined route → unloads + sells → correct reward → repeats after
restart. **The server becomes recognizably classic ArcheAge here.**

---

## M5 — Gameplay Actor Contract

**NOT autonomous bots — the contract first.** This is normalization, not
invention: wrap existing capabilities behind ONE additive, inspectable
contract. Estimated 2-4 focused sessions (existing primitives are reusable).

**Existing primitives to wrap:**
- NPC AI movement (NpcAi)
- target selection
- skill execution
- interaction
- inventory/game services
- administrator commands (where useful for development)
- normal player and unit state

**New work:**
- one unified observation snapshot
- one validated action request format
- lifecycle tracking (Requested → Accepted → Running → Completed |
  Rejected(reason) | Interrupted(reason) | TimedOut)
- failure reasons
- cancellation + timeout
- diagnostics + trace IDs
- policy forbidding database shortcuts
- adapter implementations over existing systems

**Explicit NON-GOALS:** no autonomous planning · no LLM integration · no
generalized navigation rewrite · no core gameplay interface replacement ·
no bot-only inventory or combat behavior.

**Action surface tiers (contract defines the FULL vocabulary; implementations land in slices):**
- **M5 required actions:** Observe · Move · Stop · Target · Cast · Interact ·
  Loot · UseItem · Mount/Dismount
- **M5.1 economic extension:** Plant · Harvest · Craft · PackPickup/PutDown ·
  BoardVehicle · Buy/Sell · Deposit/Withdraw

Slicing keeps M5 from expanding when crafting or vehicle APIs expose
special cases.

**Architectural rule:** invokes normal gameplay services only — no direct
DB manipulation, no bot-only resource creation.

**Bot audit trail:** every action emits a structured trace record —
`{trace_id, actor_id, action, target_id, requested_at, started_at,
completed_at, result, state_changes}` — supporting both debugging and the
M8 economic audit.

**Exit test:** a scripted actor completes the curated golden-path primitives
and produces a machine-readable trace showing every request, transition,
result, and failure. Actor contract tests (server executes/observes a
command correctly) pass independent of any controller.

---

## M6 — Deterministic playerbot framework

- **6.0 PlayerBot embodiment decision — LOCKED (AzerothCore Playerbots pattern,
  2026-08-03):** Persistent bots use ORDINARY AAEmu login accounts and
  ordinary character records. Gameplay state lives in normal character,
  inventory, quest, mount, mail, housing and economy systems. At runtime,
  BotManager activates the character through a **trusted internal headless
  game session** — bots do NOT emulate the external client login handshake
  and no real game client process is involved.

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

  **Account model:** one managed bot account per bot character initially
  (`bot_managed_000001`…); strong random credentials; accounts flagged
  HeadlessBot and BLOCKED from public client login. (PlayerAltBot — humans
  activating their own alts as companions — is a later category, once
  permissions/abuse are understood.)

  **Core policy:** reuse standard character loading + gameplay services.
  Permit ONLY narrowly scoped lifecycle hooks (internal character loading,
  headless session create/cleanup, world registration, distinguishing
  connected humans from headless actors) — no broad core rewrites, no
  parallel player persistence model, no direct gameplay-state DB writes.

  **Bot-specific persistence is limited to metadata with no normal
  character equivalent:** personality profile, schedule, profession, home
  assignment, behavior config, last planner state.
- **6.1 Core:** BotManager, PlayerBot entity, tick registration, spawn/
  despawn, persistent identity/inventory/position, controlled logout,
  per-bot diagnostics, tick budget accounting
- **6.2 Safety FIRST (before "roam"):** stuck detection, navigation timeout,
  invalid-target recovery, death/resurrection, unreachable-object handling,
  inventory-full handling, mount-state repair, retry budgets, safe return
- **6.3 Behaviors:** idle, roam (permitted zone), follow, defend self,
  assist party target, loot, return home
- **6.4 Config:** spawn count, zone density, tick rate, allowed activities,
  home position, class/equipment templates, debug overlay, admin pause
- **6.5 Fidelity tiers (population scalability — do NOT simulate 1000 full
  players; only nearby/relevant bots run expensive):**
  - **Tier 1 — Full PlayerBot:** combat, navigation, parties
  - **Tier 2 — Reduced simulation:** coarse movement, trade, farming
  - **Tier 3 — Scheduled simulation:** harvest timers, crafting, travel
    progress (DB-driven, tick-light)
  - **Tier 4 — Dormant:** loaded only when needed
  - The Population Director (M7+) assigns fidelity by proximity, relevance,
    and activity; a player walking into town "upgrades" nearby citizens
    from Tier 3/4 to Tier 1/2 without the world paying for 1000 full
    simulations at once.

**Exit test:** 10 bots run 6 hours with no unrecovered loops, no inventory
duplication, no runaway combat, no DB corruption, no tick-budget overrun.
Playerbot behavior tests (controller chooses the right command sequence)
run against the M5 actor contract — a failed bot harvest must resolve to
one of: wrong choice / navigation / action rejected / state transition /
persistence.

---

## M7 — Adventurer and party bots (Playerbots Alpha)

Split by archetype, not one universal mind.

- **Adventurer v1:** curated quest route, hostile targeting, fixed skill
  priority, distance maintenance, heal/retreat, loot, equip upgrades,
  return to quest NPC, death recovery
- **Party v1:** invite/join, follow leader, rally, assist target, avoid
  extra pulls, tank/damage/healer roles, wait for missing members,
  resurrect, mount + travel together

**Exit test:** one human + three bots complete the curated leveling route
and a selected group encounter.

---

## M8 — Living Village (first true vision release: "AAEmu: Living Village")

Sequenced carefully — tasks BEFORE talk.

- **8.1 Farmer bot:** check farm, identify mature crops, harvest, deposit,
  replant approved crops, report shortages
- **8.2 Crafter bot:** read production request, check/withdraw materials,
  use workstation, store output, report shortages
- **8.3 Hauler/trader bot:** acquire pack materials, craft pack, load
  vehicle, navigate route, sell, deposit proceeds, return home
- **8.4 Schedules:** home / work / travel / rest / social / emergency
- **8.5 Lightweight social (pre-LLM):** greetings, task acks, status
  messages, contextual canned dialogue, party callouts, trade-route warnings
- **8.6 LLM bridge LAST:** high-level goal choice, conversational variation,
  relationship memory, activity explanations, rumors/flavor. **The model
  does not issue raw gameplay commands — it selects validated goals.**

**Exit test:** a village with 2 farmers, 1 crafter, 2 haulers, 3 adventurers
+ human-owned homes/farms operates a full day across multiple restarts with
an auditable economy.

---

## Module architecture (AzerothCore-inspired, additive capability layers)

Playerbots is the SUBSTRATE; modules are specialized layers around it.
Inspired by: mod-ah-bot-plus · mod-llm-chatter · mod-player-bot-level-brackets
· mod-dungeon-clear · mod-llm-guide. Copy the ARCHITECTURAL ROLES, not the
code. Modules split into two categories:

**Embodied modules** (control real bot characters — MUST use the M5 Gameplay
Actor Contract): adventuring, farming, hauling, dungeon clearing, party
behavior, homesteading.

**Ambient services** (affect the world but are NOT embodied players — must
not masquerade as bot behavior): market liquidity, population direction,
LLM bridge, guide, test coordinator.

Proposed module set (each declares: required observations, required actions,
emitted events, persisted metadata, commands/config, performance budget,
failure modes):

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

**Test harness pattern (copy wholesale):** `.bot test start golden-path
seed=1234` → run → `{run_id, seed, activity, result, failure, stage,
elapsed_seconds}` → replay failed runs. Fits the actor-vs-playerbot
failure taxonomy exactly.

**LLM boundary (locked):** Chatter explains/embellishes — never controls
movement, combat or economy. Guide answers facts — live data only.
Server NEVER waits on an LLM; events queue in, responses queue out.

**Development loop (Hermes as prototype lab):** prototype behaviors in
Hermes → observe failures/edge cases → distill into deterministic game
logic → deploy to thousands of bots. Hermes is the research environment,
not the per-bot runtime.

**The modular shape (what this project actually is):**

```
AAEmu
├── Core gameplay                    (upstream + our canonical fixes)
├── Gameplay Actor Contract          (M5 — the normalize layer)
├── PlayerBot Runtime                (M6 — headless sessions + controllers)
├── Population Director              (level/faction/zone/profession balance)
├── Activity Modules                 (each pluggable, no core edits)
│   ├── Farming · Trading · Adventure · Dungeon · Party
│   ├── Crime · Homestead · Fishing · Siege (later)
│   └── future: Bot Fishing Fleet, Festival Coordinator…
├── Economy Services                 (MarketMaker: bootstrap → living)
├── Social Services                  (Chatter tiers 1-3, async LLM bridge)
├── Guide Services                   (grounded, live-data-only)
└── Test Harness                     (seeded runs, replay, JSONL)
```

New modules (a fishing fleet, a festival coordinator) slot in WITHOUT
touching combat AI or the bot framework — that modularity is the point.

---

## M8.5 — Social services & grounded guide (post-village, pre-activities)

- **8.5a Lightweight social (pre-LLM):** greetings, task acks, status
  messages, contextual canned dialogue, party callouts, trade-route warnings
- **8.5b External LLM bridge (async):** personality + memory + ambient
  chatter via queue — homelab bridge (gestalt/openclaw), never blocking
- **8.5c Grounded guide:** `.guide` commands querying live character state,
  quest/recipe/NPC/housing tables, server-known-issue awareness

---

## M9.5 — Emergent world systems (the "world simulator" layer)

ArcheAge was memorable because SYSTEMS COLLIDED — emergent stories, not
scripted quests. These make the world generate stories whether players are
online or not. Each is event-propagation driven; LLM only decides how to
TELL the story, never what happens.

- **Illegal tree farms:** bots follow incentives (need lumber → public farms
  crowded → remote mountain → plant → leave; other bots spot + harvest +
  sell; owner marks area unsafe, relocates). Emerges from incentive rules,
  NOT scripted "bot steals farm" behavior.
- **Trade pack economy:** market price changes → pack value shifts → farmers
  grow materials → crafters produce → haulers schedule → escorts join →
  scouts spot pirates → route changes. An economy, not a loop.
- **Crime & justice system:** steal → witnesses report → crime points →
  guards pursue → escape/capture → TRIAL → sentence → prison labor →
  release → behavior changes. Bots carry lawfulness/risk-tolerance/faction-
  loyalty/greed/mercy traits — juries become recognizable individuals
  ("don't get arrested while Farmer Edwin is on the jury").
- **Pirates & convoys:** merchant guild schedules convoy → scouts route →
  pirates notice → ambush → escort responds → guards react. Not every
  convoy attacked; not every pirate wins.
- **Rumors (event propagation, no perfect information):** "heard someone
  stole cedar north of Lilyut" → merchants adjust → guards patrol more →
  players hear it from a trader passing through. LLM only narrates.
- **Politics & village identity:** village A (4 farmers/2 traders/1 smith)
  produces excess lumber → trades with village B (ore) → guilds form →
  taxes matter → castles become infrastructure, not just PvP objectives.

---

## M9 — Activities and world events

Contests fit here — they now have actual residents to participate.

Candidates: fishing contest, race-track time trials, scheduled trade
caravans, bot-organized fishing trips, regional monster hunts, community
construction events, simple festival schedule. Music/FX wiring → Lane C.

---

## M10 — Territory and siege (deferred, two slices)

Only after guilds work, combat is stable, bots form groups, and the economy
generates something worth controlling.

- **Slice 1 (no combat):** one castle, one owner, declaration window,
  persistent ownership, tax state, monument interaction, admin recovery
- **Slice 2 (combat-lite):** attacker/defender registration, bot squads,
  objectives, structure health, victory state, reward settlement

---

## Work lanes (permanent, parallel)

- **Lane A — Vision-critical milestones:** the roadmap above (M1-M10)
- **Lane B — Upstream & correctness maintenance:** new regressions, upstream
  merges, security/duplication bugs, persistence corruption, broad engine
  defects, PR preparation ONLY with Josh's approval
- **Lane C — Quick wins:** music wiring, premium labor data, FX groups,
  small packet completions, low-risk data imports. Completed between larger
  tasks; never delays the golden path.

## Resolved planning decisions

| Question | Decision |
|----------|----------|
| M1 full or trimmed? | Trimmed — shared engine fixes + golden route; backlog to Lane B |
| Playtest cadence? | Per-change (focused repro + tests) / per-milestone (golden-path segment) / weekly (integrated human session from clean snapshot); restart mid-play every second week |
| Siege slice? | Deferred to M10; begins no-combat ownership + tax |
| Track 1 capstone? | The complete homestead-to-trade loop (M2-M4), NOT siege |
| Bot density? | Staged gates: 10 correctness → 25 village → 50 soak → 100 only after profiling |
| Track 2 priority? | Action contract → recovery → deterministic combat → curated questing → party → farming → crafting/hauling → schedules → social → LLM → siege |
| Economy sim? | NO abstraction — bots use real systems (Bot Economic Participation) |

## Experience scorecard (alongside the technical scorecard)

| Experience | Current (provisional) | First target |
|------------|----------------------|--------------|
| Start and level with friends | 65% | 90% |
| Build and maintain a homestead | 65% | 90% |
| Craft and transport goods | 55% | 85% |
| Travel using mounts and vehicles | 70% | 90% |
| Survive restart without lost state | 65% | 95% |
| Play with deterministic bots | 10% | 80% |
| World continues while offline | 0-5% | 75% |
| Competitive warfare | 40% | Deferred |

Technical wiring (SCORECARD.md) and experience coverage are related but
separate measurements — update both.

## Timeline (directional, 3-5 sessions/week)

| Period | Target |
|--------|--------|
| Weeks 1-2 | M1 quest and progression spine |
| Week 3 | M2 golden-path release gate |
| Week 4 | M3a homestead shell |
| Weeks 5-6 | M3b persistence and recovery |
| Weeks 7-8 | M4 trade and transport |
| Weeks 9-10 | M5 gameplay actor contract |
| Weeks 11-13 | M6 deterministic bot framework |
| Weeks 14-16 | M7 adventurer and party bots |
| Weeks 17-20 | M8 Living Village |
| Later | M9 activities, M10 territory/siege |

M3b and M4 are the highest-variance items — persistence bugs have
nonlinear scope (object identity, save ordering, parent/child restoration,
phase-state serialization, duplicate loading, schema deficiencies).

Dates are directional — quest data or vehicle attachment may reveal deeper
work.

## Definition of done per milestone

- [ ] Human scenario, automated scenario, AND restart-persistence scenario
      defined and passing
- [ ] Every task: branch, commits, tests (fail-before/pass-after), Rei signoff
- [ ] Full local gate green: Release build + compiler-check + all tests
- [ ] Both scorecards updated in-branch (technical wiring + experience)
- [ ] STATUS.md reflects the milestone (Nei)
- [ ] Milestone release candidate deployed to the AAEmu box by Mai and
      sanity-checked in-game (individual tasks pass the local gate first —
      production churn only at milestone / release-candidate boundaries)
- [ ] No upstream PR without Josh's explicit approval

## Deployment discipline (exact-SHA, auditable)

- **Deployments are EXACT-SHA, never convenience pulls:** on prod,
  `git merge --ff-only fork/develop` — production never generates merge
  commits. Refuse deployment if `git status --short` shows uncommitted
  source changes (environment drift check).
- **Milestone releases get tags:** `git tag living-village-m1-rc1 <sha>`
  pushed to the fork; production deploys the exact tag or SHA. The
  production record becomes: `M1 deployed: <sha>` / previous deployment:
  `<sha>`.
- **Deployment manifest** (`deployments/production.json`, written by the
  deploy script, not hand-maintained): environment, git SHA, deployed_at,
  milestone, database backup name, service health (db/login/game/adminer).
- **DB-changing milestones (M3b, M4+):** record pre-deploy database backup,
  schema/update revision, and Docker image IDs alongside the SHA.
- **Rollback:** `git reset --hard <previous SHA>` + rebuild. For
  DB-changing releases, roll back per the manifest's backup.
- **Bot audit trail** (M5+): structured trace records support debugging AND
  economic auditing — see M5.
