#nullable enable

using System.Text;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Compact, zero-allocation world state representation for Goal-Oriented Action Planning (GOAP).
/// Uses a 64-bit mask for boolean conditions, supplemented by numeric thresholds.
/// </summary>
public readonly struct BotWorldState : IEquatable<BotWorldState>
{
    // =========================================================================
    // Standard 64-bit State Flags
    // =========================================================================
    public const ulong None                   = 0UL;
    public const ulong InCombat               = 1UL << 0;
    public const ulong LowHealth              = 1UL << 1;
    public const ulong LowMana                = 1UL << 2;
    public const ulong HasEdibleFood          = 1UL << 3;
    public const ulong IsSitting              = 1UL << 4;
    public const ulong NearSeedMerchant       = 1UL << 5;
    public const ulong HasTreeSaplings        = 1UL << 6;
    public const ulong AtWildFarm             = 1UL << 7;
    public const ulong SecretGrovePlanted     = 1UL << 8;
    public const ulong HasActiveTarget        = 1UL << 9;
    public const ulong TargetIsHostile        = 1UL << 10;
    public const ulong TargetInRange          = 1UL << 11;
    public const ulong TargetDead             = 1UL << 12;
    public const ulong HasCorpseToLoot        = 1UL << 13;
    public const ulong HasLooted              = 1UL << 14;
    public const ulong NearContinentalRoad    = 1UL << 15;
    public const ulong HasQuestObjective      = 1UL << 16;
    public const ulong QuestObjectiveComplete = 1UL << 17;
    public const ulong NearQuestGiver         = 1UL << 18;
    public const ulong QuestTurnedIn          = 1UL << 19;
    public const ulong BagFull                = 1UL << 20;
    public const ulong NearGeneralMerchant    = 1UL << 21;
    public const ulong NearCampfire           = 1UL << 22;
    public const ulong Recovered              = 1UL << 23;
    public const ulong AtTradePost            = 1UL << 24;
    public const ulong HasTradePack           = 1UL << 25;
    public const ulong TradePackSold          = 1UL << 26;
    public const ulong HasScarecrowDesign     = 1UL << 27;
    public const ulong HasTaxCertificates     = 1UL << 28;
    public const ulong NearHousingZone         = 1UL << 29;
    public const ulong HasLandPlot             = 1UL << 30;
    public const ulong HasTimber               = 1UL << 31;
    public const ulong HasBuildingMaterials    = 1UL << 32;
    public const ulong NearWorkbench           = 1UL << 33;
    public const ulong NearHomeSite            = 1UL << 34;
    public const ulong HomeConstructed         = 1UL << 35;

    /// <summary>Active boolean flags mask.</summary>
    public ulong Flags { get; init; }

    /// <summary>Mask of flags explicitly evaluated (relevant for preconditions/goals).</summary>
    public ulong Mask { get; init; }

    /// <summary>Available labor points.</summary>
    public ushort Labor { get; init; }

    /// <summary>Available gold/copper.</summary>
    public uint Gold { get; init; }

    public BotWorldState(ulong flags, ulong mask = ulong.MaxValue, ushort labor = 0, uint gold = 0)
    {
        Flags = flags;
        Mask = mask;
        Labor = labor;
        Gold = gold;
    }

    /// <summary>Creates an empty world state.</summary>
    public static BotWorldState Empty => new(0UL, 0UL);

    /// <summary>Returns true if the specified flag is set.</summary>
    public bool Has(ulong flag) => (Flags & flag) == flag;

    /// <summary>Returns a new state with the specified flag set or cleared.</summary>
    public BotWorldState With(ulong flag, bool value = true)
    {
        var newFlags = value ? (Flags | flag) : (Flags & ~flag);
        var newMask = Mask | flag;
        return new BotWorldState(newFlags, newMask, Labor, Gold);
    }

    /// <summary>Returns a new state with updated labor points.</summary>
    public BotWorldState WithLabor(ushort labor) => new(Flags, Mask, labor, Gold);

    /// <summary>Returns a new state with updated gold.</summary>
    public BotWorldState WithGold(uint gold) => new(Flags, Mask, Labor, gold);

    /// <summary>
    /// Checks if this world state satisfies the required preconditions or goal state.
    /// Only flags specified in <paramref name="required"/>.Mask are evaluated.
    /// Also validates required labor and gold minimums.
    /// </summary>
    public bool Satisfies(in BotWorldState required)
    {
        // For all bits where required has a mask, this.Flags must match required.Flags
        if ((Flags & required.Mask) != (required.Flags & required.Mask))
            return false;

        if (required.Labor > 0 && Labor < required.Labor)
            return false;

        if (required.Gold > 0 && Gold < required.Gold)
            return false;

        return true;
    }

    /// <summary>
    /// Applies action effects onto this world state, returning the resulting state.
    /// </summary>
    public BotWorldState Apply(in BotWorldState effects)
    {
        // Overwrite flags defined in effects.Mask
        var newFlags = (Flags & ~effects.Mask) | (effects.Flags & effects.Mask);
        var newMask = Mask | effects.Mask;

        var newLabor = effects.Labor != 0 ? (ushort)Math.Max(0, Labor - effects.Labor) : Labor;
        var newGold = effects.Gold != 0 ? (uint)Math.Max(0, (long)Gold - effects.Gold) : Gold;

        return new BotWorldState(newFlags, newMask, newLabor, newGold);
    }

    public bool Equals(BotWorldState other)
        => Flags == other.Flags && Mask == other.Mask && Labor == other.Labor && Gold == other.Gold;

    public override bool Equals(object? obj)
        => obj is BotWorldState other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Flags, Mask, Labor, Gold);

    public static bool operator ==(BotWorldState left, BotWorldState right) => left.Equals(right);
    public static bool operator !=(BotWorldState left, BotWorldState right) => !left.Equals(right);

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"[Flags: 0x{Flags:X}");
        if (Labor > 0) sb.Append($", Labor: {Labor}");
        if (Gold > 0) sb.Append($", Gold: {Gold}");
        sb.Append(']');
        return sb.ToString();
    }
}
