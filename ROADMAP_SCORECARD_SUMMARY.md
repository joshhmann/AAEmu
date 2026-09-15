# AAEmu Feature Completion & Roadmap Scorecard

**Updated**: September 7, 2026  
**Repository**: `/root/aaemu-dev` (`joshhmann/AAEmu` on branch `develop`)  
**Active Deployment**: `192.168.0.165` (`aaemu-game:presence-demo`)  

---

## 📌 Primary Documents in the Repository

Here are the authoritative documents in the codebase tracking the roadmap, evidence, and feature completion:

| Document | Location | Description |
|---|---|---|
| **Master Roadmap** | [`/root/aaemu-dev/ROADMAP.md`](file:///root/aaemu-dev/ROADMAP.md) | Full 3,500+ line master roadmap tracking milestones M0 through M8, technical contracts, requirements (REQ-\*), and DoD evidence. |
| **Progression Board** | [`/root/aaemu-dev/scorecard-explorations/progression-board.md`](file:///root/aaemu-dev/scorecard-explorations/progression-board.md) | Visual kanban and status board tracking each milestone, G1 quest gate, bot backtracks, and open decisions. |
| **Partial Domains Audit** | [`/root/aaemu-dev/scorecard-explorations/partial-domains.md`](file:///root/aaemu-dev/scorecard-explorations/partial-domains.md) | Deep-dive into partially-wired game domains: housing (100%), auction, specialty trade, items, mounts, models. |
| **Zero-Wired Domains** | [`/root/aaemu-dev/scorecard-explorations/zero-wired-domains.md`](file:///root/aaemu-dev/scorecard-explorations/zero-wired-domains.md) | Breakdown of uncompleted or stubbed systems: siege/dominion, ranks, premium, moulds, race tracks, client FX. |
| **Quest Runnability** | [`/root/aaemu-dev/scorecard-explorations/runnability.md`](file:///root/aaemu-dev/scorecard-explorations/runnability.md) | Census report proving 100% (4,573/4,573) quest runnability across all level bands. |
| **PlayerBot Blockers** | [`/root/aaemu-dev/scorecard-explorations/playerbot-blockers.md`](file:///root/aaemu-dev/scorecard-explorations/playerbot-blockers.md) | Architectural and lifecycle requirements for living-world bots. |

---

## 🧭 Milestone Progression (M1 – M8)

| Milestone | Focus Area | Status | Completion | Verified Evidence |
|---|---|:---:|:---:|---|
| **M1** | Solzreed Golden Route & Core Engine Defects | ✅ **CLOSED** | **100%** | All introductory quest chains working; core engine defects (BUG-007 through BUG-013) resolved. |
| **M2 / G1** | Full Quest Census Coverage | ✅ **CLOSED** | **100%** | **4,573 / 4,573 live quests runnable (100.0%)** across level bands 1–55. 0 unexplained failures. |
| **M2b / E2E** | Live Server Bot Harness & Repeatability | ✅ **CLOSED** | **100%** | Deterministic boot/reset harness; 10-bot and 25-bot stability pass. |
| **M3a** | Homestead & Property Shell | ✅ **CLOSED** | **100%** | 9/9 housing tables wired. Placement, multi-step construction, decoration limits, tax tasks, furniture recovery. |
| **M3b** | Property Persistence & Crash Safety | ✅ **CLOSED** | **100%** | 0 loss/dup across hard `kill -9` crash cycles; autosave p95 < 2s. |
| **M4** | Trade, Crafting & Transport Integrity | ✅ **CLOSED** | **100%** | Harvest $\rightarrow$ craft pack $\rightarrow$ load vehicle cargo $\rightarrow$ drive route $\rightarrow$ specialty vendor sale $\rightarrow$ gold mail payout. |
| **M5** | Gameplay Actor Contract (M5.1–M5.3) | ✅ **CLOSED** | **100%** | Full contract action surface: Observe, Move, Stop, Target, Cast, Equip, Plant, Harvest, PackPickup/PutDown, Buy/Sell, Board/Unboard, Craft, BuildHouse. |
| **M6** | PlayerBot Living World Framework | ✅ **CLOSED** | **95%** | 250+ provisioned citizens, appearance genetics, schedules, death/resurrection (M6.2), persistent metadata store (B4), 6-hour soak passed 9/9 budgets. |
| **M7** | Adventurer & Party Bots | ✅ **CLOSED** | **95%** | **Adventurer v1**: Hostile targeting, skill rotations, distance keeping, sustain/retreat healing, loot, equip upgrades, return-to-NPC, death recovery.<br>**Party v1**: Invite/accept, formation follow ($\le 3.5\text{m}$), combat assist targeting. |
| **PB-005** | NPC Grounding & Deck Volumes | ✅ **CLOSED** | **90%** | Deck volume system wired (`deck_volumes.json`), Wardton dock snapped, clamp thresholds tightened (+0.5m / -0.1m), Z-stutter/dust/jiggle loop eliminated. |
| **M8** | Living Village & Autonomous Economy | ⏳ **ACTIVE** | **55%** | **C1 Schedules**: Complete.<br>**C2 Social Chatter**: Complete.<br>**C3 Farmer**: Complete (`FarmerCycleScenario`).<br>**C4 Hauler/Trader**: In progress.<br>**C5 Full Day Integration**: Queued. |

---

## 🎮 Game Subsystem Feature Completion (ArcheAge 1.2)

```
[==================================================] 100%  Quests (4,573/4,573 live runnable)
[==============================================    ]  95%  Housing & Homesteads (9/9 tables wired)
[=============================================     ]  90%  Combat, Skills & Buffs (>15k skills)
[=============================================     ]  90%  Farming & Agriculture (crops, livestock)
[==========================================        ]  85%  Transport & Ships (carts, boats, cargo)
[==========================================        ]  85%  Music & Artistry (sheet music, playback)
[========================================          ]  80%  Trade Packs & Economy (specialty trade)
[=====================================             ]  75%  Inventory, Gear & Items (bags, equipment)
[================================                  ]  65%  Auction House (MySQL backend working)
[============                                      ]  25%  Siege & Dominion (declarations/monuments)
[==========                                        ]  20%  Arenas & Ladders (basic packet envelopes)
[                                                  ]   0%  Race Tracks (tables present, unhandled)
```

### Summary of What Works Today:
1. **Complete Leveling 1–55**: Any player or bot can complete the full quest progression across every zone on both continents.
2. **Homesteads & Housing**: Players can claim land, place houses/farms, stage building materials across build steps, decorate within strict item caps, pay taxes, and transfer ownership.
3. **Trade Runs & Vehicles**: Rowboats, clippers, galleons, and farm carts can be summoned, driven, boarded by passengers, loaded with trade packs via direct interaction, sailed across continents, and turned in for mail gold payouts.
4. **Autonomous Living Bots**: Bots embody into normal Character records, follow schedules, chat, join parties, follow leaders in formation, assist in combat, hunt mobs, use healing skills/potions, loot corpses, equip gear upgrades, and harvest crops.
5. **Rock-Solid Stability**: 0 regressions across 2,880+ unit tests, crash-safe autosave persistence (`kill -9` verified), and zero server-side height jitter.
