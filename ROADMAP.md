# ArcheAge Slums — Roadmap & Milestones (locked-shape 2026-08-03; version number retired 2026-08-04 — v1/v4/v6/v7 label drift, the date is canonical)

> **🚫 THE RULE (Josh, permanent): NEVER push a branch or open a PR to
> upstream AAEmu/AAEmu.** Upstream is intake-only: fetch and integrate its
> updates into the fork; never send fork changes back.
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
- Additive layer rule (refined 2026-08-04): prefer composition, adapters, and
  existing extension points. Allow only narrow, reviewed core hooks when
  required to reuse the normal Character/session lifecycle — and NEVER create
  a parallel character, inventory, quest, property, or economy implementation.
- **Test boundary rule (actor vs playerbot):** *Actor contract tests* prove
  the server can execute and observe a command correctly. *Playerbot
  behavior tests* prove a controller chooses the right command sequence.
  Never blur them — every bot failure must be debuggable to one of:
  wrong choice / navigation / action rejected / state transition / persistence.
- **The golden path is the product.** All work is judged against:
  level with friends, get mounts, claim land, grow things, craft packs,
  ride carts, sail and trade.
- **M5-stand-in rule (locked 2026-08-09 — bots as the test force):** the
  roadmap's human gates assume 2–4 testers; there is one human. Once the M5
  actor contract + A1 execution boundary land, scripted M5 actors are the
  default testers for the H (human) dimension of Lane D mechanics and for
  milestone functional gates — a bot completing the scenario end-to-end
  through real gameplay services IS the functional evidence. Two limits:
  ① bots prove function, never feel — Josh still gates experience verdicts
  (does it look right, is it fun, does the village feel alive); ② bot-passed
  evidence must survive independent audit (the Rei/auditor lane reviews the
  trace, not the claim) — a bot can pass broken content if the harness can't
 see the failure (the runnable-≠-playable lesson). This rule is why the bot
 track and Lane D run in parallel: M5 isn't a detour from feature
 completeness, it's the staffing plan for it.
 **H-grade boundary (reconciled 2026-08-12, bot-backtrack program):**
 SCORECARD's H dimension means an ACTUAL PLAYER. A bot or scripted actor
 completing a scenario is functional evidence (A dimension) — never H=2.
 Bot/scripted-actor evidence is labeled proxy/bot-functional (H=1 at most),
 and actual H stays UNKNOWN until Josh runs the curated scenario.
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

## Upstream alignment rules (Josh, locked 2026-08-04 — every milestone)

These keep upstream pulls into the fork clean and reviewable. They do not make
outbound PRs an option. Verify against the wiki/code before applying; the
current-state check is recorded in `Docs/wiki/Development-Conventions.md`.

1. Target AAEmu `develop` and .NET 10 (global.json pins 10.0.0).
2. Local contributor debugging: prefer the Aspire AppHost when practical.
   Production stays on the current Docker Compose deployment (see
   `deployments/production.json` — db/login/game/adminer stack on `.165`).
3. `compact.sqlite3` is READ-ONLY reference data. Mutable bot, character,
   economy, schedule, memory, and runtime state lives in MySQL or an additive
   bot metadata schema. Never write to the reference sqlite.
4. Config precedence: `Config.json` → `Configurations/*.json` →
   `Config.Local.json`. Keep machine-specific hosts, secrets, API endpoints,
   paths, and credentials OUT of shared config.
5. Server listings come from `GameServers` configuration. Do NOT reintroduce
   the legacy `aaemu_login.game_servers` approach.
6. New managers and services use explicit constructor dependencies where
   AAEmu supports them. No hidden singleton lookup, no undocumented startup
   order.
7. Startup loading can be parallel. Shared mutable collections and
   initialization logic must be concurrency-safe.
8. AAEmu-native terminology everywhere (code, logs, cards, searches):
   Doodad = crops/trees/furniture/doors · Mate = pets and mounts ·
   Slave = carts/cars/ships · Transfer = fixed-route transports ·
   Expedition = guild · Dominion = castle/siege · Ability = combat skill
   tree · ActAbility = vocation/proficiency.
9. PlayerBots compose around ordinary `Character` records and normal
   gameplay services (headless login accounts + `HeadlessGameSession` +
   additive `PlayerBotController`, M6.0). No parallel character/inventory/
   quest/property/economy implementation.
10. Additive-layer rule (refined): composition, adapters, existing extension
    points first; narrow, reviewed core hooks only when required to reuse the
    normal Character/session lifecycle; never a parallel gameplay path.

---

## M0 — Foundation ✅ COMPLETE

Workflow v3 foundation (now superseded by v4 one-way-upstream policy),
community guidelines, kanban templates, gate.sh
verified, scorecard + 3 exploration reports, graphify graph (17.6k nodes),
shared division skill enabled on all 4 profiles.
BUG-006 (kill-acceptor, 380 quests, 1082/1082 tests) parked awaiting Josh's
merge/deploy decision.

---

## M1 — Quest and progression spine (Track 1)

Trimmed, not exhaustive. Fix shared engine defects + the selected golden
route. Individual peripheral quest bugs → Lane B (maintenance).

**Work:**
- ✅ BUG-006 kill-acceptor (380 quests, 1082/1082 tests) — merged to fork
  develop; LIVE in the M1 engine-health release @ 94f498fc (2026-08-04)
- ~~Load + validate quest_act_obj_aliases (2,746 dangling rows)~~ — ✅ VERDICT 2026-08-04: dormant id→name dict, zero live refs in 1.2 data (no use_alias=1 rows, no QuestActObjAlias act type) — no-op
- ✅ Stub-act audit — 2026-08-04: 3 real stubs (CheckGuard silent-pass,
  ItemGroup gather/use stall), 274-ctx watch item, 7,607 orphaned act rows;
  fixes LIVE @ 94f498fc (BUG-008/009, 30c2b689)
- ✅ Quest sanity verifier (startup cross-check) — BUG-007, 14 tests, LIVE
  @ 94f498fc (first live census 5 ERR / 128 WARN / 4 INFO over 4775 quests)
- ✅ Doodad phase/interaction objectives (quests 922/3889/3447) — resolved;
  T1 Solzreed 97/97 (2026-08-04)
- ✅ Solzreed golden route selected; curated Nuian opening chain +
  intentionally excluded quests documented — Docs/wiki/Golden-Route-Solzreed.md (99e7c4ec)
- 🔶 **WIDENED 2026-08-04 (Josh): verifier data-defect backlog folds into M1**
  — real structural defects from the live census (5 ERR / 128 WARN over 4775
  quests), priority after golden-route blockers:
  - COMPONENT_NEXT_MISSING quests 776/777 (next_component refs to nowhere) —
    ✅ FIX (in-memory overlay, branch fix/next-missing-776-777, Rei gate
    t_d8a8c798)
  - ACT_REF_MISSING_QUEST 2145→2146 (self-start target can never be found) —
    ✅ PRUNE (dangling accept-acts, t_60a559ab)
  - QUEST_NO_START cluster 1533–1548 (components but no Start — can never be
    accepted) — ✅ DROP 2026-08-05 (Josh): 23 legacy tutorial shells,
    t_5140fb35
  - QUEST_NO_COMPONENTS 1391 (template has no components at all) — ✅ DROP
    2026-08-05 (Josh): dummy shell, t_5a61cee3
  - 8 orphaned quest_contexts (745, 1421, 1954–1958, 2140) — ✅ DROP
    2026-08-05 (Josh): t_0ac25620
  - Register of every dropped id + restore pointers:
    scorecard-explorations/dropped-content-register.md
- 🔶 Harness extension (M1-5d, t_f198bb0e / M1-5e, t_9fc77eb): 14 unsupported
  act families → census coverage grows past 153 (currently 25 harness-gap
  SKIPs) — **MOVED TO M2 (Josh 2026-08-05)**: enhancement track, not a
  defect; M1 closes on the defect backlog + playtest.

**M1 status (2026-08-04 → 08-05; reconciled 2026-08-11):** ✅ core
delivered — **CLOSED on automated evidence** (automated exit GREEN —
153/153 runnable, superseded by G1 4,573/4,573 — plus restart-persistence
retroactively evidenced via the M2 restart baseline t_cca63225 and live
probe t_92a41fe6; M1-M3 audit t_5b1f5494). Human playtest verdict REMAINS
OPEN — board Open Decision #1, pending Josh's walk of Solzreed (C5,
tracked separately) — **recorded as an explicit deferred gate (M1 human
route; bot-backtrack program, see deferred gates below).** M1 WIDENED 2026-08-04 (Josh): the verifier
data-defect backlog rides in M1; **harness extension MOVED to M2 (Josh
2026-08-05)** — M1 closes on defects + playtest.
M2 remains the world-broadening release gate. All work items done: shared
engine defects fixed, golden route curated, doodad phase/interaction family
resolved. Automated exit test GREEN — scenario-harness census
(QuestScenarioTierTests) headline **153/153 runnable / 0 FAIL / 33 SKIP
over 186 quests** (T1 Solzreed 97/97; T2 29/29 + 6 SKIP; T3 27/27 + 27
SKIP); full gate 1148/1148 — runnability line GREEN. PROD DEPLOYED @
94f498fc (2026-08-04 20:30, M1 engine-health release — BUG-007/008/009/
010/011/012 live); verifier first live census 5 ERR / 128 WARN / 4 INFO
over 4775 quests — data-fix backlog seeded, 3 WARNs are verifier
stale-registry false positives (fix card t_913c1d4a). **Widened backlog
2026-08-05: 776/777 FIX, 2145→2146 PRUNE, cluster + 1391 + 8 orphans DROP —
all decided (Josh), rigged, Rei-gated; 1391 last merge pending gate
(t_70ae1bba); batch deploy awaiting Josh GO** (prod still @ bddd426e,
Round-2). Deploy incident:
39GB container json.log (100% disk) pre-deploy — truncated; rotation fix
shipped (t_264e1984 ✅). Remaining census SKIPs: 8 orphaned quest_contexts
(data) + 25 harness gaps (14 unsupported act families — ObjZoneKill,
ObjAggro, ObjCompleteQuest, EtcItemObtain, …) — queued in
scorecard-explorations/runnability.md.

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

## M2 — Golden-path specification and baseline gate

Define the repeatable playable journey and establish an evidence-backed
baseline before repairing its housing and trade segments. **M2 is a planning
and discovery gate, not a claim that the entire loop already works.** M3
investigation may begin while M2 documentation and test tooling are finalized.

**Golden path:** create character → starter progression → unlock mount →
acquire farm → plant & harvest → build house → craft trade pack → transport
pack → sell → return home.

**Primary outputs:** curated route · human playtest checklist · scenario
manifest · restart checkpoints · known-blocker registry · structured logging
expectations · reproducible database reset/seed procedure. Do not commit a raw
database snapshot containing accounts, secrets, or production state. (Selected race/faction, zones,
quest chain, skill builds, mount, housing zone, crop chain, crafting chain,
trade-pack recipe, land route, cart/hauler, short sea route if viable.)

**Golden-path zone — LOCKED (Josh, 2026-08-03): SOLZREED.** The Nuian
coastal starter zone: starter quests, nearby farms, trade pack routes, safe
waters. Route selection starts from Solzreed outward (Solzreed → adjacent
zones as the loop expands).

**Inherited from M1 (Josh 2026-08-05): harness expansion track** — M1-5d
(14 unsupported act families, t_f198bb0e) + M1-5e (T4 full-corpus census,
t_9fc77eb) ride in M2: census coverage grows past 153 (currently 25
harness-gap SKIPs) as M2's scenario manifests expand along the golden path.

**Exit tests:**
- **Human baseline:** two players attempt the entire route from the
  reproducible reset state; every blocker is captured with stage, repro,
  evidence, and its owning M3/M4 card. GM repair may be used only after the
  original failure is recorded so later segments can still be surveyed.
- **Automated baseline:** the manifest and reset procedure reproduce the
  selected character, item, property, recipe, pack, and vehicle prerequisites.
- **Restart baseline:** restart checkpoints identify exactly which state is
  retained, lost, duplicated, or requires repair.

M2 closes when the route and backlog are reproducible. The first integrated
playable release gate occurs at M4, after M3 and M4 have repaired the systems
the route depends on.

**Depends on:** M1 (done). **Feeds:** M3/M4 (route + blocker backlog); M4's
gate chains to M2's reset/seed procedure.

**Detail (2026-08-10 audit):** M2 was silently redefined in practice into the
quest-census sweep (M2a–M2d) — the band tables and ≥95% gates live only in
progression-board.md and are adopted here by reference. Census reality:
**G1 GATE PASSED 2026-08-10** — 4,579 live contexts = 4,573 PASS + 6
kept-by-ruling doc-SKIP + 0 FAIL, zero unexplained (4,876 rows − 297
registered drops; full gate 1495/0/1 on merged develop @ 7f5c179f7). The
detailed work list was **G1 WI-1..12** (4 harness-family closures → band
sweeps 31-40/41-50/51-55/0-null → final census) — all landed; see the G1
section for per-item status and kept-by-ruling SKIPs. The inherited harness
track (t_f198bb0e) exits via WI-2..5. Census credit counts only once merged
to develop (rule G0-1); deletion is never "fixed" — drops require register
entry + Josh decision.
The reset-procedure exit test is strengthened: a third party (or clean host)
must run the reset/seed procedure from the docs — the manifest may not
validate itself. If two humans are unavailable for the baseline, M5-contract
bots may stand in once M5 lands (human-capacity rule, G0) — **for the
AUTOMATED baseline only: a bot-driven baseline is proxy/bot-functional
evidence, never H=2. The ORIGINAL human baseline (two players, no GM repair)
remains an explicit deferred gate (bot-backtrack program; Josh-owned).**

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

**M3a status (2026-08-11, H reconciled 2026-08-12):** ✅ **COMPLETE (bot-functional/proxy evidence)** — merged to develop @
4d0427b96 (2026-08-10); Rei gates t_72c787c8 / t_449875bd ACCEPT.
`M3aExitScenarioTests` on develop: 2 scripted actors (M5-stand-in rule),
adjacent homesteads (16m enforced, 10m overlap REJECTED), curated objects
in ONE uninterrupted session (placement → construction → crops → storage →
furniture), real engine paths (HousingManager.Build / CraftEffect /
Doodad.Use / CofferContainer). Scorecard: `HOUSING-01` / `FARM-01`
C/W/A = 2; **H = UNKNOWN (proxy/bot-functional only — scripted actors;
no actual-player evidence yet)**. M3a human route + contract replay are
explicit deferred gates (below).

### M3b — Property persistence and recovery (engineering-heavy)

Furniture + bound doodad persistence · door/window phase state · crop +
livestock recovery · rotation/attachment integrity · storage persistence ·
server restart restoration · disconnect + logout cleanup · orphan/duplicate
prevention · administrative repair tooling.

**Exit condition:** the same two homesteads survive repeated logout,
restart, crash-recovery, and re-entry tests WITHOUT state loss or
duplication.

**Scorecard targets:** `HOUSING-01` and `FARM-01` reach `C/W/A = 2` for the
curated homestead at M3a (A via the scripted-actor stand-in; **H = actual
player only — H reaches 2 only when Josh runs the curated scenario; until
then H stays U (proxy/bot-functional evidence, never H=2)**); `PROPERTY-01`
and the same curated objects reach `R = 2` with recovery/load evidence at
M3b. No percentage substitutes for a missing scenario dimension.

**Depends on:** M2 (route + blocker backlog). **A4 (SaveManager dirty
tracking) is a hard prerequisite for M3b** — the single-sync-transaction save
is exactly where homestead persistence breaks at scale; M3b owns auditing it
for property objects. **Feeds:** M4, M8 (farmer/crafter bots need real
homesteads).

**Detail (2026-08-09 audit):** both exits were human-only prose — the standing
three-scenario rule now applies: add an automated homestead-persistence
scenario (place → decorate → plant → harvest → restart → assert) to the
integration harness before M3b can close. "Repeated logout, restart,
crash-recovery" is quantified: N≥3 cycles, crash method = kill -9 mid-save
and container kill during harvest, both defined in the scenario. Add a
save-duration budget at gate scale (autosave p95 < 2s with the two
homesteads + 25 bots embodied) so M3b can't pass on a save path that kills
M8 later. M3a's two-player gate may use the M5-stand-in rule.

**M3b status (2026-08-11):** ✅ **COMPLETE** — M3b-1..4 merged to develop
(5dc7c2fbd / 71b43e09f / 3913932bf / 5981246ea, 2026-08-11); EXIT gate
t_accb1c63 PASS 7m08s (merge f5b00c686): N=3 crash cycles — restart,
kill -9 mid-save (open autosave transaction observed in MySQL
INNODB_TRX), container kill during open save — 16 rows asserted per boot,
no loss/dup. Save-duration budget PASS: autosave p95 1301ms < 2000ms at
25 bots + 2 homesteads. Scorecard: `PROPERTY-01` R = 2 (U→2 in
f5b00c686).

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
restart. Then run the M2 release validation: four players complete one
integrated session from a clean reset state without GM repair. **This is the
first integrated playable release; the server becomes recognizably classic
ArcheAge here.**

**Depends on:** M3b (property persistence), M2's reset/seed procedure
validated by the third-party check, **A2 (broadcast economics)** — vehicle +
pack + group movement is where allocation churn first becomes visible. The
convoy-traffic confirmation is covered by the A2 gate at M4 entry
(t_921a7be5, Rei ACCEPT, merged f9572e1a8 — ancestor of develop): mechanism
verified (allocation-free short-circuit, wake-storm scans budgeted) and the
broadcast allocation profile is unit-verified flat. Convoy-volume
measurement is a soak-scale item owned by the M6 soak lane. **Feeds:** M8
(hauler/trader bots), M9 (trade economy).

**Detail (2026-08-09 audit):** persistence criteria are per-object-type, not
one "repeats after restart" line: Slave/vehicle attachment, pack maturation
timers, cargo ownership each get a restart assertion in the automated
scenario. The four-player gate gets an automated fallback: the full
integrated session driven by scripted actors on real engine paths
(M5-stand-in rule); the M5.1 contract-vocabulary evidence remains open
for the M5.1 lane — humans confirm feel, not function. "Clean reset
state" chains to the M2 deliverable by name.

**M4 EXIT RECORD (2026-08-12, t_97e59ffc — Rei gate):** integrated playable
release delivered on `release/m4-exit` (merged slices: fix/m4a-crafting-
integrity f28b93fc1, fix/m4-2-trade-packs e4af04a49, fix/m4-3-vehicle-
lifecycle 2907f46ff; one conflict resolution in CharacterCraft.cs keeping
both the bag-scope material check and the level-10 pack gate). Exit evidence
— merged-tree provenance: the E2E-leg binaries were re-published from the
exact merge commit by the M4 audit (t_abe87eaf, E2E_REBUILD=1); the original
E2E binaries predated the merge (04:40 PDT vs merge 07:17 PDT), so the
first-run E2E legs sat on the pre-merge release-branch tree — identical
except the CharacterCraft conflict resolution + docs:
- **Full unit gate 1778 total / 0 failed / 1 pre-existing skip** (Release,
  real clone, compact.sqlite3 present).
- **M4ExitIntegratedSessionTests** (new, 4 scripted actors = the M2 release-
  validation group, M5-stand-in rule): one session drives group harvest
  (potato 2259 real growth + Doodad.Use harvest chain, 2-4× 7992 + 1× 19887
  per crop) → craft pack 26489 via REAL CharacterCraft.Craft → CraftEffect →
  EndCraft (level-9 negative: LevelLowToUse; materials consumed before grant;
  pack to Backpack slot) → load onto slave cargo (801 despawn gate negative)
  → 3-leg travel route → unload → sell at Solzreed gold trader (base
  floor(14500×4913/1000)+20000 = 91238; payout round(91238×130%×1.05) =
  124540; labor −60; pack consumed; same-zone StoreCantSellSameZone negative
  with pack retained) → repeat second pack, 2× 124540 mails.
- **Per-object-type restart E2E on the real stack (MySQL+Login+Game):**
  M4_2TradePackRestartE2eTests (placed-pack plant_time + made_unit_id survive
  kill -9) PASS 2m12s; M4VehiclesE2eTests (slave row intact, exactly 1 row,
  TWO kill -9 restarts) PASS 3m09s; M3bExitPersistenceE2eTests (crop/house
  rows × 3 crash cycles incl. kill -9 mid-save and container kill) PASS 7m03s.
  Merged-tree re-run (audit t_abe87eaf): M4Vehicles 1/1, M4_2 1/1, M2b 5/5
  (clean rerun; first chain 4/5 on the documented MySQL bring-up stream
  flake); M3b Cycle-1 PASS every attempt, Cycle-2 observation-race miss on a
  loaded host — not a regression (M4 diff touches zero save code), tracked
  at t_1329a833; the 7m03s PASS above stands as this tree's own evidence.
- **Scorecard:** CRAFT-01, PACK-01, SLAVE-01 → C/W/A/R=2; **H = UNKNOWN
  (proxy/bot-functional only — the exit session was driven by 4 scripted
  actors under the M5-stand-in rule; humans confirm feel, not function)**.
  M4 economic/navigation replay + the original M2 human baseline are
  explicit deferred gates (below).
- **A2 convoy-traffic confirmation:** covered by the A2 gate at M4 entry
  (t_921a7be5, Rei ACCEPT, merged f9572e1a8 — ancestor of develop) — the
  short-circuit mechanism is verified (RegionBroadcastAllocationTests 9/9:
  allocation-free region iteration + character short-circuit, GC delta
  < 1KB per 100k scans; PopulationDirectorTests 26/26: wake-storm scans
  ≤ O(cap)) and the broadcast allocation profile is unit-verified flat.
  Live convoy volume (bots + vehicle convoy under broadcast load) is a
  soak-scale measurement that belongs to the M6 soak lane (numeric budgets
  + staged gate: 1 bot/30 min → 10 bots/1 h → 10 bots/6 h).
- Human playtest of the integrated release (the M2 "feel" leg) remains the
  deployment-lane follow-up once Josh GO's the release merge — **recorded as
  an explicit deferred gate (M2 original human baseline + M4 human route;
  bot-backtrack program, see deferred gates below).**

---

## M5 — Gameplay Actor Contract

**NOT autonomous bots — the contract first.** This is normalization, not
invention: wrap existing capabilities behind ONE additive, inspectable
contract. Size this milestone after a short architecture spike proves the
execution/threading boundary and one vertical action; existing primitives are
reusable, but their packet/session coupling is the main uncertainty.

**Existing primitives to wrap:**
- NPC AI movement (NpcAi)
- target selection
- skill execution
- interaction
- inventory/game services
- administrator commands for diagnostics and test setup only; never as a
  production gameplay-action implementation
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
- a single execution boundary for world/character mutation; controllers may
  enqueue requests but may not mutate a Character concurrently
- idempotency/correlation rules so retries and timeouts cannot duplicate
  items, currency, labor consumption, quest credit, or interactions

**Explicit NON-GOALS:** no autonomous planning · no LLM integration · no
generalized navigation rewrite · no core gameplay interface replacement ·
no bot-only inventory or combat behavior.

**Action surface tiers (contract defines the FULL vocabulary; implementations land in slices):**
- **M5 required actions:** Observe · Move · Stop · Target · Cast · Interact ·
  Loot · UseItem · Mount/Dismount · AcceptQuest · TurnInQuest
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

**Exit tests:**
- **M5 core:** a scripted actor completes the curated quest/combat/mount
  segment and produces a machine-readable trace showing every request,
  transition, result, and failure.
- **M5.1 economy:** a scripted actor completes the curated farm/craft/pack/
  vehicle/trade segment through the economic actions.
- Actor contract tests (server executes/observes a command correctly) pass
  independent of any controller; retry tests prove non-idempotent actions do
  not execute twice.

**Depends on:** G0 merge discipline (the contract branch
`origin/feat/m5-actor-contract` is unmerged today). The architecture spike
(threading boundary + one vertical action) is a recorded gate, not a
suggestion — M6 proceeded without it; that violation is now on the record.
**Feeds:** M6 exit, M7, M4's automated fallback, M8's auditable economy.

**Detail (2026-08-09 audit):** the detailed work list is **B1** (core action
surface: Interact · Loot · UseItem · Mount/Dismount · AcceptQuest ·
TurnInQuest — each through the real engine path) and **B2** (M5.1 economic
actions). New exit test, non-negotiable: **threading-boundary verification** —
a debug thread-affinity assertion proves zero Character/world mutation off
the single execution boundary. Trace-based exit tests alone do NOT satisfy
this: the current bot layer (8 unsynchronized worker threads,
PlayerBotScheduler.cs:84) would pass the trace tests while violating the
rule at L332-333. **A1 (marshal bot steps onto the game loop) is the
retroactive fix and is M6-exit-blocking.**

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

- **6.6 Player parity — REQUIRED for client-visible bots (2026-08-09
  findings: presence demo surfaced 5 hotfixes + parity audit `t_98415169`):**
  - **Appearance:** bots must carry race/gender-canonical `unit_model_params`
    (231B type=Face blob, correct model id) AND `VisualOptions`; provisioning
    heals degenerate rows (1B → 231B). Wrong model id / empty params /
    null VisualOptions = invisible body or packet NRE (`SCUnitStatePacket`).
  - **Equipment:** bots must be seeded with a real equipped item set —
    zero equipped items = invisible body despite valid model params.
  - **Skills + actabilities:** seed real skill rows + actability set (parity
    audit: bots have 0 skills / 0 actabilities vs human 34).
  - **Bag supplies:** starting consumables/currency for long-horizon tests.
  - **Wire surface:** SCUnitStatePacket + equipment + faction + VisualOptions
    must serialize without NRE for every bot (null-safe writes mandatory).
  - **Factory:** `BotAppearanceFactory` (randomized player-like appearance,
    per-race/class starting equipment, deterministic seeds) is the durable
    generation path; demo clones (Asssaa-replicate) are stopgaps only.
  - **Acceptance for any "bots visible" claim:** real client in same zone
    receives unit-state + movement frames AND renders distinct bodies.

**Exit test:** 10 bots run 6 hours with no unrecovered loops, no inventory
duplication, no runaway combat, no DB corruption, no tick-budget overrun.
Playerbot behavior tests (controller chooses the right command sequence)
run against the M5 actor contract — a failed bot harvest must resolve to
one of: wrong choice / navigation / action rejected / state transition /
persistence.

Before the six-hour soak, record a no-bot baseline and approve numeric budgets
for p95/p99 world-tick time, memory, database writes, action-queue backlog, and
recovery rate. Gate in stages: one bot for 30 minutes → 10 bots for one hour →
10 bots for six hours. A qualitative "no overrun" is not sufficient evidence.

**Depends on:** M5 (violated in practice — recorded; exit evidence must be
re-run against the merged contract) and **t_0fda3cd3** (merge M6 chain +
record prod SHA). **Feeds:** M7, M8, and the G2 scale ladder.

**Status reconciliation (2026-08-09):** 3 citizens on prod; 3-minute 10/25-bot
gates PASS (these substitute for the stated 1-bot/30-min and 10-bot/1h stages
— substitution recorded here, or re-run the stated stages); 6h soak attempt 1
crashed at 19 min with no RCA — **soak-failure semantics now defined: any
crash = automatic fail + RCA card + the 6h clock restarts only after the fix
lands.** The staged-ladder contradiction is resolved in favor of the Resolved
Decisions ladder: 10 correctness → 25 village → 50 soak → 100 only after
profiling. M6.6 shipped consumables without currency — the exception is
recorded here; either Josh approves it standing or a follow-up card seeds
currency.

**Physics slow-thread budget recalibrated (2026-08-10/11, t_18fccd09):** the gate's
physics-warning budget is now ≤0.1/min + a no-sustained-slow clause (≤30
warnings on the SAME world within any 60s window; 31+ = hard fail). Rationale:
the detector (upstream stock, PR #1253) measures wall-clock inter-iteration
gap on a thread that sleeps ~40ms and steps a zero-body world, so boot GC +
host scheduling jitter trip it regardless of physics load. Measured 0.031/min
(11 warnings / 6h soak, pre-GC-fix) and 0.067/min (4 warnings / 60 min,
post-fix, one 3s background-GC burst) with 0 crash/disconnect/region-overrun
— 0.1 gives ~1.5-3.3× headroom while a real overload (10-100× the measured
rate) still fails. Same-world ceilings measured on the 6h re-soaks: 3-in-8s
(2026-08-10, one pause event) and 8-in-59s per world / 16-in-76s across
worlds (2026-08-11 360-min re-soak, one 75s provisioning/GC storm, all ≤82ms,
thread recovered, 5h50m clean after) → clause at 30 ≈ 3.75× headroom; a
genuinely stuck physics thread logs consecutive-iteration warnings (~25/s)
and trips within ~1.2s. The 360-min re-soak additionally showed the STRICT H2
load budgets (100ms region ceiling, 0 tick-overrun tolerance) false-RED on
the SAME storm (one 105ms region pass, 2 overrun warnings, deferred 0
characters) — the 10-bot idle soak now uses stage-specific SoakBudgets
(region ceiling 200ms ≈ 1.9×, tick-overrun rate 0.1/min ≈ 18×) while the
25/50-bot LOAD stages keep the strict budgets. In-code rationale mirrors the
DB-write budget precedent (t_2006451f / t_b4eb35e9). Fix attempt first (GC
latency tuning, t_eecc5604, merged) per Josh's ruling; recalibration is the
recorded fallback.

**M6 EXIT RECORD (2026-08-11, t_35167e60; H/exit-label reconciled 2026-08-12):** the last M6-exit blocker — the
session/item enumeration-race class — is CLOSED and the 6h soak is GREEN.
Merge `eb6f637e0` (no-ff) landed the 4-commit chain from t_781cdb32 +
t_3fdd6ac3 (concurrency-safe Server `_sessions`/StreamManager tokens,
CharacterQuests ActiveQuests/CompletedQuests → ConcurrentDictionary,
ItemContainer `_itemsLock` + GetItemsSnapshot, ItemManager `_allItems`/
`_allPersistentContainers` → ConcurrentDictionary, rigs adapted). Merge
resolution preserved develop's BUG-014 uint CompletedQuests keys and the
t_90c0d0d1 null-entry Save guard (7 develop-side rigs seeded after the chain
parked were adapted to ConcurrentDictionary in the same merge). Full gate:
**1592 tests, 0 failed.** Soak #4 (10 bots, 360.0-min window, isolated soak2
stack, merged runtime hash-verified): **ALL 9 budgets PASS, 0 failures** —
the soak3 quest-4295 reward-distribution NRE (ApplyBindRules raw-list
iteration) does NOT reproduce; the Failures section is empty. Exit test (10
bots / 6h / no unrecovered loops / no inventory duplication / no DB
corruption / no tick-budget overrun) is now satisfied by recorded evidence;
remaining M6-gate items per the 2026-08-09 audit (A1 execution boundary,
restart-persistence scenario, observability logging, merge-to-develop G0-1)
are tracked separately. **Exit-label note (bot-backtrack program):** the
6h soak verdict stands as "passed revised approved budgets" — the original
zero-warning threshold was not met (physics-warning budget 0.03/min vs 0,
t_eecc5604) and A1 landed AFTER the soak (merged 761d1e81a); the full M6
exit label is NOT claimed — the **B4 restart-persistence scenario (bot
identity/inventory/position/schedule survive restart) is an explicit
deferred gate** (below).

**Detail (2026-08-09 audit):** M6 exit is blocked on **A1** (execution
boundary — bot steps off the game loop violate M5's core rule). Added exit
requirements: (a) restart-persistence scenario per the standing rule —
bots survive server restart with identity/inventory/position/schedule intact
(B4 store); (b) observability — the silent catches in BotAppearanceFactory
(:212/:225) and BotE2EBridgeBootstrap (:32-35) must log before any 25+-bot
gate; a silently gearless or bridge-dead bot population is a failed gate;
(c) merge-to-develop is a closing condition (G0-1).

---

## Deferred validation gates (bot-backtrack program, 2026-08-12)

Josh's directive: prior human-test waivers are **authorized sequencing, not
misconduct**. The engineering evidence above stands untouched; these gates
are deferred validation, explicitly tracked. Bots prove function; Josh
proves feel. Replayed via normal gameplay services + auditable traces
(Phases 1-3, dependency-blocked on M5/M5.1 acceptance cards).

| # | Deferred gate | Original evidence | Replay/validation plan |
|---|---|---|---|
| 1 | **M1 human route** (Solzreed walk, Open Decision #1) | Automated + restart evidence CLOSED M1; human verdict OPEN | Phase 1: golden route replayed through the M5 actor contract (real request lifecycle, normal services, machine-readable traces); Josh's feel verdict batched with other gates |
| 2 | **Original M2 human baseline** (two players, no GM repair) | Amended census/reset scope COMPLETE (G1 2026-08-10); human baseline open | Phase 1: contract-level replay of the curated route from reproducible reset; Josh's two-player baseline remains Josh-owned (bots may stand in for the AUTOMATED baseline only — never H=2) |
| 3 | **M3a contract replay** (housing/farming through contract actions) | M3a closed on scripted-actor proxy evidence (in-memory actors, reflection, GM inventory, direct service calls — predates A1/B1) | Phase 2: replay via M5.1 contract actions (Plant/Harvest/Craft/PackPickup/PutDown) on a real server, real engine paths, no direct DB/reflection/GM repair |
| 4 | **M4 economic/navigation replay** (farm → craft → pack → load → navigate → unload → sell → reward) | M4 closed on M4ExitIntegratedSessionTests (4 scripted actors; integrated rig assigns zones/transforms directly, manually attaches cargo) | Phase 2: replay via M5.1 contract actions; navigation/travel from normal movement/vehicle controls — direct Transform/ZoneId assignment FAILS the gate; preserve + rerun process-level restart E2Es; Rei verifies traces + conservation |
| 5 | **M6 B4 restart scenario** (bot identity/inventory/position/schedule survive restart) | 6h soak PASSED under revised approved budgets; A1 landed after soak; B4 metadata store not yet built | Phase 3: A1/B1 verified on merged develop; B4 metadata persistence implemented; bot-world restart test (2 checkpoints); soak verdict stays "passed revised approved budgets" |

**SCORECARD H rule (reconciled):** H = actual player only. Scripted-actor /
bot evidence is proxy/bot-functional (A dimension) and is NEVER recorded as
H=2. H stays UNKNOWN until Josh runs the curated scenario.

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

**Depends on:** M6 exit (including A1) and B1 (combat/quest actor actions).
**M7 is the largest unestimated chunk on the roadmap** — there is no combat
AI today beyond Cast/SetTarget — so a scoped spike (one adventurer clearing
a short quest chain end-to-end) gates scheduling. **Feeds:** M8 (3 of the 8
villagers are adventurers), M9 convoys/pirates.

**Detail (2026-08-09 audit):** select the "selected group encounter" at spike
time and record it; per-feature acceptance for roles, avoid-extra-pulls,
death recovery, and mount-together (each demonstrable via M5 trace); the
one-human gate may use the M5-stand-in rule but Josh confirms feel; add a
restart-persistence scenario (party mid-route → restart → resume) per the
standing rule.

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

**Depends on (previously unstated, now explicit):** M3b + M4 (farmers/
crafters/haulers require housing, farming, crafting, pack, and vehicle
integrity), M7 (adventurers), **A5 true dormancy + gate G1** (scale
budgets), **B3 goal arbitration** (behavior modules), **B4 metadata
persistence** (schedules/homes/professions survive restart).
**Feeds:** M8.5, M9.

**Detail (2026-08-09 audit):** the detailed work list is **C1–C5** (G4):
C1 schedules v1, C2 social v1 (= 8.5, deduplicated with M8.5a), C3 farmer
v1, C4 hauler/trader v1 (crafter follows B2), C5 village integration.
"Auditable economy" is now defined: the M5 audit trail flushed per B4, with
an economy-ledger reconciliation assertion in the 2-checkpoint bot-world
restart test. "Multiple restarts" = ≥3, with state-fidelity criteria
(schedule phase, home, profession, inventory, ledger) asserted after each.
The exit test runs at 25 embodied concurrent within G1's numeric budgets.

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

Treat the `AAEmu.Bot.*` names as capability boundaries first, not a mandate to
create many assemblies immediately. Begin inside the existing Game project
where access to normal gameplay services is required; split projects only
when a stable API, independent test boundary, or deployment boundary justifies
the dependency cost.

---

## M8.5 — Social services & grounded guide (post-village, pre-activities)

- **8.5a Lightweight social (pre-LLM):** greetings, task acks, status
  messages, contextual canned dialogue, party callouts, trade-route warnings
- **8.5b External LLM bridge (async):** personality + memory + ambient
  chatter via queue — homelab bridge (gestalt/openclaw), never blocking
- **8.5c Grounded guide:** `.guide` commands querying live character state,
  quest/recipe/NPC/housing tables, server-known-issue awareness

**Depends on:** M8 (8.5a is the same work as C2 social v1 — deduplicated;
it lands there), B3 module system, and C2's chat-emission spike (the
legitimate service path — no packet fabrication). **Feeds:** M9 rumors.

**Detail (2026-08-09 audit):** 8.5b must stay async/queue-based — an LLM
call may never sit in a gameplay path (tick or step); degradation rule:
bridge down ⇒ bots fall back to canned lines, never silence-critical
behavior. 8.5c queries live state only through the M5 observation snapshot,
not DB reads. **Exit test (new):** village social layer active ≥ 1 full
day — ≥ 3 distinct contextual greetings per visitor, cooldown metrics show
zero spam, and the server passes all M8 scenarios with the social module
disabled (config flag proves isolation).

---

## M9 — Emergent world systems (the "world simulator" layer)

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

**Depends on:** M8 (working economy + schedules + ledger), B4 (traits/
personality persistence), M8.5 (rumor narration surface). Emergence is a
product of incentives + traits + propagation — it is not buildable directly.

**Detail (2026-08-09 audit):** M9 had no gates at all; until each system
below has an exit test it is a **vision section, not a milestone**. Required
substrate work before any M9 credit: (a) **needs/incentives layer** — the
reason a bot farms or steals (food/gold/lumber demand signals), which today
does not exist in any form; (b) **crime/justice substrate** — SCORECARD
shows CRIME-01/TRIAL-01 as W=1 stubs and PRISON-01 with no PrisonManager at
all; card the engine substrate first; (c) **rumor propagation store** —
event → witness → hearsay graph with imperfect information, persisted per
B4. Per-system exit tests follow the standing three-scenario rule; each
must demonstrate propagation evidence (system A's output observably changes
system B's behavior), not just the mechanic existing.

---

## M9.5 — Activities and world events

Contests fit here — they now have actual residents to participate.

Candidates: fishing contest, race-track time trials, scheduled trade
caravans, bot-organized fishing trips, regional monster hunts, community
construction events, simple festival schedule. Music/FX wiring → Lane C.

**Depends on:** M8 (residents exist), B3 module system (an activity is a
module). **Feeds:** M10 (organized groups), retention texture.

**Detail (2026-08-09 audit):** "Candidates" is a wish list, not a milestone —
**the fishing contest is the locked launch activity**; everything else stays
a candidate until it has a card. **Exit test:** one scheduled event runs
start → finish with bot participants and at least one human (or M5 stand-in),
auditable results (entries, scores, winner, rewards settled in the ledger),
and the world outside the event keeps running within gate G1 budgets.

---

## M10 — Territory and siege (deferred, two slices)

Only after guilds work, combat is stable, bots form groups, and the economy
generates something worth controlling.

- **Slice 1 (no combat):** one castle, one owner, declaration window,
  persistent ownership, tax state, monument interaction, admin recovery
- **Slice 2 (combat-lite):** attacker/defender registration, bot squads,
  objectives, structure health, victory state, reward settlement

**Depends on:** M9 (an economy worth controlling), M7 (stable group combat),
guild substrate (Lane B/FC — see feature-completeness track). **Feeds:**
endgame loop.

**Detail (2026-08-09 audit):** the prose preconditions are replaced with
measurable ones — "guilds work" = guild create/invite/rank/bank scenarios
pass; "combat stable" = M7 exit passed; "bots form groups" = party module in
production; "economy worth controlling" = M9 trade ledger shows inter-village
trade volume > 0 for 7 consecutive days. **Slice 1 exit test:** ownership +
tax state survive ≥3 restarts with zero loss/duplication (persistence is the
entire point of Slice 1 — evidence = restart scenario, not prose).
**Slice 2 exit test:** a scripted siege with bot squads completes with
victory state + reward settlement reconciled in the audit trail.

---

## Work lanes (permanent, parallel)

- **Lane A — Vision-critical milestones:** the roadmap above (M1-M10)
- **Lane B — Upstream intake & correctness maintenance:** upstream syncs into
  the fork, new regressions, security/duplication bugs, persistence corruption,
  and broad engine defects. No outbound upstream PR preparation or push.
- **Lane C — Quick wins:** music wiring, premium labor data, FX groups,
  small packet completions, low-risk data imports. Completed between larger
  tasks; never delays the golden path.
- **Lane D — 1.2 feature completeness (parallel with playerbots; locked
  2026-08-09):** bring every player-facing 1.2 mechanic to its required
  evidence grade while the bot track runs. **Scope:** the SCORECARD global
  mechanic ledger (PROG, CTRL, COMBAT, ABILITY, ITEM, LABOR, MATE, HOUSING,
  FARM, PROPERTY, CRAFT, PACK, SLAVE, TRADE, MERCHANT, AUCTION, ECON, MAIL,
  TRANSFER, INDUN, FISH, PVP, DUEL, CRIME, TRIAL, PRISON, PARTY, EXPEDITION,
  CHAT, ZONE) — currently ~28 mechanics, nearly all `U` (unassessed).
  **Exclusions (Josh):** cash shop / marketplace / credits, monetization
  systems, post-1.2 expansion content.
  - **First deliverable:** a C-dimension (canonical) audit sweep of every
    ledger row — identify intended 1.2 behavior + required data per mechanic,
    so the lane has a real, sized backlog instead of 28 `U`s.
  - **Order:** ① golden-path-adjacent breadth (PROG/CTRL/ITEM/MERCHANT/MAIL/
    AUCTION/TRADE/PARTY/CHAT + the curated-scope remainder of LABOR/CRAFT/
    PACK/SLAVE — the milestones own the curated scenario, Lane D owns the
    breadth beyond it); ② world systems (ZONE war/peace transitions, PVP,
    DUEL, INDUN, TRANSFER, FISH); ③ **M9/M10 substrate early as spikes** —
    CRIME/TRIAL/PRISON/EXPEDITION gate M9 and M10, so their C/W audits start
    now even though the milestones are late.
  - **Rules:** grades promote only with linked evidence (C/W/H/A/R/S per the
    mechanic model); **bots/scripted actors may stand in for the A (functional)
    dimension once M5 lands — H remains actual-player-only and is NEVER
    recorded as H=2 (proxy/bot-functional evidence only; H UNKNOWN until Josh
    runs it)**; nothing
    lands unmerged (G0-1); Lane D never breaks an active Lane A gate —
    shared engine fixes route through Lane B; upstream-sourced fixes follow
    the upstream alignment rules.
  - **Done when:** zero `U` rows remain and every mechanic sits at the grade
    its consuming milestone requires (weakest-dimension-wins).

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
separate measurements — update both. Experience percentages remain
directional until each row links to a versioned checklist with a defined
denominator; milestone gates are scenario evidence, not subjective percentage
movement.

## Timeline (directional, 3-5 sessions/week)

| Period | Target |
|--------|--------|
| Weeks 1-2 | M1 quest and progression spine |
| Week 3 | M2 golden-path specification + baseline |
| Week 4 | M3a homestead shell |
| Weeks 5-6 | M3b persistence and recovery |
| Weeks 7-8 | M4 trade and transport + integrated playable release gate |
| After M4 spike | M5 gameplay actor contract (re-estimate after one vertical action) |
| After M5 | M6 deterministic bot framework (staged soak gates) |
| After M6 | M7 adventurer and party bots |
| After M7 | M8 Living Village |
| Later | M9 emergent systems, M9.5 activities, M10 territory/siege |

M3b and M4 are the highest-variance items — persistence bugs have
nonlinear scope (object identity, save ordering, parent/child restoration,
phase-state serialization, duplicate loading, schema deficiencies).

Dates are directional through M4 only. Reforecast M5+ from measured discovery,
not the original calendar; packet/session coupling, persistence, navigation,
and vehicle attachment may reveal deeper work.

## Definition of done per milestone

- [ ] Human scenario, automated scenario, AND restart-persistence scenario
      defined and passing
- [ ] Behavioral changes: branch, commits, proportionate tests,
      fail-before/pass-after evidence where a regression can be reproduced,
      and Rei signoff
- [ ] Fast local gate green: Release build + compiler-check + unit tests
- [ ] CI-parity gate green before merge: coverage-enabled unit tests + Login
      integration tests; run the Game integration suite when the affected
      subsystem or milestone scenario requires it
- [ ] Both scorecards updated in-branch (technical wiring + experience)
- [ ] STATUS.md reflects the milestone (Nei)
- [ ] Milestone release candidate deployed to the AAEmu box by Mai and
      sanity-checked in-game (individual tasks pass the local gate first —
      production churn only at milestone / release-candidate boundaries)
- [ ] No branch push or PR to upstream; upstream flow is intake-only

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
- **Rollback:** preserve branch history: `git switch --detach <previous-sha>`
  and rebuild the affected services. Return to `develop` only for a later
  forward deployment. DB-changing releases follow the migration-specific
  restore/rollback plan recorded before deployment; a code rollback alone is
  not assumed to reverse schema or data changes.
- **Bot audit trail** (M5+): structured trace records support debugging AND
  economic auditing — see M5.

## Gap audit 2026-08-09 (Kimi deep-dive — detailed work breakdowns)

Full audit evidence on card t_0fda3cd3. This section is the detailed "what
should be done" layer the milestones lacked. Three tracks: discipline fixes,
quest coverage to 100%, and the scale/behavior ladder to Living Village.

### G0 — Discipline fixes (precondition for milestone credit)

Root cause of the 2026-08 pile-ups: the roadmap regulates merges and deploys
as *procedures* but never as *gates*. New standing rules:

1. **No milestone credit until merged.** Claimed status requires evidence on
   `develop`. (Today: entire M6 chain 47 commits unmerged; M2c band 21-30
   sweep merged to local develop but unpushed; M5 contract branch unmerged.)
2. **No deployment except from a recorded, manifest-attested SHA** — manifest
   extended with Docker image digest + compose overlays in use. (Today: prod
   runs image ebe809a0ec5b built from a deleted context; production.json
   claims b1e2231c — false.)
3. **Hotfix lane:** mid-milestone deploys are allowed only as governed
   exceptions — carded, SHA recorded, manifest updated, merged within 48h.
4. **Failed gate ⇒ RCA card + retrial rule** (6h soak attempt 1 crashed at
   19 min, no RCA filed; the 6h clock restarts only after RCA lands).
5. **Evidence contracts:** PASS/SKIP/FAIL definitions; deletion is never
   "fix"; drops require register entry + Josh decision (already the norm —
   now it's written down).
6. Merge-first execution: t_0fda3cd3 (merge M6 chain + record prod SHA)
   gates all new feature work.

### G1 — Quest coverage to 100% ✅ GATE PASSED 2026-08-10 (4,579 live contexts; 4,573 PASS / 0 FAIL / 14 documented SKIP = 100.0% PASS-or-doc-SKIP, zero unexplained)

No engine gap blocked any live context — 4 harness family closures + band
sweeps + data triage all landed 2026-08-05 → 08-10. Final census on merged
develop @ 7f5c179f7: 4,573/4,573 runnable; full gate 1495 total / 0 failed /
1 env-gated skip (bar ≥1473 MET).

- ✅ WI-1 Push/merge M2c census to origin/develop (local aa2ef5f6d) — merged.
- ✅ WI-2 CrimePoint closure (7 ctxs) — closed the last 2 census SKIPs
  (2916/2926); added the t9 tier.
- ✅ WI-3 AbilityLevel closure (11) — rig preseeds ability exp; AbilityId=0
  all-abilities branch covered.
- ✅ WI-4 MateLevel closure (6) — SummonMate preseed + Cleanup-consume path
  (5464) covered.
- ✅ WI-5 CompleteQuest closure (11) — synthetic-block pattern from
  CharacterQuestsDailyResetTests.
- ✅ WI-6 Band 41-50 ltd triage — Josh rulings 2026-08-09: 6069 DROP
  (executed t_6810ebd4, merged t_ec1a3326); 3419/4967 NO-GO keep
  (register §8).
- ✅ WI-7 T9 sweep band 31-40 (t_eb2556c3) — 643/643 PASS, zero harness
  SKIPs as predicted.
- ✅ WI-8 T10 sweep band 41-50 (t_fc85a317) — 1,589 PASS / 2 doc-SKIP
  (3419/4967) / 0 FAIL.
- ✅ WI-9 T11 sweep band 51-55 (t_867af9e4) + lvl-99 straggler 3465 —
  269/269 PASS-or-doc-SKIP.
- ✅ WI-10 Driver fidelity stages (t_abafd918 @ 9f785d430) — TIMEOUT /
  RESET / GUARD_DIED, 642 probe stages, 0 new FAIL.
- ✅ WI-11 Band 0/null — WI-11a triage (t_724ccab2; Josh Q2 ruling: 4
  no-components NO-GO keep) + WI-11b sweep (t_8ec705f0 @ e4dcc22c7):
  60/60 accounted, 56 PASS / 4 doc-SKIP; BUG-014 found → fixed
  (t_8b47a3bf @ 4b73b63ac, Rei gate PASS).
- ✅ WI-12 Final census + denominator reconciliation (t_971d275b @
  7f5c179f7) — **G1 GATE PASS**: 4,876 rows − 297 registered drops = 4,579
  live = 4,573 PASS + 6 doc-SKIP, zero unexplained; gate 1495/0/1.

**Deferred / kept-by-ruling (documented SKIP — not actionable without Josh
overturning):** 6 live contexts — 3419/4967 (ltd no-report, WI-6 register §8)
and 315/1576/1728/2046 (no-components, WI-11a A2) — plus 8 orphaned contexts
with no quest_contexts row (745/1421/1954–1958/2140, register §3,
census-SKIP).

Purged-content policy (settled by audit): the 26 engine-stuck (zone 22
A-cluster) have no completion path in canonical 1.2 data — restoring them is
content authoring, not engine work, and is out of scope unless a future
"rebuild old Sunny Wilderness" content milestone wants the A1 dialogue seed
(27 chat_bubbles preserved in register §6). The 91 shells: never restore.

### G2 — Scale ladder (order of failure: threading → broadcast GC →
density lock/scheduler ceiling → autosave wall → dormancy/fan-out/memory)

- A1 (L) Execution boundary: bot steps marshalled onto the game-loop thread;
  scheduler stays a pure wake producer. Acceptance: thread-affinity assert
  proves zero Transform writes off the tick thread; 25-bot 6h soak, tick
  invoke p95 < 50ms, zero position-tear in wire capture.
- A2 (S) Broadcast economics: humans-nearby short-circuit + allocation-free
  GetAround overload (WorldManager.cs:1113) + kill Region.GetList array
  copies on the hot path. Acceptance: 100 bots 0 humans ⇒ zero bot-originated
  packets; gen0 GC < 1/min.
- A3 (M) PopulationDirector O(1): incremental per-zone/per-activity counters;
  RefreshPressure on a 5s timer (never called today); human-proximity wake
  trigger. Acceptance: 1,000-bot wake storm transition p99 < 100ms.
- A4 (M) Save scalability: per-character dirty tracking + batching.
  Acceptance: autosave p95 < 2s at 250 characters; zero _isSaving skips.
  ✅ implementation merged 5ed5d6493 (2026-08-10, t_8c18eb1c, Rei gate
  t_53025996 ACCEPT — dirty-only periodic saves, force-all on shutdown + /save);
  acceptance measurement still a milestone-gate item.
- A5 (L) TRUE DORMANCY — the pivotal item: Dormant = DB row + metadata only,
  no Character materialized, no region presence, no per-second tick; Tier 3 =
  DB-driven scheduled simulation (harvest/travel timers advance while nobody
  is embodied). Acceptance: 1,000 registered / ≤50 embodied, RSS within 15%
  of the 50-only baseline; wake-to-visible p95 < 3s; dormant timers advance
  over 6h.
- A6 (M) Manifest-driven mass provisioning (citizen manifest as data;
  replaces hardcoded CitizenNN + 10-bot clamp). Acceptance: cold boot →
  100 citizens on schedule < 60s.
- Gate G1: 50-bot 6h soak with numeric budgets → 100 profiling → 250 staged.

### G3 — Behavior foundation

- B1 (M) Complete M5 actor surface: Interact, Loot, UseItem, Mount/Dismount,
  AcceptQuest, TurnInQuest — real engine paths, retry/idempotency tests.
- B2 (M) M5.1 economic actions: Plant, Harvest, Craft, PackPickup/PutDown,
  BoardVehicle, Buy/Sell, Deposit/Withdraw; exit = scripted farm→craft→pack→
  sell loop with full trace.
- B3 (M) Goal arbitration + module contract (IBotActivityModule): re-implement
  roam as a module with zero scheduler changes; delete/absorb the dead
  PlayerBotBehaviorController stack; new module = one file + one config line.
- B4 (S-M) playerbot_metadata store (personality, schedule, profession, home,
  planner state — the M6.0 list that has no table today) + audit-trace flush;
  2-checkpoint bot-world restart test.

### G4 — Living Village content (M8)

- C1 (M) Schedules v1: game-time clock, home/work/social/rest anchors.
- C2 (S-M) Social v1 (pre-LLM): chat emission path, proximity greet, canned
  contextual lines, cooldowns. (Small spike: legitimate chat service path.)
- C3 (M) Farmer v1 (real plot, harvest→deposit→replant, shortage report).
- C4 (M) Hauler/trader v1 (pack→route→sell→proceeds); crafter follows B2.
- C5 (M) Village integration + M8 exit: 2 farmers/1 crafter/2 haulers/
  3 adventurers, full day, multiple restarts, auditable economy, 25 embodied.
- M7 caveat: adventurer/party bots are the largest unestimated chunk (no
  combat AI beyond Cast/SetTarget today) — spike before scheduling Phase C.

### Roadmap self-fixes

- M2: record that it was redefined into the census sweep; adopt the board's
  ≥95% gates + band tables; add "merged to develop" as closing condition.
- M6: resolve the staged-ladder contradiction (L456-457 1→10→10 vs L658
  10→25→50→100); record the 3-min gate substitution or re-run the stated
  stages; add soak-failure semantics (any crash = RCA + clock restart).
- M3b/M4: add SaveManager dependency (A4) + quantified crash-injection
  method; M8: state dependencies on M3/M4 and on A5/G1.
- M9/M9.5/M10: add exit tests per the standing three-scenario rule or mark
  explicitly as vision sections.
- Timeline: rebaseline around the real order (M1 → M2 census → M6 framework →
  G0/G2 hardening → M3/M4 → M7 spike → M8) or enforce the written order.
- Human-capacity realism: M3a/M3b/M4/M7 gates assume 2-4 humans; define the
  automated fallback (M5 contract bots as stand-ins) or name the humans.
