# PlayerBot Capability Matrix (Perceive / Decide / Act / Verify)

Populated from implementation reality @ develop `b6992c5ce` (2026-08-24).
Legend: ✅ through real engine paths · 🟡 partial/rig-only · ❌ missing.
Autonomous Loop = can a bot run this system's loop unattended end-to-end.

| System | Perceive | Decide | Act | Verify | Autonomous Loop |
|---|---|---|---|---|---|
| Movement | 🟡 positions via Observe; no terrain awareness | ✅ simple (straight-leg, standoff band, stuck detection) | ✅ MoveTo/MoveToUnit/DriveVehicle (real CSMoveUnit path) | ✅ arrival events + stuck reasons | 🟡 open courtyards only — PB-001 |
| Combat | ✅ Observe (units, hp, targets) + causal traces (hp deltas) | ✅ rotation priority, sustain thresholds, no-progress skip | ✅ SetTarget/Cast (real skill pipeline) | ✅ kill credit + hp-delta traces | ✅ party spike live-proven |
| Quests | 🟡 contract reports state; no world quest discovery | 🟡 scenario/scripted chains only | ✅ AcceptQuest/TurnInQuest/AdvanceQuest (real gates) | ✅ criteria + census harness | 🟡 curated golden route live; discovery primitive missing — PB-002 |
| Loot | ✅ corpse/inventory via contract | ✅ loot-after-kill step | ✅ Loot action | ✅ item-granted criteria | ✅ within hunt loops |
| Vendors | ✅ money/inventory observable | ✅ trivial buy/sell rules | ✅ Buy/Sell actions (real shop paths) | ✅ ledger conservation | ✅ economy cycle live-proven |
| Crafting | ✅ inventory/materials observable | ✅ recipe steps scripted | ✅ Craft action (real CharacterCraft) | ✅ products granted + materials consumed asserts | ✅ in economy cycle |
| Farming | ✅ crop growth observable (doodad phases) | ✅ mature→harvest rule | ✅ Plant/Harvest actions (real Doodad.Use) | ✅ doodad state + items | ✅ in economy cycle (+ restart persistence) |
| Housing | ✅ ownership/placeable observable | 🟡 build step scripted | ✅ BuildHouse action (real HousingManager.Build) | ✅ persisted rows across restarts | 🟡 M5.2 slice; decoration interior loops open |
| Trade packs | ✅ pack slot/bundle observable | 🟡 route steps scripted | ✅ PackPickup/PutDown/LoadPackOntoVehicle/DriveVehicle | ✅ payout conservation (mail + labor) | 🟡 M4 exit rig-proven; live replay = deferred gate #4 |
| Fishing | ✅ bite/labor observable | ✅ cast-retry loop | ✅ CastAt(position) (real plot 809) | ✅ labor/worm/loot deltas | ✅ FishingVerificationE2eTests live |
| Duels | ✅ challenge frames observable | ✅ accept/refuse rules | ✅ packet injection (CSChallengeDuel/StartDuel) | ✅ started frames + faction swap | 🟡 live E2E PASS; not an autonomous loop yet |
| Expeditions | ✅ membership observable | ✅ invite/accept rules | ✅ ExpeditionCreate/Invite/Accept/Leave actions | ✅ roster asserts | 🟡 rig-level lifecycle; not composed into bot gameplay |
| Parties | ✅ team registry + member state | ✅ follow/assist/fault rules | ✅ PartyInvite/Accept/FollowAssist/SpikeScenario | ✅ membership + kill credit | ✅ party spike live (3 bots vs elite) |
| Indun (dungeons) | ✅ instance isolation observable | 🟡 enter/clear steps scripted | 🟡 portal use via real doodad-cast injection; interior combat ✅ | ✅ room-clear events + isolation asserts | 🟡 Hadir Farm E2E PASS ×2; exit-portal data gap PB-003 |
| Banking/storage | ✅ bank balances observable | ✅ deposit/withdraw rules | ✅ DepositMoney/Item, Withdraw actions | ✅ bank conservation across restart | ✅ in economy cycle |
| Chat/social presence | ✅ proximity observable | ✅ greet/cooldown rules | ✅ real local-chat emission (BotChatterService) | ✅ sink capture tests | 🟡 greetings only; conversation depth open |

## Highest-leverage gaps (one primitive unlocks many loops)
1. **QuestDiscovery perception** (PB-002) → unlocks autonomous leveling
2. **Waypoint-network movement** (PB-001) → unlocks dungeons, cross-region caravans, believable travel
3. **Doodad-interact contract action** (generalize the fishing portal-injection into a first-class InteractWith(doodad)) → unlocks dungeon portals, convert/buy fish stands, world interactables
4. **Proximity-fidelity driving** (A3: RefreshPressure never driven) → unlocks 100+ bot villages cheaply
