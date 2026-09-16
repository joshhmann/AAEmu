#nullable enable

using System.Numerics;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Execution and planning context passed to GOAP actions, arbitrators, and state providers.
/// Bridges the compressed 64-bit planning state with rich server objects and persistent memory.
/// </summary>
public sealed class BotContext
{
    public BotMemory Memory { get; } = new();

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public Vector3? CurrentDestination { get; set; }

    public uint SelectedMerchantNpcId { get; set; }

    public WildFarmPoi? SelectedFarmPoi { get; set; }

    public int MaxActionRetries { get; init; } = 2;

    public DateTime GetUtcNow() => TimeProvider.GetUtcNow().UtcDateTime;
}
