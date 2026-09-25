using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>What kind of world object a radar row describes.</summary>
public enum RadarEntityKind
{
    Npc = 0,
    Doodad = 1,
    Character = 2
}

/// <summary>
/// One enriched radar row: the live facts of one objId that the observation
/// already named. This is a READ-ONLY projection of server state — no packet,
/// no region scan, no mutation.
///
/// Honesty contract:
///  - <see cref="ObjId"/> and <see cref="Kind"/> come from the observation's
///    own objId lists, so they can never fail;
///  - every other field is a live read whose FAILURE is an explicit null,
///    never a dropped or invented row (<see cref="TemplateId"/>,
///    <see cref="Position"/> and <see cref="DistanceM"/> are therefore
///    nullable: an objId that no longer resolves yields a row carrying only
///    identity, which is exactly the "gone from the world" fact);
///  - <see cref="ObservedAtUtc"/> is stamped once per projection and carried by
///    every row (a consumer can always tell how stale a row is).
/// </summary>
public sealed record RadarEntity
{
    /// <summary>objId as named by the observation list (never fails).</summary>
    public required uint ObjId { get; init; }

    /// <summary>Entity kind, taken from which observation list named the objId.</summary>
    public required RadarEntityKind Kind { get; init; }

    /// <summary>Live <c>BaseUnit.TemplateId</c>; null when the objId no longer resolves.</summary>
    public uint? TemplateId { get; init; }

    /// <summary>Live display name (<c>BaseUnit.Name</c>, else the template name); null when unnamed/unresolved.</summary>
    public string? Name { get; init; }

    /// <summary>Level of the NPC template (<c>Npc.Template.Level</c>) or of a nearby character; null for doodads and unresolved rows.</summary>
    public int? Level { get; init; }

    /// <summary>Live world position; null when the objId no longer resolves.</summary>
    public Vector3? Position { get; init; }

    /// <summary>Flat (XY) distance from the observer in metres; null when unresolved. Never clamped — rows beyond the perceived radius are dropped instead.</summary>
    public float? DistanceM { get; init; }

    /// <summary>Zone name of the entity's own zone; null when the zone table has no entry for its zone key.</summary>
    public string? ZoneName { get; init; }

    /// <summary>Live <c>Unit.Hp</c> for units (npc/character); null for doodads and unresolved rows.</summary>
    public int? Hp { get; init; }

    /// <summary>
    /// Whether the unit's loot container is both non-empty and within
    /// <c>LootingContainer.MaxLootingRange</c> — for NPCs the canonical
    /// <see cref="QuestBehavior.ProbeCorpse"/> verdict; for doodads the
    /// engine's own func-driven loot-phase verdict
    /// (<see cref="Doodad.IsFuncDrivenLootFunc"/> over the current phase).
    /// Null when the read failed (unresolved row).
    /// </summary>
    public bool? Lootable { get; init; }

    /// <summary>Loot container ENTRY count; null when the container read failed.</summary>
    public int? LootContainer { get; init; }

    /// <summary>Current doodad phase (<c>Doodad.FuncGroupId</c>); null for non-doodads.</summary>
    public uint? PhaseFuncGroupId { get; init; }

    /// <summary>Doodad database-relative owner id (<c>Doodad.OwnerId</c>); null when unowned/unknown.</summary>
    public uint? OwnerId { get; init; }

    /// <summary>Doodad owner objId (<c>Doodad.OwnerObjId</c>); null when unowned/unknown.</summary>
    public uint? OwnerObjId { get; init; }

    /// <summary>Doodad owner kind name (<c>Doodad.OwnerType</c>); null when the owner type is undefined (0).</summary>
    public string? OwnerType { get; init; }

    /// <summary>True when the NPC template is a merchant AND carries a shop pack (the live shopping gate); null when the template read failed.</summary>
    public bool? Merchant { get; init; }

    /// <summary>Merchant pack template id (<c>NpcTemplate.MerchantPackId</c>); null when 0 or unknown.</summary>
    public uint? ShopPackId { get; init; }

    /// <summary>Scheduled despawn (<c>GameObject.Despawn</c>); null when nothing is scheduled (<c>DateTime.MinValue</c>).</summary>
    public DateTime? DespawnAtUtc { get; init; }

    /// <summary>Server UTC time this row was read (same stamp on every row of one snapshot).</summary>
    public required DateTime ObservedAtUtc { get; init; }
}

/// <summary>
/// One immutable radar frame: the observer's identity/position, the read time,
/// the bounded row set and the perception radius the rows were cut against.
/// </summary>
public sealed record RadarSnapshot
{
    public required uint ActorId { get; init; }

    public required Vector3 Position { get; init; }

    public required DateTime TakenAtUtc { get; init; }

    /// <summary>Rows, grouped by kind (npcs, then doodads, then characters) and in observation-list order within each kind.</summary>
    public required IReadOnlyList<RadarEntity> Entities { get; init; }

    /// <summary>Radius the census was cut against (metres, flat).</summary>
    public float PerceivedRadiusM { get; init; } = BotRadarProjection.PerceivedRadiusM;
}

/// <summary>
/// Layer 1 of the perception stack — the LIVE RADAR.
///
/// Pure, read-only projection of an <see cref="ActorObservation"/> (plus the
/// live <see cref="Character"/> whose observation it is) into a
/// <see cref="RadarSnapshot"/> of enriched rows. It generalises the
/// <see cref="BotSurveySenses"/> occupant-resolution precedent and adds the
/// per-entity facts a planner needs (level, hp, loot state, phase, owner,
/// merchant gate, scheduled despawn).
///
/// Discipline (architectural, enforced by an assembly-surface unit test):
///  - input is an EXISTING observation — never a new scan; the three objId
///    lists are the census, so this adds ZERO region queries, ZERO packets and
///    ZERO world mutation;
///  - every live read is a dictionary lookup on the observer's own world
///    (<c>GetNpc</c>/<c>GetDoodad</c>/<c>GetCharacterByObjId</c>) or an
///    existing engine predicate (<see cref="QuestBehavior.ProbeCorpse"/>);
///  - a failed read becomes an explicit null field, never a dropped or
///    invented row;
///  - rows beyond <see cref="PerceivedRadiusM"/> (+
///    <see cref="PerceivedRadiusToleranceM"/>) are DROPPED, never clamped —
///    the radar can never widen perception beyond the observation's 25 m
///    census;
///  - <see cref="BotRadarProjection"/>, <see cref="RadarSnapshot"/> and
///    <see cref="RadarEntity"/> are referenced by no decision path
///    (<c>BotDecisionSelector</c>, <c>BotProposalPrecondition</c>,
///    <c>QuestObjectiveTargetSelector</c>, scenarios, executors): the radar is
///    a planning/diagnostic surface only.
/// </summary>
public static class BotRadarProjection
{
    /// <summary>Perception radius of the observation census (the 25 m Observe band).</summary>
    public const float PerceivedRadiusM = 25f;

    /// <summary>
    /// Flat-distance slack allowed above <see cref="PerceivedRadiusM"/> before a
    /// row is dropped (float-edge only; the census is already 25 m-bounded).
    /// </summary>
    public const float PerceivedRadiusToleranceM = 1f;

    /// <summary>
    /// Projects one observation into a radar frame.
    ///
    /// The census lists come from the region graph, whose per-region object
    /// order is not stable across frames; rows are therefore emitted ordered
    /// by objId. Every downstream diff (layer 3) is order-stable because of
    /// it — the G3 determinism doctrine applied to perception.
    /// </summary>
    public static RadarSnapshot Project(ActorObservation observation, Character character)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(character);

        var taken = DateTime.UtcNow;
        var pos = observation.Position;
        var world = character.ParentWorld;
        var rows = new List<RadarEntity>(
            observation.NearbyNpcObjIds.Count +
            observation.NearbyDoodadObjIds.Count +
            observation.NearbyCharacterObjIds.Count);

        foreach (var objId in observation.NearbyNpcObjIds)
            Emit(rows, RadarEntityKind.Npc, objId, world?.GetNpc(objId), pos, taken, character);

        foreach (var objId in observation.NearbyDoodadObjIds)
            Emit(rows, RadarEntityKind.Doodad, objId, world?.GetDoodad(objId), pos, taken, character);

        foreach (var objId in observation.NearbyCharacterObjIds)
        {
            // The census never contains the observer (GetAround excludes it),
            // but a hand-built observation must not be able to produce a
            // self-row either: the observer is the frame origin, not an entity.
            if (objId == observation.ActorId)
                continue;
            Emit(rows, RadarEntityKind.Character, objId, world?.GetCharacterByObjId(objId), pos, taken, character);
        }

        rows.Sort(static (a, b) => a.ObjId.CompareTo(b.ObjId));

        return new RadarSnapshot
        {
            ActorId = observation.ActorId,
            Position = pos,
            TakenAtUtc = taken,
            Entities = rows
        };
    }

    /// <summary>
    /// Builds (and radius-gates) one row. <paramref name="unit"/> is the live
    /// resolution of <paramref name="objId"/>; null means the objId no longer
    /// resolves, which yields an identity-only row rather than a dropped one.
    /// </summary>
    private static void Emit(
        List<RadarEntity> rows,
        RadarEntityKind kind,
        uint objId,
        BaseUnit unit,
        Vector3 observerPos,
        DateTime taken,
        Character character)
    {
        if (unit is null)
        {
            rows.Add(new RadarEntity { ObjId = objId, Kind = kind, ObservedAtUtc = taken });
            return;
        }

        var worldPos = unit.Transform.World.Position;
        var distance = MathUtil.CalculateDistance(observerPos, worldPos, false);

        // Perception cannot be widened: a row that the live transform puts
        // outside the census band is DROPPED (never clamped to the radius),
        // because a clamped row would claim a distance the object is not at.
        if (distance > PerceivedRadiusM + PerceivedRadiusToleranceM)
            return;

        rows.Add(Build(kind, objId, unit, worldPos, distance, taken, character));
    }

    private static RadarEntity Build(
        RadarEntityKind kind,
        uint objId,
        BaseUnit unit,
        Vector3 worldPos,
        float distance,
        DateTime taken,
        Character character)
    {
        var row = new RadarEntity
        {
            ObjId = objId,
            Kind = kind,
            TemplateId = unit.TemplateId,
            Name = NameOf(unit),
            Level = LevelOf(unit),
            Position = worldPos,
            DistanceM = distance,
            ZoneName = ZoneNameOf(unit),
            Hp = unit is Unit u ? u.Hp : null,
            DespawnAtUtc = unit.Despawn > DateTime.MinValue ? unit.Despawn : null,
            ObservedAtUtc = taken
        };

        if (unit is Npc npc)
        {
            // The canonical corpse probe, called ONCE (same gates
            // GameplayActor.Loot pre-flights): container exists, entry count,
            // and the non-empty-within-loot-range verdict. A probe that could
            // not resolve an owner/container yields null loot fields rather
            // than a fabricated false.
            var probe = QuestBehavior.ProbeCorpse(character, npc);
            var template = npc.Template;
            return row with
            {
                Lootable = probe.ContainerExists ? probe.Lootable : null,
                LootContainer = probe.ContainerExists ? probe.ItemCount : null,
                Merchant = template is null ? null : template.Merchant && template.MerchantPackId != 0,
                ShopPackId = template is { MerchantPackId: not 0 } ? template.MerchantPackId : null
            };
        }

        return unit switch
        {
            Doodad doodad => row with
            {
                PhaseFuncGroupId = doodad.FuncGroupId,
                // The engine's own "phase grants loot" predicate (Doodad.
                // IsFuncDrivenLootFunc, the same rule CSLootOpenBagPacket
                // applies). An unset phase (0 = no group loaded) is UNKNOWN
                // rather than a no-loot verdict.
                Lootable = doodad.FuncGroupId == 0
                    ? null
                    : doodad.CurrentFuncs is { Count: > 0 } funcs
                      && funcs.TrueForAll(f => Doodad.IsFuncDrivenLootFunc(f.FuncType)),
                LootContainer = null, // doodad loot is never container-mediated
                OwnerId = doodad.OwnerId != 0 ? doodad.OwnerId : null,
                OwnerObjId = doodad.OwnerObjId != 0 ? doodad.OwnerObjId : null,
                OwnerType = doodad.OwnerType != 0 ? doodad.OwnerType.ToString() : null
            },
            _ => row
        };
    }

    /// <summary>
    /// Display name: the live object's own name when the engine set one, else
    /// the NPC template name; null when neither exists (never an empty string,
    /// which would read as a name).
    /// </summary>
    private static string? NameOf(BaseUnit unit)
    {
        if (!string.IsNullOrWhiteSpace(unit.Name))
            return unit.Name;
        return unit is Npc npc && !string.IsNullOrWhiteSpace(npc.Template?.Name) ? npc.Template!.Name : null;
    }

    private static int? LevelOf(BaseUnit unit) => unit switch
    {
        Npc npc => npc.Template is { } npcTemplate ? npcTemplate.Level : null,
        Character character => character.Level,
        _ => null
    };

    /// <summary>
    /// Zone name of the entity's own zone. A zone table that is absent
    /// (headless rigs) or unloaded degrades to an explicit null instead of
    /// throwing or inventing a label.
    /// </summary>
    private static string? ZoneNameOf(BaseUnit unit)
    {
        try
        {
            return ZoneManager.PeekInstance?.GetZoneByKey(unit.Transform.ZoneId)?.Name;
        }
        catch
        {
            return null;
        }
    }
}
