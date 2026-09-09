namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Demand-signal kinds of the N1 needs layer (M9 substrate slice N1).
/// Fixed declaration order doubles as the deterministic tie-break when two
/// needs share the maximum value.
/// </summary>
public enum BotNeedKind : byte
{
    Gold,
    Labor,
    Food,
    Lumber
}

/// <summary>
/// Observable needs input. All values are plain counts/balances copied from
/// an observation snapshot — the evaluator never reads Character, world, or
/// Personality state itself. Defaults are the empty-bot shape (broke, no
/// labor, empty bags), which evaluates to maximum urgency, never throws.
/// </summary>
public sealed record BotNeedsSnapshot(
    long Money = 0,
    int LaborPower = 0,
    int LaborCap = BotNeedsEvaluator.DefaultLaborCap,
    int FoodCount = 0,
    int LumberCount = 0);

/// <summary>
/// Slice-policy targets the needs are measured against. These are N1 policy
/// constants at rig scale, not canonical game data: the gold target is a
/// copper sufficiency line, food/lumber targets are bag-count lines, and the
/// rest line is the urgency at or below which a bot idles.
/// </summary>
public sealed record BotNeedsThresholds(
    long GoldTarget = 1000,
    int FoodTarget = 5,
    int LumberTarget = 5,
    double RestUrgency = 0.2);

/// <summary>
/// Evaluated needs vector. Every need is in [0, 1] (1 = desperate);
/// <see cref="Urgency"/> is the maximum; <see cref="DominantNeed"/> is the
/// first kind in <see cref="BotNeedKind"/> order that reaches the maximum.
/// </summary>
public sealed record BotNeeds(
    double GoldNeed,
    double LaborNeed,
    double FoodNeed,
    double LumberNeed,
    double Urgency,
    BotNeedKind DominantNeed);

/// <summary>
/// N1 pure needs evaluator (M9 substrate slice N1): the reason a bot farms
/// or trades, as four demand signals plus urgency.
///
/// Slice boundaries (hard): pure function of the input records — NO tick,
/// NO persistence, NO hunger simulation on Character, NO labor-regen or
/// economy-price changes, NO Personality reads or mutation, NO new columns,
/// no <c>CrimeManager</c>/<c>TrialManager</c> touches, zero singleton
/// lookups. FoodNeed is an explicit proxy (bag food-count shortfall — there
/// is no hunger stat to read). Inert unless a caller evaluates a snapshot.
/// </summary>
public static class BotNeedsEvaluator
{
    /// <summary>Labor scale the default snapshot is measured against.</summary>
    public const int DefaultLaborCap = 100;

    /// <summary>
    /// Evaluates the needs for a snapshot. Null is the empty-bot shape.
    /// Never throws: non-positive targets mean "no scale, no need", negative
    /// balances clamp to full need, every output is clamped to [0, 1].
    /// </summary>
    public static BotNeeds Evaluate(BotNeedsSnapshot? snapshot = null, BotNeedsThresholds? thresholds = null)
    {
        var snap = snapshot ?? new BotNeedsSnapshot();
        var limits = thresholds ?? new BotNeedsThresholds();

        var gold = Shortfall(snap.Money, limits.GoldTarget);
        var labor = snap.LaborCap <= 0 ? 0.0 : Shortfall(snap.LaborPower, snap.LaborCap);
        var food = Shortfall(snap.FoodCount, limits.FoodTarget);
        var lumber = Shortfall(snap.LumberCount, limits.LumberTarget);

        var urgency = Math.Max(Math.Max(gold, labor), Math.Max(food, lumber));

        // Fixed-order tie-break: the first kind reaching the maximum wins,
        // so identical snapshots always report the identical dominant need.
        var dominant = BotNeedKind.Gold;
        var best = gold;
        if (labor > best)
        {
            best = labor;
            dominant = BotNeedKind.Labor;
        }
        if (food > best)
        {
            best = food;
            dominant = BotNeedKind.Food;
        }
        if (lumber > best)
        {
            best = lumber;
            dominant = BotNeedKind.Lumber;
        }

        return new BotNeeds(gold, labor, food, lumber, urgency, dominant);
    }

    private static double Shortfall(long have, long target)
    {
        if (target <= 0)
        {
            return 0.0;
        }
        var ratio = (double)have / target;
        var clamped = Math.Clamp(ratio, 0.0, 1.0);
        return 1.0 - clamped;
    }
}
