# C10 Fishing Domain Dossier (FISH-01 all-U exploration, 2026-08-24)

Canonical reference: ArcheAge 1.2 (`r208022`), `AAEmu.Game/Data/compact.sqlite3` (queried strictly `mode=ro`).
Status at writing: develop @ 895516a39.

## 1. Server code today

### Implemented and wired

| File | What it actually implements |
|---|---|
| `AAEmu.Game/Models/Game/Skills/Effects/SpecialEffects/FishingLoot.cs` | The **core catch payout**. `SpecialType.FishingLoot (=79)`. Resolves caster's zone group via `ZoneManager`, picks `zoneGroup.FishingLandLootPackId` if `target.Transform.World.Position.Z > 101` else `FishingSeaLootPackId` (crude sea-level heuristic), then `LootGameData.GetPack(...).GiveLootPack(character, ActabilityType.Fishing, ItemTaskType.SkillEffectGainItem)`; sends `ErrorMessageType.BagFull` on failure. Reached from the fishing **plot**, not `skill_effects` (§3). |
| `Core/Managers/FishSchoolManager.cs` | Read-only index of already-spawned fish-school doodads (templates 6447 freshwater / 6448 saltwater) per world; loaded from `SpawnManager.Load:939`; feeds `RadarManager`. No spawn/respawn lifecycle of its own. |
| `GameData/FishDetailsGameData.cs` | `[GameData]` loader for `fish_details` (34 rows). `Create(...)` builds a `BigFish` with randomized length + lerped weight; 16-byte detail blob; used only by trophy conversion. |
| `Models/Game/Items/BigFish.cs` | Item detail type `ItemDetailType.BigFish`, correct Read/WriteDetails. |
| `Core/Managers/RadarManager.cs` | Fish-finder radar: `RegisterForFishSchool(player, range)`; 1s tick scans schools, batches 10/packet. Auto-driven by buffs with `FindSchoolOfFishRange > 0`. |
| `SCSchoolOfFishDoodadsPacket` (0x1b7) / `SCSchoolOfFishFinderToggledPacket` (0x1b6) | Both implemented against real offsets; actually sent. |
| `Scripts/Commands/FishFinderCmd.cs` | Working GM command `//fishfinder set true\|false`. |
| `DoodadFuncConvertFish.cs` + `ItemManager.GetLootConvertFish` | Trophy conversion: removes backpack bundle, weighted roll over mapped loot pack, creates graded BigFish. Known shortcut: `break; // TODO use only the first item`. |

### Stubbed / orphaned

| File | Status |
|---|---|
| `SpawnFishEffect.cs` | Fully written (sports-fish spawn + aggro), loaded from `spawn_fish_effects` (12 rows) — but canonical `effects` hub has ZERO `actual_type='SpawnFishEffect'` rows → unreachable dead code. |
| `DoodadFuncCatch.cs` | Pure stub (`doodad_func_catches`: 2 rows). |
| `DoodadFuncFishSchool.cs` | Returns false — "spawn triggered by fishing", nothing triggers it. |
| `DoodadFuncConvertFishItem/BuyFishItem/BuyFishModel` | Phase-func templates parsed; `Use()` trace-only stubs. |
| `DoodadFuncBuyFish.cs` | Implemented but double-credits money (`Money += total` AND `AddMoney(...)`) — audit before use. |
| `SkillTemplate.TargetFishing/.TargetOnlyWater` | Loaded, never read anywhere — no server-side water/rod gating. |

## 2. Canonical data

- Tables: `fish_details` (34), `spawn_fish_effects` (12, orphaned), `doodad_func_fish_schools` (21 → npc_spawner_id), convert/buy fish func tables, `zone_groups.fishing_{sea,land}_loot_pack_id` populated for essentially every zone group.
- Skills (`target_fishing=t`): **21571 낚시하기** — plot **809**, reagent 27142 Wriggling Worm ×1 (`skill_reagents` 2381), labor 5 via actability group 7, `need_learn=f`, max_range 30, `target_type=Pos`, `target_only_water=t`. Twins: 18711 (test, plot 659), 22106 (event, plot 903).
- Rods: item category 145 (`ItemCategory.Fishing_Rod`), 27 items, two-handed holdables.
- Catch items: land pack 7706 (freshwater), sea packs analogous; sports-fishing chain present-but-dormant (tuna/marlin NPCs → bundle trade packs → trophy conversion/sale).
- Fish finder buff: 5736/5811 (`find_school_of_fish_range=1000`) wired to radar.
- World data: 95 fish-school spawns in main_world (freshwater Z≈129–266, saltwater Z≈100).

## 3. The gameplay loop — encoded in plot 809

Chain from `plot_next_events`: start (anim/cancel-buffs) → casting 1500ms → **channeling 6500ms** (bobber wait) → poll self-loop every 500ms → **`PlotConditionType.Chance` 25%** → SetVariable(82,1) + StopChanneling/FinishChanneling → variable branch → success event applies **SpecialEffect 10860 = FishingLoot**, ConsumeLaborPower(5), GiveLivingPoint, cleanup.

Engine support verified: `Skill.Use → Template.Plot.RunAsync`; Pos target handled; plot runtime has Chance/Variable/SetVariable/StopChanneling/FinishChanneling classes; labor charged in `Skill.EndSkill → ChangeLabor(-5, 7)` (grants proficiency); worm consumption rides `skill_reagents`. Bobber is purely client-visual.

| Loop step | Verdict |
|---|---|
| Equip rod | PARTIAL — client gates; server never validates rod |
| Bait consumption | EXISTS |
| Cast at water (Pos target) | EXISTS (engine); water flag not enforced server-side |
| Wait-for-bite + 25% bite | PARTIAL→EXISTS (plot data complete; zero coverage proving plot 809 executes; Variable condition code comments flag "not implemented correctly") |
| Loot roll (land vs sea) | EXISTS (`Z > 101` heuristic is crude but functional) |
| Labor + proficiency | EXISTS |
| Fish finder radar | EXISTS |
| Schools spawning sports fish | MISSING (data complete; trigger orphaned; funcs stubbed) |
| Trophy conversion / sale | PARTIAL (first-item-only TODO; BuyFish double-credit) |
| School depletion/respawn | MISSING |

## 4. Packets

No dedicated C2G fishing packets — loop rides CSStartSkill + SCPlot* (all registered). G2C fish-finder packets implemented. Nothing stubbed at the wire level.

## 5. Sizing & bot feasibility

- **Basic fishing playable**: S–M (verify plot 809 E2E, fix surfaced plot-runtime edges, optionally enforce rod/water server-side, replace Z>101 heuristic).
- **Full pillar incl. dormant sports fishing**: M–L.
- **Bots cannot exercise it today**: `Cast(skillId, targetObjId)` requires a UNIT target; fishing needs a POSITION cast. Needs one new contract action — `CastAt(position)` (or purpose-built `Fish`) driving the same `Skill.Use` seam with a Pos cast-target — after which Equip + bagged worms compose the whole loop through existing idempotency/labor scaffolding.
