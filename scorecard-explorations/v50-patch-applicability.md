# 5.0 Patch Applicability Matrix (r208022 1.2 tree)

Source: `/root/archeage-v5.0-patches` (external reference, NEVER merged;
clone lives outside this repo). AAFree 5.0.7.0 patches against upstream
AAEmu; our tree is ArcheAge 1.2 r208022 (`compact.sqlite3` md5
`78b3bdbf038db3b927056106efdf91af`).
Data claims below corroborated 2026-09-07 ~20:52 PDT (tool stamp
2026-09-08T03:52:53Z) via Archaeology MCP (`aaemu-archaeology` 1.1.0,
`query_sql`/`lookup_row`, source_id `compact.sqlite3`, version
`1.2 r208022`, path `AAEmu.Game/Data/compact.sqlite3`); 5.0-side data
(`compact.server.table.sqlite3`, trimmed pak, GeoDataMode) is treated as
non-transferable unless this repo shows the equivalent. Patch target paths
verified against diff `---`/`+++` headers, resolved to true repo paths
(namespace-dot filenames normalized). "ABSENT" means absent at the checked
path only — not proof of 5.0-only status.

Rule: no wholesale import. Generic correctness/security fixes are
candidates ONLY after confirming the bug exists here; protocol/client/
data/schema changes need 1.2 archaeology + neighbor-code verification.
DB-backed `game_servers` rows conflict with our locked `GameServers`
config convention — never applied.

Legend: ✅ VERIFIED (checked against our tree, evidence cited) ·
🔶 TRIAGED (README read, target unverified) · ⛔ OUT (5.0-only/custom)

## Already covered here — no action

| Patch | Evidence |
|---|---|
| packetstream-roundup-overflow | `PacketStream.cs:191-193` overflow guard present |
| protocol-handler-short-read-spin | Game/Internal/Stream handlers carry remnant-stash + rollback + malformed-close; public Login uses bounds-checked reader API |
| socket-disposed-on-accept | `Session.cs:38-55` fallback; `Server.cs:34` null-guarded |
| autoattack-hit-fx | Our `Skill.cs` already orders ApplyEffects→EndSkill on the instant path |
| doodad-boot-save-burst (class) | Load-never-writes discipline present (`Doodad.cs:348,1009,1055`) |

## High-value candidates (bug confirmed or strongly indicated here)

| Patch | Status | Evidence / next step |
|---|---|---|
| npc-ai-params-wrong-type | ✅ BUG PRESENT (variant) | MCP: 10,893 NPCs have `npc_ai_param_id` 0/NULL; `lookup_row npc_ai_params id=0` empty; `GetAiParamsForId` returns null (`AiGameData.cs:27-32`); `LoadAiParams` assigns null (`NpcManager.cs:899-905`); attack behaviors bail on `is not X` (`ArcherAttackBehavior.cs:53`, `BigMonsterAttackBehavior.cs:40`) → NPCs never reach skill selection. Our tree lacks the `??= DefaultAiParams` line but fails identically via null. Needs adapted fix (correct-type fallback or null-tolerant behaviors). |
| npc-pos-skill-aim | ✅ BUG PRESENT | Our `Behavior.cs:167-175` builds Pos-targeted casts at the CASTER's own position — identical lines. Needs port (target-position fix). |
| npc-respawn-floors | ✅ LANDED in a2b82ef1 | 90s normal / 300s elite floors (WorldConfig keys + World.json, live-tunable via /reloadconfig), elite = grade>=7, 10s placeholder threshold (both delays must be at/below), raise-only — authored timers untouched; 6 focused tests (`NpcRespawnFloorTests`). |
| npc-cave-float-clamp | 🔶 COMPARE, don't copy | `Models/Game/NPChar/TrackAndStoreCoordinates.cs` + `NpcPathfindingImproved.cs` ABSENT at checked paths (2026-09-07 session) — their fix has no direct seam here. Their MaxGroundRise-8m rise-reject vs our ±2m deadband + whitelist + dry-run telemetry (Phase 1 landed). Different philosophy, same cave problem. Needs a compare pass against our height-resolution path. |
| quest-offer-freeze-on-reject | ✅ LANDED in a2b82ef1 | Error replies on the two silent `AddQuest` paths; supply-item path already replied; template-null / StartQuest-false / TryAdd-race intentionally untouched. |
| quest-sphere-component-match | ⏸️ PARKED | Requires client `quest_sign_sphere.g` geometry + `ClientData.Sources`; verify ordinary 1.2 behavior/assets first. |
| stuck-player-ghost / stuck-rider-autorecover | ⏸️ DEFERRED (no code change) | Today's finding: no 1.2 path produces an attached-but-missing target (orderly detach on all removals — see row evidence); `CSMoveUnitPacket` null branch stays log-only, unchanged; upstream ghost variant was reverted. A future live 1.2 repro reopens this. Out of all pending queues until then. |
| well-gather-fix | ⏸️ PARKED | Broadens `Use.Execute` target selection (nearest-doodad fallback) — not generic; verify ordinary 1.2 gather behavior first. |
| doodad-aoe-target-quest-credit | 🔶 CHECK | `Skill.cs` AoE quest credit path. |
| doodad-save-batching | 🔶 CHECK | Beyond the boot-burst guard: batching + null guards. |
| quest-doodad-requirequest-and-rotation | 🔶 CHECK | Quest 307-class fixes + spawn rotation. |
| quest-act-types-and-disable | 🔶 PARTIAL | `Models/Game/Quests/Acts/QuestActConReportNpcGroup.cs` ABSENT at checked path (2026-09-07 session); other targets exist — verify each. |
| transfer-path-cell-offset | 🔶 CHECK | Transfer route loading + offsets; our TransferRideE2e passes, so compare carefully. |
| vehicle-oob-movement-oom | ✅ COVERED (not queued) | Covered by `VehicleMovementModel` bounds checks — no port queued, verification only. (`CSStartedCinema2Packet.cs` ABSENT at checked path, 2026-09-07 session; `CSStartedCinemaPacket.cs` exists.) |
| vehicle-world-streaming | 🔶 CHECK | Seat-offset runaway occupant position. |
| vehicle-summon-fx-sound | 🔶 CHECK | Stuck cast FX on summon skill 15802. |
| corpse-casting-skill-controller | 🔶 CHECK | `Unit.cs` corpse-casting guard. |
| log-noise-perf-cleanup | 🔶 CHECK | WARN-rate + NLog drops; needs our log-rate measurement first. |
| doodad-boot-save-burst (residual) | 🔶 CHECK | Beyond the load guard: climate `growth_time` discard + save-origin telemetry. |
| mate-death-revive | 🔶 CHECK | Mate death + revive cooldown paths differ here; verify. |
| autoattack-continuous | 🔶 CHECK | Server-side repeat wiring for basic attack. |
| gathering-min-level-config | 🔶 CHECK | Config-gated gathering minimums. |
| item-use-skill-lifetime-expired | 🔶 CHECK | Expired item-use skills. |
| recover-exp-blessing | 🔶 CHECK | Exp recovery blessing effect. |
| character-death-penalty | 🔶 DESIGN CHOICE | Level-scaled XP/durability loss — behavior change, needs design sign-off, not a bugfix. |
| ui-data-binary-safe | 🔶 CHECK | UIData packet binary safety. |
| world-doodad-growth-persistence | 🔶 CHECK | Growth-time persistence across restarts. |
| cinematic-vehicle-dismount | 🔶 PARTIAL | `CSStartedCinema2Packet.cs` ABSENT at checked path (2026-09-07 session); `CSStartedCinemaPacket.cs` exists — verify. |
| cinema-directing-mode-freeze | ⛔ no seam | `CSRequestPermissionToPlayCinemaForDirectingModePacket.cs` ABSENT at checked path (2026-09-07 session) — no 1.2 target; not pursued. |
| towerdef-event-spawning | ⛔ 5.0 event content | Targets (`TowerDefGameData`, event spawners) absent — skip. |
| marketplace-loyalty | ⛔ custom feature | Loyalty marketplace program + SQL setup — design choice, not a fix. |
| mate-type-dragon-category | ⛔ INCOMPATIBLE (closed) | MCP: dragon items 44647/45129/45130 return 0 rows in 1.2 DB; no `MateType` symbol anywhere in 1.2 tree (verified); 5.0 `CharacterMates.cs` has no 1.2 seam. No further pursuit by this name. |
| 0001-stop-scanner-junk-error-flood | 🔶 CHECK | Config-only error-flood suppression. |
| 0002-summon-fx-stuck-effect | 🔶 CHECK | Data-only stuck-effect fix. |

## Data-ops (not code)

- `apply-english-localization.py`: FINDING ONLY under the read-only rule.
  Our DB already has `localized_texts` (158,543 en_us rows). Trial on a
  disposable copy: 241,398 rows across 124 table/columns. Applies to an
  isolated server via symlinked runtime DB only — never the canonical file.
  Changes server-side names/logs/admin output, NOT client in-game text.
- `remove-farm-clutter.py`: 1.2-world applicability unverified (their ids
  are main_world 5.0 spawns). Do not run here without a 1.2 id census.

## Recommended import order (security/correctness first, verified bugs only)

Landed — out of the pending queue:

- npc-ai-params wrong-type variant → Almighty fix `03c381241`
- npc-pos-skill-aim → Pos-facing fix `8f7f1b8e`
- quest-offer-freeze-on-reject → `a2b82ef1`
- npc-respawn-floors config floor → `a2b82ef1`
Deferred (not closed — a live 1.2 repro reopens): stuck-player-ghost / stuck-rider-autorecover — no code change (see row).
Covered, not queued: vehicle-oob-movement-oom (`VehicleMovementModel` bounds).

Pending queue:

1. quest-sphere-component-match (player-visible freeze path still open)
2. Everything else only after target-file verification per patch.

## Parked upstream phases (not queued)

Roadmap-scale input from `UPSTREAM_BRANCHES_AND_50_ARCHAEOLOGY_FINDINGS.md` (Part VI) — preserved, not queued; needs design sign-off before any implementation.

- Phase 0 protocol truth table (VERIFIED/DEFINED/INFERRED/UNRESOLVED/NOT_IN_1.2 opcode classification) — parked: needs evidence-standard sign-off; see root doc Part VI Phase 0.
- Phase 2 character/auth hardening follow-ups (creation-payload validation beyond landed guards) — parked: needs design sign-off on scope; see root doc Part VI Phase 2.
- Phase 3 vehicle/slave socket topology (attach points, seat binding, RidersEscape) — parked: roadmap-scale, needs 1.2-wire + Jitter2 design; see root doc Part VI Phase 3 / Ticket A.
- Phase 4 doodad & public-farm lifecycle (phase consumption, crop-expiry release) — parked: needs simulation/bot-loop design sign-off; see root doc Part VI Phase 4.
- Phase 5 economy telemetry (server-side SalesData/SoldsData history) — parked: client-UI absent in 1.2, needs design sign-off; see root doc Part VI Phase 5 / Ticket D.
- Phase 6 UCC pipeline + skill CC interruption (crest upload :1250, cast-cancel hooks) — parked: roadmap-scale, needs design sign-off; see root doc Part VI Phase 6.
