# Repository audit 2026-09-15 — codebase, scorecard, roadmap, docs

Read-only audit (no code, doc, or data changes; no builds/tests run) to support a dashboard roadmap/scorecard-tab refresh describing what remains for a complete 1.2 implementation. Four scout lanes + HEAD checks. HEAD at audit time: `e93e3df2d` (branch `develop`).

## 0. Version ruling (read this first)

"Complete 1.2.4" needs a definition before the tab can measure against it:

- The repo uniformly pins **ArcheAge 1.2 / client build r208022**: every `*Offsets.cs` header (`client_12_r208022`), `Docs/wiki/Client.md` ("1.2 (r208022)"), archaeology provenance, guides. Server pak md5 verified byte-identical to the official r208022 client pak.
- **1.2.4.13 is the marketing label for the same 1.2 line** — `Client.md:18` literally names the file `ArcheAge 1.2.4.13 (r208022)`.
- **r208088 is a different, newer compact-data revision** referenced only in `bugs/002-sqlite-schema-too-old.md`, `bugs/005-pak-version-drift.md` (RETIRED 2026-08-02), and `stub-acts.md` ("prod compact r208088"). Opcodes stay pinned to r208022 — exactly the opcode-vs-DB parity risk BUG-005 warned about.
- Canonical DB: `AAEmu.Game/Data/compact.sqlite3`, md5 `78b3bdbf038db3b927056106efdf91af`, 679 tables (verified this session).
- Recommendation for the tab: pin **1.2 = 1.2.4.13 = r208022** as the target banner, and open a ruling on BUG-005 (confirm r208088 is dead history or re-open the drift). No doc maps md5→revision today.

## 1. Codebase reality

- Structure as documented: Game (DI host, topological `ManagerOrchestrator` boot), Login (Kestrel TCP), Commons, 4 Tools (WorldConverter, SurveyGridBaker, NavGCostProbe, UpdatesForTransform), 3 test projects (TUnit unit + 2 integration). SDK 10.0.0. GameData loaders auto-discover by reflection (no wiring list to rot). SQL updates mirror base files on recent slices (shipyards, dominions, crime, playerbot_metadata/audit).
- **Real bugs found (dashboard should list as work, not claims):**
  1. `CrimeManager` registered TWICE in `Program.cs` (:167-168 and :448-449).
  2. `/doodad` command collision: `CrimeCmd.cs:13` and `DoodadCmd.cs:12` both claim `["doodad"]` — crime subcommands unreachable if DoodadCmd wins registration.
  3. Possibly-dead managers: `SlaveManager`, `MateManager`, `GimmickManager`, `AiGeodataManager`, `TransferManager`, `SpawnManager`, `PhysicsManager`, `SphereQuestManager` have no DI registration and live `.Instance` callsites appear only in comments — needs a verification pass before calling them dead (bots use mounts/slaves through other paths).
  4. `CSOffsets.cs`: `0x9f` claimed unknown AND assigned (`CSTakeAttachmentSequentially`); `CSReturnMailPacket=0x0a2` is strongly inferred, not client-verified.
- Packet surface: ~252 C2G classes, ~235 registered + 22 proxy; 13 present-but-parked at `0xfff` (by design); ~20 opcode slots with neither class nor registration. G2C 397 files (no registration needed).
- GM commands: 95 files; documented verbs spot-checked present (`/trace`, `/bot` family, `/item`, `/house`, `/teleport`, kit).
- TODO density centers: QuestManager phases, HousingManager (~20: castle/mail/coffer), CrimeManager, PortalManager, ItemManager, MailManager.
- Stray file: repo-root `compact.sqlite3` is a **0-byte placeholder** (2026-09-03, ignored, untracked) — some tool created it via a wrong-path connect. Delete on next cleanup pass; real DB untouched.
- GOAP tension: `GoapPlanner`/`BotWorldState` exist in code while PROJECT-CONTROL defers full GOAP — flag for the tab, not a contradiction per se.

## 2. Scorecard audit

- H discipline is GOOD: H=COMPLETE exactly once (M0 foundation, not gameplay); every mechanic H cell is U; H-vs-proxy hygiene held (09-05 M3a correction on record). No bot evidence presented as human feel anywhere.
- **Stale rows that understate us (good news):** AGGRO-PACK-01 and RESPAWN-LADDER-01 claim W=0/unread — contradicted by `NpcGameData.cs:177` + `Behavior.cs:411` + tests, and `ResurrectionGameData.cs:52` + `CharacterCombat.cs` + tests. Deferred mount-riding NOT-NOW contradicted by landed PB-MOUNT (09-03).
- **Weakest evidence links:** 09-13 live-E2E promotions rest on out-of-repo reports + dirty tree + old HEAD; M8 QUALIFIED has no ledger row (table stops at M7); A5 stamp mismatch; gateless PB-006 3211/0/1 count without SHA; Navigate evidence counts 5/8/9 unreconciled; upstream tracker frozen 2026-08-03; COMBAT-01 W=1 vs REPAIR-01 W=2 grading split needs a ruling.
- **Dashboard-confusing terminology:** ledger-A (automated) vs ladder-A (autonomous) vs G (society) collide on one letter; ladder H=U is sprint-scoped. The tab must scope every letter.
- Structural gaps for "complete": 33 census rows all-U (the 1.2 long tail); R/S columns blank outside M3b/M4/AUCTION/DOMINION; newest A-grades depend on single-machine unversioned reports.

## 3. Roadmap audit

- M0–M7 + M5.x: every CLOSED claim carries its open H/sub-gate explicitly — no silent closures. M8 QUALIFIED (runtime leg) with accepted transient + INVALID-by-design; M8.5/M9/M10 correctly unclaimed; Q7/Q8 blocked; PB-FARM-01 open; WAR-HONOR deferred.
- **Evidence-location gap:** the newest runtime reports (A5 tier-3, C5 re-soak, 09-13 E2E six, Q6, Mail S3, Dominion) live in `/root/aaemu-e2e*` lane dirs, not the repo — the tab cannot link them. Only `soak-artifacts/smoke-rb/20260912-014234` is in-repo.
- No ledger rows exist for M8/M9/M10/A3/A4/A5/Q-lanes — post-M7 evidence lives only in prose docs.
- Minor: G1 denominator stated three ways (4579 vs 4587 vs 4573+6); auction-fix SHA pending-vs-cited; mirage-duel report unregistered; M4 deployed-cell acknowledged stale.
- Open gates all Josh-owned: H gates #1–#4 (+#5 feel), W4-1–W4-4 (deploy currency open), PB-005 tour, Q7 rulings, M9 substrate approval.

## 4. Docs audit

- **P0 fix:** `CONTRIBUTING.md` instructs fork-and-PR with zero mention of the permanent NEVER-push-to-upstream rule — following it violates the top-line rule.
- Clone URLs point at upstream `AAEmu/AAEmu` (Installation ×2, Docker guide, Dependencies) instead of the fork.
- 4 dangling internal refs: `Docs/networking.md` (real: `AAEmu.Login/Docs/networking.md`), `tools/quest-graph` + `tools/gamedata-graph` (absent), root `scorecard-explorations/*` links (content lives in a worktree), `Docs/JOSH-QAT-WAVE4.md` case/path.
- Stale currency: ~26 wiki pages stamped 2026-08-05 (including the skill-blessed setup pages); census numbers conflict across three pages (153/153 vs 86/97 vs 88/97); `Project-Status.md` presents 08-05 snapshot as current; `AAEmu.Login/README.md` documents a boot-failing config (no `GameServers`); Track-B sample omits SecretKey/internal ports → guaranteed Maintenance.
- Four overlapping port tables, none canonical (recommend `REFERENCE.md` as home); FAQ omits 1234; Aspire omits 1234/1280/15133; config-precedence and GameServers-not-MySQL each worded 5–7×; help pages ×4 overlap.
- Absolute `file:///root/aaemu-dev` links (START-HERE, World page) — dev-box-only.

## 5. What the dashboard tab needs (build order)

1. Target banner: 1.2 = 1.2.4.13 = r208022 + DB md5 + BUG-005 ruling slot.
2. Per-mechanic rows with evidence links, freshness (SHA + date), and scoped letters (ledger-A vs ladder-A vs G).
3. Weakest-links section (§2) and code-bug worklist (§1) as first-class rows, not footnotes.
4. H-gate intake list (Josh-owned, Field-Guide worksheets) kept separate from A/R/L evidence.
5. Doc-fix queue (§4 P0 first) with file:line pointers.
6. In-repo evidence rule going forward: reports cited by the tab must live under `scorecard-explorations/generated/`.

## Method + limits

Grep/read-backed; verdicts tie to observed lines (full transcripts in scout payloads). Not verified: external URL liveness, DB item IDs, exact unopened-page line numbers, behavior correctness beyond name existence, full 679-table recount, test-suite green at HEAD. Counts sampled where noted.
