# AGENT HANDOFF — 2026-08-26 Mail S3 acceptance + recovery reconciliation

Starting cold? Read this, then `STATUS.md` → `ROADMAP.md` →
`SCORECARD.md` → `scorecard-explorations/playerbot-blockers.md` →
`scorecard-explorations/mechanics/playerbot-capability-matrix.md`. This is the
current recovery delta after the ox-alpha loss; preserve the evidence trail.

## Current state

- **Branch of record:** fork `joshhmann/AAEmu` `develop @ 241d3e34d`
  (= `origin/develop`). Final gate: **2480/0/1**.
- **On develop:** grounding fix `38c4997d3`; recovered Retribution wire-test
  merge `a4f7820ba`; merchant merge `e5db6d390` (funds gate, buyback refund,
  and grant-failure rollback); Mail S3 acceptance `31045d033`; and the earlier
  committed M0–M7, G3-B5, Dominion slice-1, PvP/Crime, and economy features.
- **PB-005:** **FIXED-PARTIAL**. Positive-only terrain clamp plus the
  intentional aerial/water/structure whitelist are landed. The bounded replay
  corrects 593 non-whitelisted severe-positive rows; cave/deck/submerged
  behavior and duplicate-row decisions remain open.
- **PB-007:** **OPEN, narrowed**. Targeted rig PASS 1/1 (real `Skill.Use`,
  same-faction `ForceAttack` HP decrease, Retribution present; first
  application and Refresh broadcasts); live non-immune damage-frame proof
  remains pending.
- **Mail S3:** **PASS / LANDED** in `31045d033`. The authenticated
  `MailS3RestartE2eTests.Mail_EquipmentAndCopper_SurviveRestart_AndTakeByRealPackets`
  E2E passed 1/1 in 2m39s on isolated MySQL/Docker, covering restart,
  instance-faithful equipment+copper attachment, ownership guards, unread
  recount, sequential take, and delete persistence. Return opcode `0x0a2`
  remains STRONGLY_INFERRED pending real-client capture; no live-client
  confirmation is implied.

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

1. Run the corrected PB-007 live rerun with buff-state dump and corrected
   packet accounting; update the blocker only from observed evidence.
2. Review PB-005's terrain-only limits and make the registered owner decisions
   for cave/deck/submerged classifications and duplicate rows.
3. Continue the existing ROADMAP queue without deleting survivor worktrees or
   changing the H=UNKNOWN rule.

## Hard rules

- Never push a branch or PR to upstream AAEmu/AAEmu; upstream is intake-only.
- `compact.sqlite3` is a SELECT-only reference.
- Bots use normal gameplay paths; no direct DB or bot-only resource creation.
- H means actual-player feel and remains UNKNOWN until Josh runs the scenario.
- Preserve history and leave this reconciliation uncommitted for the later
  commit lane.
