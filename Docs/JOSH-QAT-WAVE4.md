# JOSH-QAT-WAVE4

Human QAT packet — wave 4. Josh runs; bots never stand in for H.
Author: ox-alpha docs wave · 2026-08-25
Format mirrors: JOSH-QAT-PACKET-M4-M5.x.md (t_cf6710e3, Nei) — fast-forward shape,
PASS/FAIL/CAVEAT verdicts, H only ever lands from this packet being run.
Scope: today's develop landings that need a human eyeball (mail return + mail
ownership guards, labor regen revival, war-gated PvP honor, nav G-cost feel) plus
four prod observations to chase (NPC grounding, boats, slave-test console errors,
Mirage Isle content density). Docs only — no statuses touched.

---

## 0. HOW THIS PACKET WORKS

Same rules as the M4/M5.x packet:

    spawn -> GM kit -> run the numbered steps -> PASS / FAIL / CAVEAT

Every QAT has: ID/TITLE · PREREQ (with the exact minimum build SHA, honestly
marked **current prod** vs **requires deploy ≥ <sha>**) · SETUP · STEPS ·
EXPECTED · PASS/FAIL · REPORT-ON-FAILURE (what evidence to capture).

VERDICT RULE unchanged: H=PASS/FAIL can ONLY come from Josh running this
packet. Nothing here claims a verdict.

### 0.1 Build prerequisite (read once)

Verified before writing: fork `develop` @ origin/develop head **bfbea4093**
(2026-08-25 20:23 -0700). Today's behavior changes landed in:

| SHA | Change | Affects |
|---|---|---|
| 8d5a0fb20 | PvP honor war-gated; War base 20→40 (32 killer + 4/assist INFERRED split); victim −10 | W4-4 |
| 08d2d7a0d | Labor regen revived (`Labor:` config section, Mode Unchained default: everyone 10 LP per 5 min online AND offline, cap 5000; Mode VanillaRetail restores retail free 5/patron 10 tiers, caps 2000/5000) | W4-3 |
| 531a732fe | CSReturnMailPacket wired at opcode **0x0a2 (STRONGLY_INFERRED)** + ownership guards on read/take/delete receive paths | W4-1, W4-2 |
| 7e5d96e74 + 1b8bf260e + 0d6736282 | Nav A* G-cost fix (+ spatial grid) — detour factor measured 1.91×→1.22× on bots | W4-5 (feel ask only) |

**CT 133 prod is still image `aaemu-game:presence-demo` pinned 95bb1c78e
(2026-08-12).** That means: W4-1 through W4-4 are meaningless until a deploy
carrying bfbea4093 (or at least the per-QAT SHA) reaches your stack. Each QAT
states its own minimum. W4-5 through W4-8 are observation packs — they are
valid on current prod and get BETTER after deploy.

If you are unsure what is deployed: run `.labor 100` — if LP does not visibly
move to 100, you are pre-08d2d7a0d. Or just ask Mai for the deploy manifest.

### 0.2 Fast-forward commands (unchanged from the M4/M5.x packet)

```
.kit hytest            # level/labor/gold/portals consumables (or .level/.labor/.gold)
.teleport mirage       # land (3680.5, 4572.2, 156), zone 183 — venue for most tests
.position              # dumps your coords — the "coords dump" used by W4-5
.zonestate             # zone-state viewer/changer (W4-4)
```

Two-client QATs (W4-2, W4-4) are marked **[TWO-CLIENT]**: one person, two game
clients/two accounts. Everything else is solo.

---

## W4-1 — MAIL RETURN (the 0x0a2 hypothesis)

**PREREQ: requires deploy ≥ 531a732fe.** Solo.

THE FLAG, stated plainly: the client's Return button fires an opcode nobody can
read from the shipped binary (x2game.dll is obfuscated). We wired
`CSReturnMailPacket` at **0x0a2** because the decompiled mailbox Lua shows the
full `X2Mail:*` send-API set mapping 1:1 onto AAEmu's contiguous C2S mail block
(Send=0x098 … Delete=0x0a1, ReportSpam=0x0a3) and 0x0a2 is the only hole. That
is slot arithmetic, not proof. **This QAT IS the confirmation experiment:
if Return fails cleanly here, the real opcode differs — report, don't debug.**

**SETUP**
```
1. Log in char A (GM). .teleport mirage -> walk to mailbox doodad 320
   at (3446.9, 4295.2).
2. Give yourself something worth returning: .item add self 12900 1
   (any cheap equipment works) and make sure you have >1s copper.
3. At the mailbox: send mail from A to your alt B (same account is fine):
   type EXPRESS if the UI offers it (delivers instantly; Normal has a
   30-minute server delay), attach the item, title "returntest".
   Fee shown should be 100c express base (50c normal; +30c per extra
   attachment beyond the first).
4. Log out, log in as B, open mailbox: mail arrived, attachment listed.
5. READ the mail (open the body). The server refuses to return unread mail —
   reading first is part of the test, not a workaround.
```

**STEPS + EXPECTED**

| # | Step | Expected |
|---|---|---|
| R1 | In B's open read window, click the RETURN button, confirm the dialog | Mail leaves B's box. No error popup. (Server sends SCMailReturnedPacket.) |
| R2 | Log back to A, open mailbox | The mail is back, addressed B→A: same title/text, SAME attachment (grade/count intact), attached money intact if you attached any |
| R3 | (Integrity) On A, take the returned attachment into bag | Item taken normally; grade/enchant state identical to what A originally sent |
| R4 | (Once-only guard) Repeat the whole loop and have B return the SAME mail twice | Second return refused with an error (MailNotAllowedToReturn family) |

Note: retail charged no fee to RECEIVE-side return in our reconstruction — the
bounce keeps attachments/money whole and swaps sender/receiver. If you observe
a fee being charged or money vanishing, that's a FAIL detail worth reporting.

**PASS/FAIL**: PASS = R1–R4 all as expected. FAIL = clicking Return does
nothing visible, errors immediately, wrong recipient, or attachment loss.

**REPORT-ON-FAILURE (this is the valuable part — capture all of it):**
1. Screenshot of B's read window right after the click (any error dialog text verbatim).
2. Server console/log lines from ~10 s before to ~10 s after the click — we need
   to know whether ANY C2S packet arrived (wrong-but-something vs nothing-at-all).
3. Whether other mail buttons work from the same window (Take Attachment, Delete,
   Report Spam) — tells us if the failure is return-specific or the whole window.
4. Timestamp + character names + the mails-table row id if you can reach MySQL
   (`SELECT id,type,status,sender_name,receiver_name FROM aaemu_game.mails ORDER BY id DESC LIMIT 5;`).

---

## W4-2 — MAIL OWNERSHIP GUARDS [TWO-CLIENT]

**PREREQ: requires deploy ≥ 531a732fe.** Two clients/accounts.

Background: before 531a732fe, 4 of the 5 receive-path packets trusted the
client-supplied mail id — a hacked/modified client could read, drain, or delete
someone else's mail by id. Guards now mirror the sequential-take refusal on
Read / TakeItem / TakeMoney / Delete (+ TakeAll). The deep coverage lives in
unit tests (MailOwnershipGuardTests); what a human can honestly verify is that
no UI-visible leak path exists and owner flows still work.

**SETUP**
```
Client 1: char A (GM). Client 2: char B on a DIFFERENT account.
A sends B a normal express mail with 1 item + some copper (see W4-1 setup).
Both stand at the Mirage mailbox plaza (3443-3460, 4289-4310).
```

**STEPS + EXPECTED**

| # | Step | Expected |
|---|---|---|
| G1 | B opens mailbox | Only B's mail visible. No trace of A's other mail, sent or received |
| G2 | B takes attachment + money, then deletes the mail | All succeed (owner flow must not regress) |
| G3 | A opens own mailbox sent-tab | A still sees their copy of the sent mail (sent-tab read stays allowed for the sender) and CAN still read its body |
| G4 | A tries to DELETE the sent-tab entry while it still exists | Refused/no-op (pre-existing known limitation: sent entries with live claims aren't deletable — confirm it didn't silently corrupt anything) |
| G5 | If you have ANY modified-client or packet-tool inclination: attempt reading/deleting a foreign mail id | Server refusal with MailInvalid error; log line "check for hackers trying to steal mails" / ownership-guard warn appears |

G5 is optional — skip without prejudice if you don't run tools.

**PASS/FAIL**: PASS = G1–G4 clean. FAIL = any point where B sees or touches
A's mail, OR owner flows start refusing (guard too tight — equally a bug).

**REPORT-ON-FAILURE**: both character names, which step, screenshot, server log
window ±30 s, and the mail ids involved (mails table query from W4-1).

---

## W4-3 — LABOR REGEN REVIVAL

**PREREQ: requires deploy ≥ 08d2d7a0d.** Solo. Default config assumed:
`Labor.Mode = Unchained` → EVERYONE regens 10 LP / 5 min online AND offline,
cap 5000. If prod's Config.json overrides differ, note the values and judge
against those instead.

**SETUP**
```
Log in fresh. Note current LP exactly (character sheet).
Have a labor SPEND ready: fishing pole + bait, or stand at the Fellowship
Workbench doodad 7701 @ (3457.4, 4302.2) with a cheap craft queued.
```

**STEPS + EXPECTED**

| # | Step | Expected |
|---|---|---|
| L1 | Stand online, do nothing labor-related, stopwatch 5 min (+~30 s grace) | LP ticks up by EXACTLY +10 (not +5, not 0, not 11). Watch a second tick if patient: another +10 |
| L2 | Spend: fish once or complete one workbench craft | LP drops by the action's displayed cost; labor-XP accrues (XP bar moves on spend) |
| L3 | Cap: `.labor 4995`, wait one 5-min tick | LP displays 5000 and STAYS 5000 — clamped, not overflowing |
| L4 | Offline: log out for ≥ 11 minutes, log back in | LP rose by floor(minutesOffline/5) × 10 — e.g. 12 min away ≈ +20. Record exact away-time and delta |
| L5 | (Optional, only if prod runs Mode=VanillaRetail) repeat L1 on a non-patron account | +5 per tick online; NO offline regen; cap 2000 |

**PASS/FAIL**: PASS = L1–L4 match. FAIL = zero regen (regen still dead),
wrong amount (tier math leaking through Unchained), missing clamp, or
offline delta of 0.

**REPORT-ON-FAILURE**: LP readings with timestamps for each step, the
`Labor` section of the server's Config.json (copy-paste), and whether XP moved
on spend (L2).

---

## W4-4 — PVP HONOR WAR-GATE [TWO-CLIENT]

**PREREQ: requires deploy ≥ 8d5a0fb20.** Two clients/accounts. PvpHonorRate
assumed 1.0 (World config) — multiply expectations if prod overrides.

Owner ruling was "keep it korean": Conflict-zone kills award ZERO honor; War
kills award base 40 — solo kill = +40 to killer; with assists = +32 killer +
+4 per assist (INFERRED split — the 40 base is official RU 2.9, the split is
our convention). Victim loses −10, clamped at 0.

**SETUP**
```
Both clients logged in, chars A (killer) and B (victim), same starting zone —
Solzreed (.teleport solzreed -> 15369, 13864, 159) is fine and has conflict data.
Make B attackable WITHOUT tripping the justice system: target B on A's client
and run .setfaction haranya (B becomes hostile-faction; A stays Nuia).
Note BOTH characters' honor values before every leg (character sheet).
Zone states: .zonestate peace / .zonestate conflict / .zonestate war
(names or digits 7/5/6). Verify state took effect via plain `.zonestate`.
Kill method: whatever is fastest — .damage, duel-style burst, or just A
attacking until B dies. Res B between legs (or accept death penalties).
```

**STEPS + EXPECTED**

| # | Step | Expected |
|---|---|---|
| K1 | `.zonestate peace`, A kills B | Killer honor delta ZERO. Victim delta ZERO (peace kills aren't the war path) |
| K2 | `.zonestate conflict`, A kills B | Killer honor delta **ZERO** — the headline fix. Zone escalation STILL registers (zone may progress toward war on repeated kills — note any zone-state banner) |
| K3 | `.zonestate war`, A kills B (no third party touched B within 30 s) | Killer **+40**, victim **−10** (clamped at 0 if broke) |
| K4 | OPTIONAL assist leg — needs a THIRD character to damage B within 30 s of the kill. Skip unless you spin up a third client/bot | Killer **+32**, each assist **+4** |

**PASS/FAIL**: PASS = K1/K2/K3 exact (K4 informational). FAIL = any honor paid
in Conflict, wrong War amounts, victim penalty missing or exceeding 10.

**REPORT-ON-FAILURE**: honor before/after per character per leg, zone name +
state at kill time, factions of both, timestamps, server log ±30 s
(CharacterCombat honor lines).

CAVEAT ask regardless of outcome: does the zone visually announce Conflict/War
on your client when you flip it with `.zonestate`? (We've never confirmed the
broadcast against a real client.)

---

## W4-5 — NPC GROUNDING TOUR

**PREREQ: current prod OK.** Solo. Purely observational — nothing can be
broken by walking around. Context: prod reports many NPCs floating, standing
under roads, or clipping into terrain. The offline grounding audit is DONE
(`scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md`):
89.5% of 23,058 defect-audited spawns grounded, 5.6% severely floating,
2.9% submerged — its worst offenders are embedded below as stops T13–T20.

The engineering ask behind your eyes: nav got an A* repair today (bots measure
detours shrinking 1.91×→1.22×) but NOTHING in this QAT requires deploy — we
want Josh-grade ground truth on WHERE grounding looks worst.

**METHOD FOR EVERY STOP**
```
Walk the stop slowly. Look at every visible NPC: feet vs ground.
For any bad one: TARGET it (shows name), run .position, paste the output +
NPC name into your notes, screenshot. One line per finding is enough:
  "<zone> <npc name> @ <x,y,z> — floating ~2m / sunk to knees / under road"
```

**TOUR STOP LIST (fixed portion)**

| # | Stop | Coords (x, y) | Look for |
|---|---|---|---|
| T1 | Mirage arrival plaza | 3680.5, 4572.2 | Guides/greeters near spawn |
| T2 | Mirage vendor row | 3443–3460, 4289–4310 | Vendor 7961, warehouse 7983, mailbox 320 area NPCs |
| T3 | Mirage craft cluster | 3481–3493, 4302–4336 | Station-side NPCs |
| T4 | Mirage housing display cluster | 3528–3538, 4370–4380 | Real-estate NPCs among display houses |
| T5 | Mirage farm + aquafarm path | 3585–3659, 4373–4423 | Rural NPCs along the walk |
| T6 | Mirage farmhouse | 3854, 4611 | Isolated building NPCs |
| T7 | Mirage portals | 3565.6, 4335.1 and 3466.2, 4233.6 | Portal-side NPCs |
| T8 | Solzreed landing | 15369, 13864, 159 | Arrival-area NPCs |
| T9 | Solzreed gold trader | 15180.1, 13612.4, 103.9 | Misty (10664) + dock neighbors |
| T10 | Moang housing plots walk | w_solzreed_1, zone key 142 | Plot-edge NPCs |
| T11 | Any road segment T8→T9 | follow the road | THE classic complaint: NPCs standing UNDER the road surface or floating beside it — walk, don't teleport, this leg |
| T12 | Any bridge/dock on the route | wherever found | Waterline NPCs half-submerged / hovering |

Stops T13–T20: WORST OFFENDERS from npc-grounding-audit-2026-08-25.md
(offline heightmap audit of all 25,118 main_world spawns: 89.5% grounded /
3.7% minor float / 5.6% severe float >2 m / 2.9% submerged — your job is to
confirm the worst of these look as bad in a real client). Teleport near each
coord with `.move <x> <y> <z>` (the only raw-coord GM path in this tree;
`.position` to fine-tune). Z guidance from the audit: FLOATER stops T13–T17 —
use the data's spawn z but land a touch LOWER (ground + ~2 m) for the cleanest
view of the offset; CAVE-SUSPECT stops T18–T20 — use the listed z as-is.
If you lack the z, start z≈150–200 and adjust after landing. Then LOOK:

| # | Zone | NPC @ coords (x, y) | Audit says |
|---|---|---|---|
| T13 | e_hasla_2 | Citizen @ 30011.7, 8709.9 | FLOATING +183.6 m — worst in the world |
| T14 | e_hasla_2 | Maid @ 29966.4, 8730.9 | FLOATING +172.5 m |
| T15 | e_hasla_1 | Ravra @ 28904.7, 7679.7 | FLOATING +142.1 m |
| T16 | e_lokas_checkers_2 | Purple Falcon @ 24837.3, 10710.2 | FLOATING +119.6 m |
| T17 | s_freedom_island | Ocean Razorbeak @ 21726.4, 17789.8 | FLOATING +100.5 m |
| T18 | w_white_forest_1 | Striped Muzzle Kobold Miner @ 8854.9, 13568.4 | SUBMERGED −270.3 m |
| T19 | w_lilyut_meadow_2 | Dahuta Cult Priestess @ 11909.5, 16410.6 | SUBMERGED −213.8 m |
| T20 | w_white_forest_1 | Deshak the Cave Troll @ 8881.3, 13523.2 | SUBMERGED −163.4 m |

Audit caveat you should test directly: the SUBMERGED entries may be LEGIT cave
dwellers — the offline terrain reference can't see cave meshes. If T18/T20 are
happily pacing around inside a cave, record CAVEAT-LEGIT, not FAIL.

BONUS GROUP TOURS if you have time: w_two_crowns_2 (~159 floaters),
e_hasla_2 (~95), e_mahadevi_2 (~94) — walk any settlement in these zones and
count what you see vs the audit's offline numbers.

**PASS/FAIL**: This QAT doesn't fail — it MEASURES. Verdict format:
`GROUNDING: CLEAN / MINOR (<5 findings, cosmetic) / BAD (≥5 findings or any
gameplay-blocking) — <count> findings, worst: <one-liner>`.

**REPORT**: your finding lines (coords mandatory) + screenshots. These feed
the grounding audit directly — raw notes are perfect, no formatting needed.

---

## W4-6 — BOATS (rowboat core; clipper stretch)

**PREREQ: current prod OK.** Solo. Prod status is "boats uncertain" — nobody
has written down whether a human can actually sail anything. Ships dossier
says physics is real (Jitter2, per-kind tuning) and boarding binds the DRIVER
seat; passenger seating rides on seat doodads and is UNCONFIRMED live.

**CORE SETUP**
```
.teleport mirage, then get to water (the island shore works; a lake is fine —
rowboats don't care about salt).
Summon: .slave spawn 15        (rowboat, slave template 15)
```

**CORE STEPS + EXPECTED**

| # | Step | Expected | Feel ask |
|---|---|---|---|
| B1 | Summon | Rowboat spawns in front of you, in/over water, no console spam | Does it look placed-right, or spawning mid-air/beach? |
| B2 | Click boat to board | You attach at the helm/driver position; camera follows | Snap-in clean? Right seat height? |
| B3 | Helm steering: throttle up, turn hard both ways, stop | Boat responds within ~1 s of input; turns are arcs, not spins; stops decelerate | Does steering feel like a boat or like ice? Deadzone? |
| B4 | Passenger view (second character on same account if you can dual-box, else visual-only): watch from a dock while it sails | Boat bobs with waves, banks into turns, no rubber-banding from shore view | Does it look alive on the water? |
| B5 | Dock/disembark: steer to shore, dismount | Clean detach; boat remains; re-board works | Boarding again glitch-free? |
| B6 | Despawn: as owner, despawn/remove the boat (`.slave remove` or UI) | Boat gone; no orphaned ghost; summon again works | — |

**STRETCH — clipper build flow (optional, expect pain, report anyway):**
Design certificates exist in data (adventure clipper design 23636, merchant
23698, fishing boat 28013). Grant via `.item add self <id> 1`; materials
consumed AT PLACEMENT (design + taxes + skill reagents, e.g. lumber 8318×10 +
iron 8337×10 for the clipper). Flow: place drydock → cast plank-pack skills on
frame per step → final launch interaction grants scroll → scroll summons ship.
KNOWN SHARP SEAMS, please note what you see: (a) the completion ceremony's
launch interaction is unverified against a real client — if the finished frame
just sits there, THAT is the finding; (b) half-built frames vanish on restart
(memory-only) — don't lose real materials to a restart mid-build.

**PASS/FAIL**: Core = B1–B6. PASS = all six behave. CAVEAT = works but feels
wrong (describe the feel — that's the deliverable). FAIL = cannot board /
cannot steer / boat sinks or vanishes.

**REPORT-ON-FAILURE**: `.position` + slave template id, what the console said,
screenshot of the broken pose/state, client behavior vs server behavior
(did YOUR view and a second client's view agree?).

---

## W4-7 — SLAVETEST OBSERVATION (console errors + naked slaves)

**PREREQ: current prod OK (observation valid on any build); CONFIRMATION leg
requires deploy ≥ fix/slavetest @ d68efe74ab once merged to develop.** Solo.
Root cause has been FOUND by the hunting lane — this QAT is now (a) human
confirmation of the diagnosis on your stack and (b) regression observation if
the fix is already deployed.

Clarification first: there is no command literally named `/slavetest` in this
tree. Prod "/slavetest" = **`.testslave`** (alias `.test_slave`) — hand-spawns
one hardcoded test slave (template 73 cotton-field cart, model 1008, level 50,
faction 143). `.slave spawn <templateId>` exists separately and creates slaves
properly (`.slave info` / `.slave remove` / `.slave save` also exist).

FINDINGS ALREADY IN HAND (isolated-stack repro, hunting lane): each
`.testslave` run throws THREE server-side errors, client-visible only as a
generic packet error:
```
[ERROR] PacketStream - Error writing string. System.ArgumentNullException:
Value cannot be null. (Parameter 's')
   at PacketStream.Write(String) PacketStream.cs:1575
```
Cause: the hand-rolled Slave has a null Name that SCUnitStatePacket.cs:72
tries to write; the same shortcut path skips slave_initial_items equipment and
DoodadBindings → missing clothes. Fix (branch `fix/slavetest` @ d68efe74ab,
NOT yet on develop at writing time): delegate to
`SlaveManager.Create(character, null, 73)`; regression E2E PASS.

**STEPS + EXPECTED**

| # | Step | Expected |
|---|---|---|
| S1 | Run `.testslave` once; watch the server console | IF FIX NOT DEPLOYED: the PacketStream ArgumentNull error above, ×3 per run. IF DEPLOYED: clean spawn, zero errors — that IS the PASS signal |
| S2 | Look at the spawned cart | Pre-fix: naked/base-model (no cotton, no equipment bindings). Post-fix: dressed per slave_initial_items. Record which you see |
| S3 | `.slave spawn 73`, compare side by side | The properly-created slave is dressed and named in BOTH cases — this contrast confirms the bypass diagnosis if you're on the old build |
| S4 | Try 2–3 more templates via `.slave spawn <id>` (e.g. 15 rowboat + any land mount template) | Which types spawn clean, which error — template ids + verbatim console text |
| S5 | Interact: try mounting/boarding each spawned slave | Bind accepted? Movement works? Error codes on refusal? |

S1/S2 are the human half of the hunting lane's evidence: their E2E proves the
code path; only your eyes confirm what a PLAYER sees pre- vs post-fix.

**VERDICT FORMAT**:
```
SLAVETEST: PRE-FIX OBSERVED / POST-FIX CLEAN / STILL BROKEN
console errors verbatim (or "none"), dressed/bare per command path,
which templates worked, anything else that looked wrong.
Screenshots welcome.
```

---

## W4-8 — MIRAGE ISLAND CONTENT WALK

**PREREQ: current prod OK.** Solo. Feeds a future content audit — prod says
the island is sparse beyond housing even though interactions work.

**METHOD**: walk the island edge-to-edge (don't portal-hop) with one question
in mind: *does this feel like a place, or like a stage set?* At each facility,
do ONE interaction to confirm it's alive.

**INTERACTION INVENTORY**

| Facility | Where (x, y) | Interaction to try | Working? |
|---|---|---|---|
| Mailbox 320 | 3446.9, 4295.2 | Open mailbox | |
| General vendor 7961 | 3443.6, 4307.1 | Buy cheapest, sell one coin | |
| Warehouse/auctioneer 7983 | 3452.5, 4289.4 | Deposit + withdraw 1 item | |
| Fellowship workbench 7701 | 3457.4, 4302.2 | Start smallest craft | |
| Regal craft-station cluster | 3481–3493, 4302–4336 | Open crafting UI at one station | |
| Fenced farm plot | 3585–3597, 4373–4384 | Harvest/work pre-planted crop (doodad 2259) | |
| Housing design cluster | 3528–3538, 4370–4380 | Enter/inspect a display house | |
| Test-drive vehicle pad | island pad | Board Comet Speedster 18260, drive 50 m | |
| Mirage portals 4895 ×2 | 3565.6, 4335.1 / 3466.2, 4233.6 | Open portal UI (do NOT exit — note UI opens) | |
| Aquafarm | 3659, 4423 | Interact if anything is interactable | |

Then the SPARSENESS inventory — count, don't judge:
1. Total NPC names you saw (rough count is fine).
2. How many were interactable (shop/quest/dialog) vs scenery-only.
3. Anything that existed in retail Mirage you remember missing (quest boards?
   trainers? mirage-specific vendors?) — list from memory, flagged as memory.
4. Dead zones: areas with structures/NPC placements but nothing interactive.

**VERDICT FORMAT**:
```
MIRAGE WALK: interactions <N>/10 working. Density: DEAD / SPARSE / POPULATED.
Missing-from-memory list: <items>. Dead zones: <areas>.
```

---

## MASTER VERDICT SHEET (fill in as you run)

```
Date:            Runtime SHA (ask Mai / server banner):            Client:
Two-client legs done: W4-2 [Y/N]   W4-4 [Y/N] (K4 third-char assist [Y/N/N-A])

W4-1 MAIL RETURN      R1 ____ R2 ____ R3 ____ R4 ____  OVERALL ____
     (if FAIL: console capture attached? [Y/N] — REQUIRED for the 0x0a2 call)
W4-2 MAIL GUARDS      G1 ____ G2 ____ G3 ____ G4 ____  OVERALL ____
W4-3 LABOR            L1 ____ L2 ____ L3 ____ L4 ____  OVERALL ____  (mode observed: ______)
W4-4 PVP HONOR        K1 ____ K2 ____ K3 ____ K4 ____  OVERALL ____  (zone banner seen: Y/N)
W4-5 GROUNDING        CLEAN / MINOR / BAD — ___ findings, worst: ____________________
W4-6 BOATS            B1 ____ B2 ____ B3 ____ B4 ____ B5 ____ B6 ____  OVERALL ____
                      stretch clipper attempted: [Y/N] — result: ____________________
W4-7 SLAVETEST        paragraph attached: [Y/N]
W4-8 MIRAGE           interactions ___/10 — density: ____________

Blockers (FAIL, no workaround — stage + repro + evidence):
1.
2.

CAVEAT notes (worked, but felt off):
1.
2.
```

How verdicts land: each OVERALL goes into EVIDENCE-LEDGER.md class 7
(human-feel-accepted) against the matching scorecard rows (MAIL-01 return/
security legs, LABOR regen, PVP honor, NAV feel, SHIPS/SLAVE, MERCHANT venue)
cited with this file + run date. H flips ONLY from this sheet.

---

## SOURCES (canonical, read from the current tree @ bfbea4093)

- Commits: 8d5a0fb20 (war-gated honor, CharacterCombat.cs AwardPvpHonor —
  solo 40 / 32+4 split / victim −10 read straight from the diff),
  08d2d7a0d (LaborConfig — Models/Game/Configurations.cs:303+, AppConfiguration.cs:36
  `Labor` section; Unchained default 10/5min online+offline cap 5000),
  531a732fe (CSReturnMailPacket @ 0x0a2 STRONGLY_INFERRED + ownership guards;
  MailManager.ReturnMail requires Read status, once-only, attachments intact),
  1b8bf260e/0d6736282 (nav G-cost + grid).
- scorecard-explorations/mechanics/mail-domain.md + Addendum A1 (client Lua
  archaeology: return button handler, fee constants 50+30n / 100+80n).
- scorecard-explorations/generated/formula-corroboration-2026-08-25.md
  (P1/P2 contested rows the honor ruling resolves; L1–L4 labor table).
- scorecard-explorations/mechanics/ships-domain.md (§2 build flow incl. launch-
  ceremony seam, §3 driver-bind boarding, §9 slice-1 rowboat = slave 15,
  §10 passenger-seat unknown).
- ships-domain.md §11 + branch `fix/slavetest` @ d68efe74ab (W4-7 root cause:
  null Slave.Name → PacketStream ArgumentNull at PacketStream.cs:1575 ×3 per
  .testslave run; fix = delegate to SlaveManager.Create; E2E
  SlaveTestBugHuntE2eTests PASS — not yet on develop at writing time).
- AAEmu.Game/Scripts/Commands/: TestSlave.cs (template 73/model 1008,
  bypasses SlaveManager.Create → no InitialItems), SlaveCmd.cs + SubCommands/
  Slaves/SlaveSpawnSubCommand.cs (Create path applies InitialItems),
  GetPosition.cs (.position), TestZoneState.cs (.zonestate states 0–7),
  SetFaction.cs (.setfaction on target).
- JOSH-QAT-PACKET-M4-M5.x.md §0.2 (Mirage facility coordinates reused here).
- scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md
  (LANDED — offline heightmap audit of 25,118 main_world NPC spawns; W4-5
  stops T13–T20 are its worst offenders, §4 of that report).

Fork-local doc — never in an upstream PR (THE RULE).
