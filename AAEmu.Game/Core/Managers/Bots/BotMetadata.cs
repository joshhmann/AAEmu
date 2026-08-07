namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Bot embodiment fidelity (spec §7 / ROADMAP M6.5). PopulationDirector is the
/// only authority that may change a bot's fidelity; the no-downgrade guard
/// (combat/slave/pack/trial/party/saving) lives with that slice.
/// </summary>
public enum BotFidelity : byte
{
    /// <summary>Persistent citizen, not embodied; only schedule/skip data ticks.</summary>
    Dormant = 0,

    /// <summary>Embodied with tick-light behavior (scheduled/DB-driven sub-state).</summary>
    Reduced = 1,

    /// <summary>Fully embodied — visible, movable, interactive with the world.</summary>
    Full = 2
}

/// <summary>Population pressure state per bot (spec §14, density feedback).</summary>
public enum BotPressureState : byte
{
    Normal = 0,
    High = 1,
    Critical = 2
}

/// <summary>Current activity lifecycle state (spec §17/§18 actor states).</summary>
public enum BotActivityState : byte
{
    Idle = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Interrupted = 4
}

/// <summary>
/// The six bot-metadata domains, as a flags set for dirty tracking.
/// One bit per additive playerbot_* table.
/// </summary>
[Flags]
public enum BotMetadataDomain : byte
{
    None = 0,
    Profile = 1 << 0,
    Schedule = 1 << 1,
    Activity = 1 << 2,
    Home = 1 << 3,
    MemoryFlags = 1 << 4,
    PopulationState = 1 << 5,
    All = Profile | Schedule | Activity | Home | MemoryFlags | PopulationState
}

/// <summary>playerbot_profile row — one per bot citizen.</summary>
public sealed class BotProfileMetadata
{
    public uint CharacterId { get; set; }
    public uint AccountId { get; set; }
    public BotFidelity Fidelity { get; set; } = BotFidelity.Dormant;
    public string BehaviorProfile { get; set; } = "idle";
    public bool ScheduleEnabled { get; set; } = true;
    public DateTime LastSeenUtc { get; set; } = DateTime.MinValue;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>playerbot_schedule row — one daily activity window (one-to-many).</summary>
public sealed class BotScheduleEntry
{
    /// <summary>0 = new row (assigned by the DB on flush); &gt;0 = existing row id.</summary>
    public long Id { get; set; }

    public uint CharacterId { get; set; }

    /// <summary>Bitmask of active weekdays: bit 0 = Monday .. bit 6 = Sunday. All bits = every day.</summary>
    public byte DayMask { get; set; } = 0b0111_1111;

    /// <summary>Window start, server-local time.</summary>
    public TimeSpan StartTime { get; set; } = TimeSpan.Zero;

    /// <summary>Window end, server-local time.</summary>
    public TimeSpan EndTime { get; set; } = TimeSpan.FromHours(24).Subtract(TimeSpan.FromSeconds(1));

    public string ActivityType { get; set; } = "idle";

    /// <summary>Activity-specific parameters (serialized by the slice that defines them).</summary>
    public string? Params { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>playerbot_activity row — current activity state per bot (bounded, not a log).</summary>
public sealed class BotActivityMetadata
{
    public uint CharacterId { get; set; }
    public string ActivityType { get; set; } = "idle";
    public BotActivityState State { get; set; } = BotActivityState.Idle;
    public DateTime StartedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime EndedAtUtc { get; set; } = DateTime.MinValue;
    public uint Cycles { get; set; }
    public uint FailureCount { get; set; }
    public string? LastError { get; set; }
}

/// <summary>playerbot_home row — home / return anchor per bot.</summary>
public sealed class BotHomeMetadata
{
    public uint CharacterId { get; set; }
    public uint WorldId { get; set; }
    public uint ZoneId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public bool ReturnOnCombatExit { get; set; } = true;
}

/// <summary>playerbot_memory_flags row — persistent 64-bit flag bitmask per bot.</summary>
public sealed class BotMemoryFlagsMetadata
{
    public uint CharacterId { get; set; }

    /// <summary>Named bit assignments are defined by the slices that use them (M5 actor lifecycle etc.).</summary>
    public ulong Flags { get; set; }
}

/// <summary>playerbot_population_state row — PopulationDirector state per bot.</summary>
public sealed class BotPopulationStateMetadata
{
    public uint CharacterId { get; set; }
    public BotFidelity Fidelity { get; set; } = BotFidelity.Dormant;
    public BotPressureState PressureState { get; set; } = BotPressureState.Normal;
    public DateTime LastTransitionAtUtc { get; set; } = DateTime.MinValue;
    public uint TransitionCount { get; set; }
}

/// <summary>
/// In-memory bot metadata record (all six domains) with per-domain dirty
/// tracking. The persistence manager only writes domains whose dirty bit is
/// set — never per-AI-step, always via flush (batched periodic or mandatory
/// on deactivate/downgrade/shutdown).
/// </summary>
public sealed class BotMetadataRecord
{
    public uint CharacterId { get; }

    public BotProfileMetadata Profile { get; } = new();
    public List<BotScheduleEntry> Schedule { get; } = [];
    public BotActivityMetadata Activity { get; } = new();
    public BotHomeMetadata Home { get; } = new();
    public BotMemoryFlagsMetadata MemoryFlags { get; } = new();
    public BotPopulationStateMetadata PopulationState { get; } = new();

    private BotMetadataDomain _dirty;

    public BotMetadataRecord(uint characterId)
    {
        CharacterId = characterId;
        Profile.CharacterId = characterId;
        Activity.CharacterId = characterId;
        Home.CharacterId = characterId;
        MemoryFlags.CharacterId = characterId;
        PopulationState.CharacterId = characterId;
    }

    /// <summary>Domains with a pending write (read-only view of the dirty set).</summary>
    public BotMetadataDomain Dirty => _dirty;

    public bool HasAnyDirty => _dirty != BotMetadataDomain.None;

    public bool IsDirty(BotMetadataDomain domain) => (_dirty & domain) == domain;

    public void Mark(BotMetadataDomain domain) => _dirty |= domain;

    public void MarkAll() => _dirty |= BotMetadataDomain.All;

    public void ClearDirty() => _dirty = BotMetadataDomain.None;
}
