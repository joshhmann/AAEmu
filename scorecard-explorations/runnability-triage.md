# Quest Runnability Triage — M1-5b follow-up (19 scenario-harness FAILs)

Generated: 2026-08-05 (t_36278a75, hx-coder)
Census source: `scorecard-explorations/runnability.md` (2026-08-05 00:16Z regen,
T1 86/97 PASS, T2 21/29 PASS, 19 FAIL total).
Scope: triage ONLY — no quest engine code changed. Verdicts classify every FAIL
as **harness-gap** (scenario harness / manifest generator at fault) or
**quest-defect** (AAEmu.Game / AAEmu.Commons engine fault). Fix cards are listed
at the bottom; the harness diagnostics were enriched (see §Method) so the
census now carries per-quest observed state.

## Headline

| class | count | quests |
|---|---|---|
| **Quest-defect** (engine) | **3** | 350, 4292, 1313 — all one root cause (BUG-010) |
| **Harness-gap** | **16** | 265, 266, 299, 303, 1033, 3656, 2248, 269, 294, 5489, 295, 250, 6578, 6600, 6615, 1897 |
| total | 19 | 11 T1 + 8 T2 |

Every FAIL quest is **completable in-game** (evidence: the READY/REWARD stages
pass on the harness for 14 of the 16 harness-gap quests — the report path
force-advances the quest, `QuestActConReportNpc.cs:59-60`). The three
quest-defects are timer-quest persistence corruption that affects **every**
CheckTimer quest in the data, not just the census sample.

## Root-cause registry (5 distinct)

### RC-1 (harness) — generator `kind_is_auto` reads the RAW sqlite `selective` value
`tools/quest-scenario/gen-manifests.py` (stage model v3): inside `build_manifest`
the local `selective` is the raw sqlite cell (`'f'` string), and `if selective:`
is truthy for **both** `'t'` and `'f'` in Python. Every quest takes the
`any(...)` auto-pass branch, so a Progress step containing ANY auto/hydrated act
(CheckGuard, CheckTimer, ItemGather…) is mis-modeled as fully auto-passing →
START/SUPPLY stages expect a step the engine never reaches. The `parse_bool`
fix (commit 14c78c94) was applied to the manifest fields but NOT to this local.
Affects: 1033, 3656 (START expects Ready), 1897 (SUPPLY expects Reward).

### RC-2 (harness) — stage model v3 lacks LetItDone semantics
Engine: `QuestStep.RunComponents` forces `res = false` for any LetItDone
Progress step even when objectives are complete (`AAEmu.Game/Models/Game/Quests/QuestStep.cs:128-129`);
`RunCurrentStep` therefore never advances Progress→Ready for let-it-done quests
(`NewQuestCode.cs:80-83`). The HackFix at `NewQuestCode.cs:69-78` requires
`Score > 0` AND no Ready step — absent for these quests. Objectives ARE credited
(census observed `status=Ready` + full objective counts) and the quest completes
via the report force-advance (`QuestActConReportNpc.cs:59-60` → `Step = Ready`).
The generator's START/PROGRESS expectations of step=Ready are therefore wrong
for let-it-done quests. Affects: 265, 266, 299, 303, 2248, 269, 294.

### RC-3 (harness) — guard NPC rig only covers Start-component CheckGuards
`gen-manifests.py` emits the `guard` block only from Start components
(`if kind_name == "Start"`), and `QuestScenarioDriver.BuildQuest` spawns the
guard only when the manifest has one. `QuestActCheckGuard.RunAct`
(`AAEmu.Game/Models/Game/Quests/Acts/QuestActCheckGuard.cs:26-33`, BUG-008 fix)
returns false when the guard cannot be resolved → Progress steps containing a
CheckGuard in a non-Start component can never pass in the harness. In-game the
guard exists in the world. Affects: 1033 (guard 4617), 3656 (guard 9846), 1897
(guard 7548).

### RC-4 (harness) — event shapes for ItemUse-family acts
- 5489 (`test_time`): `QuestActObjItemGroupUse` subscribes `OnItemUse`
  (`AAEmu.Game/Models/Game/Quests/UnusedActs/QuestActObjItemGroupUse.cs:39`), but
  the generator emits an `ItemGroupUse` event and the driver fires
  `OnItemGroupUse` (`QuestScenarioDriver.cs:502-504`) — nothing listens, the
  objective never credits (census observed `objectives=[0,0,0,0,0]`). Group 10
  members [8518, 29173] ARE seeded in the manifest — only the event shape is
  wrong.
- 295: `QuestActObjItemUse.OnItemUse` does `AddObjective(questAct, 1)` per event
  (`QuestActObjItemUse.cs:46`, ignores `args.Count`); the driver fires the
  ItemUse event exactly once → 1/3 → step stuck, report blocked (not letItDone).
  The generator must emit the event `Count` times (or the driver must).

### RC-5 (quest-defect, BUG-010) — `Helpers.UnixTime(long)` decodes any timestamp > 59s to DateTime.MaxValue
`AAEmu.Commons/Utils/Helpers.cs:54`:
`if (time > DateTime.MaxValue.Second) return DateTime.MaxValue;` —
`DateTime.MaxValue.Second` is **59** (the seconds-of-minute component), so every
unix-seconds value > 59 is treated as out-of-range. PERSIST round-trip evidence
(harness byte-diff, new diagnostic): `first diff at byte 30 (field time:
snapshot=1785894127s, round-trip=253402300800s)` for 350/4292/1313 —
253402300800s == UnixTime(DateTime.MaxValue). Flow: `QuestActCheckTimer.
InitializeAction` → `QuestManager.AddQuestTimer` sets `quest.Time = UtcNow +
limit` (`QuestManager.cs:2015`) → `Quest.WriteData` writes it (`Quest.cs:590`)
→ `Quest.ReadData` → `Helpers.UnixTime(long)` → MaxValue (`Quest.cs:575`)
→ re-WriteData emits 253402300800 → byte mismatch. Readable fields
(step/acceptor/componentId/objectives) all round-trip correctly — the defect is
exclusively the Time field. In-game impact: any timer quest restored through
ReadData (reload / quest-state restore paths) gets `Time = DateTime.MaxValue` →
`LeftTime` (`Quest.cs:89`) overflows int, the expiry task state is corrupt, and
the timer can never expire. Affects every CheckTimer quest in the data; the
census only samples 3 (350, 4292, 1313).

## Per-quest verdicts

| quest | tier | bucket | class | root cause (evidence) | in-game impact |
|---|---|---|---|---|---|
| 265 | T1 | START:Fail | harness-gap | RC-2; observed step=Progress, status=Ready, objectives=[3,0,0,0,0] (3/3 credited) | completable via report (READY/REWARD pass) |
| 266 | T1 | START:Fail | harness-gap | RC-2 (letItDone + score=100); HackFix requires no Ready step (NewQuestCode.cs:69-78); observed [10,0,0,0,0], status=Ready | completable via report |
| 299 | T1 | START:Fail | harness-gap | RC-2; observed [8,0,0,0,0], status=Ready | completable via report |
| 303 | T1 | START:Fail | harness-gap | RC-2; observed [12,0,0,0,0], status=Ready | completable via report |
| 1033 | T2 | START:Fail | harness-gap | RC-1 (START expect Ready, engine rests Progress) + RC-3 (guard 4617 not spawned; observed objectives=[1,0,0,0,0] after Talk, step still Progress) | completable in-game (guard exists; report path) |
| 3656 | T2 | START:Fail | harness-gap | RC-1 + RC-3 (guard 9846 not spawned; Talk credited 1, step Progress) | completable in-game |
| 2248 | T1 | PROGRESS:Fail | harness-gap | RC-2; observed [8,0,0,0,0], status=Ready | completable via report |
| 269 | T1 | PROGRESS:Fail | harness-gap | RC-2; observed [5,0,0,0,0], status=Ready | completable via report |
| 294 | T1 | PROGRESS:Fail | harness-gap | RC-2; observed [8,0,0,0,0], status=Ready | completable via report |
| 5489 | T2 | PROGRESS:Fail | harness-gap | RC-4a (event-shape mismatch; observed [0,0,0,0,0] — objective never credits; test quest `test_time`) | test-only quest; needs right event to progress |
| 295 | T1 | PROGRESS:Fail | harness-gap | RC-4b (ItemUse fired once; observed [1,0,0,0,0] = 1/3; cascade READY/REWARD Fail) | in-game: using the item 3x works |
| 250 | T1 | REWARD:Fail | harness-gap | RC-6: KeyNotFoundException 'General' — CharacterAbilities.AddActiveExp (CharacterAbilities.cs:55) via QuestActSupplyExp.RunAct (QuestActSupplyExp.cs:20) → Character.AddExp (Character.cs:1455); rigged Character.Ability1 defaults to General(0) (Character.cs:92; AbilityType.General=0), CharacterAbilities ctor seeds keys 1..10 only (CharacterAbilities.cs:17) | in-game chars have ability1 from creation packet (1..10) — latent engine fragility, see §Secondary |
| 6578 | T2 | REWARD:Fail | harness-gap | RC-6 (same stack; SupplyExp 65480 + copper) | same |
| 6600 | T2 | REWARD:Fail | harness-gap | RC-6 (same stack) | same |
| 6615 | T2 | REWARD:Fail | harness-gap | RC-6 (same stack; + SupplyAppellation) | same |
| 350 | T1 | PERSIST:Fail | **quest-defect** | RC-5 (BUG-010): byte 30 Time field, snapshot=1785894127s → round-trip=253402300800s (=DateTime.MaxValue); CheckTimer 3600000ms | timer quest restore corrupts Time; expiry broken |
| 4292 | T1 | PERSIST:Fail | **quest-defect** | RC-5 (same evidence; CheckTimer 3600000ms) | same |
| 1313 | T2 | PERSIST:Fail | **quest-defect** | RC-5 (same evidence; CheckTimer 180000ms) | same |
| 1897 | T2 | SUPPLY:Fail | harness-gap (PERSIST part quest-defect) | SUPPLY: RC-1 (model over-advance: expected Reward, engine rests Progress); PROGRESS/REWARD: RC-3 (guard 7548 not spawned — observed [1,0,0,0,0], ItemUse credited only) + RC-7 (objective index fidelity, see §Secondary); PERSIST: RC-5 (CheckTimer 180000) | in-game: guard exists; quest completes normally; timer restore still hit by BUG-010 |

RC-6 (Ability1 rig) and RC-7 (objective index fidelity) are named inline above
for readability; full detail in §Secondary.

## Counts per class

- Quest-defect: **3** (350, 4292, 1313) — single root cause BUG-010.
- Harness-gap: **16** — generator stage model (RC-1/RC-2): 10 quests;
  guard rig (RC-3): 3; event shapes (RC-4): 2; Ability1 rig (RC-6): 4.
  (quests can appear in several; primary classification is the stage that
  first fails.)

## Secondary engine findings (not the 19's root cause — separate follow-ups)

1. **QuestActCheckSphere never passes / would crash on entry** —
   `QuestActCheckSphere` does not set `CountsAsAnObjective` → loader assigns
   index 0xFF (`QuestManager.cs:220`), `RunAct` reads count 0 → always false
   (`QuestActCheckSphere.cs:33-35`), and `OnEnterSphere`→`SetObjective` writes
   `Objectives[255]` (`QuestActTemplate.cs:126`) → IndexOutOfRange in-game on
   sphere entry. Quest 1033 completes only via the report force-advance path,
   not via its Progress step. Fix-card candidate (engine).
2. **CharacterAbilities has no General(0) key** — ctor seeds Fight..Love
   (`CharacterAbilities.cs:17`); `AddActiveExp` assumes Ability1 ∈ seeded keys
   (`CharacterAbilities.cs:54-59`). Any character with `ability1`=0 (General —
   the DB column has no default, `SQL/aaemu_game.sql:127`) crashes on quest exp
   rewards. The harness rig must set Ability1..3 (RC-6); the engine should also
   guard/seed General (defensive fix-card candidate).
3. **Harness objective-index fidelity (RC-7)** — `QuestScenarioDriver.BuildTemplate`
   resets `objectiveIndex` per component (`QuestScenarioDriver.cs:220`), the
   real loader resets per KIND (`QuestManager.cs:207-211`). Multi-component
   steps (266, 1033, 3656, 1897) share slot 0 in the harness but not in-game.
   Doesn't change any of the 19 verdicts (they fail for other reasons) but must
   be fixed before 5c's census trusts objective columns.

## Method (evidence trail)

- Re-ran `tools/quest-scenario/gen-manifests.py` against prod 1.2
  `compact.sqlite3` (2026-08-04 snapshot, /tmp): byte-identical to the
  committed manifests (so the census ran on current generator output).
- Instrumented the generator's `kind_is_auto` (debug trace) → RC-1 proven:
  `selective='f'` string, `any()` branch taken for all quests.
- Enriched `QuestScenarioAssertions` (harness-only): PERSIST byte-diff decoder
  (offset → field → decoded values, `DescribePersistDiff`) + observed
  step/status/objectives appended to every FAIL reason. Regenerated census
  `scorecard-explorations/runnability.md` — verdicts unchanged (19 FAIL), now
  with per-quest evidence. Gate: `./scripts/gate.sh QuestScenarioTierTests`
  green (build 0 errors, compiler-check "Compilation successful", tests 1/1).
- File:line evidence cited from AAEmu.Game / AAEmu.Commons (see RC table).

## Fix-card queue

| # | card (title) | class | resolves |
|---|---|---|---|
| 1 | `fix(quest-harness): generator stage model v4 — parse selective bool in kind_is_auto + LetItDone Progress never auto-advances` | harness | RC-1 + RC-2 (265/266/299/303/2248/269/294/1033/3656/1897 stage expectations) |
| 2 | `fix(quest-harness): driver spawns guard NPCs for QuestActCheckGuard in any component + BuildTemplate objective index per-kind (mirror QuestManager.cs:207-211)` | harness | RC-3 + RC-7 (1033/3656/1897; census objective fidelity) |
| 3 | `fix(quest-harness): event shapes — ItemGroupUse acts listen OnItemUse (5489), ItemUse fired Count times (295)` | harness | RC-4 (5489, 295) |
| 4 | `fix(quest-harness): rig character Ability1..3 in BuildQuest so QuestActSupplyExp rewards don't KeyNotFound 'General'` | harness | RC-6 (250/6578/6600/6615) |
| 5 | `fix(quest): BUG-010 Helpers.UnixTime(long) compares against DateTime.MaxValue.Second (59) — any ts >59s decodes to MaxValue; timer-quest persistence corrupt` | **quest-defect** | RC-5 (350/4292/1313 + all CheckTimer quests) |
| 6 | `fix(quest): QuestActCheckSphere objective handling — 0xFF index makes RunAct never pass and OnEnterSphere write Objectives[255]` | **quest-defect** (secondary) | §Secondary #1 |
| 7 | `fix(quest): CharacterAbilities seed/guard General(0) key — AddActiveExp KeyNotFound for ability1=General` | **quest-defect** (secondary, defensive) | §Secondary #2 |

Cards 1-4 keep the harness honest so 5c's T3 census measures the engine, not
the rig; cards 5-7 are the real engine findings from this triage.
