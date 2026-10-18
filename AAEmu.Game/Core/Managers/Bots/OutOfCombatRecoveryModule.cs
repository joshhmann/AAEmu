using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Managers.Bots;

public enum ConsumablePreference
{
    Auto = 0,
    Health = 1,
    Mana = 2,
    Any = 3
}

public enum RecoveryStage
{
    None = 0,
    FoodDrink = 1,
    RestSit = 2
}

public enum RecoveryStatus
{
    Idle,
    Eating,
    Resting,
    Completed,
    Interrupted,
    Blocked
}

public sealed record RecoveryStepResult(
    RecoveryStatus Status,
    RecoveryStage Stage,
    string Detail,
    Item? ConsumedItem = null,
    ActorRequest? Request = null);

/// <summary>
/// Offers ordinary owned recovery. Election is read-only; the generation-bound
/// roam executor owns requests and posture. Only ordinary skills/buffs and the
/// world's Character regeneration may change points or consume inventory.
/// </summary>
public sealed class OutOfCombatRecoveryModule : IBotActivityModule
{
    public const string ActivityNameRest = "recovery.rest";
    public const string ActivityNameEat = "recovery.consume";
    public const float DefaultHpTriggerFraction = 0.75f;
    public const float DefaultMpTriggerFraction = 0.60f;
    public const float DefaultHpExitFraction = 0.95f;
    public const float DefaultMpExitFraction = 0.95f;

    public string Name => "OutOfCombatRecovery";
    public int Priority { get; init; } = 85;

    /// <inheritdoc />
    public IReadOnlyList<string> DeclaredActivities => [ActivityNameRest];

    public float HpTriggerFraction { get; init; } = DefaultHpTriggerFraction;
    public float MpTriggerFraction { get; init; } = DefaultMpTriggerFraction;
    public float HpExitFraction { get; init; } = DefaultHpExitFraction;
    public float MpExitFraction { get; init; } = DefaultMpExitFraction;

    /// <summary>Read-only executor refusal for unchanged facts in this activation.</summary>
    public Func<PlayerBotRuntime, string?>? RecoveryEligibilityProvider { get; init; }

    public static bool ShouldTriggerRecovery(Character character,
        float hpTrigger = DefaultHpTriggerFraction, float mpTrigger = DefaultMpTriggerFraction)
        => character != null && !character.IsDead && character.Hp > 0 && !character.IsInBattle
           && character.MaxHp > 0 && character.MaxMp > 0
           && (character.Hp < character.MaxHp * hpTrigger || character.Mp < character.MaxMp * mpTrigger);

    public static bool IsRecoveryComplete(Character character,
        float hpExit = DefaultHpExitFraction, float mpExit = DefaultMpExitFraction)
        => character == null || character.MaxHp <= 0 || character.MaxMp <= 0
           || character.Hp >= character.MaxHp * hpExit && character.Mp >= character.MaxMp * mpExit;

    /// <summary>
    /// Conservatively supported current-HP effects, not food names/categories.
    /// Current-MP restoration is not implemented by HealEffect; MaxMana bonuses
    /// are not refills. MP recovery therefore remains ordinary native regeneration.
    /// Conditional/charged/level-scaled, area, plot and mixed-effect uses are not
    /// selected until their actual effect shape can be supported truthfully.
    /// </summary>
    public static bool IsSupportedHealthConsumable(Item item)
        => TryGetHealthSupport(item, out _);

    internal static bool TryGetHealthSupport(Item item, out uint buffId)
    {
        buffId = 0;
        if (item?.Template == null || item.Count <= 0 || item.Template.UseSkillId == 0)
            return false;
        var manager = SkillManager.PeekInstance;
        var skill = manager?.GetSkillTemplate(item.Template.UseSkillId);
        if (skill == null || skill.Plot != null || skill.PlotOnly || skill.ChannelingTime > 0
            || skill.TargetAreaRadius > 0 || skill.TargetType != SkillTargetType.Self
            || skill.Effects.Count == 0)
            return false;

        foreach (var effect in skill.Effects)
        {
            if (effect == null || !effect.Friendly || effect.Chance < 100 || effect.Front != effect.Back
                || effect.TargetBuffTagId != 0 || effect.TargetNoBuffTagId != 0
                || effect.SourceBuffTagId != 0 || effect.SourceNoBuffTagId != 0
                || effect.TargetNpcTagId != 0 || effect.ItemSetId != 0
                || effect.ApplicationMethod is not (SkillEffectApplicationMethod.Target
                    or SkillEffectApplicationMethod.Source or SkillEffectApplicationMethod.SourceOnce))
                return false;
            switch (effect.Template)
            {
                case HealEffect heal when IsSupportedHeal(heal):
                    break;
                case BuffEffect buff when buff.Chance >= 100 && IsSupportedHealthBuff(buff.Buff):
                    if (buffId != 0 && buffId != buff.Buff.Id)
                        return false;
                    buffId = buff.Buff.Id;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static bool IsSupportedHeal(HealEffect heal)
        => heal.UseFixedHeal && heal.FixedMin > 0 && heal.FixedMax >= heal.FixedMin
           && !heal.Percent && !heal.UseChargedBuff && !heal.UseLevelHeal && heal.ActabilityGroupId == 0;

    internal static bool IsSupportedHealthBuff(BuffTemplate? buff)
    {
        if (buff == null || buff.Id == 0 || buff.Duration <= 0 || buff.Tick <= 0 || buff.Kind != BuffKind.Good
            || buff.TickAreaRadius > 0 || buff.ConditionalTick || buff.RequireBuffId != 0
            || buff.TickEffects.Count == 0)
            return false;
        foreach (var tick in buff.TickEffects)
        {
            if (tick.TargetBuffTagId != 0 || tick.TargetNoBuffTagId != 0
                || SkillManager.PeekInstance?.GetEffectTemplate(tick.EffectId) is not HealEffect heal
                || !IsSupportedHeal(heal))
                return false;
        }
        return true;
    }

    internal static bool HasActiveHealthRecoveryBuff(Character character)
    {
        foreach (var buff in character.Buffs.GetEffectsByType(typeof(BuffTemplate)))
        {
            if (buff.State is not (EffectState.Finishing or EffectState.Finished)
                && IsSupportedHealthBuff(buff.Template))
                return true;
        }
        return false;
    }

    internal static bool IsProtectedRecoveryItem(Character character, Item item)
    {
        var template = item.Template;
        if (template.LootQuestId > 0 || template.CategoryId is (int)ItemCategory.Quest_Item
            or (int)ItemCategory.Quest_Equip_Weapon or (int)ItemCategory.uest_Equip_Armor
            or (int)ItemCategory.Quest_Equip_Accessory)
            return true;
        var quests = character.Quests?.ActiveQuests;
        if (quests == null || quests.Count == 0)
            return false;
        var manager = QuestManager.PeekInstance;
        if (manager == null)
            return true; // Active purpose with unreadable requirements cannot authorize spending.
        foreach (var questId in quests.Keys)
        {
            var quest = manager.GetTemplate(questId);
            if (quest == null)
                return true;
            foreach (var component in quest.Components.Values)
            {
                if (component.KindId == QuestComponentKind.Reward)
                    continue;
                foreach (var act in component.ActTemplates)
                {
                    if (act is IQuestActGenericItem generic && generic.ItemId == item.TemplateId
                        || act is QuestActObjItemGather gather && gather.ItemId == item.TemplateId
                        || act is QuestActObjItemUse use && use.ItemId == item.TemplateId
                        || act is QuestActObjItemGroupGather groupGather
                        && manager.GetGroupItems(groupGather.ItemGroupId).Contains(item.TemplateId)
                        || act is QuestActObjItemGroupUse groupUse
                        && manager.GetGroupItems(groupUse.ItemGroupId).Contains(item.TemplateId))
                        return true;
                }
            }
        }
        return false;
    }

    public static Item? SelectConsumableItem(Character character, ConsumablePreference preference = ConsumablePreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (preference == ConsumablePreference.Mana || character.MaxHp <= 0
            || character.Hp >= character.MaxHp * DefaultHpExitFraction
            || HasActiveHealthRecoveryBuff(character))
            return null;
        return SelectHealthConsumable(character);
    }

    internal static Item? SelectHealthConsumable(Character character)
    {
        if (character.MaxHp <= 0 || character.Hp >= character.MaxHp * DefaultHpExitFraction || character.HealMul <= 0)
            return null;
        var bag = character.Inventory?.Bag;
        if (bag == null)
            return null;
        foreach (var item in bag.GetItemsSnapshot())
        {
            if (TryGetHealthSupport(item, out var buffId) && !IsProtectedRecoveryItem(character, item)
                && IsWithinRecoveryItemLevel(character, item)
                && (buffId == 0 || !character.Buffs.CheckBuff(buffId) && !character.Buffs.CheckBuffImmune(buffId)))
                return item;
        }
        return null;
    }

    private static bool IsWithinRecoveryItemLevel(Character character, Item item)
    {
        if (item.Template.LevelRequirement > character.Level)
            return false;
        foreach (var effect in SkillManager.PeekInstance!.GetSkillTemplate(item.Template.UseSkillId).Effects)
        {
            if (effect.StartLevel > character.Level || effect.EndLevel < character.Level)
                return false;
        }
        return true;
    }

    public static bool EnterSittingStance(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (character.IsDead || character.Hp <= 0 || character.IsInBattle)
            return false;
        if (character.Stance == UnitStance.Sit)
            return true;
        character.Stance = UnitStance.Sit;
        character.IdleStatus = true;
        character.BroadcastPacket(new SCUnitIdleStatusPacket(character.ObjId, true), true);
        return true;
    }

    public static bool ExitSittingStance(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (character.Stance != UnitStance.Sit)
            return false;
        character.Stance = UnitStance.Stand;
        character.IdleStatus = false;
        character.BroadcastPacket(new SCUnitIdleStatusPacket(character.ObjId, false), true);
        return true;
    }

    /// <summary>
    /// One legal ordinary dispatch/rest boundary. This never drives native time,
    /// assigns vitals, decrements counts or replaces pending actor work. Continuous
    /// ownership, terminal observation and interruption cleanup belong to the executor.
    /// </summary>
    public static RecoveryStepResult ExecuteRecoveryStep(Character character, IGameplayActor? actor = null,
        float hpTrigger = DefaultHpTriggerFraction, float mpTrigger = DefaultMpTriggerFraction,
        float hpExit = DefaultHpExitFraction, float mpExit = DefaultMpExitFraction)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (character.IsDead || character.Hp <= 0 || character.IsInBattle)
            return new(RecoveryStatus.Interrupted, RecoveryStage.None, "recovery.unsafe");
        if (IsRecoveryComplete(character, hpExit, mpExit))
            return new(RecoveryStatus.Completed, RecoveryStage.None, "recovery.complete");
        if (character.Stance != UnitStance.Sit && !ShouldTriggerRecovery(character, hpTrigger, mpTrigger))
            return new(RecoveryStatus.Idle, RecoveryStage.None, "recovery.not-needed");
        if (actor?.ActiveRequest is { IsTerminal: false })
            return new(RecoveryStatus.Idle, RecoveryStage.None, "recovery.waiting-safe-boundary");
        var item = SelectConsumableItem(character);
        if (item != null)
        {
            if (actor == null)
                return new(RecoveryStatus.Blocked, RecoveryStage.FoodDrink, "recovery.ordinary-actor-unavailable");
            return UseRecoveryItem(character, actor, item);
        }
        EnterSittingStance(character);
        return new(RecoveryStatus.Resting, RecoveryStage.RestSit, "recovery.awaiting-native-progress");
    }

    internal static RecoveryStepResult UseRecoveryItem(Character character, IGameplayActor actor, Item item)
    {
        var beforeHp = character.Hp;
        var request = actor.UseItem(item.TemplateId, character.ObjId);
        if (request.State is not (ActorLifecycleState.Running or ActorLifecycleState.Accepted or ActorLifecycleState.Completed))
            return new(RecoveryStatus.Blocked, RecoveryStage.FoodDrink,
                $"recovery.item-refused: {request.Detail}", Request: request);
        if (!request.IsTerminal)
            return new(RecoveryStatus.Eating, RecoveryStage.FoodDrink, "recovery.item-pending", Request: request);
        if (character.Hp <= beforeHp && !HasActiveHealthRecoveryBuff(character))
            return new(RecoveryStatus.Blocked, RecoveryStage.FoodDrink, "recovery.item-no-progress", Request: request);
        return new(RecoveryStatus.Eating, RecoveryStage.FoodDrink, "recovery.item-effect-observed", item, request);
    }

    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var character = context.Bot.Character;
        if (character == null || character.IsDead || character.Hp <= 0)
            return BotActivityDecision.Deny("bot is dead");
        if (character.IsInBattle)
            return BotActivityDecision.Deny("bot is in battle");
        if (RecoveryEligibilityProvider?.Invoke(context.Bot) is { } blocked)
            return BotActivityDecision.Deny(blocked);
        if (IsRecoveryComplete(character, HpExitFraction, MpExitFraction))
            return BotActivityDecision.Deny("recovery complete");
        if (context.ActiveActivity is ActivityNameRest or ActivityNameEat
            || ShouldTriggerRecovery(character, HpTriggerFraction, MpTriggerFraction))
            return BotActivityDecision.Allow(ActivityNameRest);
        return BotActivityDecision.Deny("health and mana sufficient");
    }

    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new BotActivity(ActivityNameRest, Name);
    }
}
