using System.Diagnostics;
using System.Text;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Measures where one bot step spends its time, so a slow game tick can name
/// the part of the bot's turn that caused it (populated-area run 4: 144 slow
/// ticks in 30 minutes, all inside the scheduler drain, none attributed).
/// The scheduler opens a profile around each step; the arbiter and the roam
/// executor call <see cref="Mark"/> after each section, which charges the time
/// since the previous mark to that section's name. Measurement only: nothing
/// reads a profile to decide what a bot does. Per thread, so the rig's parallel
/// tests never share one; outside an open profile every call is a no-op.
/// </summary>
public static class BotStepProfile
{
    private const int MaxPhases = 32;

    [ThreadStatic]
    private static Recorder? t_recorder;

    /// <summary>Starts timing a step on this thread, discarding any open profile.</summary>
    public static void Begin()
    {
        var recorder = t_recorder ??= new Recorder();
        recorder.Count = 0;
        recorder.Open = true;
        recorder.Last = Stopwatch.GetTimestamp();
    }

    /// <summary>Charges the time since the previous mark to <paramref name="phase"/>.</summary>
    public static void Mark(string phase)
    {
        var recorder = t_recorder;
        if (recorder is not { Open: true })
            return;
        var now = Stopwatch.GetTimestamp();
        recorder.Add(phase, now - recorder.Last);
        recorder.Last = now;
    }

    /// <summary>
    /// Closes the profile, charging the unmarked remainder to "other", and
    /// copies its phases into <paramref name="into"/> (cleared first).
    /// </summary>
    public static void End(List<(string Phase, long Ticks)> into)
    {
        into.Clear();
        var recorder = t_recorder;
        if (recorder is not { Open: true })
            return;
        Mark("other");
        recorder.Open = false;
        for (var i = 0; i < recorder.Count; i++)
            into.Add((recorder.Phases[i], recorder.Ticks[i]));
    }

    public static double ToMs(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    /// <summary>"quest-leg=41.2 arbiter=3.0" for the phases at or above <paramref name="minMs"/>, slowest first.</summary>
    public static string Describe(IEnumerable<(string Phase, long Ticks)> phases, double minMs = 0.5, int max = 6)
    {
        var builder = new StringBuilder();
        foreach (var (phase, ticks) in phases.OrderByDescending(p => p.Ticks).Take(max))
        {
            var ms = ToMs(ticks);
            if (ms < minMs)
                break;
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(phase).Append('=').Append(ms.ToString("F1"));
        }
        return builder.Length > 0 ? builder.ToString() : "-";
    }

    private sealed class Recorder
    {
        public readonly string[] Phases = new string[MaxPhases];
        public readonly long[] Ticks = new long[MaxPhases];
        public int Count;
        public bool Open;
        public long Last;

        public void Add(string phase, long ticks)
        {
            for (var i = 0; i < Count; i++)
            {
                if (ReferenceEquals(Phases[i], phase) || Phases[i] == phase)
                {
                    Ticks[i] += ticks;
                    return;
                }
            }
            if (Count == MaxPhases)
            {
                Ticks[MaxPhases - 1] += ticks; // overflow folds into the last slot
                return;
            }
            Phases[Count] = phase;
            Ticks[Count] = ticks;
            Count++;
        }
    }
}
