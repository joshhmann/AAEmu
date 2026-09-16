#nullable enable

using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.Game.Core.Managers.Bots.Goap.Actions;

/// <summary>
/// Scans perception and selects a hostile target unit.
/// </summary>
public sealed class AcquireHostileTargetAction : GoapActionBase
{
    public AcquireHostileTargetAction(float baseCost = 1.0f)
        : base("AcquireHostileTarget", baseCost)
    {
        WithEffect(BotWorldState.HasActiveTarget);
        WithEffect(BotWorldState.TargetIsHostile);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        // In real execution, target is resolved from BotSurveySenses
        return effectiveActor.SetTarget(0);
    }
}

/// <summary>
/// Moves the bot into weapon engagement range of the active target.
/// </summary>
public sealed class ApproachTargetAction : GoapActionBase
{
    public ApproachTargetAction(float baseCost = 1.5f)
        : base("ApproachTargetInRange", baseCost)
    {
        WithPrecondition(BotWorldState.HasActiveTarget);

        WithEffect(BotWorldState.TargetInRange);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var target = bot.Character.CurrentTarget;
        return target != null ? effectiveActor.MoveToUnit(target.ObjId) : null;
    }
}

/// <summary>
/// Executes offensive class skills and auto-attacks until target is defeated.
/// </summary>
public sealed class ExecuteCombatComboAction : GoapActionBase
{
    public uint SkillId { get; init; }

    public ExecuteCombatComboAction(uint skillId = 139, float baseCost = 2.0f)
        : base("ExecuteCombatCombo", baseCost)
    {
        SkillId = skillId;

        WithPrecondition(BotWorldState.HasActiveTarget);
        WithPrecondition(BotWorldState.TargetInRange);

        WithEffect(BotWorldState.InCombat);
        WithEffect(BotWorldState.TargetDead);
        WithEffect(BotWorldState.HasCorpseToLoot);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var target = bot.Character.CurrentTarget;
        return target != null ? effectiveActor.Cast(SkillId, target.ObjId) : null;
    }
}

/// <summary>
/// Loots dropped items and coins from a defeated monster corpse.
/// </summary>
public sealed class LootCorpseAction : GoapActionBase
{
    public LootCorpseAction(float baseCost = 1.0f)
        : base("LootMonsterCorpse", baseCost)
    {
        WithPrecondition(BotWorldState.TargetDead);
        WithPrecondition(BotWorldState.HasCorpseToLoot);

        WithEffect(BotWorldState.HasCorpseToLoot, false);
        WithEffect(BotWorldState.HasLooted);
        WithEffect(BotWorldState.InCombat, false);
        WithEffect(BotWorldState.HasActiveTarget, false);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var target = bot.Character.CurrentTarget;
        return target != null ? effectiveActor.Loot(target.ObjId) : null;
    }
}
