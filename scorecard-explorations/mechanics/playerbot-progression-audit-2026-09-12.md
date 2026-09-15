# PlayerBot progression audit — Level 1 to sustainable farm (2026-09-12)

Owner-directed audit, no milestone rewrite. Core principle: **a PlayerBot is a
player** — not a scripted role, not a fake end-state. The Living Village must be
the emergent result of many independently progressing bots, not something
constructed directly.

Provenance: `compact.sqlite3` md5 `78b3bdbf038db3b927056106efdf91af` (1.2
r208022); code read at working HEAD `c7351b242`. Confidence: **exact** for
canonical rows (direct SQLite) and engine paths (read this session);
**heuristic** for bot capability inferred from scenario code. Companion tier map:
[fresh-bot-to-farm](fresh-bot-to-farm-audit-2026-09-12.md).

## 1. Current reality

What bots can actually do today: perceive quest offerings (`DiscoverQuests`,
`GameplayActor.cs:936`), choose the lowest-level legal offering in a level band
via a bounded deterministic selector (`BotDecisionSelector`, quest-accept only),
and execute single legs through real engine paths — Accept/Advance/TurnIn,
Move/NavigateTo, SetTarget/Cast, Loot, Buy/Sell, Plant/Harvest, Craft,
Deposit/Withdraw, PackPickup/PutDown/Load/DriveVehicle, BuildHouse, Mount. The
`LevelingLoopScenario` closes bounded quest legs (254→255 plus Dewstone and
later zone chains with AdaptiveBand, inter-zone travel, Nui death recovery).
`FarmerCycleScenario` sustains harvest → bank → replant on an owned plot.
`EconomyDayCycleScenario` proves ledger conservation across kill-9. Every one of
these starts from seeded state (money, items, crops, level) — no controller
earns its own prerequisites. Needs (`BotNeedsEvaluator`: gold/labor/food/lumber
shortfalls) and work-choice (`NeedsDecisionScenario`: harvest-vs-buy-vs-rest)
exist as inert, default-OFF library code no wake drives.

## 2. Level-1 → farm progression graph

Spawn L1 (ordinary `CharacterManager.Create` record, class gear + consumables,
no copper) → discover offerings in band → accept lowest-level legal quest →
pursue objectives (kill/gather/interact via actor legs) → turn in (copper via
`Quest.cs:395-401` + level supplies) → repeat across Solzreed → Dewstone (L10+)
→ complete quest 4438 (L7, NPC 9789) for scarecrow design 15596 → buy seeds
(15659, 25 copper, merchant 8522) → plant (public farm: no design/tax/labor;
private: owned plot) → grow (GrowthRate-accelerated) → harvest → deposit →
replant → earn 50,000 copper/week tax (trade packs pay 124,540 after 22h mail
delay) → sustain. Copper wall: ~869 starter-band supply copper vs ~64,000+
(design value + first tax + deposit) — the bot must progress far beyond L10
questing into vending and trade.

## 3. Capability matrix

| Step | Human | Bot | Implementation | Gap | Automatable | Acceleration |
|---|---|---|---|---|---|---|
| Spawn/quest/level L1–9 | yes | scoped (bounded bands) | LevelingLoop + actor legs | full-route decision (PB-002) | yes | ExpRate |
| Earn quest copper | yes | yes (same path) | Quest.cs:395-401 | none | yes | — |
| Buy seeds/supplies | yes | yes | actor.Buy:2371 | none | yes | — |
| Plant/harvest/replant | yes | yes (leg) | actor.Plant/Harvest, Doodad.Use | autonomous driver | yes | GrowthRate |
| Public-farm sustain | yes | leg-only | PublicFarmManager + FarmerCycle | driver | yes | GrowthRate |
| Quest 4438 → design | yes | unverified | data + Accept path | bot completion proof | yes | — |
| Place farm (BuildHouse) | yes | leg-only | HousingManager.Build | headed connection; placement choice | yes | — |
| Pay weekly tax | yes | unproven | tax engine enforced | recurring income | yes | tax timers |
| Fund tax via trade | yes | rig-only | M4/EconomyDayCycle (seeded) | autonomous trade chaining | yes | econ cycles |
| Goal chaining (earn→own→sustain) | n/a | missing | N1 proposed only | needs→goal→plan driver | yes | — |

## 4. First autonomous failure

Goal chaining past seeds. Perceive and Choose are generic, not Solzreed-locked,
but no controller composes earn → acquire → place → pay tax → sustain. Public
sustain needs only a driver; private placement adds design/position/connection/
upfront tax; tax ownership adds recurring income downstream of autonomous trade.

## 5. Full gap inventory

(1) Needs→goal→plan driver (no code drives N1 slices); (2) quest-4438 bot
completion proof; (3) placement-position choice; (4) headed-connection story for
BuildHouse (rejects headless); (5) farm-design trade path (no merchant row —
quest or player trade only); (6) recurring-income autonomy (trade chaining);
(7) full-route leveling decision beyond bands (PB-002); (8) mixed party consent
path (M7 worksheet prereq). No emulator mechanic is missing for Tier 0; Tier 1+
needs economy length, not new systems.

## 6. Existing systems to reuse

Actor contract (all legs), DiscoverQuests + BotDecisionSelector (choice
primitive), LevelingLoop (band progression), FarmerCycle (sustain),
EconomyDayCycle (ledger proof), BotGoalArbiter + activity modules (wake
arbitration), BotNeedsEvaluator + NeedsDecisionScenario (N1 slices),
NavigateTo/Unit (movement), BotBagManager (vendoring), PublicFarmManager
(zero-cost planting), GrowthRate/tax-timer knobs (acceleration).

## 7. Minimal implementation sequence

1. Drive N1 slices on a live wake: snapshot → needs → work choice → existing
   actor leg (Tier 0 public farm first).
2. Add farm-ownership goal composer: 4438 → design → placement search →
   BuildHouse (headed session) → Tier 1.
3. Close recurring tax via autonomous trade legs → Tier 2 sustain.
4. Each step reuses listed systems; no parallel implementations, no new engine.

## 8. Automated verification strategy

Fresh ordinary character, no grants; checkpoint origins recorded; perception →
legal choice → action → observation trace per decision; conservation checks
(seeds, copper, labor, tax); FAST_SIM labeled with every knob; first-divergence
reporting closes only demonstrated legs. Seeded rosters prove subsystems only.

## 9. Milestone impact

M1–M2: band traversal proven, full-route decision open. M3: paths exist,
acquisition/placement autonomy missing. M4: loop exists, autonomous funding
unproven. M5: contract complete, autonomy consumer-scoped. M6: framework
complete, autonomous life unproven. M7: mechanics exist, consent path open.
M8: slices are callable capabilities; emergent operation needs progressing
residents — the reframe (bots, not roles) is documentation, already recorded.
M9 substrate (R1/N1) is the missing decision layer, confirmed not redesigned.

## 10. Living Village implications

One autonomous lifecycle proves the pattern; 5 bots prove coexistence
(no contamination, shared public farms); 20 prove specialization pressure
(needs divergence → roles emerge); 50+ ride the closed A3/A5 population tiers.
The village is the output; player simulation is the system.
