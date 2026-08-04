# ArcheAge Slums — Roadmap & Milestones (2026-08-03)

> **🚫 THE RULE (Josh, permanent): NEVER push a PR to upstream AAEmu/AAEmu
> unless Josh explicitly approves it.** Everything stays in our own lane.
>
> Two tracks: Track 1 = canonical 1.2 fixes (primary, learn first). Track 2 =
> playerbots/LLM/economy (bonus, after Track 1 momentum).
> Division routing on every task: Tai builds → Rei verifies → Nei tracks,
> Mai dispatches/deploys.

## Guiding principles

1. **Canonical first.** Ground every change in 1.2 data (live sqlite) + docs.
   If data and code disagree, the data wins.
2. **Small, shippable slices.** Every milestone is a set of focused tasks,
   each with its own branch, tests, scorecard update. No mega-branches.
3. **Scorecard is the map.** A domain's % is the definition of done — every
   milestone moves at least one scorecard row.
4. **Bots inherit correctness.** Track 2 bots need quests/housing/trade to
   WORK before bots can USE them. That's why Track 1 comes first.
5. **Continuous upstream pulls.** Keep the fork current (`git pull upstream
   develop`) — our lane stays mergeable, our knowledge stays fresh.

---

## MILESTONE 0 — Foundation (status: ~DONE)

Workflow v3 (lane gate + tracking discipline), COMMUNITY-GUIDELINES (Nei),
kanban templates (Nei in progress), gate.sh verified, SCORECARD.md + 3
exploration reports, graphify graph (17.5k nodes), VISION.md division routing.

Remaining: Nei's template v2 set + STATUS.md convention (task t_7aa85a0f).
Acceptance: templates used by first real task.

---

## MILESTONE 1 — Quest engine correctness (Track 1, highest impact)

**Why first:** 30+ upstream quest bugs; the kill-acceptor bug alone blocks
~40 quests. Quests are the spine of the game — bots (Track 2) can't quest
if quests don't work. Also the cleanest "canonical fix" story we have.

| # | Task | Size | Scorecard effect | Evidence source |
|---|------|------|------------------|-----------------|
| 1.1 | **Kill-acceptor quest fix** (QuestActConAcceptNpcKill copy-paste bug: add QuestAcceptorType.Kill, wire Npc death path, fix RunAct) | S-M | quests 82%→84%, unlocks ~40 quests | quests.md §3 (already queued: t_71e48494) |
| 1.2 | **Load quest_act_obj_aliases** (2,746 rows dangling; add loader block, resolve use_alias FKs) | S | quests data-wired 82%→84% | quests.md §2, §4.2 |
| 1.3 | **Stub-act audit** (UnusedActs: must-return-false vs functional decision per act) | M | prevents silent auto-complete/stall | quests.md §4.3 |
| 1.4 | **Quest sanity verifier** (startup cross-check: act_detail_type vs class registry, detail-ids vs loaded tables) | M | ops safety net for all future quest work | quests.md §4.5 |
| 1.5 | **Doodad phase/interaction objectives** (QuestActObjInteraction wi_id+phase TODOs, QuestActObjItemUse gating; fixes quests 922/3889/3447) | M | quests runtime reliability | quests.md §3, §4.4 |

**Division:** Tai implements 1.1→1.4 (1.5 can parallel). Rei verifies each
with fail-before/pass-after tests. Nei updates scorecard + STATUS.md.
**Acceptance:** 0 failed tests; kill-acceptor quests (e.g. 1119) start in
game; sanity verifier passes on boot; scorecard quests row updated.
**Effort:** ~4-8 focused sessions. **Risk:** low — all code-grounded findings.

---

## MILESTONE 2 — Housing depth (Track 1)

**Why:** housing is a signature ArcheAge feature and only 38% wired; humans
AND bots want houses. Small gaps on a working system = high value per hour.

| # | Task | Size | Scorecard effect |
|---|------|------|------------------|
| 2.1 | **Deco-limit enforcement** (deco_limit read at HousingGameData.cs:103-105 but never checked; :1651 TODO) | S-M | housing 38%→~55% |
| 2.2 | **Zone validation** (:477 TODO — housing placement zone checks) | S | housing →~65% |
| 2.3 | **housing_groups UI tables** (load + serve; currently no consumer) | S | housing →~75% |

**Division:** Tai → Rei → Nei. **Acceptance:** place/decorate respects limits,
invalid zones rejected, scorecard updated. **Effort:** ~2-4 sessions. **Risk:** low.

---

## MILESTONE 3 — Zero-wired quick wins (Track 1)

**Why:** fast scorecard movement, satisfying momentum, fills world depth.

| # | Task | Size | Notes |
|---|------|------|-------|
| 3.1 | **Music wiring** (load instrument_sounds into MusicManager; PlayUserMusic.cs:40 names the gap) | S | 0%→100% on music domain |
| 3.2 | **Premium benefits** (read premium_benefits/grades; drive labor from data instead of hardcoded 5000) | S-M | 0%→100%; changes gameplay feel |
| 3.3 | **FxGroup/FxGroupAnim stubs** (implement or delete; client-only but skill chain touches them) | S | cleans the 15-table 0% row |

**Division:** Tai → Rei → Nei. **Effort:** ~2-3 sessions. **Risk:** very low.

---

## MILESTONE 4 — Contest & activity systems (Track 1)

**Why:** gives the world *things happening* — feeds the "living world" feel
even before bots.

| # | Task | Size | Notes |
|---|------|------|-------|
| 4.1 | **Ranks: fishing contest** (collect max catch length, rank, mail reward chests — SCRankRewardMailPacket offset exists) | M | ranks 0%→60% |
| 4.2 | **Race tracks: time trial** (doodad start, loop timer, record, mail chest) | M | race-tracks 0%→80% |

**Division:** Tai → Rei → Nei. **Effort:** ~4-6 sessions. **Risk:** medium
(new packet flows, but offsets exist).

---

## MILESTONE 5 — Siege (M slice: declare + own + tax) (Track 1 capstone)

**Why:** the biggest zero-wired feature; rich partial code (DeclareDominion
hardcoded, 5 packet offsets, doodad funcs loaded). Canonical capstone before
Track 2 — and bots will fight sieges later.

**Slice:** persistent single-castle dominion lifecycle WITHOUT combat:
schedule windows (siege_settings/plans), declare at monument doodad, owner +
tax state persisted (MySQL). Full combat war = L, later.

**Division:** Tai (design doc first → implement) → Rei (integration tests +
repro) → Nei. **Effort:** ~6-10 sessions. **Risk:** medium-high — new
persistence + lifecycle; mitigated by the existing packet surface.

---

## MILESTONE 6 — Track 2 foundation: bot framework

**Why:** everything after this is built on it. Must be an ADDITIVE layer
(lane-separation): new BotManager + PlayerBot entity, hooks into existing
TickManager (100ms) + AStar pathfinding + NpcAi pattern — NO core-interface
rewrites (keeps upstream pulls clean).

| # | Task | Size | Notes |
|---|------|------|-------|
| 6.1 | **BotManager + PlayerBot entity** (fake Character owner, tick registration, spawn/despawn, persistence stub) | L | the skeleton |
| 6.2 | **Bot behaviors v1** (roam, idle, follow, aggro-reply via existing combat) | M | reuse NpcAi behavior pattern |
| 6.3 | **Bot config + admin commands** (spawn N bots, /bot list, density zones) | S-M | ops + demo |

**Division:** Tai (architecture) → Rei (integration tests) → Mai (deploy to
box for live test) → Nei. **Effort:** ~8-12 sessions. **Risk:** medium —
new system; mitigated by additive design + graphify maps.

---

## MILESTONE 7 — Bot living world (Track 2)

| # | Task | Size | Notes |
|---|------|------|-------|
| 7.1 | **Bot chat (LLM bridge)** — bots respond to /say + zone chat via homelab ollama (gestalt .96); personality config per bot | M | the "talk to the world" feature |
| 7.2 | **Party bots** — bots accept invites, follow, assist, role (tank/heal/dps) | M | requires combat confidence |
| 7.3 | **Economy sim v1** — bots craft/trade-run/auction at configurable rates; feeds auction house + specialty demand | M-L | uses M2/3/4 systems |
| 7.4 | **Simulated PvP/sieges v1** — bot squads in conflict zones + siege defense/attack participation | L | uses M5 |

**Division:** Tai → Rei (QA each) → Mai (field-test with Josh's friends) →
Nei. **Effort:** ~10-16 sessions. **Risk:** medium-high per feature; each is
independent so they can land one at a time.

---

## MILESTONE 8 — Polish & scale

- Performance: bot count tuning, tick budget profiling (AI tick starvation
  upstream bug #1491 is a warning)
- STATUS.md always current; scorecard reviewed weekly
- Optional: community showcase (screenshots/video of the living world)
- Revisit upstream PRs ONLY with Josh's explicit approval

---

## Timeline estimate (side-project cadence, ~3-5 sessions/week)

| Milestone | Weeks (est) | Cumulative |
|-----------|-------------|------------|
| M0 foundation | done | — |
| M1 quest engine | 1-2 | wk 2 |
| M2 housing | 1 | wk 3 |
| M3 quick wins | 0.5-1 | wk 4 |
| M4 contests | 1-2 | wk 5-6 |
| M5 siege (M slice) | 2-3 | wk 8-9 |
| M6 bot framework | 2-3 | wk 11-12 |
| M7 living world | 3-5 | wk 15-17 |
| M8 polish | ongoing | — |

First real playtest milestone: **end of M1** (quests visibly fixed in-game).

## Dependencies map

```
M1 (quests) ──► M2 (housing) ──► M3 (quick wins) ──► M4 (contests) ──► M5 (siege)
                                                                          │
M6 (bot framework) ◄──────────────────────────────────────────────────────┘
      │
      ▼
M7 (LLM chat / party / economy / PvP) ──► M8 (polish)
```

M1-M5 are Track 1 (canonical, PR-able if ever approved). M6-M7 are Track 2
(fork-only product features). Bots depend on M1 (quests work), M2 (housing),
M4 (contests), M5 (siege) for their behaviors to have meaning.

## Definition of done per milestone

- [ ] Every task: branch, commits, tests (fail-before/pass-after), Rei signoff
- [ ] Full local gate green: Release build + compiler-check + all tests
- [ ] Scorecard row(s) updated in-branch; exploration report updated if needed
- [ ] STATUS.md reflects the milestone (Nei)
- [ ] Deployed to the aaemu box (Mai) and sanity-checked in-game where possible
- [ ] No upstream PR without Josh's explicit approval
