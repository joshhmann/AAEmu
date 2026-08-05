# BUG-013 — NPC sit poses render "knees in" (unplayable sit anim ids sent to the 1.2 client)

- **Status**: FIXED (branch `fix/npc-sit-pose`, 2026-08-05)
- **Severity**: Medium (player-visible NPC behavior; ~28 in-zone sit-set NPCs in Solzreed/Gweonid affected, more zone-wide)
- **Component**: Unit posture packets — `Unit.ModelPosture` (used by `SCUnitStatePacket` +
  `SCUnitModelPostureChangedPacket`), new `SitPoseFallback`
- **Discovered via**: playtest (Josh 2026-08-04) + Recon B (`scorecard-explorations/npc-behavior.md` §2)

## Symptom

NPCs with a sit posture set (`npc_postures.anim_action_id`) render in a crouched "knees in"
pose instead of their intended sit pose (chair / lean / crouch-work poses).

## Investigation (what the client actually expects)

1. **Packet structure**: the 1.2 (r208022) `ActorModelState` posture section is
   `byte postureType=4, bool isLooted, uint animActionId, bool activate` — confirmed against
   the original sniffed captures (Mr. Nikes 1.2-era `NP_SCUnitStatePacket_0x0064.cs`) and
   unchanged upstream since 2019. **There are no "sub-pose params" in this packet** — the
   premise in Recon B hypothesis S3 does not hold; the only server-side lever is the anim id
   value.
2. **Client asset census** (game_pak on the aaemu box, 218,069 entries): the client ships
   `game/animations/<race>/<gender>/fist/<anim_name>.caf` only for a **subset of race/gender
   models**. All 181 `npc_postures` anim ids resolve in `anim_actions`, but 42 anim names have
   no playable `.caf` for at least one model; in the sit range (25-224):
   - **ids 70** (`fist_pos_sit_crouch_investigation_idle`) and **160/187**
     (`fist_pos_sit_chair_snooze_idle`) have **no `.caf` anywhere**;
   - **26** (`lean`), **25** (`crouch`), 155/65/75/223/224/87/92/93/105/144 etc. are
     **race/gender-limited** (e.g. `lean` has no hariharan/elf assets; `chair_nursery_dealer`
     is male-only).
3. **In-zone census** (1050 spawn units, 689 with posture sets): 41 sets affected, sit sets
   **17 (lean, 5 npcs), 34 (crouch, 1), 41 (snooze, 15), 53 (investigation, 7)**. The client
   cannot play a missing animation → falls back to its default crouched pose = "knees in".

## Fix

New `AAEmu.Game/Models/Game/Units/SitPoseFallback.cs`: a data-grounded remap table
(178 entries, 36 sit anims, ids 25-224) generated from the 1.2 client pak census. At the
single wire choke point (`Unit.ModelPosture`, `ActorModelState` branch) an unplayable sit
anim id for the NPC's (race, gender) is replaced by the closest playable sit anim
(e.g. 70→224/223, 160→141, 26→141 for elves). Stand/walk anims and playable sit anims pass
through untouched. `npc_postures` data itself is unchanged (compact.sqlite3 stays read-only).

- Generator: `Tools/sit-pose-census/sit-pose-census.py` (runs on the aaemu box against
  game_pak + compact.sqlite3; re-run to regenerate the table if client data changes).

## Evidence

- Fail-before: with the `Unit.ModelPosture` remap reverted, 3/5 packet serialization tests
  fail (remap assertions); the 2 pass-through tests (stand pose 100, playable chair 87) stay
  green — no regression on non-sit poses.
- Pass-after: `SitPoseFallbackTests` 16/16, `SCUnitModelPostureChangedPacketTests` 5/5,
  full `scripts/gate.sh` green.
- ToD re-broadcast (`TimeManager`) and AI posture resets flow through the same choke point,
  so the remap applies everywhere the pose is sent.
