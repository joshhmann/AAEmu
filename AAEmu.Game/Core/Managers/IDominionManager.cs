using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Siege;

using AAEmu.Game.Core.Network.Connections;
namespace AAEmu.Game.Core.Managers;

public interface IDominionManager : ILoadable, IInitializable
{
    IReadOnlyList<SiegeZoneTemplate> SiegeZones { get; }
    IReadOnlyList<SiegeSettingTemplate> SiegeSettings { get; }
    IReadOnlyCollection<Dominion> Dominions { get; }

    IReadOnlyList<SiegePlanEntry> SiegePlans { get; }
    Dominion? GetDominion(uint zoneGroupId);

    /// <summary>Persists a newly declared dominion and broadcasts it server-wide.</summary>
    void Declare(DominionData dominionData, string expeditionName);

    /// <summary>
    /// Validates and applies a tax-rate change for the sender's owned dominion.
    /// Returns false with <paramref name="error"/> set when refused.
    /// </summary>
    bool ChangeTaxRate(Character character, ushort dominionId, int taxRate, out ErrorMessageType error);

    /// <summary>Broadcasts every stored dominion to the given connection (enter-world).</summary>
    void SendDominions(GameConnection connection);

    // Pure schedule helpers (exposed for tests)
    SiegePhase GetCurrentPhase(SiegeZoneTemplate zone, DateTime utcNow);
    DateTime CurrentCycleSiegeSunday(DateTime utcNow);
    HashSet<uint> ActiveZoneGroupsForWeek(DateTime siegeSunday);
}
