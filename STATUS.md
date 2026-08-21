# STATUS — ArcheAge Slums (fork joshhmann/AAEmu)

Updated: 2026-08-20 · by Kimi (Josh-directed no-cards pass #2: B4 playerbot_metadata store — M6 deferred gate #5 engineering COMPLETE)
Branch of record: develop @ efb98e4ae (local, ls-remote verified 2026-08-19 by fleet commit 50af336dc)

## Deferred validation gates (bot-backtrack program, 2026-08-12)

Prior human-test waivers are **authorized sequencing, not misconduct**.
Earned engineering evidence stands; these are explicitly deferred
validation. Bots prove function; Josh proves feel. H = actual player only —
scripted-actor/bot evidence is proxy/bot-functional, never H=2. Full table:
ROADMAP.md "Deferred validation gates".

1. **M1 human route** — Josh walks Solzreed (Open Decision #1).
2. **Original M2 human baseline** — two players, no GM repair; Josh-owned.
3. **M3a contract replay** — Phase 2 via M5.1 actions (Plant/Harvest/Craft/PackPickup/PutDown).
4. **M4 economic/navigation replay** — Phase 2 contract replay; normal movement/vehicle controls (direct Transform/ZoneId assignment FAILS the gate).
5. **M6 B4 restart scenario** — Phase 3; bot identity/inventory/position/
   schedule survive restart. **Engineering COMPLETE 2026-08-20** (store
   built + 2-checkpoint replay re-run with direct metadata assertions,
   PASS) — what remains is the full-M6-exit LABEL decision, not code.

## Milestone state

**M0 — Foundation: ✅ CLOSED (2026-08-03, Josh signoff)**
Workflow v4 (permanent one-way upstream gate), community guidelines,
kanban template set (Nei), gate.sh verified, scorecard + 3 exploration
reports, graphify graph (17.6k nodes), shared skill aaemu-fork-workflow
enabled on all 4 profiles, LIVING-WORLD.md canon, ROADMAP.md locked-shape
2026-08-03 (date is canonical).

**M1 — Quest and progression spine: ✅ CLOSED**
Items 1-8 delivered; automated exit test GREEN — census headline
**153/153 runnable / 0 FAIL / 33 SKIP over 186 quests**; full gate
1148/1148. PROD DEPLOYED @ 94f498fc (2026-08-04, M1 engine-health
release — BUG-007/008/009/010/011/012 live). Deploy incident (39GB
container json.log) resolved; rotation fix shipped (t_264e1984 ✅).
M1 closed on automated evidence (M1-M3 audit t_5b1f5494); human playtest
verdict open (Open Decision #1, pending Josh — C5) — **explicit deferred
gate: M1 human route (bot-backtrack program)**.

**M2 — Golden-path baseline: ✅ DONE — G1 census gate PASSED (2026-08-10)**
M2 redefined (2026-08-10 audit, in ROADMAP) into the M2a–M2d census sweep.
G1 GATE @ 7f5c179f7: 4,579 live = 4,573 PASS + 6 doc-SKIP, 0 unexplained;
full gate 1495/0/1 (t_971d275b / gate card t_4221f85c). Baseline legs
Rei-gated: automated (t_c6eb12ec / t_1998cfd8 PASS), restart (t_cca63225 /
t_c069bacd PASS + live probe t_92a41fe6 2/2), clean-host (t_52755daa /
t_819930ef PASS-WITH-FIXES). Human leg DEFERRED to M4 close (t_46bf9b84) —
**explicit deferred gate: original M2 human baseline, Josh-owned
(bot-backtrack program; bots may stand in for the AUTOMATED baseline only,
never H=2)**.

**M3a — Homestead shell: ✅ CLOSED on scripted-actor (proxy) evidence (2026-08-10, Rei gate t_449875bd ACCEPT; H reconciled 2026-08-12)**
Merged @ 4d0427b96; two-player exit via M3aExitScenarioTests (M5-stand-in:
2 scripted actors, adjacent 16m, ONE session — placement → construction →
crops → storage → furniture). Scorecard HOUSING-01 / FARM-01 C/W/A = 2;
**H = U (proxy/bot-functional only — scripted actors; H UNKNOWN until Josh
runs it; M3a contract replay = explicit deferred gate)**.

**M3b — Property persistence and recovery: ✅ CLOSED (2026-08-11, EXIT gate t_accb1c63 PASS)**
M3b-1..4 merged (5dc7c2fbd / 71b43e09f / 3913932bf / 5981246ea); EXIT E2E
f5b00c686 PASS 7m08s — N=3 crash cycles incl. kill -9 mid-save (INNODB_TRX-
observed) + container kill, 16 rows/boot, no loss/dup; autosave p95 1301ms
< 2000ms at 25 bots + 2 homesteads. PROPERTY-01 R = 2 (U→2 in f5b00c686).

**M4 — Trade, crafting and transport integrity: ✅ MERGED + DEPLOYED (2026-08-12; H reconciled 2026-08-12)**
Pinned audited SHA **95bb1c78e** (merge: M4 EXIT integrated playable release,
t_97e59ffc, **Rei gate PASS** t_abe87eaf ACCEPT) — crafting integrity
(bag-scope material check + level-10 pack gate) + trade packs + vehicle
lifecycle + integrated session evidence. **PROD DEPLOYED** to CT 133 by Mai
(t_442f3016): image `aaemu-game:presence-demo` @ 6d5a07cf49a5 built from
pinned 95bb1c78e; rollback tag `presence-demo-rollback-pre-m4` (3ddcf7a4bdbc);
prod startup test PASS 37 min (0 restarts, 0 FATAL, 3/3 bots roaming, real
client accepted); manifest deploy/m4-manifest @ 03d3442bd (deliberately NOT
develop — fork develop carries M5-lane content). Gates: unit 1778/0/1;
M4ExitIntegratedSessionTests (4 scripted actors: harvest → craft pack →
slave cargo → 3-leg route → sell, 2× 124540 mails); restart E2E kill -9 PASS
(2m12s/3m09s/7m03s); CRAFT-01 / PACK-01 / SLAVE-01 C/W/A/R = 2, **H = U
(proxy/bot-functional only — scripted actors; H UNKNOWN until Josh runs it;
M4 economic/navigation replay = explicit deferred gate)**. Human playtest of
the integrated release remains the deployment-lane follow-up pending Josh GO.

**M5 — Gameplay Actor Contract: ✅ COMPLETE (2026-08-17 sync 2026-08-20)**
A1 (marshal bot steps onto the game loop — the M6-exit-blocking retroactive
fix) + B1 core action surface (Interact · Loot · UseItem · Mount/Dismount ·
AcceptQuest · TurnInQuest, each through real engine paths) merged to fork
develop @ 761d1e81a (Rei gates t_d06d8dd9 / t_ebfc9b35; merged-tree re-verify
1850/0/1 via Phase 3 t_9340e85d). **M5.1 economic extension — ALL MERGED:**
Plant (t_b1d7c430) · PackPickup/PutDown (t_64ecf525) · Buy/Sell (t_8741b03d) ·
control-plane API (t_7b6d7a4b) · MCP sidecar (t_446228b5) · first consumer
(t_52b2b084) · salvage wave Deposit/Withdraw (t_78ce17a2), Harvest
(t_234da01a), BoardVehicle (t_15343fdd), Craft rig+impl (t_6b5ac43e,
t_cffb71ad) — all done per kanban. Phase-2 prerequisites LoadPackOntoVehicle
(t_a7756a00) + DriveVehicle (t_eaf1754d) merged @ 6c2429ae0 + 6edbf0cbb.
Housing.Build = M5.2 merged @ 3396d9ef1 (Rei gate t_ebf36737 ACCEPT).
**M5.3 core surface (Observe/Move/Stop/Target/Cast): MERGED 2026-08-17 @
6b4ffe1d2** — canonical verification + exit scenario (t_c73d6293), Move rework
(8e9c0713a), SetTarget broadcast + ExecutionBoundary rework (t_09e1c671, Rei
gate t_5fa9bd73 ACCEPT), full gate 2102/0/1 on merged tree. hytest GM kit
(level/labor/gold/portals) merged @ 782ac3b3c (t_e1cf82c9, Josh ruling
2026-08-17) + .teleport mirage (t_42e24eca) — human-test fast-forward lane
LIVE. BACKTRACK Phase 1 (t_61a0eebb + full-route follow-up t_15787275) and
Phase 2 (t_b4f455b0) both DONE.
**H = UNKNOWN** — proxy/bot-functional evidence only; the five deferred human
gates below remain Josh-owned.

**M7 — Adventurer and party bots: 🔶 GATING SPIKE DONE (2026-08-20) + heal/retreat landed**
One adventurer cleared quest 250 (Solzreed fox cull) end-to-end through the
M5 contract — accept at the real board → travel → hostile select (CanAttack)
→ burst Cast rotation → 3/3 REAL kills → 3 corpse loots → quest complete.
Rig 4/4 green + E2E PASS 1/1 2m15s (37 trace records; evidence
m7-adventurer-spike-report.json). Gate 2125/0/1. Spike found BUG-016
(18131-class melee skills never hit their primary target — FIXED same day,
census 415/13, 18131-led combo-chain rotation is the live regression) +
leash-reset/mana realities now recorded in ROADMAP M7. **Adventurer v1
sustain (heal/retreat) landed 2026-08-20**: the hunt loop checks vitals
before engaging — below threshold (0.35) the bot retreats along the
threat→bot vector, recovers (configured heal item through the real UseItem
path when bagged; out-of-combat regen fallback), and re-engages at 0.8 —
bounded rounds fail CLOSED with Starvation. Rig E-M7-3/4/5 green; live
exercise awaits level-appropriate content (foxes can't hurt the level-50
spike bot — recorded). Potion data note: no low-level direct-heal potion in
canonical compact.sqlite3 (retail heal pots are buff-tick shaped) —
HealItemTemplateId defaults to 0 until the right template is verified.
Spike shortcuts on the record: level-50 provisioning, straight-line Move (no
pathfinding), death/resurrection (**RESOLVED 2026-08-20** —
scheduler death watch + CharacterResurrection, see M6 exit blockers below).
**Distance maintenance landed 2026-08-20**: the hunt loop keeps a standoff
band [StandoffMin, EngageRange] before the cast burst — too far closes in
(melee default: straight onto the unit, the proven live behavior; ranged:
to the band edge, never face-planting the target), too close (ranged
StandoffMin > 0) backs off along the threat→bot vector. Rig E-M7-6/7 green
(band-edge stop distances asserted through a position-recording runtime);
melee defaults byte-identical in behavior (EngageRange 3 = the old
hardcode). Rig gotcha recorded: Character.MaxHp computes from Level via
FormulaManager — rig tests that set Level must also refill Hp or the
sustain loop fires on every run.
**Equip contract action landed 2026-08-20** (M7 equip-upgrades
prerequisite): `IGameplayActor.Equip(itemTemplateId)` moves a bagged item
into equipment through the real CSSwapItemsPacket Inventory→Equipment path
(Inventory.SplitOrMoveItem, SwapItems task) — the engine's
EquipmentContainer.CanAccept validates slot compatibility before anything
moves, and the slot pick uses the engine's own GetAllowedGearSlots table
(first EMPTY allowed slot, else first allowed = client equip-over-occupied
swap). Full idempotency discipline (same-key pre-flight refusal + fresh-key
engine backstop). Rig 7/7 (GameplayActorEquipTests). Engine gap on record:
no level/requirement gate on the equip path (CanAccept checks slot only).
Rig gotcha recorded: SeedEquipItemTemplate must run AFTER CreateActor
(ItemManager is DI-only, seeded by the rig's Seed()).
**Equip upgrades landed 2026-08-20**: the spike hunt loop evaluates bagged
equippables after each corpse loot and equips upgrades through the Equip
contract — the upgrade rule mirrors the contract's own slot pick (first
EMPTY allowed slot else first allowed; equip when the slot is empty or the
candidate's template Level beats the occupant's), and level discipline
lives in the scenario (LevelRequirement ≤ bot level; the engine has no
equip level gate). Rig E-M7-8 green (two equips through the contract, the
equal-Level third sword stays bagged); live no-op honesty: fox loot is
flavor, so the stage records nothing on the live stack unless real gear
drops.
**Live failure found + hardened 2026-08-20 (E2E rebuild run):** a fox
pinned at full 217 HP across 100+ successful casts (leash-stuck class —
damage never lands) starved the hunt at 2/3 in 150 attempts. The hunt loop
now carries a NO-PROGRESS SKIP: a target that takes zero net damage across
NoProgressSkipRounds (3) executed-cast rounds is excluded from reselection
(exclusion only, never a kill credit; HUNT-SKIP stage) and the hunt moves
on. Rig E-M7-9 (one unkillable fox of four — skip fires, cull completes
with the healthy three). Open question on record: WHY a freshly spawned
fox can enter the pinned-HP state at all (cold-boot correlation: both
rebuild-run E2E failures showed it, warm runs never did) — worth a
dedicated look at Npc leash/return-home healing.
**Return-to-NPC leg landed 2026-08-20**: the spike is now the M7-worded
short quest chain — after the 250 cull completes, the bot travels to the
quest-330 acceptor (golden route §1a step 3: Npc 3597, no objectives,
report Npc 3511), accepts through the real AddQuest gate, travels to the
report NPC, and turns in through the real packet path, draining the step
machine to completion (M1M2 replay shape). Rig E-M7-10 green (both quests
completed-and-dropped; contract vocabulary gains the second accept + the
turn-in). Rigs default the leg off (ReturnQuestId 0 keeps the one-quest
shape); live defaults run the chain.
**Adventurer v1 feature list COMPLETE 2026-08-20** — targeting, skill
priority, distance maintenance, heal/retreat, loot, equip upgrades,
return to quest NPC, death recovery all landed. Party v1 open.
Scheduling unblocked per the roadmap's spike gate. H UNKNOWN.
**Party v1 slice 1 landed 2026-08-21** — invite/join contract actions
(PartyInvite = 34, PartyAccept = 35) on IGameplayActor + the
PlayerBotControllerAdapter, through the real engine paths:
TeamManager.AskToJoin via the target-object overload (the exact
CSInviteToTeamPacket call, skipping the global name registry so headless
rigs resolve) and TeamManager.ReplyToJoinTeam (the exact
CSReplyToJoinTeamPacket call; invitation.TeamId 0 → engine CreateNewTeam,
else AddMember on the inviter's team). The engine's refusals on both
paths are SILENT voids, so the contract pre-flights (pending invitation /
already a member / no pending invitation → StateTransition, engine never
entered) and post-checks the observable outcomes (invitation record for
invite; Character.InParty + active-team membership for accept).
TeamManager.GetActiveInvitation went private→public (the invitation
record IS the observable outcome the actor must inspect). Rig upgrades:
the TeamManager seed now wires real ChatChannel instances and
incrementing team ids (bare mocks NRE'd CreateNewTeam's chat wiring and
collided every team on id 0), FriendMananger is seeded with an empty
friends table (Character.InParty's setter NRE'd headless), and
JoinActorWorld moves a second actor's character into the host session's
world (each CreateActor gets its OWN world; a party needs one). Rig
GameplayActorPartyTests 6/6 green. Follow-up slices: follow leader /
assist target (scenario surface), then the M7 party spike. H UNKNOWN.

**M6 exit blockers (as of 2026-08-20):** physics-warning regression
t_eecc5604 ✅ done · adopt-heal fix t_555ed207 ✅ done (merged; prod
re-provision verified by presence deploy chain) · **B4 playerbot_metadata
store ✅ done 2026-08-20** · **6.2 death/resurrection ✅ done 2026-08-20**
(CharacterResurrection core shared with the packet path + scheduler death
watch: dead bots stop getting work steps, poll, resurrect at the nearest
return portal after a 5s delay with the real 10%/debuff semantics,
server-side relocation through Character.SetPosition, then normal stepping
resumes — 5 rig tests green) · PlayerBotScheduler
scheduler-driven soak still open if M6 exit mandates it. **Exit-label note
(reconciled 2026-08-12, bot-backtrack):** soak verdict = "passed revised
approved budgets" — full M6 exit label NOT claimed; **B4 restart-persistence
scenario = explicit deferred gate**.

**M6 — Deterministic playerbot framework: 🔶 presence-demo hotfix chain DONE — parity + soak open**
Presence demo (3 citizen bots embody + roam AT Josh's spawn, zone 179)
live via the hotfix3 deploy overlay. Hotfix chain on
feat/bot-appearance-factory: null-safe ForceDismount + inactivity-sweep
skip (1c1fdd721), null-safe VisualOptions (53c2baee5), restart-idempotent
provisioning (fa9037c3c), terrain-aware roam waypoints + above-home
probe + flat-arrival Z clamp (2ff6f19f3/8e4b2b6b0/a32ee64d2), env-driven
patrol-home override AAEMU_PRESENCE_HOME_X/Y/Z (c22575d9d), world-ready
poll widened to 300s for cold boot (96e45252a), race-appropriate
unit_model_params provisioning so bot bodies render (d0e5feb9d),
BotAppearanceFactory — randomized player-like looks + per-class starting
equipment (91b308d71, t_61814965). M6.6 player-parity requirements
landed in ROADMAP.md (74151e060). E2E harness committed (Scripts/e2e);
presence-demo compose overlay captured in-repo
(docker-compose.presence.yaml). GM bot commands deployed P0
(t_7b4f9423).

**M6.6 open items — RESOLVED 2026-08-10 (three-card verification sweep t_120bb6c9 / t_509ef8c2 / t_1ed9881f):**
- **Parity audit t_98415169: ✅ CLOSED** — PARITY_AUDIT.md delivered 08-08; CRITICAL (factory-in-lineage) + MODERATE (skills/actabilities/bag) gaps closed by fix/parity-seeding @ 45cd3f3a9 (t_747a1c44): live-verified 34 actabilities/bot + skills row + bag byte-identical to human Asssaa (t_120bb6c9); LOW residual gaps tracked in PARITY_AUDIT.md (template/ambiance routes).
- **In-client visual acceptance: ✅ PASS (wire-level) — rendered screenshots pending Josh's client** — real X2 protocol client session received unit-state for all 3 Citizen bots (17× 0x69 distinct objIds/names + 164× 0x6C, all walking, t_509ef8c2); Josh sighting ACCEPTED 08-09. No Windows client in lab → rendered screenshot confirmation awaits Josh. ⚠️ Defect found: adopt-heal force-stamps demo blob → looks collapse to 1 on reboot (t_555ed207; fix pushed fix/adopt-heal-keeps-factory-look @ cdf6d4a62, awaiting Rei gate; prod needs re-provision after merge).
- **6h/10-bot soak: ⚠️ FAIL (numeric budget) — operational criteria all PASS** — full 6h window completed (attempt 3): 10/10 bots connected, 0 crash, 0 disconnect, RSS flat 3418-3453MB, tick p95 0.02ms, DB writes 262/500 — but physics slow-thread warnings 0.03/min vs 0 limit (11 transient single-frame WARNs, first = boot spike 459ms 21:25:18 PDT). Regression card t_eecc5604 filed: RCA or budget recalibration (precedent t_2006451f). Evidence: soak-report-20260810.md + gate-10-soak-20260810-102503.md (attached t_1ed9881f). Caveat: PlayerBotScheduler NOT enabled this run — scheduler-driven soak still required if M6 exit mandates it.

**G2-A4 save path (1,000-bot item): 🔶 implementation MERGED — acceptance measurement open**
SaveManager dirty-tracking merged to fork develop @ 5ed5d6493 (2026-08-10, t_8c18eb1c,
Rei gate t_53025996 ACCEPT): the periodic autosave now persists ONLY dirty characters
(Character.IsDirty/MarkDirty chokepoints; SaveManager.GetCharactersToSave;
DoSave(saveAllCharacters) force-all on shutdown + /save), closing the Kimi audit
finding "DoSave full-table sync save on every cycle" (t_0fda3cd3 → ROADMAP G2-A4).
Evidence: SaveManagerTests 10/10 (incl. 1,000-character simulated load), merged-tree
gate 1575/0/1, M2bE2e restart-persistence 5/5 (t_2ee39438 — disconnect save path
untouched). Remaining for A4: autosave p95 < 2s at 250 characters + zero _isSaving
skips at the milestone gate.

## E2E gates (GateSoakRunner, real Login+Game+MySQL, canonical data — evidence /root/aaemu-e2e/logs/):
- **10-bot correctness: PASS** (2026-08-09) — tick invoke p95 0.014ms /
  max 0.20ms (limits 100/250), ActiveRegionTick worst 18ms / 0 overruns,
  DB writes 276.53 (limit 500), 0 physics/tick-overrun warnings.
- **25-bot stability: PASS** (2026-08-09) — H2 gate 1.00, tick invoke p95
  0.018ms / max 3.02ms, ActiveRegionTick worst 45ms / 0 overruns, DB
  writes 262.66 (limit 500), 0 warnings.
- **6h/10-bot soak: ⚠️ FAIL (physics budget) — operational PASS** (2026-08-10,
  t_1ed9881f) — 10/10 connected 6h, 0 crash/disconnect, RSS flat, tick p95
  0.02ms; 11 transient physics WARNs (0.03/min vs 0) → t_eecc5604.

## Per-lane

| Lane | Sister | Current work | Status |
|------|--------|--------------|--------|
| Builds | Tai | M4 prod image 6d5a07cf49a5 live (t_442f3016); M5.1 salvage wave next (Deposit/Withdraw t_78ce17a2 → Harvest → BoardVehicle → Craft split); M5.2 Housing.Build t_94761d55 running | 🔶 running |
| Verifies | Rei | M4 gate PASS t_abe87eaf; M5 gates t_d06d8dd9 / t_ebfc9b35 done | ✅ done |
| Dispatches | Mai | M4 deploy to CT 133 + prod startup verification (t_442f3016) | ✅ done |
| Tracks | Nei | 08-13 (t_c9f0d7f6): M5.1 recovery sync — salvage order + Phase-2 prereqs + Housing.Build scope mirrored across ROADMAP/STATUS/SCORECARD/progression-board/wiki; branch of record 983b35736 (ls-remote verified) | ✅ this card |

## Open tasks (kanban, AAEmu lane)

**2026-08-20 cleanup sync (Kimi, Josh-directed):** every AAEmu card listed
here on 08-13 is now `done` in kanban — including the M5.1 salvage wave
(t_78ce17a2 / t_234da01a / t_15343fdd / t_6b5ac43e / t_cffb71ad), M6
regression t_eecc5604, adopt-heal t_555ed207, harness extension t_f198bb0e,
verifier stub-registry t_913c1d4a, authority envelope t_5999b370 (ACTIVATED,
closed t_b1002aad), and both backtrack phases (t_61a0eebb / t_15787275 /
t_b4f455b0). Human test packet t_2b654349 delivered. No open AAEmu-lane
engineering cards remain.

**New 2026-08-20 — production defect found + fixed (this pass):** a stray
half-closed LAN client wedged the stream-port (:1250) receive loop into a
zero-progress spin (~20k ERROR lines/sec, 174% CPU) because PacketStream
over-reads log-and-return-0 instead of throwing, making packetLen == 0 on a
1-byte remnant. Fixed on branch fix/protocol-spin-guard: truncation guard +
malformed-length close in Stream/Game/Game-side-Login protocol handlers +
5 regression tests. Prod mitigation: game container restarted 2026-08-20
(spin cleared, boot clean, 0 FATAL).

**Known deployment gap:** prod CT 133 image 32978f3613e3 = develop @ ~81676c0d6
(08-17 teleport-mirage). Missing from prod: M5.3 rework (6b4ffe1d2), hytest GM
kit (782ac3b3c), spin-guard fix. Rebuild + redeploy recommended before QAT.

**Remaining Josh-owned (deferred gates, bots cannot substitute):**
M1 Solzreed human route · original M2 two-player baseline · M3a contract
replay · M4 economic/navigation replay. hytest GM
kit + .teleport mirage + GM access (t_01a893c7, deployed t_d8658d50) are the
fast-forward lane for these. (M6 B4 restart scenario engineering completed
2026-08-20 — the remaining piece there is the M6 exit-label decision; the
B4 line item is now FULLY closed: metadata store + audit-trace flush.)

**New 2026-08-20 — B4 playerbot_metadata store (this pass):** the M6.0
metadata list (personality/schedule/profession/home/behavior/planner state)
now has a table and a store — `PlayerBotMetadataStore` (self-healing schema,
write-through REPLACE on mutation for hard-kill safety + dirty flush in the
SaveManager transaction), presence demo resolves home explicit-env →
persisted → template and records home + roam-loop schedule per bot. B4
restart replay extended to assert metadata directly: PASS 1/1 (4m39s,
evidence gate-m6-reconcile-b4-20260820-162058.md). Full unit gate 2121/0/1
(+15 store tests). Branch feat/b4-playerbot-metadata. NOT yet deployed to
prod — presence-demo home resolution gains a persisted fallback, so prod
deploy is a separate Josh decision.

## Legacy upstream item (predates one-way policy)

- #1494 — glibc Dockerfile fix (BUG-001) — awaiting maintainer (Greptile 5/5)
- No new upstream branches or PRs are permitted; upstream is intake-only.

## Last scorecard update

- 2026-08-13 — **canonical sync (t_c9f0d7f6)**: M5.1 recovery plan recorded —
  Kimi memo + Codex reconciliation (salvage order Deposit/Withdraw → Harvest →
  BoardVehicle → Craft split with card ids; work preserved, no
  re-implementation); LoadPackOntoVehicle (t_a7756a00) + DriveVehicle
  (t_eaf1754d) recorded as genuine Phase-2 prerequisites; Housing.Build =
  M5.2 contract card t_94761d55 in Josh-approved Phase-2 scope (impl open);
  Phase 1 t_61a0eebb stays open (min-slice evidence only; follow-up
  t_15787275); control-plane API / MCP sidecar / first consumer marked DONE
  (were queued); branch of record → 983b35736 (ls-remote verified); H stays
  UNKNOWN everywhere.
- 2026-08-12 — **bot-backtrack Phase 0.2 reconciliation (t_4ec066d3)**: M3a/M4
  H grades corrected — scripted-actor evidence is proxy/bot-functional, H =
  UNKNOWN until Josh runs it; SCORECARD H dimension = actual player only
  (never H=2); deferred gates recorded (M1 human route, original M2 human
  baseline, M3a contract replay, M4 economic/navigation replay, M6 B4 restart
  scenario); waivers visible in ROADMAP/SCORECARD/STATUS — branch
  docs/phase0-2-reconcile, Rei gate t_ee64e86b.
- 2026-08-12 — tracking refresh (t_773f9651): STATUS.md M4 row (merged +
  deployed @ pinned 95bb1c78e, Rei PASS, prod 6d5a07cf49a5, t_442f3016) +
  M5 A1/B1 row (develop @ 761d1e81a, ls-remote verified) + M6 exit blockers;
  branch-of-record → 761d1e81a; hermes-ops SLO window relabeled sidecar/shadow
  baseline (f31f829, decision log 8a0fb09).
- 2026-08-11 — this commit: M1-M3 audit C4 tracking refresh (t_b3980118,
  audit t_5b1f5494 PASS WITH NOTES) — STATUS.md M2/M3a/M3b rows +
  branch-of-record 4ded92c61; ROADMAP.md M3a/M3b closeout lines + M1
  status reconcile (closed on automated evidence, human playtest verdict
  open — C5); progression-board.md M3a/M3b rows.
- 2026-08-10 — this commit: post-merge tracking for SaveManager dirty-tracking
  (merged 5ed5d6493, t_8c18eb1c / Rei gate t_53025996 ACCEPT) — SCORECARD.md fork-fix
  entry + PROG-01 save-path pointer, ISSUES.md AUDIT-001 closure, STATUS.md G2-A4
  note, ROADMAP.md A4 implementation annotation.
- 2026-08-10 — this commit: STATUS.md M6.6 closeout — parity audit
  t_98415169 CLOSED (seeding gaps live-verified 45cd3f3a9), in-client
  wire-level PASS (t_509ef8c2) with appearance defect t_555ed207 pending
  Rei gate, 6h/10-bot soak operational PASS but harness FAIL on physics
  budget (t_1ed9881f → regression t_eecc5604).
- 2026-08-09 — this commit: progression-board.md refresh (M1 CLOSED,
  M2b-E2E DONE, M2c kill-acceptor + ZoneKill landed, M6 hotfix chain done
  + 6h soak running) and STATUS.md drift fix (parity audit t_98415169
  done, in-client sighting ACCEPTED, soak running t_1ed9881f).
- 2026-08-09 — earlier: STATUS.md M6 presence-demo refresh (M1 closed,
  hotfix chain + e2e gates 10-bot/25-bot PASS, M6.6 open items); e2e
  harness + presence overlay committed (06e6fcb4a, 615c3719c).
- 2026-08-04 — M1-5c closeout (t_cb64d872, 6e367585 on feat/quest-scenario-harness):
  SCORECARD.md quests-row runnability note 153/153 + M1-5 entry.

## Rules

- STATUS.md is fork-local — never in an upstream PR
- One screen max; Nei updates it on every completed task (the "what changed"
  one-liner is the input contract — see `.kanban-templates/tracking.md`)
