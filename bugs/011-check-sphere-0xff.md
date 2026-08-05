# BUG-011 — QuestActCheckSphere can never pass + sphere entry crashes (Objectives[0xFF])

- **Status**: FIXED (branch `fix/quest-check-sphere`, 2026-08-04)
- **Severity**: Medium-High (1 live quest — 1033 기억과 쇠 골렘 — progress step can never pass
  via RunComponents; sphere entry throws IndexOutOfRangeException server-side)
- **Component**: Quest engine — `QuestActCheckSphere` act
- **Discovered via**: M1-5b runnability triage (`runnability-triage.md` §Secondary #1)

## Symptom

`QuestActCheckSphere` does not set `CountsAsAnObjective`, so the loader assigns
`ThisComponentObjectiveIndex = 0xFF` (QuestManager.cs:220). Consequences:

1. `QuestAct.RunAct` (QuestAct.cs:49) reads objective count only when the index is
   `< Objectives.Length` — `0xFF (255) < 5` is false, so the count is always `0`.
2. `RunAct` returned `currentObjectiveCount > 0` → **always false**. Any Progress step
   containing a CheckSphere can never pass via `RunComponents`.
3. `OnEnterSphere` called `SetObjective(questAct, 1)` (QuestActTemplate.cs:126) which
   writes `quest.Objectives[0xFF]` → **IndexOutOfRangeException** in-game on sphere
   entry (Objectives has MaxObjectiveCount = 5 entries). Same for `OnExitSphere`.

Live data check on prod compact.sqlite3 (r208088): **exactly 1 quest_context references
`QuestActCheckSphere`** — quest **1033** (기억과 쇠 골렘), Progress component **5065**
(kind 4), `quest_act_check_spheres` id 45 → sphere 945
('Q1033 _되찾은 기억_크레비츠가숨어있는곳', enter_or_leave 't', milestone 5).
The other 10 `quest_acts` rows with `act_detail_type='QuestActCheckSphere'` are orphans
(their `quest_component_id` has no `quest_components` row — the loader never builds them).

## Root cause

`QuestActCheckSphere` is a "check" act (like `QuestActCheckGuard`) but was written as if
it had an objective counter: the loader keeps `ThisComponentObjectiveIndex = 0xFF` for
non-objective acts, and both the counter read (`RunAct`) and the counter write
(`OnEnterSphere`/`OnExitSphere`) are invalid at that index.

## Fix (smallest change, mirrors QuestActCheckGuard)

`QuestActCheckSphere.RunAct` now evaluates the owner's **live position** against the
component's quest spheres:

1. `quest.Owner is not Character` → false (defensive; same cast pattern as CheckGuard).
2. `character.ParentWorld?.SphereQuestManager.GetQuestSpheres(ParentComponent.Id)` —
   the sphere lookup is keyed by component id (the same table `AddSphereQuestTriggers`
   registers triggers from, SphereQuestManager.cs:89-110). No spheres → false.
3. `character.Transform?.World?.Position ?? Vector3.Zero` inside any sphere
   (`SphereQuest.Contains`) → true; outside → false.

`OnEnterSphere`/`OnExitSphere` no longer write an objective (would index past the
array). They only call `questAct.RequestEvaluation()` (routing to
`QuestManager.EnqueueEvaluation` via the RequestEvaluationFlag path), so the quest
re-runs `RunComponents` while the player is inside/outside the sphere. Early-return
guards kept and extended with the sphere's `ComponentId` (in addition to `QuestId` +
`ActId`) so events for a different component of the same quest don't trigger this act.

No changes to `InitializeAction`/`FinalizeAction` (trigger registration unchanged) or
to `quest_act_check_spheres` loading.

## Files changed

- `AAEmu.Game/Models/Game/Quests/Acts/QuestActCheckSphere.cs` (RunAct live-position
  check; OnEnterSphere/OnExitSphere re-evaluation only)
- `AAEmu.UnitTests/Game/Models/Game/Quests/Acts/QuestActCheckSphereTests.cs` (new,
  8 tests — rig authored 507256ec, `[NotInParallel]` added for the shared statics)
- `ISSUES.md` (BUG-011 row), `SCORECARD.md` (fork-fix note), `bugs/011-check-sphere-0xff.md`

## Tests

New `QuestActCheckSphereTests` (MethodName_Scenario_ExpectedResult), real `Character` +
`WorldInstance` + seeded `SphereQuestManager` static sphere table (center
(100,200,300) r=5, component 5065):

- `RunAct_OwnerInsideSphere_ReturnsTrue` — **failed before fix** (always false at 0xFF), passes after
- `RunAct_OwnerOutsideSphere_ReturnsFalse` — passes before and after
- `RunAct_NoSphereDataForComponent_ReturnsFalse` / `RunAct_NullOwnerTransform_ReturnsFalse` /
  `RunAct_OwnerIsNotCharacter_ReturnsFalse` — defensive, pass before and after
- `OnEnterSphere_MatchingSphere_RequestsEvaluation_NoObjectiveWrite` / `OnExitSphere_...` —
  **failed before fix** (IndexOutOfRangeException on Objectives[0xFF]), now request
  evaluation once and never touch objectives
- `OnEnterSphere_WrongComponent_DoesNotRequestEvaluation` — **failed before fix**
  (crash), now filtered out

The class is `[NotInParallel]` (same convention as `QuestScenarioTests`): the rig seeds
shared statics (`SphereQuestManager._sphereQuests` + the `QuestManager` singleton), and
the original per-test seed/restore raced under TUnit's parallel execution (observed as
a flaky single failure in the class run; deterministic 8/8 once serialized).

## Verification

- Fail-before (old code): 4 failed / 4 passed — `RunAct_OwnerInsideSphere` assertion
  fail + 3 `IndexOutOfRangeException` at QuestActTemplate.SetObjective:121.
- Pass-after: `./scripts/gate.sh QuestActCheckSphereTests` → **8/8**, stable across 3
  repeat runs (race check).
- Full gate (`./scripts/gate.sh`): see commit message / gate log (build 0 errors,
  compiler-check clean).

Census note: quest 1033's runnability row (START:Fail) is a harness-gap verdict and
does **not** flip with this fix — that's tracked by the harness cards (guard-rig
t_fea18232). This fix removes the engine defect so 1033's Progress step *can* pass via
RunComponents once the harness models the sphere entry.

No upstream PR (lane gate). Commit identity: Tai <tai@asslorde.com>.
