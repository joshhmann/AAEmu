#nullable enable

using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// One replay run: which frozen wake was re-decided, how many times, and
/// whether every replay agreed with the frozen verdict.
/// </summary>
public sealed record JevReplayResult(
    JevBrain Brain,
    string WakeId,
    int Replays,
    bool Deterministic,
    string? MismatchDetail);

/// <summary>
/// The OFFLINE replay harness (test-side utility, never production): given a
/// frozen wake record (inputs + the brain decision the live wake reached),
/// re-runs the matching brain's pure <c>Decide</c> N times and asserts the
/// same verdict every time.
///
/// The harness performs no world reads, touches no actor, and owns no clock:
/// every number the decision can read arrives on the frozen inputs, so any
/// mismatch is a genuine determinism break (or a tampered record), never an
/// environment difference.
/// </summary>
public static class JevReplayHarness
{
    /// <summary>Default replay count: enough runs to catch flaky nondeterminism without slowing the suite.</summary>
    public const int DefaultReplays = 8;

    /// <summary>Re-runs <see cref="TravelBrain.Decide"/> over the frozen inputs and asserts every verdict equals the frozen one.</summary>
    public static JevReplayResult Replay(in JevTravelWake wake, int replays = DefaultReplays)
    {
        GuardReplays(replays);
        for (var i = 0; i < replays; i++)
        {
            var actual = TravelBrain.Decide(wake.Inputs);
            if (!actual.Equals(wake.Expected))
                return Mismatch(JevBrain.Travel, wake.WakeId, replays, i, wake.Expected.Describe(), actual.Describe());
        }
        return Pass(JevBrain.Travel, wake.WakeId, replays);
    }

    /// <summary>Re-runs <see cref="SurvivalBrain.Decide"/> over the frozen inputs and asserts every verdict equals the frozen one.</summary>
    public static JevReplayResult Replay(in JevSurvivalWake wake, int replays = DefaultReplays)
    {
        GuardReplays(replays);
        for (var i = 0; i < replays; i++)
        {
            var actual = SurvivalBrain.Decide(wake.Inputs);
            if (!actual.Equals(wake.Expected))
                return Mismatch(JevBrain.Survival, wake.WakeId, replays, i, wake.Expected.Describe(), actual.Describe());
        }
        return Pass(JevBrain.Survival, wake.WakeId, replays);
    }

    /// <summary>Re-runs <see cref="NeedsBrain.Decide"/> over the frozen inputs and asserts every verdict equals the frozen one.</summary>
    public static JevReplayResult Replay(in JevNeedsWake wake, int replays = DefaultReplays)
    {
        GuardReplays(replays);
        for (var i = 0; i < replays; i++)
        {
            var actual = NeedsBrain.Decide(wake.Inputs);
            if (!actual.Equals(wake.Expected))
                return Mismatch(JevBrain.Needs, wake.WakeId, replays, i, wake.Expected.Describe(), actual.Describe());
        }
        return Pass(JevBrain.Needs, wake.WakeId, replays);
    }

    /// <summary>Re-runs <see cref="CombatBrain.Decide"/> over the frozen inputs and asserts every verdict equals the frozen one.</summary>
    public static JevReplayResult Replay(in JevCombatWake wake, int replays = DefaultReplays)
    {
        GuardReplays(replays);
        for (var i = 0; i < replays; i++)
        {
            var actual = CombatBrain.Decide(wake.Inputs);
            if (!actual.Equals(wake.Expected))
                return Mismatch(JevBrain.Combat, wake.WakeId, replays, i, wake.Expected.Describe(), actual.Describe());
        }
        return Pass(JevBrain.Combat, wake.WakeId, replays);
    }

    private static void GuardReplays(int replays)
    {
        if (replays < 1)
            throw new ArgumentOutOfRangeException(nameof(replays), replays, "A determinism check must replay at least once.");
    }

    private static JevReplayResult Pass(JevBrain brain, string wakeId, int replays)
        => new(brain, wakeId, replays, true, null);

    private static JevReplayResult Mismatch(
        JevBrain brain, string wakeId, int replays, int replayIndex, string expected, string actual)
        => new(brain, wakeId, replays, false,
            $"wake {wakeId}: replay #{replayIndex} diverged: expected [{expected}] but got [{actual}]");
}
