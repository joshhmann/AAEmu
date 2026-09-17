# Fresh level-1 PlayerBot to farm — tiered audit (2026-09-12)

> **Current-use correction, 2026-09-16:** read the addendum below before planning.
> The original private-farm gold-wall conclusion omits quest certificate rewards;
> quest 4438 also has a prerequisite chain whose first quest requires level 10.
> Historical observations remain below; they are not current complete-loop proof.

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

## 2026-09-16 — Owned-small-plot route: canonical correction and gap map

### Scope and evidence

User outcome: one ordinary new bot earns and owns a small scarecrow plot, plants,
harvests into its real inventory, and begins another cycle. Start with Nuian/Solzreed
as the planning corridor; actual spawn/route/targets must be pinned from current
data and runtime, not copied coordinates. No multi-race or large-house claim.

Source HEAD `63f6567e7a198775ffe885a3345db70ff5a50777` plus pre-existing dirty
core fixes, templates/docs, synthetic homestead test/evaluator and reports.
Archaeology stdio prebuilt server (`dotnet run --no-build --no-restore --project
AAEmu.ArchaeologyMcp --no-launch-profile`), UTC 2026-09-16 13:56–14:00.
Catalog first: `list_sources {}`, `list_tables {}`; relevant schemas inspected with
`describe_table`. `source_id=compact.sqlite3`, path
`/root/aaemu-dev/AAEmu.Game/Data/compact.sqlite3`, version `1.2 r208022`, MD5
`78b3bdbf038db3b927056106efdf91af` unchanged. SQL rows are **exact data**;
`find_quest_objectives` labels its polymorphic linkage **heuristic**. Source reading
is **textual/code** (`game-source`, catalog `fork develop`), not runtime evidence.
Graphify's 09-15 graph was supplementary/stale, 300-token bounded traversal; not
used to prove current wiring. [Archaeology acceptance boundary](archaeology-mcp-acceptance.md).
No gameplay tests, live server, client, deploy or human acceptance run in this review.

### Findings that change the plan

| Claim | Verified finding / remaining boundary |
|---|---|
| Level 7 → quest 4438 directly | `quest_contexts.LEVEL=7` is not the full eligibility. `unit_reqs` gives completion chain **4415 → 4479 → 4417 → 4424 → 4439 → 4438**. First start component 19287 requires kind 1 (Level), value1 10. Kind 31 is CompleteQuestContext, enforced by UnitReqs/CharacterQuests. Plan for ordinary progression to level 10, not granted levels or direct 4438 injection. |
| Quest 4438 reward | 1×15596 design, 1×8337 lumber, 25×31892 bound certificates, supply rows 2835/3826/4263. Acceptor/report details both name NPC template 9789. Actual spawns/reach and reward execution remain unproved here. |
| Bot certificate constant | Current homestead code calls 8000001 a certificate. Canonical items row 8000001 is **APEX**; engine Item constants use 31891 / 31892. Fix identity and fixture isolation before trusting bot inventory predicates. Do not delete or convert persisted inventory without separate authorization. |
| Initial tax | Design 15596 maps to housing 267, tax group 8 = 50000 units. Intended ordinary first-property formula is base×2 deposit + base weekly = 150000, converted to 15 certificates when taxItem is active. Quest's 25 would leave 10 (two base weekly payments), conditional on runtime rules/account property count. **Source defect candidate:** CalculateBuildingTaxInfo returns false with zero outputs when account owns no houses; Build/actor callers ignore the bool. Reproduce and fix this before claiming the first plot actually charged 15. |
| Tax mode | FeatureSet default byte 7 = 0x8d includes bit 3 for taxItem=59; FeaturesManager's switch to gold is commented out. This is source configuration evidence, not a fresh deployed-feature observation. Record the actual runtime flag in acceptance. The earlier universal recurring-gold barrier is superseded. |
| Tax crafting | Craft 76 → skill 16767, skill consume_lp=200; product 5×31892; no craft_materials or skill_reagents rows for this craft/skill. Craft has required Doodad 2392 (building management sign). Availability, ownership/access, actual labor regeneration and successful repeat crafting need live proof; "any house" is not established by these rows alone. |
| Gold merchant fallback | 31891 price=10000 exists, but the bounded merchant_goods query returns **no certificate or design stock**, only potato seed 15659 in pack 171. Price is not availability. Adding vendor stock would be a separate game-rule change; not part of this journey. |
| Farm versus house construction | Housing 267 has one build-step row: skill 18553, one action; skill consume_lp=10. Prove this small plot's own completion through normal services. Do not substitute a general timber-pack/house-construction chain. Exact material/effect handling is still a required trace; empty skill_reagents alone does not prove no cost elsewhere. |
| Existing progression controller | QuestBootstrapActivityModule is a default-off copper/active-quest driver, not a proved L1→L10 farm prerequisite driver. GoalArbitrator currently has no GoalLevelUp. Wiring those names together is not completion. |
| Seed identity | 15659 is potato seed, price 25, not a tree sapling. Use explicit canonical item/target identities, not a shared misleading label. |

### Quest capability requirements (not live passes)

| Quest | Data-discovered objective / dependency | Proof still required |
|---|---|---|
| 4415 | Level 10 start; gather item 15694 ×5 | Locate real water source; ordinary gather generates quest credit. |
| 4479 | After 4415; interaction wi_id 19 with Doodad 5066 ×1 | Resolve eligible real target; interaction resources/timing and quest credit. |
| 4417 | After 4479; use potato seed 15659 ×1 | Real eligible planting/use produces quest credit; acquire seed normally. |
| 4424 | After 4417; gather item 24376 ×1, cleanup true; supplied item act exists | Resolve supply/cleanup semantics and legitimate report path; no item injection. |
| 4439 | After 4424; accept/report acts, no objective-family rows returned | Actual accept/report and next-quest eligibility. |
| 4438 | After 4439; accept/report NPC template 9789; three rewards | Actual inventory/reward transaction and nonrepeatable/idempotent completion. |

No live verdict is implied by an empty objective list or a mapped act. Resolve all
NPC/Doodad placements, aliases, normal prerequisite item sources and level-1→10 route
in the first route slice. Unknown is not proof the mechanic is absent.

### Reproducible queries

Each SQL below was sent through `query_sql` (default tool row cap 100; all results
untruncated). Recursive query caps depth 12 and output 50, returned depth 0–5 only.
`trace_quest {id:4438}` / `{id:4439}`, `trace_crafting {id:76}` and
`trace_doodad {id:2392}` returned exact, untruncated rows (limit 20).
`find_quest_objectives {quest_id:N}` for N=4415,4479,4417,4424,4439,4438 returned
1/1/1/1/0/0 rows, heuristic, untruncated, limit 50.

```sql
WITH RECURSIVE chain(quest_id,depth) AS (
 SELECT 4438,0 UNION ALL
 SELECT u.value1,chain.depth+1 FROM chain
 JOIN quest_components c ON c.quest_context_id=chain.quest_id
 JOIN unit_reqs u ON u.owner_type='QuestComponent' AND u.owner_id=c.id
 WHERE u.kind_id=31 AND chain.depth<12
) SELECT chain.depth,q.id,q.name,q.LEVEL FROM chain
JOIN quest_contexts q ON q.id=chain.quest_id ORDER BY chain.depth LIMIT 50;

SELECT c.quest_context_id,c.id AS component_id,u.kind_id,u.value1,u.value2
FROM quest_components c JOIN unit_reqs u
ON u.owner_type='QuestComponent' AND u.owner_id=c.id
WHERE c.quest_context_id IN (4415,4479,4417,4424,4439,4438)
ORDER BY c.quest_context_id,c.id;

SELECT a.id AS act_id,s.id,s.item_id,s.count,i.name FROM quest_acts a
JOIN quest_components c ON c.id=a.quest_component_id
JOIN quest_act_supply_items s ON s.id=a.act_detail_id JOIN items i ON i.id=s.item_id
WHERE c.quest_context_id=4438 AND a.act_detail_type='QuestActSupplyItem';

SELECT id,name,price,use_skill_id FROM items
WHERE id IN (15596,31891,31892,8337,15659,8000001);
SELECT * FROM merchant_goods WHERE item_id IN (31891,31892,15596,15659) LIMIT 20;
SELECT * FROM item_housings WHERE item_id=15596;
SELECT id,name,taxation_id,garden_radius,main_model_id,heavy_tax FROM housings WHERE id=267;
SELECT * FROM taxations WHERE id=8;
SELECT * FROM housing_build_steps WHERE housing_id=267 ORDER BY step;
SELECT * FROM crafts WHERE id=76;
SELECT * FROM craft_products WHERE craft_id=76;
SELECT * FROM craft_materials WHERE craft_id=76;
SELECT id,name,consume_lp,casting_time FROM skills WHERE id=16767;
SELECT id,name,consume_lp,casting_time FROM skills WHERE id=18553;
SELECT * FROM skill_reagents WHERE skill_id IN (18553,16767);
SELECT * FROM quest_act_con_accept_npcs WHERE id=3673;
SELECT * FROM quest_act_con_report_npcs WHERE id=3950;
```

Submit each statement separately; the read-only MCP intentionally rejects batches.
Source corroboration: `CharacterQuests.AddQuest`, `UnitReqsKindType` / `UnitReqs.Check`,
`GameplayActor.BuildHouse`, `HousingManager.GetByAccountId` / `CalculateBuildingTaxInfo`
/ `Build`, `FeatureSet.Check`, `FeaturesManager.Initialize`, `Item` tax constants,
`QuestBootstrapActivityModule`, `GoalArbitrator`, `CraftEffect` Building branch.

### Exit and next action

**LOOP INCOMPLETE.** Data prerequisites now mapped; ordinary leveling, chain execution,
target discovery/reach, first-property tax, plot completion/ownership, real farming,
repeat and restart remain to be verified for this journey. Use the
[roadmap slice sequence](../../ROADMAP.md#first-owned-small-plot-journey) and
[implementation contract](../../.kanban-templates/implementation.md).
An end-to-end acceptance run cannot inject levels, items, quest events, positions or
completed requests. A seeded live subloop is useful but must be reported separately.
