# Senses & Contracts Gap Audit (code audit, develop @ 2026-09-18)

READ-ONLY audit. No file was modified; no server, test, or build was run. Every claim
carries a `file:line` citation. Anything not found in code is marked UNKNOWN — nothing
is inferred. Prior matrix (`scorecard-explorations/mechanics/capability-surface-matrix.md`)
established `POST /api/actors/* → BotActionCommandQueue → GameplayActor → engine` as the
canonical seam; this document audits only gaps/risks in that seam and in the production
autonomy chain (`PlayerBotScheduler → BotGoalArbiterStepExecutor → BotRoamStepExecutor →
QuestDecisionScenario / GOAP-homestead → GameplayActor`). No redesign is proposed beyond
the requested concurrency-policy sketch.

## 1. PERCEPTION AUTHORITY

### 1.1 The three perception surfaces (do not conflate)

| Surface | Entry point | Cadence | Shape |
|---|---|---|---|
| `ActorObservation` (canonical) | `GameplayActor.Observe()` `AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:202-268` | On demand per call; each `Observe()` re-queries and emits its own audit record (`:263-267`) | Immutable snapshot; objId lists only, no versions |
| `BotObservedContext` (decision copy) | `BotObservedContext.Capture(actor)` → `From(actor.Observe())` `AAEmu.Game/Core/Managers/Bots/BotDecisionProposal.cs:98-110` | Per decision; `QuestDecisionScenario` captures up to 3×/wake (`QuestDecisionScenario.cs:100,159,181`) | Deep-copied lists (`:105-109`); ActorId-bound (`BotDecisionCycle.Execute` throws on cross-actor use, `:319`) |
| `BotWorldState` (GOAP projection) | `BotWorldStateProvider.Project(bot, context)` `AAEmu.Game/Core/Managers/Bots/Goap/BotWorldStateProvider.cs:60-232` | Per `GoapPlanRunner.Tick` sense phase (`GoapPlanRunner.cs:92-93`) | 64-bit flags + labor/gold + mask; detail stays in `BotContext`/`BotMemory` (`BotWorldStateProvider.cs:15-16`) |
| `BotQuestLoopObservation.Snapshot` | `Capture(character, questLegActive, questIds)` `AAEmu.Game/Core/Managers/Bots/BotQuestLoopObservation.cs:64-107` | Per bridge `quest observe` wake | Live `CharacterQuests` + `Transform`; `questLegActive` is an executor flag, not engine state (`:46-47`) |
| `SurveyPoint` (surveyor) | `BotSurveySenses.SenseWaypoint()` `AAEmu.Game/Core/Managers/Bots/BotSurveySenses.cs:161-185` | On demand | `Observe()` + re-resolution + stealth filter + lidar; mates/slaves/transfers explicitly out of scope (`:12-14`) |

### 1.2 Per-observation table

Source key: A=authoritative live read at call time; C=cached/persisted memory (stale-able);
S=synthetic/derived (not engine truth). Refresh = when the value is re-read.

| Observation | Source class | Upstream query | Refresh cadence | Stale risk | ID invalidation on zoning/reconnect |
|---|---|---|---|---|---|
| Position | A | `Character.Transform.World.Position` (`GameplayActor.cs:236`) | Every `Observe()` | None at read instant; snapshot freezes immediately after | Transform is live; no id involved |
| Current target | A | `Character.CurrentTarget?.ObjId` (`GameplayActor.cs:237`) | Every `Observe()` | Target may die/deserialize between observe and dispatch; actor re-resolves at dispatch (`ResolveUnit`, `:4678-4685`) | ObjId is world-instance-scoped (`ResolveUnit` comment `:4682-4684`); cross-world reuse is UNKNOWN-verified — no generation/version on the id |
| HP/MP/Max | A | `Character.Hp/MaxHp/Mp/MaxMp` (`GameplayActor.cs:238-241`) | Every `Observe()` | Snapshot vs live drift within the same wake (3 captures/wake possible) | N/A (scalars) |
| Nearby characters (25 m) | A at instant | `WorldManager.GetAround<Character>(Character, 25f)` (`GameplayActor.cs:242`) | Every `Observe()` | Region-graph membership at call time; movement between observe→dispatch is unchecked (range re-gated only inside mutating actions, e.g. Talk `:957`) | ObjIds only; despawned/zoned units resolve null at use time → `RejectedAction` (fail-closed). Region null → empty list (`WorldManager.cs:1242-1243,1269`) |
| Nearby NPCs (25 m) | A at instant | `GetAround<Npc>(…, 25f)` (`GameplayActor.cs:243`) | Every `Observe()` | Same as above; stealth/invisible units INCLUDED (no `CanSeeTarget` filter — survey explicitly notes plain Observe does not apply it, `BotSurveySenses.cs:157-160,250-251`) | Same fail-closed null path (`GetNpc` null → Reject, e.g. `:951-953`) |
| Nearby doodads (25 m) | A at instant | `GetAround<Doodad>(…, 25f)` (`GameplayActor.cs:244`) | Every `Observe()` | Same; despawn-scheduled doodads listed but refused at dispatch (`Doodad.Despawn` mirror-guard `:1444-1445,:1474-1475`) | Same |
| Quest availability/state | A | `Character.Quests.ActiveQuests.Keys` (`GameplayActor.cs:245`); full rows in `BotQuestLoopObservation` (`:72-84`); offers via `DiscoverQuests` filtered by `IsDiscoverable` (`GameplayActor.cs:1210-1211,1371-1390`) | Every capture | `BotObservedContext` freezes ActiveQuestIds; `QuestDecisionScenario` re-captures post-decision (`:159`) to narrow the window, but accept/advance still TOCTOU vs engine gates | Quest ids are template ids (stable across zoning); progress lives on `CharacterQuests` (server-side, survives zoning; reconnect persistence UNKNOWN — not found in audited files) |
| Target validity (hostile/dead/in-range) | S (planner) over A reads | `ch.CurrentTarget` + `CombatDecisionTree.IsHostileTarget` + distance vs `TargetEngagementRange` (`BotWorldStateProvider.cs:200-222`); `IsDead` → `TargetDead`/`HasCorpseToLoot` (`:216-221`) | Per GOAP Tick | Flag projection only; roam hunt re-validates per wake with its own 30 s engagement cap (`BotRoamStepExecutor.cs:692-695`) | Same objId caveat as target |
| Inventory/equipment/money | A | `Character.Money/Money2/LaborPower` (`GameplayActor.cs:246-248`); `CountByTemplate(Bag/Warehouse)` (`:249-250,277-288`); Backpack-slot template (`:251-252`) | Every `Observe()` | Count snapshot; concurrent loot/craft between observe→dispatch unhandled except per-action engine gates | Instance ids (`Item.Id`) reused in ledger keys (see §4); bag-position changes across zoning UNKNOWN |
| Skills/cooldowns | NOT SENSED — gap | `Observe()`/`BotObservedContext` carry zero skill, cooldown, GCD, buff, or stance fields (`ActorObservation.cs:22-91`; `BotDecisionProposal.cs:16-64`). Only consumer: `CombatDecisionTree.IsSkillInRangeAndReady` reads `bot.Cooldowns?.CheckCooldown` + `SkillManager` template mana/range live (`CombatDecisionTree.cs:233-261`) at decision time, and `Character.Skills.Skills.Keys` as candidate pool (`:557`) | N/A (no channel) | Full TOCTOU: no observation → decision reads live cooldowns, then `GameplayActor.Cast` validates only template-exists + learned + target-resolve (`GameplayActor.cs:540-558`) and lets the engine refuse cooldown/mana/range post-`Start` (`:573-586`) | N/A |
| Doodads (state/phase) | A id list only; state re-read at dispatch | Phase/`FuncGroupId` read live inside `Interact`/`Harvest`/`Butcher` paths (`GameplayActor.cs:1447,1509-1515,3503-3620`; roam butcher `beforePhase` `:910`) | Per action | Observe carries no phase; every consumer re-resolves (`BotSurveySenses.ResolveOccupants` `:242-260`) | Despawn guard fail-closed (above) |
| Farms (soil/crop/maturity) | S + A mix | `BotMemory.Groves` records (`BotMemory.cs:139-161`, `IsMature` `:18`); live crop scan `NearestNeedsFarmCrop` + `IsValidFarmSoil` + ground-Z pin in roam executor (`BotRoamStepExecutor.cs:1429,1572-1586,1931-1939`) | Memory persists until `MarkGroveHarvested`; live scan per needs wake | `WildGroveRecord.DoodadId` + `PlantedAtUtc` + `MaturationDuration` go stale if doodad despawns/is harvested by others — maturity is clock arithmetic, not a live phase read (BLOCKER §1.3-3) | Doodad objId reuse across zoning UNKNOWN |
| Merchants | A at dispatch; C position hints | Dispatch re-resolves `ParentWorld.GetNpc` + `Template.Merchant` (+`MerchantPackId` for Buy) (`GameplayActor.cs:2708-2711,2774-2775`); GOAP proximity reads `Memory.KnownSeedMerchantPos` 15 m (`BotWorldStateProvider.cs:120-125`); needs leg scans nearest merchant live (`BotRoamStepExecutor.cs:1469-1490`) | Per action / per wake | `KnownSeedMerchantPos`/`KnownSeedMerchantNpcId` (`BotMemory.cs:35-36`) have no invalidation path found (no timestamp/TTL/version — UNKNOWN expiry) | Npc objId in `SelectedMerchantNpcId` (`BotContext.cs:20`) is world-scoped; stale after respawn/rezone |
| Party/combat state | A | `TeamManager.GetActiveTeamByUnit` + leader resolve via member list + leader target (`GameplayActor.cs:216-231`); `InParty`, `PartyOwnerId`, `PendingInvitationOwnerId`, `PartyLeader*` (`:253-258`); combat flag `ch.IsInBattle` (`BotWorldStateProvider.cs:86-87`); sit stance (`:89-90`) | Every `Observe()`/Tick | Leader position/target is point-in-time; party follow compares against `PendingLeg.Destination` with 3 m hysteresis (`BotRoamStepExecutor.cs:582-584`) | Team membership is server-side; objId leader refs same caveat |
| Zone | A (partial) | `Transform.ZoneId` in `BotQuestLoopObservation.Snapshot` (`:101`) and `SurveyPoint` + names via `ZoneManager` (`BotSurveySenses.cs:167-169`); `ActorObservation` carries NO zone field | Per capture | Consumers needing zone (conflict module `ConflictJoinActivityModule.cs:64-87`, zone-kill gate `LevelingLoopScenario.cs:1007-1009,2878-2881`) re-read `Transform.ZoneId` live — no staleness, but two zone vocabularies coexist (zone key vs zone group; group resolved via `GetZoneByKey(...).GroupId` `:2879`) | Zone id is stable; region membership re-sync on teleport handled by callers (`LevelingLoopScenario.cs:2991-2999` region move) |
| Nav state | NOT OBSERVED — gap | No waypoint/route/stuck observation exists. Move progress (`_lastProgressPosition`, `_noProgressElapsed`, `_unstickWaypoint`) is actor-private (`GameplayActor.cs:3993-3996`); route lives in roam `BotRoamState.Path` (`BotRoamStepExecutor.cs:963-976`) | N/A | External callers cannot distinguish Running-healthy vs stuck-until-`TimedOut(Navigation)`; stuck declares via `Expire(Navigation, "stuck: …")` (`:4372-4376`) which shares the budget-expiry reason (see §3) | N/A |
| Housing/land | A live scan at Tick | `HousingManager.PeekInstance.GetAllHouses().Any(h => h.OwnerId == ch.Id [+ CurrentStep == -1 …])` (`BotWorldStateProvider.cs:152-176`); positions from `Memory.KnownHousingZonePos` 25 m / `OwnedHousePos` 10 m (`:179-191`) | Per GOAP Tick | `OwnedHousePos`/`OwnedHouseId` (`BotMemory.cs:47-48`) written UNKNOWN-where (no writer found in audited files — UNKNOWN refresh); live scan is authoritative for flags, memory only for proximity | House `Id` is persistent (not objId) — safe |
| Vehicles (slave/mate/transfer) | NOT SENSED — gap | No vehicle/mount/boarded observation in `ActorObservation`/`BotObservedContext`/`BotWorldState`. Mount/board preflights read live (`MateManager.GetIsMounted` `:1971`; `SlaveManager.GetIsMounted` `:2445`; `Character.Bonding` `:2447`) at dispatch | N/A | Autonomy cannot see boarded state except via dispatch refusal (`StateTransition` already-mounted/already-boarded `:1971-1972,:2445-2448`) | N/A |
| World events (conflict/contest) | C signalling only | `ConflictJoinActivityModule.CanActivate` reads `FindActiveConflict(zoneKey)` off `Transform.ZoneId` (`ConflictJoinActivityModule.cs:50-87`); no event payload/phase/timing in any observation surface | Per arbiter pass | UNKNOWN source/refresh of the conflict registry (not in audited files) | N/A |

### 1.3 BLOCKER-class perception findings

- **B-P1 (stale objId across zoning/reconnect).** Every id-based sense (`Nearby*ObjIds`, `TargetWildFarmPos`+poi, `SelectedMerchantNpcId`, `TargetNpcObjId`, `TargetButcherDoodadObjId`, `QuestTravelTarget`) is a bare `uint` with no generation, version, or world-instance tag. `ResolveUnit` does `Character.ParentWorld?.GetUnit(objId)` (`GameplayActor.cs:4678-4685`) — a null `ParentWorld` (zoning window) yields null → `RejectedAction`, which is fail-closed per action, BUT: (a) objId reuse across worlds is not guarded anywhere found (UNKNOWN whether `GetUnit` can return a *different* unit for a recycled id — no check compares template/kind after resolve except per-action kind checks); (b) cached decisions (`BotObservedContext` held across `Discover→Select→Dispatch` in `QuestDecisionScenario.cs:100-172`; roam `PendingLeg.Destination` + `QuestTravelTarget` `BotRoamStepExecutor.cs:1231-1237`) survive across wakes with no revalidation timestamp. Severity: medium — dispatch gates are fail-closed, but wrong-target (not no-target) on id reuse is unverified.
- **B-P2 (skills/cooldowns/GCD invisible).** No observation surface carries learned skills, per-skill cooldown, GCD, mana-cost-vs-current (beyond raw Mp), buffs, or stance (except GOAP `IsSitting`). `Cast` preflights only template+learned+resolve (`GameplayActor.cs:540-558`); real refusals (`CooldownTime`, `LackMana`, range) surface post-`Start` as `RejectedAction` (`:586`). `CombatDecisionTree.IsSkillInRangeAndReady` (`CombatDecisionTree.cs:233-261`) duplicates the engine's gate (cooldown+`ManaCost`+effective range incl. weapon override `:162-182`, fallback table `:187-228`) at decision time — a second, drifting copy of game rules (see §2). `M1M2ReplayScenario` documents the GCD retry dance (`M1M2ReplayScenario.cs:636-651`) as scenario-level knowledge, not actor contract.
- **B-P3 (farm memory is clock, not world).** `WildGroveRecord.IsMature` is `PlantedAtUtc + MaturationDuration` (`BotMemory.cs:18`); nothing re-reads doodad phase. A reaped/stolen/despawned crop still reads mature; `HasActiveGroves` (`:160`) counts unharvested records regardless of world existence. The needs-farm live scan (`NearestNeedsFarmCrop`) mitigates per wake, but GOAP homestead flags (`SecretGrovePlanted` from `HasActiveGroves`, `BotWorldStateProvider.cs:103-104`) ride memory alone.
- **B-P4 (merchant/POI memory without invalidation).** `KnownSeedMerchantPos/NpcId`, `TargetWildFarmPos/PoiId` (+`TargetWildFarmPoiInvalidated` flag — the ONLY invalidation bit, `BotMemory.cs:35-40`), `KnownHousingZonePos`, `KnownWorkbenchPos`, `OwnedHousePos` (`:45-48`) have no TTL/version/source-world recorded. `TargetWildFarmPoiInvalidated` is checked (`BotWorldStateProvider.cs:127`) but its writer is outside audited files (UNKNOWN who sets it). Stale POI → `AtWildFarm`/`NearSeedMerchant` mis-fires → plan steps walk to empty ground until `TimedOut(Navigation)`.
- **B-P5 (Observe omits stealth filter; survey re-adds it).** Plain `Observe()` lists stealth/invisible units (`BotSurveySenses.cs:250-251` comment). Any decision using `NearbyNpcObjIds` directly (quest discovery sweep `QuestDecisionScenario.cs:125-132`, hunt scan) can target units a real client could not see. Only `ResolveOccupants` (`:242-260`) filters via `CanSeeTarget`.
- **B-P6 (25 m hard radius + region-edge semantics).** `Observe()` fixes 25 m (`GameplayActor.cs:242-244`); hunt uses 45 m (`BotRoamStepExecutor.cs:108-110`); survey defaults 25 m (`BotSurveySenses.cs:161`). `GetAround<T>(obj, radius)` degrades to neighbor-region scan when the radius crosses the region boundary (`WorldManager.cs:1248-1256`) and returns empty when `Region == null` (`:1242-1243`) — zoning/teleport windows read as "empty world", indistinguishable from genuinely empty.

## 2. PRECONDITIONS — where validated

Rule: behavior-layer checks (scenario/GOAP/roam) are *advisory duplicates*; actor-layer
checks (inside `GameplayActor.*` before `Start`) are *gates*; engine results
(`UseSkill`/`AddQuest`/`DoReportEvents` return codes) are *truth*. Duplication = the same
game rule evaluated in ≥2 layers.

### 2.1 Precondition map (AcceptQuest / Interact / Talk / Cast / MoveTo)

| Action | Behavior-layer duplicate (advisory) | Actor-layer gate (binding) | Engine truth |
|---|---|---|---|
| AcceptQuest | `AcceptProposal` preconditions + `IsDiscoverable` band/level filter (`QuestDecisionScenario.cs:154-157`; `BotRoamStepExecutor.cs:1317-1328` mirrors band + `IsDiscoverable`) | `TryBegin` busy+key gate (`GameplayActor.cs:4565-4596`); `questcredit:accept` effect probe + active-map check (`:797-800`); `AddQuest` return (`:802-824`) | `CharacterQuests.AddQuest` accept/refuse (`:821-824`); `IsDiscoverable` itself mirrors AddQuest preconditions without mutating (`:1364-1390`) |
| Interact (doodad+skill) | Needs `PlantProposal` seed/position/soil/doodad-allowed (`NeedsDecisionScenario.cs:276-284` + perception-time soil gate `:285-286`); roam butcher pre-resolves skill + range (`BotRoamStepExecutor.cs:888-911`) | resolve doodad-or-house (`:1407-1436`); skill-template exists (`:1437-1438`); range ≤ `MaxInteractRange` 25 m (`:1439-1440`); `Despawn` guard (`:1444-1445`) | `Doodad.Use` / house `UseSkill` result (`:1428-1432,:1447+`); `InteractWith` post-checks observable delta, else `RejectedAction` no-state-change (`:1549-1552`) |
| Talk | Quest-leg readiness implied via `TurnInProposals`/`AdvanceProposal` selection (`QuestDecisionScenario.cs:105-118`); Ready-quests excluded from advance explicitly (`:107-115`) | live `GetNpc` (`:951-953`); range ≤ 25 m (`:957-958`); per-active-quest talk-objective match; zero-credit → `RejectedAction` (`:1030-1033`) | `QuestManager.DoTalkMadeEvents` per matched quest (`:993-994`) + delta post-check |
| Cast | `CombatDecisionTree.IsSkillInRangeAndReady` (cooldown+`ManaCost`+effective range, `:233-261`) + `SelectPrioritizedSkill` combo chains (`:295-466`) + spacing logic (`:518-582`) evaluated in roam hunt/PvP branches (`BotRoamStepExecutor.cs:747-796,2339-2383`) | template-exists (`:542-543`); learned-skill (`:552-553`); target resolve (`:556-558`); `CastAt` adds reagent preflight (`:623-630`) — but NO cooldown/mana/range gate | `Character.UseSkill` `SkillResult` (`:573-586`); `CooldownTime` is the only retryable result per `CombatExecutor` (`CombatExecutor.cs:28-32,181-184`) |
| MoveTo / MoveToUnit | Roam guards (`ActiveRequest is not terminal → skip`; `PendingLeg.Destination` 2–3 m hysteresis `:754-756,:582-584`); quest/needs travel arming (`:1211-1241,:1984-1991`); soil ground-Z pin (`:1931-1939`) | `TryBegin`; speed>0 + finite (`:340-343`); already-within-`ArrivalRadius` no-op `Completed` (`:345-351`); `MoveToUnit` resolves unit once at dispatch (`:471-474`), `NavigateToUnit` likewise (`:331-335`) | Tick stepping + arrival gate flat≤0.5 m AND \|ΔZ\|≤0.5 m (`:4153`); budget `Expire(Navigation)` (`:4031-4042`); stuck `Expire(Navigation,"stuck:…")` (`:4372-4376`) |

Additional engine-mirror gates inside the actor (all fail-closed `RejectedAction`, all
duplicating packet-handler rules by comment): merchant `Template.Merchant[+MerchantPackId]`
(`:2708-2711,:2774-2775`), shop range `MaxShopRange` 3 m (`GameplayActor.cs:2693-2694` cited at
`:2707`), loot range `LootingContainer.MaxLootingRange` (`:1603`), trade range
`TradeManager.MaxTradeRange` (`:1858-1859`), board `MaxBoardRange` (`:2430-2431`), discovery
`MaxQuestDiscoverRange` (`:1153-1154` cited at `:1184`), pack slot `CanReplaceGliderInBackpackSlot`
(`:2183-2185`), trade-session liveness (`:1854-1859,:1892-1899,:1928-1929`).

### 2.2 Game-rule duplication (flagged)

1. **Cooldown/mana/range** — `CombatDecisionTree.IsSkillInRangeAndReady` (`CombatDecisionTree.cs:242-260`) vs engine skill pipeline (`Character.UseSkill` result). Drift vector: weapon-range override (`:167-176`), zero-range default 4.0 m (`:178-179`), fallback table (`:187-228`) vs engine truth. The actor deliberately does NOT gate (comment `:569-572`), so every behavior/actor/engine triple evaluation can disagree across three ticks.
2. **Quest accept legality** — `IsDiscoverable` (`GameplayActor.cs:1371-1390`: template known, no active duplicate, supply-item gate, start-component unit_reqs, non-repeatable completion) vs `QuestDecisionScenario` band filter (`:147-148`) vs roam `OffersWalkableQuest` band+discoverable mirror (`BotRoamStepExecutor.cs:1317-1328`). Three copies of "should we walk to this offerer".
3. **Zone-kill gating** — engine `OnZoneKill` carries victim zone-group but the act does not gate (engine watch item, `LevelingLoopScenario.cs:136-142,1001-1009`); the loop performs the gate at selection (`:2871-2881`). Behavior-layer game rule compensating for an engine gap — must be re-verified if the engine ever gates.
4. **Economy legality** — `NeedsDecisionScenario` funds/materials/labor/surplus-not-seed (`NeedsDecisionScenario.cs:254-260,339-340,373-376`), `EconomyDecisionScenario` money/bank/bag gates (`EconomyDecisionScenario.cs:152-236`, reads ONLY the immutable context per `:15-22`), vs actor item/money/engine gates at dispatch. Duplication is intentional (legality-before-preference), but the ranking rule (priority → personality → tie-break → index, `BotDecisionProposal.cs:283-288`) plus `MaxCandidates = 64` (`:233`) silently drops the 65th candidate (`:246-248` returns no-proposal with explanation — a decision failure, not a queue).
5. **Ready-quest advance no-op guard** — scenario-level exclusion (`QuestDecisionScenario.cs:107-115`, documents a live 60-no-op starvation incident) for what is arguably an engine/step-machine semantic (`RunCurrentStep` from Ready reports Completed). If the engine changes, the guard becomes wrong.

## 3. RESULT CONTRACT + CANCELLATION

### 3.1 Lifecycle (authoritative)

`ActorRequest` `AAEmu.Game/Core/Managers/Bots/ActorRequest.cs:9-13,120-121`:
`Requested → Accepted → Running → Completed | Rejected | Interrupted | TimedOut`, terminal
final. `Accept` from `Requested` only (`:123-130`); `Start` from `Accepted` only (`:132-139`);
`Complete/Reject/Interrupt/Expire` from `Accepted|Running` only (`CanTerminate`, `:196`).
`IsDedupeRejection` marks key-gate refusals (`:80`, set in `TryBegin` `:4587`).
Timeout reason mapping is per-action via `ActorTimeoutPolicy.ReasonFor` (Move/Drive →
`Navigation`, else `Starvation`) — actor Tick (`GameplayActor.cs:4028-4030`) and queue
backstop (`BotActionCommandQueue.cs:402`) both cite it.

Every terminal transition emits exactly one `ActorAuditRecord` via `Finish`
(`GameplayActor.cs:4640-4659`; trace capped at `MaxTraceRecords`, evicted oldest-first
`:4655-4656`). `Observe()` emits too (`:263-267`) — audit spam per perception call
(3 captures/quest-wake ⇒ 3 audit records/wake even when idle).

### 3.2 Queue deadline backstop (two clocks)

- **Actor clock:** `Tick(elapsed)` expires any `Running` request past `Timeout`
  (`GameplayActor.cs:4031-4042`), clears move/craft state, `Expire(ReasonFor(action))`.
  Craft/PutDown have their own completion polls in the same Tick (`:4049-4114`).
- **Queue clock:** drain step 2 expires in-flight entries past budget when the actor
  never finished them (e.g. scheduler stopped, nothing ticks)
  (`BotActionCommandQueue.cs:399-411`), then calls `RecordBackstopTimeout` for Q-ledger
  parity (`:404-407`; `GameplayActor.cs:4666-4676` locks the explicit key as `TimedOut`).
- **Gap:** the two clocks can race (Tick expires + backstop expires the same request);
  `Finish`/`Expire` are idempotent via `CanTerminate` (second transition returns false),
  but the *audit* taken depends on who ran first (`CaptureAudit` prefers the actor trace,
  else builds locally `:821-830`). Stuck-declaration (`"stuck: no progress …"`,
  `:4373-4374`) and budget-expiry (`"navigation budget exceeded"`, `:4038`) share
  `FailureReason.Navigation` — distinguishable only by substring on `Detail`.

### 3.3 Interrupt semantics

- `ActorRequest.Interrupt` carries NO failure reason (by design — `Expire` is used for
  reasoned timeouts; comment `:4330-4338`); `GameplayActor.Interrupt(traceId)` only
  interrupts the exact `_active` trace, else returns false (`:761-767` — idempotent
  no-match, no audit on miss).
- `Stop()` interrupts whatever is live with `"stop requested"` + `BroadcastStop` for
  Move legs (`:486-496`), then completes its own `Stop` request (`:497-499`).
- Queue `Interrupt` kind resolves the *API* trace → *actor* trace via `_history`
  (`BotActionCommandQueue.cs:618-632`) and is audited as a synthetic `Completed(Stop)`
  entry (`:463-470`) — the interrupted request keeps its own `Interrupted` record.
  `BotActionKind.Interrupt → ActorActionType.Stop` mapping (`:915`) conflates control-op
  and gameplay-stop in mapped views (matrix §B.7).
- GOAP interrupts (`DefendSelf`/`Recover`, `GoapPlanRunner.cs:98-123`) null the plan and
  reset indices WITHOUT touching the actor's in-flight request — the runner drops its
  *reference* (`CurrentActorRequest = null`) while the actor request keeps running to its
  own terminal. Replan-after-interrupt resumes from post-interrupt state (`:134-156`).

### 3.4 Target-death / zoning mid-action

- **No mid-flight watch in the actor.** `MoveToUnit`/`NavigateToUnit` snapshot target
  position at dispatch (`GameplayActor.cs:331-335,471-474`); Tick walks the snapshot —
  a moving/dying/zoning target is followed to stale coordinates until budget/stuck expiry.
- `Cast` samples target HP at acceptance for post-hoc observation (`:560-565`) but
  `Complete`s on `UseSkill == Success` regardless of later death (`:574-584`); damage
  lands via delayed `ApplySkillTask` inside a bounded 2 s window (`:85-92,576-583`).
  Observation failure ≠ action failure by contract (`:580-581`).
- **Roam-level (not actor-level) mitigations:** 30 s engagement caps
  (`BotRoamStepExecutor.cs:692-695,2309-2312`); per-wake `isDeadOrInvalid` revalidation
  driving `Loot` then disengage (`:687-738`); PvP validity re-check (`:2302-2329`).
  `AutoAttack` dispatch refuses dead targets (`GameplayActor.cs:680-681`) but nothing
  stops it when the victim dies mid-attack (roam stops it on loot-branch `:719-722` only
  in the wildlife path).
- **Zoning:** `ParentWorld == null` → fail-closed rejects at dispatch (all `?.` lookups);
  mid-flight zoning (world swap under a `Running` Move/Craft/PutDown) has NO handler
  found — Tick keeps stepping the old `_moveTarget` in the new world; craft snapshot
  comparison reads the new world's bag. UNKNOWN whether `Character.ParentWorld` can
  change under a live request; no test hook or guard was found.

### 3.5 Fire-and-forget / invisible abandonment (flagged with lines)

1. **`_ = actor.Stop()` discards the stop receipt** — 15+ sites
   (`BotRoamStepExecutor.cs:566,589,597,644,735,842,879,896,906,954,998,1456,1487,1545,1578,2357,2424,2349`).
   The stop itself is audited actor-side, but the caller never checks whether the
   interrupt landed (returns-bool ignored) — a failed stop (trace mismatch) leaves the
   old leg live while `PendingLeg = null` pretends it is gone.
2. **`_ = actor.MoveTo(...)` leisure legs** (`:1645,1675`) — request object dropped;
   outcome (including `TimedOut(Navigation)`) visible only in the actor trace.
3. **`BotDecisionCycle.Execute` non-terminal path** (`BotDecisionProposal.cs:327-328`)
   returns the live request with `TerminalObservation = null`; `QuestDecisionScenario`
   treats non-terminal as `Fail("EXECUTE", Starvation, "left the terminal surface")`
   (`QuestDecisionScenario.cs:174-179`) — but every actor method in the audited surface
   returns terminal-synchronous EXCEPT Move/Drive/Craft/PutDown (Tick-driven). A scenario
   dispatching a Tick-driven action through `BotDecisionCycle` would always "fail" while
   the leg keeps running — the abandonment is then invisible (no owner ticks it; the
   roam executor ticks only `actor.Tick` for ITS actor, `:987`).
4. **GOAP reference-drop** (§3.3): plan invalidation sets `CurrentActorRequest = null`
   (`GoapPlanRunner.cs:113-116,144-146,211-224,237-238,264-266,289-292,360-364`) while the
   actor-side request continues; the new plan's `TryBegin` then hits busy → the fresh
   leg is `Rejected(busy)` and the orphan keeps running. No `Stop()` is issued on the
   GOAP interrupt/replan paths found.
5. **Queue eviction vs `_apiOwned`:** `EvictIfNeeded` drops oldest non-live history
   past 1024 (`BotActionCommandQueue.cs:926-945`); the in-flight refresh loop treats a
   missing entry as gone (`:367-373`) — `TryRemove` + continue with NO audit, NO
   snapshot update. If eviction ever takes an in-flight entry (guarded by `live` set
   `:931-940`, so believed-safe but single-predicate), the waiter polls forever.
6. **`BotActionEnqueueResult.Failure` returns `Guid.Empty`** (`:158-159`) — callers
   polling `Guid.Empty` get UNKNOWN behavior (not traced in audited files).

## 4. OWNERSHIP + CONCURRENCY + IDEMPOTENCY

### 4.1 Decision owners (EXACTLY one intended owner per decision)

Convention: OWNER = the layer that both *decides* and *is accountable for re-deciding*;
callers that merely forward are marked via. Evidence is the code that writes the decision.

| Decision | Intended owner (exactly one) | Evidence |
|---|---|---|
| Wake cadence / lease / execution-boundary marshal | `PlayerBotScheduler` | Lease `_leases` + `_tickQueue` drain on game-loop thread (`PlayerBotScheduler.cs:38-69`) |
| Activity selection (which life the bot lives this wake) | `BotGoalArbiter` (via `BotGoalArbiterStepExecutor` decorator) | `Arbitrate` per wake, activate-on-change (`BotGoalArbiterStepExecutor.cs:32-42`); modules vote via `CanActivate` (`BotGoalArbiter.cs:161-175`); broken modules skipped, never blocking (`:164-170`) |
| Route / waypoint / patrol progression | `BotRoamStepExecutor` | `Path.Move` advance only on terminal `PendingLeg` (`BotRoamStepExecutor.cs:1000-1011`); flat-arrival `Stop` (`:990-999`) |
| Quest goal/behavior/target leg | `QuestDecisionScenario.Run` (per wake, on the live actor) | Perceive→propose→`Select`→`Dispatch` (`QuestDecisionScenario.cs:99-193`); travel fallback arms route (`:1206-1208`) |
| Needs/farm goal/behavior/target leg | `NeedsDecisionScenario` dispatch tail (per wake) | Proposal builders + `DispatchNeedsFarmLeg` (`BotRoamStepExecutor.cs:2094-2129`; `NeedsDecisionScenario.cs:243-433`) |
| Homestead goal selection | GOAP `GoalArbitrator` | `ArbitrateGoal` per Tick (`GoapPlanRunner.cs:96`) |
| Homestead behavior/target sequence | GOAP `GoapPlanner` + `GoapPlanRunner` | Plan gen/cache (`:306-346`), action-start precondition recheck (`:351-365`), status evaluation (`:160-246`) |
| Destination (where the body goes) | The leg that armed the route (`BotRoamStepExecutor` route arming: `ArmFarmRoute` `:1984-1991`, `ArmQuestTravel` `:1211-1241`, quest/farm travel branches) — NOT the scenario that requested it | Route layer owns `Path`/`PendingLeg`; scenarios only set targets via `BotPath.PathTo` |
| Retry (same logical op, again) | `GoapPlanRunner` for GOAP actions (`ActionRetryCount < MaxActionRetries`, `GoapPlanRunner.cs:189-196`, failures in `BotMemory` `:163-178`); scenario legs do NOT retry — they replan next wake | No `MaxActionRetries` consumption found outside `GoapPlanRunner`; `BotContext.MaxActionRetries = 2` (`BotContext.cs:24`) |
| Cancel (stop this leg now) | Whoever preempts owns the `Stop()` call: roam branches (listed §3.5-1), queue drain for API commands (`BotActionCommandQueue.cs:437-451`), `GameplayActor.Stop/Interrupt` as mechanism only | Mechanism ≠ owner: `InterruptActive` (`GameplayActor.cs:4598-4610`) executes, never decides |
| Replan (this sequence is dead, find another) | `GoapPlanRunner` for homestead (invalidated/failed/max-retries paths `:197-239,:360-364`); `BotRoamStepExecutor` for roam/quest/needs (next-wake re-`Run`, stale-target discard `:1602-1604,1531-1532`) | Plan null + index reset is the replan primitive |
| Completion (did the work land) | The dispatching leg, via terminal `ActorRequest.State` + post-observation (`BotDecisionCycle.Execute` `:327-332`; quest `landed` check `:1196`; needs 0b contract `:1680-1683`) | `QuestLegActive`/`NeedsLegActive`/`HomesteadLegActive` flags (`BotRoamStepExecutor.cs:648,667,678`) feed `BotQuestLoopObservation.Snapshot.QuestLegActive` (`BotQuestLoopObservation.cs:46-47`) |

Out-of-scope-but-present writers (must not own the above): `GameplayActorStepExecutor`
(legacy tick-only executor, superseded per `BotRoamStepExecutor.cs:57-60` — verify DI
binding before citing), scenario E2E drivers (`PartyInvite/Accept` direct calls, matrix
§A2), bridge wakes (`farm needs`/`quest wake` enroll+observe only, matrix §A3).

### 4.2 Queue multi-enqueue / ordering / stale-command semantics (as found)

- **Enqueue:** unbounded `ConcurrentQueue<Guid>` + `_history` dict; `Sequence =
  Interlocked.Increment` (`BotActionCommandQueue.cs:301-304`) is a diagnostic tie-break
  (used for trace sort `:334-335`), NOT execution order. No per-bot queue: commands for
  the same bot interleave FIFO-globally; drain cap 256/wake (`:202`).
- **Ordering:** single-writer per actor serializes *execution*, not *arrival*. Second
  command while API-owned-busy → `Rejected(busy)` (`:439-445`); while world-internal
  (roam/scenario) busy → preempt+`Interrupted` (`:448-451`). There is no priority,
  no deadline-ordering, no coalescing (two `MoveTo`s = second rejected, not merged).
- **Stale commands:** no TTL on enqueue (only the request `Timeout` budget, checked at
  drain `:399-400` and Tick). A command queued long before drain executes against the
  world-as-now (target re-resolved at `ExecuteKind` → actor method). History eviction
  past 1024 drops oldest terminal entries first (`:926-945`); `GET
  /api/actors/actions/{traceId}` for an evicted id is UNKNOWN (handler outside audited files).

### 4.3 API-vs-autonomy conflict policy (as found — proposal in §4.4)

Current behavior (`BotActionCommandQueue.cs:437-451`): API commands ALWAYS win over
world-internal legs (preempt+interrupt, audited) but NEVER win over other API commands
(reject-busy). Autonomy has no veto, no deferral, no resume contract: a preempted roam
leg's `PendingLeg` still points at the interrupted request (caller sets `PendingLeg =
null` only on paths that remembered to), and GOAP's runner is not notified (its
`CurrentActorRequest` reference goes stale while `ActionRetryCount` persists). Net:
external control is safe for one-shots, hazardous mid-plan (GOAP sequence broken without
replan trigger — the runner sees the next `EvaluateStatus` against a world where step N
never ran).

### 4.4 Concurrency policy proposal (minimal, no redesign)

1. Preempt path must `Stop()`-then-dispatch through ONE helper that also clears the
   roam `PendingLeg` and nudges the GOAP runner (`ActivePlan = null` replan trigger) —
   today each of ~18 roam sites hand-rolls `Stop(); PendingLeg = null`.
2. Give `BotActionCommandQueue` drain the same `PendingLeg`-clearing hook (or return the
   interrupted trace so the executor can clear on next wake) — close the orphan-leg window.
3. Reserve `BotDecisionCycle` for terminal-synchronous actions only (assert
   `request.IsTerminal` at dispatch in debug); route Tick-driven legs exclusively via
   the roam route layer that actually ticks them.
4. Never reuse `BotActionKind.Interrupt → Stop` audit mapping (`:915`) — emit the control
   op under its own label so trace consumers can distinguish gameplay stops from
   preemptions.

### 4.5 Idempotency verdicts per verb

Two independent guards: (K) explicit-key outcome gate in `TryBegin` — locks after
Completed/Interrupted/TimedOut, retryable after Rejected (`GameplayActor.cs:4583-4592`;
`ActorEffectLedger` `ActorIdempotency.cs:52-66,96-103`); (E) applied-effect fingerprint
(`RecordEffect` after landing + `IsEffectApplied` preflight where implemented).
`MaxRecords = 256` distinct-key FIFO eviction (`:71,131-145`) bounds both — key reuse
after eviction RE-EXECUTES (documented retention window, caller-visible risk).

| Verb | (K) key gate | (E) effect fingerprint | Engine backstop (fresh-key retry) | Verdict |
|---|---|---|---|---|
| AcceptQuest | ✅ | ✅ `questcredit:{id}:accept` rec `:820`; probe `:797-800` | `AddQuest` duplicate-refuse | SAFE (double-accept impossible via all three) |
| TurnInQuest/Doodad/Auto | ✅ | ✅ `questcredit:{id}:reward` `:916` (probe: none found — reward double-claim relies on K + engine step advance) | Step machine advance; turn-in after completion finds quest gone → `StateTransition` not-active (`:867-870`) | SAFE with same key; fresh-key retry after ambiguous timeout can re-fire `DoReportEvents` — PARTIAL (no reward probe) |
| AdvanceQuest | ✅ | none | Missing quest → `StateTransition` (`:834-837`) | SAFE (step advance is monotonic; repeat is no-op-ish but re-executes engine evaluation — no ledger claim) |
| Talk / InteractNpc | ✅ | none | Zero-credit → `RejectedAction` (`:1030-1033`); credit is event-counter increment — re-talk CAN double-credit if objectives reset (by engine design) | NO double-execution safety beyond K — fresh-key repeat is a new talk (intended) |
| Interact / InteractWith | ✅ | none | Despawn guard + no-state-change reject (`:1549-1552`); phase machine consumes | PARTIAL — same-key safe; fresh-key retry post-`TimedOut` can apply twice (no fingerprint). Harvest sibling HAS one (below) — inconsistent |
| Cast / CastAt / AutoAttack | ✅ | none (HP-before sampling is observation, not a guard `:560-565`) | Engine cooldown/mana refusal; damage is not transactional | NO safety beyond K — retries are new casts by design, but ambiguous-timeout retry is indistinguishable from intentional double-cast |
| Move / Navigate / Drive | ✅ | none (arrival is not an "effect") | Already-there no-op `Completed` (`:345-351`); budget/stuck expiry | SAFE-trivially (motion is idempotent-ish); stale-destination risk (§3.4), not duplication risk |
| UseItem | ✅ | ✅ `itemuse:{tpl}:{instanceId}` `:1705` (probe: none — comment `:1704` cites charge-count backstop instead) | Item charge consumption | SAFE with same key; fresh-key retry consumes another charge (intended) — ledger is correlation-only here |
| Equip | ✅ | ✅ `equip:{tpl}:{instanceId}` `:1757` + probe `:1731-1734` | Slot occupancy | SAFE |
| Deposit/WithdrawMoney | ✅ | ✅ `currency:0:deposit:{amt}` `:3326` / `withdraw:{amt}` `:3360` + probes `:3303-3306,:3343-3346` | Balance comparison | SAFE (probes are amount-shaped, not instance-shaped — two identical-amount deposits share a fingerprint family; keyed by amount string — review key entropy if identical amounts recur) |
| Deposit/WithdrawItem | ✅ | ✅ `deposit/withdraw:{tpl}:{instanceId}` `:3410,:3448` + probes `:3387-3390,:3429-3432` | Container membership | SAFE |
| Buy | ✅ | ✅ `tradebuy:{tpl}:{merchant}:{count}` `:2762` (probe: none) | Money + bag delta | SAFE with same key; fresh-key repeat buys again (intended — amount-keyed fingerprint cannot distinguish) |
| Sell | ✅ | ✅ `tradesell:{tpl}:{itemId}` `:2816` (probe: none) | Item leaves bag | SAFE |
| SellSpecialty | ✅ | ✅ `tradesellspecialty:{packTpl}:{itemId}` `:2869-2870` (probe: none) | Pack consumed | SAFE |
| Plant | ✅ | ✅ `plant:{doodadTpl}:{seedTpl}` `:3162` (probe: none) | Seed consumed ("new-key retry finds no seed" `:3161`) | SAFE |
| Harvest | ✅ | ✅ `harvest:{doodadObjId}` `:3618` (probe: none) | Phase advance + empty-yield reject (`:3611-3615`) | SAFE |
| Loot | ✅ | NONE — no `RecordEffect` in `Loot` (`:1594-1623`) | Engine consumes entries (`TryReserveLootItem` `:1614-1616`); retry sees empty → `Completed(0)` or `RejectedAction` empty (`:1607-1609`) | SAFE via engine backstop, but NO ledger correlation (inconsistent with sibling Harvest); same-key retry after Completed is key-locked, fresh-key retry grants nothing — safe outcome, misleading `Completed(0 granted)` terminal |
| TradeOffer/Putup/LockOk | ✅ | ✅ `tradeoffer:{charId}` `:1876`; `tradeputup:{tpl}:{count}` `:1915`; `tradelockok:0:{finished\|awaiting}` `:1945,:1948` (probes: session-liveness preflights `:1854-1859,:1892-1893,:1928-1929`) | Session state machine; `AddItem` cancels session on invalid putup (`:1889-1891`) | SAFE with same key; cross-key trade state is inherently sessional — no verdict stronger than the session |
| PackPickup/PutDown/LoadPack | ✅ | ✅ all three (`:2217,:2319,:2397-2398`; async PutDown Tick-completion records `:4109`) | Slot/container transitions verified post-hoc (`:2209-2211`) | SAFE |
| Board/UnboardVehicle | ✅ | ✅ `board/unboard` variants (`:2507,:2560,:2597,:2628,:2653,:2672`) (probes: seat-occupancy preflights, not ledger probes) | Seat/bond state | SAFE |
| Mount/Dismount/DismissMate | ✅ | NONE found (no `RecordEffect` on the mate paths `:1960-2072`) | `already mounted` / `not mounted` `StateTransition` (`:1971-1972,:2021-2025`) | PARTIAL — state-gate safe (mount/unmount are toggles), but no ledger correlation; same-key lock is the only dedupe |
| Repair | ✅ | NONE found (`:2880-…` repair path records no effect) | Engine durability delta | PARTIAL — repair is naturally idempotent (second repair no-ops on full durability) but unledgered |
| Craft | ✅ | ✅ `craft:{id}` at Tick-completion (`:4091`) (probe: none; comment `:4086-4090` names consumed-materials as backstop) | Material consumption check (`:4067-4071`) | SAFE |
| PartyInvite/Accept | ✅ | ✅ (`:1801,:1834`) + pending/team preflights (`:1779-1785,:1816-1817`) | Invitation/team registries | SAFE |
| Expedition* | ✅ | ✅ (`:3862,:3901,:3939,:3966`) | Manager stores | SAFE (bridge-scripted only per matrix §A2 — verdict applies if ever queued) |
| Auction Post/Buy | ✅ | ✅ (`:2971,:3026`) | Lot registry | SAFE (same scoping caveat) |
| BuildHouse | ✅ | ✅ `housebuild:{design}:{itemTpl}` `:3276` (probe: none) | Design-item consumption (`:3275`) | SAFE |
| Target/SetTarget | ✅ | none | Assign-only; re-target is the operation (no duplication concept) | N/A |
| Stop | n/a (no key param; `NewRequest(Stop,0)` `:482`) | n/a | Interrupts live leg or no-ops | N/A — but every roam `_ = actor.Stop()` is an unaudited-by-caller cancel (see §3.5-1) |

Cross-cutting ledger risks (BLOCKER-class marked):

- **B-I1 (eviction re-opens keys).** `MaxRecords = 256` (`ActorIdempotency.cs:71`) with
  distinct-key FIFO (`:131-145`); re-record does NOT refresh recency (`:132-137`
  comment). A bot doing >256 keyed ops (quest chain + farm loop with per-wake
  `needs:{actor}:{cycle}:plant`-style keys — `NeedsDecisionScenario.cs:269,326,360,423`
  mints a fresh `CycleId` per wake in roam usage `BotRoamStepExecutor.cs:1194`) evicts
  live keys; a delayed retry with an evicted key RE-EXECUTES. Per-wake-unique keys also
  defeat K entirely (never reused ⇒ never dedupes) — the farm/quest legs' idempotency
  posture is E-or-engine-only by construction.
- **B-I2 (keyless = no dedupe).** Null/empty key always executes (`ActorIdempotency.cs:20-23`;
  `TryRecordOutcome` returns false `:96-103`). Queue `ExecuteKind` passes
  `spec.IdempotencyKey` through (`BotActionCommandQueue.cs:515`) — UNKNOWN whether the
  WebAPI/MCP layers default, require, or forward keys (controller outside audited files).
  Roam/GOAP/scenario dispatches use ad-hoc `quest:{actor}:{cycle}:…` keys or none —
  autonomy retries are NOT same-key retries (GOAP `CreateActorRequest` key policy UNKNOWN —
  not found in audited action files).
- **B-I3 (Interrupted/TimedOut lock forces fresh-key for genuine retries).** By design
  (`:52-58`), but combined with B-I1/B-P4 (stale POI → `TimedOut(Navigation)` locks the
  key), a bot that timed out walking to a stale merchant must mint a fresh key AND
  re-resolve the merchant — two recoveries for one failure, owned by nobody per §4.1
  (retry owner = GOAP only; roam legs have no retry owner — they replan).
- **B-I4 (asymmetric fingerprint coverage).** Loot/Mount/Repair/Cast/Interact have no
  `RecordEffect`; siblings (Harvest/Pack/Craft/Equip) do. A uniform "every mutating
  verb records" invariant does not exist — audit each verb separately (table above),
  do not assume ledger protection from key-gate presence.

## 5. Zoning / reconnect — consolidated

- ObjId invalidation: no versioning found anywhere in the audited surface (UNKNOWN
  whether the engine recycles objIds across worlds; `GetUnit` registry comment
  `GameplayActor.cs:4682-4684` implies per-`WorldInstance` registries, which bounds the
  blast radius to "null after zone-out" rather than "wrong unit" — but cross-instance
  id collision on teleport-while-`Running` is unverified).
- `ParentWorld == null` windows: every dispatch null-guards (`?.`) → `RejectedAction`
  (fail-closed). Nofound handler for world-swap-under-`Running` (§3.4).
- `Character.Region == null` → `GetAround` returns empty (`WorldManager.cs:1242-1243`):
  perception reads "empty world" during region transitions.
- Reconnect (session drop/re-provision): `HeadlessSession.Provision` / `deactivate` /
  `EnsureFreshBotRow` raw deletes (matrix §A3) are provisioning-time; no
  reconnect-resume contract for in-flight `ActorRequest`s was found — a reconnected bot
  gets a fresh actor (fresh ledger, fresh `_active`), so pre-reconnect keys do NOT lock
  post-reconnect retries (ledger is per-actor memory, `GameplayActor.cs:172`). UNKNOWN
  whether the queue rebinds `_history`/`_apiOwned` across reprovision.

## 6. File index (every citation, alphabetical)

- `AAEmu.Game/Core/Managers/Bots/ActorIdempotency.cs:20-23,25-46,52-66,68-146`
- `AAEmu.Game/Core/Managers/Bots/ActorObservation.cs:22-91`
- `AAEmu.Game/Core/Managers/Bots/ActorRequest.cs:9-13,15-206`
- `AAEmu.Game/Core/Managers/Bots/BotActionCommandQueue.cs:21-96,134-150,152-160,183-232,256-307,334-342,344-411,433-452,454-513,618-632,807-830,835-869,914-917,926-945,969-998`
- `AAEmu.Game/Core/Managers/Bots/BotDecisionProposal.cs:16-110,112-124,147-204,231-292,294-334`
- `AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs:161-175`
- `AAEmu.Game/Core/Managers/Bots/BotGoalArbiterStepExecutor.cs:16-43`
- `AAEmu.Game/Core/Managers/Bots/BotQuestLoopObservation.cs:26-107`
- `AAEmu.Game/Core/Managers/Bots/BotRoamStepExecutor.cs:57-60,66-123,192-216,218-324,359-364,386-390,497-600,612-679,681-738,741-796,839-845,873-921,951-976,987-1011,1015-1020,1071-1076,1110-1114,1159-1241,1317-1328,1342-1362,1364-1391,1429-1490,1531-1606,1642-1676,1680-1730,1931-1991,2022-2030,2066-2129,2168-2228,2296-2427`
- `AAEmu.Game/Core/Managers/Bots/BotSurveySenses.cs:12-14,138-185,187-260`
- `AAEmu.Game/Core/Managers/Bots/CombatDecisionTree.cs:19-23,51-59,70-73,104-112,135-153,160-182,184-228,230-261,278-289,292-466,468-473,486-616`
- `AAEmu.Game/Core/Managers/Bots/CombatExecutor.cs:28-32,159-184,227-230,313-317`
- `AAEmu.Game/Core/Managers/Bots/ConflictJoinActivityModule.cs:50-87`
- `AAEmu.Game/Core/Managers/Bots/EconomyDecisionScenario.cs:15-28,126-236`
- `AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:46-68,70-123,171-173,200-292,292-475,477-587,589-667,669-767,771-830,830-919,945-1033,1048-1107,1151-1391,1393-1476,1477-1552,1594-1707,1711-1759,1763-1956,1960-2072,2100-2426,2428-2675,2691-2820,2835-2872,2970-3028,3055-3165,3275-3278,3283-3451,3473-3621,3659-3800,3801-3969,3972-4462,4464-4542,4544-4688`
- `AAEmu.Game/Core/Managers/Bots/GameplayActorStepExecutor.cs` (cited via roam header only — content not re-audited; UNKNOWN currency)
- `AAEmu.Game/Core/Managers/Bots/Goap/BotContext.cs:12-27`
- `AAEmu.Game/Core/Managers/Bots/Goap/BotMemory.cs:18,25-179`
- `AAEmu.Game/Core/Managers/Bots/Goap/BotWorldStateProvider.cs:15-16,20-38,43-58,60-232`
- `AAEmu.Game/Core/Managers/Bots/Goap/GoapPlanRunner.cs:86-303,304-376`
- `AAEmu.Game/Core/Managers/Bots/Goap/Actions/` (`CombatActions`, `FarmingActions`, `HomesteadActions`, `RecoveryActions` precondition/effect declarations per §1 grep)
- `AAEmu.Game/Core/Managers/Bots/NeedsDecisionScenario.cs:243-284,324-433`
- `AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:38-69,91-104`
- `AAEmu.Game/Core/Managers/World/WorldManager.cs:1195-1300`
- `AAEmu.Game/Core/Managers/Bots/QuestDecisionScenario.cs:35-200,202-221`
- `AAEmu.Game/Core/Managers/Bots/LevelingLoopScenario.cs:136-142,997-1009,2871-2881,2991-2999`
- `AAEmu.Game/Core/Managers/Bots/M1M2ReplayScenario.cs:636-651`
