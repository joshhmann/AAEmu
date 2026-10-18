using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Owner of the Fighting mode (<see cref="BotMode"/>): while the bot is in
/// battle and something in reach is attacking it, the bot fights back. Every
/// other module denies while the bot is in battle, so before this module a
/// fight had no owner: live run 11 (E325) elected "(none)" mid-fight, nothing
/// swung back, the battle never ended and the bot stood being hit for minutes.
///
/// The fight itself runs through the executor's shared engagement step (the
/// conflict engagement's approach, auto-attack and skill cast), so this module
/// only decides; it owns no state. Priority 90 sits above out-of-combat
/// recovery (85) and below the schedule phases (100, which deny in battle) and
/// an operator assignment (110), which still outranks a fight.
/// </summary>
public sealed class SelfDefenceActivityModule : IBotActivityModule
{
    public const string ActivityName = "defend.self";

    private readonly Func<Character, Unit?> _findAttacker;

    public SelfDefenceActivityModule(Func<Character, Unit?>? findAttacker = null)
    {
        _findAttacker = findAttacker ?? (character => BotAttackers.FindNearest(character, BotAttackers.DefaultReach));
    }

    public string Name => "SelfDefence";
    public int Priority { get; } = 90;

    /// <inheritdoc />
    public IReadOnlyList<string> DeclaredActivities => [ActivityName];

    /// <inheritdoc />
    public IReadOnlyList<BotMode> ServedModes => [BotMode.Fighting];

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var character = context.Bot.Character;
        if (character.IsDead)
            return BotActivityDecision.Deny("bot is dead");
        if (!character.IsInBattle)
            return BotActivityDecision.Deny("not in battle");
        // A battle whose attacker is gone or out of reach clears on the
        // ordinary combat timeout; there is nothing to fight back until then.
        if (_findAttacker(character) is null)
            return BotActivityDecision.Deny("in battle but no attacker in reach");

        return BotActivityDecision.Allow(ActivityName);
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new BotActivity(ActivityName, Name);
    }
}

/// <summary>Who is attacking a bot, from the ordinary world state.</summary>
public static class BotAttackers
{
    /// <summary>Default reach, the wildlife hunt's perception radius.</summary>
    public const float DefaultReach = 45f;

    /// <summary>
    /// The unit attacking <paramref name="me"/>: its current target when that
    /// target is attacking it (so a fight already joined keeps its target),
    /// otherwise the nearest attacker in <paramref name="reach"/>.
    /// </summary>
    public static Unit? FindNearest(Character me, float reach,
        IEnumerable<Npc>? npcs = null, IEnumerable<Character>? players = null)
    {
        if (me.CurrentTarget is Unit current && IsAttacker(me, current)
            && MathUtil.CalculateDistance(me.Transform.World.Position, current.Transform.World.Position, false) <= reach)
            return current;

        Unit? best = null;
        var bestDistance = float.MaxValue;
        foreach (var candidate in (npcs ?? WorldManager.GetAround<Npc>(me, reach)).Cast<Unit>()
                     .Concat(players ?? WorldManager.GetAround<Character>(me, reach)))
        {
            if (!IsAttacker(me, candidate))
                continue;
            var distance = MathUtil.CalculateDistance(me.Transform.World.Position, candidate.Transform.World.Position, false);
            if (distance <= reach && distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is alive, attacking
    /// <paramref name="me"/> and something the bot may attack back: an NPC
    /// that hates the bot (or is in battle with it as its target), or a player
    /// the bot is actively hostile with. An NPC the bot may not attack (a
    /// protected guard) is never a fight: auto-attack would silently do nothing
    /// while every other activity stood aside. An NPC that has only turned to
    /// look (alert) is not an attacker either.
    /// </summary>
    public static bool IsAttacker(Character me, Unit? candidate)
    {
        if (candidate is null || candidate.ObjId == me.ObjId || candidate.Hp <= 0)
            return false;
        return candidate switch
        {
            Npc npc => (npc.AggroTable.ContainsKey(me.ObjId) || (npc.IsInBattle && npc.CurrentTarget?.ObjId == me.ObjId))
                && CanAttack(me, npc),
            Character player => me.IsActivelyHostile(player) && CanAttack(me, player),
            _ => false,
        };
    }

    private static bool CanAttack(Character me, Unit target)
    {
        try
        {
            return me.CanAttack(target);
        }
        catch
        {
            return false;
        }
    }
}
