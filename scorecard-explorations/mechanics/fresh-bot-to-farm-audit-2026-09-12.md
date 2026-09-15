# Fresh level-1 PlayerBot to farm — tiered audit (2026-09-12)

Owner-directed audit, no milestone rewrite. Question: can a fresh level-1 PlayerBot
progress through existing game mechanics to place and sustain a farm? Accelerated
rates allowed; no granted items/levels.

Provenance: `compact.sqlite3` md5 `78b3bdbf038db3b927056106efdf91af` (1.2 r208022);
code read at working HEAD `c7351b242`. Confidence: **exact** for canonical rows
(direct SQLite reads) and engine paths (read this session); **heuristic** for bot
capability inferred from scenario code.

## Tier 0 — Public farm (no design, no tax)

- 17 `common_farms` rows (e.g. Lilyut Hills plots); group caps: crops 10,
  arboretum/livestock 5 (`farm_groups`).
- Potato seed 15659 costs 25 copper, sold in pack 171 (merchant 8522) —
  verified `merchant_goods` row. L1 quest copper (33+) funds the first seed.
- `GameplayActor.Plant` zeroes labor on public farms and carries no connection
  gate (`GameplayActor.cs:2735-2744`). `PublicFarmManager.CanPlace` enforces
  cap + allowed-doodad (`PublicFarmManager.cs:91-111`).
- Grow honors `World.GrowthRate` (`DoodadFuncGrowth.cs:21`); harvest runs the
  real `Doodad.Use` chain; `FarmerCycleScenario` sustains harvest → bank → replant.
- Verdict: no mechanic missing. Unproven: autonomous driver choosing
  earn-seed → travel → plant → sustain.

## Tier 1 — Private small scarecrow farm (owned, taxed)

- Design 15596 (straw scarecrow → template 267) prices at 14,000 but has NO
  `merchant_goods` row — merchant purchase is not a canonical path.
- Quest 4438 (L7, NPC 9789 merchant recruiter) grants 15596 via supply row 2835
  (comp 19404), bypassing gold. Bot completion of 4438 is unverified; no
  LevelingLoop seed, no test.
- `BuildHouse` runs the real `HousingManager.Build` with design-in-bag, tax
  pre-flight, and placement validation — but rejects headless characters with
  no game connection (`GameplayActor.cs:2844-2847`).
- Tax 50,000 copper/week (taxations id 8, small 8×8); deposit = 2× base via
  `CalculateBuildingTaxInfo` (`HousingManager.cs:1060-1064`, certs ceil/10000).
- Quest copper L1–10 totals ≈ 869 vs the ~64,000+ wall (design value + first
  tax + deposit). The bot must earn far beyond the starter band.
- Verdict: engine paths exist; autonomous earn → acquire → place chain unproven.

## Tier 2 — Large/taxed farms

- Tax id 9 (large 16×16) 100,000/week; id 10 (24×24) 150,000/week; heavy-tax
  multiplier for multi-property owners (`HousingManager.cs:1055-1058`).
- Unpaid tax → demolish path with 22h mail delay (`HoursForFailedTaxToReturnHouse`).
- Same mechanics as Tier 1, larger recurring economy. Nothing new missing —
  the decision layer and recurring income are the gaps.

## First autonomous failure

Goal chaining past seeds. Perceive (`DiscoverQuests`) and Choose
(`BotDecisionSelector`) are generic, not Solzreed-locked, but no controller
composes earn → acquire design → place → pay recurring tax → sustain. Every
farmer test starts after the earn step (seeded money, stocked seeds,
pre-planted crops). Split precisely: public-farm sustain needs only a driver;
private placement additionally needs quest-4438/design, placement choice,
headed connection, and upfront tax; tax-sustained ownership additionally needs
recurring 50,000/week income — the emergent-economy test, downstream of
autonomous trade (proposed N1/R1 substrate, not redesigned here).
