using System.Collections.Frozen;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.World.Zones;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Conflict-join module (Q8 WAR-HONOR, war-horn model): when the bot stands
/// in a zone whose conflict is active (anything but Peace), and the bot is
/// combat-ready with a willing personality, it switches to the fight instead
/// of roaming. No queues, no instances, no sign-ups — war is a place, and
/// anyone standing in it is in it. Falls back the moment the zone goes
/// quiet (Peace gate closes, next module wins).
///
/// v1 scope: same-zone join only (the bot must already be inside the
/// conflict zone; roam brings it there naturally). Travel-to-war from
/// neighboring zones is a later slice. Combat behavior itself (target
/// acquisition + Cast through GameplayActor) is exercised by the
/// HalcyonaSkirmishScenario rig, not duplicated here.
/// </summary>
public sealed class ConflictJoinActivityModule : IBotActivityModule
{
    private readonly Func<uint, PlayerBotMetadata> _metadataProvider;

    public string Name => "ConflictJoin";
    public int Priority { get; } = 75;

    /// <summary>Minimum HP fraction to consider joining.</summary>
    public const float MinHpFraction = 0.7f;

    /// <summary>Personalities that walk toward war (B4 Personality field / chatter archetypes).</summary>
    public static readonly FrozenSet<string> EagerPersonalities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "guard", "lawful", "pirate", "cheerful", "greedy" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Personalities that avoid war.</summary>
    public static readonly FrozenSet<string> ReluctantPersonalities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "farmer", "merchant", "paranoid" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public ConflictJoinActivityModule(Func<uint, PlayerBotMetadata>? metadataProvider = null)
    {
        _metadataProvider = metadataProvider ?? DefaultMetadataProvider;
    }

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var character = context.Bot.Character;
        if (character.IsDead)
            return BotActivityDecision.Deny("bot is dead");

        if (character.MaxHp <= 0 || character.Hp / character.MaxHp < MinHpFraction)
            return BotActivityDecision.Deny("HP below join threshold");

        if (!IsCombatReady(character))
            return BotActivityDecision.Deny("no weapon and no learned offensive skill");

        var zoneKey = character.Transform.ZoneId;
        var conflict = FindActiveConflict(zoneKey);
        if (conflict == null)
            return BotActivityDecision.Deny("no active conflict in current zone");

        var metadata = _metadataProvider(context.Bot.CharacterId);
        var personality = metadata.Personality?.Trim() ?? "";
        if (ReluctantPersonalities.Contains(personality))
            return BotActivityDecision.Deny($"personality '{personality}' avoids war");

        return BotActivityDecision.Allow($"conflict.{conflict.ZoneGroupId}");
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The activity name carries the conflict; steady-state wakes keep it
        // without re-arming. Combat driving (target + Cast) runs through the
        // ordinary step-executor path owned by the GameplayActor.
        var zoneKey = context.Bot.Character.Transform.ZoneId;
        var conflict = FindActiveConflict(zoneKey);
        var name = conflict != null ? $"conflict.{conflict.ZoneGroupId}" : "conflict.unknown";
        return new BotActivity(name, Name);
    }

    internal static bool IsCombatReady(Character character)
    {
        var equipment = character.Inventory?.Equipment;
        if (equipment != null)
        {
            foreach (EquipmentItemSlot slot in Enum.GetValues<EquipmentItemSlot>())
            {
                if (slot is EquipmentItemSlot.Mainhand or EquipmentItemSlot.Offhand or EquipmentItemSlot.Ranged
                    && equipment.GetItemBySlot((int)slot) != null)
                    return true;
            }
        }
        return character.Skills != null && character.Skills.Skills.Count > 0;
    }

    internal static ZoneConflict? FindActiveConflict(uint zoneKey)
    {
        var zoneManager = ZoneManager.Instance;
        var zone = zoneManager.GetZoneByKey(zoneKey);
        if (zone == null)
            return null;
        var conflict = zoneManager.GetConflictByGroup((ushort)zone.GroupId);
        if (conflict == null || conflict.CurrentZoneState == ZoneConflictType.Peace)
            return null;
        return conflict;
    }

    private static PlayerBotMetadata DefaultMetadataProvider(uint characterId) =>
        PlayerBotMetadataStore.Instance.GetForRead(characterId);
}
