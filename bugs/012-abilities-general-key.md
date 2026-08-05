# BUG-012 — CharacterAbilities KeyNotFoundException 'General' on quest exp rewards (Ability1 == General)

- **Status**: FIXED (branch `fix/char-abilities-general`, 2026-08-04)
- **Severity**: Medium-High (crash on quest reward for any character whose
  ability1/2/3 is an unseeded value — reachable from the client create packet
  with no server-side validation)
- **Component**: `AAEmu.Game` — `Models/Game/Char/CharacterAbilities.cs`
  (`AddActiveExp`, `AddExp`)
- **Discovered via**: M1-5b quest scenario harness census REWARD:Fail
  250/6578/6600/6615 (scorecard-explorations/runnability-triage.md RC-6 /
  secondary finding 2)

## Symptom

`CharacterAbilities` ctor seeds keys `Fight(1)`..`Love(10)` only
(CharacterAbilities.cs:17-22). `AbilityType.General == 0` (Ability.cs:5) is
never seeded. `AddActiveExp` indexed `Abilities[Owner.Ability1]` whenever
`Ability1 != None` (CharacterAbilities.cs:54-59) → `KeyNotFoundException:
The given key 'General' was not present in the dictionary.` when
`Ability1 == General`.

Reachability: `character.ability1` column has no default (SQL/aaemu_game.sql:127);
`CharacterManager.Create` takes ability1 from the client packet
(CharacterManager.cs:501, CSCreateCharacterPacket.cs:31) with no server-side
validation — a client sending 0 (General) or any unseeded value crashes quest
exp rewards: `QuestActSupplyExp` → `Character.AddExp` → `AddActiveExp`.

## Impact

Census evidence: REWARD:Fail on quests **250**, **6578**, **6600**, **6615**
(stack: CharacterAbilities.cs:55 via QuestActSupplyExp.cs:20 →
Character.cs:1455). The harness's `BuildQuest` never set Ability1..3, so the
default `General(0)` hit the unseeded-key path on every quest exp reward.
Same flaw class in `AddExp(type, exp)` (CharacterAbilities.cs:46-47) — an
`AddExp(AbilityType.General, …)` call threw the same exception.

## Root cause

Dictionary seeded for 1..10 only; the `None` sentinel guard (11) does not
cover `General` (0) or any other byte value the client can place in the
ability1/2/3 fields.

## Fix

Guard both exp paths with `Abilities.TryGetValue` — skip unseeded abilities
instead of throwing, matching the existing defensive pattern in
`GetAbilityLevel` (CharacterAbilities.cs:155). Character-level exp is
unaffected: `Character.AddExp` grants `Experience`/`Level` *before* calling
`AddActiveExp` (Character.cs:1452-1456), so skipping ability exp keeps
character exp intact. Seeding a `General(0)` entry was deliberately NOT
chosen: it would silently accumulate exp on a non-ability and persist a
bogus `id=0` row on `Save()`.

## Verification

- 6 new `CharacterAbilitiesTests` (AAEmu.UnitTests/Game/Models/Game/Char/
  CharacterAbilitiesTests.cs): Ability1/2/3 == General no-throw (fail-before:
  KeyNotFoundException at CharacterAbilities.cs:55/57/59), all-slots-None
  no-throw, seeded-ability exp grant control, max-level-exp cap control.
- Gate: Release build 0 errors, compiler-check clean, **1127/1127** tests
  passed (baseline 1121 + 6 new).
- Harness census: REWARD:Fail 250/6578/6600/6615 unblocked at the engine
  level (crash removed); verdict flip tracked by the harness Ability1 rig
  card t_2d482bc3 + Rei gate t_59a623c4.
