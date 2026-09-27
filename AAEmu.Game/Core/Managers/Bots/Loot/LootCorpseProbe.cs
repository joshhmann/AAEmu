#nullable enable

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.Game.Core.Managers.Bots.Loot;

/// <summary>
/// THE ONE corpse-identity resolve for the loot side: what a recorded corpse
/// objId resolves to this wake, read with the engine's own ObjId-recycling rule
/// (a recorded objId that resolves LIVE again — or resolves to another template —
/// is not our corpse).
///
/// Split out of <see cref="LootBrainPlanner"/> so the loot decision chain and the
/// corpse-approach travel seam (<see cref="LootTravelDispatch"/>) read ONE rule
/// instead of each carrying a copy: a second copy would drift the moment either
/// caller's corpse handling changed, and the fail-closed direction (an absent
/// frame is never a fabricated dead corpse) has to be identical for both.
/// </summary>
internal static class LootCorpseProbe
{
    /// <summary>One corpse objId's resolution: the named state, plus the row when it resolved.</summary>
    internal readonly record struct Resolved(LootCorpseState State, Npc? Corpse)
    {
        /// <summary>True when the recorded objId resolved to the lootable dead shape this wake.</summary>
        internal bool IsDead => State == LootCorpseState.Dead;
    }

    /// <summary>
    /// Resolves <paramref name="corpseObjId"/> against the actor's own world.
    ///
    /// <paramref name="corpseObjId"/> 0 (and a missing actor frame) reads
    /// <see cref="LootCorpseState.Unreadable"/> — "no corpse pinned at all" and
    /// "no frame to resolve one against" are the same fact to every consumer, and
    /// neither may be mistaken for a lootable corpse. A missing world reads
    /// <see cref="LootCorpseState.Gone"/>: the objId cannot be there.
    ///
    /// The template gate is applied only when the caller established one
    /// (<paramref name="preyTemplateId"/> 0 = not established, never a fabricated
    /// match).
    /// </summary>
    internal static Resolved Resolve(Character? character, uint corpseObjId, uint preyTemplateId)
    {
        if (corpseObjId == 0 || character == null)
            return new Resolved(LootCorpseState.Unreadable, null);

        var corpse = character.ParentWorld?.GetNpc(corpseObjId);
        if (corpse == null)
            return new Resolved(LootCorpseState.Gone, null);

        var recycled = corpse.Hp > 0 || (preyTemplateId != 0 && corpse.TemplateId != preyTemplateId);
        return new Resolved(recycled ? LootCorpseState.Recycled : LootCorpseState.Dead, corpse);
    }
}
