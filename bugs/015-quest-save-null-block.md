# BUG-015 — CharacterQuests.Save NREs on a null completed-block entry — disconnect save aborts, quest rows lost

**Status:** FIXED (branch `fix/quest-save-null-guard` @ 6bd442715, 2026-08-10)
**Severity:** HIGH (state-loss vector — the disconnect save aborts BEFORE the
active-quest REPLACE loop, so the checkpoint quest row is never persisted)
**Affected:** every character with concurrent quest completion during a
disconnect save (observed live on the E2E restart flow, runtime 711181bc0)

## Symptom

During a disconnect save, `CharacterQuests.Save` threw
`System.NullReferenceException` at `command.Parameters.AddWithValue("@id", quest.Id)`
— `quest` was null while iterating `CompletedQuests.Values`. The exception
aborted the save BEFORE the active-quest REPLACE loop, so the checkpoint quest
row was never persisted (test save-wait timed out; /root/aaemu-e2e/logs/game.log
12:21:08, runtime 711181bc0; trace: CharacterQuests.Save ← Character.Save:2782).

## Root cause — Dictionary race artifact (file:line)

`CompletedQuests` is a plain `Dictionary<uint, CompletedQuest>` mutated by
`SetCompletedQuestFlag` (check-then-act Add, :394-397) and `Load` (:518). Both
Add sites write NON-NULL values, so the null entry is a **mutation-during-
enumeration artifact**: a concurrent Add during `foreach (CompletedQuests.Values)`
(in Save, :588) can yield a null (the enumerator reads an empty slot of a
resized entries array). Same class exists in the ACTIVE-quest loop
(`ActiveQuests.Values`, :609) via AddQuest/DropQuest.

## Evidence (deterministic hermetic rig — t_90c0d0d1)

`CharacterQuestsSaveNullEntryRigTests` (AAEmu.UnitTests/Game/Quests/Scenario/)
injects a null entry into the private dictionaries via reflection and calls
`Save` with a never-opened MySqlConnection — the NRE fires at parameter binding,
BEFORE any DB call, so the rig is hermetic:

```
fail-before (pre-fix): 3/3 RED
  Save_NullCompletedBlockEntry_Skipped_SaveCompletes  -> NRE at CharacterQuests.cs:590
  Save_NullCompletedBlockEntry_RealBlockStillWritten  -> NRE (null first, insertion order)
  Save_NullActiveQuestEntry_Skipped_SaveCompletes     -> NRE at CharacterQuests.cs:611

pass-after (post-fix): 3/3 GREEN — nulls skipped with WARN, real blocks still
written, active-quest REPLACE still runs
```

Full gate: 1497/1498 (0 failed, 1 skip — census). E2E restart-persistence test
(`E2e_RestartPersistence_TwoCheckpoints_FullStateMatch`) PASS 3m03s on the
fixed runtime (E2E_REBUILD=1, runtime DLL md5 == branch build, 0 NRE in run).

## Fix (per-character locks + defensive snapshot; no quest semantics change)

- `SetCompletedQuestFlag` / `Load` / `AddQuest` / `DropQuest`: CompletedQuests
  and ActiveQuests mutations take the same lock Save's snapshot takes
  (`lock (CompletedQuests)` / `lock (ActiveQuests)`) — single-statement scopes,
  no nesting, no deadlock.
- `Save`: snapshot `CompletedQuests.Values.ToArray()` and
  `ActiveQuests.Values.ToArray()` under the per-character lock before the
  REPLACE loops; skip null entries with a `[QuestSave] skipping null ...` WARN
  instead of dereferencing them.

Note: the lock also SERIALIZES the t_ca4683e1 SetCompletedQuestFlag check-then-
act race (two threads → one wins the Add, loser blocks then re-reads) — both
fixes compose (TryAdd/GetOrAdd from t_ca4683e1 is compatible with the lock).
