# Wiki Currency Audit — M0/M1 state (2026-08-04)

Auditor: hx-researcher (kanban t_95460a0f) · Scope: Docs/wiki/ (21 pages), synced to
GitHub wiki via `.github/workflows/wiki-sync.yml` · Ground truth: current fork tree at
/root/aaemu-dev (develop, last upstream pull 2026-08-03), ROADMAP.md v4, STATUS.md,
SCORECARD.md, scorecard-explorations/, bugs/.

Method: every wiki page read in full; every setup/architecture fact the wiki asserts
was spot-checked against the current tree (AppHost project, Scripts/, SQL/, config
defaults, ports, build outputs). Live wiki page list at github.com/AAEmu/AAEmu/wiki
compared against Docs/wiki/ (they match exactly — 20 pages + Home, all last synced by
github-actions[bot] on Mar 2, 2026).

## Headline verdict

- **Facts are current; framing is stale.** No wiki page asserts anything that
  contradicts the current tree — the Feb 2026 Aspire/Kestrel/GameServers wave is all
  still true. Every page carries "Last verified against: `develop` on February 28,
  2026" — now 5+ months old.
- **The fork's M0/M1 state is entirely absent from the wiki.** Nothing documents the
  roadmap/milestones, the lane gate, golden route, gate.sh, the quest harness, the
  scorecard/graphify tooling, or the BUG-00x fixes. That is the actual currency gap
  Josh is asking about ("make sure you're up to speed on the wiki and if need to
  update anything on m0").
- **One page is missing outright:** `Docs/wiki/Golden-Route-Solzreed.md` was reported
  delivered by M1-6 (task t_e1e6a99b, 2026-08-04) but does NOT exist in the tree
  (verified: no `*Golden*` file anywhere under /root/aaemu-dev). Needs recovery or
  regeneration before the wiki update card runs.
- **Two pages are content-stale in their own right** (Classes.md, Tutorial.md) — both
  pre-date the Feb 2026 verify pass and need a real re-verify, not just a date stamp.

---

## 1. How the wiki publishes (constrains every proposed update)

`Docs/wiki/**` is published by `.github/workflows/wiki-sync.yml`:
`on: push → develop, paths: Docs/wiki/**` → `Andrew-Chen-Wang/github-wiki-action@v5.0.3`
with the repo's own GITHUB_TOKEN. Consequences:

1. **Wiki edits are committed as files under `Docs/wiki/` and must land on the
   `develop` branch** (the workflow only fires there). Docs commits on develop are the
   established pattern (docs commits d248a691, 199b8952, 85c6b0af, e59a285b); the
   "never commit to develop" code rule does not apply to docs, but the wiki edit card
   should still use a docs-only branch merged to develop for reviewability.
2. **The action publishes to the wiki of the repo the workflow runs in.** On the fork
   that is `joshhmann/AAEmu`'s wiki — NOT the upstream `AAEmu/AAEmu` wiki. Getting M0/M1
   content onto the UPSTREAM wiki would mean touching upstream's develop → that is an
   upstream-facing change → **lane gate: needs Josh's explicit approval.** Default
   proposal below targets the fork wiki (consistent with the lane gate).
3. **Operational check for the edit card:** whether `joshhmann/AAEmu` has wiki enabled
   (the action creates it if not, but verify with Josh/Mai first).

---

## 2. Per-page currency table

| Page (Docs/wiki/) | Last-relevant-state | Needs-update? | Proposed change |
|---|---|---|---|
| Home.md | Feb 28 2026 verify; facts still true (verified Aspire/Kestrel/GameServers/Config.Local against tree) | Yes (light) | Add "Fork project status" links (Project-Status, Golden-Route-Solzreed, Quest-Test-Harness); refresh "Recent Platform Changes" framing (now 5 months old); refresh date stamp |
| Installation-&-Setup.md | Facts verified current (SQL/aaemu_login.sql + aaemu_game.sql exist, AppHost exists, ports match) | Stamp only | Refresh "Last verified" date; no content change |
| Aspire-Development-Guide.md | Current (AAEmu.Aspire.AppHost present) | Stamp only | Date refresh |
| Docker-Installation-Guide.md | Current (Scripts/docker-install-local.{ps1,sh}, docker-update-local.sh present) | Stamp only | Date refresh |
| Dependencies-and-Downloads.md | Current (download links external, unverifiable from here) | Stamp only | Date refresh; note compact.sqlite3 is NOT shipped in repo (dev box uses /tmp download) — optional |
| Client.md | Current (develop targets 1.2 r208022 — matches everywhere) | Stamp only | Date refresh |
| Server.md | Current | Stamp only | Date refresh |
| Components.md | Current (AppHost orchestrates; Kestrel login; 2 MySQL schemas) | Stamp only | Date refresh |
| Code-Terminology.md | Current (timeless) | No | None |
| FAQ.md | Facts verified (ports 1237/1239/1250/1280 match AAEmu.Login/Config.json + AAEmu.Game/Config.json; GameServers config-driven confirmed) | Stamp only | Date refresh |
| Working-with-the-Config.json-files-and-server-listings.md | Current (matches config code) | Stamp only | Date refresh |
| Mini-troubleshoot-guide.md | Current | Stamp only | Date refresh |
| Getting-Help.md | Current (timeless) | No | None |
| Help-Us-Help-You.md | Current (timeless) | No | None |
| Asking-for-Help-on-Discord.md | Current (timeless) | No | None |
| Asking-for-Help-on-GitHub-Discussions.md | Current (timeless) | No | None |
| _Footer.md | Timeless | No | None |
| Tutorial.md | Stub — "planned but not yet written" (pre-Feb 2026) | **Yes (content)** | Fill with the golden-route playtest walkthrough (Solzreed opening chain) or mark deprecated; see §4.7 |
| Classes.md | Content pre-dates Feb 2026; 10 of 11 classes "TODO", Sorcery notes describe a long-fixed era (Meteor damages caster, etc.) | **Yes (content)** | Full re-verify pass against current develop (upstream concern → Lane B); at minimum add staleness warning banner; see §4.8 |
| Developer-Notes.md | "Recent architecture note: manager DI" cites PRs #1363/#1366 (Feb 2026) as recent | **Yes (content)** | Refresh framing; add M0/M1-era notes (quest sanity verifier, scenario harness, gate.sh, graphify); see §4.5 |
| Documentation-Maintenance.md | Does not document the actual publish mechanism | **Yes (content)** | Add "how wiki pages publish" (Docs/wiki → develop push → wiki-sync.yml) + wiki page metadata convention; see §4.6 |
| **MISSING** Golden-Route-Solzreed.md | Reported delivered by t_e1e6a99b (2026-08-04) but absent from tree | **Yes (new page)** | Recover or regenerate; see §4.2 |

Facts verified as CURRENT (no update needed): Aspire AppHost project exists · .NET 10
(net10.0 build outputs) · login public port 1237 + game 1239 / stream 1250 / web API
1280 · login listings via `GameServers` config (not MySQL) · `Config.Local.json` loaded
last · `Scripts/docker-install-local.*` · base SQL files referenced by AAEmu.slnx +
docker-compose.yaml · client 1.2 r208022.

---

## 3. Concrete findings

1. **F1 — Date stamps 5+ months stale on all 20 substantive pages.** Mechanically
   refresh "Last verified against: `develop` on …" to 2026-08-04 (or the edit card's
   date) on every page that passes re-verification. This is the wiki's own convention
   (Documentation-Maintenance.md).
2. **F2 — No fork state anywhere.** Zero mentions of roadmap, milestones, golden path/
   route, lane gate, gate.sh, scorecard, graphify, quest harness, BUG-00x fixes. The
   wiki documents only the upstream community surface. (Gap, not error.)
3. **F3 — Golden-Route-Solzreed.md missing despite M1-6 completion claim.** Task
   t_e1e6a99b summary says it was delivered (209 lines, wiki style: arrival → main
   chain 254→255→256→257→259→…). Tree has no such file. Likely written to the task's
   scratch workspace and never committed, then the workspace was cleaned. The edit
   card must regenerate it (source material in §4.2 is sufficient) or recover it from
   the run log / kanban attachments.
4. **F4 — Tutorial.md is an empty stub** while the project now has real playable-route
   content (curated Nuian opening chain, golden zone) that belongs there.
5. **F5 — Classes.md content is from an old era** (only Sorcery documented; "Meteor
   damage dealt on the caster", "Fireball projectile not spawned" — descriptions of a
   codebase that has moved substantially; stale even against Feb 2026, since the page
   was last touched long before). Needs a from-scratch re-verify (Lane B — it is an
   upstream-community page, not M0/M1 work).
6. **F6 — Developer-Notes.md "recent" framing** references PRs #1363/#1366 as the
   latest architecture news; that was February. M0/M1 produced more current
   contributor-facing notes (verifier, harness, gate).
7. **F7 — Repo-doc side notes (not wiki, but same currency theme; Nei's lane):**
   - STATUS.md (2026-08-03) does not reflect the 2026-08-04 merges of BUG-007/008/009
     (commit 30c2b689) or BUG-010; its open-tasks table shows only BUG-006.
   - ROADMAP.md header says "v4, locked-shape 2026-08-03" but the M0 line says "ROADMAP
     v1" (line 77) and STATUS.md says "ROADMAP.md v7" — version labels disagree across
     the three docs.
   - Task-body census figure "T1 86/97" vs current runnability.md (generated
     2026-08-05 00:53Z) "88 PASS / 9 FAIL / 0 SKIP" — the report is newer; use 88/97.

---

## 4. Proposed update list for the M0 milestone state

All paths relative to repo root; all new/edited pages follow the wiki metadata
convention (Audience / Last verified against / Prerequisites / Related).

### 4.1 NEW `Docs/wiki/Project-Status.md` — fork milestone status (the core M0 ask)

Full draft below (wiki-ready as-is; edit card may trim).

```markdown
# Project Status — "ArcheAge Slums" fork milestones

- Audience: Contributors, players, and testers
- Last verified against: `develop` on August 4, 2026
- Prerequisites: None

Status of the fork's milestone plan (joshhmann/AAEmu). The canonical plan
lives in the repo: ROADMAP.md; this page is the short public status.

## Milestones

| # | Milestone | State |
|---|-----------|-------|
| M0 | Foundation — workflow lane gate, kanban templates, gate.sh, scorecards, graphify graph, shared division skill, BUG-006 fix merged | ✅ COMPLETE (2026-08-03) |
| M1 | Quest & progression spine — shared engine fixes + curated golden route (Solzreed) | 🔶 IN FLIGHT |
| M2 | Golden-path release gate — first repeatable playable loop (Solzreed locked as golden zone) | ⏳ next |
| M3a/b | Homestead shell, then property persistence & recovery | ⏳ |
| M4 | Trade, crafting, transport integrity | ⏳ |
| M5 | Gameplay Actor Contract (normalize, not invent) | ⏳ |
| M6 | Deterministic playerbot framework (headless bot sessions) | ⏳ |
| M7 | Adventurer & party bots (Playerbots Alpha) | ⏳ |
| M8 | Living Village — the first true vision release | ⏳ |

## M0 — Foundation (done, 2026-08-03)

Workflow v3 with the lane gate (no upstream PRs without owner approval),
kanban task templates, `scripts/gate.sh` (Release build + compiler-check +
full test suite), the 679-table technical scorecard (SCORECARD.md) +
exploration reports, the graphify semantic code graph (~17.6k nodes), and
the shared division skill on all worker profiles.

## M1 — Quest and progression spine (in flight)

Fix shared quest-engine defects first, then drive the selected golden route.
Progress to date:

- BUG-006 kill-acceptor quests (380 quests) — fixed, merged to fork develop;
  production deploy pending owner decision.
- BUG-007 quest sanity verifier — startup cross-check in QuestManager.Load
  (unknown act types, broken refs, orphaned rows); 14 tests.
- BUG-008 QuestActCheckGuard — silent auto-complete fixed (guard must exist
  and be alive); 3 tests.
- BUG-009 item-group gather/use objectives — 4 live quests + test quest
  unstalled; 14 tests.
- BUG-010 UnixTime clamp — every timer quest restored correctly; 8 tests.
  Full gate: 1129/1129 (2026-08-04).
- Scenario harness (M1-5b): engine-level full-lifecycle quest driver
  (START→PROGRESS→READY→REWARD→PERSIST) in AAEmu.UnitTests.
- Runnability census: Solzreed golden zone 88/97 PASS, 9 FAIL, 0 SKIP;
  fix-family set 22 PASS / 7 FAIL / 6 SKIP (orphaned contexts).
- Solzreed locked as the golden zone (zones 9/124/125, 97 quest contexts);
  curated Nuian opening chain documented — see Golden-Route-Solzreed.
- Known M1 blockers from census: step-suppression sequencing
  ("expected Ready, got Progress") on 265/266/269/294/295/299/303/1033/2248/
  3656/5489; reward-stage KeyNotFoundException 'General' in
  CharacterAbilities.AddActiveExp on 250/6578/6600/6615.
- Remaining M1: doodad phase/interaction objectives (quests 922/3889/3447),
  triage of the 9 zone fails, BUG-006 deploy decision.

## The golden route concept

The project builds ONE dependable classic-ArcheAge life loop first, then
expands outward: create character → starter progression (Solzreed) → unlock
mount → acquire farm → plant & harvest → build house → craft trade pack →
transport → sell → return home. All work is judged against that path
("the golden path is the product"). Bots will later master the same slice
(M5-M8); M2 is the first human-playable release gate.

## Related

- Golden-Route-Solzreed · Quest-Test-Harness · Home
- Repo: ROADMAP.md · STATUS.md · SCORECARD.md · VISION.md · LIVING-WORLD.md
```

### 4.2 NEW `Docs/wiki/Golden-Route-Solzreed.md` — RECOVER (F3)

**Action: recover the t_e1e6a99b artifact first** (kanban attachments / run log /
conversation log); if unrecoverable, regenerate from these sources (all present in
tree):

- `scorecard-explorations/solzreed-zone-report.md` — 97 quest contexts, kind-31 chain
  table (251→252, 254→255→256→257→259→…, 324→325, 290→291, 2255→2256→…→2266, 4292→
  4294→4295, 5095→5096→5097→5098, …), requirement gates, missing-data checklist.
- `scorecard-explorations/runnability.md` — per-quest PASS/FAIL verdicts for the same
  zone (drives "known issues" callouts per quest).
- Task summary of t_e1e6a99b: "curated Nuian opening chain from kind-31 data (arrival
  → main chain 254→255→256→257→259→…)" with excluded quests documented.

Page shape (from the M1-6 summary + wiki conventions): Audience · the curated route
table (order, quest id, name, giver, step, prereq, notes/status) · intentionally
excluded quests with reasons · known issues per quest (census failures) · Related →
Project-Status, Quest-Test-Harness.

### 4.3 NEW `Docs/wiki/Quest-Test-Harness.md` — contributor-facing tooling

The task explicitly calls for the tooling (harness/graphs) as contributor-facing docs.
Draft:

```markdown
# Quest Test Harness & Game-Data Graphs

- Audience: Contributors
- Last verified against: `develop` on August 4, 2026
- Prerequisites: .NET 10 SDK, `compact.sqlite3` for graph regen

## Scenario harness (M1-5b)

`AAEmu.UnitTests/Game/Quests/Scenario/QuestScenarioTierTests.cs` drives quests
through the full engine lifecycle — START → PROGRESS → READY → REWARD →
PERSIST — via manifests (`tools/quest-scenario/gen-manifests.py`) and the
`QuestScenarioDriver`. Verdicts: PASS / FAIL(stage + reason) / SKIP(reason).

Run:

```bash
./scripts/gate.sh QuestScenarioTierTests        # full suite: ./scripts/gate.sh
```

Census (2026-08-05): Solzreed golden zone 88/97 PASS (9 FAIL, 0 SKIP);
fix-family tiers 22 PASS / 7 FAIL / 6 SKIP (orphaned contexts).
Latest report: scorecard-explorations/runnability.md (regen = run the tests;
the report is regenerated from the run).

## Game-data knowledge graphs

`tools/quest-graph/` + `tools/gamedata-graph/` build queryable graphs over
compact.sqlite3 in graphify format:

```bash
python3 tools/quest-graph/build-quest-graph.py <compact.sqlite3>
python3 tools/gamedata-graph/build-gamedata-graph.py <compact.sqlite3>
python3 tools/quest-graph/report-zone.py <compact.sqlite3> 9 124 125   # Solzreed
```

Queries: `graphify explain "Quest 1119" --graph graphify-out/quest-graph.json` etc.
Per-zone readiness pipeline → golden-path expansion. See tools/quest-graph/README.md.

## Quality gate

`scripts/gate.sh` = Release build + in-game script compiler-check + full unit
test suite. Local gate is the real gate (upstream CI on fork PRs is
unreliable).

## Related

- Golden-Route-Solzreed · Project-Status · Developer-Notes · Home
```

### 4.4 UPDATE `Docs/wiki/Home.md`

- Add under a new "Project Status" heading (in Documentation Map → Project Reference,
  or directly under Start Here): links to `Project-Status`, `Golden-Route-Solzreed`,
  `Quest-Test-Harness`.
- Rename/re-date "Recent Platform Changes" → "Platform changes (February 2026)" so the
  framing doesn't claim recency; keep the four facts (all still true).
- Refresh the "Last verified against" stamp.

### 4.5 UPDATE `Docs/wiki/Developer-Notes.md`

- Replace the "Recent architecture note" heading with "Architecture notes" (or add an
  "August 2026" section above it).
- Add an M0/M1-era section: quest sanity verifier at startup (QuestManager.Load),
  scenario harness + census, gate.sh as the local gate, graphify refresh after pulls
  (`graphify update .`), fix-log convention (`bugs/NNN-*.md`), scorecard fork-fixes
  section.

### 4.6 UPDATE `Docs/wiki/Documentation-Maintenance.md`

- Add a "How wiki pages publish" section: commit under `Docs/wiki/` on `develop` →
  `.github/workflows/wiki-sync.yml` publishes to the wiki of the repo the workflow
  runs in (fork wiki for fork develop; upstream wiki only via upstream develop).
- Note the metadata convention is already in use (Audience / Last verified / Related)
  and that "Last verified against" must be bumped on every touch.

### 4.7 UPDATE `Docs/wiki/Tutorial.md`

Fill the stub with a playtest-oriented walkthrough derived from
Golden-Route-Solzreed (create character → opening chain → first mount
prerequisite), or replace with "see Golden-Route-Solzreed" + one short example
tutorial. Keep the planned-topics list.

### 4.8 UPDATE `Docs/wiki/Classes.md` — re-verify (NOT M0/M1 work)

Mark with a staleness banner ("content predates current develop; under
re-verification — see upstream issue tracker for skill status") and route a
per-class re-verify to Lane B (upstream-facing, needs a test pass on current
develop; 67 upstream open issues include 3 skill + many quest/skill gaps).
Do not let this block the M0/M1 wiki card.

### 4.9 Mechanical pass (all pages)

Bump "Last verified against: `develop`" to the edit-card date on: Home,
Installation-&-Setup, Aspire-Development-Guide, Docker-Installation-Guide,
Dependencies-and-Downloads, Client, Server, Components, FAQ,
Working-with-the-Config.json-files-and-server-listings, Mini-troubleshoot-guide,
Documentation-Maintenance, Developer-Notes, Tutorial, Classes.

---

## 5. Follow-up edit card spec (the card that MAKES the edits)

1. **Branch:** `docs/wiki-m0-status` off develop; docs-only commits (commit identity:
   division fallback `Hyraxknot Division <division@asslorde.com>` per WORKFLOW.md, or
   Nei as the tracking owner).
2. **Files:** add Project-Status.md, Golden-Route-Solzreed.md (recover first — F3),
   Quest-Test-Harness.md; edit Home.md, Developer-Notes.md,
   Documentation-Maintenance.md, Tutorial.md, Classes.md (banner only), plus the §4.9
   stamp refresh.
3. **Merge path:** merge to fork `develop` so wiki-sync.yml fires. Verify the fork
   wiki page list after publish.
4. **Decisions needed before the card runs:**
   - Where should the M0/M1 pages be visible — fork wiki only (default, lane-gate
     clean) or also upstream AAEmu/AAEmu wiki (needs Josh's explicit approval)?
   - Is the fork's wiki enabled (or should Mai enable it)?
   - Recover vs regenerate Golden-Route-Solzreed.md (check t_e1e6a99b run log).
5. **Non-goals for the card:** Classes.md full re-verify (Lane B), STATUS.md
   refresh (Nei — already flagged F7), ROADMAP version-label alignment (Nei).

## 6. Sources

- Docs/wiki/* (21 pages, read in full)
- .github/workflows/wiki-sync.yml · AAEmu.slnx · docker-compose.yaml
- ROADMAP.md (v4) · VISION.md · WORKFLOW.md · STATUS.md · SCORECARD.md ·
  LIVING-WORLD.md · .kanban-templates/README.md
- scorecard-explorations/{runnability,solzreed-zone-report,stub-acts}.md
- bugs/001…010 · tools/quest-graph/README.md · AAEmu.Game/Config.json ·
  AAEmu.Login/Config.json
- Live: https://github.com/AAEmu/AAEmu/wiki (+ /_pages) — page set matches Docs/wiki/
