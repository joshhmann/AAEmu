# BUG-010 — Helpers.UnixTime(long) clamps every timestamp > 59s to DateTime.MaxValue

- **Status**: FIXED (branch `fix/bug-010-unix-time`, 2026-08-04)
- **Severity**: High (all CheckTimer quests in the 1.2 data — restored quest state gets Time=DateTime.MaxValue)
- **Component**: `AAEmu.Commons` — `Helpers.UnixTime(long)` (AAEmu.Commons/Utils/Helpers.cs)
- **Discovered via**: M1-5b quest scenario harness PERSIST byte-diff (scorecard-explorations/runnability-triage.md RC-5)

## Symptom

`Helpers.UnixTime(long)` (Helpers.cs:54) guarded the upper bound with
`time > DateTime.MaxValue.Second` — and `DateTime.MaxValue.Second == 59`. Any unix
timestamp larger than 59 seconds decoded to `DateTime.MaxValue`.

PERSIST evidence (harness byte-diff, census 2026-08-05): quests **350 일손 부족**,
**4292 망아지 운반**, **1313 말동무** (the only CheckTimer quests in the census) —
`first diff at byte 30 (field time: snapshot=1785894127s, round-trip=253402300800s)`.
253402300800s is exactly `UnixTime(DateTime.MaxValue)` (double-rounded).

## Impact

Flow: `QuestActCheckTimer.InitializeAction` → `QuestManager.AddQuestTimer` sets
`quest.Time = UtcNow + limit` (QuestManager.cs:2015) → `Quest.WriteData`
(Quest.cs:590) → `Quest.ReadData` → `stream.ReadDateTime()` (PacketStream.cs:840)
→ `Helpers.UnixTime(long)` → `DateTime.MaxValue` (Quest.cs:575) → re-`WriteData`
emits 253402300800.

Every CheckTimer quest restored via ReadData gets `Time = DateTime.MaxValue`:
`LeftTime` (Quest.cs:89) int-overflows, expiry task state corrupt, timer never
expires. Affects **all** CheckTimer quests in the 1.2 data, not just the census
sample. The same decode path also serves `PacketStream.ReadDateTime()` / network
`DateTime` round-trips (PacketStream.cs:840/1315) — any future timestamp > 59s
corrupted on the wire.

## Root cause

The range check compared against a *component* of `DateTime.MaxValue` (the
`Second` property, 59) instead of the maximum *unix-seconds* value the type can
represent.

## Fix

Compare against the exact max unix-seconds value using integer ticks math:

```csharp
// (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond == 253402300799
// NOTE: (long)(DateTime.MaxValue - DateTime.UnixEpoch).TotalSeconds double-rounds UP to
// 253402300800, which AddSeconds cannot represent (throws) — ticks math is exact.
private static readonly long MaxUnixTimeSeconds =
    (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond;

if (time > MaxUnixTimeSeconds) return DateTime.MaxValue;
if (time < 0) return DateTime.MinValue;   // was `time < DateTime.MinValue.Second` (== 0) — same semantics
```

Lower bound rewritten as `time < 0` (numerically identical to the old
`time < DateTime.MinValue.Second`, which is also 0) for clarity. Values in
[0, 253402300799] now decode via `UnixEpoch.AddSeconds(time)`; 253402300800+
still clamps to `DateTime.MaxValue` (no throw).

## Verification

- 8 new `HelpersTests` (AAEmu.UnitTests/Commons/Utils/HelpersTests.cs): 2026
  timestamp round-trip (fail-before/pass-after), >59s decodes to a real date,
  max-representable second decodable + round-trips, beyond-max clamps without
  throwing, `UnixTime(DateTime.MaxValue)` round-trips, negative → MinValue,
  zero → UnixEpoch.
- Harness census regen: quests **350 / 4292 / 1313 flipped PERSIST:Fail →
  PERSIST:Pass**; zero other verdict changes across all 132 census quests.
- Gate: Release build 0 errors, compiler-check clean, **1129/1129** tests passed
  (baseline 1121 + 8 new).
