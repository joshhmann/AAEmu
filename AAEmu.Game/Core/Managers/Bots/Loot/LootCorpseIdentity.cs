#nullable enable

using System.Runtime.CompilerServices;
using System.Threading;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.Game.Core.Managers.Bots.Loot;

/// <summary>A stable, memory-only identity for one NPC instance, without retaining it.</summary>
internal static class LootCorpseIdentity
{
    private sealed class Identity(long token)
    {
        internal long Token { get; } = token;
    }

    private static readonly ConditionalWeakTable<Npc, Identity> Identities = new();
    private static long _nextToken;

    internal static long GetToken(Npc? npc)
        => npc == null ? 0 : Identities.GetValue(npc,
            _ => new Identity(Interlocked.Increment(ref _nextToken))).Token;
}
