# 5.0 Patch Applicability Matrix (r208022 1.2 tree)

Source: `/root/archeage-v5.0-patches` (external reference, NEVER merged;
clone lives outside this repo). AAFree 5.0.7.0 patches against upstream
AAEmu; our tree is ArcheAge 1.2 r208022 (`compact.sqlite3` md5
`78b3bdbf038db3b927056106efdf91af`).

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
| npc-ai-params-wrong-type | ✅ BUG PRESENT (variant) | 10,893 NPCs have `npc_ai_param_id` 0/NULL; no id-0 row; `GetAiParamsForId` returns null; attack behaviors bail on `is not X` → NPCs never fight. Our tree lacks the `??= DefaultAiParams` line but fails identically via null. Needs adapted fix (correct-type fallback or null-tolerant behaviors). |
| npc-pos-skill-aim | ✅ BUG PRESENT | Our `Behavior.cs:167-175` builds Pos-targeted casts at the CASTER's own position — identical lines. Needs port (target-position fix). |
| npc-respawn-floors | ✅ DATA DISEASE PRESENT | 11,773 spawner rows at exactly 10.0/10.0 (theirs: 11,774). Config-floor concept applies; no code overlap issue. |
| npc-cave-float-clamp | 🔶 COMPARE, don't copy | Their MaxGroundRise-8m rise-reject vs our ±2m deadband + whitelist + dry-run telemetry (Phase 1 landed). Different philosophy, same cave problem. Needs a compare pass. |
| quest-offer-freeze-on-reject | 🔶 CHECK | Silent `AddQuest` rejection vs `SCQuestContextStartedPacket` expectation — verify our `CharacterQuests.AddQuest` reply paths. |
| quest-sphere-component-match | 🔶 CHECK | Zero sphere geometry loaded here too? Verify `SphereQuestManager`/ClientData sources. |
| stuck-player-ghost / stuck-rider-autorecover | 🔶 CHECK | `CSMoveUnitPacket` invalid-target recovery vs ours; needs target-file compare. |
| well-gather-fix | 🔶 CHECK | `Use.cs` self-targeted-gather fallback; near our Q4/Q6 paths — verify. |
| doodad-aoe-target-quest-credit | 🔶 CHECK | `Skill.cs` AoE quest credit path. |
| doodad-save-batching | 🔶 CHECK | Beyond the boot-burst guard: batching + null guards. |
| quest-doodad-requirequest-and-rotation | 🔶 CHECK | Quest 307-class fixes + spawn rotation. |
| quest-act-types-and-disable | 🔶 PARTIAL | `QuestActConReportNpcGroup` target file MISSING here (5.0-only type?); other targets exist — verify each. |
| transfer-path-cell-offset | 🔶 CHECK | Transfer route loading + offsets; our TransferRideE2e passes, so compare carefully. |
| vehicle-oob-movement-oom | 🔶 PARTIAL | `CSStartedCinema2Packet` target MISSING here; check `CSMoveUnitPacket` validation + `WorldManager` bounds. |
| vehicle-world-streaming | 🔶 CHECK | Seat-offset runaway occupant position. |
| vehicle-summon-fx-sound | 🔶 CHECK | Stuck cast FX on summon skill 15802. |
| corpse-casting-skill-controller | 🔶 CHECK | `Unit.cs` corpse-casting guard. |
| log-noise-perf-cleanup | 🔶 CHECK | WARN-rate + NLog drops; needs our log-rate measurement first. |
| doodad-boot-save-burst (residual) | 🔶 CHECK | Beyond the load guard: climate `growth_time` discard + save-origin telemetry. |
| npc-respawn-floors (code part) | 🔶 CHECK | Respawn floor config keys. |
| mate-death-revive | 🔶 CHECK | Mate death + revive cooldown paths differ here; verify. |
| autoattack-continuous | 🔶 CHECK | Server-side repeat wiring for basic attack. |
| gathering-min-level-config | 🔶 CHECK | Config-gated gathering minimums. |
| item-use-skill-lifetime-expired | 🔶 CHECK | Expired item-use skills. |
| quest-offer-freeze (duplicate entry, see above) | — | — |
| recover-exp-blessing | 🔶 CHECK | Exp recovery blessing effect. |
| character-death-penalty | 🔶 DESIGN CHOICE | Level-scaled XP/durability loss — behavior change, needs design sign-off, not a bugfix. |
| ui-data-binary-safe | 🔶 CHECK | UIData packet binary safety. |
| world-doodad-growth-persistence | 🔶 CHECK | Growth-time persistence across restarts. |
| cinematic-vehicle-dismount | 🔶 PARTIAL | `CSStartedCinema2Packet` target MISSING; `CSStartedCinemaPacket` exists — verify. |
| cinema-directing-mode-freeze | ⛔ target file MISSING | `CSRequestPermissionToPlayCinemaForDirectingModePacket` absent in 1.2 — 5.0-only. |
| towerdef-event-spawning | ⛔ 5.0 event content | Targets (`TowerDefGameData`, event spawners) absent — skip. |
| marketplace-loyalty | ⛔ custom feature | Loyalty marketplace program + SQL setup — design choice, not a fix. |
| mate-type-dragon-category | ⛔ content absent | Dragon items 44647/45129/45130 not in 1.2 DB; our mate typing has no `MateType` switch to fix. |
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

1. npc-ai-params wrong-type variant (10,893 silent NPCs — highest impact)
2. npc-pos-skill-aim (identical lines, near-trivial port)
3. npc-respawn-floors config floor (data disease confirmed, additive config)
4. quest-offer-freeze + quest-sphere-component-match (player-visible freezes)
5. stuck-player/rider + vehicle-oob (after confirming each bug here)
6. Everything else only after target-file verification per patch.
