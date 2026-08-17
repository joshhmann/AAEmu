# JOSH-QAT-PACKET-M4-M5.x

Human QAT packet — fast-forward format. Josh runs; bots never stand in for H.
Author: Nei (quartermaster/continuity) · 2026-08-17 · Card t_cf6710e3
Supersedes: JOSH-HUMAN-TEST-PACKET-M1-M4.md (t_2b654349, Rei) for the M4/M5.x legs.
Venue direction: Aya (HYRAX DIRECTOR) 2026-08-17 — test venue is MIRAGE ISLE.
Scope: M4, M5.1, M5.2 (+ M5.3 when it lands). Docs only — no statuses touched.

---

## 0. HOW THIS PACKET WORKS

Josh's ruling (2026-08-17): human testing must be CHEAP — no walking from
start. Every milestone below follows the same fast-forward shape:

    spawn -> .kit hytest -> .teleport mirage -> run the numbered steps -> PASS/FAIL/CAVEAT

Requirements are indexed to the canonical REQ-M-* sets in ROADMAP.md (already
Rei-verified). Every test has FIVE parts:

1. TESTING REQUIREMENTS — which REQ-M-* the test exercises and the mechanic it
   drives (objective mechanics are already bot-verified; see each header).
2. PREREQUISITE SETUP — the exact fast-forward commands to start AT the
   mechanic (no grinding, no walking).
3. TEST STEPS — numbered, observable actions ("place pack on slave cargo
   point", not "test vehicles").
4. PASS/FAIL CRITERIA — per step: the deterministic check (row/binding/
   transform / animation / world reaction) AND the feel ask (does it look and
   behave right).
5. VERDICT FORMAT — PASS / FAIL / CAVEAT + one-line why. Josh fills these in;
   each attaches to its REQ-M-* as H=... evidence that can land in
   EVIDENCE-LEDGER.md class 7 (human-feel-accepted). H stays UNKNOWN until
   this packet is run.

VERDICT RULE: H=PASS/FAIL can ONLY come from Josh running this packet. Nothing
in this file claims a verdict; the packet makes running cheap so verdicts stop
being deferred.

Runtime prerequisite: CT 133 prod stack (image `aaemu-game:presence-demo` @
6d5a07cf49a5, pinned 95bb1c78e, deploy Mai t_442f3016, prod startup PASS 37
min) plus the hytest kit + mirage teleport from t_e1cf82c9 (Tai, in flight —
see §0.1).

### 0.1 Prerequisite: t_e1cf82c9 (test-infra card, assignee Tai)

Two commands this packet depends on are landing on t_e1cf82c9 (Josh redesign
2026-08-17, fork-only, Rei-gated):

- `.kit hytest` — curated GM kit: level 50 equivalent, labor, gold, portals,
  key gear for the M5.x surface (canonical 1.2 item ids, no inventions).
- `.teleport mirage` — convenience teleport to the test island.

If either command is not live yet (kit list from `.kit ?`, or the teleport
menu), run this packet against the existing fallback in §1.2 and flag CAVEAT
per test: "ran on fallback path, command was absent".

### 0.2 Test venue: Mirage Isle (zone 183 / arche_mall_world, group 49)

Canonical 1.2 test island, in-tree data (418 NPCs / 1196 doodads,
AAEmu.Game/Data/Worlds/arche_mall_world/). Mirage's own test infrastructure
IS the environment:

- Spawn on arrival: X 3680.5, Y 4572.2, Z 156 (world_spawns.json
  arche_mall_world; zone_key 260 / zone 183 / zone_group 49).
- Auctioneer/Warehouse doodad 7983 @ (3452.5, 4289.4) — deposit/withdraw.
- General Vendor doodad 7961 @ (3443.6, 4307.1) — buy/sell.
- Mailbox doodad 320 @ (3446.9, 4295.2) — reward mail checks.
- Fellowship Workbench doodad 7701 @ (3457.4, 4302.2) + Regal craft-station
  cluster @ (3481-3493, 4302-4336) — crafting surface.
- Fenced farm plot @ (3585-3597, 4373-4384) — plant/harvest surface.
- Housing design cluster (townhouses/cottages/manors/villas) @ (3528-3538,
  4370-4380) + farmhouse @ (3854, 4611) + aquafarm @ (3659, 4423) — build
  surface for M5.2.
- Test-drive vehicles: Comet Speedster (18260), Apex Squall (18269),
  Timber Coupe (18270) — board/drive surface.
- Mirage Isle Portal doodads 4895 @ (3565.6, 4335.1) and (3466.2, 4233.6) —
  exit/return.
- GOLD TRADER: NOT on the island. The M4/M5.1 sell leg runs at the Solzreed
  gold trader (NPC 10664, Misty) @ (15180.1, 13612.4, 103.9) — see M4 steps.

---

## 1. M4 — Trade / craft / transport

Supersedes the M4 rows of t_2b654349. Engineering status: EXIT RECORD
2026-08-12 (t_97e59ffc, Rei t_abe87eaf ACCEPT), merged + DEPLOYED to CT 133,
pinned 95bb1c78e. Bot coverage: M4ExitIntegratedSessionTests (4 scripted
actors, real paths), per-object restart E2Es — function + persistence are
PROVEN. What is NOT proven: the FEEL verdict. That is this section.

### 1.1 TESTING REQUIREMENTS

| REQ | Mechanic exercised |
|---|---|
| REQ-M4-1 | Crafting: recipe prerequisites, material + labor consumption, output, workstation range |
| REQ-M4-2 | Trade packs: creation, backpack occupancy, placement/pickup, sale + reward |
| REQ-M4-3 | Vehicles: summon/despawn, passenger + cargo attachment, movement |
| REQ-M4-4 | Integrated exit: harvest → craft → pack → load → travel → sell → reward |
| REQ-M4-5 | M2 release validation — four players from clean reset (Josh solo approximation OK; two-player is the recorded baseline) |
| REQ-M4-6 | Restart assertions — bot-covered (M4_2TradePackRestart / M4Vehicles); Josh spot-checks one restart sighting only |
| REQ-M4-7 | A2 broadcast economics — bot-covered (verified at M4 entry); not a Josh ask |

### 1.2 PREREQUISITE SETUP

```
1. Login (any character; GM level required for commands — AccessLevel >= 100).
2. .kit hytest          -> level 50 equiv, labor, gold, portals, gear.
3. .teleport mirage     -> land at (3680.5, 4572.2, 156), zone 183.
   FALLBACK (until t_e1cf82c9 lands): use a Mirage Isle Portal doodad in the
   main world (e.g. Solis 1.2 Mirage Portal) OR
   .teleport solzreed -> (15369, 13864, 159) and use the island portal there.
4. Confirm the craft station cluster renders at (3481-3493, 4302-4336) and
   the farm plot at (3585-3597, 4373-4384).
```

### 1.3 TEST STEPS + PASS/FAIL

Fixed point for M4 feel: pack 26489 (golden potato bundle) via craft 5404
(3 x golden potato 19887), sell at Solzreed bundle 10. Canonical math:
base = floor(14500 x 4913/1000) + 20000 = 91238; payout = round(91238 x 130%
x 1.05) = 124540; labor -60; pack consumed.

| # | Step (observable action) | PASS (objective) | Feel ask |
|---|---|---|---|
| S1 | Harvest: at farm plot (3585-3597, 4373-4384), work the potato doodad (2259) to harvest potatoes (7992) + golden potatoes (19887) | Items appear in inventory; crop phases advance visually; harvest replants/resets cleanly | Does the growth/harvest animation read correctly? Does the crop look alive while growing? |
| S2 | Craft: at the craft station cluster, craft golden potato pack 26489 (craft 5404) using 3 x 19887 + labor | Pack appears in Backpack slot; labor deducted -60 per pack; materials consumed | Does the craft animation/UI feel right? Is the pack visibly a carried pack on your back? |
| S3 | Load: summon a vehicle (test-drive Comet Speedster 18260 at the vehicle pad) and place the pack on the slave cargo point | Pack attaches at the cargo point (snap-to-cargo behavior); binding row exists; pack stays on vehicle during motion | Does the pack load visually onto the vehicle? Does it look attached, not floating? |
| S4 | Drive: board and drive the vehicle a short route (e.g. pad -> vendor plaza ~100m) | Vehicle moves smoothly; character rides; pack stays attached; dismount leaves pack on vehicle | Does driving feel responsive? Does the world react (collision, terrain)? |
| S5 | Sell: .teleport solzreed -> walk to gold trader Misty (10664) at (15180.1, 13612.4, 103.9), sell the pack | Sell accepted; mail rewards arrive: 124540 gold; labor -60; pack consumed; not sellable same-zone (StoreCantSellSameZone if tried in origin zone) | Does the sale feel satisfying? Does the payout mail arrive visibly? Do you get the "classic ArcheAge trade run" feeling? |
| S6 | Restart spot-check (optional, REQ-M4-6 sighting): after any natural server restart during play, re-enter and confirm vehicle/pack state intact | Vehicle + cargo present exactly once (no dup, no loss) | — (engineering evidence; bots already proved it) |

### 1.4 VERDICT FORMAT (M4)

```
M4-S1: PASS / FAIL / CAVEAT — <one line why>
M4-S2: PASS / FAIL / CAVEAT — <one line why>
M4-S3: PASS / FAIL / CAVEAT — <one line why>
M4-S4: PASS / FAIL / CAVEAT — <one line why>
M4-S5: PASS / FAIL / CAVEAT — <one line why>
OVERALL M4 FEEL: PASS / FAIL / CAVEAT — <the classic-AA verdict (REQ-M4-4 / REQ-M4-5)>
```

Ledger landing: H=PASS/FAIL per REQ-M4-1..5 into EVIDENCE-LEDGER.md M4 row,
class 7, with this card + run date.

---

## 2. M5.1 — Economic extension (contract actions)

Engineering status: all 9 actions merged, Rei-gated, real engine paths
(Plant/Harvest/Craft/PackPickup/PutDown/BoardVehicle/Buy/Sell/
Deposit/Withdraw + LoadPackOntoVehicle + DriveVehicle). Bot coverage:
per-action tests 240/240 family + Phase-2 replay (t_b4f455b0). What is NOT
proven: how the ACTIONS LOOK when a human does them. That is this section.

### 2.1 TESTING REQUIREMENTS

| REQ | Mechanic exercised |
|---|---|
| REQ-M5.1-1 | Economic action surface: Plant, Harvest, Craft, PackPickup/PutDown, BoardVehicle, Buy/Sell, Deposit/Withdraw |
| REQ-M5.1-2 | Every action executes through its REAL engine path — Josh sees real engine behavior, no shortcuts |
| REQ-M5.1-3 | LoadPackOntoVehicle — pack snaps to slave cargo point via real path |
| REQ-M5.1-4 | DriveVehicle — client-authored movement while boarded |
| REQ-M5.1-5 | Exit: curated farm/craft/pack/vehicle/trade segment (Housing.Build FIRST, then farm/storage → craft → pack → load/drive → unload → sell → reward) |

### 2.2 PREREQUISITE SETUP

Same as M4: `.kit hytest` then `.teleport mirage` (fallbacks in §1.2).
Land at (3680.5, 4572.2, 156). Facility map (§0.2) applies.

### 2.3 TEST STEPS + PASS/FAIL

| # | Step (observable action) | PASS (objective) | Feel ask |
|---|---|---|---|
| E1 | Plant: use a seed from the hytest kit on the farm plot (3585-3597, 4373-4384) | Seed consumes; crop doodad spawns at position; cycle state advances | Does the planting gesture/animation read right? Does the seed visibly go into the ground? |
| E2 | Harvest: work the grown crop | Items land in bag; crop phases advance; harvest completes | Does the crop look ready/grown before harvest? Does harvest feel like the farm is alive? |
| E3 | Craft: craft an item at the Fellowship Workbench (7701) or Regal cluster | Craft completes via real CharacterCraft path; labor deducted; item lands in bag | Does the craft station read as a proper station? Does the item appear plausibly in the bag? |
| E4 | PackPickup/PutDown: create a pack (26489 chain or kit-provided), pick it up, put it down near a vehicle | Pack occupies backpack slot on pickup; placed pack appears as a world object on putdown | Does the pack visually ride on the character when carried? Does it sit correctly on the ground when placed? |
| E5 | LoadPackOntoVehicle: put the placed pack onto the test-drive vehicle cargo point | Pack snaps to cargo point (real snap-to-cargo behavior); binding recorded | Does the pack snap cleanly, or clip/float? Does it track the vehicle when it moves? |
| E6 | BoardVehicle + Drive: board the vehicle, drive a route, dismount | Real BindSlave/Seat.LoadPassenger + VehicleMovementModel path; movement broadcasts observed | Does boarding feel right? Does driving feel like driving, not teleporting? |
| E7 | Buy/Sell + Deposit/Withdraw: buy from General Vendor (7961), sell something back, deposit/withdraw at Auctioneer/Warehouse (7983) | Buy deducts gold and grants item; sell grants gold; warehouse deposit/withdraw round-trips currency/items | Do shop + storage UIs feel like the classic mall? Do items/currency move visibly? |
| E8 | Exit segment (REQ-M5.1-5): chain E1→E7 in order (Build FIRST for E3 craft if housing-gated, else farm first) | Whole loop completes with no engine refusal; conservation holds (labor, currency, mail 124540/pack) | Does the FULL economic loop feel like a coherent game loop end-to-end? |

### 2.4 VERDICT FORMAT (M5.1)

```
M5.1-E1..E8: PASS / FAIL / CAVEAT — <one line why each>
OVERALL M5.1 FEEL: PASS / FAIL / CAVEAT — <the economy-feels-alive verdict>
```

Ledger landing: H=PASS/FAIL per REQ-M5.1-1..5 into the M5.1 row, class 7.

---

## 3. M5.2 — Housing.Build contract action

Engineering status: BuildHouse over the REAL HousingManager.Build engine path
(exact CSCreateHousePacket handler call) merged @ 3396d9ef1 (t_94761d55, Rei
t_ebf36737 ACCEPT 3/3); 13 canonical-rig tests, HouseBuild 14/14. Bot coverage
complete. What is NOT proven: what placing a house LOOKS like for a human,
and whether the placement feels canonical.

### 3.1 TESTING REQUIREMENTS

| REQ | Mechanic exercised |
|---|---|
| REQ-M5.2-1 | BuildHouse contract action over the REAL HousingManager.Build engine path |
| REQ-M5.2-2 | Contract tests — bot-covered (13 canonical rig + 14/14 post-fix); not a Josh ask |
| REQ-M5.2-3 | Phase-2 replay sequences Housing.Build BEFORE farm/storage (scope t_2625be99) — Josh verifies the build-first ordering feels right |

### 3.2 PREREQUISITE SETUP

Same fast-forward: `.kit hytest` -> `.teleport mirage`. Use the housing design
cluster @ (3528-3538, 4370-4380) or the farmhouse design @ (3854, 4611) /
aquafarm @ (3659, 4423) as the placement surface. Kit provides the house
design (level/gold/labor already covered by hytest).

### 3.3 TEST STEPS + PASS/FAIL

| # | Step (observable action) | PASS (objective) | Feel ask |
|---|---|---|---|
| H1 | Place: use the house design from the kit at a valid spot in the housing cluster | Placement accepted at a valid spot; construction state begins; house object appears | Does the placement ghost/validation read correctly? Does the construction start look right? |
| H2 | Construct: run the build to completion (watches or instant per kit/command) | Construction completes; door/window phases exist; house usable (door opens) | Does the house LOOK like a house a player would want? Scale/lighting/entrance feel? |
| H3 | Build-FIRST ordering (REQ-M5.2-3): confirm house exists before farm/storage work in the same session | House persists and is interactable before planting/crafting in that session | Does build-first ordering feel natural for the Phase-2 route? |

### 3.4 VERDICT FORMAT (M5.2)

```
M5.2-H1: PASS / FAIL / CAVEAT — <one line why>
M5.2-H2: PASS / FAIL / CAVEAT — <one line why>
M5.2-H3: PASS / FAIL / CAVEAT — <one line why>
OVERALL M5.2 FEEL: PASS / FAIL / CAVEAT — <the homestead-feels-right verdict>
```

Ledger landing: H=PASS/FAIL per REQ-M5.2-1, -3 into the M5.2 row, class 7.

---

## 4. M5.3 — Core surface (Observe · Move · Stop · Target · Cast)

STATUS: SPEC MERGED — 2026-08-17 (t_d837ee0b, Josh GO, spec commit 346eeb792
now on develop via merge 9cc400fd2; ROADMAP M5.3 slice, REQ-M5.3-1..11 +
exit tests E1-E11). Implementation is still PARKED at the M5.2 cap — the five
actions carry v1 implementations on develop since 34cf33cb2 (t_4f11a519) but
are NOT verified against the M5.3 standard (Move is KNOWN non-conforming:
silent local Transform write, no broadcast). This section activates only when
the M5.3 implementation card lands and passes its Rei gate. Do not run it
before then — the actions would not be testing the spec.

### 4.1 TESTING REQUIREMENTS (from spec, REQ-M5.3-1..11)

| REQ | Mechanic exercised |
|---|---|
| REQ-M5.3-2 | Observe — one unified observation snapshot through real engine queries |
| REQ-M5.3-3 | Move — real 1.2 client-authored movement path (no silent transform teleport) |
| REQ-M5.3-4 | Stop — real 1.2 halt semantics, interrupt of running request |
| REQ-M5.3-5 | Target — real engine target-set path (Unit.CurrentTarget) |
| REQ-M5.3-6 | Cast — one skill through the real character skill pipeline (Character.UseSkill) |
| REQ-M5.3-7..11 | Threading-boundary, idempotency, audit trail, contract tests, exit — bot/rig evidence; Josh asks are the five ACTION feels only |

### 4.2 PREREQUISITE SETUP

Same fast-forward (.kit hytest -> .teleport mirage). Actions are driven from
the control-plane/MCP surface (t_446228b5) or GM surface — the human's part is
watching the client react and judging whether the WORLD responds correctly.
Mirage targets: use the island's NPCs (Mirage Isle Guide 10916-10925) as
move/target practice targets; open ground near the vehicle pad for movement.

### 4.3 TEST STEPS + PASS/FAIL

| # | Step (observable action) | PASS (objective) | Feel ask |
|---|---|---|---|
| C1 | Observe: while standing on the island, run one Observe call | Snapshot equals what the client shows: your position, nearby units, state | Does the snapshot match what you SEE on screen? |
| C2 | Move: issue Move to a nearby point (e.g. vehicle pad -> vendor plaza) | Character walks via the real movement path; movement broadcasts reach the client; arrival completes | Does the character walk smoothly with a real animation, no snapping? |
| C3 | Stop: issue Move, then Stop mid-walk | Movement halts cleanly; request ends Interrupted("stop requested") | Does the character stop naturally (decelerate), not freeze mid-pose? |
| C4 | Target: target a Mirage guide NPC | Target resolves (CurrentTarget set); selection indicator appears | Does targeting feel like classic AA targeting? |
| C5 | Cast: cast one learned skill (kit must grant one) at the NPC | Skill executes through real pipeline; mana/cooldown consumed; effect applies | Does the cast animation/time play correctly? Does the world react (damage/buff)? |

### 4.4 VERDICT FORMAT (M5.3 — only after the spec lands)

```
M5.3-C1..C5: PASS / FAIL / CAVEAT — <one line why each>
OVERALL M5.3 FEEL: PASS / FAIL / CAVEAT — <the actions-feel-native verdict>
```

Ledger landing: H=PASS/FAIL per REQ-M5.3-2..6 into the M5.3 row, class 7,
only after the M5.3 implementation card lands and passes its Rei gate
(spec merged 2026-08-17 @ 9cc400fd2; implementation still parked).

---

## 5. MASTER VERDICT SHEET (fill in as you run)

```
Date:            Runtime:          Client:          Server (SHA): 
Venue: Mirage Isle (zone 183)      Commands used: .kit hytest [Y/N]  .teleport mirage [Y/N]

M4   S1 ______  S2 ______  S3 ______  S4 ______  S5 ______  S6 ______  OVERALL ______
M5.1 E1 ______  E2 ______  E3 ______  E4 ______  E5 ______  E6 ______  E7 ______  E8 ______  OVERALL ______
M5.2 H1 ______  H2 ______  H3 ______  OVERALL ______
M5.3 C1 ______  C2 ______  C3 ______  C4 ______  C5 ______  OVERALL ______  [only after M5.3 impl lands + Rei gate]

Blockers (FAIL, no workaround — stage + repro + evidence, do NOT GM-repair):
1.
2.

CAVEAT notes (worked, but looked off):
1.
2.
```

How verdicts land in EVIDENCE-LEDGER.md: each PASS/FAIL above is written into
the ledger's class-7 (human-feel-accepted) cell for its milestone row, per
REQ, cited with this card (t_cf6710e3) + run date. No bot/scripted evidence
is ever recorded as H=2 — H flips to PASS/FAIL only from this sheet.

---

## 6. SOURCES (canonical, read from the current tree)

- ROADMAP.md — M4 (REQ-M4-1..7, exit record t_97e59ffc), M5 (REQ-M5-1..15),
  M5.1 (REQ-M5.1-1..5), M5.2 (REQ-M5.2-1..3), M5.3 spec (branch
  docs/m5.3-spec, REQ-M5.3-1..11 + E1-E11), deferred gates #1-5.
- EVIDENCE-LEDGER.md — 7 evidence states; class 7 = human-feel-accepted,
  NEVER inferred from bot/scripted evidence.
- AAEmu.Game/Scripts/Commands/ — Teleport.cs (named locations incl.
  solzreed), Kit.cs + kits.json (kit surface, 28 kits today; hytest lands via
  t_e1cf82c9), Move.cs, MoveTo.cs, BuildHouse.cs, AddGold/AddLabor/AddXP/
  ChangeLevel/AddPortals/GodMode/IgnoreCooldowns.
- AAEmu.Game/Data/Worlds/arche_mall_world/ — npc_spawns.json (418),
  doodad_spawns.json (1196): facility positions cited in §0.2.
- AAEmu.Game/Data/Worlds/world_spawns.json — arche_mall_world spawn
  (3680.518, 4572.221, 156, zone_key 260).
- AAEmu.Game/Data/compact.sqlite3 — zones (183 arche_mall, key 260,
  group 49); canonical item/craft ids verified against the M4 exit math
  (packs: 124540 payout, craft 5404, items 7992/19887/26489).
- M4ExitIntegratedSessionTests.cs + M3aM4ReplayScenario.cs — canonical route
  + conservation math (labor -60, 124540/pack, pack exactly-once).

Fork-local doc — never in an upstream PR (THE RULE).