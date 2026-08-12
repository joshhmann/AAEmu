# FARM-01 Livestock Interactions — FIX-1 (t_afbf7cb7)

**Status:** IMPLEMENTED, Rei-gated (gate card filed on completion)
**Branch:** fix/m3-fix1-livestock (fork-only, no upstream PR)
**Ground truth:** canonical 1.2 chains in `AAEmu.Game/Data/compact.sqlite3` (verified 2026-08-11; see
`mechanics/m3-canonical-audit.md` §3 on branch m3-canonical-audit)

## What was broken

`DoodadFuncFeed`, `DoodadFuncDairyCollect`, `DoodadFuncShear`, `DoodadFuncButcher` loaded their data
(`DoodadManager` lines ~334/970/1183/2224) but their `Use()` bodies were log-only stubs — a player could
plant a calf, watch it grow, and recover it across restarts, but **could not feed, milk, shear, or butcher**
any livestock.

## Implemented behavior (per the 1.2 chains)

| Func | Data | Behavior |
|---|---|---|
| `DoodadFuncFeed` | `doodad_func_feeds` item_id/count (e.g. 797 ×1, feed 14310) | Consumes the feed item from the caster's inventory. Short on feed → client error `not_enough_item` (ErrorMessageType.NotEnoughItem), nothing consumed. Canonical feed rows wire `next_phase = -1` (feeding does not move the phase); a chain that wires a next phase is honored. |
| `DoodadFuncDairyCollect` | (id only) | Advances to the milked phase; the milk yield comes from the loot funcs on that phase — mirrors `DoodadFuncCropHarvest`/`DoodadFuncFruitPick`. Canonical: happy cow 5786 → milked cow 8436 → LootPack 81 (pack 6392) → milk 8055 ×7-9. |
| `DoodadFuncShear` | shear_type_id, shear_term (60,000 ms) | Advances to the sheared phase and publishes the shear term as the regrow deadline (`GrowthTime`); a regrow timer on the sheared phase (canonical delay = term) is the authoritative revert. Sheep: 5649 → 384 sheared → timer 60 s → 5649 woolly again. |
| `DoodadFuncButcher` | corpse_model (client-side display) | Advances to the butchered/corpse phase; the meat yield comes from the loot funcs there. Canonical: cow 5782 → 5790 butchered → LootPack 79 (pack 6390) → beef 8048 ×14-16 (+ leather 8007, +1 milk); sheep → 640 → mutton 8052. |

Interaction skills (client-driven, already functional via `DoodadFuncUse` + loot funcs, now pinned):
20595 사료 먹이기 (feed), 13800 가축 젖짜기 (milk), 13972/13970 도축하기 (butcher), 13802 가축 털뽑기 (shear).

## Tests

`AAEmu.UnitTests/.../DoodadObj/LivestockInteractionTests.cs` — 9/9 on the real canonical chains
(additive rig on CropHarvestLoopRig; restart-recovery pins in PhaseStateRestartRecoveryTests untouched):

1. Feed consumes the item and stays in phase (×3 feeds)
2. Feed without the item refuses and consumes nothing
3. Feed interaction on the calf (skill 20595) advances 5780 → 5781 and schedules growth 792
4. DairyCollect advances to the milked phase
5. Butcher advances to the butcher loot phase and yields mutton
6. Shear advances to the sheared phase and publishes the 60 s term
7. Sheep shear-term loop: regrow timer reverts the sheep to the woolly phase
8. E2E dairy: place calf → grow (both stages) → cow → feed → happy cow → milk → milk 8055 in bag
9. E2E butcher: cow → butcher skill → beef 8048 + leather + milk in bag

Rig pitfalls pinned: per-test unique actor names (ItemManager's global container registry keys bags by
character id — a shared name shares the bag); `[ParallelLimiter]` required in addition to `[NotInParallel]`.

## Evidence

- Filtered gate `./scripts/gate.sh LivestockInteractionTests` → 9/9 green
- Full gate on this branch → see gate card (t_filed on completion)
- Growth + restart recovery regression surface: `PhaseStateRestartRecoveryTests` 8/8 (in full gate)

## Files

- `AAEmu.Game/Models/Game/DoodadObj/Funcs/DoodadFuncFeed.cs` (implemented)
- `AAEmu.Game/Models/Game/DoodadObj/Funcs/DoodadFuncDairyCollect.cs` (implemented)
- `AAEmu.Game/Models/Game/DoodadObj/Funcs/DoodadFuncShear.cs` (implemented)
- `AAEmu.Game/Models/Game/DoodadObj/Funcs/DoodadFuncButcher.cs` (implemented)
- `AAEmu.UnitTests/Game/Models/Game/DoodadObj/LivestockInteractionTests.cs` (new, 9 tests + additive rig)
- `SCORECARD.md` FARM-01 row (evidence extended)
