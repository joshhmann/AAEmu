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
        return effectiveActor.SetTarget(0);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.HasActiveTarget))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
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

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        // If target was lost or died while approaching, invalidate action
        if (!observedState.Has(BotWorldState.HasActiveTarget) || observedState.Has(BotWorldState.TargetDead))
            return GoapActionStatus.Invalidated;

        if (observedState.Has(BotWorldState.TargetInRange))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
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

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        // Crucial Invariant: TargetDead must be observed on the target unit!
        if (observedState.Has(BotWorldState.TargetDead))
            return GoapActionStatus.Succeeded;

        if (!observedState.Has(BotWorldState.HasActiveTarget))
            return GoapActionStatus.Invalidated;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
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

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.HasLooted) || !observedState.Has(BotWorldState.HasCorpseToLoot))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
    }
}
