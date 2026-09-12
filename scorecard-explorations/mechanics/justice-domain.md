# JUSTICE DOMAIN Dossier — CRIME-01 / TRIAL-01 / PRISON-01 (2026-08-25 exploration)

Scorecard rows at writing: CRIME-01 W=1 U/C=U, TRIAL-01 W=1 U/C=U, PRISON-01 all-U ("No `PrisonManager` found"). All three are **wrong in the same direction**: the justice chain is one of the most completely reconstructed systems in the fork. The C dimension this dossier fills is **C=2** for CRIME-01 and TRIAL-01 (real engine paths, never verified end-to-end against a live client), **C=U** for PRISON-01's labor/escape sub-scope (that part genuinely does not exist).

Repo state verified before work: branch `develop` == `origin/develop` @ `214bed8342dc`.

## Verdict: signature 2014 content, substantially implemented — the gap is E2E proof, not code

- **Crime leg**: `CrimeManager` (`AAEmu.Game/Core/Managers/CrimeManager.cs`, 445 pp.) — evidence doodads (bloodstains/footprints), reporting, crime points (online + offline criminals), MySQL persistence, bot-report subsystem. Fully wired: DI singleton (`Program.cs:165-166`), `ICrimeManager : ILoadable`, save flushed through `SaveManager.cs:125`.
- **Trial leg**: `TrialManager` (`Core/Managers/TrialManager.cs`, 1033 pp.) + `TrialData` (`Models/Game/Crime/TrialData.cs`) — full state machine (`TrialStep` enum, 11 states), jury queues per mother-faction, courtroom occupancy, timed step transitions on a 5 s `TrialUpdateTask` (`TrialManager.cs:84`), verdict voting with 5 guilt tiers, arrest-on-kill. Loaded during world spawn (`SpawnManager.cs:942-945`).
- **Prison leg**: sentencing + teleport-to-jail + timed `Prisoner_Nuian`(631)/`Prisoner_Haranyan`(2028) buff exist (`TrialManager.cs:769-787`). **Labor, escape tunnels, guard NPCs: absent entirely** — PRISON-01's U was fair for that scope. **CORRECTED 2026-09-12 — see Addendum A3: release-on-expiry is NOT absent.** Both sentence buffs carry a canonical `buff_triggers` Timeout → `SpecialType.Return` → jail-exit worldgate chain that the real expiry path already executes.
- **World data**: all 4 courtrooms have jury seats (5 each), spectator seats (8/8/9/9) and judge NPCs spawned from `justice.*` special links in `Data/Worlds/main_world/doodad_spawns.json` + `npc_spawns.json`; jails are plain coordinates in `Configurations/Justice.json`.
- GM test seams already exist: `Scripts/SubCommands/Crimes/` (7 commands incl. `CrimeFakeTrialSubCommand` which fabricates evidence → reports → arrests end-to-end).

## Packet archaeology (client-1.2 opcode tables)

### C2G — 12 justice opcodes in `Core/Packets/C2G/CSOffsets.cs:106-117`, ALL present, registered, with handler classes

| Opcode | Value | Handler state | Evidence |
| --- | --- | --- | --- |
| CSCriminalLockedPacket | 0x06e | STUB (log-only) | `C2G/CSCriminalLockedPacket.cs:12` |
| CSReplyImprisonOrTrialPacket | 0x06f | Implemented → `ReplyImprisonOrTrial` | `GameNetwork.cs:124`, `TrialManager.cs:695` |
| CSSkipFinalStatementPacket | 0x070 | Implemented | `TrialManager.cs:924` |
| CSReplyInviteJuryPacket | 0x071 | Implemented | `TrialManager.cs:417` |
| CSJurySummonedPacket | 0x072 | STUB (log-only) | `C2G/CSJurySummonedPacket.cs:14` |
| CSJuryEndTestimonyPacket | 0x073 | Implemented | `TrialManager.cs:837` |
| CSCancelTrialPacket | 0x074 | Implemented (cancel == plead guilty) | `C2G/CSCancelTrialPacket.cs:16` |
| CSJuryVerdictPacket | 0x075 | Implemented | `TrialManager.cs:874` |
| CSReportCrimePacket | 0x076 | Implemented | `C2G/CSReportCrimePacket.cs:24` |
| CSJoinTrialAudiencePacket | 0x077 | Implemented | `TrialManager.cs:949` |
| CSLeaveTrialAudiencePacket | 0x078 | Implemented | `TrialManager.cs:988` |
| CSRequestJuryWaitingNumberPacket | 0x079 | Implemented | `TrialManager.cs:360` |

Adjacent: `CSNaviOpenBountyPacket` 0x0ea (`CSOffsets.cs:218`, registered `GameNetwork.cs:235`) answers the bounty-board UI with an **empty** `SCBountyListPacket` — filling real data crashes client LUA (comment block, `CSNaviOpenBountyPacket.cs:19-44`). `CSReportSpamPacket` 0x0a3 exists but belongs to the spam system, not the justice chain.

### G2C — 25 justice opcodes in `SCOffsets.cs:356-379,383`; 21 sent by live code, 4 orphaned

Wired (send-site verified): SCCrimeChanged 0x16f (`Character.cs:2968`), SCCriminalArrested 0x170 (`TrialManager.cs:623`), SCAskImprisonOrTrial 0x171 (`TrialData.cs:77`), SCInviteJury 0x172 (`TrialData.cs:201`), SCSummonJury 0x173 (`TrialData.cs:252`), SCJuryBeSeated 0x174 (`TrialData.cs:261`), SCSummonDefendant 0x175 (`TrialData.cs:162`), SCCrimeData 0x176 (`TrialData.cs:228`), SCCrimeRecords 0x177 (`TrialManager.cs:660-677`), SCChangeTrialState 0x178 (throughout `UpdateTrialStates`), SCChangeJuryOKCount 0x179 (`TrialManager.cs:864`), SCTrialWaitStatus 0x17b (`TrialData.cs:79`), SCJuryWaitStatus 0x17c (`TrialData.cs:166,307`), SCRulingStatus 0x17d (`TrialManager.cs:908`, `TrialData.cs:474`), SCRulingClosed 0x17e (`TrialManager.cs:267`), SCTrialAudienceJoined/Left 0x17f/0x180 (`TrialManager.cs:976,995`), SCTrialInfo 0x181 (`TrialData.cs:213`), SCJuryWaitingNumber 0x182 (`TrialManager.cs:368`), SCBotSuspectReported 0x184 (via `ReportBot` special effect), SCJuryPointChanged 0x18a (`Character.cs:201`).

Orphaned (class + offset exist, zero send sites): **SCChangeJuryVerdictCount 0x17a, SCTrialCanceled 0x183, SCBotSuspectArrested 0x185, SCSuspectGoingBotTrial 0x186**.

Absent from tables entirely (graded UNKNOWN, not invented): any dedicated "wanted list" query packet, bail, prison-labor, escape, and guard-interaction packets. The bounty board packet above is the only wanted-player surface the 1.2 client exposes here.

## Server/data layer — wired vs orphaned

- **MySQL**: `crime` table (`SQL/aaemu_game.sql:622-641`: criminal/victim/reporter/crime_type/coords/times/skill/func/msg/judgement_time); characters columns `crime_point`, `crime_record` (=InfamyPoint, `Character.cs:2324`), `jury_point`, six justice counters (`aaemu_game.sql:152-154,175-184`), `offline_guilty_time/region`. Load path `CrimeManager.Load:57`, write path `Save:85-168` via dirty-lists.
- **compact.sqlite3**: `report_crime_effects` (id/value/crime_kind_id, loaded `SkillManager.cs:1327-1341`), `quest_act_supply_crime_points` + `quest_act_supply_jury_points` (quest rewards → `QuestActSupplyCrimePoint.cs:21`), 19 `DoodadFuncEvidenceItemLoot` rows in `doodad_funcs`, evidence templates present in `doodad_almighties`: small/large bloodstain 877/878, footprints 3313/3314 (`DoodadConstants.cs:7-10`).
- **Known dead spot inside the chain**: `DoodadFuncEvidenceItemLoot.Use` is an empty warn-stub (`Funcs/DoodadFuncEvidenceItemLoot.cs:13-17`) — by design points are granted in `CrimeManager.ReportCrime:234-236` instead (two explicit TODOs admit this split).
- **Memory-only**: in-flight trials and jury queues (lost on restart); `DeletedEventIds`/`UpdatedEventIds` flushed only through the periodic SaveManager cycle.

## Behavioral contract (as reconstructed)

1. **Become criminal**: friendly-fire kill without prior retaliation → large bloodstain doodad owned by killer, `Data`=victim id (`CharacterCombat.cs:158-166` → `GenerateEvidenceFromKill`). Damage leaves small bloodstains (`DamageEffect.cs:398`); uprooting another's farm leaves gendered footprints (`Doodad.cs:528-530`). Same-faction-only: hostile-relation kills award honor instead (`CharacterCombat.cs:136-157`).
2. **Report**: victim uses evidence → CSReportCrimePacket → self-report refused + SusManager cheat flag (`CrimeManager.cs:182-187`) → CrimeEvent created, crime-kind/value read off the evidence's `DoodadFuncEvidenceItemLoot` func, criminal's CrimePoint+InfamyPoint raised (online via `AddCrime`, offline via direct SQL `CharacterManager.cs:1094-1109`), forwarded into any open trial, doodad phase-advanced.
3. **Wanted state**: threshold checks run inside the `CrimePoint`/`InfamyPoint` property setters (`Character.cs:159-186`) and again at login (`CharacterLifecycleService.cs:276`): CrimePoint≥50 → `Wanted`(3710) buff; InfamyPoint≥3000 → Wanted+`Contemptuous`(4832)+forced Pirate faction (`CharacterCombat.cs:426-467`). **No time decay exists anywhere** — points only clear via verdict.
4. **Arrest**: a TagWanted player killed by any player → `ArrestCriminal` (pirates exempt only in desperado zones, `CharacterCombat.cs:175-188`); revived at 1 HP, teleported to the courtroom holding cell, asked imprison-or-trial (`TrialData.EnterCourtJail:47-80`). Portals and dungeons refuse players in court (`PortalManager.cs:400`, `Dungeon.cs:148-152`).
5. **Trial**: plead-guilty shortcut (`ReplyImprisonOrTrial:705-709`) or full cycle — free courtroom claimed, defendant seated/buffed, jury invites to top-10 queued eligibles (eligibility: online, JuryPoint>0, not wanted/offender/prisoner, Nuia/Haranya mother faction, `CanAcceptTrialInvites:91-118`), 2 min summons window skipped when queue empty (`UpdateTrialStates:199-205`), testimony-confirm → closing statement (skippable) → secret ballot 0–6, majority vote; no votes = guilty default (`FinalizeVerdict:420`).
6. **Sentence**: base minutes = Σ evidence scores (murder 20, theft 8, assault 0; ×10 if victim level<30) × (1+Infamy/1000), pirates flat 40 (`CalculateJailTime:85-132`); verdict tiers scale ×0.2/0.5/0.8/1.0/1.2 (`FinalizeVerdict:439-472`). Guilty → evidence archived (KeepHistory=true keeps `judgement_time`-stamped records), CrimePoint zeroed, `Prisoner_<faction>` buff applied for the sentence, teleport to Marianople Guard Barracks or Solis Headlands Isle of Penance (`ResultIsGuilty:727-789`). NotGuilty additionally refunds InfamyPoint and un-pirates (`ResultIsNotGuilty:796-824`).
7. **Skip-a-trial exploit guard**: jail time persisted to `offline_guilty_time/region` at case creation; logging in with it set auto-runs a guilty verdict (`HandlePlayerLogin:560-573`).
8. **Escape/labor/guards**: nothing (labor, escape tunnels, guard NPCs, cell geometry are absent); `CannotEscapeBuff`(6729) is applied to defendant/jury during trial only; `AllowJuryEscape=true` config means jury may walk out instead of being teleported home (`TrialManager.cs:279-283`). **Release on sentence expiry: EXISTS and is data-driven — corrected 2026-09-12, see Addendum A3.** The earlier text here ("The Prisoner buff simply expiring drops the tag with no teleport, gate, or guard interaction coded") is falsified: 631/2028 each carry a canonical `buff_triggers` `event_id 6` (Timeout) → `special_effects` `SpecialType.Return` (value1 = worldgate 48 "Nuian Jail Exit" / 262 "Haranya Jail Exit") chain that the real expiry path (`DispelTask` → `Buff.StopEffectTask` → `BuffTrigger.Execute` → `Return.Execute`) already executes, teleporting the released prisoner to the canonical exit.

## Gaps (why W=1 was fair despite the code volume)

1. **Zero end-to-end proof**: no test, bot run, or capture exercises any of this against a live 1.2 client; `SummonJuryMember` says so itself (`TrialData.cs:240` "TODO: Verify the packet order with a real capture").
2. **Client-contract unknowns concentrated at the UI edges**: bounty list crashes client LUA; jury-verdict button time labels "TODO: Figure out how the times on the jury buttons are calculated" (`TrialData.cs:416`); SCChatToken replacement for the judge's opening text is a guess (`TrialManager.cs:215-217`).
3. **Prison interior is a bare coordinate**: no cell geometry check, no guards, no release cinematic — nothing stops a sentenced player from simply walking out of the jail coordinates while the Prisoner buff runs (min 10 s floor at `TrialManager.cs:770`). **Corrected 2026-09-12 (Addendum A3): the trailing claim "buff expiry itself triggers no release logic at all" is FALSE** — expiry runs the canonical `buff_triggers` Timeout → `SpecialType.Return` → jail-exit-worldgate chain (48/262). The walk-out-during-sentence half of this gap still stands.
4. Orphaned G2C quartet (verdict-count, trial-canceled, both bot-arrest packets) suggests unfinished parity with the 1.2 flow.
5. Two hardcoded seat-attachment IDs bypass `DoodadFuncAttachment` lookup (`TrialData.cs:264-280`).

## Sized slice plan

Implementation exists; slices are **verification verticals**, cheapest-first:

- **Slice 1 — CRIME leg (recommended)**: bot A kills same-faction bot B (unprovoked) → assert large-bloodstain doodad spawns at B with Owner=A/Data=B; B reports it via CSReportCrimePacket seam → assert A's CrimePoint/InfamyPoint rise, SCCrimeChangedPacket emitted, `crime` row written, and values survive game-server restart (MySQL reload). PASS = all asserts green + wanted buff appears at the 50-point boundary (inject via `CrimeAddPointSubCommand`). Stays UNKNOWN: client rendering of the report dialog.
- **Slice 2 — TRIAL leg**: reuse `CrimeFakeTrialSubCommand` to drive arrest → plead-guilty path only (no jury needed): assert holding-cell teleport, SCAskImprisonOrTrial payload, Prisoner buff duration, jail teleport, counter/columns updated. Then optionally the jury loop with two bots (invite→accept→testimony→verdict→sentence).
- **Slice 3 (stretch, defines PRISON-01 scope)**: decide with owner whether "imprisonment = timed buff + teleport" is the accepted 1.2 contract, explicitly descoping labor/escape to UNKNOWN forever, or whether escape mechanics get reconstructed.

## Open questions for the owner

1. Is the bounty-board UI (empty-list workaround) worth a capture session, or permanently out of scope?
2. Should the 4 orphaned G2C packets be wired (trial-cancel broadcast especially) or deleted as speculative?
3. Prison contract: is buff-expiry-walk-out acceptable, or do we owe door/guard reconstruction?
4. Wanted decay: 1.2 reportedly decayed infamy slowly — no code or data trace here; reconstruct or ignore?

## Sharpest single UNKNOWN

Whether the **client accepts this server's packet ordering during the jury summon sequence** — everything server-side composes cleanly, but `SCSummonJuryPacket` → teleport → `SCJuryBeSeated` order is explicitly flagged unverified against a real 1.2 capture (`TrialData.cs:240`), and a wrong order strands the whole TRIAL leg even though every individual piece is implemented.

---

## Addendum A1 (2026-08-25, later) — Client UI scripts mined (game_pak x2ui bytecode)

The 1.2 client's entire trial UI ships as Lua 5.1 bytecode (`game/scriptsbin/x2ui/usertrial/*.alb`, header `\27LuaQ`) and decompiles cleanly (unluac; evidence in `/root/aaemu-pak-lua/dec/x2ui/usertrial/`). The SC→UI mapping is native (x2game.dll carries no plaintext packet names — checked ASCII+UTF-16, string-obfuscated), so packet *opcodes/order* are not recoverable from scripts; what the scripts do prove is the client-side contract:

- **State machine VERIFIED**: `TRIAL_STATUS(state, cur)` event drives a 5-icon progress bar over **9 server-defined states** (`locale.trial.stateMessage[1..9]`); UI logic: `state > TRIAL_FREE && state < TRIAL_TESTIMONY` → init icon, then `TRIAL_TESTIMONY` → `TRIAL_FINAL_STATEMENT` → `TRIAL_SENTENCE` → ruling (`usertrial/trial_status.lua:79-95`). Matches AAEmu's `TrialState` ordering.
- **Jury button times TODO (`TrialData.cs:416`) → RESOLVED direction**: the client does NOT compute them. A separate `TRIAL_TIMER(state, remainTable)` event delivers remaining time, rendered verbatim via `locale.time.GetPeriodToMinutesSecondFormat(remainTable)` (`usertrial/verdict.lua:93-99`). Server must push remaining time with state changes.
- **Jury count/verdict**: `MAX_VERDICT = 6` juror buttons (`verdict.lua:1`), submit = `X2Trial:ChooseVerdict(idx)` (`verdict.lua:44`); live jury tally via `JURY_OK_COUNT`-style `remainCount(count,total)` labels and `SCChangeJuryOKCount`-fed updates (`crime_records.lua:260-267`).
- **Defendant wait window**: event `SHOW_DEPENDANT_WAIT_JURY(count, total, sentenceTime)` displays `sentenceTime / 60000` as minutes (`defendant_wait.lua:58-59`) — **confirms millisecond units** on the wait-status payloads AAEmu sends; cancel button fires `X2Trial:CancelTrial`. Same ms convention in ruling display (`ruling_status.lua:131`).
- **Complete C2S trial surface exposed by UI** (native-bound, one per button): `ReportCrime`, `ConfirmCrimeRecords`, `RequestSetBountyMoney`, `SendBountyUpdate`, `ReportBotSuspect`, `ChooseVerdict`, `CancelTrial` — nothing else. No juror "accept" API exists: seat assignment is fully server-push, consistent with `SCSummonJury`→teleport→`SCJuryBeSeated`.

On §Sharpest single UNKNOWN: scripts neither prove nor break the current send order — no Lua runs between the three sends (all native), and the UI treats the resulting events as independent (no client-side teleport logic; sitting comes from the seat doodad's attachment skill). The order question still needs a wire capture; grade remains UNKNOWN-from-scripts, but the ms-units and state-machine contracts above are now VERIFIED against the real client.

---

## Addendum A2 (2026-08-26) — Slice-1 CRIME leg verified end-to-end on a live stack

Isolated stack `jus1acc` (`E2E_ROOT=/root/aaemu-e2e-jus1`, ports 2737/2739/2750/2760/2734/2780/db 27306,
worktree `.worktrees/justice1`, branch `feat/justice-crime-vertical`). Test:
`AAEmu.IntegrationTests.E2e.JusticeCrimeE2eTests` — **PASS, 8/8 stages**:

| Stage | Result | Evidence |
| --- | --- | --- |
| PROVISION | PASS | Two Nuian bots charId 1/2, objIds live, level 40; first account carries GM access 100 (AccessLevelFirstAccount) |
| KILL-EVIDENCE | PASS | Unprovoked same-faction kill (see attribution below) → SCUnitDeath observed; large-bloodstain doodad template **878** spawned with **Owner=A(1), Data=B(2)** — proven via bridge `doodadObjId` (BcId 37249) AND MySQL `doodads` row (878/1/2) |
| REPORT | PASS | Victim B sends real `CSReportCrimePacket` (0x076) with evidence ObjId → A receives `SCCrimeChangedPacket` **(+10 delta, CP 10, infamy 10)** — value/kind from large-bloodstain `DoodadFuncEvidenceItemLoot` id 1 (crime_value 10, kind 3 murder) |
| MYSQL-CRIME-ROW | PASS | `crime` row id 4096: criminal 1, victim 2, reporter 2 (victim!), crime_type 3 |
| MYSQL-CHARACTER | PASS | `characters.crime_point=10`, `crime_record=10` for A |
| RESTART-PERSISTENCE | PASS | Hard process-tree kill of the game server after one save cycle → points identical post-reboot, crime rows reloaded by `CrimeManager.Load` |
| WANTED-SEAM | PASS | GM `/crime points self crime=45` pushes CP past 50 → `SCCrimeChanged.state=1`, which is `GetCrimeState()` computed server-side from `Buffs.CheckBuff(Wanted 3710)`; DB crime_point ≥ 50 |
| FINAL-MYSQL | PASS | characters.crime_point=145 (test applied the +45 seam more than once across its two paths — harness artifact, not engine), crime_record=10 |

### Attribution / findings

1. **Kill path deviation (documented)**: ForceAttack (CSSetForceAttackPacket 0x04f) was set and Triple
   Slash (18131) casts were accepted, but friendly-relation damage never landed in ~15 attempts — the
   CanAttack/skill-targeting gates block same-faction damage at the Nuia spawn area even under
   ForceAttack (mother-zone/safe-zone checks run before the ForceAttack branch). The kill therefore used
   the documented GM-assist fallback (IndunParty precedent): `/kill` → `Kill` command →
   `ReduceCurrentHp(character=A, …)` keeps REAL killer attribution, so `DoDie`'s friendly-fire evidence
   branch ran unchanged. Whether ForceAttack *should* pierce zone protection is an owner question.
2. **SCDoodadCreatedPacket is not pushed at evidence spawn** — it is visibility-driven
   (`Doodad.AddVisibleObject`) only. Players already in range never receive it; discovery must use world
   queries (`around`/bridge `doodadObjId`) or the persisted `doodads` row.
3. **Engine fix shipped (small, clearly correct)**: `Character.CrimePoint`/`InfamyPoint` setters now call
   `MarkDirty()`. Previously point changes did not dirty the character, so the periodic SaveManager cycle
   could skip the row and reported points would silently fail to persist across a restart whenever no
   unrelated change had flagged the character.
4. **GM sub-command syntax**: `SubCommandBase` requires key=value form — `/crime points self crime=45`;
   the space form ("crime 45") silently parses as the zero-arg query branch (misleading UX, not fixed).
5. **Code-read observation (not fixed, owner territory)**: `Character.AddCrime`'s short.MaxValue ceiling
   branch is dead — the trailing `else` overwrites the clamped value with `(short)newAmount` (wraps
   negative → setter floors to 0). Unreachable in practice (< 32757 CP required).
6. Rig half: `AAEmu.UnitTests … CrimePointsRigTests` (3 tests): AddCrime math/clamp-floor +
   SCCrimeChanged wire decode (opcode 0x16f at byte offset 6 of the captured frame), the 49→50→49
   wanted-boundary through the real setter seam (buff applied AND removed), and the new MarkDirty
   persistence guarantee. Full suite green: 2433 passed / 0 failed / 1 skipped.

---

## Addendum A3 (2026-09-12) — PRISON-01 release-on-expiry: CORRECTION, the path already exists and is data-driven

**Verdict: the earlier "release-on-expiry logic: absent entirely" claim is WRONG.** The release already
exists, is fully data-driven, and the engine's real expiry chain runs it. Evidence below is
contract/reflection-level (headless rig) plus canonical-data — **not** live/client, so `H` stays UNKNOWN.

Verified at HEAD `90ad756769ffb56dd164b8d25d30721df7e69e91` (branch `develop`), canonical DB
`compact.sqlite3` md5 `78b3bdbf038db3b927056106efdf91af` (unchanged).

### The contract, end to end

1. **Sentence buffs are canonical.** `buffs` 631 (`Prisoner_Nuian`) and 2028 (`Prisoner_Haranyan`):
   `duration 1800000` (30 min default — `TrialManager.ResultIsGuilty` overrides per sentence),
   `kind_id 2` (Bad), `save_rule_id 1` (Normal), `real_time` false. Both carry `tagged_buffs` tag
   **344** (`TagPrisoner`, the tag `CanAcceptTrialInvites`/`GetCrimeState` gate on). *(confidence: exact)*
2. **Each buff carries a canonical Timeout trigger** — the release:
   `buff_triggers` row `id 190` (buff 631) / `id 1128` (buff 2028), `event_id 6`
   (`BuffEventTriggerKind.Timeout`), `effect_id 3154` / `11573`. *(exact)*
3. **That effect is `Return`.** `effects` maps 3154 → `actual_type 'SpecialEffect'`, `actual_id 563`;
   11573 → `actual_id 3129`. `special_effects` 563 / 3129 both have
   `special_effect_type_id 25` = **`SpecialType.Return`**, with `value1` = **48** / **262**. *(exact)*
4. **`value1` is the worldgate id, and it resolves to the jail exits.** `Data/Portal/worldgates.json`
   (shipped in this repo, added with the justice system in `e4df9b291`):
   `Id 48` = **"Nuian Jail Exit"** (zone 183, 11022/12207/134, yaw 200);
   `Id 262` = **"Haranya Jail Exit"** (zone 141, 16866/8770/149, yaw 90). *(exact)*
   Cross-confirmed in `compact.sqlite3`: `return_points` 48 = "마리아노플 출소장소" (Marianople release point),
   262 = "징벌의 섬 감옥 출소장소" (Isle of Penance jail release point). *(exact)*
   `Return.Execute` takes the `value1 != 0` branch → `PortalManager.GetWorldGatesById(48|262)`.
5. **The engine already runs it.** On expiry: `DispelTask.Execute` → `Buff.ScheduleEffect` →
   `Buff.StopEffectTask` → `Buff.Events.OnTimeout` → `BuffTrigger.Execute` (subscribed by
   `BuffTriggersHandler.SubscribeEvents`, `BuffEventTriggerKind.Timeout` case) → `SpecialEffect.Apply`
   → `Type.GetType("…SpecialEffects.Return")` → `Return.Execute` → `SCLoadInstancePacket` +
   `SCTeleportUnitPacket(TeleportReason.MoveToLocation)` with the jail-exit coordinates. *(exact — code
   path; the `Type.GetType` resolution from inside `AAEmu.Game` was explicitly confirmed)*

So the earlier dossier line "the Prisoner buff simply expiring drops the tag with no teleport" is
falsified: expiry **does** teleport the released prisoner to the canonical jail exit.

### Provenance of the claim (what was actually executed)

`AAEmu.UnitTests/Game/Core/Managers/PrisonReleaseRigTests.cs` drives the real chain with
the **shipped** data — real `SkillManager.Load()` from canonical `compact.sqlite3`, real `PortalManager.Load()`
from the shipped `Data/Portal/*.json` — and the real `DispelTask`:

| Test | Asserts |
| --- | --- |
| `SentenceBuffs_CarryCanonicalTimeoutReturnTrigger` | 631/2028 each carry exactly 1 `Timeout` trigger → `SpecialType.Return` with `value1` 48/262, and each gate resolves in the portal data. Guards the data fact the contract rests on. |
| `SentenceBuffExpiry_MovesPrisonerToCanonicalJailExit` | **Pre-fix FAIL** `AssertionException: Expected to be 11022`; post-fix PASS. Buff gone, `TagPrisoner` gone, position == the canonical exit, one `SCTeleportUnitPacket` with `reason 14` (MoveToLocation) + an `SCLoadInstancePacket`. Covers both factions. |
| `OrdinaryBuffExpiry_EmitsNoTeleportAndLeavesPosition` | Canonical trigger-free Good buff 15: no teleport frame, no load-instance frame, position unchanged. |
| `SentenceIsPersisted_OrdinaryBadDebuffIsNot` | (see A3b) Sentence persisted; ordinary Bad 2167 refused; short Good still refused; a Finished sentence still refused. |
| `ResumedSentenceKeepsRemainingTime_NotFullDuration` | (see A3b) 30-min term with 18 min elapsed resumes at ~12 min, `Duration < 1_800_000`; `TagPrisoner` intact; real-time rows still tick down. |
| `ResumedSentenceStillReleasesToCanonicalJailExit` | (see A3b) Resumed sentence releases to the canonical exit through the same data-driven trigger, both factions. |

The release pre-fix failure was produced by temporarily suppressing the `BuffEventTriggerKind.Timeout`
subscription in `BuffTriggersHandler.SubscribeEvents` (the real integration point; the release is
data-driven, so there is no line of prison code to revert).

**At the time of this initial exploration, no production change was required or made** — the release
already existed. The logout hole found below was subsequently ruled in scope and fixed; see A3b.

### Grade implication (owner-defined contract, honest dimensions)

Graded against the owner's stated contract — **"the sentence must persist for its full duration and
release on expiry"** — with labor/escape tunnels/guard NPCs/cell geometry still owner-descoped and NOT
claimed by these grades:

- **C → 2.** The full sentence contract is identified end-to-end in canonical data + code (release chain
  and persistence rule), not inferred.
- **W → 2.** Both halves run through real engine paths end-to-end: the release is the engine's own
  data-driven expiry chain, and persistence/resume is the engine's own `SaveActiveBuffs`/
  `LoadActiveBuffs` path (the loader was refactored onto the tested `ComputeRemainingMs` seam).
- **A → 1.** Rig-level only. The tests drive the real engine and real canonical/portal data, but they are
  headless contract tests — no live bot/client run. The live MySQL round trip below was a throwaway probe,
  and repo convention is explicit that unit rigs must not open MySQL (`PropertyRestartRestorePolicyTests`),
  so it is not a committed test and cannot raise A.
- **H stays U** — no human has played it. Only a real playthrough moves H, and it must never be recorded
  as H=2 from rig/contract evidence.
- **R stays U** — restart/crash recovery of a *live* character mid-sentence is not exercised (the
  round-trip that was run is save→load, not a process kill).
- **S stays U** — no soak/load budget claimed.

### A3b (2026-09-12, later same day) — the logout hole is FIXED (owner ruled it in scope)

**The hole.** `ShouldPersistBuff` (`AAEmu.Game/Models/Game/Units/Buffs.cs`) returned **false** for
631/2028: `save_rule_id 1` (Normal) + `kind_id 2` (Bad) hit the "don't save debuffs" rule. Logout runs
`Buffs.CancelAllEffectTasks()` then saves, so a mid-sentence logout returned the player with **no Prisoner
buff, no timer, and therefore no expiry release** — the sentence was silently voided.
`OfflineGuiltyTime` is *not* a backstop: it is zeroed in `ResultIsGuilty` (`TrialManager.cs:753`) at
sentencing time and only ever set at case creation (`TrialData.CalculateJailTime:130`), so it covers the
*skip-the-trial* exploit, not a lost sentence.

**The fix (one production file, additive).** `AAEmu.Game/Models/Game/Units/Buffs.cs`:

1. `IsPrisonSentence(buff)` — true when the buff carries `BuffConstants.TagPrisoner` (compact.sqlite3
   `tagged_buffs` tag 344). **Tag-driven, no buff id hardcoded**, so a data refresh that re-points the tag
   needs no code change. Degrades safely to `false` when the tag table is unavailable.
2. `ShouldPersistBuff` consults that exemption after the generic expiry/state guards but **before** the
   SaveRule differentiation, so only tag-344 sentence buffs are affected.
3. `ComputeRemainingMs(timeLeft, realTime, savedAt, nowUtc)` — the resume rule extracted as a pure,
   testable seam (the `HousingManager.ShouldLoadHouseRow` precedent). `real_time = 0` (what both sentence
   buffs use) means the timer was paused offline, so the saved remaining time resumes verbatim.
   `LoadActiveBuffs` now calls it instead of its inline duplicate of the same logic (behaviour-preserving).
4. `ShouldPersistBuff` widened `private static` → `internal static` for the test seam — the same visibility
   the repo already uses for `ShouldLoadHouseRow` / `ShouldLoadPersistentDoodad`.

**No release logic was added.** The resumed buff is re-applied through `Buffs.AddBuff`, which re-subscribes
the canonical `Timeout` trigger, so the resumed sentence expires into the existing data-driven release.

**Evidence.**

| Check | Result |
| --- | --- |
| `SentenceIsPersisted_OrdinaryBadDebuffIsNot` | **Pre-fix FAIL** (2 tests red) → post-fix PASS. Sentence persisted; ordinary Bad 2167 refused; short Good refused; Finished sentence refused. |
| `ResumedSentenceKeepsRemainingTime_NotFullDuration` | 30-min term, 18 min elapsed → resumes at ~12 min, `Duration < 1_800_000`, `TagPrisoner` intact; real-time rows still tick down. |
| `ResumedSentenceStillReleasesToCanonicalJailExit` | Resumed sentence releases to the canonical exit (48/262) via the same trigger, both factions. |
| Live MySQL round trip (throwaway probe, not committed) | `character_active_buffs` row `buff_id=631 duration=1800000 time_left=719975 real_time=0` → after load: buff present, **resumed 719969 ms (~12.0 min of a 30 min term)**, `TagPrisoner` present, ordinary Bad 2167 **absent**. |

Verbatim pre-fix failure captured: `failed SentenceIsPersisted_OrdinaryBadDebuffIsNot … AssertionException:
Expected to be true` and `failed ResumedSentenceKeepsRemainingTime_NotFullDuration … AssertionException:
Expected to be true`; `total: 6 / failed: 2 / succeeded: 4`. Post-fix: `total: 6 / failed: 0 /
succeeded: 6`. The revert was applied to the fix line only and restored byte-identically.

### Still absent (unchanged, out of scope per owner)

Labor, escape tunnels, guard NPCs, cell geometry, release cinematic. Note the dossier's earlier
"nothing stops a sentenced player from walking out of the jail coordinates while the Prisoner buff runs"
observation still stands for the *duration* of the sentence — only the *expiry* release exists, and the
sentence now survives logout and resumes at its remaining time.
