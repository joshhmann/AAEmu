# G7 Combat→Death — Combat Continuation through Drop Data (2026-09-20, branch develop)

READ-ONLY discovery. Nothing modified, built, or run (read-only `read`/`grep` over code,
read-only selectors over `AAEmu.Game/Data/compact.sqlite3`). G3–G6 frozen (untouched).
G6 end state (frozen): quest 251 ACTIVE, live quest-owned AutoAttack loop on a
damaging-but-alive 3475, request terminal Completed("loop started"). This stream covers
combat-continuation through drop data only: loop ownership → damage ticks → death →
corpse/loot → 4058 data. Quest credit mechanics (§B authority) re-verified, not assumed.

Canonical seam: `QuestBehavior.Dispatch → GameplayActor → engine`
(`BotActionCommandQueue` carries NO AutoAttack kind — verified zero matches in
`AAEmu.Game/Core/Managers/Bots/BotActionCommandQueue.cs` — so the queue is NOT on
this path; G6's "no queue kind exists for it by design" holds).

---

## A. Combat continuation (AutoAttack-started → damage ticks → death → request terminal)

### A.1 Who owns the loop after dispatch: the engine, nobody on the bot side

- Dispatch: `QuestBehavior.Dispatch` goal-guarded arm
  `ActorActionType.AutoAttack when proposal.Goal == CombatGoal => gameplayActor.AutoAttack(...)`
  (`AAEmu.Game/Core/Managers/Bots/QuestBehavior.cs:821`).
- `GameplayActor.AutoAttack` (`AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:779-848`)
  runs synchronously on the execution thread and returns a **terminal `Completed`
  request at dispatch time** (`:847`, `"auto-attack started ({id}) on {objId}"`).
  The terminal means "loop started," never "damage dealt" (G6 doctrine, confirmed).
- The loop itself is engine-owned: `Character.IsAutoAttack = true` +
  `Character.StartAutoSkill(skill)` (`GameplayActor.cs:844-845`), which creates a
  `UseAutoAttackSkillTask` and schedules it on `TaskManager` with the weapon's
  attack-delay as both initial and repeat interval, repeat forever (`-1`)
  (`AAEmu.Game/Models/Game/Units/Unit.cs:601-613`; delay from
  `SkillManager.GetAttackDelay`). No bot-side object pumps it afterwards.
- `GameplayActor.Tick` does NOT drive combat: it advances pending cast effects and
  enforces `_active` request timeouts only
  (`AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:4161-4184`). An AutoAttack
  request is already terminal at dispatch, so `Tick` ignores it — damage ticks
  arrive from the `TaskManager` scheduler invoking
  `UseAutoAttackSkillTask.Execute()` per repeat interval, independent of wakes.

### A.2 Cadence

- First strike is synchronous inside the dispatch: `skill.Use(Character, ...,
  bypassGcd: false, ...)` (`GameplayActor.cs:841`) — respects attack timing.
- Subsequent strikes fire per `Execute()` tick at the scheduled attack-delay;
  the task re-reads attack speed every tick and adjusts `RepeatInterval` on
  weapon-swap/buff change (`AAEmu.Game/Models/Tasks/Skills/UseAutoAttackSkillTask.cs:77-80`).
- Skill id is chosen at dispatch only: melee 2 vs ranged 4 by role/distance/weapon
  (`GameplayActor.cs:800-808`; ids `CombatDecisionTree.cs:111-112`); re-dispatch
  with the same skill completes immediately as "already active" (`GameplayActor.cs:811-815`),
  with a different skill cancels the prior task first (`:817-823`).

### A.3 Range-violation handling: pause, never cancel

Per-tick guards in `UseAutoAttackSkillTask.Execute`
(`AAEmu.Game/Models/Tasks/Skills/UseAutoAttackSkillTask.cs:31-56`):
- skill-casting (`SkillTask != null`, :44-45) or GCD active (:46-47) → skip tick, resume next tick;
- `!CanAttack(target)` (:49-50) → skip tick;
- `distance > maxRange` (:53-56) → skip tick (**pause, don't cancel**).
  Max range = equipped weapon `MaxRange`, else skill-template `MaxRange`, else 3 m fallback
  (`:124-136`).

### A.4 Stop on death + terminal states + death detection

- Stop conditions (any one ends the loop on the next tick):
  caster `Hp <= 0`, target null, target `Hp <= 0`, self-target, or `Cancelled`
  → `StopAutoAttack()`: `Cancelled = true`, `IsAutoAttack = false`,
  `AutoAttackTask = null`, `Cancel()`
  (`UseAutoAttackSkillTask.cs:36-40`, `:116-122`).
- Death detection is **HP-zero, no state flag, no event subscription**:
  `ReduceCurrentHp` clamps `Hp = max(Hp - value, 0)` + broadcasts `SCUnitPointsPacket`,
  then `PostUpdateCurrentHp` (`AAEmu.Game/Models/Game/Units/Unit.cs:373-395`);
  `PostUpdateCurrentHp` returns early if `Hp > 0`, else calls `DoDie` unconditionally
  (`Unit.cs:439-459`). `IsDead` is derived: `Hp <= 0` (`Unit.cs:946-952`).
- Engine-side stop coverage is partial: `Unit.DoDie` calls `StopAutoSkill` (which only
  sets `Cancelled`) solely inside the `CurrentTarget != null` branch and only for
  Character victim/killer combos (`Unit.cs:536-568`); `StopAutoSkill` itself leaves
  `AutoAttackTask`/`IsAutoAttack` set (rest is commented out, `Unit.cs:583-599`).
  The reliable stop is the task's **own next-tick self-termination** on target `Hp <= 0`
  (§A.4 first bullet) — death stops damage even if no `StopAutoAttack` verb is ever issued.
- Request terminal states: `Requested → Accepted → Running →
  Completed | Rejected | Interrupted | TimedOut`
  (`AAEmu.Game/Core/Managers/Bots/ActorRequest.cs:9-13`); `IsTerminal` =
  `Completed/Rejected/Interrupted/TimedOut` (`ActorRequest.cs:184-185`).
  AutoAttack dispatch outcomes: `Completed` (started / already-active) vs `Rejected`
  (target null or `Hp <= 0` at dispatch, `GameplayActor.cs:789-791`; unknown skill
  template, `:825-827`). Death never mutates the already-terminal request.

### A.5 Full chain (AutoAttack-started → death → request terminal)

`Dispatch` (`QuestBehavior.cs:821`) → `GameplayActor.AutoAttack` → request
`Completed("started")` + engine `TaskManager` loop live → per-tick `skill.Use`
damage → victim `ReduceCurrentHp` → `Hp == 0` → `PostUpdateCurrentHp` → `DoDie`
(§B) → task self-stops next tick (target `Hp <= 0`). Killer-side corroboration:
`DoDie` clears `killerUnit.CurrentTarget` and broadcasts `SCTargetChangedPacket`
(`Unit.cs:551-567`) — the G6-handoff "killer target-clear" signal.

---

## B. Death authority (where HP hits zero → corpse + respawn scheduled)

### B.1 Where HP hits zero / who marks dead / death event

- Authority: `Unit.ReduceCurrentHp` → `Unit.PostUpdateCurrentHp` → `Unit.DoDie`
  (`Unit.cs:373-395`, `:404-459`, `:484-569`). `Npc` overrides `DoDie`, not the
  HP path (`AAEmu.Game/Models/Game/NPChar/Npc.cs:839-1032`).
- Nobody "marks" dead — `IsDead` is derived from `Hp <= 0`; death = executing `DoDie`.
- `Unit.DoDie` (`Unit.cs:484-533`): `InterruptSkills`, `IsInBattle = false`,
  fires `Events.OnDeath` + `ParentWorld.Events.OnUnitKilled` + killer `Events.OnKill`
  (`:493-495`; shapes `OnDeathArgs{Killer, Victim}` `UnitEvents.cs:311-314`,
  `OnKillArgs{Target, Killer, Victim}` `UnitEvents.cs:322-325`),
  `Buffs.RemoveEffectsOnDeath`, broadcasts `SCUnitDeathPacket(ObjId, killReason,
  resurrectWaitTime, ...)` (`:517`; NPC wait = `Spawner.RespawnTime`, `:511-514`).

### B.2 Corpse: explicit? No — the dead Npc IS the corpse

- No corpse entity/class exists (verified: no `Corpse` type matched in Units/NPChar;
  the only death-behavior transition is `Ai.GoToDead()` = `BehaviorKind.Dead`,
  `AAEmu.Game/Models/Game/AI/v2/Framework/NpcAi.cs:282-284`, called from
  `Npc.DoDie`, `Npc.cs:1030-1031`). Corpse = same `Npc` object, `Hp == 0`,
  `DeadTime` set (`Npc.cs:841`), loot container attached, awaiting despawn.

### B.3 Entity persistence / ObjId stability

- The dead Npc stays a live world object until its `Despawn` time; despawn
  processing calls `npc.Spawner.Despawn(npc)` (or `obj.Delete()`) and then
  `ObjectIdManager.Instance.ReleaseId(obj.ObjId)`
  (`AAEmu.Game/Core/Managers/World/SpawnManager.cs:1136-1160`). So the corpse
  keeps its ObjId while lootable; the ObjId is **released at despawn** and
  [INFERENCE] a respawn is a fresh `Npc` instance (`NpcSpawnerNpc` spawn path sets
  `npc.Spawner`, rolls `RespawnTime`, calls `npc.Spawn()`,
  `AAEmu.Game/Models/Game/NPChar/NpcSpawnerNpc.cs:130-133`) → new ObjId, never
  the victim's. Loot-grant entry ids are derived from the corpse ObjId
  (`(ObjId << 32) | type << 16 | index`, `LootingContainer.cs:270`).

### B.4 Corpse/loot attachment + respawn start

- `Unit.DoDie` calls `LootingContainer.GenerateLoot(killer)` synchronously
  (`Unit.cs:532-533`) — loot is fixed at death (§C.2), before aggro/target cleanup.
- `Npc.DoDie` then clears aggro, clears taggers, nulls `CurrentAggroTarget`,
  calls `Spawner.DoDespawn(this)` + `Ai.GoToDead()` (`Npc.cs:1024-1031`).
- `NpcSpawner.DoDespawn` (`AAEmu.Game/Models/Game/NPChar/NpcSpawner.cs:742-777`)
  atomically: schedules respawn (`npc.Respawn = DeadTime + RespawnTime`,
  `SpawnManager.AddRespawn`) when population allows; sets
  `npc.Despawn = now + DespawnTime` **plus `LootDespawnExtensionTime` (300 s,
  `LootingContainer.cs:42`) when the container is non-empty** (`:768-773`);
  queues `AddDespawn`. (Base `DespawnTime` property value source NOT traced this
  pass — UNKNOWN beyond the property existing on `NpcSpawner`.)
- `RespawnTime` is rolled per spawn from `SpawnDelayMin..SpawnDelayMax` with a
  placeholder floor (`NpcSpawnerNpc.cs:130-133`, `:160-186`). Cited 3475 spawner
  row 3372 shows spawn-delay ≈ 48 (columns partially truncated in read output —
  exact field mapping UNKNOWN; spawner ids 3372/3710/3729/8541 re-verified via
  `npc_spawner_npcs WHERE member_id=3475`).

### B.5 Killer attribution relevance (verified G4 §B preview — holds)

- `Npc.DoDie` attribution (`Npc.cs:843-887`): TagTeam → in-`MaxLootingRange`
  (200 f, `LootingContainer.cs:32`) members eligible; else `Tagger`; else
  killer-fallback (`DoOnMonsterHuntEvents(killer)` + full XP). Eligible loop grants
  XP (level-scaled, `:949-982`) + `DoOnMonsterHuntEvents` per eligible (`:987`).
  TagShare fanout is quest-events-only, XP/loot unaffected (`:991-1022`).
- For 251 the whole hunt-event fanout is progress-neutral (no hunt acts; G4 §A.2
  re-verified via `QuestActObjItemGather.cs` — 251's only objective act is the
  ItemGather). Attribution still matters for (a) XP, (b) container `EligiblePlayers`
  / loot rights (§C.3), (c) `Killer` recorded on the container (`LootingContainer.cs:101`).
- Credit authority (re-verified, not assumed): `QuestActObjItemGather.OnItemGather`
  matches `questAct.Id == ActId && args.ItemId == 4058` → recounts live bag
  (`AAEmu.Game/Models/Game/Quests/Acts/QuestActObjItemGather.cs:70-78`); armed in
  `InitializeAction` (`:32-39`), removed in `FinalizeAction` (`:41-47`);
  `RunAct` recounts `GetItemsCount(4058) >= 3` (`:25-30`).

---

## C. Corpse/loot state (container, ownership, interaction, expiry)

### C.1 Container creation — exists on the unit, filled at death

- `LootingContainer(IBaseUnit owner)` is constructed bound to its owner unit
  (`AAEmu.Game/Models/Game/Items/Containers/LootingContainer.cs:22-75`);
  `AlreadyGenerated` guard makes generation once-only — concurrent killing blows
  cannot double-fill (`:85-92`).

### C.2 Pack generation timing — at death, never at loot time

- `GenerateLoot` rolls **every** pack in `GetLootPackIdByNpcId(npc.TemplateId)`
  (`LootingContainer.cs:203-211`; lookup returns the full list,
  `AAEmu.Game/Core/Managers/ItemManager.cs:122-123`) — no default-pack filter.
- `UpdateLootState` broadcasts `SCLootableStatePacket` to eligible players
  (`LootingContainer.cs:294-303`).

### C.3 Ownership (killer/party) and loot rights

- Container-side eligibility mirrors the kill side (`LootingContainer.cs:123-196`):
  TagTeam members within 200 f → `EligiblePlayers`; else Tagger (FreeForAll);
  else killer-without-aggro fallback (logged + added, `:189-196`). Solo bot is
  covered by tagger-or-killer fallback. `lootDropRate` = max eligible
  `DropRateMul` (or killer's on fallback, `:170-196`).
- Per-entry rights in `TryTakeLootLocked` (`:393-512`): `HighestRoller` claims,
  team `LootingRule` methods (FreeForAll solo → immediate; RotateWinner /
  LootMaster / Public branches, `:441-483`), roll pooling when mandatory
  (`:490-507`). **Quest-item gate**: entry with `Template.LootQuestId > 0`
  requires `player.Quests.HasQuest(...)` else `SCLootItemFailedPacket`
  (`:414-422`) — second gate on 4058 (first is generation, §D.3).
- Granted entries are **removed** (`TryReserveLootItem`, `:709-722`); bag-full
  restores the entry (`:615-622`, `:724-730`). No duplication on retry.
- Public fallback: after `MakeLootPublicTime` (180 s, `:37`) `CanMakePublic`
  (`:789-799`) → `MakeLootPublic` forces method Public (`:771-787`).

### C.4 Interaction + despawn/expiry

- Bot path: `GameplayActor.Loot` (`GameplayActor.cs:1733-1762`): owner must
  resolve in world (`:1739-1741`), be within `MaxLootingRange` 200 f (`:1742-1743`),
  container non-empty (`:1746-1748); then `OpenBag(Character, owner, lootAll: true)` —
  the exact `CSLootOpenBagPacket` call (`:1753-1756`; cf. `CSLootOpenBagPacket.cs:81`).
  `granted = before - after`; `Completed` with 0 granted = empty/already-looted no-op
  (`:1758-1761`). (Note: the actor's 200 f gate is the tag-range constant, NOT a
  melee-interact range — loot still needs corpse proximity in practice via travel.)
- `OpenBag` lootAll grants every entry via `TryTakeLoot` then reports remainder
  (`LootingContainer.cs:332-375`).
- Expiry: unlooted corpse despawns at `DespawnTime + 300 s` (§B.4); once emptied,
  `UpdateLootState` collapses despawn to spawner time or `now + 2 s`
  (`PostLootMinimumDespawnTime`, `LootingContainer.cs:47`, `:304-323`).

---

## D. 4058 drop data (data facts from `compact.sqlite3`, read-only)

### D.1 Pack 4530 rows — exactly one

| id | group | item_id | drop_rate | min | max | pack | grade | always_drop |
|----|-------|---------|-----------|-----|-----|------|-------|-------------|
| 10014 | 0 | 4058 | 10000000 | 1 | 1 | 4530 | 0 | f |

- Verified: `SELECT * FROM loots WHERE loot_pack_id=4530` returns ONLY this row; and
  per G4 (standing) it is the only `loots` row with `item_id=4058` → **sole source**.
- Therefore for 3475→pack 4530→4058: **conditional-guaranteed, not probabilistic**:
  `group = 0` with no `loot_groups` rows for pack 4530 (verified empty query) takes
  the per-item path in `GeneratePackNewV2` (`LootPack.cs:190-213`):
  `requiresDice = floor(10,000,000 × lootDropRate × LootRate)`; grant iff
  `dice < requiresDice || AlwaysDrop` (`:204-211`). With `drop_rate = 10M` at base
  rates (`lootDropRate = 1`, server `LootRate = 1`) `requiresDice = 10M > any dice`
  → always granted **when the roll runs**. Conditions that un-guarantee it:
  quest filter (§D.3), negative `DropRateMul`, or server `LootRate < 1`.
  `always_drop = f` is irrelevant here (the 10M rate saturates the roll on its own).

### D.2 Dropping NPCs — only 3475 drops pack 4530; 3475 drops 4 packs

- `SELECT * FROM loot_pack_dropping_npcs WHERE loot_pack_id=4530` → single row
  id 2093: **npc 3475 → pack 4530, default t**.
- `... WHERE npc_id=3475` → 4 rows: 2093 → 4530 (default t); 5163, 6851 → 1616
  (f); 79421 → 8055 (f). All four roll per kill (§C.2).
- Pack 1616: **zero rows** in `loots` (verified empty) → dead link, contributes nothing.
- Pack 8055: single row item 29203, `drop_rate = 1`/10M, ×1–1, `always_drop = f`
  (verified) → wins its roll ~1 in 10M; expected contribution ≈ 0.
- Net per 3475 kill (quest holder, base rates): **4058 ×1** + ~never 29203 ×1.

### D.3 Quest-only? Effectively yes — two gates

- Item 4058 row (`SELECT * FROM items WHERE id=4058`): `loot_quest_id = 251`
  (name `솔즈리드 멧돼지 고기`), `max_stack_size = 10`, `bind_id = 2`,
  `level_requirement = 0`.
- Gate 1 (generation): quest items skipped unless the killer `HasQuest(LootQuestId)`
  (`LootPack.cs:194-201`; `player` = `killer as Character`, `LootingContainer.cs:208`).
- Gate 2 (taking): quest-item entry requires `HasQuest` else fail packet
  (`LootingContainer.cs:414-422`).
- So a kill without quest 251 generates no 4058 at all; a non-holder cannot take
  one either. [INFERENCE] `player = null` (non-Character killer, e.g. pet final
  blow — `LootingContainer.cs:208` passes `killer as Character`) skips the quest
  filter (`itemTemplate?.LootQuestId > 0 && player != null`), so edge-case
  generation without a holder is not excluded by the gen gate — the take gate
  still blocks non-holders.

### D.4 Modifiers / ownership / stack semantics

- Level modifiers on the drop: **none** — no level/explicit quest-state check in
  `GeneratePackNewV2`; only `HasQuest` + rate multipliers. (Level-scaled XP in
  `Npc.cs:951-982` does not touch loot.)
- Ownership effects on 4058: `lootDropRate` (max eligible `DropRateMul`, §C.3)
  multiplies `requiresDice`; at 10M base the grant survives any non-negative
  buff and only fails on negative `DropRateMul` or server `LootRate < 1`
  [INFERENCE — arithmetic reading of `LootPack.cs:204-206`].
- Stack semantics: count rolled `Random.Next(1, 1+1) = 1` (`LootPack.cs:308`);
  `max_stack_size = 10` so three kills stack trivially; grant goes through
  `Bag.AcquireDefaultItem(LootAll, 4058, 1, grade)` (`LootingContainer.cs:670`);
  inventory acquire fires `DoItemsAcquiredEvents` → `OnItemGather{4058}` → recount
  → `RunCurrentStep` flips Progress→Ready at ≥ 3 (G4 §B.1/B.4, re-verified §B.5).

### D.5 Kills expected per 4058 — exactly 1 (then 3 kills for the quest)

- Per kill (holder of 251, base rates, corpse successfully looted): **1 × 4058**.
  Quest needs 4058 ×3 (`quest_act_obj_item_gathers` 616, G4 §A.2) → **≥ 3 kills +
  3 successful loots**. No variance at base rates; variance enters only via
  sub-unit server `LootRate`, negative `DropRateMul`, missed loot window
  (despawn §C.4), bag-full restores, or contested `HighestRoller`/rolls in groups.

### D.6 Data-vs-inference ledger (this stream)

- Data facts (this pass): loots 10014 row; pack-4530 single-row; empty
  `loot_groups` for 4530; pack→npc 2093; npc 3475's 4 pack links; empty pack 1616;
  pack 8055 → 29203 @ rate 1; item 4058 full row (`loot_quest_id 251`,
  `max_stack_size 10`); spawner membership ids 3372/3710/3729/8541; all cited
  code lines.
- [INFERENCE]: respawned Npc gets a new ObjId (release + fresh spawn — allocation
  not traced to `ObjectIdManager.GetNextId` this pass); 10M/10M scale =
  guaranteed-at-base-rates (loader normalizes `drop_rate <= 1 → 10M`,
  `LootGameData.cs:40`, corroborating the 10M scale); pet-kill quest-filter
  edge case (null-player short-circuit reading); spawner 3372 delay ≈ 48
  (truncated columns).
- UNKNOWN: `NpcSpawner.DespawnTime` base value source; 3475 spawner coordinates
  (G4 cited `npc_spawns.json`, not re-read); server `LootRate`/`GoldLootMultiplier`
  live config values; `CanAttack` faction relation detail for 3475 (G4-established,
  not re-derived).
