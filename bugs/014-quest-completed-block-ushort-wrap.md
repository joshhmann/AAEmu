# BUG-014 — quest completed-block id wraps for quest ids >= 4,194,304

**Status:** OPEN (found by WI-11b census sweep t_8ec705f0, 2026-08-10)
**Severity:** HIGH for the 8,000,000-series quests (live: 8000004 할로윈 축제 준비)
**Affected:** any quest whose id >= 65536 × 64 = 4,194,304 (the 8,000,000-series
event/anniversary quests; B2 title quests 8000001-8000003 were dropped by the
WI-11a ruling, so 8000004 is the only live carrier)

## Symptom

A player who completes a high-id daily quest can never re-accept it after
`ResetDailyQuests` — the daily reset silently never clears the completed bit,
and `AddQuest` refuses with `QuestDailyLimit` forever.

## Evidence (census-driven, harness-independent)

WI-11b drives 8000004 through the engine's own reset path
(`ResetDailyQuests(true)` + `AddQuest` on the same character). Probe output:

```
HasQuestCompleted(8000004): True
GetTemplate(8000004) null: False | DetailId: Daily | Repeatable: False
questBlockId math: 8000004/64 = 125000 (ushort 59464) | 125000*64+4 = 8000004
after ResetDailyQuests -> HasQuestCompleted(8000004): True
re-accept via engine AddQuest: False
```

## Root cause — ushort block-id arithmetic (file:line)

`CharacterQuests` keys completed-quest blocks by `ushort`:

- `CharacterQuests.cs:25` — `private Dictionary<ushort, CompletedQuest> CompletedQuests`
- `CharacterQuests.cs:32-36` — `HasQuestCompleted`: `var questBlockId = (ushort)(questId / 64);` — for 8000004: `(ushort)(8000004/64)` = `(ushort)125000` = **59464** (125000 is 17 bits; ushort keeps the low 16). The wrap is SELF-CONSISTENT here, so the bit stores and reads correctly at block 59464.
- `CharacterQuests.cs:385-397` — `SetCompletedQuestFlag`: same `(ushort)(questId / 64)` wrap → writes block 59464, bit 4.
- `CharacterQuests.cs:463-486` — `ResetQuests` (the engine body of `ResetDailyQuests`): iterates the stored (wrapped) block keys and RECOMPUTES the quest id as `(uint)(completeBlockId * 64) + (uint)blockIndex` = 59464×64+4 = **3,805,700** — not 8000004. `QuestManager.Instance.GetTemplate(3,805,700)` → null → `continue`. The completed bit for 8000004 can never be cleared by any daily reset.

Any quest id >= 4,194,304 wraps the same way. The recomputation in `ResetQuests`
is inconsistent with the wrapped storage — the bug is only visible on ids that
overflow ushort (normal quest ids are well below 4M, which is why six years of
census sweeps never caught it).

## Impact

- 8000004 (할로윈 축제 준비, halloween festival prep, detail Daily 7): after
  the first completion, `ResetDailyQuests` never clears it → the daily
  re-accept is refused with `QuestDailyLimit` → the quest is one-time-per-
  character forever. REAL gameplay defect for the event quest.
- Dropped 8000001-8000003 would have shared the defect; they are gone (WI-11a).
- Any future quest with id >= 4,194,304 inherits it.

## Fix contract (for the fix card)

- Change the completed-block key from `ushort` to `uint` (or otherwise make
  the block-id math consistent) in `CharacterQuests`:
  `CompletedQuests` dict, `HasQuestCompleted`, `SetCompletedQuestFlag`,
  `IsQuestComplete`, `ResetQuests`; check the MySQL save/load path
  (`complete_block_id` column, `CompletedQuests` save loop) and the packet
  bodies that carry block ids (`SCQuestContextResetPacket`).
- Regression pin: a rig test completing a quest id in the 8,000,000 range
  (e.g. 8000004) then running `ResetDailyQuests` + engine `AddQuest` and
  asserting the re-accept succeeds. The WI-11b probe
  (`AAEmu.UnitTests/Game/Quests/Scenario/Wi11bProbeTests.cs` — TEMP, delete
  after the fix lands) reproduces it deterministically.
- Census side: 8000004's RESET probe stays FAIL in the WI-11b census until
  this lands (it is the bug detector, not a harness gap — do not mark it
  RESET-ineligible).

## Found by

WI-11b band-0/null sweep (t_8ec705f0), quest 8000004 — the first engine defect
caught by the WI-10 RESET fidelity stage on a high-id quest.
