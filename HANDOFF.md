# AGENT HANDOFF — 2026-08-26 Mail S3 acceptance + recovery reconciliation

Starting cold? Read this, then `STATUS.md` → `ROADMAP.md` →
`SCORECARD.md` → `scorecard-explorations/playerbot-blockers.md` →
`scorecard-explorations/mechanics/playerbot-capability-matrix.md`. This is the
current recovery delta after the ox-alpha loss; preserve the evidence trail.

## Current state

- **Branch of record:** fork `joshhmann/AAEmu` `develop @ 13b8bedb8`
  (= `origin/develop`). Full gate (`./scripts/gate.sh`): **2531 total / 2530 passed / 0 failed / 1 skipped**; compiler **0/0**; MCP stdio smoke **39 tools**.
- **On develop:** PB-007 WAR-HONOR test suite and parallel rig isolation `13b8bedb8` (`PvpFlaggingRigTests` 11/11, `ZoneConflictTests` 13/13); ZeromusXYZ per-item mail attachment split and multi-item test `ac8953813` (`MailCodLifecycleTests` 5/5); review gate bug fixes `fa56915e6` (sent-tab deletion state removal, `SCAttachmentTakenPacket` item `aaPoint` fix, `NameManager` $O(1)$ dictionary lookup, bounded `CraftLeg` loop); PB-002 interaction slice `e9ace7f22` (quest 269→270 with Doodad 687 torch/hay skill 11229); next objective types `49f0aee07` (`QuestActObjSphere` 1372, `QuestActObjCraft` 6024, `QuestActObjCinema` 6041); self-quest discovery channel `970d6a557` (`LevelingLoopScenarioRigTests` 14/14); Mail COD payment deduction/dispatch `69861b73c` (`MailCodLifecycleTests` 4/4); timer cancellation safety `950cfd279`.
- **PB-002:** **SCOPED ACTOR/RIG SLICES LANDED; BROAD CLAIM OPEN**. Interaction (quest 270), ItemUse (quest 252), Sphere (quest 1372), Craft (quest 6024), Cinema (quest 6041), and Self-Discovery channel pass headlessly 14/14. Live stack progression and human feel (`H=UNKNOWN`) remain open.
- **PB-005:** **FIXED-PARTIAL**. Positive-only terrain clamp plus intentional whitelist landed (593 severe-positive rows corrected). Cave/deck/submerged classification awaits Josh's W4-5 grounding tour data.
- **PB-007:** **NARROW HANDSHAKE CLOSED; WAR-HONOR TEST SUITE COMPLETE**. 1v1 same-faction flagged aggression and Peace block verified live (`3871459d142fdd1767b9365a1de8d4cd3652ab0e`); full kill-counter escalation (Tension...Conflict->War), multi-role assists (damage, heal, CC), offline assist fallback, War honor distribution (32/4) & penalty (−10), and respawn escalation verified (`0492b7199`).
- **Mail:** **PASS / LANDED**. Mail S3 + COD payment charge enforcement, item looting payment deduction, payment mail dispatch to sender, sent-tab deletion, and name resolution fallback are landed and tested (26/26 `Mail*` unit tests pass).

## Surviving worktrees — do not delete

The repository's worktrees are retained for recovery. In particular:

- `.worktrees/mails3` — retained detached recovery survivor at `3fc64ae`;
  its uncommitted `BotDriveBridge` worktree is not the landed S3 evidence.
- `.worktrees/nav-probe` — detached at `41ddb88`; uncommitted
  `WorldCell`/`WorldTemplate` changes and `Tools/NavProbeScratch/`.
- `.worktrees/rowboat` — detached at `bfbea40`; untracked
  `RowboatE2eTests.cs`.
- Other retained research/verification survivors: `.worktrees/b1-interact-loot`,
  `.worktrees/crafting-dossier`, `.worktrees/trade-packs-dossier`,
  `.worktrees/vehicles-ships-dossier`, `.worktrees/m3-canonical-audit`,
  `.worktrees/gate-t6c952150`, `.worktrees/rei-g1-rv2`, and the M5.1 craft-rig
  worktree under `/root/.hermes/kanban/workspaces/t_6b5ac43e/rig-repo`.

Do not remove, reset, or overwrite these survivors while reconciling. They are
not evidence that their unmerged work landed on `develop`.

## Next resume order

1. **PB-007 WAR-HONOR scope**: Implement deterministic conflict zone kill escalation rig (0→251 kills $\rightarrow$ Tension...Conflict$\rightarrow$War) and multi-bot assist / honor division test fixtures.
2. **PB-002 live progression slice**: Run quest-270 / leveling loop on live Game stack (`AAEMU_LIVE_RIG`) with real client/bot to capture live packet evidence.
3. **PB-005 grounding review**: Await Josh's W4-5 grounding tour coordinates/screenshots to classify cave/deck/submerged findings.

## Hard rules

- Never push a branch or PR to upstream AAEmu/AAEmu; upstream is intake-only.
- `compact.sqlite3` is a SELECT-only reference.
- Bots use normal gameplay paths; no direct DB or bot-only resource creation.
- H means actual-player feel and remains UNKNOWN until Josh runs the scenario.
- Preserve history and leave this reconciliation uncommitted for the later
  commit lane.
