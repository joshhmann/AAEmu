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
///
/// v2 ADDITIVE fields (M5 policy-extension consumers): economy state
/// (Money/BankMoney/LaborPower/BagItemCounts/BankItemCounts/
/// CarriedPackTemplateId) and party state (InParty/PartyOwnerId/
/// PendingInvitationOwnerId/PartyLeaderObjId/PartyLeaderTargetObjId), all
/// read through ordinary Character/TeamManager services at Observe() time.
/// Existing field names never change; old consumers ignore the new keys.
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

    /// <summary>Inventory copper balance (Character.Money, direct query).</summary>
    public long Money { get; init; }

    /// <summary>Bank copper balance (Character.Money2, direct query).</summary>
    public long BankMoney { get; init; }

    /// <summary>Current labor power (Character.LaborPower, direct query).</summary>
    public int LaborPower { get; init; }

    /// <summary>Bag item counts by template id (Inventory.Bag snapshot, direct query).</summary>
    public IReadOnlyDictionary<uint, int> BagItemCounts { get; init; } = new Dictionary<uint, int>();

    /// <summary>Bank warehouse item counts by template id (Inventory.Warehouse snapshot, direct query).</summary>
    public IReadOnlyDictionary<uint, int> BankItemCounts { get; init; } = new Dictionary<uint, int>();

    /// <summary>Template id of the pack carried in the Backpack slot (0 = none).</summary>
    public uint CarriedPackTemplateId { get; init; }

    /// <summary>True when the character is a member of an active team (Character.InParty).</summary>
    public bool InParty { get; init; }

    /// <summary>Id of the active team's owner (0 = not in a team).</summary>
    public uint PartyOwnerId { get; init; }

    /// <summary>ObjId of the owner of the character's pending party invitation (0 = none).</summary>
    public uint PendingInvitationOwnerId { get; init; }

    /// <summary>ObjId of the active team's leader character (0 = not in a team).</summary>
    public uint PartyLeaderObjId { get; init; }

    /// <summary>World position of the active team's leader (Vector3.Zero when not in a team).</summary>
    public Vector3 PartyLeaderPosition { get; init; }

    /// <summary>ObjId of the team leader's current target (0 = none).</summary>
    public uint PartyLeaderTargetObjId { get; init; }

    public override string ToString()
        => $"actor={ActorId} pos={Position} target={CurrentTargetObjId} hp={Hp}/{MaxHp} mp={Mp}/{MaxMp} " +
           $"money={Money} bank={BankMoney} labor={LaborPower} inParty={InParty} " +
           $"nearChars={NearbyCharacterObjIds.Count} nearNpcs={NearbyNpcObjIds.Count} nearDoodads={NearbyDoodadObjIds.Count} quests={ActiveQuestIds.Count}";
}
