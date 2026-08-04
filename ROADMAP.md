# ArcheAge Slums — Roadmap & Milestones (v2, reshaped 2026-08-03)

> **🚫 THE RULE (Josh, permanent): NEVER push a PR to upstream AAEmu/AAEmu
> unless Josh explicitly approves it.** Everything stays in our own lane.
>
> **THE CORE SHIFT (Codex review, endorsed):** Do not finish AAEmu before
> building playerbots. Build ONE dependable classic-ArcheAge life loop, make
> bots master that slice, and expand outward together. Bots continuously
> expose real server defects while the world comes alive.

## Three phases

1. **Playable Classic Loop** (M1-M4) — a dependable slice humans can enjoy
2. **Bot-Compatible Game Platform** (M5-M6) — the observation/action boundary
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

**Exit test (human + automated):** new character enters world, completes
curated opening chain, gains levels, receives rewards, logs out and
continues, reaches first-mount prerequisite. Automated: scripted actor runs
the same chain via the golden path.

---

## M2 — Golden-path playtest harness

The repeatable playable journey — the largest missing piece. Mostly test
tooling + documentation; parallelizable with M3 prep (we already have
nightly backups, test accounts, the box).

**Golden path:** create character → starter progression → unlock mount →
acquire farm → plant & harvest → build house → craft trade pack → transport
pack → sell → return home.

**Deliverables:** selected race/faction, zones, approved quest chain, skill
builds, mount, one housing zone, one crop chain, one crafting chain, one
trade-pack recipe, one land route, one cart/hauler, one short sea route (if
ships viable). Artifacts: human playtest checklist, known-blocker doc,
save/restart checkpoints, structured event log, repeatable DB snapshot.

**Exit test:** four humans complete the loop twice including one server
restart. **This is the first real playable release.**

---

## M3 — Homestead integrity (split internally M3a/M3b)

Housing + property + farming as the dependable homestead loop.

- **M3a shell:** decoration-limit enforcement, placement-zone validation,
  housing-group/UI data, ownership + permission validation, construction
  state, taxes + tax mail, demolition/reclaim safeguards
- **M3b persistence:** bound doodad persistence, door/window phase
  persistence, furniture placement/pickup, containers/storage, rotation/
  attachment, crop placement, growth timers, harvesting, livestock (where
  stable), restart recovery, no duplicated/orphaned doodads

**Exit test:** two players maintain adjacent farms + houses over THREE
server restarts without losing/duplicating/relocating property.

**Scorecard targets (bounded — not gold-plating):** housing ~75-85% usable
for curated loop, farming ~70-80%, property persistence green for tested
items.

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

## M5 — Bot-compatible action and observation layer

**NOT autonomous bots yet — the boundary first.** Largely wraps EXISTING
capabilities (NpcAi move/target/cast/interact primitives + admin commands)
into a clean contract + lifecycle states. Smaller than greenfield.

**Observation snapshot:** position/movement, health/mana/buffs, target,
nearby units + interactables, inventory, equipment, skills/cooldowns,
quests/objectives, mount state, property ownership, party state, current
action, last failure, stuck status.

**Action interface:** Move, Stop, Follow, Target, Cast, Interact, Loot,
UseItem, EquipItem, AcceptQuest, CompleteQuest, SummonMount, Mount/
Dismount, Plant, Harvest, Craft, PackPickup/PutDown, Board/LeaveVehicle,
Buy/Sell, Deposit/Withdraw.

**Lifecycle:** every action returns Requested → Accepted → Running →
Completed | Rejected(reason) | Interrupted(reason) | TimedOut.

**Architectural rule:** invokes normal gameplay services only.

**Exit test:** admin manually instructs one controlled actor to complete
every golden-path primitive.

---

## M6 — Deterministic playerbot framework

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

**Exit test:** 10 bots run 6 hours with no unrecovered loops, no inventory
duplication, no runaway combat, no DB corruption, no tick-budget overrun.

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
| Week 3 | M2 golden-path harness |
| Weeks 4-5 | M3 homestead integrity |
| Weeks 6-7 | M4 trade and transport |
| Weeks 8-9 | M5 bot action/observation layer |
| Weeks 10-12 | M6 deterministic bot framework |
| Weeks 13-15 | M7 adventurer and party bots |
| Weeks 16-19 | M8 Living Village |
| Later | M9 activities, M10 territory/siege |

Dates are directional — quest data or vehicle attachment may reveal deeper
work.

## Definition of done per milestone

- [ ] Human scenario, automated scenario, AND restart-persistence scenario
      defined and passing
- [ ] Every task: branch, commits, tests (fail-before/pass-after), Rei signoff
- [ ] Full local gate green: Release build + compiler-check + all tests
- [ ] Both scorecards updated in-branch (technical wiring + experience)
- [ ] STATUS.md reflects the milestone (Nei)
- [ ] Deployed to aaemu box (Mai) + sanity-checked in-game where possible
- [ ] No upstream PR without Josh's explicit approval
