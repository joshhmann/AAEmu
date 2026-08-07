# ARCHITECTURE_REVIEW.md — PlayerBot Scale Architecture (archived 2026-08-07)

Archived from kanban card t_be295ecf (Tai's review, 2026-08-07) by Nei via card t_3a70162c.
This is the decision record for the locked PlayerBot scale architecture folded into ROADMAP.md (M5/M6)
and WORKFLOW.md. Includes the M6.0 decision record (deliverable 7) and the spec §21 lock status.
Target lock: docs folding — fork-only, no implementation.

# ARCHITECTURE_REVIEW.md — PlayerBot Scale Architecture (1,000-citizen spec, code-backed)

Task t_be295ecf · Tai (architect/reviewer) · 2026-08-07
Scope: REVIEW + DECISION RECORD only — no implementation. Read-only review of
fork `joshhmann/AAEmu` develop @ 15922a53 (working tree on feat/m2b-e2e for
E2E; all file:line refs verified against develop content). Upstream
AAEmu/AAEmu develop @ 2246114e fetched read-only for comparison.
Canonical spec: doc_4c80964c551e (21 sections). All 21 sections validated;
11 confirmed as written, 10 confirmed with corrections (marked **CORRECTION**).

---

## Deliverable 1 — Code-backed architecture review

### The five systems that matter, with evidence

**A. Character lifecycle (spec §1-2).** Characters are DB-first models with
`Load()` (Character.cs:2545), `Save(MySqlConnection, MySqlTransaction)`
(Character.cs:2622, full REPLACE INTO), and `SaveDirectlyToDatabase()`
(Character.cs:2590). Activation today lives almost entirely inside
`CSSelectCharacterPacket.Read()` (CSSelectCharacterPacket.cs:16-117):
Load → Connection bind → ObjId assign → `WorldManager.TryAddCharacter` →
`new Simulation(character)` → buffs/HP/MP restore → client packets. The
packet sink is already null-safe (`Unit.SendPacket` →
`Connection?.SendPacket`, Unit.cs:801-804), so a headless Character with a
null Connection is a no-op sender, not a crash — the M2b pilot
(HeadlessSession.cs) proved this live. **CORRECTION:** there is no
`CharacterService` class; the extraction target is
CSSelectCharacterPacket + Character lifecycle. The pilot's
`HeadlessSession.Create` builds DB-row-less characters in a synthetic world —
E2E fixture only, NOT the production citizen path (production = real managed
accounts, ROADMAP M6.0 already locks this).

**B. TickManager / world tick (spec §3-6).** TickManager is one dedicated
thread (TickManager.cs:16-30) invoking ALL subscribers synchronously
(`OnTick.Invoke()` :23, 20ms sleep, warn >100ms). `ActiveRegionTick`
(WorldManager.cs:156-204) subscribes SYNCHRONOUSLY (:365) and iterates ALL
characters (:162), all mates/slaves of all worlds (:168-173), and all active
spawners' `Update()` (:193-196). This is the #1491 starvation mechanism.
**Upstream verdict: NOT fixed** — TickManager.cs and WorldManager.cs are
byte-identical to our fork at upstream HEAD. The 25-bot gate stands.
`TaskManager2` (TaskManager2.cs) is a linear due-time queue firing
unbounded `Task.Run` — fine for sparse bot events, wrong as the bot AI
scheduler.

**C. AIManager (spec §3).** Singleton with ONE shared `List<NpcAi>` and one
lock; `Tick` (AIManager.cs:36-53) iterates every AI under `_aiLock` — one
slow AI blocks dispatch to all others. CONFIRMED: it must NOT become the bot
scheduler. It stays the NPC AI ticker; bots stay out of it.

**D. Region activity semantics (spec §7 — the critical one).** CONFIRMED:
any `Character` increments `Region._playerCount` (+ neighbors)
(Region.cs:56-62); `HasPlayerActivity()` = `_playerCount > 0`
(Region.cs:303-306); consumed by `NpcAi.Tick` (NpcAi.cs:148), AreaTriggerManager
(:55), SphereQuestManager (:162). `Region.AddToCharacters` flips
`npc.Ai.ShouldTick = true` on every visible NPC (Region.cs:134-136).
`NpcSpawner.IsPlayerInSpawnRadius` (NpcSpawner.cs:455-493) iterates
`GetAllCharacters()` with a 10s cache. **Blind bot embodiment wakes the NPC
world everywhere bots travel — the spec's #1 stability risk is real and
mechanism-verified.** Fix = 2-line guarded change (see deliverable 4) +
explicit bot activity opt-in.

**E. Navigation (spec §9).** Reusable assets confirmed: `PathNode`
(AI/AStar/PathNode.cs:15, `FindPath(world, start, goal)` :74 — A* over
geodata), `AiPathHandler` (AI/v2/Controls/AiPathHandler.cs — waypoint queue
+ per-tick `RunCurrentPath`), `Simulation` (Units/Route/Simulation.cs —
route controller with a `Character` property already at :34), and the
NPC movement broadcast pattern (`Npc.MoveTowards` → Transform +
`BroadcastPacket(SCOneUnitMovementPacket)`, Npc.cs:1331-1361). No second nav
engine needed; a Character-side MoveTowards variant (reusing the same
moveType/packet code) is the narrow addition. Pilot `BotPath.cs` already
provides waypoint math with bounded steps.

**F. Persistence (spec §13).** Exactly two lifecycles today: (1) periodic
`SaveManager.DoSave()` — ONE global transaction over houses/mail/items/
auction/crimes + ALL characters (SaveManager.cs:63-100), `AutoSaveInterval`
5.0 min; (2) leave/logout — GameConnection remove path
(GameConnection.cs:203-234): Delete → TryRemoveCharacter →
SaveDirectlyToDatabase. No third lifecycle needed: bots ride both,
plus additive `playerbot_*` metadata flushed dirty-only. **NB:** inventory
Load/Save inside Character is commented out (Character.cs:2560) — inventory
persists only via SaveManager's itemManager.Save; bots must not rely on
leave-save for inventory (M2b evidence).

**G. Actor Contract (spec §16).** M5 exists ONLY in ROADMAP.md (304-366) —
no contract code anywhere (grep: 0 files). The M2b pilot PlayerBotController
is a mini quest-engine contract (real AddQuest/DoReportEvents paths) —
the seed, not the M5. The roadmap's M5 slice plan, failure taxonomy, and
audit-trail shape are already correct and spec-aligned.

---

## Deliverable 2 — Files/classes: reuse / extend / avoid

**REUSE as-is:**
| File/Class | Why |
|---|---|
| `Models/Game/Char/Character.cs` | Load/Save/SaveDirectlyToDatabase/IsOnline/SetPosition |
| `Core/Managers/World/WorldManager.cs` | TryAddCharacter :1324, GetAllCharacters, region placement :990, GetAround |
| `Models/Game/World/Region.cs` | object graph + neighbor visibility (after §7 guard) |
| `Models/Game/AI/AStar/PathNode.cs` | A* pathfinding over geodata |
| `Models/Game/AI/v2/Controls/AiPathHandler.cs` | waypoint queue + RunCurrentPath |
| `Models/Game/Units/Route/Simulation.cs` | route controller; Character property exists |
| `Core/Managers/TaskManager2.cs` | sparse event triggers (crop/mail/schedule) |
| `Core/Managers/SaveManager.cs` | periodic save loop + shutdown flush hook |
| `Models/Game/Bots/BotPath.cs` | waypoint movement math (pilot) |
| `Models/Game/Bots/BotSafety.cs` | BotSafetyMonitor: stuck/nav-timeout/bounds/inventory-full/tick-budget/combat-gate (pilot, M6.2-ready) |
| `Models/Game/Bots/BotBehaviors.cs` | BotBehaviorStack: Idle/Roam/QuestDrive/Return (pilot, M6.3-ready) |
| `Models/Game/Bots/PlayerBotController.cs` | real quest-engine driver (pilot — becomes the M5 adapter seed) |
| `Core/Packets/C2G/CSSelectCharacterPacket.cs` | human activation body → extraction source |

**EXTEND (small, reviewed, additive-first):**
| File | Change |
|---|---|
| `Models/Game/Char/Character.cs` | add `IsPlayerBot` + fidelity enum (spec §7) |
| `Models/Game/World/Region.cs` | playerCount guard: only count non-bot Characters; add explicit bot-activity opt-in |
| `Models/Game/NPChar/NpcSpawner.cs` | IsPlayerInSpawnRadius: exclude reduced-fidelity bots |
| `Core/Managers/SaveManager.cs` | bot metadata dirty-flush integration (not inside Character.Save) |
| `Core/Managers/World/WorldManager.cs` | ActiveRegionTick: async/time-budgeted execution (gate for >25 bots) |
| `Models/Game/Bots/HeadlessSession.cs` | production variant: real account+character rows (keep E2E variant) |

**AVOID:**
- `Core/Managers/AIManager.cs` as bot scheduler (single-lock, all-AI serial)
- Per-bot TickManager subscriptions (TickManager.cs:71-126 linear list)
- `HeadlessSession.Create` DB-row-less path for production citizens
- Fake-client/packet-perception layer (spec §8 — direct query instead)
- A second persistence engine or third save lifecycle

---

## Deliverable 3 — Minimal new interfaces/classes

1. `ICharacterLifecycleService` (impl `CharacterLifecycleService`) —
   `ActivateHuman(connection, character)`, `ActivateHeadless(character,
   botContext)`, `Deactivate(character, reason)`. Human path = today's
   CSSelectCharacterPacket body; headless = Load/ObjId/world-add/buffs/HP-MP
   minus packets. Thin `IPacketInitializer` for the human-only packet block.
2. `IPlayerBotManager` — registry, spawn/activate/deactivate, lookup,
   runtime ownership, diagnostics (spec §3 responsibilities).
3. `IPlayerBotScheduler` — `PriorityQueue<BotId, NextWakeTime>` due-time
   queue + event queue + bounded worker pool (Channel, 4-8 workers),
   per-bot execution lease, wake latency + queue-depth metrics. Lightest
   possible TickManager relation: ONE async subscription for its own
   ​wake-scan, or a dedicated thread — never per-bot subscriptions.
4. `IPopulationDirector` — fidelity assignment (Dormant/Reduced/Full),
   density by zone/activity/pressure, wake/sleep decisions, transition
   safety gate (no downgrade in combat/slave/pack/trial/party/saving).
5. `IGameplayActor` (M5 contract) — Observe/Move/Stop/Target/Cast/Interact/
   Loot/UseItem/Mount + lifecycle
   Requested→Accepted→Running→Completed|Rejected|Interrupted|TimedOut;
   audit trace records. PlayerBotController becomes its first adapter.
6. `IBotPerceptionService` — direct server-state observations (nearby
   Characters/NPCs/Doodads/Mates/Slaves, target, combat state, inventory,
   cooldowns, quests, zone state) via World/Region/services — NO packets.
7. `IBotPersistence` — dirty-flagged bot metadata (profile/schedule/activity/
   home/memory_flags/population_state), flush on deactivate/shutdown/transition.
8. `IBotActivity` (spec §18) — modular activities (FarmMaintenance,
   TradeRun, QuestRoute, …) each with observations/actions/events/metadata/
   budget/failure modes.
9. `Character.IsPlayerBot` (bool) + `BotFidelity` enum on Character —
   the §7 marker; no other core flags needed.

Placement: `AAEmu.Game/Models/Game/Bots/` grows into the runtime;
interfaces in `AAEmu.Game/Core/Managers/` (or an `AAEmu.Bot.Core` project
when module split lands — roadmap's AAEmu.Bot.* names are already the target).

---

## Deliverable 4 — Required narrow core hooks/refactors

**H1 (P0, §7): Region activity split** — Region.cs:56-62 count only
`Character { IsPlayerBot: false }`; add `Region`-level explicit bot-activity
registration (e.g. `AddBotActivity(character)`/`RemoveBotActivity`) that
full-fidelity bots (or humans) call to wake the ecosystem. Keeps all 6
consumers (NpcAi, AreaTrigger, SphereQuest, spawner radius, AddToCharacters
ShouldTick, region visibility) correct with a 2-line semantic change.
Corollary: NpcSpawner.cs:470-484 skips reduced-fidelity bots.

**H2 (P0, §6): ActiveRegionTick must not starve the world tick** —
WorldManager.cs:365 subscription is synchronous. Convert ActiveRegionTick to
`useAsync:true` + a time-budgeted step (or a dedicated region-tick thread),
with a hard 100ms budget and drop-oldest semantics for spawner updates.
This is the #1491-class fix; gate >25 bots on it.

**H3 (P1, §2): Lifecycle extraction** — move the
Load→ObjId→TryAddCharacter→Simulation→buffs/HP/MP block out of
CSSelectCharacterPacket into `CharacterLifecycleService`; packet block
becomes the human-only `IPacketInitializer`. Byte-identical human behavior
(verified by the M2b E2E golden route before/after).

**H4 (P1, §13): SaveManager batched-bot flush** — keep the global
transaction for humans; add a separate dirty-flush path for playerbot_*
metadata (bounded batch, own connection, mandatory on deactivate/shutdown
via SaveManager.StopAsync hook at :52-54).

**H5 (P2, §9): Character-side MoveTowards** — extract the movement-packet
build from Npc.MoveTowards (Npc.cs:1300-1361) into a Unit-level helper so
Characters move + broadcast identically without Ai.Owner coupling.

---

## Deliverable 5 — Concurrency/locking/performance risks

| # | Risk | Sev | Evidence | Mitigation |
|---|---|---|---|---|
| R1 | ActiveRegionTick sync on tick thread, O(all chars+spawners) | P0 | WorldManager.cs:156-204,365 | H2 async+budget |
| R2 | Bot presence wakes NPC AI/spawners world-wide | P0 | Region.cs:56-62,303-306; NpcAi.cs:148; NpcSpawner.cs:470 | H1 activity split |
| R3 | AIManager single lock serializes all NPC AI; adding bots degrades it | P1 | AIManager.cs:36-53 | bots never in AIManager |
| R4 | Unbounded Task.Run (TickManager async, TaskManager2) | P1 | TickManager.cs:99; TaskManager2.cs:49 | bounded bot worker pool |
| R5 | SaveManager global transaction grows with bot count | P1 | SaveManager.cs:63-100 | H4 split flush |
| R6 | Character.Save full REPLACE INTO, no dirty tracking | P2 | Character.cs:2637-2660 | dirty flags for bot metadata |
| R7 | `_aiLock` held across all AI ticks → lock contention at scale | P1 | AIManager.cs:38 | (unchanged for NPCs; bots excluded) |
| R8 | Region.RemoveObject linear scan (swap-remove) | P3 | Region.cs:80-87 | watch at 1,000/region |
| R9 | Region Add/Remove lock `_objectsLock` on hot paths | P2 | Region.cs:30 | profile; per-region sharding later |
| R10 | Inventory persistence gap (leave-save doesn't save inventory) | P2 | Character.cs:2560; M2b evidence | bots: rely on periodic save; never leave-save critical state |
| R11 | PlayerBotScheduler single global queue — one slow bot stalls wake scan | P1 | (new code) | per-bot lease + time-sliced wake scan + metrics |

---

## Deliverable 6 — M5/M6 roadmap edits (draft text, for Nei)

1. **M5 intro (after line 310):** add — "Execution boundary: the M5 actor
   contract runs exclusively through CharacterLifecycleService; no controller
   may mutate a Character outside an active actor request (single-writer rule)."
2. **M5 Existing primitives (line 312):** add — "PlayerBotController (M2b
   pilot, Models/Game/Bots) as the first adapter seed; BotSafetyMonitor +
   BotBehaviorStack as reference safety/state layers."
3. **M6.0 (after line 378):** add — "Embodiment entry: headless activation
   reuses the CSSelectCharacterPacket lifecycle core (Load → ObjId →
   WorldManager.TryAddCharacter → buffs/HP/MP) extracted into
   ICharacterLifecycleService; packet initialization is human-only."
4. **M6.0:** add — "Region activity rule (P0): bots are ordinary Characters
   but DO NOT count toward Region player activity by default; only
   full-fidelity bots/humans wake NPC AI, spawners, area triggers, sphere
   quests (H1)."
5. **M6.1:** add — "Scheduler: dedicated IPlayerBotScheduler (due-time
   priority queue + bounded 4-8 worker pool + per-bot lease). Never add bots
   to AIManager; never one TickManager subscription per bot."
6. **M6.1:** add — "Persistence: playerbot_* metadata tables, dirty-flag
   flush, mandatory flush on deactivate/downgrade/shutdown. No writes from
   the AI step loop."
7. **M6 exit test (line 428):** add — "25-bot starvation gate: no soak above
   25 concurrent embodied bots until ActiveRegionTick is async/time-budgeted
   and profiled (TickManager duration, ActiveRegionTick, AI tick, scheduler
   latency, pathfinding, DB pressure)."
8. **M6.5 (line 416):** rename Tier labels to spec fidelity names
   (Dormant/Reduced/Full) and add PopulationDirector as the only fidelity
   authority; document no-downgrade conditions (combat/slave/pack/trial/
   party/saving).

---

## Deliverable 7 — M6.0 embodiment/scheduler decision record (CONCRETE)

**DECISION (validated against code — all 14 spec §21 locks confirmed
feasible):**

- **Embodiment: PlayerBotManager + real accounts.** A bot citizen = real
  `aaemu_login` account (account_type=HeadlessBot, blocked from client
  login) + ordinary `characters` row + `HeadlessSession` (production
  variant) + `PlayerBotController` via `ICharacterLifecycleService.
  ActivateHeadless`. No fake client, no network socket, no login-handshake
  emulation. Packets no-op through the null-safe sink. Reuses
  CSSelectCharacterPacket's lifecycle core verbatim.
- **Scheduler: dedicated PlayerBotScheduler** (NOT AIManager, NOT per-bot
  TickManager). One due-time `PriorityQueue<BotId, NextWakeTime>` + event
  queue + bounded worker pool (4-8 initial, configurable). TickManager
  relation: exactly one async subscription for wake-scan (or own thread) —
  spec §20's "at most a very lightweight relationship" is the target.
- **Fidelity authority: PopulationDirector** owns Dormant↔Reduced↔Full
  transitions with the no-downgrade guard list; density and pressure
  feedback (spec §14) live here.
- **Perception:** direct server queries via M5 Observe (region lists,
  WorldManager, game services). No packet serialization for bots.
- **Visibility:** bots are ordinary Characters in the region graph →
  `BroadcastPacket`/GetAround already reaches humans; SCOneUnitMovementPacket
  broadcast (Npc.cs pattern) makes them visibly move. Zero extra work — the
  spec §8/§21-9 requirement is satisfied by construction.
- **Persistence:** normal Character persistence + playerbot_* metadata with
  dirty/batched writes (H4). No third lifecycle.
- **NOT LOCKED (needs profiling):** concrete per-fidelity bot counts
  (spec §15), Abstract fidelity tier (deferred), M5 contract action
  vocabulary finalization.

---

## Deliverable 8 — Density/stability gates

| Stage | Bots | Gate |
|---|---|---|
| 1 | 10 | Correctness: M6 exit criteria + E2E golden route green |
| 2 | **25** | **FIRST STABILITY GATE — hard stop until H2 (ActiveRegionTick async/budgeted) lands + TickManager/AI-tick/region-tick profiling approved** |
| 3 | 50 | Soak ≥6h: no tick-budget overrun, no unrecovered loops, no DB corruption |
| 4 | 100 | Active-population profiling: scheduler latency p95, pathfinding/sec, DB writes |
| 5 | 250 | Mixed fidelity: ≥60% dormant/reduced; region-hotspot watch (R8/R9) |
| 6 | 500 | Broader region/event tests: spawner wake, area triggers, sphere quests, pressure states |
| 7 | 1000 | Final target: ≥80% dormant/reduced; full-fidelity count = profiling result, never promised |

Rule (spec §15/§21-13): **1,000 persistent citizens, not 1,000 thinking
clients.** Each stage needs a no-bot baseline + approved numeric budgets
(already the M6 exit-test pattern, ROADMAP.md:435-438).

---

## Deliverable 9 — Observability metrics to add before scaling

1. TickManager: invoke duration p50/p95/max; per-subscriber duration
   (identify slow sync subs); subscriber count
2. ActiveRegionTick: duration, characters/mates/slaves/spawners per pass,
   spawner-update time
3. AIManager: tick duration, AI count, lock-wait time
4. PlayerBotScheduler: queue depth, wake→start latency, due-per-cycle,
   worker utilization, per-fidelity counts, event-queue depth
5. Pathfinding: PathNode.FindPath calls/sec, duration, path length, geodata
   misses
6. DB pressure: SaveManager.DoSave duration, per-table rows, per-character
   save time, playerbot flush count/duration, pool pressure
7. Region: per-region object/character counts (hot spots), HasPlayerActivity
   eval rate
8. Population: embodied counts by fidelity, transitions/sec, pressure state

All net-new (only Warn>100ms logs exist today). Implementation vehicle:
NLog structured logging + a lightweight in-process ring buffer exposed via
WebApi (existing Services/WebApi), or an additive metrics module — decision
left to the M6.1 card.

---

## Deliverable 10 — Task cards for the first safe PlayerBot implementation slice

Filed by the director as separate cards (specs only, per card law: one
deliverable each, ≤60 turns, parent-gated chains). Suggested order:

1. **H1 slice (P0 gate):** Region activity split — `Character.IsPlayerBot`
   + Region playerCount guard + NpcSpawner exclusion + explicit
   bot-activity opt-in. Test: bot in region does NOT tick NPC AI / spawners;
   human behavior byte-identical (Rei gate).
2. **H2 slice (P0 gate):** ActiveRegionTick async/time-budgeted + TickManager
   duration metrics. Test: 100ms budget respected under load; golden route
   green.
3. **CharacterLifecycleService extraction (P1):** CSSelectCharacterPacket
   core → service; human path unchanged (fail-before/pass-after via E2E).
4. **Production HeadlessSession + account provisioning (P1):** real
   account/character rows, HeadlessBot flag, client-login block.
5. **IPlayerBotManager + registry + diagnostics (P1).**
6. **IPlayerBotScheduler v1 (P1):** due-time queue + 4-8 bounded workers +
   per-bot lease + metrics (gate: no per-bot TickManager subs).
7. **playerbot_* schema + IBotPersistence dirty-flush (P1):** SQL updates
   + flush hooks on deactivate/downgrade/shutdown.
8. **M5 IGameplayActor v1 (P1):** Observe/Move/Stop/Target/Cast + lifecycle
   + trace; PlayerBotController adapts. Actor tests prove server executes
   correctly (spec §17 split).
9. **PopulationDirector v1 (P2):** fidelity assignment + no-downgrade
   guards + pressure thresholds.
10. **Gate harness (P2):** 10→25→50 staged soak runner with the metrics
    budget checks (reuses E2E rig patterns).

---

## Spec §21 lock status (summary)

1-6, 8-14: **LOCKED** (validated). 7 (activity split): **LOCKED as H1** —
the only core change required, now precisely scoped. 14 (1,000 persistent):
**LOCKED as target** with stage gates. No spec assumption was found
unimplementable; the two biggest corrections: (a) #1491 is unfixed upstream
too — gate stays; (b) the pilot's DB-row-less HeadlessSession must not
become the production citizen path.

## Evidence index
- Character lifecycle: CSSelectCharacterPacket.cs:16-117; Character.cs:2545,2590,2622; Unit.cs:801-804
- Tick/threading: TickManager.cs:16-30,71-126; WorldManager.cs:156-204,365; TaskManager2.cs:40-74; AIManager.cs:36-53
- Region activity: Region.cs:56-62,134-136,303-306; NpcAi.cs:144-150; NpcSpawner.cs:455-493; AreaTriggerManager.cs:55; SphereQuestManager.cs:162
- Nav: PathNode.cs:74; AiPathHandler.cs:43-111; Simulation.cs:27-34; Npc.cs:1253-1362
- Persistence: SaveManager.cs:63-100; GameConnection.cs:203-234; World.json (AutoSaveInterval 5.0)
- Visibility: GameObject.cs:133-139 (BroadcastPacket → GetAround<Character>)
- Roadmap: ROADMAP.md:304-438 (M5/M6), 481-550 (modules)
- Upstream: AAEmu/AAEmu develop @ 2246114e — TickManager/WorldManager identical to fork (diff empty)
