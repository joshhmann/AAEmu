using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Observation snapshot — the M5 "unified observation snapshot" (spec §8:
/// direct server-state query, NO packets).
///
/// v1 carries position, target, vitals, nearby world objects (from the
/// region graph / WorldManager) and active quest ids. All fields are read at
/// Observe() time through normal server services; the snapshot is immutable
/// once returned. Controllers (and later behavior tests) read this — they
/// never fabricate packets or poke at the world concurrently.
/// </summary>
public sealed class ActorObservation
{
    public uint ActorId { get; init; }

    public Vector3 Position { get; init; }

    /// <summary>Current target objId (0 when no target).</summary>
    public uint CurrentTargetObjId { get; init; }

    public int Hp { get; init; }

    public int MaxHp { get; init; }

    public int Mp { get; init; }

    public int MaxMp { get; init; }

    /// <summary>Nearby ordinary Characters (region graph, direct query).</summary>
    public IReadOnlyList<uint> NearbyCharacterObjIds { get; init; } = [];

    /// <summary>Nearby NPCs (region graph, direct query).</summary>
    public IReadOnlyList<uint> NearbyNpcObjIds { get; init; } = [];

    /// <summary>Nearby doodads (region graph, direct query).</summary>
    public IReadOnlyList<uint> NearbyDoodadObjIds { get; init; } = [];

    /// <summary>Active quest ids on the character (CharacterQuests, direct query).</summary>
    public IReadOnlyList<uint> ActiveQuestIds { get; init; } = [];

    public override string ToString()
        => $"actor={ActorId} pos={Position} target={CurrentTargetObjId} hp={Hp}/{MaxHp} mp={Mp}/{MaxMp} " +
           $"nearChars={NearbyCharacterObjIds.Count} nearNpcs={NearbyNpcObjIds.Count} nearDoodads={NearbyDoodadObjIds.Count} quests={ActiveQuestIds.Count}";
}
