# PVP-01 Domain Dossier (2026-08-25 exploration)

Scorecard row at writing: PVP-01 all-U (`SCORECARD.md:97`). Sibling lanes NOT duplicated here:
**CRIME-01** (justice chain — evidence → trial → prison; referenced as input/output boundary only)
and **ZONE-01** (conflict state machine — landed 0482ba3f0 2026-08-23/24; cited where it gates PvP).
Dominion/castle sieges are their own later system (AGENTS.md terminology table) — boundary-noted only.
Format exemplar: `scorecard-explorations/mechanics/indun-domain.md`. Graph snapshot
`graphify-out/GRAPH_REPORT.md` was built @ `2b4b99c0` (pre-ZONE-01) and used as navigation aid only;
every claim below is re-grounded in current-tree file:line reads.

## Verdict: the open-world PvP substrate is substantially implemented and partially live-verified

- **Faction model**: data-driven (`compact.sqlite3` `system_factions` + `system_faction_relations`,
  loaded at `Core/Managers/World/FactionManager.cs:41,70`), resolved per-pair via mother-faction
  fallback (`Models/Game/Faction/SystemFaction.cs:24-71`). Player faction persisted on the MySQL
  character row (`faction_id`/`faction_name`, `SQL/aaemu_game.sql:138-139`; load at
  `Models/Game/Char/Character.cs:2309-2310`).
- **Aggression**: single chokepoint `BaseUnit.CanAttack` (`Models/Game/Units/BaseUnit.cs:54-138`)
  gating all damage (`Skills/Effects/DamageEffect.cs:355-357`). Self-flag = ForceAttack
  (client Ctrl+F): opcode 0x04f wired end-to-end including Bloodlust buff 1482 and
  SCForceAttackSetPacket broadcast (`Models/Game/Units/Unit.cs:684-698`).
- **Kill rewards**: full honor economy on death — Conflict/War-zone tiered awards (10/20 solo,
  killer+assist shares), assist windows (damage/heal/CC, 30 s), War-zone victim honor loss,
  `PvpHonorRate` config multiplier (`Models/Game/Char/CharacterCombat.cs:220-301`;
  `Configurations/World.json:27`).
- **Death penalties**: escalating respawn waits (15→240 s), exp loss w/ 80 % recoverable,
  durability loss, trade-pack drop to floor (`CharacterCombat.cs:61-110,347-394`).
- **Zone-war interaction**: Peace-state protection enforced inside CanAttack via
  `ZoneConflict.BlocksPvpDamage` (ZONE-01); kill counters escalate Tension→…→Conflict→War→Peace
  (`Models/Game/World/Zones/ZoneConflict.cs:61-104,179-201`).
- **Pirates conversion exists**: InfamyPoint ≥ 3000 → Wanted + Contemptuous buffs + hard
  `SetFaction(Pirate)` (`CharacterCombat.cs:426-445`, `CrimeManager.cs:25-26`).

## 1. Faction data + code

### Storage model (VERIFIED)

| Layer | Where | Evidence |
| --- | --- | --- |
| Static factions | `compact.sqlite3` `system_factions` (id, owner_name, owner_type_id, political_system_id, mother_id, aggro_link, guard_help, is_diplomacy_tgt) | `FactionManager.cs:41-63`; names via localization table (`FactionManager.cs:51`) |
| Static relation matrix | `compact.sqlite3` `system_faction_relations` (faction1_id, faction2_id, state_id) → bidirectional dicts on each SystemFaction | `FactionManager.cs:70-89` |
| Per-character faction | MySQL `aaemu_game.characters.faction_id` (+denormalized `faction_name`) | `SQL/aaemu_game.sql:138-139`; read `Character.cs:2309-2310`; headless bots construct the same pair (`Bots/HeadlessSession.cs:388-389`) |
| Runtime | `Character.Faction : SystemFaction` + `FactionName`; transient swaps (duels) kept in `DuelManager.SaveFactions` | `BaseUnit.cs` Faction member; `Core/Managers/DuelManager.cs:29,104-130` |

Root-faction ids (`Models/StaticValues/FactionsEnum.cs`): Friendly=1, Neutral=2, Hostile=3,
NuiaAlliance=148, HaranyaAlliance=149, RedTeam=159, BlueTeam=160, **Pirate=161**, PcFriendly=165.
Player races start as child factions (e.g. Nuian=101) whose `MotherId` is the alliance —
relation resolution collapses children to their mother before comparing
(`SystemFaction.cs:28-29,53-54`).

### Resolution order in CanAttack (VERIFIED, BaseUnit.cs:54-138)

1. Null faction on either side → attackable (fail-open, :56-57); self → false (:58-59).
2. Zone faction from target's zone key; unknown zone faction id (e.g. Diamond Shores FactionId 5
   in 1.2) falls back to Neutral (:64-74).
3. **Mother-zone shield**: a player whose faction's MotherId matches the zone's faction is
   unattackable unless attacker `IsActivelyHostile` or target flagged (:76-93).
   `IsActivelyHostile` = rolling 30-s-ish hostility map fed by `DamageEffect.cs:415`
   (`Character.cs:1479-1488`).
4. **Target flagged (Retribution buff 2167)** + Friendly relation + not same team → true (:84-99).
5. **Attacker self-flagged (ForceAttack)** vs Friendly non-teammate → true (:100-103).
6. ZONE-01 peace block: `ZoneConflict.BlocksPvpDamage(conflict, relation)` — while the zone's
   conflict cycle is in Peace, any non-Hostile player-vs-player damage is refused; fail-open when
   no conflict entry (:126-135; `ZoneConflict.cs:48-56`). This is the 2026-08-24 chokepoint change.
7. Fallback: attackable iff relation == Hostile (:137).

Known wart (documented upstream too): `SystemFaction.GetRelationState` returns
**Friendly** (not Neutral/Hostile) when a mother faction has no explicit relation row
(`SystemFaction.cs:65-66` TODO) — same-faction-default behavior is accidental-friendly.

## 2. Flagging + aggression packet inventory (CSOffsets.cs @ D4E6, client_12_r208022)

### Exists AND wired

| Opcode | Packet | Status | Evidence |
| --- | --- | --- | --- |
| 0x04f | CSSetForceAttackPacket | Implemented: bool → `Unit.SetForceAttack` → Bloodlust buff 1482 ("// Ctrl+F") + `SCForceAttackSetPacket` broadcast | `CSOffsets.cs:78`; `CSSetForceAttackPacket.cs:6-12`; registration `GameNetwork.cs:95`; `Unit.cs:684-698`; `BuffConstants.cs:24` |
| 0x050 / 0x051 | CSChallengeDuelPacket / CSStartDuelPacket | Implemented → DuelManager (see §Duel bounds addendum) | `CSOffsets.cs:79-80`; handlers delegate at `GameNetwork.cs:96-97` |
| 0x06e-0x079 | Crime/trial chain (CSCriminalLocked … CSRequestJuryWaitingNumber) | Registered; owned by CRIME-01 lane | `CSOffsets.cs:106-117`; registrations `GameNetwork.cs:124-134` |

There is **no dedicated "flag toggle" packet beyond 0x04f and no CSHateTarget-style C2S packet**
— client-side hate/aggression is expressed through the generic skill pipeline
(CSStartSkillPacket 0x052), and server-side hostility bookkeeping happens in DamageEffect /
aggro tables, not packets (VERIFIED by full CSOffsets read; nothing named Hate/Aggro/Blood/Pvp
exists besides the above).

Client feature gate `useForceAttack = 193` exists in the feature-bit enum sent during login
(`Models/Game/Features/Feature.cs:145`) [INFERENCE: controls whether the client shows Ctrl+F].

### Exists but STUB (log-only Read, no mutation, no G2C reply)

| Opcode | Packet | Evidence |
| --- | --- | --- |
| 0x015 | CSFactionImmigrationInvitePacket | `GameNetwork.cs:40`; handler logs invitee only |
| 0x016 | CSFactionImmigrationInviteReplyPacket | logs unkId/unk2Id/answer |
| 0x017 | CSFactionImmigrateToOriginPacket | log only |
| 0x018 | CSFactionKickToOriginPacket | log only |
| 0x019 | CSFactionDeclareHostilePacket | `CSFactionDeclareHostilePacket.cs:8-13` — reads uint, logs |

### G2C side (SCOffsets.cs @ 6EFF)

Wired and sent: `SCForceAttackSetPacket` 0x43 (`Unit.cs:697`; trial force-off
`Models/Game/Crime/TrialData.cs:187`), `SCUnitFactionChangedPacket` 0x1b
(`Unit.cs:1007`), `SCConflictZoneStatePacket` 0xee (`ZoneConflict.cs:132`),
`SCConflictZoneHonorPointSumPacket` 0xef (`CharacterCombat.cs:299`),
`SCUnitPvPPointsChangedPacket` 0x204 (`CharacterCombat.cs:147-148,278`),
`SCDuel*` 0x8b-0x8f (`DuelManager.cs:56,137-146,225-244`), `SCFactionListPacket` /
`SCFactionRelationListPacket` on login (`FactionManager.cs:99-129`).

Defined but NEVER sent (dead until immigration/hostility-declare work):
`SCFactionSetRelationStatePacket`, `SCFactionDeclareHostileResultPacket`
(0 usages outside their own files, verified by grep 2026-08-25).

## 3. Honor / kill rewards (VERIFIED — engine path + headless rig)

- Columns: `honor_point` int default 0 and `pvp_honor` int default 0 on characters
  (`SQL/aaemu_game.sql:150,156`); NPC templates carry `honor_point` for other purposes
  (`NpcManager.cs:557`).
- Award site: `Character.DoDie` → hostile-relation kill → `AwardPvpHonor`
  (`CharacterCombat.cs:129-157,220-301`).
- Formula (hard-coded constants × `AppConfiguration.World.PvpHonorRate`, `World.json:27`):
  - Conflict zone: solo 10; with assists killer 6 + 4 per assist.
  - War zone: solo 20; with assists killer 16 + 4 per assist.
  - Everything else (Peace/Tension/Danger/Dispute/Unrest/Crisis or no conflict entry): **no honor**
    (`CharacterCombat.cs:222-238`).
- Assists: 30-s window of damage-to-victim + heals-to-killer + active CC casters
  (`CollectAssists`, `CharacterCombat.cs:24-59,303-342`; heal capture `HealEffect.cs:154`;
  damage capture `Character.cs:2057`). Offline-only assists collapse back to solo award (:252-260).
- Victim penalty: −10 honor (clamped ≥ 0) for dying to a hostile kill in a **War** zone
  (`WarZoneHonorLoss`, `CharacterCombat.cs:28,151-156`).
- Broadcasts: `SCUnitPvPPointsChangedPacket` kind 0 = HonorGainedInCombat, kind 1 =
  HostileFactionKills (:146-148); `SCConflictZoneHonorPointSumPacket` keyed on **ZoneGroupId**
  not ZoneKey (comment documents that bug being fixed, :293-300).
- Persistence: `ChangeGamePoints(GamePointKind.Honor)` mutates HonorPoint +
  `SCGamePointChangedPacket`; character row saved through the normal save cycle
  (`Character.cs:1646-1664`).
- Rate/period gating: only `PvpHonorRate` config exists; there is **no diminishing-returns /
  per-victim cooldown** system anywhere in the tree [VERIFIED absence by search].
- Rig evidence: `AAEmu.UnitTests/Game/Core/Managers/PvpFlaggingRigTests.cs` drives real
  `Character.DoDie` headlessly (hostile kill awards HostileFactionKills/honor; friendly-fire kill
  does not) — rig-level A, not live-stack.
- Honor sinks exist: honor-priced shop items (`ItemManager.cs:1077`, spend path
  `CSBuyItemsPacket.cs:88-154`).

## 4. Zone war interaction (ZONE-01 boundary, what's present vs missing)

Present (VERIFIED):
- `conflict_zones` table drives per-zone-group machines (`ZoneManager.cs:134-163`); boot state is
  data-driven Peace unless legacy `World.ConflictZonesStartAtConflict` test flag set
  (`ZoneManager.cs:166-173`; `Configurations.cs:90-93`).
- Kill-counter escalation Tension→Danger→Dispute→Unrest→Crisis→Conflict via `NumKills[0..4]`
  (`ZoneConflict.cs:61-104`); timed tail Conflict→War→Peace→(Tension | direct Conflict for
  ocean zones without kill counters) (`ZoneConflict.cs:179-201`); every transition broadcasts
  `SCConflictZoneStatePacket` server-wide (:123-138) and login resyncs clients
  (`CharacterLifecycleService.cs:237-239`).
- Effect on permissions: exactly one hook — Peace blocks non-hostile PvP in CanAttack (§1 step 6).
  War itself adds NO new attack permission beyond what relations already allow; its effects are
  the doubled honor (§3), victim honor loss, and `DiedInPvpWarZone` (Leech debuff marker,
  `CharacterCombat.cs:39-44,143-144`). Faction return points per conflict group are consumed on
  resurrection (`NuiaReturnPointId`/`HariharaReturnPointId`, `ZoneConflict.cs:26-27`;
  `Models/Game/Char/CharacterResurrection.cs:99-106`).
- Tests: `AAEmu.UnitTests/Game/Models/Game/World/Zones/ZoneConflictTests.cs` (headless state machine).

Missing for a *real* war cycle (all VERIFIED-absent from the tree):
1. No war **declaration** input — CSFactionDeclareHostilePacket (0x019) is a stub and its result
   packets are unsent; wars today start only by timer escalation out of Conflict.
2. No player-driven conflict seeding — kills raise states, but nothing creates a `ZoneConflict`
   entry at runtime or toggles `Closed`.
3. War towers / siege objects (`WarTowerDefId` field exists, `ZoneConflict.cs:28`) have no
   spawn/interaction wiring in this lane.
4. Boundary note: Dominion/castle sieges (SCSiegeAlertPacket 0xed sits next to the conflict
   opcodes in `SCOffsets.cs:233`) are a separately scoped later system — pirate exclusions for
   sieges already exist as message strings only (`ErrorMessageType.cs:431,443`).

## 5. Pirates + faction switch (the faction-choice moment)

Two distinct paths exist:

1. **Punishment conversion (VERIFIED, engine-wired)**: `CheckWantedThreshold()` — called on login
   (`CharacterLifecycleService.cs:276`) and on crime/infamy point changes
   (`Character.cs:167,182`) — if `InfamyPoint >= 3000`
   (`CrimeManager.PirateCrimePointThreshold`, `CrimeManager.cs:26`): add Wanted 3710 + Contemptuous
   4832 buffs and `SetFaction(FactionsEnum.Pirate)` (`CharacterCombat.cs:426-445`).
   `Unit.SetFaction` swaps the faction, broadcasts `SCUnitFactionChangedPacket(old,new,false)`,
   and manages Contemptuous (`Unit.cs:999-1015`). Reversion after trials is justice-lane logic
   (`TrialManager.cs:818-821`: InfamyPoint ≤ 0 && Pirate → restore race-template faction) —
   cross-ref CRIME-01, not duplicated here. Pirate jail cap 40 min
   (`Models/Game/Crime/TrialData.cs:124-127`); pirates only arrestable outside
   PirateDesperado zones (`CharacterCombat.cs:176-188`; flag from zone groups
   `ZoneManager.cs:119,289-298`); pirate chat channel exists (`ChatManager.cs:43`);
   pirate respawn/recall points shipped in world JSON (`Data/Worlds/**/respawns.json`,
   `recalls.json`).
2. **Voluntary immigration/player-nation flow (STUB)**: the four 0x015-0x018 packets plus
   error strings (`ErrorMessageType.cs:676-679`) are placeholders; a player cannot currently
   switch Nuia↔Haranya↔Pirate by choice. There is also **no NUI/item-driven faction-change
   interaction**: no doodad func or item skill mutates character faction outside SetFaction
   callers found (duel temp-swap, trial revert, pirate conversion, GM/bot construction)
   [VERIFIED by exhaustive `SetFaction(` call-site review].

## 6. Behavioral contract + sized slice 1

Flow maps (intent → request → validation → mutation → broadcast → persistence):

- **Turn pvp on/off**: Ctrl+F → CS 0x04f → (no validation beyond feature presence) →
  `ForceAttack=true` + Bloodlust 1482 → `SCForceAttackSetPacket` area-broadcast → none
  (memory only, lost on relog) (`Unit.cs:684-698`).
- **Aggress same-faction player**: skill use → `Skill`/`DamageEffect` → `CanAttack` gate
  (passes if flagged/ForceAttack, fails in Peace conflict zones) → on pass: caster purple
  (Retribution 2167 via `SetCriminalState`, `Unit.cs:666-682`), assault lists updated,
  `GenerateEvidenceFromDamage` (**handoff = CRIME-01 input**, `DamageEffect.cs:378-400`) →
  normal damage broadcast → evidence rows persist via `SaveManager` (`SaveManager.cs:125`).
- **Kill rewards**: death → DoDie → relation check → honor split + zone-kill counter →
  SCUnitPvPPointsChanged + SCConflictZoneHonorPointSum + SCGamePointChanged →
  characters save cycle.
- **Die in pvp zone**: DoDie → respawn wait escalation, exp/durability loss, pack drop,
  war-zone honor loss, arrest handoff if wanted (justice-lane input,
  `CharacterCombat.cs:61-189`).
- **Faction switch**: punishment path live (§5.1); voluntary path absent.

### Slice 1 proposal — "flagged-aggression handshake on the live stack" (size S–M)

Reuses proven seams: bot party E2E stack (indun lane proved two live clients + charPos bridge),
`Test-AaemuAssets`-verified assets, existing headless rigs stay as unit-level cover.

Steps:
1. Bot A sends CS 0x04f (on) → assert B receives `SCForceAttackSetPacket(objA, true)` and A
   carries buff 1482 (buff probe).
2. In an unclaimed/no-conflict zone: A damages B (same starting faction) via skill → assert A
   turns purple (Retribution 2167), `AssaultedBy`/`AssaultOn` populated both sides, blood-stain
   evidence doodad spawned (crime-lane input observable without touching trial code).
3. Same attempt inside a Peace-state conflict zone group → assert damage refused
   (CanAttack false ⇒ hp unchanged) — this live-verifies ZONE-01's enforcement which today is
   rig-only.
4. A kills B → assert honor award path fires (honor_point delta on A; zone-kill counter bump),
   B's death penalties applied (respawn wait > 0, pack dropped if carrying one).

PASS criteria: all five assertions green against the real Game server over TCP (no test doubles);
peace-block refusal demonstrated in the SAME binary as the allowed kill; zero source changes
required (pure verification slice). Stretch (not required to PASS): repeat step 2 with ForceAttack
OFF to prove the negative case (CanAttack refuses friendly unflagged damage).

Explicitly out of scope for slice 1: honor-shop spending, duel lifecycle (DUEL-01 has its own
rig + W=2), trial/prison, voluntary faction switching.

## Addendum 2026-08-25 — packet-gap sweep answers

**(a) MAIL-01: CSReturnMailPacket 0xfff placeholder — is there a REAL mail-return opcode in the
1.2 table?** Verdict: **UNKNOWN definitively; best candidate is the unnamed gap 0x0a2, graded
STRONGLY_INFERRED-at-best.** The maintained 1.2 r208022 table (this fork `CSOffsets.cs:155-158`
and upstream AAEmu develop byte-identical, re-read from raw.githubusercontent.com today) names the
full mail block 0x098 send, 0x09a list, 0x09b list-continue, 0x09c read, 0x09d take-item, 0x09e
take-money, 0x09f take-sequential, 0x0a0 pay-charge, **0x0a1 delete, [0x0a2 unnamed gap], 0x0a3
report-spam** — no named return opcode. The server-side ack DOES exist:
`SCMailReturnedPacket = 0x121` (`SCOffsets.cs:282`, one internal caller in the rig-tested
return implementation), proving the client protocol family expects returns. Community opcode
lists (zone-game.info) associate return-mail with **0x0a1** — but that collides with 1.2's
CSDeleteMailPacket, so those lists are evidently for a different client version and cannot be
imported (AGENTS.md: "Do not invent opcodes"). Note the stale guess in our own tree:
`GameNetwork.cs:174` has a commented `RegisterPacket(0x0a1, …)` which would shadow Delete.
Resolution requires a live 1.2 client capture of the mail Return button; until then the honest
answer is "gap slot 0x0a2 is the only free candidate adjacent to the mail block."

**(b) DUEL-01 bounds — what packet/geodata defines duel rings?** Verdict: **neither — bounds are
a hardcoded radius around a spawned combat-flag doodad** (answers why W=2/A=1 needs "live-stack
geodata" only incidentally). On accept, DuelManager spawns doodad template **5014 "Combat Flag"**
at the midpoint between challenger and challenged (Z snapped via GeoData.GetHeight)
(`DuelManager.cs:74-84`), then binds both duelists' UI to it with `SCDuelStatePacket(playerObjId,
flagObjId)` (`DuelManager.cs:141-142`). The "ring" the client draws is anchored to that flag
doodad's position [STRONGLY_INFERRED for the rendering side; server never sends a radius]. Server
enforcement is a per-second poll: either duelist ≥ **75 m** (hardcoded `DistanceForSurrender`,
`DuelManager.cs:24`) from the flag surrenders (`DuelDetType` 02, comment at :222; check loop
:297-337). There is no geodata region, no ring packet, no zone involvement; duration is a flat
5-minute timer (:25,150-151). So the DUEL-01 "bounds need live geodata" caveat reduces to
verifying the flag doodad spawns on terrain correctly on the live stack — the bound itself is a
constant.

## Addendum 2026-08-25 — OWNER RULING: contested honor values aligned to KR/RU official ("keep it korean")

Contested rows P1/P2 of `formula-corroboration-2026-08-25.md` §3 are resolved by owner ruling,
citing the official RU 2.9 notes (https://archeage.ru/updates/28042016/). Landed on worktree
branch `bots/honor-krbase` @ `8d5a0fb20`:

- **P1 (Conflict-zone kill honor): 10 → 0.** The award path in `AwardPvpHonor`
  (`CharacterCombat.cs:220`) is now WAR-GATED — any zone state other than War returns before any
  award, matching RU official («kills during Conflict award 0 honor»). Zone-kill counter
  registration (`conflictData?.AddZoneKill()`) still fires for Conflict kills so the escalation
  state machine is unchanged.
- **P2 (War-zone kill honor): base 20 → 40** («40 очков чести за убийство на войне»).
  **INFERRED assist split** — RU publishes only the base; the fork keeps its existing absolute
  4-honor assist share, so 40 ⇒ killer 32 + 4/assist (was 20 ⇒ 16 + 4).
- **P3 (victim −10 clamp ≥0) and P4 (30-s assist window): UNCHANGED** (P3 confirmed exact vs RU).
- **P5 (Leech/repeat-kill diminishing returns): still out of scope**, separate owner decision.

Rig cover (`AAEmu.UnitTests/Game/Core/Managers/PvpFlaggingRigTests.cs`):
`DoDie_HostileKillInConflictZone_CountsKillButAwardsNoHonor` (war-gating regression guard),
`DoDie_HostileKillInWarZone_AwardsKillCountAndHonorAndVictimPenalty` (updated to 40-base),
`DoDie_HostileKillInWarZoneWithOnlineAssist_SplitsKiller32AndAssist4` (32+4 split).

## Addendum 2026-08-26 — CORRECTION (PB-007 root-caused): flagged aggression DOES land; the slice-1 failure was login-protection immunity + a silent crime-branch skip

This supersedes the 2026-08-25 refuted-flow note: §"Aggression" flow IS wired end-to-end.
Instrumented live trace (`[PB7]` probes, worktree `.worktrees/pvpfix`):

1. Acquisition → AoE filter → per-effect gates ALL pass for a ForceAttack-flagged
   same-faction cast of 18131 (`possibleTargets=[victim/rel=Friendly/canAtk=True]`,
   `effectsToApply=1`). The suspected second CanAttack-family gate was NOT the drop point.
2. The actual gate was `DamageEffect.Apply`'s `CheckDamageImmune` early-return
   (`DamageEffect.cs:97-104`): the victim was inside the **login-protection window** —
   buff 2423 "LoggedOn", granted at every login (`CharacterLifecycleService.cs:263`),
   carries all-type damage immunity for ~20 s (compact.sqlite3 `buffs` row). The immune
   branch broadcasts an Immune-tagged SCUnitDamaged frame and returned WITHOUT running
   `ReduceCurrentHp` OR the crime branch — hence zero HP loss, no Retribution, no bloodstain,
   while naive wire scans still "saw damage frames".
3. Engine fix: `DamageEffect.RegisterCrimeForAttempt` (extracted from the landed-damage path)
   is now invoked on the immune path too — an immuned hit is still an assault; HP protection
   itself is unchanged. Mother-zone shield, Peace-state `BlocksPvpDamage`, hostile combat, and
   non-PvP paths are untouched (regression-rig-covered).
4. Residual (PB-007 stays OPEN on this single point): Retribution 2167 SCBuffCreated is not
   observed on either bot's wire even though the crime branch provably executes server-side
   (bloodstain doodad spawns). Suspect `Buffs.AddBuff` silent early-returns
   (`Buffs.cs:424-449`) for buff 2167's stack rule.
