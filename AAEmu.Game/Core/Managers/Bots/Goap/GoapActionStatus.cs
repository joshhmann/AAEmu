#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Execution state of an atomic GOAP action within the multi-tick bot runtime.
/// Actions may remain Running across multiple ticks until observed world state confirms success or failure.
/// </summary>
public enum GoapActionStatus : byte
{
    /// <summary>Action has been planned or queued but has not yet started execution.</summary>
    NotStarted = 0,

    /// <summary>Action is currently executing in-flight across one or more ticks.</summary>
    Running = 1,

    /// <summary>Action effects have been observed and verified in the real game world.</summary>
    Succeeded = 2,

    /// <summary>Action execution failed (e.g. actor request rejected, timed out, or unachievable).</summary>
    Failed = 3,

    /// <summary>Action assumptions or preconditions were invalidated by external world changes.</summary>
    Invalidated = 4
}
