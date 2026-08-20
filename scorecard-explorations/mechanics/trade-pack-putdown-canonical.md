# Trade-Pack Put-Down Mechanic — Canonical 1.2 Placement Rules + PUTDOWN Refusal Verdict (PACK-01 dossier)

**Task:** t_dc809039 (mechanic-research lane, dossier; gates t_eaee04ee continuation)
**Date:** 2026-08-14
**Scope:** evidence only, no code changes. Ground truth: joshhmann fork `develop` @ 62691fb29 (M3a/M4 replay merged); canonical 1.2 data surface = `AAEmu.Game/Data/compact.sqlite3` (read-only).
**Question:** is the M3aM4ReplayScenario PUTDOWN refusal (pack 26488, run 2338, game.log 08:48 08-14) canonical engine behavior (test placed at invalid spot) or an engine defect (wrongly refuses)?

> **VERDICT: CANONICAL-REFUSAL** — the engine refused nothing. Skill 20412 (pack 26488's put-down skill) carries `casting_time=1500`; `Skill.Use` schedules the effect via an async `CastTask` and returns Success immediately. The actor's synchronous post-state check raced the canonical cast and misread the not-yet-applied effect as a refusal. The test expectation (immediate synchronous placement) was wrong; the fix is test-side (wait out the cast / poll the Backpack slot — already drafted in the t_eaee04ee workspace and empirically passing on the live path). No engine fix card follows.

---

## 1. TL;DR

- **Ground put-down is a skill, not a zone mechanic.** Every auto-equip trade pack carries a put-down use skill whose single effect is `PutDownBackpackEffect` (data-verified: 341 effects rows of `actual_type='PutDownBackpackEffect'`, one per pack; pack 26488 → skill 20412 → effect 27200 → doodad 6068).
- **Placement rules (canonical 1.2, engine + data):** the pack must be in the Backpack equipment slot; the placement point (1 m in front of the player, always facing north) must NOT be inside a public-farm subzone (966/998 farm, 968 nursery, 967 ranch, 974 stable — hard-coded in `PublicFarmManager.Load()`); if a house occupies the point, the player needs interaction permission; otherwise **any open-world ground position is valid** (no zone whitelist, no terrain check, no range-from-farm rule).
- **The 08:48 refusal was a cast race, not a placement refusal.** Skill 20412 has `casting_time=1500`; `Skill.Use` returns `Success` the moment it schedules the `CastTask` (1.5 s later). The actor's step-4 post-state check ran synchronously, saw the pack still in the Backpack slot, and logged "did not take effect (engine refused placement)". The effect landed (canonically) after the cast — the run-2349 patch that polls the slot fixed PUTDOWN on the live path, which also proves the spot was valid (a real farm/house refusal never resolves by waiting).
- **Cross-check: unboard-from-vehicle is NOT a put-down.** Pack→vehicle is `PackVehicleService` (attaches the pack doodad to a cargo point); `UnboardVehicle` only dismounts the character (pack stays on the wagon). The ONLY ground placement path is the pack's use skill. The scenario's own flow (PutDown → PackPickup → Load → Drive → Unboard) is the canonical shape.

---

## 2. Finding recap (from t_eaee04ee)

- Run 2338 (08:48, live E2E hook): `M3aM4ReplayScenario - m3a-m4 replay FAIL at PUTDOWN: pack 26488 (RejectedAction) ... trade pack 26488 put-down did not take effect (engine refused placement)` (game.log; quoted in Mai ops triage on t_eaee04ee — the original log lines have since been overwritten by run 2349's log).
- Worker was mid-investigation of `PutDownBackpackEffect` + farm subzones when budget exhausted (run crashed 09:03).
- Run 2349 (10:40:24): after the worker's in-flight patch (poll the Backpack slot while the request stays Running), PUTDOWN **passed** — the terminal event moved to a LATER stage (`USE-SUMMON-SCROLL: item 18660 not found in inventory`). Since scenario stages are strictly sequential and any FAIL returns immediately, the log proves PUTDOWN + PACKPICKUP completed on the live path with the patch.

## 3. Root-cause chain (all data-verified unless labeled)

1. `items.id=26488` → `use_skill_id=20412`, `impl_id=22` (trade pack impl), `specialty_zone_id=22` — 황금 평원 마취제 (Golden Plains anesthetic pack). [compact.sqlite3, 2026-08-14]
2. `skills.id=20412` → name **특산품 내려놓기: 황금 평원 마취제** ("trade pack put-down: Golden Plains anesthetic"), **`casting_time=1500`**, `plot_id=NULL`, `plot_only='f'`, `consume_lp=0`. [compact.sqlite3]
3. `skill_effects` row 21460 (skill 20412) → `effect_id=27200`, chance 100, friendly+non-friendly. [compact.sqlite3]
4. `effects.id=27200` → `actual_type='PutDownBackpackEffect'` (polymorphic effect table; 341 total PutDownBackpackEffect entries, one per pack item — the per-pack put-down template). [compact.sqlite3]
5. `effects.actual_id=109` → `put_down_backpack_effects.id=109` → `backpack_doodad_id=6068` — the placed-pack doodad template; matches the scenario constant `PackPlacedDoodadTemplateId=6068`. [compact.sqlite3, cross-checked with M3aM4ReplayScenario.cs:70]
6. `Skill.Use` (`Skill.cs:311-349`): `casting_time>0` → broadcast `SCSkillStartedPacket`, schedule `CastTask` at castTime, **return `SkillResult.Success` immediately**. The plot branch (async `Task.Run`, `PlotOnly` → early Success) is skipped here — plot_id is NULL. [engine code, fork develop]
7. `CastTask.Execute` → `Skill.Cast` → `ApplyEffects` at T+1500 ms. [engine code]
8. `PutDownBackpackEffect.Apply` then: public-farm gate → house gate → move pack to System container → spawn doodad 6068. [engine code, see §4]
9. `GameplayActor.PutDown` step 4 (`GameplayActor.cs:756-758`) ran the slot check synchronously right after `Use` → pack still in slot → `RejectedAction` "did not take effect (engine refused placement)". The message wording in the card/log is EXACTLY this actor-side line, confirming the cast-race path (the "refused by engine: {result}" line, which fires on a non-Success result, never triggered). [actor code; log quote]

> **Record correction (continuity note):** the t_eaee04ee in-flight patch comments attribute the async delivery to "plot 5 — 사방치기 / plot-only skill". That is not supported by the canonical data: skill 20412 has `plot_id=NULL`, `plot_only='f'`. The actual mechanism is the 1.5 s **cast** (`casting_time=1500` → CastTask). The fix shape is unaffected (wait/poll for the async effect); only the mechanism name is wrong.

## 4. Canonical 1.2 put-down placement rules (engine = data-verified; retail = research-derived)

Where/when may a pack be put down:

- **Carried state:** must be in the Backpack equipment slot, looked up by instance id (`PutDownBackpackEffect.cs:32`; the SkillItem caster carries `ItemId`). A pack in the bag is not placeable (the SkillItem branch's `GetItemByItemId` finds nothing → silent no-op early return).
- **Public-farm exclusion:** `PublicFarmManager.InPublicFarm` (`PublicFarmManager.cs:64-68`) → position inside subzone 966/998 (공용 농장 = public farm), 968 (공용 수목원 = nursery), 967 (공용 목장 = ranch), 974 (탈것 축사 = stable) → error `CommonFarmNotAllowedType`, effect early-returns without moving the pack (`PutDownBackpackEffect.cs:35-39`). The five ids are **hard-coded in `PublicFarmManager.Load()`**; the `sub_zones` rows (all-zero geometry templates) name them. Real polygons come from client `level_design/.../subzone_area.xml` via `SubZoneManager` — **the E2E runtime ships no level-design XML, so `InPublicFarm` is always false on the live E2E stack** (verified: no `subzone_area.xml` under /root/aaemu-dev or /root/aaemu-e2e). The 08:48 refusal therefore cannot have been the farm gate — and the poll-fix passing PUTDOWN at the same spot is independent proof the spot was not farm-restricted.
- **House gate:** a house at the placement point (1 m in front, `GetHouseAtLocation`) requires interaction permission (`AllowedToInteract`), else error `Backpack` (`PutDownBackpackEffect.cs:51-60`). Retail-consistent: "You can drop it within your house or any protected land... You can't ... put it in protected land you don't have access to" (Ten Ton Hammer trade-pack guide, David Piner, 2014-07-30 — research-derived, 1.0-era; consistent with the 1.2 engine gate).
- **Everything else is valid open-world ground placement.** No zone whitelist, no terrain/obstacle check, no farm-distance rule in the effect. Placement point is 1 m in front, rotation reset to face north (`PutDownBackpackEffect.cs:46-49`).
- **Anti-dupe invariant:** success moves the pack into the System container THEN spawns the doodad; the actor's post-state check uses the same move as retry-proof state (a retry finds no pack in the slot). Retail: dropped packs are free-for-all pickups (Ten Ton Hammer, same source — research-derived; matches `DoodadFuncLootPack`/RecoverItem pickup path).

Vehicle interplay (cross-check requested by card, t_eaf1754d lineage @ 6edbf0cbb):

- **Unboard-from-vehicle is NOT a put-down.** `BoardVehicle`/`UnboardVehicle` (SlaveManager bind) only seat/dismount the character. Loading a pack onto a wagon is `PackVehicleService` (`PackVehicleService.cs`), which resolves the pack's put-down doodad id (via the SAME `PutDownBackpackEffect.BackpackDoodadId` — `ResolveBackpackDoodadId`, `PackVehicleService.cs:275-288`) and attaches it to a cargo point; the pack never enters a "put-down" state on the vehicle path.
- The two pack-placing surfaces are: ground = use-skill → `PutDownBackpackEffect`; vehicle = `LoadPackOntoVehicle` → `PackVehicleService`. Neither is a form of the other. The scenario's PUTDOWN stage (ground) and LOAD-PACK stage (vehicle, carried path on live) are both canonical flows.
- Retail: you load a carried pack directly onto a farm cart/wagon without placing it on the ground first (IGN wiki Trade Routes, 2014-era — research-derived; consistent with the engine's carried-load path at `GameplayActor.cs:810-822`/PackVehicleService).

## 5. Verdict and test-contract correction

- **Verdict: CANONICAL-REFUSAL.** The engine's behavior — async cast, Success returned before the effect lands — is the canonical, data-driven contract for cast-time skills (retail shows the 1.5 s put-down cast animation before the pack appears on the ground). The test's assumption of synchronous effect application was wrong. This is NOT a case of "worker placed at invalid spot" either: the spot was valid (proven by the poll-fix passing); the refusal was never positional.
- **Required test-side change (already drafted in the t_eaee04ee workspace, uncommitted):** `GameplayActor.PutDown` must hold the request Running after a successful `Use` when the pack is still in the Backpack slot, and complete on Tick when the cast effect moves it out (the run-2349 patch; plus a direct-Apply bridge for unit-world fixture packs that carry no cast). When the cast completes with the pack unmoved (farm/house/invalid-item early return — the engine sends the error message), the request should Reject with the engine's reason. Engine code untouched; target-lock respected.
- **Do NOT file an engine-defect card.** No engine change is warranted.
- **Link back:** t_eaee04ee stays blocked on this dossier per director routing; resume guidance in §3 of this dossier + the workspace's in-flight patch. Remaining t_eaee04ee scope: scenario core 1-2-3 only, no broad E2E re-runs.

## 6. Sources

- Data-verified (queried 2026-08-14, `compact.sqlite3` r208022 read-only): items 26488; skills 20412; skill_effects 21460; effects 27200 + 341 PutDownBackpackEffect rows; put_down_backpack_effects 109 → 6068; sub_zones 966/967/968/974/998.
- Engine code (fork develop @ 62691fb29): `Skill.cs` (Use/Cast/CastTask scheduling), `CastTask.cs`, `PutDownBackpackEffect.cs`, `PublicFarmManager.cs`, `SubZoneManager.cs`, `SkillManager.cs` (effect load + plot_id/plot_only mapping), `PackVehicleService.cs`, `GameplayActor.cs` (PutDown), `M3aM4ReplayScenario.cs`.
- Log evidence: game.log 08:48 (run 2338, quoted in t_eaee04ee comment by Mai ops triage 2026-08-14) and game.log 10:40:24 (run 2349 terminal event, current file).
- Research-derived: Ten Ton Hammer trade-pack guide (2014-07-30); IGN wiki Trade Routes (2014-era) — 1.0-era retail descriptions, consistent with the 1.2 engine gates, used only as corroboration.
