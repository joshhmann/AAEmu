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

**Current branch record (2026-08-27 MCP expansion checkpoint):**
`develop @ 1638b007c` (= `origin/develop`). Commit `1638b007c` adds five
authenticated actor routes and matching MCP tools; the 24-tool catalog,
validation, benchmark, and deferred action boundary are recorded below.
Milestone shape and historical evidence are unchanged.

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

## Milestone Requirements & DoD — THE STANDARD (retrofit 2026-08-14, Codex audit finding)

**Origin (Josh directive 2026-08-14):** milestones were closed on "got
something working" evidence, not on true-to-requirements evidence. Every
milestone therefore carries an explicit, uniform block: **Requirements**,
**DoD (evidence classes)**, **Non-goals**, and requirement-indexed **Exit
tests**. A closure claim that cannot cite a requirement and its DoD evidence
is untrustworthy. (Retrofit card t_730b04bd; the AAEmu cap at M5.2 stands —
this is requirements governance, exempt from the cap by Josh ruling.
Re-grading is the Rei gate's job, t_ec7f0c19 — this section defines the
block format and the retrofitted blocks only, and changes NO milestone
status.)

### Requirements (REQ-<M>-<n>)
- **Verifiable:** an independent observer can check pass/fail (test, gate
  verdict, census, replay, registered decision).
- **Canonical-1.2-true:** the bar is ArcheAge 1.2 retail behavior (reference
  data + 1.2 mechanics), never invented behavior. Where Josh ruled a
  deviation (drop / prune / keep-by-ruling), the requirement's bar is the
  ruling itself — registered, dated, with restore pointers where relevant.
- **Independently testable:** each item carries its own evidence path; no
  requirement may borrow another requirement's test and count as covered.
- **Provenance marks (mandatory):** `(reconstructed 2026-08-14)` = recovered
  from the milestone's existing Work:/status/Detail prose — never silently
  invented. `REQUIREMENT NOT RECOVERABLE` = no source basis exists in the
  record; that mark is itself a finding — the item is UNVERIFIABLE and needs
  a Josh requirement ruling before any closure claim.

### DoD — evidence classes REQUIRED to claim closure (7-state ledger, EVIDENCE-LEDGER.md, t_547ef82d)

| Class | Ledger state(s) | Meaning |
|---|---|---|
| engine-path implementation | 1 implemented | code merged to fork develop; real engine path, normal gameplay services only (no direct DB, no bot-only resource creation) |
| bot-replay | 3 bot-replay-ready + 4 bot-replay-passed | scripted rig exists AND passes on the merged tree (M5-stand-in rule; proxy/bot-functional evidence) |
| restart-persistence | 5 restart-passed | restart/persistence scenario passed (standing 3-scenario rule) |
| soak | 6 soak-passed | duration/load soak within approved numeric budgets — required only where the milestone touches load-sensitive paths |
| human-feel | 7 human-feel-accepted | Josh's H verdict — NEVER inferred from bot/scripted evidence; H=2 only after Josh runs the feel gate |

Per-milestone DoD blocks mark each class **REQUIRED / N/A (reason) /
DEFERRED-RECORDED (Josh-owned deferred gate, card cited)**. A milestone with
any DEFERRED or UNKNOWN class may close at most **CLOSED-WITH-CAVEATS** —
never CONFIRMED-CLOSED. Ledger discipline (t_547ef82d): every transition
cites card/commit/date; earned evidence is never erased; H stays UNKNOWN
everywhere until Josh runs the feel gate.

### Non-goals
Explicit per milestone. Work outside the requirement set is out of scope and
must be filed as its own card, never absorbed into the milestone.

### Exit tests
Requirement-indexed: each exit-test bullet is tagged with the REQ ids it
exercises. An exit test that exercises no requirement is either redundant or
a sign of scope creep.

### Retrofit summary (2026-08-14, t_730b04bd)

All blocks below were reconstructed from each milestone's own record; no
status was re-graded (that is t_ec7f0c19).

| Milestone | Requirements | Reconstructed | NOT RECOVERABLE findings |
|---|---|---|---|
| M1 | REQ-M1-1..10 | 10 | 2 (completeness bar; peripheral-coverage target) |
| M2 / M2a–d | REQ-M2-1..9 + REQ-M2a/b/b-E2E/c/d | 14 | 1 (M2c/M2d per-band thresholds) |
| M3a | REQ-M3a-1..8 | 8 | 0 |
| M3b | REQ-M3b-1..12 | 12 | 0 |
| M4 | REQ-M4-1..7 | 7 | 0 |
| M5 | REQ-M5-1..15 | 15 | 0 |
| M5.1 | REQ-M5.1-1..5 | 5 | 0 (LoadPackOntoVehicle restart-assertion gap flagged) |
| M5.2 | REQ-M5.2-1..3 | 3 | 0 |

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

**Requirements (retrofit 2026-08-14):**
- **REQ-M1-1** — Shared engine defects on the golden route fixed through real
  engine paths: BUG-006 kill-acceptor (380 quests startable; 1082/1082
  tests). *(reconstructed 2026-08-14)*
- **REQ-M1-2** — `quest_act_obj_aliases` load + validate: verdict documented
  (dormant id→name dict; zero live 1.2 refs — no use_alias=1 rows, no
  QuestActObjAlias act type — no-op). *(reconstructed 2026-08-14)*
- **REQ-M1-3** — Stub-act audit: genuine stubs fixed (CheckGuard
  silent-pass, ItemGroup gather/use stall); orphaned act rows registered.
  *(reconstructed 2026-08-14)*
- **REQ-M1-4** — Quest sanity verifier (startup cross-check) with tests
  (BUG-007, 14 tests); first live census over all quests (5 ERR / 128 WARN /
  4 INFO over 4,775). *(reconstructed 2026-08-14)*
- **REQ-M1-5** — Doodad phase/interaction objectives resolve (quests
  922/3889/3447; T1 Solzreed 97/97). *(reconstructed 2026-08-14)*
- **REQ-M1-6** — Solzreed golden route selected + curated; intentionally
  excluded quests documented (Docs/wiki/Golden-Route-Solzreed.md).
  *(reconstructed 2026-08-14)*
- **REQ-M1-7** — Widened verifier data-defect backlog closed per registered
  decision: 776/777 FIXED (in-memory overlay, t_d8a8c798) · 2145→2146 PRUNED
  (t_60a559ab) · QUEST_NO_START cluster 1533–1548 DROPPED (Josh,
  t_5140fb35) · QUEST_NO_COMPONENTS 1391 DROPPED (Josh, t_5a61cee3) · 8
  orphaned quest_contexts DROPPED (Josh, t_0ac25620) — every drop registered
  with restore pointers (scorecard-explorations/dropped-content-register.md).
  *(reconstructed 2026-08-14)*
- **REQ-M1-8** — Automated exit GREEN: scenario-harness census 153/153
  runnable / 0 FAIL / 33 SKIP over 186 quests; full gate 1148/1148
  (superseded by G1 4,573/4,573). *(reconstructed 2026-08-14)*
- **REQ-M1-9** — Restart-persistence: character resumes the golden route
  after server restart. *(reconstructed 2026-08-14 — promoted from the exit
  test below)*
- **REQ-M1-10** — Human playtest verdict on the curated Solzreed route (Josh;
  Open Decision #1 / deferred gate #1). *(reconstructed 2026-08-14)*
- **REQUIREMENT NOT RECOVERABLE:** a completeness bar for "shared engine
  defects" beyond the enumerated list — M1 scope is explicitly "trimmed, not
  exhaustive"; no finite defect enumeration exists in the record.
- **REQUIREMENT NOT RECOVERABLE:** a peripheral-quest coverage target —
  explicitly out of scope (Lane B maintenance); no bar was ever set.

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M1 evidence |
|---|---|---|
| engine-path implementation (1) | REQUIRED | ✅ merged @ 94f498fc (BUG-006..012) + widened fixes (t_d8a8c798, t_60a559ab, registered drops) |
| bot-replay (3+4) | REQUIRED | ✅ 153/153 census → G1 4,573/4,573; control-plane contract replay 16/16 + full-route live PASS (t_15787275, proxy) |
| restart-persistence (5) | REQUIRED | ✅ retroactive via M2 baseline t_cca63225 + live probe t_92a41fe6 (2/2) |
| soak (6) | N/A | quest-spine milestone; no load-sensitive path claimed |
| human-feel (7) | DEFERRED-RECORDED | ⏳ deferred gate #1 / Open Decision #1 (Josh-owned); H UNKNOWN |

**Non-goals (reconstructed 2026-08-14):** individual peripheral quest bugs
(→ Lane B maintenance); harness extension M1-5d/5e (→ M2); full-corpus
coverage (→ G1/M2); no M5 contract dependency (pre-M5 automation
distinction).

**Exit tests (requirement-indexed):** Human → REQ-M1-10 · Automated (pre-M5)
→ REQ-M1-1, -2, -3, -4, -5, -7, -8 · Restart-persistence → REQ-M1-9 · After
M5: golden route replayed through the actor contract (REQ-M1-6 + M5 surface).

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

**Requirements (retrofit 2026-08-14):**
- **REQ-M2-1** — Golden path defined + documented: create character → starter
  progression → unlock mount → acquire farm → plant & harvest → build house →
  craft trade pack → transport pack → sell → return home.
  *(reconstructed 2026-08-14)*
- **REQ-M2-2** — Golden-path zone LOCKED: Solzreed (Josh 2026-08-03), route
  expanding outward. *(reconstructed 2026-08-14)*
- **REQ-M2-3** — Primary outputs delivered: curated route · human playtest
  checklist · scenario manifest · restart checkpoints · known-blocker
  registry · structured logging expectations · reproducible DB reset/seed
  procedure (no raw snapshot with accounts/secrets/production state).
  *(reconstructed 2026-08-14)*
- **REQ-M2-4** — Reset/seed procedure reproducible by a THIRD PARTY (clean
  host) from the docs — the manifest may not validate itself.
  *(reconstructed 2026-08-14)*
- **REQ-M2-5** — Human baseline: two players attempt the entire route from
  the reproducible reset state; every blocker captured with stage, repro,
  evidence, and its owning M3/M4 card. *(reconstructed 2026-08-14; the
  original two-player leg = deferred gate #2, Josh-owned)*
- **REQ-M2-6** — Automated baseline: manifest + reset procedure reproduce the
  selected character, item, property, recipe, pack, and vehicle
  prerequisites. *(reconstructed 2026-08-14)*
- **REQ-M2-7** — Restart baseline: checkpoints identify exactly which state
  is retained, lost, duplicated, or requires repair.
  *(reconstructed 2026-08-14)*
- **REQ-M2-8** — Quest census to 100%: every live context PASS or
  registered-drop or doc-SKIP, zero unexplained (band sweeps M2a–M2d + G1
  gate; ≥95% band gates adopted by reference from progression-board.md).
  *(reconstructed 2026-08-14)*
  - **REQ-M2a** — Band 1–20 census ≥95% (final: 1,169 PASS / 0 FAIL /
    0 doc-SKIP). *(reconstructed 2026-08-14)*
  - **REQ-M2b** — Playerbot repeatability pilot on Solzreed (final: 30/30).
    *(reconstructed 2026-08-14)*
  - **REQ-M2b-E2E** — Live-server bot harness (Login+Game+MySQL): 10-bot
    correctness + 25-bot stability gates. *(reconstructed 2026-08-14)*
  - **REQ-M2c** — Band 21–30 sweep (final: 847/847 PASS).
    *(reconstructed 2026-08-14)*
  - **REQ-M2d** — Band 41–50 sweep (final: 1,589 PASS / 2 doc-SKIP
    kept-by-ruling). *(reconstructed 2026-08-14)*
- **REQ-M2-9** — Harness expansion track inherited from M1: M1-5d (14
  unsupported act families, t_f198bb0e) + M1-5e (T4 full-corpus census,
  t_9fc77eb) ride in M2; census coverage grows past 153.
  *(reconstructed 2026-08-14)*
- **REQUIREMENT NOT RECOVERABLE:** the original per-band numeric thresholds
  for M2c/M2d beyond the reference-level "≥95%" (the board's meaning columns
  carry no threshold; both landed 100% PASS-or-doc-SKIP via G1).

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M2 evidence |
|---|---|---|
| engine-path implementation (1) | REQUIRED | ✅ census harness + M2b-E2E harness merged (G1 @ 7f5c179f7) |
| bot-replay (3+4) | REQUIRED | ✅ 4,573 PASS / 0 FAIL / 14 doc-SKIP (G1 gate PASSED 08-10); M2b pilot 30/30; contract replay 16/16 incl. mount chain; live E2E min slice + full-route live PASS (t_15787275, proxy) |
| restart-persistence (5) | REQUIRED | ✅ automated t_c6eb12ec/t_1998cfd8; restart t_cca63225/t_c069bacd + probe t_92a41fe6; clean-host t_52755daa/t_819930ef |
| soak (6) | N/A | planning/baseline gate; no soak claimed |
| human-feel (7) | DEFERRED-RECORDED | ⏳ original two-player baseline deferred (t_46bf9b84); bot-driven baseline is proxy, never H=2; H UNKNOWN |

**Non-goals (reconstructed 2026-08-14):** M2 is a planning/discovery gate —
not a claim that the full loop works (housing/trade repairs belong to
M3/M4); no content implementation beyond the census/harness track.

**Exit tests (requirement-indexed):** Human baseline → REQ-M2-5 · Automated
baseline → REQ-M2-6 · Restart baseline → REQ-M2-7 · Third-party reset check
→ REQ-M2-4 · Census/G1 → REQ-M2-8, REQ-M2a–d · Harness expansion → REQ-M2-9.

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

**Requirements (retrofit 2026-08-14):**
- **REQ-M3a-1** — Housing placement-zone validation (adjacent-homestead
  spacing enforced; overlap rejected). *(reconstructed 2026-08-14)*
- **REQ-M3a-2** — Decoration-limit enforcement. *(reconstructed 2026-08-14)*
- **REQ-M3a-3** — Housing-group/UI data wired. *(reconstructed 2026-08-14)*
- **REQ-M3a-4** — Ownership + permissions. *(reconstructed 2026-08-14)*
- **REQ-M3a-5** — Construction through the real engine path
  (HousingManager.Build). *(reconstructed 2026-08-14)*
- **REQ-M3a-6** — Crop placement · growth · harvest through the real engine
  path (Doodad.Use). *(reconstructed 2026-08-14)*
- **REQ-M3a-7** — Selected storage + furniture interactions (CofferContainer
  path). *(reconstructed 2026-08-14)*
- **REQ-M3a-8** — Two players establish ADJACENT homesteads and use the
  curated objects in ONE uninterrupted session (M5-stand-in rule allowed for
  the automated leg). *(reconstructed 2026-08-14)*

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M3a evidence |
|---|---|---|
| engine-path implementation (1) | REQUIRED | ✅ merged @ 4d0427b96; real HousingManager.Build / CraftEffect / Doodad.Use / CofferContainer paths |
| bot-replay (3+4) | REQUIRED | ✅ M3aExitScenarioTests: 2 scripted actors, 16m adjacency (10m overlap REJECTED), ONE session; Rei gates t_72c787c8 / t_449875bd ACCEPT |
| restart-persistence (5) | N/A | single-session by design; persistence is M3b's class |
| soak (6) | N/A | no load path claimed |
| human-feel (7) | UNKNOWN (recorded) | H stays UNKNOWN; M3a contract replay = deferred gate #3 |

**Non-goals (reconstructed 2026-08-14):** persistence/recovery (→ M3b);
save-performance budget (→ M3b); server-restart restoration (→ M3b).

**Exit tests (requirement-indexed):** two-player one-session gate →
REQ-M3a-1..8 (automated leg via M5-stand-in scripted actors; human leg
deferred, H UNKNOWN).

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

**Requirements (retrofit 2026-08-14):**
- **REQ-M3b-1** — Furniture + bound doodad persistence. *(reconstructed 2026-08-14)*
- **REQ-M3b-2** — Door/window phase state persistence. *(reconstructed 2026-08-14)*
- **REQ-M3b-3** — Crop + livestock recovery. *(reconstructed 2026-08-14)*
- **REQ-M3b-4** — Rotation/attachment integrity. *(reconstructed 2026-08-14)*
- **REQ-M3b-5** — Storage persistence. *(reconstructed 2026-08-14)*
- **REQ-M3b-6** — Server restart restoration. *(reconstructed 2026-08-14)*
- **REQ-M3b-7** — Disconnect + logout cleanup. *(reconstructed 2026-08-14)*
- **REQ-M3b-8** — Orphan/duplicate prevention. *(reconstructed 2026-08-14)*
- **REQ-M3b-9** — Administrative repair tooling. *(reconstructed 2026-08-14;
  evidence: PropertyRepairScanner/Service + /house_repair GM command merged
  @ 5981246ea (99edc67a, t_7c71be66), 13/13 scanner tests, Rei gate PASS run
  1892 — cited in the M3b exit record, t_c2dd474b)*
- **REQ-M3b-10** — N≥3 crash cycles (restart · kill -9 mid-save with open
  autosave transaction · container kill during harvest), 16 rows asserted per
  boot, no loss/dup. *(reconstructed 2026-08-14, from the 08-09 audit + exit record)*
- **REQ-M3b-11** — Save-duration budget: autosave p95 < 2s at gate scale
  (2 homesteads + 25 bots embodied). *(reconstructed 2026-08-14)*
- **REQ-M3b-12** — A4 SaveManager dirty tracking audited for property objects
  (hard prerequisite). *(reconstructed 2026-08-14)*

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M3b evidence |
|---|---|---|
| engine-path implementation (1) | REQUIRED | ✅ M3b-1..4 merged (5dc7c2fbd / 71b43e09f / 3913932bf / 5981246ea) |
| bot-replay (3+4) | REQUIRED | ✅ M3bExitPersistenceE2eTests rig on the tree |
| restart-persistence (5) | REQUIRED | ✅ EXIT E2E f5b00c686 PASS 7m08s — N=3 cycles incl. kill -9 mid-save + container kill, 16 rows/boot, no loss/dup |
| soak (6) | REQUIRED (load-sensitive save path) | ✅ save budget at scale: autosave p95 1301ms < 2000ms @ 25 bots + 2 homesteads |
| human-feel (7) | UNKNOWN (recorded) | H stays UNKNOWN |

**Non-goals (reconstructed 2026-08-14):** housing construction/UI (→ M3a);
trade/craft/transport (→ M4); multi-homestead scale (→ M8).

**Exit tests (requirement-indexed):** repeated logout/restart/crash-recovery
→ REQ-M3b-1..8, -10 · quantified cycles → REQ-M3b-10 · save-duration budget
→ REQ-M3b-11 · A4 audit → REQ-M3b-12.

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
f5b00c686). REQ-M3b-9 (administrative repair tooling):
PropertyRepairScanner/Service + /house_repair GM command merged @ 5981246ea
(99edc67a, t_7c71be66), 13/13 scanner tests, Rei gate PASS run 1892 —
citation added 2026-08-14 (t_c2dd474b).

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

**Requirements (retrofit 2026-08-14):**
- **REQ-M4-1** — Crafting: recipe prerequisites · material + labor
  consumption · output correctness · workstation range/ownership ·
  inventory-full handling. *(reconstructed 2026-08-14)*
- **REQ-M4-2** — Trade packs: creation · backpack occupancy · placement/pickup
  · ownership · storage on property · maturation · sale + reward correctness.
  *(reconstructed 2026-08-14)*
- **REQ-M4-3** — Vehicles/ships: summon/despawn · passenger + cargo
  attachment · death/disconnect cleanup · portal/instance behavior · restart
  recovery · stuck recovery. *(reconstructed 2026-08-14)*
- **REQ-M4-4** — Integrated exit: group harvests real materials → crafts pack
  → loads vehicle → travels defined route → unloads + sells → correct reward
  → repeats after restart. *(reconstructed 2026-08-14)*
- **REQ-M4-5** — M2 release validation: four players complete one integrated
  session from a clean reset state without GM repair (automated fallback:
  scripted actors on real engine paths, M5-stand-in rule).
  *(reconstructed 2026-08-14)*
- **REQ-M4-6** — Per-object-type restart assertions: slave/vehicle
  attachment, pack maturation timers, cargo ownership each survive restart.
  *(reconstructed 2026-08-14, from the 08-09 audit)*
- **REQ-M4-7** — A2 broadcast economics verified at M4 entry
  (allocation-free short-circuit; wake-storm scans budgeted); convoy-VOLUME
  measurement is a soak-scale item owned by the M6 soak lane.
  *(reconstructed 2026-08-14)*

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M4 evidence |
|---|---|---|
| engine-path implementation (1) | REQUIRED | ✅ release/m4-exit merged (f28b93fc1 / e4af04a49 / 2907f46ff); unit gate 1778/0/1; pinned audited SHA 95bb1c78e (deployed, ledger state 2) |
| bot-replay (3+4) | REQUIRED | ✅ M4ExitIntegratedSessionTests: 4 scripted actors, real paths (harvest→craft→pack→load→travel→sell→repeat; negatives incl. LevelLowToUse, 801 despawn, StoreCantSellSameZone); merged-tree re-published run (t_abe87eaf) |
| restart-persistence (5) | REQUIRED | ✅ M4_2TradePackRestart PASS 2m12s (kill -9); M4Vehicles PASS 3m09s (2× kill -9); M3bExit E2E PASS 7m03s; merged-tree re-run 1/1 + 1/1 + M2b 5/5 |
| soak (6) | N/A at closure | convoy-volume deferred to M6 soak lane (recorded) |
| human-feel (7) | DEFERRED-RECORDED | ⏳ deferred gate #4 + deployment-lane playtest after Josh GO; H UNKNOWN |

**Non-goals (reconstructed 2026-08-14):** music/contests/siege (explicitly
ahead of M4); combat balance; housing placement (→ M3a).

**Exit tests (requirement-indexed):** integrated group run → REQ-M4-4 · M2
release validation → REQ-M4-5 · per-object restart assertions → REQ-M4-6 ·
A2 entry verification → REQ-M4-7.

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

**Requirements (retrofit 2026-08-14):**
- **REQ-M5-1** — One unified observation snapshot. *(reconstructed 2026-08-14)*
- **REQ-M5-2** — One validated action request format. *(reconstructed 2026-08-14)*
- **REQ-M5-3** — Lifecycle tracking: Requested → Accepted → Running →
  Completed | Rejected(reason) | Interrupted(reason) | TimedOut.
  *(reconstructed 2026-08-14)*
- **REQ-M5-4** — Failure reasons on rejection/interruption.
  *(reconstructed 2026-08-14)*
- **REQ-M5-5** — Cancellation + timeout. *(reconstructed 2026-08-14)*
- **REQ-M5-6** — Diagnostics + trace IDs. *(reconstructed 2026-08-14)*
- **REQ-M5-7** — No-database-shortcut policy: actions invoke normal gameplay
  services only — no direct DB manipulation, no bot-only resource creation.
  *(reconstructed 2026-08-14)*
- **REQ-M5-8** — Adapter implementations over existing systems: NpcAi
  movement, target selection, skill execution, interaction, inventory/game
  services (administrator commands for diagnostics/test setup only, never as
  a production gameplay-action implementation). *(reconstructed 2026-08-14)*
- **REQ-M5-9** — Single execution boundary: one world/character mutation
  seam; controllers may enqueue requests but may not mutate a Character
  concurrently. *(reconstructed 2026-08-14)*
- **REQ-M5-10** — Threading-boundary verification: a debug thread-affinity
  assertion proves zero Character/world mutation off the execution boundary
  (A1 marshal seam) — trace-based exit tests alone do NOT satisfy this.
  *(reconstructed 2026-08-14, from the 08-09 audit)*
- **REQ-M5-11** — Idempotency/correlation: retries and timeouts cannot
  duplicate items, currency, labor consumption, quest credit, or
  interactions. *(reconstructed 2026-08-14)*
- **REQ-M5-12** — Bot audit trail: every action emits a structured trace
  record `{trace_id, actor_id, action, target_id, requested_at, started_at,
  completed_at, result, state_changes}`. *(reconstructed 2026-08-14)*
- **REQ-M5-13** — M5 required action surface on the contract: Observe · Move
  · Stop · Target · Cast · Interact · Loot · UseItem · Mount/Dismount ·
  AcceptQuest · TurnInQuest. *(reconstructed 2026-08-14)*
- **REQ-M5-14** — Exit: a scripted actor completes the curated
  quest/combat/mount segment and produces a machine-readable trace showing
  every request, transition, result, and failure. *(reconstructed 2026-08-14)*
- **REQ-M5-15** — Actor contract tests pass independent of any controller;
  retry tests prove non-idempotent actions do not execute twice.
  *(reconstructed 2026-08-14)*

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M5 evidence (as of 2026-08-14; status NOT re-graded here) |
|---|---|---|
| engine-path implementation (1) | REQUIRED | 🔶 partial: A1 seam c6d8f93a0 + B1 six-action surface 761d1e81a merged (merged-tree re-verify 1850/0/1); remaining surface (Observe/Move/Stop/Target/Cast) v1 impls on develop since 34cf33cb2 (t_4f11a519) — canonical fidelity UNVERIFIED (Move known non-conforming: silent Transform write, no broadcast), spec'd as M5.3 2026-08-16 (t_d837ee0b) |
| bot-replay (3+4) | REQUIRED | 🔶 B1Actions/B1ContractLayer tests on the merged tree; control-plane contract replay rig (t_61a0eebb, 16/16 quests) |
| restart-persistence (5) | N/A | contract layer adds no new persistence; underlying systems carry M1/M2/M3/M4 restart classes |
| soak (6) | N/A | soak belongs to the M6 lane |
| human-feel (7) | N/A (recorded) | feel gates belong to later phases; first consumer Lane D scoped w/ JOSH GO (t_52b2b084) |

**Non-goals:** the explicit NON-GOALS paragraph below is the canonical M5
non-goal set (unchanged). *(reconstructed 2026-08-14 additions:* economic
actions → M5.1; housing actions → M5.2.*)*

**Exit tests (requirement-indexed):** M5 core scripted segment → REQ-M5-13,
-14 · M5.1 economy segment → REQ-M5.1-1..5 · contract tests independent of
controller + retry tests → REQ-M5-15 · threading-boundary assertion →
REQ-M5-10 · M5.3 core-surface segment (Observe/Move/Stop/Target/Cast) →
REQ-M5.3-1..11 (spec 2026-08-16, t_d837ee0b).

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
- **M5.3 core-surface close (SPEC'D 2026-08-16 — implementation parked at
  M5.2 cap):** Observe · Move · Stop · Target · Cast

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

**M5.1 — economic extension (Requirements + DoD, retrofit 2026-08-14):**
**Requirements:**
- **REQ-M5.1-1** — Economic action surface on the contract: Plant · Harvest
  · Craft · PackPickup/PutDown · BoardVehicle · Buy/Sell ·
  Deposit/Withdraw. *(reconstructed 2026-08-14)*
- **REQ-M5.1-2** — Every M5.1 action executes through its REAL engine path
  (no shortcuts): Plant → real planting · Harvest → real doodad.Use · Craft
  → real CharacterCraft.Craft · BoardVehicle → real SlaveManager.BindSlave /
  Seat.LoadPassenger · PackPickup/PutDown → real pack ops · Buy/Sell → real
  shop ops · Deposit/Withdraw → real storage ops.
  *(reconstructed 2026-08-14)*
- **REQ-M5.1-3** — Phase-2 prerequisite LoadPackOntoVehicle: real gameplay
  path PackVehicleService → SlaveManager.AttachDoodadAtPoint (retail
  snap-to-cargo-point), capacity from slave_doodad_bindings.
  *(reconstructed 2026-08-14)*
- **REQ-M5.1-4** — Phase-2 prerequisite DriveVehicle: contract drive action
  (MoveTo-when-boarded) through client-authored VehicleMovementModel
  (CSMoveUnitPacket path); no Transform assignment. *(reconstructed 2026-08-14)*
- **REQ-M5.1-5** — Exit: a scripted actor completes the curated
  farm/craft/pack/vehicle/trade segment through the economic actions;
  Phase-2 replay sequences Housing.Build FIRST, then farm/storage → craft →
  pack → load/drive vehicle → unload → sell → reward (scope marker
  t_2625be99). *(reconstructed 2026-08-14)*

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M5.1 evidence |
|---|---|---|
| engine-path implementation (1) | REQUIRED | ✅ all merged, Rei-gated: Deposit/Withdraw f760256a0 · Harvest ebff582a8 · BoardVehicle e7e7ef0fe · Craft rig 7a01ff57c + Craft dab91ecb0 · LoadPackOntoVehicle 6c2429ae0 · DriveVehicle 6edbf0cbb |
| bot-replay (3+4) | REQUIRED | ✅ per-action contract tests (13 Craft · 14 load · 7 drive · BoardVehicle 21/21 targeted · GameplayActor family 240/240 · full gate 2054/0/1); Phase-2 replay scenario merged (t_b4f455b0) — live E2E hook execution deferred to t_eaee04ee |
| restart-persistence (5) | N/A (inherited) | M5.1 adds no new persistence; pack/vehicle restart coverage = M4 DoD. GAP FLAG for the Rei gate: attached-pack-on-slave state (LoadPackOntoVehicle) has no dedicated restart assertion as of 2026-08-14 |
| soak (6) | N/A | M6 soak lane |
| human-feel (7) | UNKNOWN (recorded) | H stays UNKNOWN — never H=2 from scripted evidence |

**Non-goals (reconstructed 2026-08-14):** housing contract actions (→ M5.2);
combat/quest actions (→ M5 core); navigation rewrite; autonomous behavior.

**M5.2 — Housing.Build contract action (Requirements + DoD, retrofit 2026-08-14):**
**Requirements:**
- **REQ-M5.2-1** — BuildHouse contract action on the IGameplayActor surface
  over the REAL HousingManager.Build engine path (exact CSCreateHousePacket
  handler call). *(reconstructed 2026-08-14)*
- **REQ-M5.2-2** — Contract tests on the canonical rig (13 tests) + Rei gate
  3/3. *(reconstructed 2026-08-14)*
- **REQ-M5.2-3** — Phase-2 replay sequences Housing.Build BEFORE farm/storage
  (scope marker t_2625be99). *(reconstructed 2026-08-14)*

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M5.2 evidence |
|---|---|---|
| engine-path implementation (1) | REQUIRED | ✅ merged @ 3396d9ef1 (t_94761d55, Rei t_ebf36737 ACCEPT); latent upstream ParentWorld bug fixed; test-singleton pollution t_14bd519b + rig order-dependence flakes t_3c33557d root-caused + merged |
| bot-replay (3+4) | REQUIRED | ✅ 13 canonical-rig tests; post-merge gate 2074/0/1 |
| restart-persistence (5) | N/A | housing persistence = M3b class; Build adds no new persistence |
| soak (6) | N/A | no load path claimed |
| human-feel (7) | UNKNOWN (recorded) | H stays UNKNOWN |

**Non-goals (reconstructed 2026-08-14):** other housing interactions
(decoration/storage/furniture are M3a surface, not contract actions); any
non-housing contract action.

**M5.1 status — Kimi+Codex-verified recovery plan (2026-08-13; memo
`.hermes-ops/docs/m51-backtrack-recovery-memo-2026-08-13.md`; canonical sync
card t_c9f0d7f6):** remaining M5.1 work is NOT over-complex and needs NO
redesign — 3 of 4 families reached green/near-green inside one 160-iteration
budget before dying of operational causes (oversized 8-worker wave at
15:11:31 UTC incl. 3 already-merged cards re-verifying, 4+ concurrent Release
builds on one host, stale workspace bases 18 commits behind develop, mass
kill at 16:06:08 UTC — host/orchestrator-level). Work product survives in
kanban workspaces and is largely good → **SALVAGE AND SERIALLY MERGE; do not
re-implement.**
- **Completed M5.1 (merged to develop):** Plant (t_b1d7c430 — successful
  continuation; supersedes t_a69e4998, blocked "do not redispatch") ·
  PackPickup/PutDown (t_64ecf525, Rei gate t_9ca4aa07) · Buy/Sell
  (t_8741b03d) · control-plane API (t_7b6d7a4b, Rei gate t_29d2273b) · MCP
  sidecar (t_446228b5, Rei gate t_b5467288) · first consumer — scripted Lane
  D auction-house scenario (t_52b2b084, Rei gate t_0e01ef42).
- **Salvage wave — serial merge queue, readiness order (one tree = one merge;
  worktree-parallel prep OK):**
  1. **Deposit/Withdraw — t_78ce17a2 (blocked):** greenest; rebase, DROP
     out-of-scope livestock flake change (dup of develop c51b33645), renumber
     actions 22-25, keep-or-split EconomyDrive extras (stop condition).
  2. **Harvest — t_234da01a (todo):** small, green; rebase/renumber; add or
     justify the missing IntegrationTests replay hook.
  3. **BoardVehicle — t_15343fdd (todo):** real BindSlave path; rebase/
     renumber ×2; needs a full-gate run (19/19 targeted only today).
  4. **Craft — SPLIT (t_6b5ac43e rig repair + t_cffb71ad implementation):**
     only technical blocker is the rig world-registration NRE; develop's rig
     rework 11978eafd is the fix substrate — fix the rig first, rebase the
     implementation onto it.
  All four diffs collide textually on the same enum tail (15-18 vs develop's
  15-21) and the GameplayActor insertion ~:563 — every one renumbers on
  rebase (textual coupling, not architectural). Doctrine: never wave >2
  builders at the shared tree; never dispatch already-merged cards; no global
  budget increase (3/4 reached green in one budget).
- **Genuine Phase-2 prerequisites — previously in NO card's scope (memo
  F.1/F.2):**
  - **LoadPackOntoVehicle — t_a7756a00 (blocked):** no engine path loads a
    placed pack onto a vehicle. `PutDownBackpackEffect.cs:51-88` attaches
    packs only to housing; nothing re-parents a placed pack to a slave (the
    old M4 rig hand-attached: M4ExitIntegratedSessionTests.cs:328). Retail
    1.2 snaps put-down-near-vehicle to cargo points.
    **DONE 2026-08-14 — merged to develop @ 6c2429ae0 (Rei gate t_aca50468
    ACCEPT):** real gameplay path PackVehicleService →
    SlaveManager.AttachDoodadAtPoint (retail snap-to-cargo-point via
    ApplyAttachPointLocation); capacity from slave_doodad_bindings.
  - **DriveVehicle — t_eaf1754d (blocked):** contract MoveTo is
    character-walking only; vehicle movement is client-authoritative
    (CSMoveUnitPacket/VehicleMoveType). BoardVehicle = boarding only — needs
    a drive action or move-when-boarded semantics.
    **DONE 2026-08-14 — merged to develop @ 6edbf0cbb (Rei gate t_bc74fd29
    ACCEPT):** DriveVehicle(uint objId, Vector3 dest, float speed, timeout,
    idempotencyKey) through client-authored VehicleMovementModel
    (CSMoveUnitPacket path); no Transform assignment.
- **Housing.Build — t_94761d55 (running):** a separate **M5.2 contract card**,
  included in the current Josh-approved Phase-2 scope (marker t_2625be99).
  Inclusion is approved; **implementation remains OPEN** (card running, not
  merged; deferred gate #3 lists only farming actions — HousingManager.Build
  has no contract action until this card lands).
  **DONE 2026-08-14 — merged to develop @ 3396d9ef1 (Rei gate t_ebf36737
  ACCEPT):** BuildHouse over the REAL HousingManager.Build engine path
  (exact CSCreateHousePacket handler call). Scope inclusion stands — the
  Phase-2 replay now sequences Housing.Build BEFORE farm/storage.
- **H stays UNKNOWN** everywhere — no bot/scripted evidence is H=2 (human
  packet t_2b654349, Rei).

**M5.3 — close the M5 core surface: Observe · Move · Stop · Target · Cast
(Requirements + DoD + Exit tests, SPEC'D 2026-08-16 — implementation parked
at the M5.2 cap until Josh GO; spec t_d837ee0b, review gate t_a844e2b1):**

REQ-M5-13's vocabulary is Observe · Move · Stop · Target · Cast · Interact ·
Loot · UseItem · Mount/Dismount · AcceptQuest · TurnInQuest. B1 (761d1e81a)
landed 6 of 11; M5.1/M5.2 covered the economic + housing extensions. The
five core actions carry **v1 implementations on develop since the original
contract spike (t_4f11a519, 34cf33cb2, 2026-08-07) — present but NOT
verified against this standard**: no canonical dossier, no Rei gate, no
threading-boundary evidence, and **Move is KNOWN non-conforming** — it
advances via a silent local Transform write (`GameplayActor.ApplyPosition`,
GameplayActor.cs:2173-2179: no movement broadcast, no client-authored path;
the player-equivalent reference is DriveVehicle's VehicleMovementModel /
CSMoveUnitPacket path). M7 (Adventurer bots) depends on these five ("B1
(combat/quest actor actions)").

**Requirements (canonical-1.2-true; dossier-first per mechanic-research
doctrine):**
- **REQ-M5.3-1** — Canonical dossier FIRST: before any implementation,
  commit `scorecard-explorations/mechanics/m5-core-actions-canonical.md`
  covering (a) character foot movement — walk/run, the client-authored
  CSMoveUnitPacket path, movement broadcasts, stop/halt semantics; (b)
  targeting — the real engine target-set path for `Unit.CurrentTarget`; (c)
  skill cast mechanics — casting_time, CastTask scheduling,
  SCSkillStartedPacket/SCSkillEndedPacket, move-interrupt rules, mana/
  cooldown consumption — all canonical-1.2 ground truth; every claim flagged
  research-derived (wiki cited + dated) or data-verified (compact.sqlite3 /
  engine code); no invented mechanics.
- **REQ-M5.3-2** — Observe: one unified observation snapshot through real
  engine queries only (region lists, WorldManager, character state — REQ-M5-1
  carry); no packets (spec §8); emits the audit record and completes
  immediately. v1 shape (GameplayActor.cs:89-113) retained or adjusted per
  dossier findings.
- **REQ-M5.3-3** — Move: MoveTo/MoveToUnit advance the character through the
  REAL 1.2 movement path — the client-authored unit-movement model
  (CSMoveUnitPacket-equivalent; the same family DriveVehicle rides via
  VehicleMovementModel), with real movement broadcasts; the v1 silent
  Transform write is replaced. Arrival (ArrivalRadius 0.5f) → Completed;
  budget expiry → TimedOut(Navigation); non-positive speed / non-finite
  destination → Rejected(RejectedAction); busy → Rejected(StateTransition).
- **REQ-M5.3-4** — Stop: interrupts the running request (Interrupted, detail
  "stop requested") and completes itself through the real 1.2 halt semantics
  per dossier; no-op when idle (idempotent).
- **REQ-M5.3-5** — Target: SetTarget through the real engine targeting path
  (`Unit.CurrentTarget` — the exact assignment the engine's targeting
  performs per dossier); unknown objId → Rejected(RejectedAction).
- **REQ-M5.3-6** — Cast: executes ONE skill through the real character skill
  pipeline (Character.UseSkill — the exact call CSStartSkillPacket's
  learned-skill branch makes; cast mechanics per dossier — cast time, cast
  task, start/end broadcasts). Validation gates: skill template exists,
  character knows the skill (learned / default / common / variant — the
  packet branch's own rule), target resolves. Engine refusal →
  Rejected(RejectedAction). One skill per request — no rotation logic.
- **REQ-M5.3-7** — Threading-boundary (carries REQ-M5-10): every M5.3 action
  executes only on the A1 marshal seam (game-loop thread); a debug
  thread-affinity assertion (ExecutionBoundary) proves zero Character/world
  mutation off the boundary — trace-based exit tests alone do NOT satisfy
  this.
- **REQ-M5.3-8** — Idempotency/correlation (carries REQ-M5-11): retries and
  timeouts cannot double-execute — Cast never double-casts (request-key
  dedupe primary; engine-true backstop: mana/cooldown consumed); Move/Stop/
  Target idempotent by construction + key; Observe is a read.
- **REQ-M5.3-9** — Bot audit trail (carries REQ-M5-12): every action emits
  the structured trace record `{trace_id, actor_id, action, target_id,
  requested_at, started_at, completed_at, result, state_changes}`.
- **REQ-M5.3-10** — Contract tests (carries REQ-M5-15): per-action contract
  tests on the canonical rig pass independent of any controller; retry tests
  prove non-idempotent actions do not execute twice; gate.sh green on the
  merged tree.
- **REQ-M5.3-11** — Exit (carries REQ-M5-14): a scripted actor completes a
  curated segment exercising all five actions through their real paths
  (observe → move → stop → target → cast) and produces a machine-readable
  trace showing every request, transition, result, and failure.

Carried constraints (unchanged): REQ-M5-7 (no database shortcuts — normal
gameplay services only), REQ-M5-9 (single execution boundary — controllers
enqueue requests, never mutate a Character concurrently).

**DoD — evidence classes (ledger t_547ef82d):**
| Class | Required | M5.3 evidence / status |
|---|---|---|
| engine-path implementation (1) | REQUIRED | v1 impls on develop since 34cf33cb2 (t_4f11a519) — to be verified/reworked per dossier: Move KNOWN non-conforming (silent Transform write, no broadcast); Observe/SetTarget/Cast shapes engine-true (WorldManager queries / Unit.CurrentTarget / Character.UseSkill), verification pending |
| bot-replay (3+4) | REQUIRED | per-action contract tests on the canonical rig (existing v1 tests GameplayActorTests + dossier-driven assertions); M5.3 exit scenario replay; post-merge gate green |
| restart-persistence (5) | N/A | none of the five introduces persistence; restart classes live in M1/M2/M3b/M4 |
| soak (6) | N/A | soak belongs to the M6 lane |
| human-feel (7) | UNKNOWN | H stays UNKNOWN — Josh runs it; never inferred from bot/scripted evidence |

**Non-goals (canonical M5 set, carried):** no autonomous planning · no LLM
integration · no generalized navigation rewrite (Move = straight-leg walk via
the real movement path; no navmesh/pathfinding/obstacle-avoidance work) · no
core gameplay interface replacement · no bot-only inventory or combat
behavior. **M5.3-specific:** no actions beyond the five; no combat
decision-making/rotation (Cast = one skill per request); no changes to
M5.1/M5.2 actions; no persistence additions; Observe stays a server-side
query (no packet fabrication).

**Exit tests (requirement-indexed):**
- E1 — dossier committed with citations + research-derived/data-verified
  flags → REQ-M5.3-1 (checked at review gate).
- E2 — Observe test: snapshot equals direct WorldManager query results;
  audit record Observe/Completed → REQ-M5.3-2.
- E3 — Move test: position advances via the real path with real movement
  broadcasts observed; arrival → Completed; timeout → TimedOut(Navigation);
  speed≤0 / non-finite → Rejected(RejectedAction); busy →
  Rejected(StateTransition) → REQ-M5.3-3.
- E4 — Stop test: running Move interrupted (Interrupted, "stop requested"),
  Stop Completed; second Stop is a no-op → REQ-M5.3-4.
- E5 — Target test: CurrentTarget set to the resolved unit; unknown unit →
  Rejected(RejectedAction); Observe reflects the target → REQ-M5.3-5.
- E6 — Cast test: real skill executes (mana/cooldown consumed, effects per
  template); unknown skill / not-learned / unknown target →
  Rejected(RejectedAction); same-key retry never double-casts → REQ-M5.3-6.
- E7 — ExecutionBoundary thread-affinity assertion passes for every action
  on the A1 seam → REQ-M5.3-7 (carries REQ-M5-10).
- E8 — retry/idempotency tests per action (request-key dedupe + engine-true
  backstop) → REQ-M5.3-8 (carries REQ-M5-11).
- E9 — audit-record shape assertion per action → REQ-M5.3-9 (carries
  REQ-M5-12).
- E10 — contract tests run with no controller in the rig; full gate.sh green
  on the merged tree → REQ-M5.3-10 (carries REQ-M5-15).
- E11 — M5.3 exit scenario (observe → move → stop → target → cast) completes
  with a machine-readable trace → REQ-M5.3-11 (carries REQ-M5-14).

### MCP expansion (2026-08-27)
MCP sidecars and the management gateway remain client-neutral; availability is
not external-client actor lifecycle evidence. Historical coverage merge
`8a22dcb4` and its 33-test / 19-tool smoke record are retained below as
historical evidence.

Commit `1638b007c` adds five authenticated actor routes and matching MCP tools:
`POST /api/actors/discover_quests`, `POST /api/actors/discover_self_quests`,
`POST /api/actors/interact_with`, `POST /api/actors/talk`, and
`POST /api/actors/equip`. The MCP catalog is now 24 tools.

Focused validation passed: `BotActionControllerRouteTests` 2/2,
`BotControlActionMcpTests` 33/33, `BotActionCommandQueueTests` 16/16; MCP
projects Release build clean; stdio smoke 24 tools; full gate **2486 total /
2485 succeeded / 0 failed / 1 skipped**.

The live `discover_self_quests` MCP benchmark passed with `action_status`,
`trace`, and an independent MySQL character-row cross-check. No safe doodad
interaction was attempted. The earlier asset-missing
`mcp-live-smoke-2026-08-27.md` run at `7e109d550` remains historical: Game
exited before WebApi, and that run is not the current benchmark verdict.

Plant, Harvest, Craft, party, trade, expeditions, and related newer actor
actions still lack authenticated routes. They remain explicitly deferred and
are not claimed as MCP-exposed.

### MCP actor-route expansion and layered validation (2026-08-27)

**Current state:** **31 MCP tools**; added authenticated routes/tools for
`craft`, `plant`, and `harvest`, joining `deposit_money`, `withdraw_money`,
`deposit_item`, `withdraw_item`, `discover_quests`, `discover_self_quests`,
`interact_with`, `talk`, and `equip`. Focused route/MCP/queue tests are **53/53**
(`BotActionControllerRouteTests` 2/2, `BotControlActionMcpTests` 33/33,
`BotActionCommandQueueTests` 18/18); protocol smoke covers 31 tools; full
solution gate **2490 total / 2489 succeeded / 0 failed / 1 skipped**. The real
local MCP benchmark passed `observe`/`move`/`discover_self_quests` with
`action_status`/`trace` and independent DB evidence.

**Goal:** expose only stable, player-like `IGameplayActor` actions through
authenticated, enqueue-only `/api/actors/*` routes and matching MCP tools,
then validate each through MCP + direct E2E + DB/wire/restart evidence as
appropriate. Ordered families (each starts with archaeology and a reviewed
contract): **Deposit/Withdraw (LANDED) → Plant/Harvest (LANDED) → Craft (LANDED) →
Buy/Sell → Pack/vehicle → Party → Expedition → Trade → Auction**.

**Acceptance per family:** route authentication/binding tests; MCP
schema/mapping tests; negative, idempotency, and lifecycle tests; one real MCP
scenario; direct E2E/DB/wire/restart checks where required; and updates to the
SCORECARD, STATUS, and capability matrix. **H remains human-only**: MCP or
scripted evidence never promotes a human-feel result.

**Prerequisites and evidence:** MCP sidecars require a running WebApi and
token; Game requires `game_pak`, worlds, and `compact.sqlite3`. Headless MCP
needs no running ArcheAge client, while visual, packet, and H tests require
the client. Record tool/route counts, focused-test totals, lifecycle timings,
trace IDs and state deltas, plus direct DB rows, wire captures, and restart
parity artifacts when applicable. If MCP reports `Completed` without the
expected trace/state, record a **PLAYERBOT_BLOCKER**.

**Non-goals:** fake WebApi routes; direct manager/DB/world mutation from API
threads; hidden-state player mode; MCP replacing packet/DB/scaling/human tests;
or mass endpoint generation. Actions without a safe authenticated controller
route or observable contract — currently Plant/Harvest/Craft, party, trade,
and related actions until their route contracts land — remain deferred, not
claimed complete.
>>>>>>> origin/develop

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

**Scheduler-driven soak STAGE 1 (2026-08-24, 4e460305b):** the recorded caveat
"scheduler-driven soak still required if M6 exit mandates it" now has its first
rung — `SchedulerSoakStage1Tests` drove 10 manifest-provisioned citizens ×
30min through real IPlayerBotScheduler wakes; TWO VALID runs: ~90k steps, 0
failed/timed-out, wake avg ~99ms, DB writes 14–19/min/citizen, tick+region
budgets PASS. Three open engine findings on record: (a) manifest roster entries
without home spawn start at race-template position but walk to the
patrol-default Nuian home (run-1 elves walked 4.3km and drowned); (b) physics
slow-thread rate ~3× the scheduler-disabled baseline (0.23–0.27/min vs
calibrated 0.031–0.067) — same-world clause far inside budget, recalibration is
an M6-exit decision; (c) heap churn to ~5.9GB under roam vs the flat 3.4GB
band. Stage-1 execution changes NO exit label — the full M6 exit-label decision
remains open.

**Detail (2026-08-09 audit):** M6 exit is blocked on **A1** (execution
boundary — bot steps off the game loop violate M5's core rule). Added exit
requirements: (a) restart-persistence scenario per the standing rule —
bots survive server restart with identity/inventory/position/schedule intact
(B4 store); (b) observability — the silent catches in BotAppearanceFactory
(:212/:225) and BotE2EBridgeBootstrap (:32-35) must log before any 25+-bot
gate; a silently gearless or bridge-dead bot population is a failed gate;
(c) merge-to-develop is a closing condition (G0-1).

---

## PlayerBot-Validated Development Loop (added 2026-08-25)

The development methodology for all NEW system work (locks in what M1-M7
actually did, makes it explicit for M8+):

```
client/server evidence → system archaeology → behavioral contract
  → minimum vertical slice → validation → PlayerBot interaction
  → bot/regression validation → PROGRESSION BLOCKER discovered
  → gap fixed → bots progress farther → repeat
```

Rules:
1. **Reconstruct, don't invent.** Unimplemented systems are built from
   evidence (neighboring implementations, packets/opcodes, compact.sqlite3,
   DB schemas, client data, logs). Findings graded VERIFIED /
   STRONGLY_INFERRED / PLAUSIBLE / UNKNOWN. UNKNOWN areas stay unfilled
   until evidence arrives (see mechanics dossiers: fishing-domain.md,
   indun-domain.md — the template).
2. **Contract before implementation**: player intent → client action →
   server validation → domain logic → state mutation → world effects →
   persistence → broadcast → client-visible result.
3. **Vertical slices**, not horizontal systems: perform action → state
   updates → world reflects → restart → still correct. Expand from there.
4. **PLAYERBOT_BLOCKER ledger** (`scorecard-explorations/playerbot-
   blockers.md`): when a bot cannot continue playing normally, capture
   {scenario, bot state, intended action, observed vs expected, suspected
   layer (bot/server/data/unknown), evidence, repro}. Blockers feed the
   backlog and outrank speculative features. Do not work around server
   faults inside the bot — find the layer that is actually wrong.
5. **PLAYER_MODE vs TEST_MODE**: bots perceive what a player could
   perceive; test-only instrumentation (DB asserts, packet captures) never
   leaks into autonomous behavior.
   *Status 2026-08-25:* rule documented, mechanical guard not yet proven —
   the headless-roam BroadcastMovement opt-out (615a645c9), bridge metrics,
   and rig seed seams are gated by inspection only; B5 owns the audit +
   negative tests that make the separation enforced rather than asserted.
6. **Capability matrix** (`scorecard-explorations/mechanics/playerbot-
   capability-matrix.md`): Perceive / Decide / Act / Verify per system,
   populated from implementation reality. A system is BOT_TESTED only when
   a bot can do all four through real engine paths.
7. **Completeness lifecycle** per system: UNKNOWN → EVIDENCE_COLLECTED →
   CONTRACT_DEFINED → VERTICAL_SLICE → PLAYABLE → PERSISTENT →
   CLIENT_VALIDATED → BOT_INTERACTABLE → BOT_TESTED → COMPLETE.
   Implementation alone ≠ complete.

## Simulation-Fidelity Scaling Model (G2-A5/A3, added 2026-08-25)

Measured basis (g2-scaling-curve-report.json): marginal embodied bot ≈
16.5MB RSS; tick p95 0.42ms at 30 citizens vs 100ms budget; baseload ~5.2GB
is world data. Raw count is NOT the wall — per-bot simulation frequency,
broadcast cost near players, and synchronized cadences are.

Fidelity ladder (PopulationDirector Dormant/Reduced/Full + scheduler
cadence), driven by RELEVANCE not count:

```
DORMANT (db row + metadata, near-zero cost)
  → BACKGROUND (coarse/event-driven: schedules, travel-as-progress)
    → ACTIVE (normal gameplay cadence)
      → OBSERVED/ENGAGED (player within radius; full fidelity)
```

Principles:
- Spend computation only where a bot can affect or be affected by gameplay.
- Real players always win scheduling priority over background bots.
- Event-driven wakes > polling; staggered cadences > synchronized spikes.
- Bots exercise the REAL game systems — an optimization that bypasses the
  system under test destroys the bot's reason to exist.
- Graceful degradation: load ↑ → background fidelity ↓ first; queues bounded;
  real gameplay stable.
- Optimization waves: PROFILE → remove pathological work → reduce frequency
  → event-driven wakeups → staggering → incremental perception → interest
  management → pathfinding → shared immutable knowledge → persistence
  batching → allocation work. Measure before/after every wave.

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
| 3 | **M3a contract replay** (housing/farming through contract actions) | M3a closed on scripted-actor proxy evidence (in-memory actors, reflection, GM inventory, direct service calls — predates A1/B1) | Phase 2: replay via M5.1/M5.2 contract actions on a real server, real engine paths, no direct DB/reflection/GM repair — **sequenced route: Housing.Build FIRST (BuildHouse contract, M5.2 t_94761d55, merged @ 3396d9ef1), then farm/storage (Plant/Harvest), then craft → pack → load/drive vehicle → unload → sell → reward**; labor + mail payout conservation (−60/pack, 124540/pack SpecialtyManager); machine-readable traces; process-level restart evidence (M4_2TradePackRestart / M4Vehicles / M3bExitPersistence re-run as-is); H stays UNKNOWN (proxy/bot-functional, never H=2) |
| 4 | **M4 economic/navigation replay** (farm → craft → pack → load → navigate → unload → sell → reward) | M4 closed on M4ExitIntegratedSessionTests (4 scripted actors; integrated rig assigns zones/transforms directly, manually attaches cargo) | Phase 2: replay via M5.1 contract actions incl. **LoadPackOntoVehicle (t_a7756a00, merged @ 6c2429ae0) + DriveVehicle (t_eaf1754d, merged @ 6edbf0cbb) — both prerequisites DONE 2026-08-14**; route = craft → pack → load/drive vehicle → unload → sell → reward (Housing.Build precedes per gate #3); navigation/travel from normal movement/vehicle controls — direct Transform/ZoneId assignment FAILS the gate; **labor (−60/pack) + mail payout (124540/pack, SpecialtyManager) conservation required**; preserve + rerun process-level restart E2Es (M4_2TradePackRestart, M4Vehicles); Rei verifies traces + conservation |
| 5 | **M6 B4 restart scenario** (bot identity/inventory/position/schedule survive restart) | 6h soak PASSED under revised approved budgets; A1 landed after soak; B4 metadata store BUILT 2026-08-20 (`playerbot_metadata`, below) | Phase 3: A1/B1 verified on merged develop; B4 metadata persistence implemented 2026-08-20; bot-world restart test (2 checkpoints) re-run with DIRECT metadata assertions — PASS |

**Deferred-gate execution (2026-08-12, Phase 3 t_9340e85d):** gate #5 (M6 B4
restart scenario) EXECUTED:
- **A1/B1 re-verified on the merged develop tree** (fork tip 857fbae20,
  lineage c6d8f93a0 + 761d1e81a): fresh-clone full gate **1850 passed / 0
  failed / 1 pre-existing skip** (`./scripts/gate.sh`, contract classes
  PlayerBotSchedulerTests + GameplayActorB1* included).
- **Bot-world restart test (2 checkpoints) PASS** — `B4BotRestartPersistenceE2eTests`
  (AAEmu.IntegrationTests/E2e/, bot-backtrack Phase 3): 2 process-level
  restarts; bot roster byte-identical (same account ids / character ids,
  exactly 3 Citizen rows, no re-creation/accumulation); identity/inventory/
  position persisted (adopt path, distinct factory looks, byte-identical
  item set, position NOT reset to the creation spawn — 6.4 km away); roam
  route re-armed deterministically (schedule); zero NameAlreadyExists.
  Evidence: `gate-m6-reconcile-b4-20260813-022040.md` (E2E logs).
- **Soak verdict PRESERVED verbatim as "passed revised approved budgets"** —
  M6 EXIT RECORD above and the EVIDENCE-LEDGER M6 soak-passed cell are
  untouched; this execution note records the preservation, no history
  rewritten.
- **H stays UNKNOWN** — no bot/scripted evidence recorded as H=2.
- Follow-up (NOT in this card — no feature work): the B4 playerbot_metadata
  store (schedule/profession/home as persisted data) is still not built;
  the restart replay proves persistence through the ordinary Character/save
  path + deterministic schedule re-arm.

**Deferred-gate #5 follow-up EXECUTED (2026-08-20, Josh-directed no-cards
pass):** the B4 `playerbot_metadata` store is now BUILT —
`PlayerBotMetadataStore` (personality/profession/home/schedule/behavior/
planner state keyed by characters.id; self-healing schema +
SQL/updates/2026-08-20 migration + base-dump entry; write-through REPLACE on
mutation — hard-kill safe — plus a dirty-row flush inside the SaveManager
transaction). The presence demo resolves home explicit-env → persisted →
template (stored state is load-bearing on adopt, not decorative) and records
home + roam-loop schedule per bot. `B4BotRestartPersistenceE2eTests` now
asserts the store DIRECTLY: per checkpoint, row exists pre-restart with
has_home=1 + env-pinned home + roam-loop schedule, and pre/post-restart
snapshots are EQUAL across both process restarts — **PASS 1/1 (4m39s,
evidence gate-m6-reconcile-b4-20260820-162058.md)**; the deterministic
re-arm log lines stay as secondary evidence. Full unit gate 2121/0/1 (15
new store tests). The audit-trace flush half of the B4 line item remains
open. H stays UNKNOWN — Josh's feel verdict is untouched.

**Reconciliation note (2026-08-13, canonical sync t_c9f0d7f6 — provenance:
Kimi independent engineering memo `m51-backtrack-recovery-memo-2026-08-13.md`
(read-only investigation, verified against source/git/tests, not card claims)
+ Codex reconciliation supplied by Josh + live kanban card states queried
2026-08-13):**
- **Phase 1 (M1/M2 contract replay, t_61a0eebb) REMAINS OPEN.** Live evidence
  is min-slice only — 1 quest (`/root/aaemu-e2e/logs/m1m2-contract-replay-report.json`
  PASS); the full 16-quest route passed only on the in-memory rig. Full-route
  LIVE replay is not yet evidenced; follow-up card **t_15787275** (full-route
  live replay) is queued. No Phase-1 completion claim is made.
- **Phase 2 (M3a/M4 economic replay, root t_b4f455b0; scope t_2625be99):
  Housing.Build then farm/storage → craft → pack → load/drive → unload →
  sell/reward**, via M5.1 contract actions on a real server. Hard constraints:
  no direct Transform/ZoneId/GM/reflection/DB shortcuts; labor and mail payout
  conservation required (−60/pack, 124540/pack mail, SpecialtyManager);
  process-level restart suites re-run as-is (M4_2TradePackRestart,
  M4Vehicles, M3bExitPersistence). Phase 2's originally listed parent
  t_a69e4998 is blocked "do not redispatch" — the dependency re-points to
  t_b1d7c430 (done); the vehicle leg additionally depends on the new
  prerequisites t_a7756a00 (LoadPackOntoVehicle) + t_eaf1754d (DriveVehicle).
  **Scope LOCKED 2026-08-14 (marker t_2625be99 — all prerequisites now
  MERGED):** Housing.Build t_94761d55 @ 3396d9ef1 · LoadPackOntoVehicle
  t_a7756a00 @ 6c2429ae0 · DriveVehicle t_eaf1754d @ 6edbf0cbb · full M5.1
  surface on develop (Plant/PackPickup/PutDown/Buy-Sell/Deposit-Withdraw/
  Harvest/BoardVehicle/Craft). Replay route locked: **Housing.Build →
  farm/storage → craft → pack → load/drive vehicle → unload → sell →
  reward** — Housing.Build FIRST, before farm/storage. Requirements: real
  normal gameplay services + auditable traces only; labor (−60/pack) and
  mail payout (124540/pack, SpecialtyManager) conservation required;
  process-level restart evidence (M4_2TradePackRestart / M4Vehicles /
  M3bExitPersistence re-run as-is); no direct DB/GM/reflection/Transform
  shortcuts; H stays UNKNOWN (proxy/bot-functional, never H=2). Root card
  t_b4f455b0 dispatched 2026-08-14.
- **Prior shortcut rigs are SUPERSEDED for authentic acceptance — NOT erased.**
  M3a's in-memory actors/reflection/GM inventory/direct service calls and
  M4's direct zone/transform assignment + manual cargo attach remain visible
  as historical evidence (this table's "Original evidence" column + the M4
  EXIT RECORD); their grades stand as proxy/bot-functional until the
  authentic replay lands.
- **H stays UNKNOWN** in every dimension until Josh tests feel (human packet
  t_2b654349, Rei).
- No doc was silently rewritten: every change in this sync is an additive,
  dated annotation of this form.

**SCORECARD H rule (reconciled):** H = actual player only. Scripted-actor /
bot evidence is proxy/bot-functional (A dimension) and is NEVER recorded as
H=2. H stays UNKNOWN until Josh runs the curated scenario.

---

## M7 — Adventurer and party bots (Playerbots Alpha)

Split by archetype, not one universal mind.

- **Adventurer v1:** curated quest route, hostile targeting, fixed skill
  priority, distance maintenance, heal/retreat, loot, equip upgrades,
  return to quest NPC, death recovery
  *(2026-08-20 status: targeting/skill-priority/loot land in the spike;
  heal/retreat DONE (sustain loop: retreat + heal-item/regen + re-engage,
  fail-closed Starvation, rig E-M7-3/4/5); death recovery DONE (scheduler
  death watch); distance maintenance DONE (standoff band
  [StandoffMin, EngageRange]: close-in/back-off to the band edge, melee
  default unchanged, rig E-M7-6/7); equip upgrades DONE (Equip contract
  action + per-corpse upgrade evaluation in the spike hunt loop, rig
  E-M7-8); return to quest NPC DONE (spike is now the M7-worded chain:
  250 → 330 — travel to acceptor Npc 3597, accept, travel to report
  Npc 3511, real-packet turn-in, rig E-M7-10))*
- **Party v1:** invite/join, follow leader, rally, assist target, avoid
  extra pulls, tank/damage/healer roles, wait for missing members,
  resurrect, mount + travel together
  *(2026-08-21: invite/join DONE as contract actions — PartyInvite/
  PartyAccept on IGameplayActor through the real engine paths
  (TeamManager.AskToJoin target-object overload = the CSInviteToTeamPacket
  call; ReplyToJoinTeam = the CSReplyToJoinTeamPacket call). The engine's
  refusals are silent voids, so the contract pre-flights StateTransition
  and post-checks the observable outcomes (invitation record; InParty +
  team membership). Rig GameplayActorPartyTests 6 green. 2026-08-22:
  follow leader / assist target DONE as `PartyFollowAssistScenario` — a
  scenario composition over `MoveToUnit` and `SetTarget`, with real active
  party/owner/world pre-flights and fail-closed no-leader-target behavior;
  rig PartyFollowAssistScenarioRigTests 4 green, full gate 2163/0/1.
  Remaining: party spike — **DONE 2026-08-23 @ c98da8a53**:
  `PartySpikeScenario` (template m7-party-spike) ran a real 3-bot party
  through rally → assist → kill of elite NPC 1870 inside the leash window as
  LIVE E2E PASS over the generalized multi-actor bridge seam
  (`HandlePartyFollowAssistScenario` generalized to N actors), with causal
  cast-effect traces (ActorAuditRecord v2 additive fields
  target_hp_before/target_hp_after, effect_observed, effect_wait_ms).
  **Party v1 feature list COMPLETE.**)*

**Exit test:** one human + three bots complete the curated leveling route
and a selected group encounter.

**GATING SPIKE — DONE (2026-08-20, Josh-directed no-cards pass, branch
feat/m7-adventurer-spike):** one adventurer clears the Solzreed fox cull
(quest 250: accept doodad 5047 → kill 3× fox npc 3492 → auto-complete —
ids verified against Golden-Route-Solzreed.md + canonical compact.sqlite3)
end-to-end through the M5 contract: Accept → travel → hunt loop (Observe →
nearest attackable via CanAttack → SetTarget → burst Cast rotation) →
Loot → complete. `AdventurerSpikeScenario` (world-agnostic, M3aM4 options
pattern) dispatched as template `adventurer-spike-fox`. Rig: 4 TUnit tests
green (kill leg synthetic-but-real-credit per the pre-authorized rig
convention — bare rig NPCs cannot survive Npc.DoDie); generated trace
evidence scorecard-explorations/generated/m7-adventurer-spike.{jsonl,md}.
**E2E PASS 1/1 (2m15s)**: real board accept, real Move legs, 3/3 REAL fox
kills via DoDie→DoOnMonsterHuntEvents, 3 real corpse loots, quest 250
completed + dropped, 37 lifecycle-complete trace records (evidence
/root/aaemu-e2e/logs/m7-adventurer-spike-report.json). Full unit gate
2125/0/1. Spike surfaced four engine realities now on the record:
**BUG-016** (18131-class area+Target melee skills never hit their primary
target — **FIXED 2026-08-20**, census 415/13, 18131-led combo-chain rotation
is the live regression), weapon-less bots deal 0 damage (bridge
provisioning now applies starting equipment), NPC leash-reset demands burst
kills, mana starvation shapes rotation depth. **Known spike shortcuts
(recorded, not hidden):** spike bot provisions at level 50 (chain+contract
proof, not combat balance); Move is straight-line — no pathfinding; bot
death/resurrection — **BUILT 2026-08-20** (the spike's recorded top gap):
CharacterResurrection shares the CSResurrectCharacterPacket engine path and
the scheduler death watch stops work on dead bots, resurrects at the
nearest return portal after 5s, relocates server-side, and resumes work
(5 rig tests green). H stays UNKNOWN — Josh confirms feel.

**Cold-start fox follow-up (2026-08-22):** an isolated local E2E run with a
forced rebuild/cold start passed 1/1 in 2m57s. All three foxes took damage
and died; the trace had neither a `HUNT-SKIP` nor a return-home/full-HP reset.
Early unchanged HP samples are consistent with asynchronous effect scheduling,
not proof of a failed hit. The previously observed pinned-HP case is not
reproduced, its root cause remains UNKNOWN, and no speculative Npc AI change
is authorized by this evidence. A second local repeat stalled during harness
startup before producing a scenario report and is not gameplay evidence.

**Party spike — DONE (2026-08-23, c98da8a53):** `PartySpikeScenario`
(template m7-party-spike): one real party of 3 bots completes rally →
assist → kill of elite NPC 1870 inside the leash window — live E2E PASS.
The multi-actor bridge seam landed with it (`HandlePartyFollowAssistScenario`
generalized to N actors — forward-queue item 1), plus causal cast-effect
traces: ActorAuditRecord v2 additive fields target_hp_before/target_hp_after,
effect_observed, effect_wait_ms (forward-queue item 4 — delayed effects are
now distinguishable from failed hits). With this pass **Adventurer v1
(2026-08-20) AND Party v1 feature lists are both COMPLETE.** The M7 exit
(one human + three bots on the curated route + a selected group encounter)
remains open; H stays UNKNOWN — Josh confirms feel.

**Forward hardening queue (2026-08-22; non-blocking, ordered for M7→M8):**

1. **Party spike first** — one real party completes a selected group
   encounter; turn its follow/assist, rally, role, avoid-extra-pull,
   regroup, death-recovery, and travel legs into reusable scenario steps.
   `PartyFollowAssistScenario` is currently rig-only: give multi-actor
   scenarios an explicit `BotScenarioRunner` template/bridge execution seam
   before counting the feature as live E2E coverage.
   **DONE 2026-08-23 (c98da8a53):** bridge execution seam landed —
   `HandlePartyFollowAssistScenario` generalized to N actors;
   `PartySpikeScenario` (m7-party-spike) ran the real 3-bot rally→assist→
   kill as LIVE E2E PASS. Role/avoid-extra-pull/mount-together legs remain
   open scenario work.
2. **Party lifecycle fault matrix** — exercise leader/member death,
   disconnect, restart, world change, invitation retry, target death, and
   leash/return while preserving party membership, no duplicate work, and
   fail-closed regroup behavior. Multi-actor coordination is the first place
   where independently safe actor requests can still be unsafe together.
3. **E2E repeatability** — give the isolated stack bounded startup/shutdown,
   infrastructure-only retry classification, reliable cleanup, and a single
   per-run artifact bundle. A harness startup stall is never gameplay proof.
4. **Causal traces** — standardize action accepted → effect observed → target
   state change → bounded timeout/failure reason. This distinguishes delayed
   effects from failed hits and makes a recurrence of the fox issue diagnosable.
   **DONE 2026-08-23 (c98da8a53):** ActorAuditRecord v2 additive fields
   target_hp_before/target_hp_after + effect_observed/effect_wait_ms give
   exactly that chain — cast accepted → effect observed within a bounded
   wait, with the target HP delta on the record.
5. **Group movement and navigation** — replace curated straight-line movement
   workarounds with terrain/obstacle-aware movement, formation cohesion, and
   explicit stuck recovery before dungeon-scale party scenarios.
6. **Npc state telemetry** — log/trace aggro transitions, return-home entry,
   healing, and target changes; investigate a reproduced pinned-HP case only,
   never by speculative AI behavior changes.
7. **Scheduler and recovery proof** — run a long mixed-behavior scheduler
   soak and a party-mid-route restart/resume E2E before M8 increases embodied
   bot counts. Scheduler behavior is currently covered by deterministic rigs;
   the E2E must assert the live bridge metrics and actual wake/lease behavior.
   **Progress 2026-08-24 (4e460305b):** scheduler-driven soak STAGE 1
   executed — SchedulerSoakStage1Tests, 10 manifest citizens × 30min through
   real IPlayerBotScheduler wakes; two valid runs ~90k steps, 0 failed/
   timed-out, wake avg ~99ms, DB writes 14–19/min/citizen, tick+region budgets
   PASS; three open engine findings (home-spawn roster gap, physics slow-thread
   ~3× scheduler-disabled baseline, heap churn ~5.9GB under roam). Staged
   ladder continues — 1h/6h rungs, party-mid-route restart leg, and the M6-exit
   physics-recalibration decision remain open.
   **Run 3 on the fixed build (2026-08-24, 2703fd46e):** findings (a) and (c)
   FIXED — home divergence closed @ 2703fd46e (all 10 citizens stayed within
   ~40 units of home, zero resurrections) and roam heap churn cut 38%/wake
   @ 615a645c9 (RSS plateau ~5.5GB → GC reclaim to 3.7GB, no monotonic growth).
   Budgets: ALL PASS except physics warnings 0.17/min vs the 0.1/min budget —
   same-world clause at 2/30 (15× headroom), worst pass 110ms vs 40ms target,
   n=5 events (Poisson σ≈45%). **OPEN JOSH DECISION:** recalibrate the
   physics-warning per-minute aggregate (~0.3/min, or severity+same-world-only
   clause) per the t_18fccd09 precedent — at 0.1/min the gate flakes on host
   jitter noise while every severity signal sits far inside its limit.
   Evidence: scheduler-soak-stage1-20260824-160357.{json,md} +
   scheduler-soak-stage1-run3-budget-fail-report-20260824-161836.md.
8. **Coverage ledger and fault injection** — maintain a small action/system
   matrix (rig, isolated E2E, integrated route, human-feel status) and add
   controlled disconnect, server restart, delayed-effect, and persistence
   fault cases to the reusable harness.
9. **C2 social v1 — DONE 2026-08-23 (8c198f13d):** BotChatterService, 8
   archetypes × 4 canned lines, cooldowns/budgets/combat-suppressed, default
   OFF via Bots.EnableChatter / AAEMU_BOT_CHATTER_ENABLED (G4/C2 + M8.5a).
10. **Movement stuck detection — DONE 2026-08-23 (8c198f13d):**
   NoProgressWindow 2.5s → TimedOut(Navigation) "stuck" + one unstick nudge
   (the stuck-recovery slice of queue item 5; terrain-aware group movement
   itself stays open).
11. **C1 schedules v1 — DONE 2026-08-23 (62f13fdc7):** BotScheduleService
   Home/Work/Travel/Rest phase machine with hysteresis, persisted additively
   inside the schedule JSON blob (B4 restart byte-equality preserved),
   default OFF via Bots.EnableSchedules / AAEMU_BOT_SCHEDULES_ENABLED.
12. **Economy day-cycle v0 — DONE 2026-08-23 (62f13fdc7,
   m8-economy-cycle-v0):** buy seed → plant → harvest → craft → sell →
   deposit with explicit ledger + reconciliation laws; live E2E incl. kill -9
   restart ledger-equality PASS. Hauler leg added same day (6b2f15a6d):
   pack craft → LoadPackOntoVehicle → DriveVehicle → gold trader → deposit.

These are test-platform investments, not a claim that bots can replace the
Josh-owned human-feel gates; client feel, visual correctness, and balance
remain human acceptance work.

**Next-wave execution specs (2026-08-25; integrated development-loop
priorities — excludes the three already-delegated slices: PB-002
quest-discovery, doodad-interact contract action, A5 acceptance run):**
  **UPDATE 2026-08-25 (wave 4):** PB-002 extended beyond discovery — quest
  offer CHANNELS v2 + Talk landed on branch `bots/quest-surface` (SHAs in
  STATUS): ~801 previously-hidden quests now perceivable via Item (342+25),
  Sphere (431, geometry via `GetQuestStartingSpheres`) and Level (3) channels
  plus DiscoverSelfQuests; the ConAcceptComponent channel is deliberately
  DEFERRED (stub true-return, no player-perceivable precondition). Talk
  contract action (Talk = 46) fires the real DoTalkMadeEvents pipeline with
  fail-closed pre/post-checks. Hunt-leg leveling extension on branch
  `bots/kill-leg` (MonsterHunt/MonsterGroupHunt pursuit + cast-burst).

1. **PB-001 navigation strategy dossier + coarse-travel slice design**
   · Owner-role: evidence scout + systems designer (docs/design first, no
   engine code in this task) · Area: BOT + SERVER navigation · Priority: HIGH
   (blocker ledger outranks features) · Depends on: nothing · Milestone:
   feeds M8 (G4 C3/C4 travel legs) and the indun/party loops.
   *Goal:* pick the waypoint-network vs coarse-route-graph strategy from
   evidence before any engine work. *Work:* archaeology dossier
   `scorecard-explorations/mechanics/navigation-domain.md` — what canonical
   1.2 data exists (NPC paths/waypoints in compact.sqlite3? client-side nav
   data?), how neighboring movement code resolves terrain; grade
   VERIFIED/INFERRED/UNKNOWN; write the behavioral contract and size the
   vertical slice (one cross-region leg, travel-as-progress for background
   bots per the fidelity ladder). *Acceptance criteria:* dossier exists with
   grades; strategy decision recorded with evidence citations; a sized slice
   plan a follow-up card can execute. *Outputs:* dossier + ROADMAP lane
   annotation. *Follow-up unlocked:* dungeon interiors (PB-001), cross-region
   caravans, believable background travel.
  **DONE 2026-08-25 — premise refuted (data always existed); exit E2E PASS
  11/11; see playerbot-blockers.md PB-003 FIXED.** Original spec preserved
  below:
2. **PB-003 Hadir Farm exit-portal SQL patch candidate** · Owner-role: data
   archivist · Area: DATA (read-only-reference overlay patch) · Priority:
   MEDIUM · Depends on: canonical verification vs reference client data ·
   Milestone: closes an indun-loop gap found by the party spike.
   *Goal:* give cleared dungeon parties a way out. *Work:* per the blockers
   ledger (PB-003: zone 46 ships no exit doodad spawn data, 4289/4927
   absent, no indun_events), verify absence against REFERENCE CLIENT DATA
   FIRST; sibling-dungeon exit-doodad pattern mining is the fallback ONLY if
   the reference data lacks zone 46, and any such inference is graded
   INFERRED (never VERIFIED). Then author the overlay SQL patch +
   rig/E2E asserting post-clear exit returns the party to the main world.
   *Acceptance criteria:* patch applied to the E2E stack only (compact.sqlite3
   stays READ-ONLY); party-clear-then-exit E2E PASS; blockers ledger PB-003 →
   FIXED with evidence. *Outputs:* SQL patch + `indun-domain.md` addendum +
   ledger status flip. *Follow-up unlocked:* repeatable dungeon loop.
3. **A4 acceptance measurement — autosave p95 @ 250 characters** · Owner-role:
   perf/validation engineer · Area: persistence (G2-A4 gate) · Priority: HIGH
   (milestone-gate item) · Depends on: existing scaling-probe harness ·
   Milestone: G2-A4. *Goal:* record or fail the explicit pending gate
   (autosave p95 < 2s @ 250 characters). *Baseline:* M3b measured 1301ms p95
   at 25 bots + 2 homesteads; dirty-only periodic saves merged 5ed5d6493.
   *Hypothesis:* dirty-only tracking holds p95 < 2s at 250 characters with ≥
   30% headroom [INFERRED]. *Metric:* autosave p95 via SaveDurationMetrics +
   `_isSaving` skip count. *Expected improvement:* gate MET at scale.
   *Regression checks:* M3bExitPersistence re-run as-is; soak budgets hold.
   *Work/Acceptance:* extend ScalingProbeTests-style probe to 250 characters,
   sample across a soak window, annotate G2-A4 with MET/FAILED + numbers.
   *Outputs:* probe report JSON + G2-A4 gate annotation. *Follow-up
   unlocked:* Gate G1's "250 staged" rung.
4. **A3 remainder — incremental counters, staggered cadences, wake-storm
   probe** · Owner-role: server perf engineer · Area: scheduler/fidelity
   machinery (G2-A3) · Priority: MEDIUM · Depends on: proximity-fidelity
   sweep (d6cabcfd4) + true dormancy slice (e672b9579) · Milestone: G2-A3.
   *Goal:* meet the A3 acceptance — 1,000-bot wake-storm transition p99 <
   100ms. *Baseline:* RefreshPressure driven once/sweep;
   ScanEmbodiedInZone budgeted O(cap); ONE global scheduler scan cadence
   (100ms) shared by all bots. *Hypothesis:* incremental per-zone/activity
   counters plus staggered per-bot wake offsets remove synchronized spikes
   and keep transition p99 under bar without behavior change. *Metric:*
   wake-storm transition p99, sweep wall time. *Expected improvement:*
   synchronized-cadence spikes eliminated pre-scale. *Regression checks:*
   SchedulerSoakStage1Tests budgets PASS unchanged; proximity-tier rig green.
   *Work/Acceptance:* implement counters + stagger behind the existing
   default-OFF gates; run a 1,000-registered-dormant storm probe; annotate
   A3 with the number. *Outputs:* PopulationDirector/scheduler changes +
   probe report. *Follow-up unlocked:* Gate G1 50→100 profiling rungs.
5. **Allocation wave 2 — A2 broadcast-economics numbers** · Owner-role:
   server perf engineer · Area: broadcast/GC hot path (G2-A2, still open) ·
   Priority: MEDIUM · Depends on: A4 measurement (shares the probe harness)
   · Milestone: G2-A2. *Goal:* measure, then meet, the A2 acceptance (100
   bots / 0 humans ⇒ zero bot-originated packets; gen0 GC < 1/min).
   *Baseline:* roam heap churn cut 38%/wake by the BroadcastMovement opt-out
   (615a645c9; RSS plateau ~5.5GB → GC reclaim ~3.7GB); Region.GetList array
   copies + allocation-free GetAround overload still on the table.
   *Hypothesis:* remaining churn concentrates in per-wake allocations
   (audit records, observation snapshots); humans-nearby short-circuit +
   allocation-free GetAround close most of it. *Metric:* gen0 GC/min, bytes
   allocated/wake, RSS band. *Expected improvement:* A2 acceptance met at
   100 bots. *Regression checks:* all seven bot-regression scenarios stay
   green (movement packets must still reach real clients); B5 leakage audit
   confirms the opt-out never touches player-visible sessions. *Work/
   Acceptance:* profile first (optimize-in-waves discipline), implement the
   two named seams, record before/after numbers in the G2-A2 entry.
   *Outputs:* profiling note + WorldManager changes + gate annotation.
   *Follow-up unlocked:* 100-bot village runs inside budget.
6. **Behavioral scenario library + PLAYER_MODE/TEST_MODE leakage audit**
   (= G3-B5) · ✅ **DONE 2026-08-26** (branch feat/b5-scenario-library @
   46fe4332d): scenario library promoted
   (scorecard-explorations/generated/b5-scenario-library-2026-08-26.md — 7
   scenarios indexed with contracts/failure-attribution; the executable index
   remains `Scripts/e2e/bot-regression-pass.sh`) + leakage audit ALL THREE
   seams PROVEN-UNREACHABLE with 6 negative regression tests (bridge gated +
   loopback + private dispatch; BroadcastMovement opt-out confined to the
   roam executor with observer stream preserved; rig hooks compile-time
   isolated). Owner-role: test-platform engineer · Area: validation
   infrastructure · Priority: MEDIUM · Depends on: none · Milestone: G3-B5 /
   development-loop rule 5. *Goal:* make the regression-validation stage of
   the integrated loop a registry lookup instead of tribal knowledge.
   *Work:* index the seven live scenarios from
   `Scripts/e2e/bot-regression-pass.sh` into a library doc (contract,
   inputs, observable outcomes, layer-tagged failure attribution,
   capability-matrix row links); enumerate every test-only seam (bridge
   metrics, headless-roam BroadcastMovement opt-out, rig seed hooks) with
   its gate and a negative test proving unreachability from player sessions
   and autonomy. *Acceptance criteria:* library doc exists and every matrix
   row maps to ≥0 scenarios with gaps named; each seam has a gate citation
   or a filed negative-test task. *Outputs:* scenario-library doc + audit
   section. *Follow-up unlocked:* new systems register regression coverage
   as part of their vertical slice (loop stage 6 becomes mechanical).

7. **Justice slice-1 — crime-points vertical** · Area: JUSTICE (CRIME-01) ·
   Priority: HIGH · Depends on: nothing (implementation exists; pure
   verification vertical per justice-domain.md slice plan) · Milestone: M9
   lane. *Goal:* first live proof of evidence → points → persistence.
   *Work/Acceptance:* bot A kills same-faction bot B unprovoked → assert
   large-bloodstain doodad spawns (Owner=A/Data=B); CSReportCrimePacket seam →
   CrimePoint/InfamyPoint rise + SCCrimeChanged emitted + MySQL `crime` row
   survives restart; Wanted buff appears at the 50-point boundary (inject via
   CrimeAddPointSubCommand). Client report-dialog rendering stays UNKNOWN.
8. **PvP slice-1 — flagged-aggression handshake live E2E** · Area: PVP
   (PVP-01) · Priority: HIGH · **STATUS 2026-08-26: OPEN, narrowed** after
   recovery. Targeted rig PASS 1/1 (real `Skill.Use`, same-faction
   `ForceAttack` HP decrease, Retribution present; first application and
   Refresh broadcasts); live non-immune damage-frame proof remains pending.
   *Next acceptance:* prove flag → aggress → peace-refusal → honor on the real
   server; no grade promotion from rig-only evidence.
9. **Mail security + S3 persistence slice** · Area: MAIL (MAIL-01, SECURITY
   priority) · Depends on: none · **✅ LANDED 2026-08-26 in
   `31045d033`**. Ownership guards now protect the receive paths that accept a
   client-supplied mail id; owner flows remain intact. **Mail S3 acceptance:**
   `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
   PASS 1/1 in 2m39s on isolated MySQL/Docker with real authenticated packets:
   `CSSendMailPacket` near a mailbox; process kill-9/restart; persisted
   `SlotType.Mail=5`; receiver ownership retargeting; unread count 1 after
   registration; `CSListMail`/`CSReadMail`; `CSTakeAttachmentSequentially`;
   exact equipment item-instance detail/grade/durability/rune/temper fidelity;
   copper transfer; read transition to 0; and `CSDeleteMail` persistence
   deletion. The root-cause fix moves `Character.Load` unread recount after
   `TryAddCharacter` and before human client initialization. **Follow-ups:**
   return opcode `0x0a2` remains STRONGLY_INFERRED pending real-client capture;
   COD enforcement and expiry/bounce E2E remain open.
10. **Ships slice-1 — rowboat E2E** · Area: SLAVE-01 naval half (ships-domain
    Slice 1) · Priority: MEDIUM · Depends on: indun/party bridge charPos +
    packet-tap seams. *Goal:* first live proof of sailing physics + lifecycle:
    summon slave 15 → water-depth spawn assert → bind driver → inject
    CSMoveUnitPacket ShipRequestMoveType throttle/steer → observe
    SCOneUnitMovementPacket stream + displacement over T → steer reversal
    flips heading sign → UnbindSlave → despawn clean (no leaked RigidBody).
11. **Dominion slice-1 — persistence vertical (zero combat)** · ✅
    **SLICE-1 LANDED 2026-08-26** (branch d42e708f5→66f124533): persistence/
    schedule/tax live — DominionManager loads siege_zones(6)/siege_settings(11)/
    siege_plans(158); additive MySQL `aaemu_game.dominions`;
    CSUpdateDominionTaxRatePacket round-trip; phase cron announcing via
    SCSiegeAlertPacket; kill -9 persistence E2E PASS. Combat/siege-battle is
    explicitly deferred to later slices; declare-trigger UI path still UNKNOWN.
    Area: DOMINION (siege domain zero-wired; dominion-domain Slice 1) ·
    Priority: MEDIUM · Depends on: none. *Work:* new DominionManager loading
    siege_zones/settings/plans; additive MySQL `aaemu_game.dominions`; wire
    CSUpdateDominionTaxRate → owner-gated store → SCDominionTaxRate echo;
    TickManager phase cron (Peace/Declare/Warmup/Siege/Payoff) announcing via
    a new SCSiegeAlertPacket marshaler. *Acceptance:* declared dominion
    survives game-server restart; tax-rate change round-trips C2G→store→G2C.
12. **Merchant bug-fix trio** · Area: ECONOMY (MERCHANT-01) · ✅ **LANDED
    2026-08-26** in merge `e5db6d390`: funds gate `cb514c42e`, buyback refund
    `beaf9b82e`, and grant-failure rollback `3ba33b3af`. Rig tests now refuse
    insolvent buys without changing money, refuse full-bag/late-grant purchases
    atomically, and pay no buyback refund when the move is refused. The live
    `EconomyDayCycleE2eTests` conservation run passed across kill -9 restart.
    MERCHANT-01 W/A fixes are merged; H remains UNKNOWN.
13. **Labor regen tick decision — schedule or delete** · Area: LABOR-01
    (economy-domain LAB-A) · Priority: MEDIUM (decision card). *Fact on
    record:* TimedRewardsManager.Initialize has NO caller anywhere — online
    regen is dead-by-default; offline AddOfflineLabor IS called; shipped
    configs define no Labor section, so even scheduled default regen would be
    0/min. *Work:* owner decision recorded; if scheduled: integration test
    shows +TickAmount after TickMinutes with cap clamp at 2000/5000; if
    deleted: remove task + config stubs (clean cutover).

**Recovery queue status (2026-08-27; develop @ `1638b007c`):**

- **PB-005:** **FIXED-PARTIAL** after `38c4997d3` — positive clamp and
  intentional-floater whitelist landed; cave/deck/submerged classification and
  duplicate-row decisions remain open.
- **PB-007:** **OPEN, narrowed** after `a4f7820ba` — targeted rig PASS 1/1
  (real `Skill.Use`, same-faction `ForceAttack` HP decrease, Retribution
  present; first application and Refresh broadcasts); live non-immune
  damage-frame proof remains pending.
- **MCP expansion:** commit `1638b007c` adds authenticated
  `POST /api/actors/discover_quests`, `/discover_self_quests`, `/interact_with`,
  `/talk`, and `/equip` routes with matching MCP tools; the catalog is now 24
  tools. Focused route/contract/queue validation passed 2/2, 33/33, and 16/16;
  MCP projects Release build clean; stdio smoke 24 tools; full gate **2486
  total / 2485 succeeded / 0 failed / 1 skipped**. The live
  `discover_self_quests` benchmark passed with `action_status`, `trace`, and
  an independent MySQL character-row cross-check. No safe doodad interaction
  was attempted. The asset-missing `7e109d550` smoke remains historical.
  Plant, Harvest, Craft, party, trade, expeditions, and related newer actions
  still lack routes and remain explicitly deferred.
- **Mail S3:** **PASS / LANDED** in `31045d033` — authenticated
  `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
  passed 1/1 in 2m39s on isolated MySQL/Docker; the restart, instance-faithful
  attachment, ownership, unread-recount, take, and delete assertions passed.
  Return opcode `0x0a2` still needs real-client capture; COD and expiry/bounce
  remain follow-ups.


**Low-lift first moves (2026-08-22; use existing seams):**

- Add a small isolated-stack Party follow/assist smoke that uses the live
  bridge and asserts real team membership, member position, and copied target.
  This is the first consumer of the multi-actor execution seam, not a new
  party framework.
- Add a scheduler smoke through the existing bridge statistics: wake a managed
  bot, assert one real step/lease completion and bounded wake latency, then
  capture the existing scheduler metrics in the report.
- Standardize every E2E report's small manifest from data already available:
  scenario inputs, cold/warm mode, runtime `compact.sqlite3` MD5, server
  process/log tails, and the action/criterion trace. This makes flakes
  comparable without building an observability platform first.
- Promote the existing stack boot/isolation checks and restart helpers into a
  reusable preflight for new E2Es, rather than duplicating startup assumptions
  in each scenario.
- For a new failure class, land the smallest deterministic rig reproduction
  alongside the E2E artifact before broadening retries or changing engine
  behavior. The fox investigation is the model for this discipline.

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

> Process checklist (below) + the per-milestone Requirements & DoD evidence
> classes from "Milestone Requirements & DoD — THE STANDARD" above (retrofit
> 2026-08-14). A milestone is CLOSED only when both layers hold: this
> checklist AND every required DoD evidence class.

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
  **Progress 2026-08-25 (d6cabcfd4): RefreshPressure DRIVEN** — proximity-
  fidelity sweep runs it once per interval; human-proximity tier ladder
  (Full ≤75m / Reduced ≤200m / Dormant beyond, 2-sweep hysteresis, safety
  gate respected, Wake() re-arms stepping) landed behind
  Bots.EnableProximityFidelity (default OFF).
  **A3 REMAINDER EXECUTED 2026-08-25 (same day, server-perf wave):**
  (1) **Incremental per-zone/activity counters REJECTED as speculative** —
  the new sweep-wall-time ring shows the entire O(dormant-specs) scan pass
  costs p50 ≈ 0.066 ms/sweep at population scale, and sweep p95 is pure
  budget-paced materialization work (≈3 × ~250 ms DB row-load); density scans
  short-circuit when caps are unset (-1 default). No counter work exists to
  incrementally maintain — evidence over hypothesis.
  (2) **Staggered per-bot wake offsets LANDED** behind
  `Bots.EnableStaggeredWakes` / `AAEMU_BOT_STAGGERED_WAKES` (default OFF,
  byte-identical when unset): first step of a freshly materialized bot is
  scheduled at a deterministic SplitMix32 phase within
  `StaggeredWakeWindowMs` (default 5 s) instead of synchronizing onto one
  scan cadence. Unit-proved deterministic + spreading + deferred-first-step.
  (3) **Event-driven human-proximity wake DEFERRED** with rationale
  (no WorldManager movement/enter event seam exists; an off-tick sweep would
  run ~250 ms world-mutating materializations on connection threads; the
  measured detection-latency bound is just the 2 s sweep cadence and does not
  affect the transition-cost acceptance) — see g2-a3-storm-report.md §6.
  (4) **Wake-storm probe LANDED** (`A3StormProbeTests`, real-TCP-human
  trigger, 1,000 seeded dormant in a Reduced-tier annulus): transition
  latency ring added to the director (count/p50/p95/p99/max exposed via
  bridge `population.transitions`). Numbers: see §4.2 of
  scorecard-explorations/generated/g2-a3-storm-report.md.
  (5) **Boot race FIXED en route** (`IdManager.GetNextId` lazy-init guard):
  ItemContainer's ctor allocation through `ContainerIdManager.Instance`
  raced Stage-2 Load — flaky at 40 chars (§10.2 of the A5 report),
  reproducible at 1,000 seeded characters; now initializes on first use
  instead of NRE-ing the boot.
- A4 (M) Save scalability: per-character dirty tracking + batching.
  Acceptance: autosave p95 < 2s at 250 characters; zero _isSaving skips.
  ✅ implementation merged 5ed5d6493 (2026-08-10, t_8c18eb1c, Rei gate
  t_53025996 ACCEPT — dirty-only periodic saves, force-all on shutdown + /save);
  ✅ **GATE MET 2026-08-25: autosave p95 393.1ms @ 250 active (80.3%
  headroom), 0 skips — report §9**
- A5 (L) TRUE DORMANCY — the pivotal item: Dormant = DB row + metadata only,
  no Character materialized, no region presence, no per-second tick.
  ✅ **Vertical slice LANDED 2026-08-25 (e672b9579):** dormant registry +
  materialize/dematerialize through the real lifecycle, proximity-budgeted,
  default OFF via `AAEMU_BOT_TRUE_DORMANCY` (`DormantBotRegistry`; DI wiring
  in Program.cs). **Acceptance gates PENDING:**
  - NEAR-TERM GATE (official — verbatim from the owner's 2026-08-25
    handoff): RSS within 15% of the no-bot baseline AND
    materialize-to-visible p95 < 3s, at ~100 dormant registered / ~10
    embodied (ScalingProbeTests rerun with the flag ON);
    ✅ MET 2026-08-25: RSS +2.09%, materialize p95 260.1ms post-PB-004-fix,
    100 dormant/10 embodied real-path proven — report §8/§10
  - **PB-004 discovered-by-measurement and fixed same day (2026-08-25,
    6ba363a28):** materialized dormant bots never stepped (no Wake() +
    dormancy-only boot skipped scheduler start); post-fix 3001 steps/min
    with 10 embodied, dematerialize-on-leave clean.
  - FINAL Tier-3 acceptance: 1,000 registered / ≤50 embodied,
    RSS within 15% of the 50-only baseline; wake-to-visible p95 < 3s;
    dormant timers advance over 6h (Tier 3 = DB-driven scheduled simulation:
    harvest/travel timers advance while nobody is embodied).
    ✅ **SHAPE MEASURED 2026-08-26 (g2-a5-acceptance-report.md §11,
    worktree .worktrees/tier3 @ 214bed834):** 1,000 dormant seeded through
    the REAL provisioning path (~4.1 min sequential) / exactly 50 embodied;
    RSS Δ = **+0.13 %** vs the 50-active baseline (3832.1 → 3837.0 MB
    median) — trivially inside 15%; wake-to-visible p95 = **280.2 ms**
    (p50 220.1 / p99 474.8 ms) — 10.7× under target; steps/min parity
    15003 vs 14995 (proximity-materialized bots DO step post-PB-004 fix);
    tick p95 parity 0.8 ms both arms. **PENDING:** the 6h dormant-timers
    soak leg (scheduled). Documented hazard: CONCURRENT seedDormant corrupts
    server state after ~100 bots (non-concurrent collection) — seeding stays
    sequential (report §11.2).
- A6 (M) Manifest-driven mass provisioning (citizen manifest as data;
  replaces hardcoded CitizenNN + 10-bot clamp). Acceptance: cold boot →
  100 citizens on schedule < 60s. **Note 2026-08-24 (4e460305b):** soak
  STAGE 1 ran 10 manifest-provisioned citizens through real scheduler wakes —
  finding: roster entries without home spawn start at race-template position
  and walk to the patrol-default Nuian home (run-1 elves walked 4.3km and
  drowned); home-spawn requirement on the record for future manifests.
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
  **DONE 2026-08-24 (0482ba3f0):** IBotActivityModule + BotGoalArbiter landed —
  priority-based single-active activity per bot per wake (schedule-phase P100 /
  presence-roam P50 / idle P0 first modules via IBotStepExecutor decorator);
  dead PlayerBotBehaviorController stack deleted; fixed latent DI gap so
  Bots.EnableSchedules actually arms BotScheduleService.
- B4 (S-M) playerbot_metadata store (personality, schedule, profession, home,
  planner state) + audit-trace flush — **DONE 2026-08-20**:
  `PlayerBotMetadataStore` + presence-demo wiring + 2-checkpoint restart
  replay asserting metadata directly (PASS), and the audit half —
  `PlayerBotAuditSink` buffers terminal BotActionCommandQueue audit records
  (bounded, drop-oldest, in-memory append on the boundary thread only) and
  batch-flushes to `playerbot_audit` inside the SaveManager transaction
  (5 hermetic tests).
- B5 (S) Behavioral scenario library + mode-leakage audit: the seven live
  scenarios in `Scripts/e2e/bot-regression-pass.sh` (goldenroute, economy,
  fishing, duels, transfers, packrestart, partyspike) exist only as a shell
  registry — promote each into an indexed scenario entry (behavioral
  contract, inputs, observable outcomes, layer-tagged failure attribution,
  capability-matrix row link) so the integrated loop's regression-validation
  stage registers new systems for free. Same pass: audit that test-only seams
  (bridge metrics surface, headless-roam BroadcastMovement opt-out, rig seed
  hooks) are provably unreachable from player-visible sessions and never feed
  autonomous bot decisions (development-loop rule 5, PLAYER_MODE vs
  TEST_MODE).
  Acceptance includes: a **BroadcastMovement opt-out negative test proving
  player-visible sessions are unaffected** — required before ANY default-ON
  flip of proximity fidelity or true dormancy.

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
